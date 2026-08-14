using System;
using System.Globalization;
using System.Windows.Data;

namespace BranchTaskWpf.Converters;

/// <summary>
/// ulong? Unix 秒 → 北京时间字符串(yyyy-MM-dd HH:mm)；null/0 返回空串。
/// 与 Rust 版 format_beijing 对应，统一用 UTC+8，不引第三方时区库。
/// </summary>
public class UnixToBeijingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ulong ts && ts > 0)
        {
            try
            {
                var dto = DateTimeOffset.FromUnixTimeSeconds((long)ts).ToOffset(TimeSpan.FromHours(8));
                return dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch { }
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
