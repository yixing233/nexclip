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
