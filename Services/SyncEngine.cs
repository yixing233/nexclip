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
    // 上传与本地捕获解耦:网络重试不能阻塞剪贴板监听,否则连续复制会被监听器丢弃。
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private ConnState _state = ConnState.NotConfigured;
    private int _credentialRecoveryInProgress;

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
        _push.ErrorOccurred += ex => _dispatcher.TryEnqueue(() =>
        {
            if (IsDeviceAuthFailure(ex))
            {
                _ = RecoverLegacyCredentialOrInvalidateAsync(ex);
                return;
            }
            ConnectionChanged?.Invoke(_state,
                $"连接失败：{ServerApi.DescribeException(ex, "无法连接到同步服务。")} 自动重连中…");
        });
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
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired)
        {
            SetState(ConnState.NotConfigured, "");
            return;
        }
        if (_push is null) return;

        // 旧版客户端只有 IsPaired，没有设备令牌。服务端为旧数据库中的已登记设备
        // 提供一次性领取路径；领取后立即作废临时配对码，再按新协议连接。
        if (string.IsNullOrWhiteSpace(s.AuthToken))
        {
            SetState(ConnState.Connecting, "正在升级设备凭证…");
            try
            {
                var migration = await _svc.Api.CreatePairingCodeAsync(
                    s.ServerUrl, s.DeviceId, s.DeviceName, "");
                if (migration is null || string.IsNullOrWhiteSpace(migration.DeviceToken))
                {
                    s.IsPaired = false;
                    s.Save();
                    SetState(ConnState.NotConfigured, "旧设备凭证无法自动升级，请重新配对");
                    return;
                }

                s.AuthToken = migration.DeviceToken;
                s.Save();
                if (!string.IsNullOrWhiteSpace(migration.Code))
                {
                    await _svc.Api.RevokePairingCodeAsync(
                        s.ServerUrl, migration.Code, s.DeviceId, s.AuthToken);
                }
                Log.Info("旧设备凭证已自动升级");
            }
            catch (ApiException ex) when (ex.StatusCode is
                System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden or
                System.Net.HttpStatusCode.NotFound or
                System.Net.HttpStatusCode.Conflict or
                System.Net.HttpStatusCode.Gone)
            {
                s.IsPaired = false;
                s.AuthToken = "";
                s.Save();
                SetState(ConnState.NotConfigured, "旧设备状态已失效，请重新配对");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"旧设备凭证自动升级失败:{ex.Message}");
                SetState(ConnState.Offline, "设备凭证升级失败，稍后重试");
                return;
            }
        }

        SetState(ConnState.Connecting, "");
        // 携带设备信息与设备凭证连接;服务端仅允许已配对设备建立推送通道。
        await _push.ConnectAsync(s.ServerUrl, s.DeviceId, s.DeviceName, SystemInfo.Platform, SystemInfo.Version, s.AuthToken);
        // ConnectAsync 内部会触发 connected/disconnected 状态回调
        await PullCurrentAsync();
    }

    /// <summary>
    /// 兼容更早版本保存在 AuthToken 中的共享令牌：新服务端返回 401 时，尝试按设备 ID
    /// 领取一次设备令牌。若服务端已有新令牌或设备已撤销，迁移会失败并要求重新配对。
    /// </summary>
    private async Task RecoverLegacyCredentialOrInvalidateAsync(Exception authError)
    {
        if (Interlocked.Exchange(ref _credentialRecoveryInProgress, 1) != 0) return;
        try
        {
            var s = _svc.Settings;
            if (ShouldAttemptLegacyMigration(authError) && s.IsPaired && !string.IsNullOrWhiteSpace(s.ServerUrl))
            {
                try
                {
                    var migration = await _svc.Api.CreatePairingCodeAsync(
                        s.ServerUrl, s.DeviceId, s.DeviceName, "");
                    if (migration is not null && !string.IsNullOrWhiteSpace(migration.DeviceToken))
                    {
                        s.AuthToken = migration.DeviceToken;
                        s.Save();
                        if (!string.IsNullOrWhiteSpace(migration.Code))
                        {
                            await _svc.Api.RevokePairingCodeAsync(
                                s.ServerUrl, migration.Code, s.DeviceId, s.AuthToken);
                        }
                        Log.Info("旧共享凭证已迁移为设备凭证");
                        await ConnectAsync();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"旧共享凭证迁移失败:{ex.Message}");
                }
            }

            s.IsPaired = false;
            s.AuthToken = "";
            s.Save();
            await (_push?.DisconnectAsync() ?? Task.CompletedTask);
            SetState(ConnState.NotConfigured, "设备凭证已失效，请重新配对");
        }
        finally
        {
            Interlocked.Exchange(ref _credentialRecoveryInProgress, 0);
        }
    }

    private static bool ShouldAttemptLegacyMigration(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is ApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) return true;
            var message = current.Message;
            if (message.Contains("4001", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Device removed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("设备已被移除", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("410", StringComparison.OrdinalIgnoreCase)) return false;
            if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
                if (!_svc.Settings.IsPaired || string.IsNullOrWhiteSpace(_svc.Settings.AuthToken))
                {
                    SetState(ConnState.NotConfigured, "设备凭证已失效，请重新配对");
                }
                else
                {
                    SetState(ConnState.Offline, "连接已断开,自动重连中…");
                }
                break;
        }
    }

    private void SetState(ConnState state, string message)
    {
        _state = state;
        ConnectionChanged?.Invoke(state, message);
    }

    /// <summary>剪贴板捕获管线:先落本地历史,再异步上传,避免网络异常导致复制内容丢失。</summary>
    private async Task OnCapturedAsync(ClipboardMonitor.CapturedClip clip, CancellationToken ct)
    {
        var s = _svc.Settings;
        if (!s.MonitorEnabled) return;
        if (clip.Text is null && clip.ImagePng is null) return;

        try
        {
            var local = await SaveLocalCaptureAsync(clip, s);

            // 复制直达提示属于本地捕获反馈,不应依赖服务端是否在线。
            if (clip.Text is not null && s.CopyDirectEnabled && Services.UrlUtil.IsUrl(clip.Text))
            {
                var linkUrl = clip.Text.Trim();
                _dispatcher.TryEnqueue(() => App.ShowLinkToast(linkUrl));
            }

            // 先刷新本地历史,即使服务端离线也能立即看到刚复制的内容。
            _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(local.Entry, local.ImagePath, false));

            // 未配置/未配对时只保留本地历史;配置恢复后由后续复制触发上传。
            if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired) return;

            // 不等待网络重试,否则 ClipboardMonitor 会一直处于 _capturing 状态。
            _ = UploadCapturedAsync(clip, s);
        }
        catch (Exception ex)
        {
            Log.Error($"本地保存剪贴板失败:{ex.Message}", ex);
        }
    }

    private readonly record struct LocalCapture(ClipboardEntry Entry, string? ImagePath);

    private async Task<LocalCapture> SaveLocalCaptureAsync(ClipboardMonitor.CapturedClip clip, SettingsStore s)
    {
        var now = DateTime.UtcNow;
        var appName = clip.SourceApp?.Name;
        var appPath = clip.SourceApp?.ExecutablePath;
        var appIcon = clip.SourceApp?.IconPath;

        var existing = History.FindByHash(clip.Hash);
        if (existing is not null)
        {
            History.TouchByHash(clip.Hash, null, s.DeviceId, s.DeviceName, now, appName, appPath, appIcon);
            existing = History.FindByHash(clip.Hash) ?? existing;
            return new LocalCapture(ToClipboardEntry(existing), existing.ImagePath);
        }

        string? imagePath = null;
        if (clip.ImagePng is { Length: > 0 })
        {
            // 本地条目尚未有服务端 id,使用负 ticks 生成不会冲突的缓存文件名。
            imagePath = await ImageCodec.SavePngAsync(clip.ImagePng, -Math.Abs(now.Ticks));
        }

        var item = new Models.HistoryItem
        {
            Type = clip.ImagePng is { Length: > 0 } ? "Image" : "Text",
            Text = clip.Text,
            ImagePath = imagePath,
            DeviceId = s.DeviceId,
            DeviceName = s.DeviceName,
            SourceAppName = appName,
            SourceAppPath = appPath,
            SourceAppIcon = appIcon,
            CreatedAt = now,
            Origin = 0,
            ContentHash = clip.Hash,
        };
        History.Insert(item);
        var saved = History.FindByHash(clip.Hash) ?? item;
        return new LocalCapture(ToClipboardEntry(saved), saved.ImagePath);
    }

    private static ClipboardEntry ToClipboardEntry(Models.HistoryItem item) => new()
    {
        Id = item.ServerId ?? 0,
        Type = item.Type,
        Text = item.Text,
        ImageRef = item.ImageRef,
        DeviceId = item.DeviceId,
        DeviceName = item.DeviceName,
        CreatedAt = item.CreatedAt,
    };

    private async Task UploadCapturedAsync(ClipboardMonitor.CapturedClip clip, SettingsStore s)
    {
        await _uploadGate.WaitAsync();
        SetTransfer(true, TransferKind.Upload);
        try
        {
            if (clip.Text is not null)
            {
                var entry = await UploadTextWithRetryAsync(s, clip.Text, CancellationToken.None);
                if (entry is not null)
                {
                    var createdAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow;
                    History.TouchByHash(clip.Hash, entry.Id, s.DeviceId, s.DeviceName, createdAt);
                    _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, null, false));
                }
            }
            else if (clip.ImagePng is { Length: > 0 })
            {
                if (clip.ImagePng.LongLength > ImageCodec.MaxImageBytes)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        ConnectionChanged?.Invoke(_state, "图片超过 10MB,已保存在本地但跳过上传");
                        SyncError?.Invoke();
                    });
                    return;
                }
                var entry = await UploadImageWithRetryAsync(s, clip.ImagePng, CancellationToken.None);
                if (entry is not null)
                {
                    var createdAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow;
                    History.TouchByHash(clip.Hash, entry.Id, s.DeviceId, s.DeviceName, createdAt);
                    var local = History.FindByHash(clip.Hash);
                    _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, local?.ImagePath, false));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"上传失败:{ex.Message}");
            if (IsDeviceAuthFailure(ex))
            {
                s.IsPaired = false;
                s.AuthToken = "";
                s.Save();
                _ = _push?.DisconnectAsync();
            }
            _dispatcher.TryEnqueue(() =>
            {
                var reason = ServerApi.DescribeException(ex, "请检查服务器配置后重试。");
                ConnectionChanged?.Invoke(_state, $"上传失败(内容已保存在本地)：{reason}");
                SyncError?.Invoke();
            });
        }
        finally
        {
            SetTransfer(false, TransferKind.Upload);
            _uploadGate.Release();
        }
    }

    private async Task<Models.ClipboardEntry?> UploadTextWithRetryAsync(SettingsStore s, string text, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _svc.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, SystemInfo.Platform, SystemInfo.Version, ct);
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
                return await _svc.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, png, s.DeviceId, s.DeviceName, SystemInfo.Platform, SystemInfo.Version, ct);
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
        byte[]? imageBytes = null;
        if (entry.Type == "Image" && !string.IsNullOrEmpty(entry.ImageRef))
        {
            SetTransfer(true, TransferKind.Download);
            try
            {
                var bytes = await _svc.Api.DownloadImageAsync(s.ServerUrl, s.DeviceId, s.AuthToken, entry.ImageRef!);
                if (bytes is not null)
                {
                    imagePath = await ImageCodec.SavePngAsync(bytes, entry.Id);
                    imageBytes = bytes;
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

        // 内容哈希:远端文本/图片字节,供本地重复内容去重置顶使用
        var contentHash = entry.Type == "Text" && entry.Text is not null
            ? ClipboardMonitor.HashText(entry.Text)
            : imageBytes is not null
                ? ClipboardMonitor.HashBytes(imageBytes)
                : null;

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
            ContentHash = contentHash,
        });

        if (s.AutoPaste)
        {
            try
            {
                using var _ = _monitor?.PauseCapture();
                if (entry.Type == "Text" && entry.Text is not null)
                {
                    ImageCodec.SetClipboardText(entry.Text);
                    if (contentHash is not null) _monitor?.RecordLastSeen(contentHash);
                }
                else if (imagePath is not null)
                {
                    await ImageCodec.SetClipboardImageAsync(imagePath);
                    if (contentHash is not null) _monitor?.RecordLastSeen(contentHash);
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
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired) return;
        try
        {
            var entry = await _svc.Api.GetCurrentAsync(s.ServerUrl, s.DeviceId, s.AuthToken);
            _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry!, null, false));
        }
        catch (Exception ex)
        {
            Log.Error($"拉取当前剪贴板失败:{ex.Message}");
            if (IsDeviceAuthFailure(ex))
            {
                s.IsPaired = false;
                s.AuthToken = "";
                s.Save();
                _ = _push?.DisconnectAsync();
                SetState(ConnState.NotConfigured, "设备凭证已失效，请重新配对");
            }
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
            using var _ = _monitor?.PauseCapture();
            if (item.Type == "Text" && item.Text is not null)
            {
                ImageCodec.SetClipboardText(item.Text);
                var hash = item.ContentHash ?? ClipboardMonitor.HashText(item.Text);
                _monitor?.RecordLastSeen(hash);
            }
            else if (item.Type == "Image")
            {
                var path = item.ImagePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    if (string.IsNullOrEmpty(item.ImageRef)) return;
                    var s = _svc.Settings;
                    var bytes = await _svc.Api.DownloadImageAsync(s.ServerUrl, s.DeviceId, s.AuthToken, item.ImageRef);
                    if (bytes is null) return;
                    path = await ImageCodec.SavePngAsync(bytes, item.ServerId ?? item.Id);
                }
                await ImageCodec.SetClipboardImageAsync(path!);
                var hash = item.ContentHash ?? ClipboardMonitor.HashBytes(await File.ReadAllBytesAsync(path!));
                _monitor?.RecordLastSeen(hash);
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
        _uploadGate.Dispose();
        History.Dispose();
        _ = _push?.DisposeAsync();
    }

    private static bool IsDeviceAuthFailure(Exception ex) => ex is ApiException api &&
        (api.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
         api.StatusCode == System.Net.HttpStatusCode.Forbidden ||
         api.StatusCode == (System.Net.HttpStatusCode)410) ||
        ex.Message.Contains("4001", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Device removed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("设备已被移除", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("410", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("设备凭证", StringComparison.OrdinalIgnoreCase);
}
