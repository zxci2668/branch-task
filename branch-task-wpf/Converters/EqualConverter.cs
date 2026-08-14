using System;
using System.Globalization;
using System.Windows.Data;

namespace BranchTaskWpf.Converters;

/// <summary>多值相等判定（缩略图高亮当前项目用）：(a, b) → a==b。</summary>
public class EqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        return Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
