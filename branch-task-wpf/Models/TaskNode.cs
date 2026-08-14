using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BranchTaskWpf.Models;

/// <summary>
/// 任务节点（与 Rust TaskNode 字段名完全对齐）
/// </summary>
public class TaskNode : INotifyPropertyChanged
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("title")]
    private string _title = "新任务";

    /// <summary>标题：通知属性——详情页/大纲任一处改名，另一边即时同步刷新。</summary>
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    [JsonPropertyName("status")]
    private string _status = "";

    /// <summary>状态：""=未标记, "doing"=进行中, "done"=已完成, "blocked"=阻塞。
    /// 通知属性——详情页/圆点改动后大纲圆点即时重绘。</summary>
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    // messages 兼容旧 JSON（旧格式是对象数组 [{id,role,content,ts}]，新格式是字符串数组）
    [JsonPropertyName("messages")]
    public System.Text.Json.JsonElement Messages { get; set; }

    [JsonPropertyName("task_info")]
    public string TaskInfo { get; set; } = "";

    [JsonPropertyName("created")]
    public ulong? Created { get; set; }

    [JsonPropertyName("updated")]
    public ulong? Updated { get; set; }

    [JsonPropertyName("children")]
    public List<TaskNode> Children { get; set; } = new();

    [JsonPropertyName("intermediates")]
    public ObservableCollection<IntermediateEntry> Intermediates { get; set; } = new();

    // ---- 非序列化的 UI 辅助 ----
    [JsonIgnore]
    public bool IsExpanded { get; set; } = true;

    [JsonIgnore]
    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>树形连接线层级（0=根节点，不画线）</summary>
    [JsonIgnore]
    public int TreeLevel { get; set; }

    /// <summary>是否父节点的最后一个子节点（└─ 无下延竖线）</summary>
    [JsonIgnore]
    public bool IsLastSibling { get; set; }

    /// <summary>递归查找节点</summary>
    public TaskNode? Find(string id)
    {
        if (Id == id) return this;
        foreach (var child in Children)
        {
            var found = child.Find(id);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>收集子树所有 id</summary>
    public void CollectIds(HashSet<string> set)
    {
        set.Add(Id);
        foreach (var child in Children)
            child.CollectIds(set);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
