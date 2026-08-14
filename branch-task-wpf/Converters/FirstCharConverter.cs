using System;
using System.Globalization;
using System.Windows.Data;

namespace BranchTaskWpf.Converters;

/// <summary>取字符串首个非空白字符（缩略图色块上的字号），空串返回 "?"。</summary>
public class FirstCharConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            foreach (var ch in s)
                if (!char.IsWhiteSpace(ch))
                    return ch.ToString();
            if (s.Length > 0) return s[0].ToString();
        }
        return "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
