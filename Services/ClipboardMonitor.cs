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
    private bool _capturing;
    private string _lastSeenHash = "";
    private string _suppressHash = "";
    private DateTime _suppressUntil = DateTime.MinValue;
    private int _pauseCount;
    private DateTime _pauseUntil = DateTime.MinValue;
    private bool _started;
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
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Clipboard.ContentChanged += OnContentChanged;
        _pollTimer?.Start();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        Clipboard.ContentChanged -= OnContentChanged;
        _pollTimer?.Stop();
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
        if (_capturing) return;
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

        _capturing = true;
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

            if (ClipboardAppFilter.ShouldFilter(sourceApp, _settings.AppFilterEnabled, _settings.CustomFilteredProcesses))
            {
                Log.Debug($"已忽略来自远程控制应用的剪贴板内容: {sourceApp?.Name ?? sourceApp?.ProcessName}");
                return;
            }
            await _onCapture(new CapturedClip(payload.Text, payload.Image, hash, sourceApp, payload.Html, payload.Files), ct);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>在后台线程解析剪贴板来源应用详情(见 SourceAppDetector.ResolveSourceApp 的性能说明)。</summary>
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
