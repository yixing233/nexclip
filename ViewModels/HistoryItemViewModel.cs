using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SyncClipboard.Desktop.Models;
using SyncClipboard.Desktop.Services;

namespace SyncClipboard.Desktop.ViewModels;

/// <summary>历史条目卡片 VM(参考设计:元信息行 + 预览 + 选中态操作)。</summary>
public partial class HistoryItemViewModel : ObservableObject
{
    private static readonly SolidColorBrush SelectedBrush = new(ColorHelper.FromArgb(255, 37, 99, 235));
    private static readonly SolidColorBrush HoverBrush = new(ColorHelper.FromArgb(255, 96, 165, 250));   // hover 线框(浅蓝)
    private static readonly SolidColorBrush NormalBrush = new(ColorHelper.FromArgb(255, 229, 231, 235));

    public HistoryItem Item { get; }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isHovered;

    [ObservableProperty]
    private bool starred;

    public IRelayCommand CopyCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand ToggleStarCommand { get; }

    public HistoryItemViewModel(HistoryItem item, HistoryViewModel parent)
    {
        Item = item;
        starred = item.Starred;
        Thumbnail = BuildThumbnail(item.ImagePath);
        CopyCommand = new RelayCommand(async () => await parent.CopyAsync(this));
        DeleteCommand = new RelayCommand(() => parent.DeleteAsync(this));
        ToggleStarCommand = new RelayCommand(() => parent.ToggleStarAsync(this));
    }

    public string TypeText => Item.Type == "Image" ? "图片" : "文本";

    public string MetaText => Item.Type == "Image"
        ? "[图片]"
        : $"{Item.Text?.Length ?? 0} 字符";

    public string RelativeTime
    {
        get
        {
            var diff = DateTime.UtcNow - Item.CreatedAt;
            if (diff < TimeSpan.FromSeconds(30)) return "刚刚";
            if (diff < TimeSpan.FromMinutes(60)) return $"{(int)diff.TotalMinutes} 分钟前";
            if (diff < TimeSpan.FromHours(24)) return $"{(int)diff.TotalHours} 小时前";
            if (diff < TimeSpan.FromDays(7)) return $"{(int)diff.TotalDays} 天前";
            return Item.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd");
        }
    }

    public string PreviewText => Item.Type == "Image" ? "" : (Item.Text ?? "");

    public string DeviceName => Item.DeviceName ?? "";

    public BitmapImage? Thumbnail { get; }

    public bool IsImage => Item.Type == "Image";

    // ---- x:Bind 辅助(hover 线框 / 选中态样式) ----
    // 边框厚度恒定 1px:只换颜色不换尺寸,避免 hover 时卡片"动一下"
    public Brush BorderBrushFor(bool selected, bool hovered) =>
        selected ? SelectedBrush : hovered ? HoverBrush : NormalBrush;

    public Thickness BorderThicknessFor(bool selected, bool hovered) => new Thickness(1);

    // 操作按钮始终占位(不改变布局),仅用透明度显隐,避免卡片内容跳动
    public double ActionOpacityFor(bool hovered, bool selected) =>
        hovered || selected ? 1.0 : 0.0;

    public bool ActionHitTestFor(bool hovered, bool selected) => hovered || selected;

    /// <summary>类型图标(lucide)。</summary>
    public ImageSource TypeIconSource => Item.Type == "Image"
        ? Services.Lucide.Image
        : Services.Lucide.FileText;

    /// <summary>收藏图标(选中=琥珀色)。</summary>
    public ImageSource StarSource => Starred ? Services.Lucide.StarActive : Services.Lucide.Star;

    partial void OnStarredChanged(bool value) => OnPropertyChanged(nameof(StarSource));

    private static BitmapImage? BuildThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return new BitmapImage(new Uri("file:///" + path.Replace('\\', '/')));
    }
}
