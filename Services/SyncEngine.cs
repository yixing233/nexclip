using Microsoft.UI.Dispatching;
using SyncClipboard.Desktop.Models;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 同步引擎(设计文档 §4):连接状态机 + 上传/下载管线 + 防回环 + 指数退避重试。
/// 剪贴板操作在 UI 线程执行;事件均在 UI 线程抛出(由本类保证)。
/// </summary>
public sealed class SyncEngine : IDisposable
{
    public enum ConnState { NotConfigured, Connecting, Connected, Reconnecting, Offline }

    private readonly AppServices _svc;
    private readonly DispatcherQueue _dispatcher;
    private ClipboardMonitor? _monitor;
    private PushClient? _push;
    private string _lastUploadedHash = "";
    private ConnState _state = ConnState.NotConfigured;

    /// <summary>本地历史(SQLite)。</summary>
    public HistoryStore History { get; }

    public SyncEngine(AppServices svc, DispatcherQueue dispatcher)
    {
        _svc = svc;
        _dispatcher = dispatcher;
        History = svc.History;
    }

    /// <summary>当前剪贴板更新(entry, 本地图片缓存路径, 是否来自远端推送)。</summary>
    public event Action<ClipboardEntry, string?, bool>? EntryUpdated;

    /// <summary>连接状态变化(state, 附加消息)。</summary>
    public event Action<ConnState, string>? ConnectionChanged;

    public enum TransferKind { Upload, Download }

    /// <summary>传输状态变化(开始/结束 + 方向)。</summary>
    public event Action<bool, TransferKind>? TransferChanged;

    /// <summary>同步错误(最终失败,托盘切错误图标)。</summary>
    public event Action? SyncError;

    public ConnState State => _state;

    public void Start()
    {
        _monitor = new ClipboardMonitor(_dispatcher, _svc.Settings, OnCapturedAsync);
        _monitor.Start();
        _push = new PushClient();
        _push.EntryReceived += entry => _dispatcher.TryEnqueue(() => _ = HandlePushAsync(entry));
        _push.StateChanged += s => _dispatcher.TryEnqueue(() => HandlePushState(s));
        _ = ConnectAsync();
    }

    /// <summary>设置变化后重建连接。</summary>
    public async Task ReconfigureAsync()
    {
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.AuthToken))
        {
            SetState(ConnState.NotConfigured, "");
            return;
        }
        if (_push is null) return;

        SetState(ConnState.Connecting, "");
        // 携带设备信息连接:服务端据此登记/更新设备列表(在线状态、名称、平台)
        await _push.ConnectAsync(s.ServerUrl, s.AuthToken, s.DeviceId, s.DeviceName);
        // ConnectAsync 内部会触发 connected/disconnected 状态回调
        await PullCurrentAsync();
    }

    private void HandlePushState(string state)
    {
        switch (state)
        {
            case "connected":
                SetState(ConnState.Connected, "");
                _ = PullCurrentAsync();   // 重连成功后主动校准(设计文档 §4.1)
                break;
            case "reconnecting":
                SetState(ConnState.Reconnecting, "");
                break;
            case "disconnected":
                SetState(ConnState.Offline, "连接已断开,自动重连中…");
                break;
        }
    }

    private void SetState(ConnState state, string message)
    {
        _state = state;
        ConnectionChanged?.Invoke(state, message);
    }

    /// <summary>上传管线:监听捕获 → 去重 → 上传 → 更新 UI。</summary>
    private async Task OnCapturedAsync(ClipboardMonitor.CapturedClip clip, CancellationToken ct)
    {
        var s = _svc.Settings;
        if (clip.Hash == _lastUploadedHash) return;
        if (!s.MonitorEnabled) return;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.AuthToken)) return; // 未配置不上传

        SetTransfer(true, TransferKind.Upload);
        try
        {
            if (clip.Text is not null)
            {
                var entry = await UploadTextWithRetryAsync(s, clip.Text, ct);
                if (entry is not null)
                {
                    _lastUploadedHash = clip.Hash;
                    History.Insert(new Models.HistoryItem
                    {
                        ServerId = entry.Id,
                        Type = "Text",
                        Text = clip.Text,
                        DeviceId = s.DeviceId,
                        DeviceName = s.DeviceName,
                        CreatedAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow,
                        Origin = 0,
                    });
                    _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, null, false));
                }
            }
            else if (clip.ImagePng is not null)
            {
                if (clip.ImagePng.LongLength > ImageCodec.MaxImageBytes)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        ConnectionChanged?.Invoke(_state, "图片超过 10MB,已跳过上传");
                        SyncError?.Invoke();
                    });
                    return;
                }
                var entry = await UploadImageWithRetryAsync(s, clip.ImagePng, ct);
                if (entry is not null)
                {
                    _lastUploadedHash = clip.Hash;
                    var path = await ImageCodec.SavePngAsync(clip.ImagePng, entry.Id);
                    History.Insert(new Models.HistoryItem
                    {
                        ServerId = entry.Id,
                        Type = "Image",
                        ImagePath = path,
                        ImageRef = entry.ImageRef,
                        DeviceId = s.DeviceId,
                        DeviceName = s.DeviceName,
                        CreatedAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow,
                        Origin = 0,
                    });
                    _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, path, false));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"上传失败:{ex.Message}");
            _dispatcher.TryEnqueue(() =>
            {
                ConnectionChanged?.Invoke(_state, $"上传失败:{ex.Message}");
                SyncError?.Invoke();
            });
        }
        finally
        {
            SetTransfer(false, TransferKind.Upload);
        }
    }

    private async Task<Models.ClipboardEntry?> UploadTextWithRetryAsync(SettingsStore s, string text, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _svc.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, ct);
            }
            catch (Exception ex) when (attempt < 3 && ex is not ApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                Log.Warn($"文本上传失败(第 {attempt + 1} 次):{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(new[] { 1, 4, 16 }[attempt]), ct);
            }
        }
    }

    private async Task<ClipboardEntry?> UploadImageWithRetryAsync(SettingsStore s, byte[] png, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _svc.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, png, s.DeviceId, s.DeviceName, ct);
            }
            catch (Exception ex) when (attempt < 3 && ex is not ApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                Log.Warn($"图片上传失败(第 {attempt + 1} 次):{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(new[] { 1, 4, 16 }[attempt]), ct);
            }
        }
    }

    /// <summary>下载管线:推送 → 图片缓存 → 写剪贴板 → 更新 UI。</summary>
    private async Task HandlePushAsync(ClipboardEntry entry)
    {
        var s = _svc.Settings;
        if (entry.DeviceId == s.DeviceId)
        {
            // 自己上传的回显:仅更新 UI
            _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, null, false));
            return;
        }

        string? imagePath = null;
        if (entry.Type == "Image" && !string.IsNullOrEmpty(entry.ImageRef))
        {
            SetTransfer(true, TransferKind.Download);
            try
            {
                var bytes = await _svc.Api.DownloadImageAsync(s.ServerUrl, s.AuthToken, entry.ImageRef!);
                if (bytes is not null)
                {
                    imagePath = await ImageCodec.SavePngAsync(bytes, entry.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"图片下载失败:{ex.Message}");
            }
            finally
            {
                SetTransfer(false, TransferKind.Download);
            }
        }

        // 写入本地历史(远端条目)
        History.Insert(new Models.HistoryItem
        {
            ServerId = entry.Id,
            Type = entry.Type,
            Text = entry.Text,
            ImagePath = imagePath,
            ImageRef = entry.ImageRef,
            DeviceId = entry.DeviceId,
            DeviceName = entry.DeviceName,
            CreatedAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow,
            Origin = 1,
        });

        if (s.AutoPaste)
        {
            try
            {
                if (entry.Type == "Text" && entry.Text is not null)
                {
                    ImageCodec.SetClipboardText(entry.Text);
                    _monitor?.SuppressNext(ClipboardMonitor.HashText(entry.Text));
                }
                else if (imagePath is not null)
                {
                    await ImageCodec.SetClipboardImageAsync(imagePath);
                    _monitor?.SuppressNext(ClipboardMonitor.HashBytes(await File.ReadAllBytesAsync(imagePath)));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"写回剪贴板失败:{ex.Message}");
            }
        }

        _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, imagePath, true));
    }

    /// <summary>拉取当前剪贴板(启动/重连校准/手动刷新)。</summary>
    public async Task PullCurrentAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.AuthToken)) return;
        try
        {
            var entry = await _svc.Api.GetCurrentAsync(s.ServerUrl, s.AuthToken);
            _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry!, null, false));
        }
        catch (Exception ex)
        {
            Log.Error($"拉取当前剪贴板失败:{ex.Message}");
        }
    }

    /// <summary>手动同步当前剪贴板(首页按钮)。</summary>
    public async Task SyncCurrentClipboardAsync()
    {
        if (_monitor is null) return;
        try
        {
            await _monitor.CaptureManualAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"手动同步失败:{ex.Message}");
        }
    }

    private void SetTransfer(bool active, TransferKind kind) =>
        _dispatcher.TryEnqueue(() => TransferChanged?.Invoke(active, kind));

    /// <summary>复制历史条目到本机剪贴板(图片先确保本地缓存),并抑制回环上传。</summary>
    public async Task CopyHistoryItemAsync(Models.HistoryItem item)
    {
        try
        {
            if (item.Type == "Text" && item.Text is not null)
            {
                ImageCodec.SetClipboardText(item.Text);
                _monitor?.SuppressNext(ClipboardMonitor.HashText(item.Text));
            }
            else if (item.Type == "Image")
            {
                var path = item.ImagePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    if (string.IsNullOrEmpty(item.ImageRef)) return;
                    var s = _svc.Settings;
                    var bytes = await _svc.Api.DownloadImageAsync(s.ServerUrl, s.AuthToken, item.ImageRef);
                    if (bytes is null) return;
                    path = await ImageCodec.SavePngAsync(bytes, item.ServerId ?? item.Id);
                }
                await ImageCodec.SetClipboardImageAsync(path!);
                _monitor?.SuppressNext(ClipboardMonitor.HashBytes(await File.ReadAllBytesAsync(path!)));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"复制历史条目失败:{ex.Message}");
        }
    }

    public void Dispose()
    {
        _monitor?.Stop();
        History.Dispose();
        _ = _push?.DisposeAsync();
    }
}
