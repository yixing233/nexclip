using System;
using Microsoft.UI.Xaml.Data;

namespace NexClip.Desktop.Converters;

/// <summary>把 0~1 的 double 格式化为百分比文本(如 0.85 -&gt; 85%)。</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is double d ? $"{Math.Round(d * 100)}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
