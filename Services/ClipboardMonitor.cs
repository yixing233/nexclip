using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 剪贴板监听:ContentChanged 事件 + 150ms 去抖 + 2s 轮询兜底。
/// 捕获回调在 UI 线程执行(剪贴板 API 要求)。自写回内容通过 SuppressHash 抑制(防回环)。
/// </summary>
public sealed class ClipboardMonitor
{
    public readonly record struct CapturedClip(string? Text, byte[]? ImagePng, string Hash, SourceAppInfo? SourceApp = null);

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

        // 彻底杜绝自写回环: 若剪贴板内容由本程序写回(远端同步/历史列表复制),直接忽略
        if (ImageCodec.IsSelfWrittenClipboard() || SourceAppDetector.IsClipboardOwnedByCurrentProcess())
        {
            return;
        }

        _capturing = true;
        try
        {
            // 图片优先:Windows 中截图/设计软件经常同时提供 Bitmap + Text/HTML,
            // 若先读文本会把图片误判成文本条目。只有确认没有位图时才读取文本。
            byte[]? image = await ImageCodec.CaptureClipboardPngAsync();
            string? text = null;
            if (image is null || image.LongLength == 0)
            {
                text = await ImageCodec.ReadClipboardTextAsync();
            }

        string hash;
        if (image is not null && image.LongLength > 0)
        {
            hash = HashBytes(image);
        }
        else if (text is not null)
        {
            hash = HashText(text);
        }
        else
        {
            return;
        }

        if (hash.Length == 0) return;
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
        await _onCapture(new CapturedClip(text, image, hash, sourceApp), ct);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>手动捕获(忽略自写抑制,用于"同步当前剪贴板")。返回 hash,空则无内容。</summary>
    public async Task<string> CaptureManualAsync(CancellationToken ct = default)
    {
        // 与自动监听保持一致:位图优先，避免混合格式被当成文本同步。
        byte[]? image = await ImageCodec.CaptureClipboardPngAsync();
        string? text = null;
        if (image is null || image.LongLength == 0)
        {
            text = await ImageCodec.ReadClipboardTextAsync();
        }

        string hash;
        if (image is not null && image.LongLength > 0)
        {
            hash = HashBytes(image);
        }
        else if (text is not null)
        {
            hash = HashText(text);
        }
        else
        {
            return "";
        }
        var sourceApp = SourceAppDetector.DetectSourceApp();
        await _onCapture(new CapturedClip(text, image, hash, sourceApp), ct);
        return hash;
    }

    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

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
