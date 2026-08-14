# 任务树（Branch Task）

一款 Windows 桌面「分支/任务管理」应用 + 配套 MCP 服务，让 AI 助手也能读写你的任务树。

以树形大纲组织任务，支持状态标记、拖拽排序、内联重命名、中间结果（文字/文件/链接）、思维导图、导出 Markdown。

## 仓库结构

```
├── branch-task-wpf/        # 桌面应用（C# / .NET 8 WPF）
└── branch-task-wpf-mcp/    # MCP Server（TypeScript / Node.js），让 AI 读写任务树
```

## 桌面应用（branch-task-wpf）

三栏布局：

```
┌──────────┬───────────────────────────────┬──────────────────┐
│ 左：项目栏 │  中：大纲树 / 思维导图（Tab）   │ 右：详情面板      │
└──────────┴───────────────────────────────┴──────────────────┘
```

### 功能
- **多项目**：新建、删除、拖拽排序、内联重命名
- **大纲树**：任意层级嵌套、拖拽排序、状态圆点（未标记/进行中/已完成/阻塞）、展开折叠、树形连接线
- **思维导图**：Canvas 自绘，缩放/平移 + 自适应
- **详情面板**：名称 / 状态 / 任务信息 / 中间结果 / 项目里程碑 / 库（文件）
- **中间结果**：文字、文件、链接三种类型，文件自动拷贝到工作目录
- **搜索与筛选**：标题搜索 + 状态筛选 + 时间筛选
- **导出 Markdown**：保存对话框选路径，导出项目大纲
- **快捷键**：`Enter` 建子任务、`Tab` 建兄任务、`↑/↓` 切换选中（均自动进入重命名）

### 构建

```bash
cd branch-task-wpf
dotnet build -c Release
# 产物：bin/Release/net8.0-windows/branch-task-wpf.exe
```

要求：.NET 8 SDK。无任何第三方 NuGet 依赖。

### 数据存储

- 项目数据：`%USERPROFILE%\.branch-task\projects.json`
- UI 状态：`%USERPROFILE%\.branch-task\wpf_ui.json`
- 中间结果文件：`%USERPROFILE%\.branch-task\workspaces\{projectId}\{taskId}\`

## MCP Server（branch-task-wpf-mcp）

TypeScript 实现的 MCP Server，通过 stdio 与 AI 助手通信，读写与桌面应用**同一份数据**（`~/.branch-task/projects.json`），需与桌面应用配合使用。

### 构建

```bash
cd branch-task-wpf-mcp
npm install
npm run build
# 产物：dist/index.js
```

### 配置（以 WorkBuddy 为例）

把 `mcp-config.example.json` 中的内容合并到 `~/.workbuddy/mcp.json` 的 `mcpServers`，并把路径改成你的实际路径：

```json
{
  "branch-task-wpf": {
    "type": "stdio",
    "command": "node",
    "args": ["<项目绝对路径>/branch-task-wpf-mcp/dist/index.js"]
  }
}
```

### 提供的工具（26 个）

| 类别 | 工具 |
|------|------|
| 项目 | `bt_list_projects` `bt_add_project` `bt_select_project` `bt_delete_project` |
| 任务树 | `bt_get_tree` `bt_build_tree` `bt_add_child` `bt_delete_node` `bt_move` `bt_start_branch` `bt_back_to_main` |
| 状态/折叠 | `bt_set_status` `bt_collapse` `bt_expand` `bt_collapse_all` `bt_expand_all` `bt_set_collapsed` `bt_list_collapsed` `bt_expand_to` |
| 中间结果 | `bt_add_intermediate` `bt_list_intermediates` `bt_update_intermediate` `bt_delete_intermediate` |
| 其他 | `bt_add_message` `bt_record_note` `bt_export_markdown` |

## 安全说明

- 本软件为**纯本地应用**，无任何网络调用、无 API 密钥。
- MCP 仅通过 stdio 与本机 AI 助手通信，只读写本地 JSON 文件。
- 数据文件仅保存在 `~/.branch-task/` 下。

## 许可

MIT License
