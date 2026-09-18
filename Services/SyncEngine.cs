using Microsoft.UI.Dispatching;
using NexClip.Desktop.Models;

namespace NexClip.Desktop.Services;

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
    private System.Threading.Timer? _heartbeatTimer;
    /// <summary>心跳回调重入闸门:1 表示上一轮心跳仍在执行中。</summary>
    private int _heartbeatRunning;

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
    /// <summary>剪贴板监听器实例(诊断与状态展示用)。</summary>
    public ClipboardMonitor? Monitor => _monitor;
    public bool IsPaused { get; private set; }
    public bool IsRunning => !IsPaused;

    /// <summary>暂停实时同步(停止剪贴板捕获并断开推送)。</summary>
    public void PauseSync()
    {
        if (IsPaused) return;
        IsPaused = true;
        _monitor?.Stop();
        _ = _push?.DisconnectAsync();
        SetState(ConnState.Offline, "同步已暂停");
    }

    /// <summary>恢复实时同步(启动剪贴板捕获并重新连接推送)。</summary>
    public void ResumeSync()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _monitor?.Start();
        _ = ConnectAsync();
    }

    /// <summary>切换暂停/恢复同步状态。</summary>
    public void ToggleSync()
    {
        if (IsPaused) ResumeSync();
        else PauseSync();
    }

    public void Start()
    {
        _monitor = new ClipboardMonitor(_dispatcher, _svc.Settings, OnCapturedAsync);
        _monitor.Start();
        _push = new PushClient();
        _push.EntryReceived += entry => _dispatcher.TryEnqueue(() => _ = HandlePushAsync(entry));
        _push.DevicesChanged += () => _dispatcher.TryEnqueue(async () =>
        {
            Log.Debug("收到服务端 DevicesChanged 广播，正在后台实时刷新设备状态…");
            await _svc.SettingsVm.RefreshDevicesAsync();
            await _svc.ChatVm.RefreshDevicesAsync();
        });
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
        StartPeriodicHeartbeat();
        _ = ConnectAsync();
    }

    private void StartPeriodicHeartbeat()
    {
        if (_heartbeatTimer is not null) return;
        try
        {
            _heartbeatTimer = new System.Threading.Timer(async _ =>
            {
                // 重入保护:回调是 async 且周期固定 30 秒,离线时内部 ConnectAsync() 可能超过 30 秒,
                // 没有闸门会导致多轮回调并发叠加、反复重建 SignalR HubConnection。
                if (Interlocked.Exchange(ref _heartbeatRunning, 1) == 1) return;
                try
                {
                    if (IsPaused) return;

                    var s = _svc.Settings;
                    if (!s.IsPaired || string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.AuthToken))
                        return;

                    if (_state == ConnState.Connected && _push?.IsConnected == true)
                    {
                        try
                        {
                            // 本回调运行在线程池线程,而设备列表是绑定到 XAML 的 ObservableCollection。
                            // 直接调用会在非 UI 线程触发 CollectionChanged,抛 RPC_E_WRONG_THREAD(0x8001010E):
                            // 集合被 Clear() 后中断在 foreach 之前,设备列表长期为空并每秒刷屏错误日志。
                            // 这里统一回切 UI 线程执行,两个刷新各自内部再做一次线程校验以覆盖其它调用方。
                            _dispatcher.TryEnqueue(() =>
                            {
                                _ = _svc.SettingsVm.RefreshDevicesAsync();
                                _ = _svc.ChatVm.RefreshDevicesAsync();
                            });
                            await Task.CompletedTask;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"后台定期刷新设备状态失败: {ex.Message}");
                        }
                    }
                    else if (_state == ConnState.Offline || (_push is not null && !_push.IsConnected))
                    {
                        Log.Debug("检测到后台处于离线或推送未连接状态，正在尝试恢复连接与对齐剪贴板…");
                        try
                        {
                            await ConnectAsync();
                            await PullCurrentAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"后台自动重连尝试失败: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // async void 语义下异常会逃逸到线程池并终止进程,此处必须兜底吞掉
                    Log.Debug($"后台心跳回调异常: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _heartbeatRunning, 0);
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Log.Warn($"初始化后台心跳定时器失败: {ex.Message}");
        }
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
                _ = _svc.SettingsVm.RefreshDevicesCommand.ExecuteAsync(null); // 静默更新设备列表与缓存
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
        if (clip.Text is null && clip.ImagePng is null && clip.Files is null) return;

        try
        {
            var local = await SaveLocalCaptureAsync(clip, s);

            // 复制直达智能提示属于本地捕获反馈,不应依赖服务端是否在线。
            if (clip.Text is not null && s.CopyDirectEnabled)
            {
                var smartAction = SmartActionService.Detect(clip.Text, forToast: true);
                if (smartAction is not null)
                {
                    _dispatcher.TryEnqueue(() => App.ShowSmartActionToast(smartAction));
                }
            }

            // 先刷新本地历史,即使服务端离线也能立即看到刚复制的内容。
            _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(local.Entry, local.ImagePath, false));

            // 文件条目只记录在本地,永不进入上传链路:
            // 复制的文件常在数百 MB 级(安装包/视频/工程目录),上传会持续占用服务器带宽与存储,
            // 且这类内容通常只在同一台机器内部使用,跨设备同步的收益远低于其资源成本。
            // 这里在"落库 + 刷新 UI"之后短路,既保证本地历史完整,又不产生任何网络请求。
            if (clip.Files is not null)
            {
                Log.Debug($"文件条目仅保存到本地历史(共 {clip.Files.Count} 项),按设计不参与同步");
                return;
            }

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

        // 说明:HistoryStore 的读写是同步阻塞的 SQLite 调用,而本方法由剪贴板捕获链路在 UI 线程调用。
        // 每次复制在 UI 线程做 2~3 次查询 + 1 次写入(Insert 内部还会执行超限清理 TrimToLimitLocked),
        // 数据库变大或磁盘繁忙时会直接表现为复制卡顿。这里统一挪到线程池;
        // HistoryStore 内部用锁串行化,跨线程调用是安全的。

        var existing = await Task.Run(() => History.FindByHash(clip.Hash));
        if (existing is not null)
        {
            var refreshed = await Task.Run(() =>
            {
                History.TouchByHash(clip.Hash, null, s.DeviceId, s.DeviceName, now, appName, appPath, appIcon);
                return History.FindByHash(clip.Hash) ?? existing;
            });
            return new LocalCapture(ToClipboardEntry(refreshed), refreshed.ImagePath);
        }

        string? imagePath = null;
        if (clip.ImagePng is { Length: > 0 })
        {
            // 本地条目尚未有服务端 id,使用负 ticks 生成不会冲突的缓存文件名。
            imagePath = await ImageCodec.SavePngAsync(clip.ImagePng, -Math.Abs(now.Ticks));
        }

        // 文件条目不落 image_path(必须保持 NULL):该列指向本应用生成的图片缓存,
        // 超限清理与清空历史都会直接删除它指向的文件,写入用户真实文件会导致误删。
        // 文件内容本身也不复制,只在 file_paths 中记录路径元数据。
        var isFile = clip.Files is { Count: > 0 };

        var item = new Models.HistoryItem
        {
            Type = isFile ? "File" : clip.ImagePng is { Length: > 0 } ? "Image" : "Text",
            Text = clip.Text,
            Html = clip.Html,
            ImagePath = imagePath,
            FilePathsJson = isFile ? ClipboardFileMeta.Serialize(clip.Files!) : null,
            DeviceId = s.DeviceId,
            DeviceName = s.DeviceName,
            SourceAppName = appName,
            SourceAppPath = appPath,
            SourceAppIcon = appIcon,
            CreatedAt = now,
            Origin = 0,
            ContentHash = clip.Hash,
        };
        var saved = await Task.Run(() =>
        {
            History.Insert(item);
            return History.FindByHash(clip.Hash) ?? item;
        });
        return new LocalCapture(ToClipboardEntry(saved), saved.ImagePath);
    }

    private static ClipboardEntry ToClipboardEntry(Models.HistoryItem item) => new()
    {
        Id = item.ServerId ?? 0,
        Type = item.Type,
        Text = item.Text,
        Html = item.Html,
        ImageRef = item.ImageRef,
        DeviceId = item.DeviceId,
        DeviceName = item.DeviceName,
        CreatedAt = item.CreatedAt,
    };

    private async Task UploadCapturedAsync(ClipboardMonitor.CapturedClip clip, SettingsStore s)
    {
        // 兜底护栏:文件条目按设计永不外发。OnCapturedAsync 已在调用前短路,
        // 这里再挡一次,确保后续任何新增的调用路径都不会把文件推到服务端。
        if (clip.Files is not null) return;

        await _uploadGate.WaitAsync();
        SetTransfer(true, TransferKind.Upload);
        try
        {
            if (clip.Text is not null)
            {
                var entry = await UploadTextWithRetryAsync(s, clip.Text, clip.Html, CancellationToken.None);
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

    private async Task<Models.ClipboardEntry?> UploadTextWithRetryAsync(SettingsStore s, string text, string? html, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _svc.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, SystemInfo.Platform, SystemInfo.Version, isManual: false, html: html, ct: ct);
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
                return await _svc.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, png, s.DeviceId, s.DeviceName, SystemInfo.Platform, SystemInfo.Version, isManual: false, ct);
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

        // 内容哈希:远端文本/图片字节,供本地重复内容去重置顶使用。
        // 富文本片段必须无条件参与哈希(与发送端算法一致),否则两端算出的哈希会不一致。
        var contentHash = entry.Type == "Text" && entry.Text is not null
            ? ClipboardMonitor.HashText(entry.Text, entry.Html)
            : imageBytes is not null
                ? ClipboardMonitor.HashBytes(imageBytes)
                : null;

        // 写入本地历史(远端条目: 手动互传条目始终插入新记录以完整保留对话流与发送方设备信息; 普通剪贴板先尝试命中已有相同内容置顶)
        var touched = !entry.IsManual && contentHash is not null && History.TouchByHash(
            contentHash,
            entry.Id,
            entry.DeviceId,
            entry.DeviceName,
            entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow);

        if (!touched)
        {
            History.Insert(new Models.HistoryItem
            {
                ServerId = entry.Id,
                Type = entry.Type,
                Text = entry.Text,
                Html = entry.Html,
                ImagePath = imagePath,
                ImageRef = entry.ImageRef,
                DeviceId = entry.DeviceId,
                DeviceName = entry.DeviceName,
                CreatedAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow,
                Origin = 1,
                SourceAppName = entry.IsManual ? "即时互传" : null,
                ContentHash = contentHash,
            });
        }

        var isFresh = entry.CreatedAt == default || Math.Abs((DateTime.UtcNow - entry.CreatedAt).TotalMinutes) <= 5;
        if (s.AutoPaste && isFresh)
        {
            try
            {
                using var _ = _monitor?.PauseCapture(TimeSpan.FromSeconds(3));
                if (entry.Type == "Text" && entry.Text is not null)
                {
                    // 富文本开关关闭时只写回纯文本
                    ImageCodec.SetClipboardText(entry.Text, s.RichTextEnabled ? entry.Html : null);
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
                // 带上类型与 HRESULT:剪贴板争用类异常(如 CLIPBRD_E_CANT_OPEN)的 Message 往往为空,
                // 只打 Message 会让日志出现"写回剪贴板失败:"这种无法定位的空描述。
                Log.Error($"写回剪贴板失败: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}", ex);
            }
        }

        // 新内容通知: 若开启通知且为远端设备同步来的新鲜剪贴板, 在右下角弹出提示浮窗
        if (s.NotifyEnabled && isFresh)
        {
            _dispatcher.TryEnqueue(() =>
            {
                var senderName = string.IsNullOrWhiteSpace(entry.DeviceName) ? "远端设备" : entry.DeviceName;
                var desc = entry.Type == "Text"
                    ? (entry.Text?.Length > 60 ? entry.Text[..60] + "..." : entry.Text ?? "")
                    : "已同步新图片到剪贴板";
                var action = new SmartAction
                {
                    Kind = SmartActionKind.Url,
                    Title = $"来自 {senderName} 的新剪贴板",
                    Subtitle = desc,
                    Icon = Lucide.ClipboardAccent,
                    PrimaryButtonText = "查看历史",
                    PrimaryButtonIcon = Lucide.ClipboardAccent,
                    PrimaryAction = () =>
                    {
                        App.ClipboardWindow?.ShowWindow();
                    }
                };
                App.ShowSmartActionToast(action);
            });
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
            if (entry is null) return;
            var isFresh = entry.CreatedAt == default || Math.Abs((DateTime.UtcNow - entry.CreatedAt).TotalMinutes) <= 5;
            if (isFresh)
            {
                _dispatcher.TryEnqueue(() => EntryUpdated?.Invoke(entry, null, false));
            }
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

    /// <summary>复制历史条目到本机剪贴板(图片先确保本地缓存),并抑制回环上传。支持 plainText 纯文本模式。</summary>
    public async Task CopyHistoryItemAsync(Models.HistoryItem item, bool plainText = false)
    {
        try
        {
            using var _ = _monitor?.PauseCapture();
            if (item.Type == "Text" && item.Text is not null)
            {
                // plainText 为真(Shift+粘贴 / 右键"粘贴为纯文本")或富文本开关关闭时,只写纯文本;
                // 否则同时写 HTML 与纯文本,由目标程序按自身能力择取。
                var html = plainText || !_svc.Settings.RichTextEnabled ? null : item.Html;
                ImageCodec.WriteClipboardText(item.Text, html);
                // 抑制回环用的哈希必须与实际写入剪贴板的内容一致:降级为纯文本时不能用条目原本的富文本哈希
                var hash = html is null
                    ? ClipboardMonitor.HashText(item.Text)
                    : item.ContentHash ?? ClipboardMonitor.HashText(item.Text, html);
                _monitor?.RecordLastSeen(hash);
            }
            else if (item.Type == "Image")
            {
                if (plainText)
                {
                    if (!string.IsNullOrEmpty(item.ImagePath)) ImageCodec.WriteClipboardText(item.ImagePath);
                    return;
                }
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
            else if (item.Type == "File")
            {
                // 写回的是文件引用(CF_HDROP),不是文件内容:即使条目包含大文件也不产生额外磁盘/内存开销。
                // 已失效的路径由 ClipboardFiles 内部跳过;全部失效时剪贴板保持原内容不变。
                var alive = item.Files
                    .Where(f => File.Exists(f.Path) || Directory.Exists(f.Path))
                    .Select(f => f.Path)
                    .ToList();
                if (alive.Count == 0)
                {
                    Log.Warn($"复制文件条目失败:文件已全部不存在或无法访问(id={item.Id})");
                    return;
                }

                if (plainText)
                {
                    // 纯文本模式:写入换行分隔的路径,便于粘贴到终端、编辑器或远程会话
                    ImageCodec.WriteClipboardText(string.Join(Environment.NewLine, alive));
                    _monitor?.RecordLastSeen(ClipboardMonitor.HashText(string.Join(Environment.NewLine, alive)));
                    return;
                }

                if (!await ClipboardFiles.WriteClipboardFilesAsync(alive)) return;
                _monitor?.RecordLastSeen(item.ContentHash ?? ClipboardFileMeta.ComputeHash(item.Files));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"复制历史条目失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 把已经合并好的文本写入剪贴板(批量粘贴用),并抑制回环上传。
    /// 复用单条复制的回环抑制方式:哈希必须与真正写进剪贴板的内容一致,否则
    /// 下次捕获会把它当成新内容再次上传。
    /// </summary>
    public void WriteMergedTextToClipboard(string text)
    {
        try
        {
            using var _ = _monitor?.PauseCapture();
            ImageCodec.WriteClipboardText(text);
            _monitor?.RecordLastSeen(ClipboardMonitor.HashText(text));
        }
        catch (Exception ex)
        {
            Log.Error($"批量粘贴写入剪贴板失败:{ex.Message}");
        }
    }

    /// <summary>手动推送单条历史记录到所有设备。文件条目按设计不支持推送,直接返回 false。</summary>
    public async Task<bool> PushHistoryItemAsync(Models.HistoryItem item)
    {
        // 文件条目体积大,推送到服务器会显著占用带宽与存储,且跨设备还原文件并非本工具的设计目标
        if (item.Type == "File") return false;

        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired)
        {
            throw new InvalidOperationException("尚未配对或连接服务器");
        }

        SetTransfer(true, TransferKind.Upload);
        try
        {
            if (item.Type == "Text" && item.Text is not null)
            {
                // 富文本开关关闭时只推送纯文本,与本机写回剪贴板的行为保持一致
                var html = s.RichTextEnabled ? item.Html : null;
                await _svc.Api.PutTextAsync(s.ServerUrl, s.AuthToken, item.Text, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString, html: html);
                return true;
            }
            else if (item.Type == "Image")
            {
                var path = item.ImagePath;
                byte[]? bytes = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    bytes = await File.ReadAllBytesAsync(path);
                }
                else if (!string.IsNullOrEmpty(item.ImageRef))
                {
                    bytes = await _svc.Api.DownloadImageAsync(s.ServerUrl, s.DeviceId, s.AuthToken, item.ImageRef);
                }

                if (bytes is not null && bytes.Length > 0)
                {
                    await _svc.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, bytes, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString);
                    return true;
                }
            }
            return false;
        }
        finally
        {
            SetTransfer(false, TransferKind.Upload);
        }
    }

    /// <summary>手动推送自定义文本或图片到所有设备。</summary>
    public async Task<bool> PushManualContentAsync(string? text, byte[]? imageBytes)
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired)
        {
            throw new InvalidOperationException("尚未配对或连接服务器");
        }

        SetTransfer(true, TransferKind.Upload);
        try
        {
            if (imageBytes is not null && imageBytes.Length > 0)
            {
                await _svc.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, imageBytes, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString);
                return true;
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                await _svc.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text.Trim(), s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString);
                return true;
            }
            return false;
        }
        finally
        {
            SetTransfer(false, TransferKind.Upload);
        }
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
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
