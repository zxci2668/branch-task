using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BranchTaskWpf.Converters;

/// <summary>
/// 与 BooleanToVisibilityConverter 相反：true → Collapsed，false → Visible。
/// 用于"收起态才显示"的元素（如左侧项目选项卡）。
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
