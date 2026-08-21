using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SyncClipboard.Desktop.Services;

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
        "SyncClipboard", "images");

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
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var w = decoder.PixelWidth;
        var h = decoder.PixelHeight;
        var longest = Math.Max(w, h);
        uint sw = w, sh = h;
        if (longest > MaxLongSide)
        {
            var scale = (double)MaxLongSide / longest;
            sw = (uint)Math.Max(1, w * scale);
            sh = (uint)Math.Max(1, h * scale);
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        var outStream = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                sw, sh, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();

            using var ms = new MemoryStream();
            await outStream.AsStreamForRead().CopyToAsync(ms);
            return ms.ToArray();
        }
        finally
        {
            outStream.Dispose();
        }
    }

    public const string SelfOriginProperty = "SyncClipboard_Self";

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
