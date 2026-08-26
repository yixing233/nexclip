using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NexClip.Desktop.Services;

public enum SmartActionKind
{
    Url,
    GitHub,
    NetDisk,
    Color,
    LocalFolder,
    LocalFile
}

public sealed class SmartAction
{
    public SmartActionKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public ImageSource? Icon { get; set; }

    // 主操作 (Enter / Space 默认执行)
    public string PrimaryButtonText { get; set; } = "";
    public ImageSource? PrimaryButtonIcon { get; set; }
    public Action PrimaryAction { get; set; } = () => { };

    // 次操作 (可选)
    public string? SecondaryButtonText { get; set; }
    public ImageSource? SecondaryButtonIcon { get; set; }
    public Action? SecondaryAction { get; set; }

    // 颜色专属数据
    public Color? PreviewColor { get; set; }
    public string? HexColorString { get; set; }
    public string? RgbColorString { get; set; }
    public string? HslColorString { get; set; }

    // 网盘专属数据 (提取码)
    public string? ExtractionCode { get; set; }

    // 本地路径专属数据
    public string? TargetPath { get; set; }
}
