using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BranchTaskWpf.Converters;

/// <summary>
/// 多值绑定：根据项目在其所属集合中的序号返回稳定的彩色画刷（缩略图色块用）。
/// 调色板沿用老版本(egui) project_color 的饱和配色，不同项目异色、刷新后稳定。
/// </summary>
public class ProjectColorConverter : IMultiValueConverter
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE5, 0x73, 0x73), // 红
        Color.FromRgb(0x4A, 0x90, 0xD9), // 蓝
        Color.FromRgb(0x67, 0xC2, 0x3A), // 绿
        Color.FromRgb(0xF0, 0xA9, 0x30), // 橙
        Color.FromRgb(0x9B, 0x59, 0xB6), // 紫
        Color.FromRgb(0x1A, 0xBC, 0x9C), // 青
        Color.FromRgb(0xE8, 0x4A, 0x8A), // 粉
        Color.FromRgb(0x7F, 0x8C, 0x8D), // 灰
    };

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        int idx = 0;
        if (values.Length >= 2 && values[1] is IList list)
            idx = list.IndexOf(values[0]);
        if (idx < 0) idx = 0;
        var c = Palette[((idx % Palette.Length) + Palette.Length) % Palette.Length];
        return new SolidColorBrush(c);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
