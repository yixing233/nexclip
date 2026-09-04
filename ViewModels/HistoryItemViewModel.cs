using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NexClip.Desktop.Models;
using NexClip.Desktop.Services;

namespace NexClip.Desktop.ViewModels;

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

    partial void OnIsHoveredChanged(bool value)
    {
        OnPropertyChanged(nameof(RemarkOpacity));
        OnPropertyChanged(nameof(RemarkHitTest));
        OnPropertyChanged(nameof(OriginalOpacity));
        OnPropertyChanged(nameof(OriginalHitTest));
    }

    [ObservableProperty]
    private bool starred;

    [ObservableProperty]
    private string? remark;

    public bool HasRemark => !string.IsNullOrWhiteSpace(Remark);

    /// <summary>备注层透明度(有备注且未悬停时为 1，悬停时渐隐为 0)。</summary>
    public double RemarkOpacity => HasRemark ? (IsHovered ? 0.0 : 1.0) : 0.0;
    public bool RemarkHitTest => HasRemark && !IsHovered;

    /// <summary>原内容层透明度(无备注或悬停时为 1，未悬停且有备注时为 0 并保留占位测量以防高度抖动)。</summary>
    public double OriginalOpacity => !HasRemark || IsHovered ? 1.0 : 0.0;
    public bool OriginalHitTest => !HasRemark || IsHovered;

    partial void OnRemarkChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRemark));
        OnPropertyChanged(nameof(RemarkOpacity));
        OnPropertyChanged(nameof(RemarkHitTest));
        OnPropertyChanged(nameof(OriginalOpacity));
        OnPropertyChanged(nameof(OriginalHitTest));
    }

    [ObservableProperty]
    private int indexInList;

    public string? ShortcutHint => IndexInList is >= 1 and <= 9 ? $"Ctrl+{IndexInList}" : null;
    public bool HasShortcutHint => IndexInList is >= 1 and <= 9;

    partial void OnIndexInListChanged(int value)
    {
        OnPropertyChanged(nameof(ShortcutHint));
        OnPropertyChanged(nameof(HasShortcutHint));
    }

    public IRelayCommand CopyCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand ToggleStarCommand { get; }
    public IRelayCommand SmartPrimaryCommand { get; }
    public IRelayCommand SmartSecondaryCommand { get; }

    public HistoryItemViewModel(HistoryItem item, HistoryViewModel parent)
    {
        Item = item;
        starred = item.Starred;
        remark = item.Remark;
        Thumbnail = BuildThumbnail(item.ImagePath);
        SourceAppIcon = BuildAppIcon(item.SourceAppIcon);
        RefreshFormatAnalysis();
        CopyCommand = new RelayCommand(async () => await parent.CopyAsync(this));
        DeleteCommand = new RelayCommand(() => parent.DeleteAsync(this));
        ToggleStarCommand = new RelayCommand(() => parent.ToggleStarAsync(this));
        SmartPrimaryCommand = new RelayCommand(() => _smartAction?.PrimaryAction());
        SmartSecondaryCommand = new RelayCommand(() => _smartAction?.SecondaryAction?.Invoke());
    }

    private bool _isColor;
    private SolidColorBrush? _colorBrush;
    private string? _domainText;
    private bool _isCodeOrJson;
    private SmartAction? _smartAction;

    public bool IsColor => _isColor;
    public SolidColorBrush? ColorBrush => _colorBrush;

    public bool HasDomain => !string.IsNullOrEmpty(_domainText);
    public string? DomainText => _domainText;

    public bool IsCodeOrJson => _isCodeOrJson;
    public bool IsNormalText => !IsImage && !_isColor && !_isCodeOrJson;

    public bool HasSmartAction => _smartAction != null;
    public string SmartPrimaryText => _smartAction?.PrimaryButtonText ?? "";
    public ImageSource? SmartPrimaryIcon => _smartAction?.PrimaryButtonIcon ?? _smartAction?.Icon ?? Services.Lucide.ExternalLink;
    public string SmartPrimaryToolTip => _smartAction?.Subtitle ?? _smartAction?.Title ?? "";

    public bool HasSmartSecondary => _smartAction?.SecondaryButtonText != null;
    public string SmartSecondaryText => _smartAction?.SecondaryButtonText ?? "";
    public ImageSource? SmartSecondaryIcon => _smartAction?.SecondaryButtonIcon ?? Services.Lucide.Copy;
    public string SmartSecondaryToolTip => _smartAction?.SecondaryButtonText ?? "";

    private void RefreshFormatAnalysis()
    {
        if (Item.Type == "Text" && !string.IsNullOrWhiteSpace(Item.Text))
        {
            _isColor = FormatHelper.TryParseColor(Item.Text, out _, out _colorBrush);
            _domainText = FormatHelper.ExtractDomain(Item.Text);
            _isCodeOrJson = FormatHelper.IsCodeOrJson(Item.Text);
            _smartAction = SmartActionService.Detect(Item.Text);
        }
        else
        {
            _isColor = false;
            _colorBrush = null;
            _domainText = null;
            _isCodeOrJson = false;
            _smartAction = null;
        }
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

    public string SourceAppName => Item.SourceAppName ?? "";

    public bool HasSourceApp => !string.IsNullOrWhiteSpace(Item.SourceAppName);

    public bool HasSourceAppIcon => SourceAppIcon != null;

    public string? SourceAppPath => Item.SourceAppPath;

    public BitmapImage? SourceAppIcon { get; }

    public BitmapImage? Thumbnail { get; }

    public bool IsImage => Item.Type == "Image";

    /// <summary>条目是否为 http/https 链接。</summary>
    public bool IsLink => Services.UrlUtil.IsUrl(Item.Text);

    /// <summary>链接条目的展示文本(整段文本即链接时直接显示)。</summary>
    public string LinkText => Item.Text?.Trim().Length > 90 ? Item.Text.Trim()[..90] + "…" : (Item.Text ?? "");

    // ---- x:Bind 辅助(hover 线框 / 选中态样式) ----
    // 边框厚度恒定 1px:只换颜色不换尺寸,避免 hover 时卡片"动一下"
    public Brush BorderBrushFor(bool selected, bool hovered) =>
        selected ? SelectedBrush : hovered ? HoverBrush : NormalBrush;

    public Thickness BorderThicknessFor(bool selected, bool hovered) => new Thickness(1);

    // 操作按钮始终占位(不改变布局),仅用透明度显隐,避免卡片内容跳动
    public double ActionOpacityFor(bool hovered, bool selected) =>
        hovered || selected ? 1.0 : 0.0;

    public bool ActionHitTestFor(bool hovered, bool selected) => hovered || selected;

    /// <summary>条目是否携带富文本(HTML)片段。</summary>
    public bool HasHtml => Item.HasHtml;

    /// <summary>类型图标(lucide)。富文本条目用独立图标区分于纯文本。</summary>
    public ImageSource TypeIconSource => Item.Type == "Image"
        ? Services.Lucide.Image
        : HasHtml
            ? Services.Lucide.RichText
            : Services.Lucide.FileText;

    /// <summary>收藏图标(选中=琥珀色)。</summary>
    public ImageSource StarSource => Starred ? Services.Lucide.StarActive : Services.Lucide.Star;

    partial void OnStarredChanged(bool value) => OnPropertyChanged(nameof(StarSource));

    /// <summary>编辑文本后同步更新卡片内容。</summary>
    public void ApplyText(string text)
    {
        Item.Text = text;
        RefreshFormatAnalysis();
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(IsColor));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(HasDomain));
        OnPropertyChanged(nameof(DomainText));
        OnPropertyChanged(nameof(IsCodeOrJson));
        OnPropertyChanged(nameof(IsNormalText));
        OnPropertyChanged(nameof(HasSmartAction));
        OnPropertyChanged(nameof(SmartPrimaryText));
        OnPropertyChanged(nameof(SmartPrimaryIcon));
        OnPropertyChanged(nameof(SmartPrimaryToolTip));
        OnPropertyChanged(nameof(HasSmartSecondary));
        OnPropertyChanged(nameof(SmartSecondaryText));
        OnPropertyChanged(nameof(SmartSecondaryIcon));
        OnPropertyChanged(nameof(SmartSecondaryToolTip));
    }

    /// <summary>更新备注后同步更新卡片展示与状态。</summary>
    public void ApplyRemark(string? newRemark)
    {
        var trimmed = string.IsNullOrWhiteSpace(newRemark) ? null : newRemark.Trim();
        Item.Remark = trimmed;
        Remark = trimmed;
        OnPropertyChanged(nameof(Remark));
        OnPropertyChanged(nameof(HasRemark));
        OnPropertyChanged(nameof(RemarkOpacity));
        OnPropertyChanged(nameof(RemarkHitTest));
        OnPropertyChanged(nameof(OriginalOpacity));
        OnPropertyChanged(nameof(OriginalHitTest));
    }

    private static BitmapImage? BuildThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            // 解码尺寸必须在设置 UriSource 之前赋值：BitmapImage(Uri) 构造函数会立即开始解码，
            // 之后再设 DecodePixel* 已经来不及，等于按原图原始尺寸解码并常驻内存。
            // 按长边约束避免超宽/超高图片被反向放大：BitmapImage 只设一个维度时会按原始比例
            // 推算另一维度，若一律限高 140，3840x200 的超宽长条图会被推算成 2688 宽，
            // 解码面积反而比限宽时大几十倍。
            var bmp = new BitmapImage();
            bmp.DecodePixelType = DecodePixelType.Logical;
            var size = ImageCodec.TryReadPngSize(path);
            if (size is { } s && s.Width > s.Height)
            {
                // 横图按列表卡片可用宽度量级限宽
                bmp.DecodePixelWidth = 360;
            }
            else
            {
                // 竖图/方图，以及非 PNG 或读取头部失败时的兜底：按卡片显示高度上限限高
                bmp.DecodePixelHeight = 140;
            }
            bmp.UriSource = new Uri("file:///" + path.Replace('\\', '/'));
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? BuildAppIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            // 同上：解码尺寸必须先于 UriSource 设置才会生效。
            var bmp = new BitmapImage();
            bmp.DecodePixelWidth = 32;
            bmp.DecodePixelHeight = 32;
            bmp.UriSource = new Uri("file:///" + path.Replace('\\', '/'));
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
