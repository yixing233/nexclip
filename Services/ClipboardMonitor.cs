using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace NexClip.Desktop.Services;

/// <summary>
/// 剪贴板监听:ContentChanged 事件 + 150ms 去抖 + 2s 轮询兜底。
/// 捕获回调在 UI 线程执行(剪贴板 API 要求)。自写回内容通过 SuppressHash 抑制(防回环)。
/// </summary>
public sealed class ClipboardMonitor
{
    public readonly record struct CapturedClip(string? Text, byte[]? ImagePng, string Hash, SourceAppInfo? SourceApp = null, string? Html = null);

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
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
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

        // 彻底杜绝自写回环: 若剪贴板内容由本程序写回(远端同步/历史列表复制),直接忽略
        if (ImageCodec.IsSelfWrittenClipboard() || SourceAppDetector.IsClipboardOwnedByCurrentProcess())
        {
            // 这是"明确决定忽略本次剪贴板状态"而非"读取失败需要重试",必须记账序列号:
            // 否则剪贴板长期停在自写内容上时,2 秒轮询会无限期反复执行昂贵的 Clipboard.GetContent()
            if (clipboardSequence != 0) _lastClipboardSequence = clipboardSequence;
            return;
        }

        _capturing = true;
        try
        {
            var (text, html, image) = await ReadClipboardPayloadAsync();

        string hash;
        if (image is not null && image.LongLength > 0)
        {
            hash = HashBytes(image);
        }
        else if (text is not null)
        {
            hash = HashText(text, html);
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
        var sourceApp = SourceAppDetector.DetectSourceApp();
        if (ClipboardAppFilter.ShouldFilter(sourceApp, _settings.AppFilterEnabled, _settings.CustomFilteredProcesses))
        {
            Log.Debug($"已忽略来自远程控制应用的剪贴板内容: {sourceApp?.Name ?? sourceApp?.ProcessName}");
            return;
        }
        await _onCapture(new CapturedClip(text, image, hash, sourceApp, html), ct);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>
    /// 按统一优先级读取剪贴板载荷。
    /// 位图优先:截图/设计软件常同时提供 Bitmap + Text/HTML,先读文本会把图片误判成文本条目。
    /// 但 Word/Excel 复制带格式内容时同样"位图 + 纯文本 + HTML"三格式齐备,因此仅当位图与
    /// "非空纯文本 + 去标签后仍有可见文字的 HTML"同时存在时判为富文本,其余仍按图片处理
    /// (纯截图没有纯文本;浏览器复制图片虽带 HTML,但去掉 img 标签后为空)。
    /// </summary>
    private async Task<(string? Text, string? Html, byte[]? Image)> ReadClipboardPayloadAsync()
    {
        var image = await ImageCodec.CaptureClipboardPngAsync();
        var hasImage = image is not null && image.LongLength > 0;

        if (!_settings.RichTextEnabled)
        {
            return hasImage ? (null, null, image) : (await ImageCodec.ReadClipboardTextAsync(), null, null);
        }

        var (text, html) = await ImageCodec.ReadClipboardRichTextAsync();
        if (hasImage)
        {
            return text is not null && ImageCodec.HasVisibleHtmlText(html)
                ? (text, html, null)
                : (null, null, image);
        }
        return (text, html, null);
    }

    /// <summary>手动捕获(忽略自写抑制,用于"同步当前剪贴板")。返回 hash,空则无内容。</summary>
    public async Task<string> CaptureManualAsync(CancellationToken ct = default)
    {
        // 与自动监听完全共用一套优先级判定,避免两条路径对同一剪贴板得出不同类型。
        var (text, html, image) = await ReadClipboardPayloadAsync();

        string hash;
        if (image is not null && image.LongLength > 0)
        {
            hash = HashBytes(image);
        }
        else if (text is not null)
        {
            hash = HashText(text, html);
        }
        else
        {
            return "";
        }
        var sourceApp = SourceAppDetector.DetectSourceApp();
        await _onCapture(new CapturedClip(text, image, hash, sourceApp, html), ct);
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
