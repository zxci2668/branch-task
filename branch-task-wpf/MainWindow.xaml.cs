using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BranchTaskWpf.Models;
using BranchTaskWpf.Services;
using BranchTaskWpf.ViewModels;
using Microsoft.Win32;

namespace BranchTaskWpf;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private bool _initialized;
    // 程序化刷新(替换 FilteredRoots/SelectedProject 导致 TreeView 重建)期间，
    // 屏蔽 Tree_SelectedItemChanged 对 VM.SelectedNode 的反向覆盖，避免 WPF 复位 SelectedItem 到第一项。
    private bool _suppressSelectionSync;

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            var dataPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".branch-task", "projects.json");

            if (VM.Projects.Count == 0)
            {
                if (System.IO.File.Exists(dataPath))
                {
                    // 数据文件存在但读不出任何项目（多半是跨版本格式不兼容）。
                    // 绝不直接覆盖——保留原文件，用户可从 .branch-task 下的 *.backup.* 恢复。
                    App.LogCrash("LoadData", new Exception(
                        $"projects.json 存在但反序列化得到 0 个项目，已保留原文件未覆盖（路径: {dataPath}）"));
                }
                else
                {
                    // 仅"确实没有任何数据文件"的首次启动才种入示例项目
                    VM.SeedDemoProject();
                }
            }
            else
            {
                VM.SelectedProject = VM.Projects[0];
                VM.SelectedNode = VM.SelectedProject?.Root;
            }
            _initialized = true;

            // 连接线：加载、展开/折叠、数据变化后重算 Level/IsLastChild
            outlineTree.Loaded += (s, e) =>
            {
                try { VM.RefreshFilteredRoots(); } catch (Exception ex) { App.LogCrash("RefreshFilteredRoots@Loaded", ex); }
                RefreshTreeLines();
            };
            outlineTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((s, e) => RefreshTreeLines()));
            outlineTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler((s, e) => RefreshTreeLines()));
            VM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.FilteredRoots) ||
                    e.PropertyName == nameof(MainViewModel.SelectedProject))
                {
                    // 重建 ItemsSource 期间屏蔽反向选中同步，并在容器重建完成后解除
                    _suppressSelectionSync = true;
                    RefreshTreeLines();
                    Dispatcher.BeginInvoke(new Action(() => _suppressSelectionSync = false),
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
                // 选中变化后，等容器生成(ContextIdle)再强制对齐 UI 选中，
                // 防止 TreeView 重建 ItemsSource 时把 SelectedItem 复位到第一项。
                if (e.PropertyName == nameof(MainViewModel.SelectedNode))
                    Dispatcher.BeginInvoke(new Action(EnsureSelection), System.Windows.Threading.DispatcherPriority.ContextIdle);
            };

            // 新建任务后自动进入就地重命名
            VM.NodeCreated += node =>
            {
                Dispatcher.BeginInvoke(new Action(() => BeginEditNode(node)),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            };
        }
        catch (Exception ex)
        {
            App.LogCrash("MainWindow.ctor", ex);
            MessageBox.Show($"启动失败:\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "新新的任务树", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        }

        /// <summary>若原数据文件存在且非空，先备份（防示例项目覆盖时丢失损坏的原数据）。</summary>
        private void BackupDataIfExists(string dataPath)
        {
            try
            {
                if (System.IO.File.Exists(dataPath))
                {
                    var info = new System.IO.FileInfo(dataPath);
                    if (info.Length > 0)
                    {
                        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var backup = dataPath.Replace(".json", $".corrupt_{stamp}.json");
                        System.IO.File.Copy(dataPath, backup, true);
                        App.LogCrash("BackupData", new Exception($"原数据文件已备份至 {backup} (长度 {info.Length})"));
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogCrash("BackupDataIfExists", ex);
            }
        }

        private void RefreshTreeLines()
        {
            // 连接线绘制逻辑绝不允许影响大纲内容：任何异常都就地吞掉，只记日志。
            // 仅调用一次 Refresh：TreeLineHelper 内部会等容器生成，并在绘制前(Render 优先级、
            // 布局已落地)统一校正竖线几何——保证折叠/展开时每帧都是正确高度，上方节点不动、下方自然收拢。
            try { TreeLineHelper.Refresh(outlineTree); }
            catch (Exception ex) { App.LogCrash("TreeLineHelper.Refresh", ex); }
        }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (VM.SelectedProject == null) return;
        var focused = Keyboard.FocusedElement;
        if (focused is TextBox || focused is ComboBox) return;

        if (e.Key == Key.Enter)
        {
            VM.AddChildCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            VM.AddSiblingCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            VM.MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            VM.MoveSelection(1);
            e.Handled = true;
        }
    }

    // ===== 拖拽排序（全节点可拖，展开三角区域除外） =====
    // 关键点：mousedown 时锁定源项(_dragItem)与起点(_dragStartPoint，相对窗口坐标)，
    // 坐标用 e.GetPosition(this) 而非 e.GetPosition(item)——后者随当前 hover 的 TreeViewItem 变化，
    // 会导致 _dragStart 与 now 来自不同项，阈值判定失效。
    private Point _dragStartPoint;     // 相对窗口坐标（稳定，不随 TreeViewItem 变化）
    private TreeViewItem? _dragItem;   // mousedown 时的源项
    private bool _dragPending;         // 左键已按下，等待超过拖拽阈值
    private bool _inExpandZone;        // mousedown 落在展开三角区域 → 只折叠，不拖拽
    private bool _dropping;             // Drop 处理中的防重入守卫（WPF 隐式冒泡可能重复触发）
    private TaskNode? _popupNode;        // 当前弹出的状态选框所属节点（因 Popup 内容在独立视觉树，无法用视觉父级回溯，故用字段保存）
    private Popup? _popupRef;             // 当前打开的状态选框 Popup 引用（用于点击选项后关闭）

    private void TreeItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = sender as TreeViewItem ?? FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = item?.DataContext as TaskNode;
        if (item == null || node == null)
        {
            _dragItem = null;
            _dragPending = false;
            return;
        }
        _dragItem = item;
        _dragStartPoint = e.GetPosition(this);              // 相对窗口，跨项稳定
        _inExpandZone = e.GetPosition(item).X < 20;         // 展开三角约在左侧 20px
        _dragPending = true;
        DbgLog($"DOWN id={node.Id} expand={_inExpandZone}");
    }

    private void TreeItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragPending = false; return; }
        if (_inExpandZone || _dragItem == null) return;

        Point now = e.GetPosition(this);                    // 相对窗口，与 _dragStartPoint 同源
        double dx = Math.Abs(now.X - _dragStartPoint.X);
        double dy = Math.Abs(now.Y - _dragStartPoint.Y);
        if (dx > SystemParameters.MinimumHorizontalDragDistance ||
            dy > SystemParameters.MinimumVerticalDragDistance)
        {
            _dragPending = false;                           // 置假，防止 DoDragDrop 返回后重入
            if (_dragItem.DataContext is TaskNode node)
            {
                DbgLog($"DRAG_START id={node.Id} dx={dx:F1} dy={dy:F1}");
                DragDrop.DoDragDrop(_dragItem, node, DragDropEffects.Move);
                DbgLog($"DRAG_END id={node.Id}");
            }
        }
    }

    private void TreeItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragPending) DbgLog("UP cancel (no drag)");
        _dragPending = false;
        _dragItem = null;
    }

    private static void DbgLog(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".branch-task");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private TreeViewItem? _lastHighlight;
    private InsertionAdorner? _adorner;     // 拖拽时的插入位置横杠
    private bool _dropBefore;               // 当前落点是插入到目标项之前(true)还是之后(false)
    private bool _dropAsFirstChild;         // 落点=插入到"展开的目标任务"的首位子节点(true)（而非同级之后）
    private TreeViewItem? _blankDropTarget; // 拖到 TreeView 空白区域时的最近上方目标项（after 插入）

    private void TreeItem_DragOver(object sender, DragEventArgs e)
    {
        var tvi = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (tvi?.DataContext is not TaskNode target) { ClearInsertionLine(); return; }
        if (!e.Data.GetDataPresent(typeof(TaskNode))) { ClearInsertionLine(); return; }
        var src = e.Data.GetData(typeof(TaskNode)) as TaskNode;
        if (src == null || src == target || IsDescendant(src, target)) { ClearInsertionLine(); return; }

        e.Effects = DragDropEffects.Move;

        // 行高亮（浅色），提示当前悬停目标
        if (_lastHighlight != tvi)
        {
            if (_lastHighlight != null) _lastHighlight.Background = System.Windows.Media.Brushes.Transparent;
            _lastHighlight = tvi;
        }
        tvi.Background = System.Windows.Media.Brushes.AliceBlue;

        // 插入位置横杠：按鼠标在行内的上下半区决定插到目标项之前还是之后
        var header = tvi.Template.FindName("HeaderBorder", tvi) as Border;
        if (header != null)
        {
            var pos = e.GetPosition(header);
            _dropBefore = pos.Y < header.ActualHeight / 2;

            // 展开且含子节点时，把"下半区"解释为"成为该任务的首位子节点"而非同级之后，
            // 符合大多数树控件的直觉——拖到展开的任务下方即放进它里面并置顶。
            // 这样展开任务的"下方"与"第一个子任务"不再冲突（两者结果都是成为第一个子任务）。
            bool asFirstChild = !_dropBefore && tvi.IsExpanded && target.Children.Count > 0;
            if (asFirstChild)
            {
                var firstChildHeader = GetFirstChildHeader(tvi);
                if (firstChildHeader != null)
                {
                    _dropAsFirstChild = true;
                    ShowInsertionLine(firstChildHeader, before: true); // 横杠画在第一个子节点上沿，表示将置顶
                    e.Handled = true;
                    return;
                }
                _dropAsFirstChild = false;
                ShowInsertionLine(header, _dropBefore);
            }
            else
            {
                _dropAsFirstChild = false;
                ShowInsertionLine(header, _dropBefore);
            }
        }
        e.Handled = true;
    }

    private void TreeItem_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionLine();
        if (_lastHighlight != null)
        {
            _lastHighlight.Background = System.Windows.Media.Brushes.Transparent;
            _lastHighlight = null;
        }
    }

    /// <summary>TreeView 空白区域兜底：命中不到 TreeViewItem 时仍允许 drop（消除红色禁止符），
    /// 落点解释为「最近上方项的之后」，画插入线供用户预览。</summary>
    private void TreeView_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TaskNode))) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var target = FindNearestItemAbove(e.GetPosition(outlineTree));
        _blankDropTarget = target;
        if (target != null)
        {
            var header = target.Template.FindName("HeaderBorder", target) as Border;
            if (header != null) ShowInsertionLine(header, before: false);
        }
        else
        {
            ClearInsertionLine();
        }
        e.Handled = true;
    }

    /// <summary>TreeView 空白区域 drop：把源节点插入到最近上方项之后。</summary>
    private void TreeView_Drop(object sender, DragEventArgs e)
    {
        if (_dropping) return;
        _dropping = true;
        try
        {
            if (!e.Data.GetDataPresent(typeof(TaskNode))) return;
            var src = e.Data.GetData(typeof(TaskNode)) as TaskNode;
            if (src == null) return;
            var target = _blankDropTarget;
            if (target?.DataContext is TaskNode tgt)
            {
                var srcParent = FindParentInTree(VM.SelectedProject?.Root, src.Id);
                if (srcParent != null) srcParent.Children.Remove(src);
                else VM.SelectedProject?.Root.Children.Remove(src);
                var tgtParent = FindParentInTree(VM.SelectedProject?.Root, tgt.Id);
                var list = tgtParent?.Children ?? VM.SelectedProject?.Root.Children;
                var idx = list.IndexOf(tgt);
                list.Insert(idx + 1, src);
                VM.Save();
                VM.SelectedNode = src;
                _suppressSelectionSync = true;
                VM.RefreshFilteredRoots();
                Dispatcher.BeginInvoke(new Action(() => _suppressSelectionSync = false),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            e.Handled = true;
        }
        finally
        {
            ClearInsertionLine();
            _blankDropTarget = null;
            _dropping = false;
        }
    }

    /// <summary>收集当前可见（已展开）的所有 TreeViewItem，按树形顺序。</summary>
    private static List<TreeViewItem> CollectVisibleItems(ItemsControl parent)
    {
        var list = new List<TreeViewItem>();
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem tvi)
            {
                list.Add(tvi);
                if (tvi.IsExpanded &&
                    tvi.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    list.AddRange(CollectVisibleItems(tvi));
            }
        }
        return list;
    }

    /// <summary>在可见项中找「下边缘在鼠标上方且最靠下」的那一项，作为空白 drop 的 after 目标。</summary>
    private TreeViewItem? FindNearestItemAbove(Point pt)
    {
        TreeViewItem? best = null;
        double bestY = double.MinValue;
        foreach (var item in CollectVisibleItems(outlineTree))
        {
            var header = item.Template.FindName("HeaderBorder", item) as Border;
            if (header == null) continue;
            var bottom = header.TranslatePoint(new Point(0, header.ActualHeight), outlineTree).Y;
            if (bottom <= pt.Y && bottom > bestY)
            {
                bestY = bottom;
                best = item;
            }
        }
        return best;
    }

    /// <summary>在目标行上画一条插入位置横杠（蓝色），标明松手后任务将插入到该行上沿或下沿。</summary>
    private void ShowInsertionLine(Border header, bool before)
    {
        var layer = AdornerLayer.GetAdornerLayer(header);
        if (layer == null) return;
        if (_adorner != null) { layer.Remove(_adorner); _adorner = null; }
        _adorner = new InsertionAdorner(header, before);
        layer.Add(_adorner);
    }

    private void ClearInsertionLine()
    {
        if (_adorner != null)
        {
            var layer = AdornerLayer.GetAdornerLayer(_adorner.AdornedElement as Visual);
            layer?.Remove(_adorner);
            _adorner = null;
        }
    }

    private void TreeItem_Drop(object sender, DragEventArgs e)
    {
        // 防重入：WPF 路由事件可能沿 visual tree 多次触发 Drop
        if (_dropping) return;
        _dropping = true;

        try
        {
            var tvi = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (tvi?.DataContext is not TaskNode target) return;
            if (!e.Data.GetDataPresent(typeof(TaskNode))) return;
            var src = e.Data.GetData(typeof(TaskNode)) as TaskNode;
            if (src == null || src == target || IsDescendant(src, target)) return;

            var srcParent = FindParentInTree(VM.SelectedProject?.Root, src.Id);
            if (srcParent != null)
                srcParent.Children.Remove(src);
            else
                VM.SelectedProject?.Root.Children.Remove(src);

            if (_dropAsFirstChild)
            {
                // 落到展开任务的首位子节点：插到该任务 Children 的最前面（与横杠位置一致）
                target.Children.Insert(0, src);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] DROP src={src.Id} -> first child of {target.Id}");
            }
            else
            {
                var tgtParent = FindParentInTree(VM.SelectedProject?.Root, target.Id);
                var list = tgtParent?.Children ?? VM.SelectedProject?.Root.Children;
                var idx = list.IndexOf(target);
                // 与拖拽时显示的插入横杠一致：上半区→之前，下半区→之后
                var insertAt = _dropBefore ? idx : idx + 1;
                list.Insert(insertAt, src);
                Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] DROP src={src.Id} -> {( _dropBefore ? "before" : "after" )} {target.Id}");
            }
            VM.Save();
            VM.SelectedNode = src;           // 保持拖拽节点选中，避免跳到父节点
            _suppressSelectionSync = true;   // 抑制重建期间的选中事件冒泡
            // 关键修复：拖拽改的是底层模型(Root.Children)，但 TreeView 绑定的是一次性快照 FilteredRoots，
            // 且 Children 是普通 List 不通知 UI。必须重建显示树，否则旧位置残留 + 冒泡重复移动 → 看起来"复制了一个任务"。
            VM.RefreshFilteredRoots();
            // 延迟解除抑制，等 TreeView 容器重建完成
            Dispatcher.BeginInvoke(new Action(() => _suppressSelectionSync = false),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            e.Handled = true;                 // 阻止 Drop 沿 visual tree 冒泡到祖先项再次触发移动
        }
        finally
        {
            // 落点后清除插入横杠与行高亮
            ClearInsertionLine();
            if (_lastHighlight != null)
            {
                _lastHighlight.Background = System.Windows.Media.Brushes.Transparent;
                _lastHighlight = null;
            }
            _dropAsFirstChild = false;
            _dropping = false;
        }
    }

    private static bool IsDescendant(TaskNode ancestor, TaskNode node)
    {
        foreach (var child in ancestor.Children)
        {
            if (child == node || IsDescendant(child, node))
                return true;
        }
        return false;
    }

    private static TaskNode? FindParentInTree(TaskNode? root, string childId)
    {
        if (root == null) return null;
        foreach (var child in root.Children)
        {
            if (child.Id == childId) return root;
            var f = FindParentInTree(child, childId);
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>取展开 TreeViewItem 的第一个子项行（HeaderBorder），用于在"成为首位子节点"时绘制插入横杠。</summary>
    private static Border? GetFirstChildHeader(TreeViewItem tvi)
    {
        if (tvi.Items.Count == 0) return null;
        var childItem = tvi.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
        if (childItem == null) return null;
        return childItem.Template.FindName("HeaderBorder", childItem) as Border;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        if (child == null) return null;
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }

    /// <summary>在子视觉树中递归查找第一个指定类型的元素</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ===== 行内重命名 =====
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // 程序化刷新(TreeView 重建)期间不反向覆盖 VM 选中，避免 WPF 复位选中到第一项
        // 两层守卫：View 层 _suppressSelectionSync（FilteredRoots 变化触发）+ VM 层 SuppressSelectionSync（AddChild/AddSibling 触发）
        if (_suppressSelectionSync || VM.SuppressSelectionSync) return;
        if (e.NewValue is TaskNode node)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Tree_SelectedItemChanged id={node.Id} title={node.Title}");
            VM.OnSelectionChanged(node);
            RefreshTreeLines();   // 数据变化（增删子节点后选中）后重算连接线
        }
    }

    /// <summary>兜底：把 TreeView 的 UI 选中强制对齐到 VM.SelectedNode。
    /// 在 SelectedNode 变化后的 ContextIdle 阶段调用（此时重建的容器已生成），
    /// 确保即使 WPF 在替换 ItemsSource 时复位了 SelectedItem，也能恢复正确选中。</summary>
    private void EnsureSelection()
    {
        if (VM.SelectedNode == null) return;
        var item = FindTreeViewItem(outlineTree, VM.SelectedNode);
        if (item != null && !item.IsSelected)
        {
            item.IsSelected = true;
            try { item.BringIntoView(); } catch { }
        }
    }

    /// <summary>新建任务后自动进入就地重命名：定位节点容器，隐藏标题 TextBlock、显示编辑框并全选。</summary>
    private void BeginEditNode(TaskNode node)
    {
        var tvi = FindTreeViewItem(outlineTree, node);
        if (tvi == null) return;
        var tb = FindVisualChild<TextBlock>(tvi);
        var box = FindVisualChild<TextBox>(tvi);
        if (tb != null && box != null)
        {
            tb.Visibility = Visibility.Collapsed;
            box.Visibility = Visibility.Visible;
            box.Focus();
            box.SelectAll();
        }
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, TaskNode target)
    {
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is TreeViewItem tvi)
            {
                if (tvi.DataContext == target) return tvi;
                if (tvi.IsExpanded &&
                    tvi.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                {
                    var found = FindTreeViewItem(tvi, target);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }
    private void TaskTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is TextBlock tb && tb.Parent is Grid grid)
        {
            // 在 Grid 中找到 TextBox（第二个子元素）
            foreach (var child in grid.Children)
            {
                if (child is TextBox tBox)
                {
                    tb.Visibility = Visibility.Collapsed;
                    tBox.Visibility = Visibility.Visible;
                    tBox.Focus();
                    tBox.SelectAll();
                    break;
                }
            }
        }
    }

    /// <summary>大纲任务右键菜单：创建子任务 / 创建兄任务 / 展开·收起 / 删除任务。</summary>
    private void TaskItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        // 任务右键菜单：整行任意位置（含圆点）都弹任务菜单，避免用户右键圆点时看到的是状态菜单而找不到"删除任务"。
        // 圆点的状态选择菜单改为仅左键触发（StatusDot_MouseUp）。
        if (sender is not FrameworkElement fe || fe.DataContext is not TaskNode node) return;
        VM.SelectedNode = node;   // 右键先选中，保证菜单命令作用于该节点

        // [diag] 记录任务右键触发（区分项目右键 menu_diag）
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".branch-task", "menu_diag.log"),
                $"=== {DateTime.Now:HH:mm:ss.fff} === TASKRIGHT: '{node.Title}'({node.Id})\n");
        }
        catch { }

        var menu = new ContextMenu { PlacementTarget = fe };

        var miChild = new MenuItem { Header = "创建子任务" };
        miChild.Click += (_, _) =>
        {
            if (VM.AddChildCommand.CanExecute(null)) VM.AddChildCommand.Execute(null);
        };
        menu.Items.Add(miChild);

        var miSibling = new MenuItem { Header = "创建兄任务" };
        miSibling.Click += (_, _) =>
        {
            if (VM.AddSiblingCommand.CanExecute(null)) VM.AddSiblingCommand.Execute(null);
        };
        menu.Items.Add(miSibling);

        // 重命名 — 触发大纲树对应行的就地编辑
        var grid2 = fe as Grid;
        var miRename = new MenuItem { Header = "重命名" };
        miRename.Click += (_, _) =>
        {
            var g = grid2;
            if (g == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TextBlock? tBlock = null; TextBox? tBox = null;
                foreach (var child in g.Children)
                {
                    if (child is TextBlock b) tBlock = b;
                    else if (child is TextBox tb) tBox = tb;
                }
                if (tBlock != null && tBox != null)
                {
                    tBlock.Visibility = Visibility.Collapsed;
                    tBox.Visibility = Visibility.Visible;
                    tBox.Focus();
                    tBox.SelectAll();
                }
            }));
        };
        menu.Items.Add(miRename);

        var miToggle = new MenuItem { Header = VM.IsCollapsed(node.Id) ? "展开任务" : "收起任务" };
        miToggle.Click += (_, _) => VM.ToggleCollapse(node.Id);
        menu.Items.Add(miToggle);

        var miDelete = new MenuItem { Header = "删除任务" };
        miDelete.Click += (_, _) =>
        {
            // 彻底修复(btwpf88)：MenuItem.Click 里同步弹 MessageBox(模态) 会与 ContextMenu 关闭时序冲突，
            // 导致 Click 处理中断 → 删除从未执行。改为 Dispatcher.BeginInvoke 延迟，等菜单关闭后再确认+删除。
            var targetId = node.Id;
            var targetTitle = node.Title;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MessageBox.Show($"确定删除「{targetTitle}」及其全部子任务？", "删除任务",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    VM.DeleteNodeById(targetId);
                }
            }));
        };
        menu.Items.Add(miDelete);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void TaskTitle_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tBox && tBox.Parent is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb)
                {
                    tBox.Visibility = Visibility.Collapsed;
                    tb.Visibility = Visibility.Visible;
                    VM.Save();
                    break;
                }
            }
        }
    }

    private void TaskTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tBox && tBox.Parent is Grid grid)
        {
            // 显式提交：UpdateSource 确保绑定写回，隐藏编辑框 + 保存
            tBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb) tb.Visibility = Visibility.Visible;
            }
            tBox.Visibility = Visibility.Collapsed;
            VM.Save();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox tBox2 && tBox2.Parent is Grid grid2)
        {
            tBox2.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            foreach (var child in grid2.Children)
                if (child is TextBlock tb) tb.Visibility = Visibility.Visible;
            tBox2.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    /// <summary>详情面板单行 TextBox 回车提交。
    /// 多行文本框(任务信息, AcceptsReturn=True)不绑此处理，回车保持换行。</summary>
    private void TaskField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tBox && !tBox.AcceptsReturn)
        {
            tBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            SyncRootTitleToProject();
            VM.Save();
            // 回车后移动焦点 → 输入框退出编辑态，视觉上"完成了"重命名
            tBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private void StatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || VM.SelectedNode == null) return;
        if (StatusCombo.SelectedItem is ComboBoxItem item && item.Tag is string status)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] StatusCombo SET {status}");
            VM.SelectedNode.Status = status;
            VM.Save();
        }
    }

    /// <summary>大纲圆点点击：弹出状态选择框。每个 TreeViewItem 模板实例内都有独立 Popup，
    /// 通过圆点的视觉父级 Grid 找到同实例的 Popup 打开。</summary>
    private void StatusDot_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 仅左键触发状态菜单；右键交给任务菜单（TaskItem_RightClick）
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is System.Windows.Shapes.Ellipse el && el.DataContext is TaskNode node)
        {
            _popupNode = node;
            if (el.Parent is Grid g)
            {
                var popup = g.Children.OfType<Popup>().FirstOrDefault();
                if (popup != null)
                {
                    _popupRef = popup;
                    // 高亮当前状态项由按钮 Foreground 体现；这里仅打开
                    popup.IsOpen = true;
                }
            }
            VM.SelectedNode = node;   // 同步详情页（选中该任务）
            e.Handled = true;
        }
    }

    /// <summary>状态选择框内点击某项：设置状态并关闭弹框。</summary>
    private void StatusPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string status)
        {
            // Popup 内容在独立视觉树，无法用 FindVisualParent 回溯；直接用字段 _popupNode
            if (_popupNode != null)
            {
                _popupNode.Status = status;
                VM.SelectedNode = _popupNode;
                VM.Save();
            }
            // 关闭当前打开的 Popup（StaysOpen=False 点击外部会自动关，但点选项属内部点击不会自动关）
            if (_popupRef != null) _popupRef.IsOpen = false;
            _popupNode = null;
            _popupRef = null;
        }
    }

    private void ToggleLeftPanel(object sender, RoutedEventArgs e)
        => VM.LeftPanelOpen = !VM.LeftPanelOpen;

    /// <summary>收起态缩略图色块点击：切换到对应项目（参考老版本 left_open_btn 的彩色小色块）。</summary>
    private void ProjectTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Project p)
            VM.SelectedProject = p;
    }

    // ── 项目拖拽排序 ──
    private System.Windows.Point _projDragStart;
    private Project? _projDragItem;
    private TextBox? _editingProjectBox;   // 当前正在编辑的项目名 TextBox，点击空白时强制提交

    private void ProjectList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 如果正在编辑项目名且点在其他地方 → 强制提交
        if (_editingProjectBox != null && !_editingProjectBox.IsKeyboardFocusWithin)
        {
            if (_editingProjectBox.Parent is Grid g)
            {
                SyncProjectTitle(_editingProjectBox, g);
                VM.Save();
                _editingProjectBox = null;
            }
        }

        _projDragStart = e.GetPosition(null);
        // 关键修复(btwpf86)：FindVisualParent 用 VisualTreeHelper.GetParent，对 TextBlock 内部 Run(ContentElement)
        // 回溯失败 → _projDragItem 永远为 null → 拖不动。ContainerFromElement 内部走逻辑树，对 Run 也有效。
        if (sender is ListBox lb)
        {
            var item = lb.ContainerFromElement(e.OriginalSource as DependencyObject) as ListBoxItem;
            _projDragItem = item?.DataContext as Project;
        }
        else
        {
            _projDragItem = null;
        }
    }

    private void ProjectList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _projDragItem == null) return;
        var diff = _projDragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var data = new DataObject(typeof(Project), _projDragItem);
            DragDrop.DoDragDrop(sender as DependencyObject, data, DragDropEffects.Move);
            _projDragItem = null;
        }
    }

    private void ProjectList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Project))) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ProjectList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Project))) return;
        var dragged = e.Data.GetData(typeof(Project)) as Project;
        if (dragged == null) return;

        var listBox = sender as ListBox;
        if (listBox == null) return;
        var hit = listBox.InputHitTest(e.GetPosition(listBox)) as DependencyObject;
        // 与按下时一致：ContainerFromElement 对 TextBlock/Run 有效
        var targetItem = listBox.ContainerFromElement(hit) as ListBoxItem;
        var target = targetItem?.DataContext as Project;
        if (target == null || target == dragged) return;

        int oldIdx = VM.Projects.IndexOf(dragged);
        int newIdx = VM.Projects.IndexOf(target);
        if (oldIdx < 0 || newIdx < 0) return;
        VM.MoveProject(oldIdx, newIdx);
    }

    // ── 拖拽排序结束 ──

    private void ToggleRightPanel(object sender, RoutedEventArgs e)
        => VM.RightPanelOpen = !VM.RightPanelOpen;

    /// <summary>详情页顶部「详情 / 库」标签切换</summary>
    private void DetailTab_Click(object sender, RoutedEventArgs e)
    {
        var isLibrary = sender is Button btn && btn.Tag as string == "library";
        DetailTabPanel.Visibility = isLibrary ? Visibility.Collapsed : Visibility.Visible;
        LibraryTabPanel.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        var blue = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x7B, 0xE5));
        if (DetailTabBtn != null)
        {
            DetailTabBtn.FontWeight = isLibrary ? FontWeights.Normal : FontWeights.Bold;
            DetailTabBtn.Foreground = isLibrary ? System.Windows.Media.Brushes.Gray : blue;
        }
        if (LibraryTabBtn != null)
        {
            LibraryTabBtn.FontWeight = isLibrary ? FontWeights.Bold : FontWeights.Normal;
            LibraryTabBtn.Foreground = isLibrary ? blue : System.Windows.Media.Brushes.Gray;
        }
        if (isLibrary) VM.RefreshLibrary();   // 切到库时刷新分组
    }

    /// <summary>库页删除文件</summary>
    private void LibraryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is IntermediateEntry im)
        {
            if (MessageBox.Show($"确定从库中删除「{im.Title}」？磁盘上的文件默认保留。", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                VM.DeleteFileById(im.Id);
            }
        }
    }

    private void ToggleColorByLevel_Click(object sender, RoutedEventArgs e)
    {
        VM.ColorByLevel = !VM.ColorByLevel;
        // Style Setter 里的背景 MultiBinding 对已生成的 TreeViewItem 不会在源变化时自动刷新，
        // 重建整棵显示树强制所有容器重新求值（选中经 VM.SelectedNode 保持）。
        VM.RefreshFilteredRoots();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        VM.SearchText = "";
        SearchTextBox?.Focus();
    }

    /// <summary>右栏拖拽手柄结束拖拽：把实际像素宽存回 VM（供再次展开恢复），
    /// 并重建列宽绑定——GridSplitter 会改写 ColumnDefinition 的本地 Width 值，覆盖原绑定，
    /// 不重建会导致后续「收起」失效。</summary>
    private void RightSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (RightCol != null)
        {
            if (RightCol.Width.IsAbsolute)
                VM.RightPanelWidth = new GridLength(Math.Clamp(RightCol.Width.Value, 240, 680));
            RightCol.SetBinding(ColumnDefinition.WidthProperty, new Binding("RightColumnWidth"));
        }
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Project proj)
        {
            if (MessageBox.Show($"确定删除项目「{proj.Name}」？", "删除项目",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                VM.DeleteProject(proj);
        }
    }

    // 列表项右键：手动弹出菜单（用被点击元素自身算屏幕坐标，避开窗口标题栏偏移）
    private void ProjectItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Project proj) return;
        ShowProjectContextMenu(proj, fe, e);
        e.Handled = true;
    }

    /// <summary>双击项目名 → 就地编辑</summary>
    private void ProjectTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is TextBlock tBlock && tBlock.Parent is Grid grid)
        {
            var tb = grid.Children.OfType<TextBox>().FirstOrDefault();
            if (tb != null)
            {
                tBlock.Visibility = Visibility.Collapsed;
                tb.Visibility = Visibility.Visible;
                tb.Focus();
                tb.SelectAll();
                _editingProjectBox = tb;
            }
        }
    }

    /// <summary>项目名编辑失焦 → 保存</summary>
    private void ProjectTitle_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Parent is Grid grid)
        {
            SyncProjectTitle(tb, grid);
            VM.Save();
            _editingProjectBox = null;
        }
    }

    /// <summary>项目名编辑键盘：Enter 保存，Esc 取消</summary>
    private void ProjectTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb && tb.Parent is Grid grid)
        {
            SyncProjectTitle(tb, grid);
            VM.Save();
            _editingProjectBox = null;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox tb2 && tb2.Parent is Grid grid2)
        {
            var tBlock = grid2.Children.OfType<TextBlock>().FirstOrDefault();
            tb2.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            if (tBlock != null) tBlock.Visibility = Visibility.Visible;
            tb2.Visibility = Visibility.Collapsed;
            _editingProjectBox = null;
            e.Handled = true;
        }
    }

    /// <summary>同步 Project.Name 到 Root.Title，并恢复 TextBlock 可见</summary>
    private static void SyncProjectTitle(TextBox tb, Grid grid)
    {
        var tBlock = grid.Children.OfType<TextBlock>().FirstOrDefault();
        if (tBlock != null) tBlock.Visibility = Visibility.Visible;
        tb.Visibility = Visibility.Collapsed;
        if (tb.DataContext is Project proj)
            proj.Root.Title = proj.Name;
    }

    // 缩略图块右键
    private void ProjectTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        // 缩略图块是 Button，DataContext 可能没传上来，从父 Grid 取
        var fe = sender as FrameworkElement;
        var proj = fe?.DataContext as Project
            ?? (fe?.Parent as FrameworkElement)?.DataContext as Project;
        if (proj == null) return;
        VM.SelectedProject = proj;
        ShowProjectContextMenu(proj, fe, e);
        e.Handled = true;
    }

    // 手动在鼠标位置弹出右键菜单
    // btwpf52~57：历经 XAML Placement / Absolute / Relative / Custom 各种方案，最终发现
    //   WPF 的 Popup 内置【屏幕边界碰撞自动翻转】——即便只返回单一放置点，只要该点算出的菜单
    //   矩形会溢出屏幕，WPF 仍会把菜单镜像翻到对侧（横向翻到光标左边）。PopupPrimaryAxis 只控制
    //   优先翻哪个轴，关不掉这个翻转 → 位置随可用空间飘忽（左边有空=箭头正下；左边没空=翻到右下/左下）。
    // btwpf58 根治：自己把放置点【夹取在屏幕工作区内】，返回的唯一候选点永远在屏内，
    //   WPF 便无任何理由翻转 → 菜单永远紧贴光标、向右下方展开，绝不再跳到左边或上面。
    private void ShowProjectContextMenu(Project proj, FrameworkElement fe, MouseButtonEventArgs e)
    {
        VM.SelectedProject = proj;
        var rel = e.GetPosition(fe);   // 鼠标相对于被点击元素的位置 (DIP)
        var feOrigin = fe.PointToScreen(new System.Windows.Point(0, 0)); // fe 原点屏幕坐标（设备像素）
        var cursorDP = fe.PointToScreen(rel);  // 鼠标屏幕坐标（设备像素）

        // ---- 诊断日志 ----
        var diag = new System.Text.StringBuilder();
        diag.AppendLine($"=== {DateTime.Now:HH:mm:ss.fff} ===");
        diag.AppendLine($"fe  : type={fe.GetType().Name}  size=({fe.ActualWidth:F0},{fe.ActualHeight:F0})");
        diag.AppendLine($"rel : ({rel.X:F0},{rel.Y:F0})");
        diag.AppendLine($"feScreen(0,0)=({feOrigin.X:F0},{feOrigin.Y:F0})   cursorScreen=({cursorDP.X:F0},{cursorDP.Y:F0})");
        try { System.IO.File.AppendAllText(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".branch-task", "menu_diag.log"),
            diag.ToString()); } catch { }
        // ------------------

        var menu = new ContextMenu
        {
            PlacementTarget = fe,
            Placement = PlacementMode.Custom
        };
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        {
            // btwpf63 根因修复：DPI 坐标单位不一致。
            //   你的显示器 150% DPI → PointToScreen() 把 DIP ×1.5 = 设备像素，targetSize 也是设备像素，
            //   但 CustomPopupPlacementCallback 返回的 Point 被 WPF 直接当成设备像素用（不转换）。
            //   所以返回 (rel.X+20, rel.Y+10) 在 DIP 是对的，在设备像素里偏小了：rel.X 越大越偏左。
            //   修复：返回设备像素偏移 = 鼠标相对 fe 的设备像素偏移 + 右侧/下侧 padding
            var rightPad = 6.0;   // 6 设备像素 ≈ 4 DIP @150%，紧贴光标右侧
            var downPad = 6.0;    // 6 设备像素 ≈ 4 DIP @150%，紧贴光标下方
            var p = new System.Windows.Point(
                (cursorDP.X - feOrigin.X) + rightPad,   // 光标偏移（设备像素）+ 向右 padding
                (cursorDP.Y - feOrigin.Y) + downPad);    // 光标偏移（设备像素）+ 向下 padding
            // ---- 回调诊断日志 ----
            var cd = new System.Text.StringBuilder();
            cd.AppendLine($"  cb: popupSize=({popupSize.Width:F0},{popupSize.Height:F0})  targetSize=({targetSize.Width:F0},{targetSize.Height:F0})  offset=({offset.X:F0},{offset.Y:F0})");
            cd.AppendLine($"  return=({p.X:F0},{p.Y:F0})  cursorOffsetDP=({cursorDP.X-feOrigin.X:F0},{cursorDP.Y-feOrigin.Y:F0})  PrimaryAxis=Vertical");
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".branch-task", "menu_diag.log"),
                cd.ToString()); } catch { }
            // ------------------
            return new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(
                p, System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical) };
        };

        // 重命名项目 — 触发就地编辑（不弹框）
        var miRename = new MenuItem { Header = "重命名项目" };
        miRename.Click += (_, _) =>
        {
            var target = proj;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var item = _projectListBox.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem;
                if (item == null) return;
                var tb = FindVisualChild<TextBox>(item);
                var tBlock = FindVisualChild<TextBlock>(item);
                if (tb != null && tBlock != null)
                {
                    tBlock.Visibility = Visibility.Collapsed;
                    tb.Visibility = Visibility.Visible;
                    tb.Focus();
                    tb.SelectAll();
                    _editingProjectBox = tb;
                }
            }));
        };
        menu.Items.Add(miRename);

        var mi = new MenuItem { Header = "删除项目" };
        mi.Click += (_, _) =>
        {
            // 彻底修复(btwpf88)：同任务菜单，Dispatcher.BeginInvoke 延迟，避免模态 MessageBox 中断菜单关闭
            var target = proj;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MessageBox.Show($"确定删除项目「{target.Name}」？", "删除项目",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    VM.DeleteProject(target);
            }));
        };
        menu.Items.Add(mi);
        menu.IsOpen = true;
    }

    private void TaskField_LostFocus(object sender, RoutedEventArgs e) { SyncRootTitleToProject(); VM.Save(); }

    /// <summary>如果详情页编辑的是根节点（项目名），同步到 Project.Name 让项目列表刷新。</summary>
    private void SyncRootTitleToProject()
    {
        if (VM.SelectedNode != null && VM.SelectedProject != null
            && VM.SelectedNode == VM.SelectedProject.Root)
            VM.SelectedProject.Name = VM.SelectedNode.Title;
    }

    /// <summary>导出 MD：弹保存对话框选路径，再写入。</summary>
    private void ExportMd_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedProject == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "导出 Markdown",
            FileName = $"{VM.SelectedProject.Name}.md",
            Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
            DefaultExt = ".md",
            AddExtension = true
        };
        if (dlg.ShowDialog() == true)
            VM.ExportMarkdownTo(dlg.FileName);
    }

    private void AddProjIM_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedProject == null) return;
        if (sender is Button btn && btn.Tag is string kind)
        {
            string? filePath = null;
            string? link = null;
            if (kind == "file")
            {
                var dlg = new OpenFileDialog { Title = "选择文件" };
                if (dlg.ShowDialog() != true) return;
                var wsDir = DataService.GetWorkspaceDir(VM.SelectedProject.Id, "project");
                filePath = DataService.CopyToWorkspace(dlg.FileName, wsDir);
            }
            else if (kind == "link")
            {
                link = "https://";  // 占位 URL，用户可在详情面板编辑
            }

            VM.SelectedProject.Intermediates.Add(new IntermediateEntry
            {
                Kind = kind,
                Title = kind switch { "text" => "新文字", "file" => Path.GetFileName(filePath ?? "未选择"), _ => "新链接" },
                Content = kind == "text" ? "" : "",
                FilePath = filePath,
                Link = link,
                Created = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            VM.Save();
        }
    }

    private void AddTaskIM_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedNode == null || VM.SelectedProject == null) return;
        if (sender is Button btn && btn.Tag is string kind)
        {
            string? filePath = null;
            string? link = null;
            if (kind == "file")
            {
                var dlg = new OpenFileDialog { Title = "选择文件" };
                if (dlg.ShowDialog() != true) return;
                var wsDir = DataService.GetWorkspaceDir(VM.SelectedProject.Id, VM.SelectedNode.Id);
                filePath = DataService.CopyToWorkspace(dlg.FileName, wsDir);
            }
            else if (kind == "link")
            {
                link = "https://";
            }

            VM.SelectedNode.Intermediates.Add(new IntermediateEntry
            {
                Kind = kind,
                Title = kind switch { "text" => "新文字", "file" => Path.GetFileName(filePath ?? "未选择"), _ => "新链接" },
                Content = kind == "text" ? "" : "",
                FilePath = filePath,
                Link = link,
                Created = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            VM.Save();
        }
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"无法打开链接: {ex.Message}"); }
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path && System.IO.File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"无法打开文件: {ex.Message}"); }
        }
    }

    // ===== 思维导图缩放 =====
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => MindmapCanvas.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => MindmapCanvas.ZoomOut();
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => MindmapCanvas.ZoomReset();

    private void DeleteIM_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is IntermediateEntry im)
        {
            var msg = im.Kind switch
            {
                "file" => $"确定删除「{im.Title}」？文件在磁盘上默认保留。",
                _      => $"确定删除「{im.Title}」？"
            };
            if (MessageBox.Show(msg, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                VM.SelectedNode?.Intermediates.Remove(im);
                VM.SelectedProject?.Intermediates.Remove(im);
                VM.Save();
            }
        }
    }

    // ===== 左侧工具栏按钮 =====
    private void LeftFilterBtn_Click(object sender, RoutedEventArgs e)
    {
        FilterPopup.IsOpen = !FilterPopup.IsOpen;
        // 定位到按钮附近
        if (sender is Button btn)
        {
            FilterPopup.PlacementTarget = btn;
            FilterPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
    }

    private void FilterStatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterStatusCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            VM.FilterStatus = tag;
        else
            VM.FilterStatus = null;  // "全部状态"
        VM.RefreshFilteredRoots();
    }

    private void FilterTimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterTimeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            VM.FilterTime = tag;
        else
            VM.FilterTime = null;  // "全部时间"
        VM.RefreshFilteredRoots();
    }

    private void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterStatusCombo.SelectedIndex = 0;
        FilterTimeCombo.SelectedIndex = 0;
        VM.FilterStatus = null;
        VM.FilterTime = null;
        VM.RefreshFilteredRoots();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        VM.Save();
        base.OnClosing(e);
    }

    /// <summary>拖拽插入位置指示横杠：在目标行上沿(before)或下沿(after)画一条蓝色横线 + 左端小圆点。</summary>
    private class InsertionAdorner : Adorner
    {
        private readonly bool _before;
        public InsertionAdorner(UIElement adornedElement, bool before) : base(adornedElement) => _before = before;

        protected override void OnRender(DrawingContext dc)
        {
            if (AdornedElement is not UIElement e) return;
            double w = e.RenderSize.Width;
            double h = e.RenderSize.Height;
            double y = _before ? 1 : Math.Max(1, h - 1);
            var brush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)); // 蓝
            var pen = new Pen(brush, 2.5);
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            dc.DrawEllipse(brush, null, new Point(3, y), 3, 3); // 左端圆点，标明插入方向
        }
    }

}

