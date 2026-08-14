use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// 消息节点。
/// 注意:实际 projects.json 中消息字段是 `content`(不是 types.ts 里的 `text`),
/// 且带可选 `ts` 时间戳。Rust 结构须与实际 JSON 对齐。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Msg {
    pub id: String,
    pub role: String, // user | assistant | note
    pub content: String,
    #[serde(default)]
    pub ts: Option<u64>,
}

/// 任务过程中产生的「中间结果 / 过程产物」条目。
/// 既可是一段文字快照（kind="text"），也可是一条外部文件引用（kind="file"，
/// 文件会被拷入工作目录 `~/.branch-task/workspaces/<pid>/<tid>/`），或一条链接（kind="link"）。
/// 长正文（>2000 字）会自动落到工作目录的 `<id>.md`，JSON 内仅存预览，避免 projects.json 膨胀。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IntermediateEntry {
    pub id: String,
    pub title: String, // 显示标题
    #[serde(default = "default_im_kind")]
    pub kind: String, // text | file | link
    #[serde(default)]
    pub content: String, // 文字正文（短）/ 长文预览；长文落盘后仅存预览
    #[serde(default)]
    pub file_path: Option<String>, // kind=file 时指向工作目录中的拷贝路径
    #[serde(default)]
    pub link: Option<String>, // kind=link 时外部链接
    #[serde(default)]
    pub created: Option<u64>, // 创建时间(Unix 秒)
}

fn default_im_kind() -> String {
    "text".to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TaskNode {
    pub id: String,
    pub title: String,
    pub status: String, // todo | doing | failed | done | parked
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub messages: Vec<Msg>,
    #[serde(default)]
    pub task_info: String, // 任务信息（自由输入，详情顶部）
    #[serde(default)]
    pub created: Option<u64>, // 创建时间(Unix 秒)
    #[serde(default)]
    pub updated: Option<u64>, // 修改时间(Unix 秒)
    #[serde(default)]
    pub children: Vec<TaskNode>,
    #[serde(default)]
    pub intermediates: Vec<IntermediateEntry>, // 任务过程中产生的中间结果/过程产物
}

impl TaskNode {
    /// 按 id 递归查找节点(只读)。
    pub fn find(&self, id: &str) -> Option<&TaskNode> {
        if self.id == id {
            return Some(self);
        }
        for c in &self.children {
            if let Some(n) = c.find(id) {
                return Some(n);
            }
        }
        None
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Project {
    pub id: String,
    pub name: String,
    pub root: TaskNode,
    #[serde(default)]
    pub cursor: String,
    #[serde(default)]
    pub intermediates: Vec<IntermediateEntry>, // 项目级过程产物/里程碑（界面标题"项目里程碑"）
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Store {
    pub projects: Vec<Project>,
    #[serde(rename = "currentId")]
    pub current_id: String,
}

/// 数据文件路径:~/.branch-task/projects.json
pub fn data_path() -> PathBuf {
    let home = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(home)
        .join(".branch-task")
        .join("projects.json")
}

/// 中间结果的工作目录:`~/.branch-task/workspaces/<project_id>/<task_id>/`。
/// 归管进来的文件会被拷到此处，长正文 .md 也写在这里。id 仅含字母数字与下划线，可直接拼路径。
pub fn intermediate_ws_path(project_id: &str, task_id: &str) -> PathBuf {
    let home = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(home)
        .join(".branch-task")
        .join("workspaces")
        .join(project_id)
        .join(task_id)
}

pub fn load() -> Result<Store, String> {
    let p = data_path();
    let s = std::fs::read_to_string(&p).map_err(|e| format!("读取失败: {e}"))?;
    serde_json::from_str(&s).map_err(|e| format!("解析失败: {e}"))
}

pub fn save(store: &Store) -> Result<(), String> {
    let p = data_path();
    if let Some(parent) = p.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("建目录失败: {e}"))?;
    }
    let s = serde_json::to_string_pretty(store).map_err(|e| format!("序列化失败: {e}"))?;
    std::fs::write(&p, s).map_err(|e| format!("写入失败: {e}"))
}

/// UI 界面状态(与业务数据分开存, 避免污染 projects.json 及触发热重载)。
/// 记录左侧项目栏 / 右侧详情栏的展开-收起状态, 以及大纲任务节点的折叠集合,
/// 使下次启动与上次关闭时一致。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UiState {
    #[serde(default = "default_true")]
    pub left_open: bool,
    #[serde(default = "default_true")]
    pub right_open: bool,
    #[serde(default)]
    pub collapsed: Vec<String>,
    // 上次关闭时正在查看的项目索引，启动时恢复
    #[serde(default)]
    pub project_idx: usize,
    // 详情(右侧)面板宽度，启动时恢复，避免 egui 内部面板记忆跨重启丢失/回弹
    #[serde(default)]
    pub right_width: Option<f32>,
}

fn default_true() -> bool {
    true
}

impl Default for UiState {
    fn default() -> Self {
        UiState {
            left_open: true,
            right_open: true,
            collapsed: Vec::new(),
            project_idx: 0,
            right_width: None,
        }
    }
}

/// UI 状态文件路径:~/.branch-task/ui_state.json
pub fn ui_state_path() -> PathBuf {
    let home = std::env::var("USERPROFILE").unwrap_or_else(|_| ".".to_string());
    PathBuf::from(home)
        .join(".branch-task")
        .join("ui_state.json")
}

/// 读取 UI 状态; 文件不存在或解析失败时回退到默认(均展开)。
pub fn load_ui() -> UiState {
    let p = ui_state_path();
    match std::fs::read_to_string(&p) {
        Ok(s) => serde_json::from_str(&s).unwrap_or_default(),
        Err(_) => UiState::default(),
    }
}

/// 写入 UI 状态(展开-收起状态发生变化时调用)。
pub fn save_ui(state: &UiState) {
    let p = ui_state_path();
    if let Some(parent) = p.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Ok(s) = serde_json::to_string_pretty(state) {
        let _ = std::fs::write(&p, s);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loads_real_projects_json() {
        let store = load().expect("应能从 ~/.branch-task/projects.json 加载");
        assert!(!store.projects.is_empty(), "项目不应为空");
        assert!(!store.projects[0].root.children.is_empty(), "根节点应有子节点");
    }
}
