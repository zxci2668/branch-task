using System;
using System.Text.Json.Serialization;

namespace BranchTaskWpf.Models;

/// <summary>
/// 中间结果条目（与 Rust IntermediateEntry 字段名对齐，JSON 兼容）
/// </summary>
public class IntermediateEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text"; // "text" | "file" | "link"

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("created")]
    public ulong? Created { get; set; }

    [JsonIgnore]
    public bool IsText => Kind == "text";
    [JsonIgnore]
    public bool IsFile => Kind == "file";
    [JsonIgnore]
    public bool IsLink => Kind == "link";
}
