using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BranchTaskWpf.Converters;

public class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "done" => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            "doing" => new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)),
            "in_progress" => new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)),
            "blocked" => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
            _ => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "done" => "已完成",
            "in_progress" => "进行中",
            "blocked" => "阻塞",
            _ => "未标记"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
