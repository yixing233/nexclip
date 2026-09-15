using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace NexClip.Desktop.Converters;

/// <summary>在线状态 → 颜色:在线=绿,离线=灰。</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OnlineBrush = new(ColorHelper.FromArgb(255, 16, 185, 129));   // #10B981
    private static readonly SolidColorBrush OfflineBrush = new(ColorHelper.FromArgb(255, 156, 163, 175));  // #9CA3AF

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? OnlineBrush : OfflineBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>胶囊选中状态 → 文本颜色: 选中=纯白(#FFFFFF), 未选中=主题主文本色自适应。</summary>
public sealed class BoolToPillForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush WhiteBrush = new(Microsoft.UI.Colors.White);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true)
        {
            return WhiteBrush;
        }

        return new SolidColorBrush(Services.Lucide.IsDarkTheme
            ? ColorHelper.FromArgb(255, 248, 250, 252)
            : ColorHelper.FromArgb(255, 15, 23, 42));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// 胶囊选中状态 → 底色: 选中=品牌蓝(#2563EB), 未选中=卡片次级底色(随主题自适应)。
/// 与 <see cref="BoolToPillForegroundConverter"/> 配对使用, 两者由同一个 VM 属性驱动,
/// 从而保证底色与文字色不可能分叉(参见 TransferChatPage.xaml 中 FilterPillStyle 的说明)。
/// </summary>
public sealed class BoolToPillBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush SelectedBrush = new(ColorHelper.FromArgb(255, 37, 99, 235));   // #2563EB
    private static readonly SolidColorBrush LightFallback = new(ColorHelper.FromArgb(255, 243, 244, 246));
    private static readonly SolidColorBrush DarkFallback = new(ColorHelper.FromArgb(255, 45, 48, 56));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true) return SelectedBrush;

        // 未选中: 优先复用主题的卡片次级底色, 取不到时按当前主题回退
        try
        {
            if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var res)
                && res is Brush themeBrush)
            {
                return themeBrush;
            }
        }
        catch
        {
            // 资源字典在启动早期可能尚未就绪, 走回退色
        }
        return Services.Lucide.IsDarkTheme ? DarkFallback : LightFallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
