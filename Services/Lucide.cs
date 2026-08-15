using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// lucide 图标加载(官方 SVG)。
/// SvgImageSource 无 Foreground,加载时把 stroke="currentColor" 替换为具体颜色并写入缓存文件;
/// 颜色取当前主题资源(启动时主题已确定),深/浅主题各得其所。
/// </summary>
public static class Lucide
{
    private static readonly string BaseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lucide");
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "SyncClipboardIcons");

    private static ImageSource? _clipboard;
    private static ImageSource? _copy;
    private static ImageSource? _star;
    private static ImageSource? _starActive;
    private static ImageSource? _trash;
    private static ImageSource? _fileText;
    private static ImageSource? _image;
    private static ImageSource? _settings;
    private static ImageSource? _search;
    private static ImageSource? _refreshCw;

    public static ImageSource Clipboard => _clipboard ??= Load("clipboard", "TextFillColorPrimaryBrush");
    public static ImageSource Copy => _copy ??= Load("copy", "TextFillColorSecondaryBrush");
    public static ImageSource Star => _star ??= Load("star", "TextFillColorSecondaryBrush");
    public static ImageSource StarActive => _starActive ??= Load("star", "#F59E0B");
    public static ImageSource Trash => _trash ??= Load("trash-2", "TextFillColorSecondaryBrush");
    public static ImageSource FileText => _fileText ??= Load("file-text", "TextFillColorSecondaryBrush");
    public static ImageSource Image => _image ??= Load("image", "TextFillColorSecondaryBrush");
    public static ImageSource Settings => _settings ??= Load("settings", "TextFillColorPrimaryBrush");
    public static ImageSource Search => _search ??= Load("search", "TextFillColorSecondaryBrush");
    public static ImageSource RefreshCw => _refreshCw ??= Load("refresh-cw", "TextFillColorPrimaryBrush");

    private static SvgImageSource Load(string name, string color)
    {
        if (!color.StartsWith('#'))
        {
            var brush = Application.Current.Resources[color] as SolidColorBrush;
            color = brush is null
                ? "#4B5563"
                : $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        }
        try
        {
            Directory.CreateDirectory(CacheDir);
            var target = Path.Combine(CacheDir, $"{name}-{color.TrimStart('#')}.svg");
            if (!File.Exists(target))
            {
                var content = File.ReadAllText(Path.Combine(BaseDir, name + ".svg"));
                content = content.Replace("stroke=\"currentColor\"", $"stroke=\"{color}\"");
                File.WriteAllText(target, content);
            }
            return new SvgImageSource(new Uri("file:///" + target.Replace('\\', '/')));
        }
        catch (Exception ex)
        {
            Log.Warn($"图标加载失败:{name} - {ex.Message}");
            return new SvgImageSource();
        }
    }
}
