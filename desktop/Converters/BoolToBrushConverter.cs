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
