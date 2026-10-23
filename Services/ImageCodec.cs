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

    /// <summary>
    /// 剪贴板位图的捕获结果。
    ///
    /// 关键区分:剪贴板上"根本没有位图"与"有位图但编码后超过 10MB"是两件完全不同的事。
    /// 旧实现两种情况都返回 null,调用方只能当成"没有图片",于是超大截图会被静默丢弃——
    /// 既不进本地历史,也不弹任何提示,用户看到的就是"复制了但没反应"。
    /// </summary>
    public enum BitmapCaptureKind
    {
        /// <summary>剪贴板中没有位图格式。</summary>
        None,
        /// <summary>成功编码为 PNG 字节。</summary>
        Success,
        /// <summary>
        /// 存在位图,但按原尺寸编码超过 <see cref="MaxImageBytes"/>。
        /// 此时 <see cref="BitmapCapture.Png"/> 仍非空:里面是自动降采样后的可预览版本,
        /// 供本地历史使用;同步链路必须跳过它(见 <see cref="BitmapCapture.Oversized"/>）。
        /// </summary>
        TooLarge,
    }

    /// <summary>位图捕获结果(不可变)。</summary>
    /// <param name="Kind">捕获结果类型。</param>
    /// <param name="Png">PNG 字节。Success 与 TooLarge 均非空;None 时为 null。</param>
    /// <param name="Size">图片像素尺寸(降采样后)。</param>
    /// <param name="Oversized">原尺寸编码超过同步上限,只能本地使用。</param>
    public readonly record struct BitmapCapture(
        BitmapCaptureKind Kind,
        byte[]? Png,
        (uint Width, uint Height)? Size,
        bool Oversized = false);

    /// <summary>从剪贴板读取位图并编码为 PNG。</summary>
    public static async Task<BitmapCapture> CaptureClipboardBitmapAsync()
    {
        if (!NativeMethods.TryProbeClipboard()) return new BitmapCapture(BitmapCaptureKind.None, null, null);
        var content = Clipboard.GetContent();
        return await CaptureClipboardBitmapAsync(content);
    }

    /// <summary>从已取得的剪贴板视图读取位图并编码为 PNG(复用视图,避免重复 GetContent)。</summary>
    public static async Task<BitmapCapture> CaptureClipboardBitmapAsync(DataPackageView content)
    {
        if (!content.Contains(StandardDataFormats.Bitmap)) return new BitmapCapture(BitmapCaptureKind.None, null, null);
        var streamRef = await content.GetBitmapAsync();
        using var stream = await streamRef.OpenReadAsync();
        return await CompressAndEncodePngAsync(stream);
    }

    /// <summary>兼容入口:只要字节,超出上限时按"无位图"处理(旧调用方语义)。</summary>
    public static async Task<byte[]?> CaptureClipboardPngAsync()
    {
        var capture = await CaptureClipboardBitmapAsync();
        return capture.Kind == BitmapCaptureKind.Success ? capture.Png : null;
    }

    /// <summary>兼容入口(已取得视图)。</summary>
    public static async Task<byte[]?> CaptureClipboardPngAsync(DataPackageView content)
    {
        var capture = await CaptureClipboardBitmapAsync(content);
        return capture.Kind == BitmapCaptureKind.Success ? capture.Png : null;
    }

    /// <summary>
    /// 写文本到系统剪贴板;html 非空时同时写入 HTML 与纯文本,由目标程序按自身能力择取。
    ///
    /// 实际写入由 <see cref="ClipboardWriter"/> 的专用 STA 线程完成。此处刻意**不**在
    /// UI 线程上做任何剪贴板 IO:SetContent/Flush 是跨进程阻塞调用,留在 UI 线程上
    /// 一旦占用者不响应就会把 UI 线程连同全机剪贴板一起拖死。
    /// </summary>
    public static Task<bool> WriteClipboardTextAsync(string text, string? html = null) =>
        ClipboardWriter.WriteTextAsync(text, html);

    /// <summary>写图片到系统剪贴板(先读成字节,再交给专用线程写入)。</summary>
    public static async Task<bool> WriteClipboardImageAsync(string localImagePath)
    {
        try
        {
            var bytes = await ReadImageBytesAsync(localImagePath);
            if (bytes is null || bytes.Length == 0) return false;
            return await ClipboardWriter.WriteImageAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取待复制图片失败: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 读出图片字节供剪贴板使用。
    /// 剪贴板只接受位图流,统一转成 PNG 字节:本应用缓存的图片本身就是 PNG,直接读文件即可;
    /// 其它来源(用户导入的 jpg/bmp 等)交给 WIC 转码,避免把不支持的格式塞进剪贴板。
    /// </summary>
    private static async Task<byte[]?> ReadImageBytesAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllBytesAsync(path);
        }

        var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        using var stream = await file.OpenReadAsync().AsTask().ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream).AsTask().ConfigureAwait(false);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask().ConfigureAwait(false);
        var bytes = new byte[outStream.Size];
        using var reader = new DataReader(outStream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)outStream.Size).AsTask().ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // 说明:这里刻意不再提供"同步重试写剪贴板"的方法。
    //
    // 原先的 SetContentWithRetry 在调用线程上 SetContent + Flush,失败后 Thread.Sleep 退避重试。
    // 它有两个致命问题:一是跑在 UI 线程上,而 SetContent/Flush 是**无超时**的跨进程调用,
    // 占用者不响应时会把 UI 线程连同全机剪贴板一起拖死;二是重试用的 Thread.Sleep 会进一步
    // 占住线程,把一次偶发争用放大成一次可感知的卡顿。
    //
    // 所有剪贴板写入现在统一走 ClipboardWriter:专用 STA 线程执行,调用方只 await 一个
    // 带硬超时的 Task,失败会如实返回 false 由调用方决定要不要提示用户。

    /// <summary>
    /// 超限时降采样重编码尝试的最小长边。
    /// 降到这个尺寸仍然超过上限就放弃(几乎不可能:2048 全噪声约 16MB,1024 约 4MB)。
    /// </summary>
    private const uint MinOversizeLongSide = 512;

    private static async Task<BitmapCapture> CompressAndEncodePngAsync(IRandomAccessStream inStream)
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

        var (bytes, width, height) = await EncodePngAsync(decoder, targetW, targetH);
        if (bytes.LongLength <= MaxImageBytes)
        {
            return new BitmapCapture(BitmapCaptureKind.Success, bytes, (width, height), Oversized: false);
        }

        // 超限:继续降采样,给本地历史留一份能看的预览。
        //
        // 为什么不直接"存不了就算了":一张 4096 噪声图 PNG 可达 60MB+,用户复制后如果列表里
        // 什么都没有,体感就是"工具坏了"。降采样后通常只有几 MB,本地查看/复制缩略图仍然有用,
        // 只是这份内容不参与跨设备同步(见 Oversized 标记)。
        var smallW = targetW;
        var smallH = targetH;
        while (Math.Max(smallW, smallH) > MinOversizeLongSide)
        {
            smallW = (uint)Math.Max(1, Math.Round(smallW * 0.5));
            smallH = (uint)Math.Max(1, Math.Round(smallH * 0.5));
            var (smallBytes, w2, h2) = await EncodePngAsync(decoder, smallW, smallH);
            if (smallBytes.LongLength <= MaxImageBytes)
            {
                Log.Info($"图片超限,已降采样到 {w2}×{h2} 作为本地预览(不同步)");
                return new BitmapCapture(BitmapCaptureKind.TooLarge, smallBytes, (w2, h2), Oversized: true);
            }
        }

        // 连最小可接受尺寸都超限:只回报标记,不留本地副本
        Log.Warn($"图片超过 {MaxImageBytes / 1024 / 1024}MB 上限且降采样后仍超限,仅记录其存在");
        return new BitmapCapture(BitmapCaptureKind.TooLarge, null, (targetW, targetH), Oversized: true);
    }

    /// <summary>把指定位图按给定尺寸编码为 PNG 字节。</summary>
    private static async Task<(byte[] Bytes, uint Width, uint Height)> EncodePngAsync(
        BitmapDecoder decoder, uint width, uint height)
    {
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height };
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
                width, height,
                decoder.DpiX, decoder.DpiY,
                pixelData.DetachPixelData());
            await encoder.FlushAsync();

            var bytes = new byte[outStream.Size];
            using var reader = new DataReader(outStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(bytes);
            return (bytes, width, height);
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
    public static Task<bool> SetClipboardImageAsync(string filePath) => WriteClipboardImageAsync(filePath);

    /// <summary>把文本写入系统剪贴板。html 非空时附带写入 HTML 格式(纯文本始终写入作为兜底)。</summary>
    public static Task<bool> SetClipboardText(string text, string? html = null) =>
        ClipboardWriter.WriteTextAsync(text, html);

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
