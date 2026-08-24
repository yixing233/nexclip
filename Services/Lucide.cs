using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NexClip.Desktop.Services;

/// <summary>
/// lucide 图标加载(官方 SVG)。
/// SvgImageSource 无 Foreground,加载时把 stroke="currentColor" 替换为具体颜色并写入缓存文件;
/// 颜色取当前主题资源(启动时主题已确定),深/浅主题各得其所。
/// </summary>
public static class Lucide
{
    private static readonly string BaseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lucide");
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "NexClipIcons");

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
    private static ImageSource? _pin;
    private static ImageSource? _pinOff;
    private static ImageSource? _pinOffActive;
    private static ImageSource? _monitorSmartphone;
    private static ImageSource? _database;
    private static ImageSource? _palette;
    private static ImageSource? _info;
    private static ImageSource? _panelLeftClose;
    private static ImageSource? _panelLeftOpen;
    private static ImageSource? _download;
    private static ImageSource? _upload;
    private static ImageSource? _folderOpen;
    private static ImageSource? _clock;
    private static ImageSource? _hardDrive;
    private static ImageSource? _server;
    private static ImageSource? _keyboard;
    private static ImageSource? _arrowUp;
    private static ImageSource? _zoomInWhite;
    private static ImageSource? _zoomOutWhite;
    private static ImageSource? _rotateCwWhite;
    private static ImageSource? _maximize2White;
    private static ImageSource? _externalLinkWhite;
    private static ImageSource? _xWhite;
    private static ImageSource? _saveWhite;
    private static ImageSource? _copyWhite;
    private static ImageSource? _imageMuted;
    private static ImageSource? _edit;
    private static ImageSource? _externalLink;
    private static ImageSource? _save;
    private static ImageSource? _zoomIn;
    private static ImageSource? _smartphone;
    private static ImageSource? _laptop;
    private static ImageSource? _monitor;
    private static ImageSource? _rotateCw;

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
    public static ImageSource RotateCw => _rotateCw ??= Load("rotate-cw", "TextFillColorSecondaryBrush");
    public static ImageSource Pin => _pin ??= Load("pin", "TextFillColorPrimaryBrush");
    public static ImageSource PinOff => _pinOff ??= Load("pin-off", "TextFillColorSecondaryBrush");
    /// <summary>置顶激活态:白色图标,用于蓝色胶囊按钮上。</summary>
    public static ImageSource PinOffActive => _pinOffActive ??= Load("pin-off", "#FFFFFF");
    public static ImageSource MonitorSmartphone => _monitorSmartphone ??= Load("monitor-smartphone", "TextFillColorPrimaryBrush");
    public static ImageSource Smartphone => _smartphone ??= Load("smartphone", "TextFillColorPrimaryBrush");
    public static ImageSource Laptop => _laptop ??= Load("laptop", "TextFillColorPrimaryBrush");
    public static ImageSource Monitor => _monitor ??= Load("monitor", "TextFillColorPrimaryBrush");
    public static ImageSource Database => _database ??= Load("database", "TextFillColorPrimaryBrush");
    public static ImageSource Palette => _palette ??= Load("palette", "TextFillColorPrimaryBrush");
    public static ImageSource Info => _info ??= Load("info", "TextFillColorPrimaryBrush");
    public static ImageSource PanelLeftClose => _panelLeftClose ??= Load("panel-left-close", "TextFillColorSecondaryBrush");
    public static ImageSource PanelLeftOpen => _panelLeftOpen ??= Load("panel-left-open", "TextFillColorSecondaryBrush");
    public static ImageSource Download => _download ??= Load("download", "TextFillColorSecondaryBrush");
    public static ImageSource Upload => _upload ??= Load("upload", "TextFillColorSecondaryBrush");
    public static ImageSource FolderOpen => _folderOpen ??= Load("folder-open", "TextFillColorSecondaryBrush");
    public static ImageSource Clock => _clock ??= Load("clock", "TextFillColorSecondaryBrush");
    public static ImageSource HardDrive => _hardDrive ??= Load("hard-drive", "TextFillColorSecondaryBrush");
    public static ImageSource Server => _server ??= Load("server", "TextFillColorPrimaryBrush");
    public static ImageSource Keyboard => _keyboard ??= Load("keyboard", "TextFillColorPrimaryBrush");
    public static ImageSource ArrowUp => _arrowUp ??= Load("arrow-up", "#FFFFFF");

    // ---- 大图查看器 HUD 与右键菜单专属 Lucide 图标 ----
    public static ImageSource ZoomInWhite => _zoomInWhite ??= Load("zoom-in", "#F8FAFC");
    public static ImageSource ZoomOutWhite => _zoomOutWhite ??= Load("zoom-out", "#F8FAFC");
    public static ImageSource RotateCwWhite => _rotateCwWhite ??= Load("rotate-cw", "#F8FAFC");
    public static ImageSource Maximize2White => _maximize2White ??= Load("maximize-2", "#F8FAFC");
    public static ImageSource ExternalLinkWhite => _externalLinkWhite ??= Load("external-link", "#F8FAFC");
    public static ImageSource XWhite => _xWhite ??= Load("x", "#F8FAFC");
    public static ImageSource SaveWhite => _saveWhite ??= Load("save", "#F8FAFC");
    public static ImageSource CopyWhite => _copyWhite ??= Load("copy", "#F8FAFC");
    public static ImageSource ImageMuted => _imageMuted ??= Load("image", "#94A3B8");

    public static ImageSource Edit => _edit ??= Load("edit", "TextFillColorPrimaryBrush");
    public static ImageSource ExternalLink => _externalLink ??= Load("external-link", "TextFillColorPrimaryBrush");
    public static ImageSource Save => _save ??= Load("save", "TextFillColorPrimaryBrush");
    public static ImageSource ZoomIn => _zoomIn ??= Load("zoom-in", "TextFillColorPrimaryBrush");

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
