using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace NexClip.Desktop.Converters;

/// <summary>null → Collapsed,非 null → Visible(用于行内编辑区显隐)。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
