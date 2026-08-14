using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BranchTaskWpf.Models;

/// <summary>
/// 项目（与 Rust Project 字段名完全对齐）
/// </summary>
public class Project : INotifyPropertyChanged
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    private string _name = "新项目";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    [JsonPropertyName("root")]
    public TaskNode Root { get; set; } = new();

    [JsonPropertyName("cursor")]
    public string Cursor { get; set; } = "";

    [JsonPropertyName("intermediates")]
    public ObservableCollection<IntermediateEntry> Intermediates { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
