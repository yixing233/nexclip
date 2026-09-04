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
        var content = Clipboard.GetContent();
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
        var content = Clipboard.GetContent();
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
        var content = Clipboard.GetContent();
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
        Clipboard.SetContent(pkg);
        Clipboard.Flush();
    }

    /// <summary>写图片到系统剪贴板;标有 SelfOriginProperty 避免循环触发同步。</summary>
    public static void WriteClipboardImage(string localImagePath)
    {
        var file = StorageFile.GetFileFromPathAsync(localImagePath).AsTask().GetAwaiter().GetResult();
        var pkg = new DataPackage();
        pkg.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        pkg.Properties[SelfOriginProperty] = "1";
        Clipboard.SetContent(pkg);
        Clipboard.Flush();
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

    /// <summary>检查当前剪贴板是否由本应用自身写回(远端同步/历史列表复制)。</summary>
    public static bool IsSelfWrittenClipboard()
    {
        try
        {
            var content = Clipboard.GetContent();
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
        Clipboard.SetContent(package);
    }

    /// <summary>把文本写入系统剪贴板。html 非空时附带写入 HTML 格式(纯文本始终写入作为兜底)。</summary>
    public static void SetClipboardText(string text, string? html = null)
    {
        var package = new DataPackage();
        package.Properties.Add(SelfOriginProperty, true);
        package.SetText(text);
        TrySetHtml(package, html);
        Clipboard.SetContent(package);
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
