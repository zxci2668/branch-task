# 任务树（Branch Task）

为了解决在 Agent 中对某一问题追问、产出对话轮次太多，导致原本的信息和计划查询困难的痛点，本项目提供一款 **Windows 桌面「分支/任务管理」应用 + 配套 MCP 服务**，让 AI 助手也能读写你的任务树。

你可以**利用 hook 在每次对话后自动生长任务树**，也可以**手动让它生长**；过程中记录任务信息和中间产物（文字/文件/链接），更方便地管理整个对话的思路。

以树形大纲组织任务，支持**状态标记、拖拽排序、内联重命名、中间结果（文字/文件/链接）、思维导图、导出 Markdown**。

## 仓库结构

```
├── branch-task-wpf/        # 桌面应用（C# / .NET 8 WPF）
├── branch-task-wpf-mcp/    # MCP Server（配合 WPF 版）
├── branch-task-egui/       # 桌面应用（Rust / egui 0.31）
└── branch-task-mcp/        # MCP Server（配合 Rust 版）
```

两个桌面实现功能对齐、共用同一份数据文件（`~/.branch-task/projects.json`），可任选其一使用。

---

## 一、WPF 版（branch-task-wpf）

### 技术栈
- C# / .NET 8 WPF，无第三方 NuGet 依赖，MVVM 架构

### 三栏布局

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
- **搜索与筛选**：标题搜索 + 状态筛选 + 时间筛选
- **导出 Markdown**：保存对话框选路径
- **快捷键**：`Enter` 建子任务、`Tab` 建兄任务、`↑/↓` 切换选中（均自动进入重命名）

### 构建

```bash
cd branch-task-wpf
dotnet build -c Release
# 产物：bin/Release/net8.0-windows/branch-task-wpf.exe
```

---

## 二、Rust 版（branch-task-egui）

### 技术栈
- Rust（edition 2021）/ egui 0.31 / eframe 0.31（glow 后端），即时模式 GUI

### 功能
- 三栏布局（左项目栏 / 中大纲·导图 / 右详情，详情可拖拽调宽）
- 多项目切换、内联重命名、任意层级嵌套 + 拖拽排序
- 状态标记、搜索/筛选、折叠控制
- 中间结果：长正文（>2000 字）自动落盘工作目录，JSON 仅存预览
- 导出 Markdown（项目里程碑 + 任务中间结果）

### 构建

```bash
cd branch-task-egui
cargo build --release
# 产物：target/release/branch-task-egui.exe
```

> 说明：`icon_data.rs` 是自动生成的窗口图标 RGBA 数据（`main.rs` 通过 `include!` 引用，构建必需）。

---

## MCP Server（AI 读写任务树）

两套 MCP（`branch-task-wpf-mcp` / `branch-task-mcp`）均为 TypeScript 实现，通过 stdio 与 AI 助手通信，读写与桌面应用**同一份数据**（`~/.branch-task/projects.json`）。

### 构建

```bash
cd branch-task-wpf-mcp   # 或 branch-task-mcp
npm install
npm run build
# 产物：dist/index.js
```

### 配置（以 WorkBuddy 为例）

把 `mcp-config.example.json` 内容合并到 `~/.workbuddy/mcp.json` 的 `mcpServers`，路径改成实际路径：

```json
{
  "branch-task-wpf": {
    "type": "stdio",
    "command": "node",
    "args": ["<项目绝对路径>/branch-task-wpf-mcp/dist/index.js"]
  }
}
```

### 提供的工具

| 类别 | 工具 |
|------|------|
| 项目 | `bt_list_projects` `bt_add_project` `bt_select_project` `bt_delete_project` |
| 任务树 | `bt_get_tree` `bt_build_tree` `bt_add_child` `bt_delete_node` `bt_move` `bt_start_branch` `bt_back_to_main` |
| 状态/折叠 | `bt_set_status` `bt_collapse` `bt_expand` `bt_collapse_all` `bt_expand_all` `bt_set_collapsed` `bt_list_collapsed` `bt_expand_to` |
| 中间结果 | `bt_add_intermediate` `bt_list_intermediates` `bt_update_intermediate` `bt_delete_intermediate`（WPF 版） |
| 其他 | `bt_add_message` `bt_record_note` `bt_export_markdown` |

---

## 数据存储

- 项目数据：`%USERPROFILE%\.branch-task\projects.json`（两版共用，JSON 大小写不敏感）
- UI 状态：`%USERPROFILE%\.branch-task\wpf_ui.json` / `ui_state.json`
- 中间结果工作目录：`%USERPROFILE%\.branch-task\workspaces\{projectId}\{taskId}\`

## 安全说明

- 纯本地应用，无网络调用、无 API 密钥。
- MCP 仅通过 stdio 与本机 AI 助手通信，只读写本地 JSON 文件。

## 许可

MIT License
