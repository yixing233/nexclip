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

    /// <summary>从剪贴板读取位图并编码为 PNG 字节;无位图返回 null;超 10MB 返回 null(调用方提示)。</summary>
    public static async Task<byte[]?> CaptureClipboardPngAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return null;
        var streamRef = await content.GetBitmapAsync();
        using var stream = await streamRef.OpenReadAsync();
        return await CompressAndEncodePngAsync(stream);
    }

    /// <summary>写文本到系统剪贴板;标有 SelfOriginProperty 避免循环触发同步。</summary>
    public static void WriteClipboardText(string text)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
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

    /// <summary>把本地图片文件写入系统剪贴板。</summary>
    public static async Task SetClipboardImageAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var package = new DataPackage();
        package.Properties.Add(SelfOriginProperty, true);
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
    }

    /// <summary>把文本写入系统剪贴板。</summary>
    public static void SetClipboardText(string text)
    {
        var package = new DataPackage();
        package.Properties.Add(SelfOriginProperty, true);
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
