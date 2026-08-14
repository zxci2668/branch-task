using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BranchTaskWpf.Converters;

/// <summary>
/// 字符串非空 → Visible，否则 Collapsed。用于搜索框清除按钮的按需显示。
/// </summary>
public class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
