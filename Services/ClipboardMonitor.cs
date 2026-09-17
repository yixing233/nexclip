using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using NexClip.Desktop.Models;
using Windows.ApplicationModel.DataTransfer;

namespace NexClip.Desktop.Services;

/// <summary>
/// 剪贴板监听:ContentChanged 事件 + 150ms 去抖 + 2s 轮询兜底。
/// 捕获回调在 UI 线程执行(剪贴板 API 要求)。自写回内容通过 SuppressHash 抑制(防回环)。
/// </summary>
public sealed class ClipboardMonitor
{
    public readonly record struct CapturedClip(
        string? Text,
        byte[]? ImagePng,
        string Hash,
        SourceAppInfo? SourceApp = null,
        string? Html = null,
        IReadOnlyList<ClipboardFileInfo>? Files = null);

    /// <summary>一次剪贴板读取的完整结果。文件字段为"复制的文件"元数据,不含文件内容。</summary>
    public readonly record struct ClipboardPayload(
        string? Text,
        string? Html,
        byte[]? Image,
        IReadOnlyList<ClipboardFileInfo>? Files);

    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsStore _settings;
    private readonly Func<CapturedClip, CancellationToken, Task> _onCapture;
    private readonly DispatcherQueueTimer? _pollTimer;
    private CancellationTokenSource? _debounceCts;
    /// <summary>捕获链互斥标记(1=有捕获进行中)。用 Interlocked 访问:看门狗在后台线程读写它。</summary>
    private int _capturing;
    private string _lastSeenHash = "";
    private string _suppressHash = "";
    private DateTime _suppressUntil = DateTime.MinValue;
    private int _pauseCount;
    private DateTime _pauseUntil = DateTime.MinValue;
    private bool _started;
    /// <summary>当前捕获阶段(用于诊断与看门狗判断)。</summary>
    private volatile string _captureStage = "idle";
    /// <summary>当前捕获开始时间;无进行中捕获时为 null。</summary>
    private long _captureStartedUtcTicks;
    /// <summary>最后一次捕获成功完成的时间。</summary>
    private long _lastCaptureSuccessUtcTicks;
    /// <summary>最后一次捕获异常时间。</summary>
    private long _lastCaptureFailureUtcTicks;
    private int _captureFailureCount;
    private int _watchdogRecoveryCount;

    /// <summary>剪贴板首次探测失败的时间;探测成功后清零。用于判断"持续不可用"而非"瞬时争用"。</summary>
    private long _clipboardUnavailableSinceTicks;
    /// <summary>剪贴板被持续独占(熔断打开)时为 1,此时跳过一切跨进程剪贴板读取。</summary>
    private int _clipboardBlocked;
    /// <summary>熔断触发次数。</summary>
    private int _clipboardBlockedCount;

    /// <summary>剪贴板持续不可用达到阈值时触发(参数为占用剪贴板的进程名,未知时为 null)。仅在状态翻转时触发一次。</summary>
    public event Action<string?>? ClipboardBlocked;

    /// <summary>
    /// 看门狗计时器刻意使用后台线程的 <see cref="System.Threading.Timer"/> 而非 DispatcherQueueTimer:
    /// 它要检测的正是"UI 线程被跨进程剪贴板调用挂住"这一情形,若与 UI 线程共用调度器,
    /// UI 线程一卡死看门狗自身也不再触发,检测能力归零。
    /// </summary>
    private readonly System.Threading.Timer? _watchdogTimer;
    private readonly int _watchdogTimeoutSeconds = 15;
    private readonly int _watchdogIntervalMs = 5000;
    /// <summary>剪贴板持续不可用多久后判定为"被独占",跳过读取并提示用户(毫秒)。</summary>
    private readonly int _clipboardBlockedThresholdMs = 10000;

    /// <summary>看门狗检测到捕获链超时并重建监听时触发(参数为超时秒数)。</summary>
    public event Action<int>? WatchdogRecovered;

    /// <summary>捕获链当前阶段。</summary>
    public string CaptureStage => _captureStage;

    /// <summary>当前是否处于捕获中。</summary>
    public bool IsCapturing => Volatile.Read(ref _capturing) != 0;

    /// <summary>剪贴板是否已被判定为持续不可用(熔断打开)。</summary>
    public bool IsClipboardBlocked => Volatile.Read(ref _clipboardBlocked) != 0;

    /// <summary>剪贴板熔断触发次数。</summary>
    public int ClipboardBlockedCount => _clipboardBlockedCount;

    /// <summary>最后一次成功捕获时间(UTC);从未成功时为 null。</summary>
    public DateTime? LastCaptureSuccessUtc => _lastCaptureSuccessUtcTicks == 0 ? null : new DateTime(_lastCaptureSuccessUtcTicks, DateTimeKind.Utc);

    /// <summary>最后一次失败时间(UTC);从未失败时为 null。</summary>
    public DateTime? LastCaptureFailureUtc => _lastCaptureFailureUtcTicks == 0 ? null : new DateTime(_lastCaptureFailureUtcTicks, DateTimeKind.Utc);

    public int CaptureFailureCount => _captureFailureCount;
    public int WatchdogRecoveryCount => _watchdogRecoveryCount;

    /// <summary>当前捕获耗时(毫秒);未捕获时为 0。</summary>
    public double CurrentCaptureElapsedMs
    {
        get
        {
            var started = Interlocked.Read(ref _captureStartedUtcTicks);
            return started == 0 ? 0 : (DateTime.UtcNow - new DateTime(started, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }

    /// <summary>上一次已处理的剪贴板序列号:内容未变化时该值不变,用于零成本短路轮询。</summary>
    private uint _lastClipboardSequence;

    public ClipboardMonitor(DispatcherQueue dispatcher, SettingsStore settings, Func<CapturedClip, CancellationToken, Task> onCapture)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _onCapture = onCapture;
        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(2);
        _pollTimer.Tick += (_, _) => _ = CaptureAsync();
        // 看门狗与轮询刻意分开:轮询必须留在 UI 线程(剪贴板 API 的线程亲和性),
        // 而看门狗要在 UI 线程卡死时仍然存活,所以跑在后台线程池上。
        _watchdogTimer = new System.Threading.Timer(_ => CheckWatchdog(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Clipboard.ContentChanged += OnContentChanged;
        _pollTimer?.Start();
        _watchdogTimer?.Change(_watchdogIntervalMs, _watchdogIntervalMs);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        Clipboard.ContentChanged -= OnContentChanged;
        _pollTimer?.Stop();
        _watchdogTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>暂停剪贴板捕获作用域:写回剪贴板时使用,彻底阻断写回触发的回环风暴。</summary>
    public IDisposable PauseCapture(TimeSpan? duration = null)
    {
        Interlocked.Increment(ref _pauseCount);
        _pauseUntil = DateTime.UtcNow.Add(duration ?? TimeSpan.FromMilliseconds(1500));
        return new ActionDisposable(() =>
        {
            Interlocked.Decrement(ref _pauseCount);
            _pauseUntil = DateTime.UtcNow.AddMilliseconds(1000);
        });
    }

    /// <summary>记录已知内容 Hash,避免自写回后再次捕获或上传。</summary>
    public void RecordLastSeen(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        _lastSeenHash = hash;
        _suppressHash = hash;
        _suppressUntil = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>记录"由本端写入"的内容 hash,时间窗内的一次捕获忽略(防回环);过期后不再拦截,外部复制同一内容仍可正常捕获。</summary>
    public void SuppressNext(string hash)
    {
        _suppressHash = hash;
        _suppressUntil = DateTime.UtcNow.AddSeconds(5);
    }

    private void OnContentChanged(object? sender, object e)
    {
        if (_pauseCount > 0 || DateTime.UtcNow < _pauseUntil) return;
        _captureStage = "starting";
        // 先取出旧实例再替换,最后取消并释放:被替换掉的 CancellationTokenSource 若只 Cancel 不 Dispose,
        // 其内部的定时器与回调注册会一直存活到 GC,剪贴板高频变动时会持续堆积。
        var previous = _debounceCts;
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        try
        {
            previous?.Cancel();
            previous?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已被释放,忽略
        }
        _ = DebounceThenCaptureAsync(ct);
    }

    private async Task DebounceThenCaptureAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(150, ct);
            await CaptureAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 新一轮变化到来,取消本轮
        }
    }

    /// <summary>捕获当前剪贴板内容(去抖后或轮询调用)。互斥:同一时刻只允许一个捕获链。</summary>
    public async Task CaptureAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0) return;
        try
        {
            if (!_settings.MonitorEnabled) return;
            if (_pauseCount > 0 || DateTime.UtcNow < _pauseUntil) return;

            // 零成本短路(必须放在所有剪贴板读取之前):剪贴板序列号只在内容真正变化时递增。
            // 序列号与上次成功处理的一致说明内容完全没变,直接返回,避免 2 秒轮询在剪贴板里
            // 存着截图时反复做整图解码 + PNG 重编码,以及反复跨进程读取剪贴板所有者。
            // 取值失败(返回 0 或抛异常)时不做任何短路,退回原有完整流程,保证不漏掉剪贴板变更。
            uint clipboardSequence = 0;
            try
            {
                clipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            }
            catch
            {
                // 序列号不可用:放弃此项优化,继续走完整捕获流程
                clipboardSequence = 0;
            }
            if (clipboardSequence != 0 && clipboardSequence == _lastClipboardSequence) return;

            // 彻底杜绝自写回环(廉价快路径): 若剪贴板所有者即本进程,直接忽略。
            // 使用 Win32 查询,不触发跨进程 OLE 调用。
            if (SourceAppDetector.IsClipboardOwnedByCurrentProcess())
            {
                // 这是"明确决定忽略本次剪贴板状态"而非"读取失败需要重试",必须记账序列号:
                // 否则剪贴板长期停在自写内容上时,2 秒轮询会无限期反复执行昂贵的 Clipboard.GetContent()
                if (clipboardSequence != 0) _lastClipboardSequence = clipboardSequence;
                return;
            }

            // ---- 前置守卫(必须在任何跨进程读取之前) ----
            // 剪贴板读取走 OLE 延迟渲染:所有者不响应时,等待发生在调用返回之前,
            // 客户端侧没有任何超时/CancellationToken 能打断它,一旦陷进去本进程也会一起挂死。
            // 所以唯一的预防手段是"在发起之前先确认可以安全读取",下面两道检查都只做本地 Win32 调用。
            if (!ProbeClipboardAvailable()) return;
            if (IsBlockedSource(clipboardSequence)) return;

            Interlocked.Exchange(ref _captureStartedUtcTicks, DateTime.UtcNow.Ticks);
            _captureStage = "reading";
            try
            {
                // Clipboard.GetContent() 是一次跨进程 OLE 调用,当剪贴板所有者无响应时可能阻塞数百毫秒。
                // 整条捕获链路只调用一次,后续所有读取(自写标记 / 文本 / HTML / 位图)复用同一视图。
                DataPackageView content;
                try
                {
                    content = Clipboard.GetContent();
                }
                catch (Exception ex)
                {
                    Log.Debug($"获取剪贴板内容失败: {ex.Message}");
                    return;
                }

                // 精确回环判定:本程序写回的内容带 SelfOrigin 标记(Clipboard.Flush 后系统接管所有权,
                // 上面的 GetClipboardOwner 快路径会失效,必须靠这个标记兜底)。
                if (ImageCodec.IsSelfWrittenClipboard(content))
                {
                    if (clipboardSequence != 0) _lastClipboardSequence = clipboardSequence;
                    return;
                }

                var payload = await ReadClipboardPayloadAsync(content);
                _captureStage = "payload-ready";

                string hash;
                if (payload.Image is { LongLength: > 0 } image)
                {
                    hash = HashBytes(image);
                }
                else if (payload.Files is { Count: > 0 } files)
                {
                    // 文件条目哈希只基于路径与大小元数据,不对大文件做任何读取
                    hash = ClipboardFileMeta.ComputeHash(files);
                }
                else if (payload.Text is not null)
                {
                    hash = HashText(payload.Text, payload.Html);
                }
                else
                {
                    return;
                }

                if (hash.Length == 0) return;
                // 序列号在确认拿到有效内容之后才记账:若本轮读取失败,下一轮轮询仍会完整重试,不会漏掉变更
                if (clipboardSequence != 0) _lastClipboardSequence = clipboardSequence;
                // 应用自写内容:时间窗内消费一次性抑制,并记录 lastSeen,避免轮询把同一内容再次上传/置顶
                if (hash == _suppressHash && DateTime.UtcNow < _suppressUntil)
                {
                    _suppressHash = "";
                    _suppressUntil = DateTime.MinValue;
                    _lastSeenHash = hash;
                    return;
                }
                if (hash == _lastSeenHash) return;
                _lastSeenHash = hash;

                // 来源应用识别:先在 UI 线程用三次廉价 Win32 调用钉住来源窗口,再把重活移出 UI 线程。
                // 进程查询、PE 版本信息读取(磁盘 IO)、图标提取(GDI+)单次可达数毫秒,
                // 留在 UI 线程会让每一次复制都产生一次可感知的卡顿。
                var sourceApp = await ResolveSourceAppAsync();
                _captureStage = "dispatching";

                if (ClipboardAppFilter.ShouldFilter(sourceApp, _settings.AppFilterEnabled, _settings.CustomFilteredProcesses))
                {
                    Log.Debug($"已忽略来自远程控制应用的剪贴板内容: {sourceApp?.Name ?? sourceApp?.ProcessName}");
                    return;
                }
                await _onCapture(new CapturedClip(payload.Text, payload.Image, hash, sourceApp, payload.Html, payload.Files), ct);
                Interlocked.Exchange(ref _lastCaptureSuccessUtcTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _captureFailureCount);
                Interlocked.Exchange(ref _lastCaptureFailureUtcTicks, DateTime.UtcNow.Ticks);
                Log.Warn($"剪贴板捕获失败(阶段={_captureStage}): {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _captureStartedUtcTicks, 0);
                _captureStage = "idle";
            }
        }
        finally
        {
            Volatile.Write(ref _capturing, 0);
        }
    }

    /// <summary>
    /// 剪贴板可用性探针 + 熔断。返回 false 表示本轮应跳过读取。
    ///
    /// OpenClipboard 在剪贴板被独占时立即失败(ERROR_ACCESS_DENIED),不会阻塞,
    /// 因此它是"剪贴板是否已经卡死"的可靠探针。被持续独占时不再发起任何跨进程读取,
    /// 让本进程保持响应,而不是跟着对方一起挂死。
    /// 注意:这里只是"跳过读取",剪贴板锁在别人手里,本进程无法代为解开。
    /// </summary>
    private bool ProbeClipboardAvailable()
    {
        if (NativeMethods.TryProbeClipboard())
        {
            // 恢复可用:复位熔断状态
            Interlocked.Exchange(ref _clipboardUnavailableSinceTicks, 0);
            if (Interlocked.Exchange(ref _clipboardBlocked, 0) != 0)
            {
                Log.Info("剪贴板已恢复可用,重新开始监听");
            }
            return true;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        var since = Interlocked.Read(ref _clipboardUnavailableSinceTicks);
        if (since == 0)
        {
            // 首次失败:可能只是瞬时争用(其它程序恰好正在写入),先记账不熔断
            Interlocked.CompareExchange(ref _clipboardUnavailableSinceTicks, nowTicks, 0);
            return false;
        }

        var unavailableMs = (nowTicks - since) / TimeSpan.TicksPerMillisecond;
        if (unavailableMs < _clipboardBlockedThresholdMs) return false;

        // 持续不可用 => 判定为被外部独占,熔断并提示(仅在状态翻转时提示一次)
        if (Interlocked.Exchange(ref _clipboardBlocked, 1) == 0)
        {
            Interlocked.Increment(ref _clipboardBlockedCount);
            var owner = SourceAppDetector.TryGetClipboardOwnerProcessName();
            Log.Warn($"剪贴板被持续独占 {unavailableMs:F0}ms,暂停读取(占用者={owner ?? "未知"})");
            ClipboardBlocked?.Invoke(owner);
        }
        // 刻意不记账序列号:熔断期间剪贴板上可能已被放入新内容,恢复后应当照常捕获,
        // 记账会让那份内容被永久跳过。重复探测本身不产生日志,不存在刷屏代价。
        return false;
    }

    /// <summary>
    /// 剪贴板所有者是否为已配置过滤的远程控制程序。仅 Win32 查询 + 路径读取,
    /// 不做 PE 版本解析与图标提取,可安全放在跨进程读取之前。
    /// </summary>
    private bool IsBlockedSource(uint clipboardSequence)
    {
        var ownerProcess = SourceAppDetector.TryGetClipboardOwnerProcessName();
        if (ownerProcess is null) return false;
        if (!ClipboardAppFilter.ShouldSkipByProcessName(ownerProcess, _settings.AppFilterEnabled, _settings.CustomFilteredProcesses))
        {
            return false;
        }
        _captureStage = "filtered-source";
        // 与下方"所有者为本进程"同理:这是明确决定忽略本次剪贴板状态,必须记账序列号,
        // 否则 2 秒轮询会对着同一份远控内容反复查询进程并重复刷日志。
        if (clipboardSequence != 0) _lastClipboardSequence = clipboardSequence;
        Log.Debug($"跳过读取远程控制应用的剪贴板内容: {ownerProcess}");
        return true;
    }

    /// <summary>在后台线程解析剪贴板来源应用详情(见 SourceAppDetector.ResolveSourceApp 的性能说明)。</summary>
    /// <summary>
    /// 看门狗:若捕获链在同一阶段停留超过阈值,说明跨进程剪贴板调用已无法按时返回。
    /// WinRT 调用本身无法取消,因此此处只做检测、记账与状态复位,并通知上层提示用户。
    ///
    /// 该方法运行在后台线程池线程上,不做任何 UI 线程亲和的操作:
    /// 不触碰 DispatcherQueueTimer(DispatcherQueueTimer 的 Start/Stop 需要 UI 线程访问,
    /// 且此刻 UI 线程恰恰可能正被挂住),只用 Interlocked/volatile 复位状态字段。
    /// 这样即使 UI 线程已死,看门狗仍能存活并把真实原因暴露出来。
    /// </summary>
    private void CheckWatchdog()
    {
        try
        {
            if (Volatile.Read(ref _capturing) == 0) return;
            var elapsedMs = CurrentCaptureElapsedMs;
            if (elapsedMs < _watchdogTimeoutSeconds * 1000.0) return;

            Interlocked.Increment(ref _watchdogRecoveryCount);
            var stage = _captureStage;
            var owner = SourceAppDetector.TryGetClipboardOwnerProcessName();
            Log.Warn($"剪贴板看门狗触发: 阶段={stage}, 持续={elapsedMs:F0}ms, " +
                     $"累计失败={_captureFailureCount}, 剪贴板占用者={owner ?? "未知"}");

            // 复位捕获状态,让后续轮询从干净状态开始。跨进程调用仍在进行时无法取消,
            // 因此这里只清标记;若 UI 线程确实已挂起,轮询定时器同样不会触发,不存在并发捕获。
            Interlocked.Exchange(ref _captureStartedUtcTicks, 0);
            Volatile.Write(ref _capturing, 0);
            _captureStage = "idle";

            WatchdogRecovered?.Invoke(_watchdogTimeoutSeconds);
        }
        catch (Exception ex)
        {
            Log.Error("剪贴板看门狗恢复失败", ex);
        }
    }

    private static async Task<SourceAppInfo?> ResolveSourceAppAsync()
    {
        var owner = SourceAppDetector.CaptureOwnerHandle();
        if (owner is null) return null;
        try
        {
            return await Task.Run(() => SourceAppDetector.ResolveSourceApp(owner.Value));
        }
        catch (Exception ex)
        {
            Log.Debug($"解析剪贴板来源应用失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按统一优先级读取剪贴板载荷(content 为已取得的剪贴板视图,全程复用不重复 GetContent)。
    ///
    /// 优先级:位图 &gt; 文本/富文本 &gt; 文件。
    /// 1) 位图优先:截图/设计软件常同时提供 Bitmap + Text/HTML,先读文本会把图片误判成文本条目。
    ///    但 Word/Excel 复制带格式内容时同样"位图 + 纯文本 + HTML"三格式齐备,因此仅当位图与
    ///    "非空纯文本 + 去标签后仍有可见文字的 HTML"同时存在时判为富文本,其余仍按图片处理
    ///    (纯截图没有纯文本;浏览器复制图片虽带 HTML,但去掉 img 标签后为空)。
    /// 2) 文件优先级最低:资源管理器复制文件时,部分来源(如"复制为路径"、某些第三方文件管理器)
    ///    会同时提供文本格式,若先判文件就会把一次文本复制误记为文件条目。
    ///    只有既无位图也无文本时,才把 CF_HDROP 视为文件条目。
    /// </summary>
    private async Task<ClipboardPayload> ReadClipboardPayloadAsync(DataPackageView content)
    {
        var image = await ImageCodec.CaptureClipboardPngAsync(content);
        var hasImage = image is not null && image.LongLength > 0;

        string? text = null;
        string? html = null;
        if (_settings.RichTextEnabled)
        {
            (text, html) = await ImageCodec.ReadClipboardRichTextAsync(content);
        }
        else if (!hasImage)
        {
            // 已判定为位图时无需再读文本(与既有行为一致,避免多一次跨进程读取)
            text = await ImageCodec.ReadClipboardTextAsync(content);
        }

        if (hasImage)
        {
            return text is not null && ImageCodec.HasVisibleHtmlText(html)
                ? new ClipboardPayload(text, html, null, null)
                : new ClipboardPayload(null, null, image, null);
        }
        if (text is not null) return new ClipboardPayload(text, html, null, null);

        var files = await ClipboardFiles.ReadClipboardFilesAsync(content);
        return new ClipboardPayload(null, null, null, files);
    }

    /// <summary>手动捕获(忽略自写抑制,用于"同步当前剪贴板")。返回 hash,空则无内容。</summary>
    public async Task<string> CaptureManualAsync(CancellationToken ct = default)
    {
        // 与自动监听完全共用一套优先级判定,避免两条路径对同一剪贴板得出不同类型。
        // 前置守卫必须与自动路径一致:手动触发同样可能踩到无响应的剪贴板所有者,
        // 否则用户点一次"同步当前剪贴板"就会把进程挂死。
        if (!NativeMethods.TryProbeClipboard())
        {
            var owner = SourceAppDetector.TryGetClipboardOwnerProcessName();
            Log.Warn($"手动捕获跳过:剪贴板当前不可打开(占用者={owner ?? "未知"})");
            return "";
        }
        uint manualSequence = 0;
        try { manualSequence = NativeMethods.GetClipboardSequenceNumber(); } catch { }
        if (IsBlockedSource(manualSequence)) return "";

        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            Log.Debug($"获取剪贴板内容失败: {ex.Message}");
            return "";
        }

        var payload = await ReadClipboardPayloadAsync(content);

        string hash;
        if (payload.Image is { LongLength: > 0 } image)
        {
            hash = HashBytes(image);
        }
        else if (payload.Files is { Count: > 0 } files)
        {
            hash = ClipboardFileMeta.ComputeHash(files);
        }
        else if (payload.Text is not null)
        {
            hash = HashText(payload.Text, payload.Html);
        }
        else
        {
            return "";
        }
        var sourceApp = await ResolveSourceAppAsync();
        await _onCapture(new CapturedClip(payload.Text, payload.Image, hash, sourceApp, payload.Html, payload.Files), ct);
        return hash;
    }

    /// <summary>
    /// 文本内容哈希。html 为空时结果与不带富文本时逐字节一致(保证存量条目哈希不失效);
    /// html 非空时把它一并纳入,使同一段文字的"纯文本版"与"富文本版"成为两条独立记录,
    /// 而不会互相 TouchByHash 置顶把富文本吃掉。
    /// </summary>
    public static string HashText(string text, string? html = null) =>
        HashBytes(Encoding.UTF8.GetBytes(string.IsNullOrEmpty(html) ? text : text + "\u0001" + html));

    public static string HashBytes(byte[] bytes)
    {
        var sha = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(sha);
    }
}

file sealed class ActionDisposable : IDisposable
{
    private Action? _action;
    public ActionDisposable(Action action) => _action = action;
    public void Dispose()
    {
        Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
