using System.Collections.Generic;

namespace BranchTaskWpf.Models;

/// <summary>库标签页的分组：公共区（项目级文件）或按任务分组的文件列表</summary>
public class LibraryGroup
{
    public string Title { get; set; } = "";
    public List<IntermediateEntry> Items { get; set; } = new();
}
