using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BranchTaskWpf.Converters;

/// <summary>
/// 多值绑定：根据节点层级(TreeLevel) 与「层级配色开关」(ColorByLevel) 返回行背景色。
/// 仅当 ColorByLevel=true 时返回浅色调色板（同一层级同色、不同层级异色），否则透明。
/// 调色板沿用老版(egui) level_color 的 6 色方案（红→蓝→绿→黄→紫→青），层级区分度高。
/// </summary>
public class LevelColorConverter : IMultiValueConverter
{
    // 沿用老版(egui) level_color 的 6 色浅色调色板：红→蓝→绿→黄→紫→青，层级区分度高
    private static readonly Color[] Palette =
    {
        Color.FromRgb(255, 224, 224), // 浅红
        Color.FromRgb(222, 235, 255), // 浅蓝
        Color.FromRgb(223, 248, 225), // 浅绿
        Color.FromRgb(255, 245, 210), // 浅黄
        Color.FromRgb(236, 228, 255), // 浅紫
        Color.FromRgb(222, 247, 255), // 浅青
    };

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return Brushes.Transparent;
        int level = 0;
        if (values[0] is int l) level = l;
        bool on = values[1] is bool b && b;
        if (!on) return Brushes.Transparent;
        var c = Palette[((level % Palette.Length) + Palette.Length) % Palette.Length];
        return new SolidColorBrush(c);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
