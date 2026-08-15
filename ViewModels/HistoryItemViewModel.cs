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

    // ---- x:Bind 辅助(选中态样式) ----
    public Brush BorderBrushFor(bool selected) => selected ? SelectedBrush : NormalBrush;

    public Thickness BorderThicknessFor(bool selected) => selected ? new Thickness(2) : new Thickness(1);

    public Visibility ActionVisibilityFor(bool hovered, bool selected) =>
        hovered || selected ? Visibility.Visible : Visibility.Collapsed;

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
