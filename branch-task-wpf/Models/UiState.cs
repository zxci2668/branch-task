using System.Text.Json.Serialization;

namespace BranchTaskWpf.Models;

/// <summary>
/// UI 状态持久化（WPF 版独立文件，不与 Rust 版 ui_state.json 冲突）
/// </summary>
public class UiState
{
    [JsonPropertyName("currentProjectId")]
    public string? CurrentProjectId { get; set; }

    [JsonPropertyName("leftPanelOpen")]
    public bool LeftPanelOpen { get; set; } = true;

    [JsonPropertyName("rightPanelOpen")]
    public bool RightPanelOpen { get; set; } = true;

    [JsonPropertyName("colorByLevel")]
    public bool ColorByLevel { get; set; } = true;
}
