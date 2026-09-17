using System.Buffers.Binary;
using System.Net;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NexClip.Desktop.Services;

/// <summary>
/// 剪贴板图片编解码(WIC)。所有方法须在 UI 线程调用(剪贴板 API 要求)。
/// 编码为 PNG;最长边超 4096 自动缩放(服务器上限 10MB)。
/// </summary>
public static class ImageCodec
{
    public const uint MaxLongSide = 4096;
    public const long MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>富文本 HTML 片段长度上限(256K 字符);超限丢弃 HTML 只保留纯文本。</summary>
    public const int MaxHtmlChars = 256 * 1024;

    private static string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexClip", "images");

    /// <summary>图片缓存目录(随数据储存目录初始化)。</summary>
    public static string CacheDir => _cacheDir;

    /// <summary>按数据储存目录初始化图片缓存位置(应用启动时调用)。</summary>
    public static void Initialize(string storageDir) =>
        _cacheDir = Path.Combine(storageDir, "images");

    /// <summary>读取剪贴板文本;无文本返回 null。</summary>
    public static async Task<string?> ReadClipboardTextAsync()
    {
        // 无参入口自行打开剪贴板:所有者无响应时跨进程读取无法取消,先探针确认可安全打开。
        if (!NativeMethods.TryProbeClipboard()) return null;
        var content = Clipboard.GetContent();
        return await ReadClipboardTextAsync(content);
    }

    /// <summary>
    /// 从已取得的剪贴板视图读取文本。
    /// Clipboard.GetContent() 是跨进程 OLE 调用,剪贴板所有者无响应时可能阻塞数百毫秒;
    /// 一次捕获只应调用一次,后续所有读取复用同一个视图。
    /// </summary>
    public static async Task<string?> ReadClipboardTextAsync(DataPackageView content)
    {
        if (!content.Contains(StandardDataFormats.Text)) return null;
        var text = await content.GetTextAsync();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// 一次性读取剪贴板的纯文本与富文本(HTML)。纯文本为空时整体返回 (null, null)——
    /// 纯文本始终是必需的兜底内容,不存在"只有 HTML 没有文本"的条目。
    /// HTML 存储为片段形式(剥掉 CF_HTML 的 Version/StartHTML 头),便于跨端传输与重新封装。
    /// </summary>
    public static async Task<(string? Text, string? Html)> ReadClipboardRichTextAsync()
    {
        if (!NativeMethods.TryProbeClipboard()) return (null, null);
        var content = Clipboard.GetContent();
        return await ReadClipboardRichTextAsync(content);
    }

    /// <summary>从已取得的剪贴板视图读取纯文本与富文本(复用视图,避免重复 GetContent)。</summary>
    public static async Task<(string? Text, string? Html)> ReadClipboardRichTextAsync(DataPackageView content)
    {
        if (!content.Contains(StandardDataFormats.Text)) return (null, null);
        var text = await content.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return (null, null);
        return (text, await ReadHtmlFragmentAsync(content));
    }

    /// <summary>
    /// 从剪贴板视图取出 HTML 片段。GetStaticFragment 会剥掉 CF_HTML 头并移除脚本,
    /// 失败(格式损坏/超长/宿主拒绝)时一律返回 null,由调用方降级为纯文本。
    /// </summary>
    public static async Task<string?> ReadHtmlFragmentAsync(DataPackageView content)
    {
        if (!content.Contains(StandardDataFormats.Html)) return null;
        try
        {
            var htmlFormat = await content.GetHtmlFormatAsync();
            if (string.IsNullOrEmpty(htmlFormat)) return null;
            var fragment = HtmlFormatHelper.GetStaticFragment(htmlFormat);
            if (string.IsNullOrWhiteSpace(fragment)) return null;
            if (fragment.Length > MaxHtmlChars)
            {
                Log.Warn($"剪贴板 HTML 超出上限({fragment.Length} > {MaxHtmlChars}),降级为纯文本");
                return null;
            }
            return fragment;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取剪贴板 HTML 失败,降级为纯文本: {ex.Message}");
            return null;
        }
    }

    /// <summary>HTML 片段去标签后是否仍有可见文字(用于区分"真富文本"与仅包一层 img 的图片复制)。</summary>
    public static bool HasVisibleHtmlText(string? htmlFragment)
    {
        if (string.IsNullOrWhiteSpace(htmlFragment)) return false;
        var stripped = HtmlTagRegex.Replace(htmlFragment, " ");
        return !string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(stripped));
    }

    private static readonly Regex HtmlTagRegex = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>从剪贴板读取位图并编码为 PNG 字节;无位图返回 null;超 10MB 返回 null(调用方提示)。</summary>
    public static async Task<byte[]?> CaptureClipboardPngAsync()
    {
        if (!NativeMethods.TryProbeClipboard()) return null;
        var content = Clipboard.GetContent();
        return await CaptureClipboardPngAsync(content);
    }

    /// <summary>从已取得的剪贴板视图读取位图并编码为 PNG(复用视图,避免重复 GetContent)。</summary>
    public static async Task<byte[]?> CaptureClipboardPngAsync(DataPackageView content)
    {
        if (!content.Contains(StandardDataFormats.Bitmap)) return null;
        var streamRef = await content.GetBitmapAsync();
        using var stream = await streamRef.OpenReadAsync();
        return await CompressAndEncodePngAsync(stream);
    }

    /// <summary>
    /// 写文本到系统剪贴板;标有 SelfOriginProperty 避免循环触发同步。
    /// html 非空时同时写入 HTML 与纯文本两种格式,由目标程序按自身能力择取。
    /// </summary>
    public static void WriteClipboardText(string text, string? html = null)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
        TrySetHtml(pkg, html);
        pkg.Properties[SelfOriginProperty] = "1";
        SetContentWithRetry(pkg, flush: true);
    }

    /// <summary>写图片到系统剪贴板;标有 SelfOriginProperty 避免循环触发同步。</summary>
    public static async Task WriteClipboardImageAsync(string localImagePath)
    {
        // 原实现用 StorageFile.GetFileFromPathAsync(...).AsTask().GetAwaiter().GetResult()
        // 在 UI 线程上同步等待异步 WinRT 调用:既会阻塞 UI 线程做磁盘 IO,也存在经典
        // sync-over-async 死锁风险。改为正常 await。
        var file = await StorageFile.GetFileFromPathAsync(localImagePath);
        var pkg = new DataPackage();
        pkg.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        pkg.Properties[SelfOriginProperty] = "1";
        SetContentWithRetry(pkg, flush: true);
    }

    /// <summary>写剪贴板的最大尝试次数与递增退避基数(毫秒)。</summary>
    private const int ClipboardWriteAttempts = 5;
    private const int ClipboardWriteBackoffMs = 20;

    /// <summary>
    /// 带退避重试的剪贴板写入。
    /// 剪贴板是全局单占资源:另一个进程(远程控制软件、其它剪贴板管理器、Office 等)
    /// 短暂持有它时,单次 SetContent 会直接失败并抛出消息为空的 COM 异常(CLIPBRD_E_CANT_OPEN 一类),
    /// 用户看到的现象就是"复制/同步没反应、内容没进剪贴板"。
    /// 主流剪贴板管理器(如 Ditto)的通行做法是短暂退避后重试,而不是把这次复制丢掉。
    /// 最坏情况下总退避约 200ms,仅在真正发生争用时才会付出。
    /// 本方法对同程序集开放:文件条目写回(ClipboardFiles)复用同一套退避策略,
    /// 避免两处各写一份重试逻辑而在后续维护中产生行为差异。
    /// </summary>
    internal static void SetContentWithRetry(DataPackage package, bool flush)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                if (flush)
                {
                    // Flush 让内容在本进程退出后仍然可用;失败只影响"退出后是否留存",不影响本次写入
                    try
                    {
                        Clipboard.Flush();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"剪贴板 Flush 失败(内容已写入,本次复制不受影响): {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                    }
                }
                return;
            }
            catch (Exception ex) when (attempt < ClipboardWriteAttempts - 1)
            {
                Log.Debug($"写剪贴板失败,退避后重试(第 {attempt + 1} 次): {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                System.Threading.Thread.Sleep(ClipboardWriteBackoffMs * (attempt + 1));
            }
        }
    }

    private static async Task<byte[]?> CompressAndEncodePngAsync(IRandomAccessStream inStream)
    {
        var decoder = await BitmapDecoder.CreateAsync(inStream);
        var origW = decoder.PixelWidth;
        var origH = decoder.PixelHeight;

        // 等比缩放到最长边 <= 4096
        var scale = 1.0;
        var maxSide = Math.Max(origW, origH);
        if (maxSide > MaxLongSide) scale = (double)MaxLongSide / maxSide;
        var targetW = (uint)Math.Max(1, Math.Round(origW * scale));
        var targetH = (uint)Math.Max(1, Math.Round(origH * scale));

        var transform = new BitmapTransform { ScaledWidth = targetW, ScaledHeight = targetH };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        var outStream = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                targetW, targetH,
                decoder.DpiX, decoder.DpiY,
                pixelData.DetachPixelData());
            await encoder.FlushAsync();
            if (outStream.Size > MaxImageBytes) return null;

            var bytes = new byte[outStream.Size];
            using var reader = new DataReader(outStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            outStream.Dispose();
        }
    }

    public const string SelfOriginProperty = "NexClip_Self";

    /// <summary>基于已取得的剪贴板视图判断是否由本应用写回(复用视图,避免重复 GetContent)。</summary>
    public static bool IsSelfWrittenClipboard(DataPackageView content)
    {
        try
        {
            return content.Properties.ContainsKey(SelfOriginProperty);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>保存 PNG 字节到本地缓存,返回文件路径。</summary>
    public static async Task<string> SavePngAsync(byte[] pngBytes, long entryId)
    {
        Directory.CreateDirectory(CacheDir);
        var dayDir = Path.Combine(CacheDir, DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dayDir);
        var file = Path.Combine(dayDir, $"{entryId}.png");
        await File.WriteAllBytesAsync(file, pngBytes);
        return file;
    }

    /// <summary>PNG 文件签名(固定 8 字节)。</summary>
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>只读取 PNG 文件头(IHDR)获取像素尺寸,不解码像素数据。失败返回 null。</summary>
    public static (int Width, int Height)? TryReadPngSize(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            // PNG 头部布局固定: 0..7 签名, 8..11 IHDR 块长度, 12..15 块类型 "IHDR",
            // 16..19 宽度, 20..23 高度(宽高均为大端 4 字节无符号整数),因此只需读前 24 字节。
            var header = new byte[24];
            // 共享读写与删除,避免与正在读取同一缓存文件的解码器互相占用
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var total = 0;
            while (total < header.Length)
            {
                var read = fs.Read(header, total, header.Length - total);
                if (read <= 0) break;
                total += read;
            }
            if (total < header.Length) return null;

            // 签名不符说明不是 PNG,交给调用方走兜底逻辑
            for (var i = 0; i < PngSignature.Length; i++)
            {
                if (header[i] != PngSignature[i]) return null;
            }

            var width = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
            var height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            if (width <= 0 || height <= 0) return null;
            return (width, height);
        }
        catch
        {
            // 文件缺失/被占用/头部损坏等一律按未知尺寸处理
            return null;
        }
    }

    /// <summary>把本地图片文件写入系统剪贴板。</summary>
    public static async Task SetClipboardImageAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var package = new DataPackage();
        package.Properties.Add(SelfOriginProperty, true);
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        SetContentWithRetry(package, flush: false);
    }

    /// <summary>把文本写入系统剪贴板。html 非空时附带写入 HTML 格式(纯文本始终写入作为兜底)。</summary>
    public static void SetClipboardText(string text, string? html = null)
    {
        var package = new DataPackage();
        package.Properties.Add(SelfOriginProperty, true);
        package.SetText(text);
        TrySetHtml(package, html);
        SetContentWithRetry(package, flush: false);
    }

    /// <summary>
    /// 给 DataPackage 附加 HTML 格式。存储的是片段,写回前需用 CreateHtmlFormat 重新生成 CF_HTML 头。
    /// 任何失败都只影响富文本,已写入的纯文本不受影响。
    /// </summary>
    private static void TrySetHtml(DataPackage package, string? htmlFragment)
    {
        if (string.IsNullOrWhiteSpace(htmlFragment)) return;
        try
        {
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(htmlFragment));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入剪贴板 HTML 失败,仅保留纯文本: {ex.Message}");
        }
    }
}
