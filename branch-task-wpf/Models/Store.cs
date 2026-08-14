using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BranchTaskWpf.Models;

/// <summary>
/// 数据存储根对象（与 Rust Store 字段名对齐）
/// </summary>
public class Store
{
    [JsonPropertyName("projects")]
    public List<Project> Projects { get; set; } = new();
}
