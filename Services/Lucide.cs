using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NexClip.Desktop.Services;

/// <summary>
/// lucide 矢量图标动态加载器(官方 SVG)。
/// 自动根据系统与应用深浅色主题动态计算高对比度着色并生成 SVG 缓存。
/// </summary>
public static class Lucide
{
    private static readonly string BaseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lucide");
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "NexClipIcons");
    private static readonly ConcurrentDictionary<string, SvgImageSource> Cache = new();

    /// <summary>当前是否处于深色主题。</summary>
    public static bool IsDarkTheme
    {
        get
        {
            try
            {
                var themeMode = App.Services?.Settings?.ThemeMode;
                if (string.Equals(themeMode, "dark", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(themeMode, "light", StringComparison.OrdinalIgnoreCase)) return false;

                // 检查已打开窗口的实际主题
                if (App.ClipboardWindow?.Content is FrameworkElement root && root.ActualTheme != ElementTheme.Default)
                {
                    return root.ActualTheme == ElementTheme.Dark;
                }

                // 通过系统颜色准确探测系统是否为深色模式
                var uiSettings = new Windows.UI.ViewManagement.UISettings();
                var color = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                return color.R < 128;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>解析逻辑颜色角色对应的十六进制颜色值。</summary>
    public static string ResolveColorHex(string colorOrRole)
    {
        if (colorOrRole.StartsWith('#')) return colorOrRole;
        bool isDark = IsDarkTheme;
        return colorOrRole switch
        {
            "primary" or "TextFillColorPrimaryBrush" => isDark ? "#F8FAFC" : "#0F172A",
            "secondary" or "TextFillColorSecondaryBrush" => isDark ? "#CBD5E1" : "#475569",
            "tertiary" or "TextFillColorTertiaryBrush" => isDark ? "#94A3B8" : "#94A3B8",
            "amber" => isDark ? "#FBBF24" : "#D97706",
            "blue" => isDark ? "#60A5FA" : "#2563EB",
            "white" => "#FFFFFF",
            _ => isDark ? "#F8FAFC" : "#0F172A"
        };
    }

    public static ImageSource Get(string name, string colorOrRole = "primary")
    {
        var hex = ResolveColorHex(colorOrRole);
        var key = $"{name}_{hex.TrimStart('#')}";
        return Cache.GetOrAdd(key, _ => LoadSvg(name, hex));
    }

    private static SvgImageSource LoadSvg(string name, string hexColor)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var cleanHex = hexColor.TrimStart('#');
            var target = Path.Combine(CacheDir, $"{name}-{cleanHex}.svg");
            if (!File.Exists(target))
            {
                var sourcePath = Path.Combine(BaseDir, $"{name}.svg");
                if (File.Exists(sourcePath))
                {
                    var content = File.ReadAllText(sourcePath);
                    content = content.Replace("stroke=\"currentColor\"", $"stroke=\"#{cleanHex}\"");
                    content = content.Replace("fill=\"currentColor\"", $"fill=\"#{cleanHex}\"");
                    File.WriteAllText(target, content);
                }
                else
                {
                    Log.Warn($"图标源文件不存在: {sourcePath}");
                    return new SvgImageSource();
                }
            }
            return new SvgImageSource(new Uri("file:///" + target.Replace('\\', '/')));
        }
        catch (Exception ex)
        {
            Log.Warn($"图标加载失败: {name} ({hexColor}) - {ex.Message}");
            return new SvgImageSource();
        }
    }

    public static ImageSource Clipboard => Get("clipboard", "primary");
    public static ImageSource Copy => Get("copy", "secondary");
    public static ImageSource Star => Get("star", "secondary");
    public static ImageSource StarActive => Get("star-filled", "amber");
    public static ImageSource Trash => Get("trash-2", "secondary");
    public static ImageSource FileText => Get("file-text", "secondary");
    public static ImageSource RichText => Get("type", "secondary");
    public static ImageSource Image => Get("image", "secondary");
    public static ImageSource Settings => Get("settings", "primary");
    public static ImageSource Search => Get("search", "secondary");
    public static ImageSource ListFilter => Get("list-filter", "secondary");
    public static ImageSource Plus => Get("plus", "secondary");
    public static ImageSource RefreshCw => Get("refresh-cw", "primary");
    public static ImageSource RotateCw => Get("rotate-cw", "secondary");
    public static ImageSource Pin => Get("pin", "primary");
    public static ImageSource PinOff => Get("pin-off", "secondary");
    public static ImageSource PinOffActive => Get("pin-off", "#FFFFFF");
    public static ImageSource MonitorSmartphone => Get("monitor-smartphone", "primary");
    public static ImageSource MonitorSmartphoneWhite => Get("monitor-smartphone", "#FFFFFF");
    public static ImageSource Smartphone => Get("smartphone", "primary");
    public static ImageSource Laptop => Get("laptop", "primary");
    public static ImageSource Monitor => Get("monitor", "primary");
    public static ImageSource Database => Get("database", "primary");
    public static ImageSource Palette => Get("palette", "primary");
    public static ImageSource Info => Get("info", "primary");
    public static ImageSource PanelLeftClose => Get("panel-left-close", "secondary");
    public static ImageSource PanelLeftOpen => Get("panel-left-open", "secondary");
    public static ImageSource Download => Get("download", "secondary");
    public static ImageSource Upload => Get("upload", "secondary");
    public static ImageSource FolderOpen => Get("folder-open", "secondary");
    public static ImageSource Clock => Get("clock", "secondary");
    public static ImageSource HardDrive => Get("hard-drive", "secondary");
    public static ImageSource Server => Get("server", "primary");
    public static ImageSource Keyboard => Get("keyboard", "primary");
    public static ImageSource ArrowUp => Get("arrow-up", "#FFFFFF");

    // ---- 大图查看器 HUD 与纯白/浅色变体 ----
    public static ImageSource ZoomInWhite => Get("zoom-in", "#F8FAFC");
    public static ImageSource ZoomOutWhite => Get("zoom-out", "#F8FAFC");
    public static ImageSource RotateCwWhite => Get("rotate-cw", "#F8FAFC");
    public static ImageSource Maximize2White => Get("maximize-2", "#F8FAFC");
    public static ImageSource ExternalLinkWhite => Get("external-link", "#F8FAFC");
    public static ImageSource XWhite => Get("x", "#F8FAFC");
    public static ImageSource SaveWhite => Get("save", "#F8FAFC");
    public static ImageSource CopyWhite => Get("copy", "#F8FAFC");
    public static ImageSource FolderOpenWhite => Get("folder-open", "#F8FAFC");
    public static ImageSource DownloadWhite => Get("download", "#F8FAFC");
    public static ImageSource PaletteWhite => Get("palette", "#F8FAFC");
    public static ImageSource FileTextWhite => Get("file-text", "#F8FAFC");
    public static ImageSource ImageMuted => Get("image", "#94A3B8");

    public static ImageSource Edit => Get("edit", "primary");
    public static ImageSource ExternalLink => Get("external-link", "primary");
    public static ImageSource ExternalLinkAccent => Get("external-link", "#2563EB");
    public static ImageSource CopyAccent => Get("copy", "#2563EB");
    public static ImageSource FolderOpenAccent => Get("folder-open", "#2563EB");
    public static ImageSource DownloadAccent => Get("download", "#2563EB");
    public static ImageSource PaletteAccent => Get("palette", "#2563EB");
    public static ImageSource ClipboardAccent => Get("clipboard", "#2563EB");
    public static ImageSource Save => Get("save", "primary");
    public static ImageSource ZoomIn => Get("zoom-in", "primary");
    public static ImageSource X => Get("x", "secondary");
    public static ImageSource Check => Get("check", "primary");
    public static ImageSource CheckWhite => Get("check", "#F8FAFC");
    public static ImageSource CheckCircle => Get("check-circle", "primary");
    public static ImageSource CheckCircleWhite => Get("check-circle", "#F8FAFC");
    public static ImageSource Sparkles => Get("sparkles", "primary");
    public static ImageSource SparklesAccent => Get("sparkles", "#2563EB");
    public static ImageSource SparklesWhite => Get("sparkles", "#F8FAFC");

    /// <summary>获取图标对应的纯白高对比度变体 (用于深色/强调色背景主按钮)。</summary>
    public static ImageSource GetWhiteVariant(ImageSource? icon)
    {
        if (icon == ExternalLink || icon == ExternalLinkAccent || icon == ExternalLinkWhite) return ExternalLinkWhite;
        if (icon == FolderOpen || icon == FolderOpenAccent || icon == FolderOpenWhite) return FolderOpenWhite;
        if (icon == Download || icon == DownloadAccent || icon == DownloadWhite) return DownloadWhite;
        if (icon == Copy || icon == CopyAccent || icon == CopyWhite) return CopyWhite;
        if (icon == Palette || icon == PaletteAccent || icon == PaletteWhite) return PaletteWhite;
        if (icon == FileText || icon == FileTextWhite) return FileTextWhite;
        return ExternalLinkWhite;
    }
}
