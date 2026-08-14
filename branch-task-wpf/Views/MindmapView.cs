using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BranchTaskWpf.Models;

namespace BranchTaskWpf.Views;

/// <summary>
/// 思维导图视图：Canvas 自绘制，支持缩放/平移 + 自动 fit-to-view
/// </summary>
public class MindmapView : Canvas
{
    public static readonly DependencyProperty RootNodeProperty =
        DependencyProperty.Register(nameof(RootNode), typeof(TaskNode), typeof(MindmapView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRootChanged));

    public TaskNode? RootNode
    {
        get => (TaskNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    private double _scale = 1.0;
    private Vector _offset = new(0, 0);
    private Point _lastMouse;
    private bool _isPanning;
    private bool _needFit = true;

    public double Scale => _scale;

    private const double LevelW = 220;   // 每层水平间距
    private const double RowGap = 14;    // 叶子节点间垂直间距
    private const double NodeH = 38;

    private readonly Dictionary<string, Rect> _nodeRects = new();
    private readonly Dictionary<string, Point> _positions = new();
    private readonly Dictionary<string, double> _nodeW = new();

    public MindmapView()
    {
        Background = Brushes.White;
        ClipToBounds = true;
        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
    }

    private static void OnRootChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MindmapView mv)
        {
            mv._needFit = true;       // 数据变化时重新自适应
            mv.InvalidateVisual();
        }
    }

    public void ZoomIn()
    {
        _scale = Math.Min(_scale * 1.2, 3.0);
        _needFit = false;
        InvalidateVisual();
    }

    public void ZoomOut()
    {
        _scale = Math.Max(_scale / 1.2, 0.15);
        _needFit = false;
        InvalidateVisual();
    }

    public void ZoomReset()
    {
        _needFit = true;
        _scale = 1.0;
        _offset = new Vector(0, 0);
        InvalidateVisual();
    }

    /// <summary>按文字长度测量节点框宽度（含状态圆点 + 文字 + padding）</summary>
    private double MeasureNodeWidth(TaskNode node)
    {
        var text = node.Title.Length > 24 ? node.Title[..23] + "…" : node.Title;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.Black, 1.0);
        return Math.Max(60, ft.Width + 34);
    }

    /// <summary>
    /// 纯布局（布局坐标，不缩放）：父节点居中于子节点垂直中点，子节点从上到下依次排布、互不重叠。
    /// 返回子树占用的底部 y（绝对布局坐标）。
    /// </summary>
    private double Layout(TaskNode node, double x, double y, Dictionary<string, Point> pos)
    {
        double slot = NodeH + RowGap;
        double w = MeasureNodeWidth(node);
        _nodeW[node.Id] = w;

        if (node.Children.Count == 0)
        {
            pos[node.Id] = new Point(x, y);
            return y + slot;
        }

        double childX = x + LevelW;
        double cursor = y;
        foreach (var child in node.Children)
        {
            cursor = Layout(child, childX, cursor, pos);
        }

        // 父节点居中于首个与末个子节点的中点（视觉平衡，且永不与子节点重叠，因为处于上一层 x）
        double firstY = pos[node.Children[0].Id].Y;
        double lastY = pos[node.Children[^1].Id].Y;
        double midY = (firstY + lastY) / 2;
        pos[node.Id] = new Point(x, midY - NodeH / 2);

        return Math.Max(cursor, midY + NodeH / 2);
    }

    /// <summary>根据布局边界自动缩放并居中整棵树到画布</summary>
    private void FitToView(Dictionary<string, Point> pos)
    {
        if (RootNode == null || pos.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (id, p) in pos)
        {
            double w = _nodeW.TryGetValue(id, out var v) ? v : 60;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X + w);
            maxY = Math.Max(maxY, p.Y + NodeH);
        }
        if (!double.IsFinite(minX) || maxX <= minX || maxY <= minY) return;

        double treeW = maxX - minX;
        double treeH = maxY - minY;
        double viewW = ActualWidth > 1 ? ActualWidth : 800;
        double viewH = ActualHeight > 1 ? ActualHeight : 600;
        const double margin = 30;

        double sx = (viewW - margin * 2) / treeW;
        double sy = (viewH - margin * 2) / treeH;
        _scale = Math.Min(Math.Min(sx, sy), 1.5);
        _scale = Math.Max(_scale, 0.15);

        _offset.X = (viewW - treeW * _scale) / 2 - minX * _scale;
        _offset.Y = (viewH - treeH * _scale) / 2 - minY * _scale;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _nodeRects.Clear();
        if (RootNode == null) return;
        try
        {
            _positions.Clear();
            _nodeW.Clear();
            Layout(RootNode, 0, 0, _positions);

            if (_needFit)
            {
                FitToView(_positions);
                _needFit = false;
            }

            var pen = new Pen(Brushes.LightGray, 1.5);
            DrawLines(RootNode, _positions, dc, pen);

            foreach (var (id, p) in _positions)
            {
                var node = RootNode.Find(id);
                if (node == null) continue;
                double w = _nodeW.TryGetValue(id, out var v) ? v : 60;

                double x = p.X * _scale + _offset.X;
                double y = p.Y * _scale + _offset.Y;
                double nw = w * _scale;
                double nh = NodeH * _scale;
                var rect = new Rect(x, y, nw, nh);
                _nodeRects[id] = rect;

                var bg = node.Status switch
                {
                    "done" => Color.FromRgb(0xE8, 0xF5, 0xE9),
                    "doing" => Color.FromRgb(0xFF, 0xF3, 0xE0),
                    "blocked" => Color.FromRgb(0xFF, 0xEB, 0xEE),
                    _ => Color.FromRgb(0xF5, 0xF5, 0xF5)
                };
                dc.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(Brushes.LightGray, 1), rect, 6, 6);

                var dot = node.Status switch
                {
                    "done" => Brushes.Green,
                    "doing" => Brushes.Orange,
                    "blocked" => Brushes.Red,
                    _ => Brushes.Gray
                };
                dc.DrawEllipse(dot, null, new Point(x + 9 * _scale, y + nh / 2), 4 * _scale, 4 * _scale);

                var disp = node.Title.Length > 24 ? node.Title[..23] + "…" : node.Title;
                var ft = new FormattedText(disp, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12 * _scale, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(x + 18 * _scale, y + (nh - ft.Height) / 2));
            }

            DrawMinimap(dc);
        }
        catch (Exception ex)
        {
            dc.DrawText(new FormattedText($"Mindmap error: {ex.Message}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.Red, 1.0), new Point(10, 40));
        }
    }

    /// <summary>缩略图(minimap)：右上角展示整棵树 + 当前视口蓝色方框，对应老版 React Flow 的 MiniMap。</summary>
    private void DrawMinimap(DrawingContext dc)
    {
        if (_positions.Count == 0 || ActualWidth < 2 || ActualHeight < 2) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (id, p) in _positions)
        {
            double w = _nodeW.TryGetValue(id, out var v) ? v : 60;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X + w); maxY = Math.Max(maxY, p.Y + NodeH);
        }
        if (!double.IsFinite(minX) || maxX <= minX || maxY <= minY) return;

        double miniW = 180, miniH = 130, pad = 12;
        double mx = ActualWidth - miniW - pad;
        double my = pad;                       // 右上角（避开右下角的缩放控件）
        var box = new Rect(mx, my, miniW, miniH);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 247, 247, 247)),
            new Pen(Brushes.LightGray, 1), box, 6, 6);

        double worldW = maxX - minX, worldH = maxY - minY;
        double s = Math.Min((miniW - 12) / worldW, (miniH - 12) / worldH);
        double ox = mx + (miniW - worldW * s) / 2;
        double oy = my + (miniH - worldH * s) / 2;

        // 整棵树缩略节点
        foreach (var (id, p) in _positions)
        {
            var node = RootNode?.Find(id);
            if (node == null) continue;
            double w = _nodeW.TryGetValue(id, out var v) ? v : 60;
            var c = node.Status switch
            {
                "done" => Colors.Green,
                "doing" => Colors.Orange,
                "blocked" => Colors.Red,
                _ => Colors.Gray
            };
            dc.DrawRectangle(new SolidColorBrush(c), null,
                new Rect(ox + (p.X - minX) * s, oy + (p.Y - minY) * s,
                         Math.Max(2, w * s), Math.Max(2, NodeH * s)));
        }

        // 当前视口在世界坐标中的范围 → minimap 中的蓝框
        double vMinX = (-_offset.X) / _scale;
        double vMinY = (-_offset.Y) / _scale;
        double vMaxX = (ActualWidth - _offset.X) / _scale;
        double vMaxY = (ActualHeight - _offset.Y) / _scale;
        var vp = new Rect(ox + (vMinX - minX) * s, oy + (vMinY - minY) * s,
                          (vMaxX - vMinX) * s, (vMaxY - vMinY) * s);
        dc.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1.5), vp);
    }

    private void DrawLines(TaskNode node, Dictionary<string, Point> pos, DrawingContext dc, Pen pen)
    {
        if (!pos.TryGetValue(node.Id, out var pp)) return;
        double pw = _nodeW.TryGetValue(node.Id, out var v) ? v : 60;
        foreach (var child in node.Children)
        {
            if (!pos.TryGetValue(child.Id, out var cp)) continue;
            double cw = _nodeW.TryGetValue(child.Id, out var cv) ? cv : 60;
            var p1 = new Point((pp.X + pw) * _scale + _offset.X, (pp.Y + NodeH / 2) * _scale + _offset.Y);
            var p2 = new Point(cp.X * _scale + _offset.X, (cp.Y + NodeH / 2) * _scale + _offset.Y);
            dc.DrawLine(pen, p1, p2);
            DrawLines(child, pos, dc, pen);
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var zoom = e.Delta > 0 ? 1.1 : 0.9;
        _scale = Math.Clamp(_scale * zoom, 0.15, 3.0);
        _needFit = false;
        InvalidateVisual();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        _offset += (Vector)(pos - _lastMouse);
        _lastMouse = pos;
        _needFit = false;
        InvalidateVisual();
    }
}
