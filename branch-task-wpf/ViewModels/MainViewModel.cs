using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BranchTaskWpf.Models;
using BranchTaskWpf.Services;

namespace BranchTaskWpf.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private Store _store = new();
    private Project? _selectedProject;
    private TaskNode? _selectedNode;
    private string _searchText = "";
    private string? _filterStatus;   // null=全部, "in_progress"/"done"/"blocked"/""
    private string? _filterTime;     // null=全部, "today"/"week"/"month"

    // ===== UI 状态 =====
    private GridLength _rightPanelWidth = new(360);
    private bool _rightPanelOpen = true;
    private bool _leftPanelOpen = true;
    private bool _colorByLevel = true;   // 大纲按层级着色（默认开，呼应"层级分色显示"需求）
    private string _exportMessage = "";
    private bool _isRenamingProject;

    public MainViewModel()
    {
        _store = DataService.Load();
        // [diag] 记录构造时 Load 结果
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".branch-task", "vm_diag.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] VM Ctor: _store has {_store.Projects.Count} proj, names=[{string.Join(", ", _store.Projects.Select(p => p.Name))}]\n"); } catch {}
        // 清理旧版(btwpf14 拖拽未彻底防重入)可能遗留的重复节点：同一 Id 出现两次说明数据损坏，
        // 保留首次出现、移除后续重复，避免"一个任务显示两处 / 选中一个全树高亮"。
        foreach (var p in _store.Projects)
            DedupeTree(p.Root);
        Projects = new ObservableCollection<Project>(_store.Projects);

        // 恢复 UI 状态（当前项目 / 面板展开 / 层级配色）
        var ui = DataService.LoadUiState();
        _leftPanelOpen = ui.LeftPanelOpen;
        _rightPanelOpen = ui.RightPanelOpen;
        _colorByLevel = ui.ColorByLevel;
        if (ui.CurrentProjectId != null)
            SelectedProject = _store.Projects.FirstOrDefault(p => p.Id == ui.CurrentProjectId);
        if (SelectedProject == null && _store.Projects.Count > 0)
            SelectedProject = _store.Projects[0];

        AddProjectCommand = new RelayCommand(AddProject);
        AddChildCommand = new RelayCommand(AddChild, () => SelectedNode != null);
        AddSiblingCommand = new RelayCommand(AddSibling, () => SelectedNode != null);
        DeleteNodeCommand = new RelayCommand(DeleteNode, () => SelectedNode != null);
        ExportMarkdownCommand = new RelayCommand(ExportMarkdown, () => SelectedProject != null);
    }

    // ===== Observable 属性 =====
    public ObservableCollection<Project> Projects { get; private set; } = new();

    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (_selectedProject == value) return;
            Dbg("SelectedProject SET", value?.Name ?? "null");
            _selectedProject = value;
            // 关键修复（btwpf77）：切换项目必须重置选中任务，否则详情面板仍显示上一个项目的任务 → 信息串。
            // 与 AddProject 行为一致：默认选中新项目根节点，详情显示项目名+项目里程碑。
            if (_selectedNode != null) _selectedNode.IsSelected = false;
            _selectedNode = value?.Root;
            if (_selectedNode != null) _selectedNode.IsSelected = true;
            OnPropertyChanged(nameof(SelectedNode));
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            RefreshFilteredRoots();
            RefreshLibrary();
            SaveUiState();
        }
    }

    // ===== 库（项目文件汇总）=====
    public ObservableCollection<LibraryGroup> LibraryGroups { get; } = new();

    /// <summary>重建库分组：公共区（项目级文件）+ 按任务遍历（含主线 root）</summary>
    public void RefreshLibrary()
    {
        LibraryGroups.Clear();
        if (SelectedProject == null) return;

        var projFiles = SelectedProject.Intermediates.Where(i => i.Kind == "file").ToList();
        if (projFiles.Count > 0)
            LibraryGroups.Add(new LibraryGroup { Title = "公共区（项目共有）", Items = projFiles });

        void Walk(TaskNode node)
        {
            var files = node.Intermediates.Where(i => i.Kind == "file").ToList();
            if (files.Count > 0)
                LibraryGroups.Add(new LibraryGroup { Title = node.Title, Items = files });
            foreach (var c in node.Children) Walk(c);
        }
        Walk(SelectedProject.Root);
    }

    /// <summary>按 id 递归删除一个文件中间结果（库页删除用），返回是否删除成功</summary>
    public bool DeleteFileById(string imId)
    {
        if (SelectedProject == null) return false;
        var pim = SelectedProject.Intermediates.FirstOrDefault(i => i.Id == imId);
        if (pim != null)
        {
            SelectedProject.Intermediates.Remove(pim);
            Save();
            RefreshLibrary();
            return true;
        }
        TaskNode? holder = null;
        bool Found(TaskNode n)
        {
            if (n.Intermediates.Any(i => i.Id == imId)) { holder = n; return true; }
            foreach (var c in n.Children) if (Found(c)) return true;
            return false;
        }
        if (Found(SelectedProject.Root) && holder != null)
        {
            var im = holder.Intermediates.First(i => i.Id == imId);
            holder.Intermediates.Remove(im);
            Save();
            RefreshLibrary();
            return true;
        }
        return false;
    }

    public TaskNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value) return;
            // 维护单选不变式：旧项取消、新项选中。XAML 中 IsSelected 为 OneWay(模型→UI)，
            // 所以只有经此 setter 才能置 true，杜绝 UI 直接写回导致的多节点 IsSelected 累积（"全选"）。
            if (_selectedNode != null) _selectedNode.IsSelected = false;
            _selectedNode = value;
            if (_selectedNode != null) _selectedNode.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedProject != null;

    /// <summary>供 View 层读取：AddChild/AddSibling 期间为 true，阻止 Tree_SelectedItemChanged 覆盖选中。</summary>
    public bool SuppressSelectionSync { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); Dbg("SearchText SET", value); RefreshFilteredRoots(); }
    }

    public GridLength RightPanelWidth
    {
        get => _rightPanelWidth;
        set { _rightPanelWidth = value; OnPropertyChanged(); OnPropertyChanged(nameof(RightColumnWidth)); }
    }

    /// <summary>右栏实际列宽：展开态用 RightPanelWidth，收起态收为 24px 选项卡。</summary>
    public GridLength RightColumnWidth => RightPanelOpen ? _rightPanelWidth : new GridLength(0);

    public bool RightPanelOpen
    {
        get => _rightPanelOpen;
        set { _rightPanelOpen = value; OnPropertyChanged(); OnPropertyChanged(nameof(RightColumnWidth)); SaveUiState(); }
    }

    /// <summary>大纲是否按层级着色（同一层级同色、不同层级异色）。</summary>
    public bool ColorByLevel
    {
        get => _colorByLevel;
        set { _colorByLevel = value; OnPropertyChanged(); SaveUiState(); }
    }

    public bool LeftPanelOpen
    {
        get => _leftPanelOpen;
        set { _leftPanelOpen = value; OnPropertyChanged(); SaveUiState(); }
    }

    public string ExportMessage
    {
        get => _exportMessage;
        set { _exportMessage = value; OnPropertyChanged(); }
    }

    public bool IsRenamingProject
    {
        get => _isRenamingProject;
        set { _isRenamingProject = value; OnPropertyChanged(); }
    }

    public string? FilterStatus
    {
        get => _filterStatus;
        set { _filterStatus = value; OnPropertyChanged(); }
    }

    public string? FilterTime
    {
        get => _filterTime;
        set { _filterTime = value; OnPropertyChanged(); }
    }

    // ===== 筛选后的根节点列表（搜索 + 状态 + 时间筛选）=====
    private ObservableCollection<TaskNode> _filteredRoots = new();
    public ObservableCollection<TaskNode> FilteredRoots
    {
        get => _filteredRoots;
        private set { _filteredRoots = value; OnPropertyChanged(); }
    }

    /// <summary>公开方法，供 code-behind 筛选按钮调用</summary>
    public void RefreshFilteredRoots()
    {
        Dbg("RefreshFilteredRoots START", FilteredRoots.Count);
        DbgStack();
        if (SelectedProject == null)
        {
            FilteredRoots = new ObservableCollection<TaskNode>();
            return;
        }
        var q = SearchText.ToLower();
        var list = new List<TaskNode>();
        // 先计算所有节点的 TreeLevel / IsLastSibling
        ComputeLineLevels(SelectedProject.Root.Children, 0);
        foreach (var child in SelectedProject.Root.Children)
            if (MatchesSearch(child, q) && MatchesStatusFilter(child) && MatchesTimeFilter(child))
                list.Add(child);
        FilteredRoots = new ObservableCollection<TaskNode>(list);
    }

    /// <summary>递归计算树形连接线层级和末子标记</summary>
    private void ComputeLineLevels(IEnumerable<TaskNode> nodes, int level)
    {
        var arr = nodes.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i].TreeLevel = level;
            arr[i].IsLastSibling = (i == arr.Length - 1);
            ComputeLineLevels(arr[i].Children, level + 1);
        }
    }

    private bool MatchesStatusFilter(TaskNode node)
    {
        if (_filterStatus == null) return true;  // 全部
        // 检查当前节点或任一子节点是否匹配状态
        return NodeOrDescendantMatchesStatus(node, _filterStatus);
    }

    private bool NodeOrDescendantMatchesStatus(TaskNode node, string status)
    {
        // 兼容旧数据：doing 与 in_progress 视为等价
        var ns = node.Status;
        if (status == "in_progress" || status == "doing")
        {
            if (ns == "in_progress" || ns == "doing") return true;
        }
        else if (ns == status) return true;
        foreach (var child in node.Children)
            if (NodeOrDescendantMatchesStatus(child, status)) return true;
        return false;
    }

    private bool MatchesTimeFilter(TaskNode node)
    {
        if (_filterTime == null) return true;  // 全部
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ulong threshold = _filterTime switch
        {
            "today" => now - 86400UL,          // 24小时
            "week"  => now - 7 * 86400UL,      // 7天
            "month" => now - 30 * 86400UL,     // 30天
            _       => 0UL
        };
        return NodeOrDescendantMatchesTime(node, threshold);
    }

    private bool NodeOrDescendantMatchesTime(TaskNode node, ulong minUpdated)
    {
        var updated = node.Updated ?? node.Created ?? 0;
        if (updated >= minUpdated) return true;
        foreach (var child in node.Children)
            if (NodeOrDescendantMatchesTime(child, minUpdated)) return true;
        return false;
    }

    private bool MatchesSearch(TaskNode node, string query)
    {
        if (node.Title.ToLower().Contains(query)) return true;
        return node.Children.Any(c => MatchesSearch(c, query));
    }

    // ===== 折叠状态 =====
    // TreeView 的展开/收起由 TaskNode.IsExpanded(TwoWay 绑定) 驱动；
    // 之前用 _collapsedNodes 独立集合，从未与 IsExpanded 打通 → 右键"展开/收起"无效。已废弃该集合。
    public bool IsCollapsed(string nodeId)
        => SelectedProject?.Root.Find(nodeId)?.IsExpanded == false;

    public void ToggleCollapse(string nodeId)
    {
        var node = SelectedProject?.Root.Find(nodeId);
        if (node == null) return;
        node.IsExpanded = !node.IsExpanded;
        // IsExpanded 非通知属性，重建显示树让 TreeView 重新读取
        RefreshFilteredRoots();
    }

    // ===== 命令 =====
    public ICommand AddProjectCommand { get; }
    public ICommand AddChildCommand { get; }
    public ICommand AddSiblingCommand { get; }
    public ICommand DeleteNodeCommand { get; }
    public ICommand ExportMarkdownCommand { get; }

    /// <summary>新建任务后触发（参数为新节点），View 订阅后自动进入就地重命名。</summary>
    public event Action<TaskNode>? NodeCreated;

    private void AddProject()
    {
        var proj = new Project { Name = "新项目" };
        proj.Root.Title = proj.Name;
        proj.Root.Created = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        proj.Root.Children.Add(new TaskNode { Title = "新任务", Status = "todo", Created = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        _store.Projects.Add(proj);
        Projects.Add(proj);   // 同步 UI 绑定的集合，否则新项目不出现在列表
        SelectedProject = proj;
        SelectedNode = proj.Root;
        Save();
        OnPropertyChanged(nameof(Projects));
    }

    /// <summary>
    /// 当没有任何可用项目时（首次运行 / 数据为空或已损坏），种入一个示例项目，
    /// 保证大纲永远不会空白。覆盖前由调用方备份原数据文件。
    /// </summary>
    public void SeedDemoProject()
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TaskNode N(string t, string s = "in_progress")
            => new TaskNode { Title = t, Status = s, Created = now, Updated = now };

        var root = N("示例项目：光子器件研发", "in_progress");

        var demand = N("需求分析");
        demand.Children.Add(N("文献调研"));
        demand.Children.Add(N("指标拆解"));
        demand.Children.Add(N("竞品对比"));

        var sim = N("仿真建模");
        var athena = N("ATHENA 结构构建");
        athena.Children.Add(N("PN 结掺杂"));
        athena.Children.Add(N("增透膜叠加"));
        athena.Children.Add(N("电极设计"));
        var atlas = N("ATLAS 光电响应");
        atlas.Children.Add(N("光电流扫描"));
        atlas.Children.Add(N("PDE 计算"));
        atlas.Children.Add(N("参数扫描脚本"));
        sim.Children.Add(athena);
        sim.Children.Add(atlas);

        var exp = N("实验验证");
        exp.Children.Add(N("流片准备"));
        exp.Children.Add(N("测试方案"));

        var fund = N("基金申报书");
        fund.Children.Add(N("研究内容"));
        fund.Children.Add(N("技术指标"));
        fund.Children.Add(N("预算表"));

        root.Children.Add(demand);
        root.Children.Add(sim);
        root.Children.Add(exp);
        root.Children.Add(fund);

        var proj = new Project { Name = "示例项目：光子器件研发", Root = root };
        _store.Projects.Add(proj);
        Projects.Add(proj);
        SelectedProject = proj;
        SelectedNode = proj.Root;
        Save();
        OnPropertyChanged(nameof(Projects));
        Dbg("SeedDemoProject done", proj.Name);
    }

    private void AddChild()
    {
        if (SelectedNode == null) return;
        Dbg("AddChild");
        var child = new TaskNode { Title = "新任务" };
        child.Created = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SelectedNode.Children.Add(child);
        OnSelectionChanged(child);
        Save();
        SuppressSelectionSync = true;
        RefreshFilteredRoots();
        // 延迟解除：等 TreeView 重建容器 + WPF 自动选中事件全部处理完
        _ = ResetSuppressAsync();
        NodeCreated?.Invoke(child);   // 通知 View 自动进入重命名
    }

    private void AddSibling()
    {
        if (SelectedNode == null || SelectedProject == null) return;
        var parent = FindParent(SelectedProject.Root, SelectedNode.Id);
        var list = parent?.Children ?? SelectedProject.Root.Children;
        var idx = list.IndexOf(SelectedNode);
        var sibling = new TaskNode { Title = "新任务" };
        sibling.Created = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        list.Insert(idx + 1, sibling);
        OnSelectionChanged(sibling);
        Save();
        SuppressSelectionSync = true;
        RefreshFilteredRoots();
        _ = ResetSuppressAsync();
        NodeCreated?.Invoke(sibling);   // 通知 View 自动进入重命名
    }

    /// <summary>上下键移动选中：在可见（已展开）的树形顺序中前移/后移 delta 位。</summary>
    public void MoveSelection(int delta)
    {
        if (SelectedProject == null || SelectedNode == null) return;
        var flat = new List<TaskNode>();
        foreach (var r in FilteredRoots)
            FlattenVisible(r, flat);
        int idx = -1;
        for (int i = 0; i < flat.Count; i++)
            if (flat[i] == SelectedNode) { idx = i; break; }
        if (idx < 0) return;
        int target = idx + delta;
        if (target < 0 || target >= flat.Count) return;
        OnSelectionChanged(flat[target]);
    }

    private static void FlattenVisible(TaskNode node, List<TaskNode> flat)
    {
        flat.Add(node);
        if (node.IsExpanded)
            foreach (var c in node.Children)
                FlattenVisible(c, flat);
    }

    private async System.Threading.Tasks.Task ResetSuppressAsync()
    {
        await System.Threading.Tasks.Task.Delay(300);  // 等 WPF 排完所有布局/选中事件
        SuppressSelectionSync = false;
        OnPropertyChanged(nameof(SuppressSelectionSync));
    }

    private void DeleteNode()
    {
        // [diag] 记录工具栏删除（区分右键 DeleteNodeById）
        var diagPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".branch-task", "delete_diag.log");
        if (SelectedNode == null || SelectedProject == null)
        {
            try { System.IO.File.AppendAllText(diagPath, $"[{DateTime.Now:HH:mm:ss.fff}] DeleteNode: SKIP (SelectedNode={(SelectedNode == null ? "null" : SelectedNode.Title)}, proj={(SelectedProject == null ? "null" : SelectedProject.Name)})\n"); } catch { }
            return;
        }
        var parent = FindParent(SelectedProject.Root, SelectedNode.Id);
        var list = parent?.Children ?? SelectedProject.Root.Children;
        var removed = list.Remove(SelectedNode);
        try { System.IO.File.AppendAllText(diagPath, $"[{DateTime.Now:HH:mm:ss.fff}] DeleteNode: '{SelectedNode.Title}'({SelectedNode.Id}) removed={removed}\n"); } catch { }
        SelectedNode = parent ?? SelectedProject.Root;
        Save();
        RefreshFilteredRoots();
    }

    /// <summary>
    /// 按 id 精确定位删除（右键菜单用，不依赖 SelectedNode——SelectedNode 可能被
    /// 菜单关闭/选中事件时序漂移，导致 DeleteNode 提前 return → 删除不落盘 → 重启后任务又出现）。
    /// 返回是否删除成功。
    /// </summary>
    public bool DeleteNodeById(string nodeId)
    {
        if (SelectedProject == null) return false;
        if (nodeId == SelectedProject.Root.Id) return false;   // 根节点不可删

        var parent = FindParent(SelectedProject.Root, nodeId);
        if (parent == null) return false;
        var node = parent.Children.FirstOrDefault(c => c.Id == nodeId);
        if (node == null) return false;

        parent.Children.Remove(node);
        if (SelectedNode == node)
            SelectedNode = parent;
        Save();
        RefreshFilteredRoots();
        // 验证：删除后读回磁盘，确认该节点已不存在（排查"删除后重启又出现"）
        try
        {
            var diagPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".branch-task", "delete_diag.log");
            var back = DataService.Load();
            var still = back?.Projects.FirstOrDefault(p => p.Id == SelectedProject.Id)?.Root.Find(nodeId);
            System.IO.File.AppendAllText(diagPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] DeleteNodeById: '{node.Title}'({nodeId}) 删除后读回={(still == null ? "已删除OK" : "仍在!!")}\n");
        }
        catch { }
        return true;
    }

    private TaskNode? FindParent(TaskNode root, string childId)
    {
        foreach (var child in root.Children)
        {
            if (child.Id == childId) return root;
            var found = FindParent(child, childId);
            if (found != null) return found;
        }
        return null;
    }

    public void OnSelectionChanged(TaskNode node)
    {
        SelectedNode = node;
        Dbg("OnSelectionChanged", node.Id, node.Title);
    }

    /// <summary>递归去重：同一 Id 出现多次视为损坏（旧版拖拽可能复制节点），保留首次出现、移除后续重复。</summary>
    private static void DedupeTree(TaskNode root)
    {
        var seen = new HashSet<string>();
        DedupeChildren(root, seen);
    }

    private static void DedupeChildren(TaskNode parent, HashSet<string> seen)
    {
        var toRemove = new List<TaskNode>();
        foreach (var child in parent.Children)
        {
            if (!seen.Add(child.Id)) toRemove.Add(child);
            else DedupeChildren(child, seen);
        }
        foreach (var child in toRemove)
            parent.Children.Remove(child);
    }

    public void DeleteProject(Project proj)
    {
        _store.Projects.Remove(proj);
        Projects.Remove(proj);   // 同步 UI 绑定的集合，否则删除后列表残留
        if (SelectedProject == proj)
            SelectedProject = _store.Projects.FirstOrDefault();
        Save();
        OnPropertyChanged(nameof(Projects));
        // [diag] 记录项目删除 + 磁盘回读校验（排查"删除不了"）
        try
        {
            var diagPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".branch-task", "delete_diag.log");
            var back = DataService.Load();
            var still = back?.Projects.FirstOrDefault(p => p.Id == proj.Id);
            System.IO.File.AppendAllText(diagPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] DeleteProject: '{proj.Name}'({proj.Id}) done, now {_store.Projects.Count} proj, 磁盘校验={(still == null ? "OK已删除" : "仍在!!")}\n");
        }
        catch { }
    }

    public void RenameProject(Project proj, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || proj.Name == newName) return;
        proj.Name = newName;   // setter 内 OnPropertyChanged → ListBox 自动刷新
        proj.Root.Title = newName;
        Save();
    }

    public void MoveProject(int fromIdx, int toIdx)
    {
        if (fromIdx < 0 || fromIdx >= _store.Projects.Count ||
            toIdx < 0 || toIdx >= _store.Projects.Count || fromIdx == toIdx)
            return;
        var proj = _store.Projects[fromIdx];
        _store.Projects.RemoveAt(fromIdx);
        _store.Projects.Insert(toIdx, proj);
        Projects.Move(fromIdx, toIdx);
        Save();
    }

    public void Save()
    {
        // 记录选中节点的更新时间（北京时间对应的 Unix 秒），供详情面板"时间"显示
        if (SelectedNode != null)
            SelectedNode.Updated = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 注意：原"安全合并"逻辑已删除（btwpf85）。
        // 它会在 Save 前把磁盘上还有的、但刚被本实例删除的项目合并回内存 → 删除不持久 → 重启后项目又出现。
        // 单实例锁(Mutex)已杜绝多开互相覆盖；如需外部工具改 JSON，让外部工具自己负责写完整文件。

        Dbg("Save");
        DataService.Save(_store);
        // 不在此通知 Projects——每次 Save 都重建 ListBox ItemsSource 会导致 SelectedProject 重入刷新树
    }

    /// <summary>保存 UI 状态（当前项目 / 面板展开 / 层级配色）到 wpf_ui.json</summary>
    private void SaveUiState()
    {
        try
        {
            DataService.SaveUiState(new Models.UiState
            {
                CurrentProjectId = SelectedProject?.Id,
                LeftPanelOpen = _leftPanelOpen,
                RightPanelOpen = _rightPanelOpen,
                ColorByLevel = _colorByLevel,
            });
        }
        catch { }
    }

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(Projects));
        RefreshFilteredRoots();
    }

    private void ExportMarkdown()
    {
        if (SelectedProject == null) return;
        // 兜底（无 UI 选择时）：写到桌面
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"{SelectedProject.Name}.md");
        ExportMarkdownTo(path);
    }

    /// <summary>导出当前项目为 Markdown 到指定路径。</summary>
    public void ExportMarkdownTo(string path)
    {
        if (SelectedProject == null) return;
        var md = GenerateMarkdown(SelectedProject);
        File.WriteAllText(path, md);
        ExportMessage = $"导出: {path}";
    }

    private string GenerateMarkdown(Project proj)
    {
        var lines = new List<string>();
        lines.Add($"# {proj.Name}");
        lines.Add("");

        // 项目里程碑
        if (proj.Intermediates.Count > 0)
        {
            lines.Add("## 项目里程碑");
            foreach (var im in proj.Intermediates)
                AppendIntermediate(lines, im, "###");
            lines.Add("");
        }

        // 任务树
        lines.Add("## 任务");
        AppendNode(lines, proj.Root, 2);
        return string.Join("\n", lines);
    }

    private void AppendNode(List<string> lines, TaskNode node, int depth)
    {
        var prefix = new string('#', Math.Min(depth, 6));
        var statusEmoji = node.Status switch
        {
            "done" => "✅",
            "doing" => "🔄",
            "in_progress" => "🔄",
            "blocked" => "🚫",
            _ => ""
        };
        lines.Add($"{prefix} {statusEmoji} {node.Title}".Trim());
        if (!string.IsNullOrWhiteSpace(node.TaskInfo))
            lines.Add($"任务信息: {node.TaskInfo}");
        if (!string.IsNullOrWhiteSpace(node.Summary))
            lines.Add($"摘要: {node.Summary}");

        foreach (var im in node.Intermediates)
            AppendIntermediate(lines, im, new string('#', Math.Min(depth + 1, 6)));

        lines.Add("");
        foreach (var child in node.Children)
            AppendNode(lines, child, depth + 1);
    }

    private void AppendIntermediate(List<string> lines, IntermediateEntry im, string prefix)
    {
        lines.Add($"{prefix} {im.Title}");
        if (im.IsText) lines.Add($"  {im.Content}");
        if (im.IsFile) lines.Add($"  文件: {im.FilePath}");
        if (im.IsLink) lines.Add($"  链接: {im.Link}");
    }

    // ===== 调试日志 =====
    private static void Dbg(string tag, params object?[] args)
    {
        var msg = $"[{DateTime.Now:HH:mm:ss.fff}] {tag} {(args.Length > 0 ? string.Join(" ", args) : "")}";
        Debug.WriteLine(msg);
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".branch-task", "debug.log");
            File.AppendAllText(logPath, msg + "\n");
        }
        catch { }
    }

    private static void DbgStack()
    {
        var trace = new StackTrace(2, true);
        for (int i = 0; i < Math.Min(trace.FrameCount, 6); i++)
        {
            var f = trace.GetFrame(i);
            var m = f.GetMethod();
            if (m == null) continue;
            Dbg("  ->", $"{m.DeclaringType?.Name}.{m.Name}");
        }
    }

    // ===== INotifyPropertyChanged =====
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ===== RelayCommand =====
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
