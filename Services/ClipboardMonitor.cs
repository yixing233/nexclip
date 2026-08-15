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
    public readonly record struct CapturedClip(string? Text, byte[]? ImagePng, string Hash);

    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsStore _settings;
    private readonly Func<CapturedClip, CancellationToken, Task> _onCapture;
    private readonly DispatcherQueueTimer? _pollTimer;
    private CancellationTokenSource? _debounceCts;
    private bool _capturing;
    private string _lastSeenHash = "";
    private string _suppressHash = "";
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

    /// <summary>记录"由本端写入"的内容 hash,监听回调时忽略(防回环)。</summary>
    public void SuppressNext(string hash) => _suppressHash = hash;

    private void OnContentChanged(object? sender, object e)
    {
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
        _capturing = true;
        try
        {

        // 文本优先(轮询时成本低);文本不存在再尝试图片
        var text = await ImageCodec.ReadClipboardTextAsync();
        string hash;
        byte[]? image = null;
        if (text is not null)
        {
            hash = HashText(text);
        }
        else
        {
            image = await ImageCodec.CaptureClipboardPngAsync();
            if (image is null || image.LongLength == 0) return;
            hash = HashBytes(image);
        }

        if (hash.Length == 0 || hash == _suppressHash || hash == _lastSeenHash) return;
        _lastSeenHash = hash;
        await _onCapture(new CapturedClip(text, image, hash), ct);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>手动捕获(忽略自写抑制,用于"同步当前剪贴板")。返回 hash,空则无内容。</summary>
    public async Task<string> CaptureManualAsync(CancellationToken ct = default)
    {
        var text = await ImageCodec.ReadClipboardTextAsync();
        byte[]? image = null;
        string hash;
        if (text is not null)
        {
            hash = HashText(text);
        }
        else
        {
            image = await ImageCodec.CaptureClipboardPngAsync();
            if (image is null || image.LongLength == 0) return "";
            hash = HashBytes(image);
        }
        await _onCapture(new CapturedClip(text, image, hash), ct);
        return hash;
    }

    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    public static string HashBytes(byte[] bytes)
    {
        var sha = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(sha);
    }
}
