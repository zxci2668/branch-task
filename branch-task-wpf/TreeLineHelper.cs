using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace BranchTaskWpf
{
    /// <summary>
    /// 为 TreeViewItem 计算层级 (Level) 与是否末子 (IsLastChild)，
    /// 并用代码直接操作 VisualTree 设置连接线几何，绘制标准树形连接线 ├─ / └─。
    ///
    /// 设计要点（避免"上下出头"）：
    ///  - 竖线(VLine) 只存在于"有子节点"的节点；
    ///  - 竖线从"自身表头中心"连到"最后一个子节点的表头中心"（即 └─ 转弯点），
    ///    因此不会在横线上方凭空伸出，也不会越过末子节点继续往下。
    ///  - 横线(HLine) 仅"有父节点"(level>0) 时显示，从父竖线连到本节点竖线。
    /// </summary>
    /// <summary>
    /// 零测量装饰器：Measure 阶段向父级报告尺寸为 0（绝不影响任何布局/行高），
    /// 但 Arrange 阶段仍按子元素的真实尺寸绘制。用于包裹 TreeViewItem 模板里的连接线矩形，
    /// 使得连接线高度变化永远不会撑高容器、永远不会引发折叠/展开时的位置跳变。
    /// </summary>
    public class ZeroMeasureDecorator : Decorator
    {
        protected override Size MeasureOverride(Size constraint)
        {
            Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return new Size(0, 0);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            // 让子元素在"装饰器被分配的完整单元格"内自行按 Margin / Alignment 布局，
            // 这样 VLine 的 Margin(topY) 与居中、HLine 的 Margin(-10) 与居中都正确生效；
            // 装饰器返回 arrangeSize 但自身 Measure 为 0，故绝不参与父 Grid 的行高/列宽计算。
            Child?.Arrange(new Rect(0, 0, arrangeSize.Width, arrangeSize.Height));
            return arrangeSize;
        }
    }

    public static class TreeLineHelper
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".branch-task", "debug.log");

        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.RegisterAttached("Level", typeof(int), typeof(TreeLineHelper),
                new PropertyMetadata(0));

        public static readonly DependencyProperty IsLastChildProperty =
            DependencyProperty.RegisterAttached("IsLastChild", typeof(bool), typeof(TreeLineHelper),
                new PropertyMetadata(false));

        public static int GetLevel(DependencyObject obj) => (int)obj.GetValue(LevelProperty);
        public static void SetLevel(DependencyObject obj, int value) => obj.SetValue(LevelProperty, value);
        public static bool GetIsLastChild(DependencyObject obj) => (bool)obj.GetValue(IsLastChildProperty);
        public static void SetIsLastChild(DependencyObject obj, bool value) => obj.SetValue(IsLastChildProperty, value);

        static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [TREELINE] {msg}\r\n"); } catch { }
        }

        public static void Refresh(TreeView tree)
        {
            Log($"Refresh(tree={(tree == null ? "NULL" : tree.Name)})");
            if (tree == null) { Log("  → early return"); return; }
            WaitAndWalk(tree, 0);
            // 布局完成后再校正竖线几何（ActualHeight 已就绪）。
            // 用 ContextIdle：在布局+渲染完成后执行，安全不会死循环。
            // 注意：绝不在回调里调 UpdateLayout()，否则 Render 优先级会无限递归导致白屏。
            tree.Dispatcher.BeginInvoke(new Action(() => WalkGeometry(tree)), DispatcherPriority.ContextIdle);
        }

        private static void WaitAndWalk(ItemsControl control, int level)
        {
            if (control.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            {
                EventHandler handler = null;
                handler = (s, e) =>
                {
                    if (control.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                    {
                        control.ItemContainerGenerator.StatusChanged -= handler;
                        WalkItems(control, level);
                    }
                };
                control.ItemContainerGenerator.StatusChanged += handler;
                return;
            }
            WalkItems(control, level);
        }

        private static void WalkItems(ItemsControl control, int level)
        {
            try
            {
                int count = control.Items.Count;
                for (int i = 0; i < count; i++)
                {
                    if (control.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem child)
                    {
                        bool isLast = (i == count - 1);
                        SetLevel(child, level);
                        SetIsLastChild(child, isLast);
                        child.ApplyTemplate();
                        Log($"  [{i}] \"{((child.DataContext as Models.TaskNode)?.Title) ?? "?"}\" L={level} Last={isLast}");
                        if (child.IsExpanded)
                            WaitAndWalk(child, level + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                // 子树遍历异常绝不影响大纲内容渲染，仅记录
                try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [TREELINE][WARN] WalkItems: {ex.GetType().Name}: {ex.Message}\r\n"); } catch { }
            }
        }

        /// <summary>布局完成后递归校正所有可见节点的竖线几何（用最新 ActualHeight 重新测量）</summary>
        private static void WalkGeometry(ItemsControl control)
        {
            int count = control.Items.Count;
            for (int i = 0; i < count; i++)
            {
                if (control.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem child)
                {
                    SetLineGeometry(child, GetLevel(child));
                    if (child.IsExpanded)
                        WalkGeometry(child);
                }
            }
        }

        /// <summary>
        /// 直接操作 VisualTree 设置连接线：
        ///  HLine 可见性由 level 决定；VLine 可见性由"是否有子节点"决定，几何由精确测量决定。
        /// </summary>
        private static void SetLineGeometry(TreeViewItem tvi, int level)
        {
            try
            {
                var h = FindChildByTag(tvi, "HLine");
                if (h != null)
                    h.Visibility = (level > 0) ? Visibility.Visible : Visibility.Collapsed;

                var v = FindChildByTag(tvi, "VLine");
                if (v == null) return;

                // 只有"有可见子节点"的节点才需要竖线（连接它自己的子节点）
                if (!(tvi.HasItems && tvi.IsExpanded))
                {
                    v.Visibility = Visibility.Collapsed;
                    return;
                }

                var header = GetTemplateChild(tvi, "HeaderBorder") as FrameworkElement;
                // 布局未完成（ActualHeight 仍为 0）时禁止设置竖线几何：
                // 此时 TranslatePoint 返回错误坐标，异常大的 Height 会撑高模板 Auto 行，把整棵树推到底部。
                // 竖线在 DispatcherPriority.Render 的 WalkGeometry 阶段（布局已落地）统一校正。
                if (header == null || header.ActualHeight < 1)
                {
                    v.Visibility = Visibility.Collapsed;
                    return;
                }
                v.Visibility = Visibility.Visible;

                // 自身表头中心（相对 tvi 顶部）
                double topY = header.TranslatePoint(new Point(0, header.ActualHeight / 2.0), tvi).Y;

                // 最后一个子节点的表头中心（竖线在此结束 = └─ 转弯点）
                double bottomY = topY;
                var lastChild = tvi.ItemContainerGenerator.ContainerFromIndex(tvi.Items.Count - 1) as TreeViewItem;
                if (lastChild != null)
                {
                    var lastHeader = GetTemplateChild(lastChild, "HeaderBorder") as FrameworkElement;
                    if (lastHeader != null && lastHeader.ActualHeight >= 1)
                        bottomY = lastHeader.TranslatePoint(new Point(0, lastHeader.ActualHeight / 2.0), tvi).Y;
                }

                double hgt = bottomY - topY;
                if (hgt < 0) hgt = 0;
                if (hgt > 32767) hgt = 32767; // 防御：异常测量值钳制，绝不允许撑坏模板布局
                v.Margin = new Thickness(0, topY, 0, 0);
                v.VerticalAlignment = VerticalAlignment.Top;
                v.Height = hgt;
            }
            catch (Exception ex)
            {
                // 连线几何异常绝不影响大纲内容：仅记录，不抛出
                try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [TREELINE][WARN] {ex.GetType().Name}: {ex.Message}\r\n"); } catch { }
            }
        }

        private static FrameworkElement? GetTemplateChild(TreeViewItem tvi, string name)
        {
            return tvi.Template?.FindName(name, tvi) as FrameworkElement;
        }

        private static FrameworkElement? FindChildByTag(DependencyObject parent, string tag)
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is FrameworkElement fe && fe.Tag as string == tag) return fe;
                var found = FindChildByTag(c, tag);
                if (found != null) return found;
            }
            return null;
        }
    }

    /// <summary>
    /// 竖向延续线可见性：非根 且 非末子 → 显示（├─ 有下延线，└─ 无）
    /// </summary>
    public class TreeLineVertVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            int level = values.Length > 0 && values[0] is int l ? l : -1;
            bool isLast = values.Length > 1 && values[1] is bool b && b;
            return (level > 0 && !isLast) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 横向肘线可见性：非根（Level > 0）→ 显示
    /// </summary>
    public class TreeLineHorizVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            int level = value is int l ? l : 0;
            return level > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
