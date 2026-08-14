using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BranchTaskWpf.Converters;

/// <summary>
/// bool 为 true 时返回展开宽度，false 时返回收起宽度（默认 0 完全隐藏）。
/// 参数格式："展开宽" 或 "展开宽:收起宽"，例如 "220:24" → 展开 220、收起 24（留出选项卡条）。
/// </summary>
public class BoolToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double open = 220, collapsed = 0;
        if (parameter is string s)
        {
            var parts = s.Split(':');
            double.TryParse(parts[0], out open);
            if (parts.Length > 1) double.TryParse(parts[1], out collapsed);
        }
        if (value is bool b && b) return new GridLength(open);
        return new GridLength(collapsed);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
