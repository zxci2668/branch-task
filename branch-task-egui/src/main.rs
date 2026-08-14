// Windows 下 release 构建使用 windows 子系统，避免启动时弹出黑色控制台窗口
// （debug 构建仍保留控制台，方便排查问题）
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

// 自动生成的图标 RGBA 数据（见 build.rs / icon_data.rs，64x64 透明图标）
include!(concat!(env!("CARGO_MANIFEST_DIR"), "/icon_data.rs"));

mod data;

use data::{Store, TaskNode, Project};
use eframe::egui;
use std::collections::{HashMap, HashSet};

const INDENT: f32 = 16.0;
const LINE_COLOR: egui::Color32 = egui::Color32::from_rgb(196, 202, 211);
// 编译时间戳（由 build.rs 注入），显示在窗口标题，用于确认当前运行的 exe 是否为最新构建
const BUILD_TS: &str = env!("BUILD_TS");
// 思维导图布局参数
const LEVEL_W: f32 = 210.0; // 每层 x 间距
const ROW_H: f32 = 48.0; // 每行 y 间距
const NODE_W: f32 = 168.0; // 节点框宽
const NODE_H: f32 = 38.0; // 节点框高
const ORIGIN_X: f32 = 30.0;
const ORIGIN_Y: f32 = 30.0;

#[derive(Clone, Copy, PartialEq, Eq)]
enum ViewMode {
    Outline,
    Mindmap,
}

// 拖拽落点模式：在目标任务的「前 / 后」作为同级插入，或「中间」变为其下属子任务
#[derive(Clone, Copy, PartialEq)]
enum DropPos {
    Before,
    After,
    Child,
}

struct App {
    store: Store,
    selected: String,
    collapsed: HashSet<String>,
    view: ViewMode,
    project_idx: usize,
    // 思维导图视图状态
    pan: egui::Vec2,
    zoom: f32,
    // 左右面板是否展开
    left_open: bool,
    right_open: bool,
    // 右侧(详情)面板宽度：自管，存 ui_state.json，跨重启恢复（不受 egui 内部面板记忆回弹影响）
    right_width: f32,
    // 大纲视图：是否按层级着色
    color_by_level: bool,
    // 搜索 / 筛选：大纲按标题 + 状态过滤任务（左栏同时按项目名过滤）
    search: String,
    status_filter: String, // all | todo | doing | failed | done | parked
    // 文件热重载：记录上次读取 projects.json 的修改时间
    last_mtime: Option<std::time::SystemTime>,
    // ui_state.json 的最后修改时间（折叠状态被外部如 MCP 修改时触发重载）
    last_ui_mtime: Option<std::time::SystemTime>,
    // 新建子任务后，自动进入「行内重命名」过程（在树中直接编辑名称）
    renaming: Option<String>,
    // 重命名焦点请求：进入重命名时仅在下一帧请求一次聚焦，避免每帧抢焦点导致无法失焦提交
    renaming_needs_focus: bool,
    // 本帧刚由 Tab 创建的同级任务 id：用于屏蔽「同一 Tab 键被新建节点再次读到」而误建第二个同级
    just_created_rename: Option<String>,
    // 左栏项目列表：正在行内重命名的项目索引（添加新项目后自动进入，回车或点击别处提交）
    renaming_project: Option<usize>,
    // 新建项目后，滚动左栏使其可见（仅下一帧生效一次）
    scroll_to_project: Option<usize>,
    // 拖拽排序：当前被拖动的节点 id
    drag_node: Option<String>,
    // 拖拽排序：松开鼠标时的落点（目标节点 id, 落点模式）
    drop_target: Option<(String, DropPos)>,
    // 左栏项目拖拽排序：当前被拖动的项目索引
    drag_project: Option<usize>,
    // 左栏项目拖拽排序：松开鼠标时的落点（目标项目索引, 是否插在其后）
    drop_project: Option<(usize, bool)>,
    // 导出 Markdown 后的反馈提示（顶部显示，数秒后自动消失）
    export_msg: Option<String>,
    export_msg_until: f64,
    // 子→父级联：当某任务被标为完成时，需要检查其所有父任务是否应自动完成
    promote_pending: bool,
    // 启动修正：首次加载时补齐历史数据中「子全部完成但父未标记」的节点
    promote_on_load: bool,
    // IME 合成态：输入法正在组字时为 true，避免合成中按回车被误判为「提交重命名」
    ime_composing: bool,
    // 最后一次 IME 事件的时间戳（秒，egui input time）。用于「冷却窗」：输入法确认候选的
    // Commit 事件与泄漏的 Enter 键可能差一两帧到达，冷却窗内的 Enter 一律视为输入法确认键。
    last_ime_time: f64,
    // 输入法是否处于活跃态（供每日进度输入框回车提交时判断是否 IME 确认键，避免误提交）
    ime_active: bool,
    // 中间结果：待确认删除的条目 id（弹窗确认，默认保留）
    confirm_delete_im: Option<String>,
    // IME 沉默提交哨兵：IME 产生事件（Preedit/Commit）后置 true。
    // 下一次在 commit 框中按的 Enter 视为 IME 确认键，吞掉避免多一个换行；吞完立即清 false。
    // 语义：「IME 参与过 → 紧随的 Enter 是确认 → 放过去之后，Enter 恢复为正常换行」。
    ime_pending_enter_swallow: bool,
    // ── IME 回车诊断（测试完后可删除）──
    debug_ime_events: Vec<String>,       // 最近 20 条回车事件记录
    debug_ime_on: bool,                  // 是否显示诊断面板
}

impl App {
    fn current_project_mut(&mut self) -> Option<&mut data::Project> {
        self.store.projects.get_mut(self.project_idx)
    }
}

/// 读取 projects.json 当前的修改时间（用于热重载检测）。
fn projects_mtime() -> Option<std::time::SystemTime> {
    std::fs::metadata(data::data_path())
        .ok()
        .and_then(|m| m.modified().ok())
}

fn ui_state_mtime() -> Option<std::time::SystemTime> {
    std::fs::metadata(data::ui_state_path())
        .ok()
        .and_then(|m| m.modified().ok())
}

fn find_node_mut<'a>(node: &'a mut TaskNode, id: &str) -> Option<&'a mut TaskNode> {
    if node.id == id {
        return Some(node);
    }
    for c in &mut node.children {
        if let Some(n) = find_node_mut(c, id) {
            return Some(n);
        }
    }
    None
}

// 生成唯一新任务 id（纳秒时间戳，避免重复）
fn new_task_id() -> String {
    format!(
        "task_{}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
    )
}

// 当前 Unix 秒级时间戳
fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

/// 用系统默认程序打开一个文件（或路径）。Windows 用 explorer，不弹控制台。
fn open_file_external(path: &str) {
    #[cfg(target_os = "windows")]
    {
        let _ = std::process::Command::new("explorer").arg(path).spawn();
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = std::process::Command::new("xdg-open").arg(path).spawn();
    }
}

/// 在文件管理器中打开该文件所在目录，并选中该文件。
fn open_containing_dir(path: &str) {
    #[cfg(target_os = "windows")]
    {
        // explorer /select,<path> 会打开所在文件夹并选中该文件
        let _ = std::process::Command::new("explorer")
            .arg(format!("/select,{}", path))
            .spawn();
    }
    #[cfg(not(target_os = "windows"))]
    {
        let dir = std::path::Path::new(path)
            .parent()
            .map(|p| p.to_string_lossy().to_string())
            .unwrap_or_else(|| ".".to_string());
        let _ = std::process::Command::new("xdg-open").arg(dir).spawn();
    }
}

/// 用系统默认浏览器打开一个 URL（Windows 用 cmd /c start，不弹控制台）。
fn open_url_external(url: &str) {
    let u = url.trim();
    if u.is_empty() {
        return;
    }
    #[cfg(target_os = "windows")]
    {
        let _ = std::process::Command::new("cmd")
            .args(["/c", "start", "", u])
            .spawn();
    }
    #[cfg(not(target_os = "windows"))]
    {
        let _ = std::process::Command::new("xdg-open").arg(u).spawn();
    }
}

/// 生成唯一中间结果 id（纳秒时间戳）。
fn new_im_id() -> String {
    format!(
        "im_{}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
    )
}

/// 文字正文超过此长度（字符）时，自动落到工作目录 `<id>.md`，JSON 内仅存预览。
const IM_TEXT_THRESHOLD: usize = 2000;

/// 中间结果类型的中文标签。
fn im_kind_label(k: &str) -> &str {
    match k {
        "text" => "文字",
        "file" => "文件",
        "link" => "链接",
        _ => k,
    }
}

/// 取一段预览文本（最多 120 字符，超出加省略号）。
fn preview_of(text: &str) -> String {
    let n = text.chars().count();
    if n <= 120 {
        return text.to_string();
    }
    let mut s: String = text.chars().take(120).collect();
    s.push('…');
    s
}

/// 在工作目录中生成不与现有文件重名的路径（重名时加 Unix 秒时间戳）。
fn unique_ws_name(ws: &std::path::PathBuf, fname: &str) -> std::path::PathBuf {
    let p = ws.join(fname);
    if !p.exists() {
        return p;
    }
    let stem = std::path::Path::new(fname)
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "file".to_string());
    let ext = std::path::Path::new(fname)
        .extension()
        .map(|s| format!(".{}", s.to_string_lossy()))
        .unwrap_or_default();
    ws.join(format!("{}_{}{}", stem, now_secs(), ext))
}

/// 递归把某节点的所有子任务（含各级后代）标记为已完成，并刷新其 updated 时间。
/// 用于「父任务标为完成时，其整棵子树自动完成」。
fn mark_children_done(node: &mut TaskNode) {
    let now = now_secs();
    for c in &mut node.children {
        if c.status != "done" {
            c.status = "done".to_string();
            c.updated = Some(now);
        }
        mark_children_done(c);
    }
}

/// 子任务全部完成后，自动把父任务标记为完成（向上传播）。
/// 从根的子节点开始后序遍历：若某节点的所有子任务都已是 done，且该节点自身还不是
/// done，则把它也标为 done。根节点(Project.root)本身不参与自动完成，交由用户手动控制。
/// 与 mark_children_done（父→子）互补，实现双向级联。
fn promote_done(root: &mut TaskNode) {
    let now = now_secs();
    for c in &mut root.children {
        promote_children(c, now);
    }
}

fn promote_children(node: &mut TaskNode, now: u64) {
    // 先递归处理后代
    for c in &mut node.children {
        promote_children(c, now);
    }
    // 后序：若所有子任务都完成、且自身未标记完成（非叶子才判定），则自动完成
    if !node.children.is_empty()
        && node.children.iter().all(|c| c.status == "done")
        && node.status != "done"
    {
        node.status = "done".to_string();
        node.updated = Some(now);
    }
}

// 由"距 1970-01-01 的天数"换算 (年,月,日)（Howard Hinnant 日期算法）
fn civil_from_days(z: i64) -> (i64, i64, i64) {
    let z = z + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    (y, m, d)
}

// 北京时间格式化(中国全年 UTC+8，无夏令时)
fn format_beijing(ts: u64) -> String {
    let local = ts as i64 + 8 * 3600;
    let days = local / 86400;
    let sod = local % 86400;
    let (y, m, d) = civil_from_days(days);
    let h = sod / 3600;
    let min = (sod % 3600) / 60;
    let s = sod % 60;
    format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}", y, m, d, h, min, s)
}

/// 计算过滤后需要显示的节点 id 集合：
/// 节点自身匹配（标题含 q 且状态符合）或存在匹配的后代时，纳入集合（从而保留祖先链）。
/// 返回自身或后代是否有匹配。
fn mark_visible(node: &TaskNode, q: &str, status: &str, out: &mut HashSet<String>) -> bool {
    let self_match = (q.is_empty() || node.title.contains(q))
        && (status == "all" || node.status == status);
    let mut any_child = false;
    for c in &node.children {
        if mark_visible(c, q, status, out) {
            any_child = true;
        }
    }
    if self_match || any_child {
        out.insert(node.id.clone());
        true
    } else {
        false
    }
}

// 在 parent_id 下新增一个子任务，默认 todo（红色待办），返回新节点 id
fn add_child(root: &mut TaskNode, parent_id: &str) -> Option<String> {
    let pid = new_task_id();
    if let Some(p) = find_node_mut(root, parent_id) {
        p.children.push(TaskNode {
            id: pid.clone(),
            title: String::new(),
            status: "todo".to_string(),
            summary: String::new(),
            messages: vec![],
            task_info: String::new(),
            created: Some(now_secs()),
            updated: Some(now_secs()),
            children: vec![],
            intermediates: vec![],
        });
        Some(pid)
    } else {
        None
    }
}

// 从树中删除 id 对应节点（不删根），返回是否删除成功
fn delete_node(root: &mut TaskNode, id: &str) -> bool {
    let mut removed = false;
    let mut i = 0;
    while i < root.children.len() {
        if root.children[i].id == id {
            root.children.remove(i);
            removed = true;
        } else {
            if delete_node(&mut root.children[i], id) {
                removed = true;
            }
            i += 1;
        }
    }
    removed
}

// 从树中摘除 id 对应节点（不删根），返回被摘除的节点（用于拖拽移动）
fn detach(root: &mut TaskNode, id: &str) -> Option<TaskNode> {
    let mut i = 0;
    while i < root.children.len() {
        if root.children[i].id == id {
            return Some(root.children.remove(i));
        }
        i += 1;
    }
    for c in &mut root.children {
        if let Some(n) = detach(c, id) {
            return Some(n);
        }
    }
    None
}

// 返回 id 节点的父节点 id
fn parent_id(root: &TaskNode, id: &str) -> Option<String> {
    for c in &root.children {
        if c.id == id {
            return Some(root.id.clone());
        }
        if let Some(p) = parent_id(c, id) {
            return Some(p);
        }
    }
    None
}

// 把 node 插入到 parent_id 的子节点列表第 idx 位（越界自动夹取）
fn insert_at(root: &mut TaskNode, parent_id: &str, idx: usize, node: TaskNode) -> bool {
    if root.id == parent_id {
        let i = idx.min(root.children.len());
        root.children.insert(i, node);
        return true;
    }
    for c in &mut root.children {
        if insert_at(c, parent_id, idx, node.clone()) {
            return true;
        }
    }
    false
}

// 判断 id 是否在 node 子树内（含自身）
fn contains(node: &TaskNode, id: &str) -> bool {
    if node.id == id {
        return true;
    }
    node.children.iter().any(|c| contains(c, id))
}

// 取 id 节点的不可变引用
fn find_node_ref<'a>(node: &'a TaskNode, id: &str) -> Option<&'a TaskNode> {
    if node.id == id {
        return Some(node);
    }
    for c in &node.children {
        if let Some(n) = find_node_ref(c, id) {
            return Some(n);
        }
    }
    None
}

// 收集 node 子树所有 id（用于拖拽时排除非法落点）
fn collect_subtree_ids(node: &TaskNode, out: &mut HashSet<String>) {
    out.insert(node.id.clone());
    for c in &node.children {
        collect_subtree_ids(c, out);
    }
}

/// 判断某节点子树当前是否处于"展开"状态（用于合并「展开/收缩全部子任务」按钮）：
/// - 自身在 collapsed 中 → 整棵不可见，视为"收起"；
/// - 否则只要任一后代不在 collapsed（即有一个展开的）就视为"展开"。
/// 用户规则：「只要子任务下有一个展开的就认为父任务为展开状态」。
fn subtree_expanded(node: &TaskNode, collapsed: &HashSet<String>) -> bool {
    if collapsed.contains(&node.id) {
        return false;
    }
    for c in &node.children {
        if !collapsed.contains(&c.id) || subtree_expanded(c, collapsed) {
            return true;
        }
    }
    false
}

fn status_color(s: &str) -> egui::Color32 {
    match s {
        "todo" => egui::Color32::from_rgb(224, 49, 49), // 待办：红
        "doing" => egui::Color32::from_rgb(26, 114, 214), // 进行中：蓝
        "failed" => egui::Color32::BLACK, // 已失败：黑
        "done" => egui::Color32::from_rgb(46, 160, 67), // 已完成：绿
        "parked" => egui::Color32::from_rgb(230, 180, 0), // 搁置：黄
        _ => egui::Color32::GRAY,
    }
}

fn status_label(s: &str) -> String {
    match s {
        "all" => "全部".to_string(),
        "todo" => "待办".to_string(),
        "doing" => "进行中".to_string(),
        "failed" => "已失败".to_string(),
        "done" => "已完成".to_string(),
        "parked" => "搁置".to_string(),
        _ => s.to_string(),
    }
}

/// 把中文输入法自动插入的弯引号/弯双引号规范化为 ASCII 直引号。
/// 部分输入法在「回车强制上屏」英文时会自动补一个 '（U+2019），落库后变成不可预期的弯符号。
fn normalize_quotes(s: &str) -> String {
    s.replace('\u{2019}', "'") // ’ → '
        .replace('\u{2018}', "'") // ‘ → '
        .replace('\u{201D}', "\"") // ” → "
        .replace('\u{201C}', "\"") // “ → "
}

/// 渲染「中间结果（过程产物）」区块。返回是否有改动。
/// 三类条目：文字(text, 多行编辑, 超长自动落盘工作目录)、文件(file, 拷入工作目录并可打开)、
/// 链接(link, 可打开)。删除走上层弹窗确认（默认保留），此处仅收集待删除 id。
fn render_intermediate_list(
    ui: &mut egui::Ui,
    title: &str,
    entries: &mut Vec<data::IntermediateEntry>,
    pid: &str,
    tid: &str,
    changed: &mut bool,
    file_open: &mut Option<String>,
    dir_open: &mut Option<String>,
    url_open: &mut Option<String>,
    delete_req: &mut Option<String>,
) {
    ui.separator();
    ui.label(egui::RichText::new(title).weak());
    ui.horizontal(|ui| {
        if ui.button("➕ 文字").clicked() {
            entries.push(data::IntermediateEntry {
                id: new_im_id(),
                title: "新文字记录".to_string(),
                kind: "text".to_string(),
                content: String::new(),
                file_path: None,
                link: None,
                created: Some(now_secs()),
            });
            *changed = true;
        }
        if ui.button("➕ 文件").clicked() {
            if let Some(src) = rfd::FileDialog::new()
                .set_title("选择要归入的中间结果文件")
                .pick_file()
            {
                let ws = data::intermediate_ws_path(pid, tid);
                let _ = std::fs::create_dir_all(&ws);
                let fname = src
                    .file_name()
                    .map(|s| s.to_string_lossy().to_string())
                    .unwrap_or_else(|| "file".to_string());
                let dest = unique_ws_name(&ws, &fname);
                if std::fs::copy(&src, &dest).is_ok() {
                    entries.push(data::IntermediateEntry {
                        id: new_im_id(),
                        title: fname.clone(),
                        kind: "file".to_string(),
                        content: String::new(),
                        file_path: Some(dest.to_string_lossy().to_string()),
                        link: None,
                        created: Some(now_secs()),
                    });
                    *changed = true;
                }
            }
        }
        if ui.button("➕ 链接").clicked() {
            entries.push(data::IntermediateEntry {
                id: new_im_id(),
                title: "新链接".to_string(),
                kind: "link".to_string(),
                content: String::new(),
                file_path: None,
                link: Some(String::new()),
                created: Some(now_secs()),
            });
            *changed = true;
        }
    });

    for e in entries.iter_mut() {
        ui.separator();
        ui.horizontal(|ui| {
            let mut title = e.title.clone();
            if ui
                .add(
                    egui::TextEdit::singleline(&mut title)
                        .id(egui::Id::new(("im_title", &e.id)))
                        .desired_width(f32::INFINITY),
                )
                .changed()
            {
                e.title = normalize_quotes(&title);
                *changed = true;
            }
            ui.label(format!("[{}]", im_kind_label(&e.kind)));
            ui.menu_button("⋯", |ui| {
                match e.kind.as_str() {
                    "file" => {
                        if ui.button("打开文件").clicked() {
                            if let Some(p) = &e.file_path {
                                *file_open = Some(p.clone());
                            }
                            ui.close_menu();
                        }
                        if ui.button("打开所在目录").clicked() {
                            if let Some(p) = &e.file_path {
                                *dir_open = Some(p.clone());
                            }
                            ui.close_menu();
                        }
                    }
                    "link" => {
                        if ui.button("打开链接").clicked() {
                            if let Some(l) = &e.link {
                                *url_open = Some(l.clone());
                            }
                            ui.close_menu();
                        }
                    }
                    _ => {}
                }
                if ui.button("删除").clicked() {
                    *delete_req = Some(e.id.clone());
                    ui.close_menu();
                }
            });
        });

        match e.kind.as_str() {
            "text" => {
                let mut buf = if let Some(fp) = &e.file_path {
                    std::fs::read_to_string(fp).unwrap_or_else(|_| e.content.clone())
                } else {
                    e.content.clone()
                };
                if ui
                    .add(
                        egui::TextEdit::multiline(&mut buf)
                            .id(egui::Id::new(("im_text", &e.id))),
                    )
                    .changed()
                {
                    let text = normalize_quotes(&buf);
                    if text.chars().count() > IM_TEXT_THRESHOLD {
                        let ws = data::intermediate_ws_path(pid, tid);
                        let _ = std::fs::create_dir_all(&ws);
                        let md = ws.join(format!("{}.md", e.id));
                        let _ = std::fs::write(&md, &text);
                        e.file_path = Some(md.to_string_lossy().to_string());
                        e.content = preview_of(&text);
                    } else {
                        e.content = text;
                    }
                    *changed = true;
                }
            }
            "file" => {
                if let Some(fp) = &e.file_path {
                    let fname = std::path::Path::new(fp)
                        .file_name()
                        .map(|s| s.to_string_lossy().to_string())
                        .unwrap_or_else(|| fp.clone());
                    let exists = std::path::Path::new(fp).exists();
                    ui.horizontal(|ui| {
                        let label = if exists {
                            egui::RichText::new(format!("📄 {}", fname))
                        } else {
                            egui::RichText::new(format!("📄 {} (不存在)", fname))
                                .color(egui::Color32::from_rgb(200, 80, 80))
                        };
                        if ui
                            .add(
                                egui::Label::new(label)
                                    .truncate()
                                    .sense(egui::Sense::click()),
                            )
                            .on_hover_cursor(egui::CursorIcon::PointingHand)
                            .on_hover_text(fp.as_str())
                            .clicked()
                        {
                            *file_open = Some(fp.clone());
                        }
                        if ui.button("打开").clicked() {
                            *file_open = Some(fp.clone());
                        }
                    });
                }
            }
            "link" => {
                let mut link = e.link.clone().unwrap_or_default();
                if ui
                    .add(
                        egui::TextEdit::singleline(&mut link)
                            .id(egui::Id::new(("im_link", &e.id)))
                            .desired_width(f32::INFINITY),
                    )
                    .changed()
                {
                    e.link = Some(link);
                    *changed = true;
                }
                if ui.button("打开链接").clicked() {
                    if let Some(l) = &e.link {
                        *url_open = Some(l.clone());
                    }
                }
            }
            _ => {}
        }
    }
}


/// 把一个项目树导出为 Markdown：
/// 根节点本身不输出标题，直接遍历其顶层子任务；已完成用 [x]，其余 [ ]。
fn export_markdown(project: &data::Project) -> String {
    let mut out = String::new();
    out.push_str(&format!("# {}\n\n", project.name));
    fn walk(node: &TaskNode, depth: usize, out: &mut String) {
        if node.id == "root" {
            for c in &node.children {
                walk(c, 0, out);
            }
            return;
        }
        let indent = "  ".repeat(depth);
        let mark = if node.status == "done" { "x" } else { " " };
        let title = if node.title.is_empty() {
            "(未命名)".to_string()
        } else {
            node.title.clone()
        };
        out.push_str(&format!(
            "{} - [{}] **{}** ({})",
            indent,
            mark,
            title,
            status_label(&node.status)
        ));
        if node.created.is_some() || node.updated.is_some() {
            let c = node.created.map(format_beijing).unwrap_or_else(|| "—".to_string());
            let u = node.updated.map(format_beijing).unwrap_or_else(|| "—".to_string());
            out.push_str(&format!("  · 创建 {} / 修改 {}", c, u));
        }
        out.push('\n');
        if !node.summary.is_empty() {
            out.push_str(&format!("{}   - 摘要: {}\n", indent, node.summary));
        }
        if !node.task_info.is_empty() {
            out.push_str(&format!("{}   - 任务信息: {}\n", indent, node.task_info));
        }
        for m in &node.messages {
            let role = match m.role.as_str() {
                "user" => "用户",
                "assistant" => "助手",
                "note" => "笔记",
                _ => &m.role,
            };
            out.push_str(&format!("{}   - {}: {}\n", indent, role, m.content));
        }
        if !node.intermediates.is_empty() {
            out.push_str(&format!("{}   - 中间结果:\n", indent));
            for e in &node.intermediates {
                let body = if !e.content.is_empty() {
                    e.content.clone()
                } else if e.file_path.is_some() {
                    "(已存工作目录)".to_string()
                } else {
                    String::new()
                };
                match e.kind.as_str() {
                    "text" => out.push_str(&format!(
                        "{}     · [文字] {}: {}\n",
                        indent,
                        e.title,
                        body
                    )),
                    "file" => out.push_str(&format!(
                        "{}     · [文件] {} ({})\n",
                        indent,
                        e.title,
                        e.file_path.clone().unwrap_or_default()
                    )),
                    "link" => out.push_str(&format!(
                        "{}     · [链接] {}: {}\n",
                        indent,
                        e.title,
                        e.link.clone().unwrap_or_default()
                    )),
                    _ => {}
                }
            }
        }
        for c in &node.children {
            walk(c, depth + 1, out);
        }
    }
    walk(&project.root, 0, &mut out);
    // 项目级里程碑（复用中间结果结构，仅标题不同）
    if !project.intermediates.is_empty() {
        out.push_str("\n## 项目里程碑\n\n");
        for e in &project.intermediates {
            let body = if !e.content.is_empty() {
                e.content.clone()
            } else if e.file_path.is_some() {
                "(已存工作目录)".to_string()
            } else {
                String::new()
            };
            match e.kind.as_str() {
                "text" => out.push_str(&format!("- [文字] {}: {}\n", e.title, body)),
                "file" => out.push_str(&format!(
                    "- [文件] {} ({})\n",
                    e.title,
                    e.file_path.clone().unwrap_or_default()
                )),
                "link" => out.push_str(&format!(
                    "- [链接] {}: {}\n",
                    e.title,
                    e.link.clone().unwrap_or_default()
                )),
                _ => {}
            }
        }
    }
    out
}

/// 把项目名清洗为安全的文件名
fn sanitize_filename(name: &str) -> String {
    let s: String = name
        .chars()
        .map(|c| {
            if c.is_alphanumeric() || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')' {
                c
            } else {
                '_'
            }
        })
        .collect();
    let s = s.trim().to_string();
    if s.is_empty() {
        "项目".to_string()
    } else {
        s
    }
}

fn level_color(depth: usize) -> egui::Color32 {
    // 按层级循环取浅色：同一层级同色、不同层级异色，强化任务层级
    const PALETTE: [egui::Color32; 6] = [
        egui::Color32::from_rgb(255, 224, 224), // 浅红
        egui::Color32::from_rgb(222, 235, 255), // 浅蓝
        egui::Color32::from_rgb(223, 248, 225), // 浅绿
        egui::Color32::from_rgb(255, 245, 210), // 浅黄
        egui::Color32::from_rgb(236, 228, 255), // 浅紫
        egui::Color32::from_rgb(222, 247, 255), // 浅青
    ];
    PALETTE[depth % PALETTE.len()]
}

// 项目配色：折叠栏小色块用，按序号循环取鲜明色，便于一眼区分不同项目
fn project_color(i: usize) -> egui::Color32 {
    const PALETTE: [egui::Color32; 6] = [
        egui::Color32::from_rgb(224, 49, 49),   // 红
        egui::Color32::from_rgb(26, 114, 214),  // 蓝
        egui::Color32::from_rgb(46, 160, 67),   // 绿
        egui::Color32::from_rgb(230, 180, 0),   // 黄
        egui::Color32::from_rgb(150, 80, 200),  // 紫
        egui::Color32::from_rgb(0, 160, 160),   // 青
    ];
    PALETTE[i % PALETTE.len()]
}

fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        s.to_string()
    } else {
        format!("{}…", s.chars().take(max).collect::<String>())
    }
}

impl App {
    // ===== 左侧树（竖线 + 折叠） =====
    // 返回 (是否改动, 本节点行的垂直中心 y)，供父节点画连线用
    /// 递归收集某节点下的所有后代 id（不含节点自身），用于「展开/收缩全部子任务」。
    fn collect_descendants(node: &TaskNode) -> Vec<String> {
        let mut out = Vec::new();
        for c in &node.children {
            out.push(c.id.clone());
            out.extend(Self::collect_descendants(c));
        }
        out
    }

    fn render_tree(
        ui: &mut egui::Ui,
        node: &mut TaskNode,
        depth: usize,
        selected: &mut String,
        collapsed: &mut HashSet<String>,
        color_by_level: bool,
        pending_add: &mut Option<String>,
        pending_del: &mut Option<String>,
        pending_expand_all: &mut Option<String>,
        pending_collapse_all: &mut Option<String>,
        drag_node: &mut Option<String>,
        drop_target: &mut Option<(String, DropPos)>,
        renaming: &mut Option<String>,
        renaming_needs_focus: &mut bool,
        // IME 合成态 / 开启态：输入法组字中或处于开启态（含英文模式）时为 true，
        // 此时回车/Tab 视为输入法确认键，不应提交重命名
        ime_active: bool,
        invalid: &HashSet<String>,
        // 过滤：Some 时只渲染集合内的节点（且强制展开以显示匹配后代）；None 表示不过滤
        visible: Option<&HashSet<String>>,
        // 子→父级联标记：当某任务状态被修改时置 true，update 中据此向上提升父任务
        pending_promote: &mut bool,
        // IME 粘滞合成态：HOLD 分支里清掉它，使下次按键成为真正提交
        ime_composing: &mut bool,
        last_ime_time: &mut f64,
        // Tab 提交重命名时，把「当前节点 id」带回给调用方，由其创建同级任务并继续重命名
        // （避免在本闭包内直接拿 &mut self，也避免 egui 的 Tab 焦点遍历把焦点甩到顶栏/详情区导致退出重命名）
        pending_sibling: &mut Option<String>,
        // 本帧刚由 Tab 创建的同级节点 id（见 App.just_created_rename）：该节点本帧读到的 Tab 视为
        // 「创建它自己的那个 Tab」，必须忽略其提交/排队，避免一次 Tab 建出两个同级任务
        just_created: &Option<String>,
    ) -> (bool, f32) {
        let mut changed = false;
        // 大纲行距收紧，避免层级之间显得松散（导图不受影响，它走另一条渲染路径）
        ui.style_mut().spacing.item_spacing.y = 1.0;
        let base_x = ui.cursor().min.x;
        let row_w = ui.available_width();
        let indent = depth as f32 * INDENT;
        // 竖脊 x 对齐到父节点状态圆点中心，使竖线从圆点正下方发出
        let line_x = base_x + indent + 18.0 + 8.0;
        let has_children = !node.children.is_empty();
        let is_collapsed = collapsed.contains(&node.id);
        // 过滤激活时强制展开，确保匹配的后代可见
        let expand = visible.is_some() || !is_collapsed;

        // 记录状态圆点的响应，供下方弹出选单定位
        let mut dot_resp: Option<egui::Response> = None;

        let row_top = ui.cursor().min.y;
        let mut title_resp: Option<egui::Response> = None;
        ui.horizontal(|ui| {
            // 按层级着色的整行背景
            if color_by_level {
                let row_left = ui.cursor().min.x;
                let row_w = ui.available_width();
                let row_h = ui.text_style_height(&egui::TextStyle::Body) + 6.0;
                let rect = egui::Rect::from_min_size(
                    egui::pos2(row_left, row_top),
                    egui::vec2(row_w, row_h),
                );
                ui.painter()
                    .rect_filled(rect, egui::CornerRadius::same(4), level_color(depth));
            }
            // 折叠按钮区域：移到缩进之前，统一位于每行最左，与左栏「项目」标题旁控件左对齐
            let fold_w = 18.0;
            let (fr, fold_resp) =
                ui.allocate_exact_size(egui::vec2(fold_w, 18.0), egui::Sense::CLICK);
            ui.add_space(indent);
            if has_children {
                let sym = if is_collapsed { "+" } else { "−" };
                let resp = fold_resp.on_hover_cursor(egui::CursorIcon::PointingHand);
                // 圆形折叠按钮：柔和填充 + 细描边 + 悬停淡蓝，比方块更精致
                let c = fr.center();
                let r = 7.5;
                let bg = if resp.hovered() {
                    egui::Color32::from_rgb(198, 214, 244)
                } else {
                    egui::Color32::from_rgb(232, 236, 245)
                };
                ui.painter().circle_filled(c, r, bg);
                ui.painter().circle_stroke(
                    c,
                    r,
                    egui::Stroke::new(1.25, egui::Color32::from_rgb(150, 160, 182)),
                );
                ui.painter().text(
                    c,
                    egui::Align2::CENTER_CENTER,
                    sym,
                    egui::FontId::proportional(16.0),
                    egui::Color32::from_rgb(45, 52, 72),
                );
                if resp.clicked() {
                    if is_collapsed {
                        collapsed.remove(&node.id);
                    } else {
                        collapsed.insert(node.id.clone());
                    }
                }
            }
            // 叶子节点：同宽占位区，不画任何东西（保证与父节点行标题左对齐）
            // 状态实心圆点：点击弹出切换选单（手型光标）
            let (dot_rect, _) =
                ui.allocate_exact_size(egui::vec2(16.0, 16.0), egui::Sense::hover());
            ui.painter()
                .circle_filled(dot_rect.center(), 6.0, status_color(&node.status));
            let resp = ui
                .interact(
                    dot_rect,
                    ui.id().with(("status_dot", node.id.clone())),
                    egui::Sense::CLICK,
                )
                .on_hover_cursor(egui::CursorIcon::PointingHand);
            if resp.clicked() {
                let id = egui::Id::new(("status_menu", node.id.clone()));
                ui.memory_mut(|mem| mem.toggle_popup(id));
            }
            dot_resp = Some(resp);
            // 标题更贴近状态圆点（减小圆点-文字水平间距）
            ui.style_mut().spacing.item_spacing.x = 2.0;
            let is_sel = *selected == node.id;
            let is_renaming = renaming.as_deref() == Some(node.id.as_str());
            let tr: egui::Response;
            if is_renaming {
                // 行内重命名：新建子任务后自动进入，回车或点击别处提交
                let edit_id = egui::Id::new(format!("rename_{}", node.id));
                let mut buf = node.title.clone();
                let r = ui.add(
                    egui::TextEdit::singleline(&mut buf)
                        .id(edit_id)
                        .desired_width(140.0)
                        .lock_focus(true)
                        .hint_text("输入名称"),
                );
                if r.changed() {
                    node.title = normalize_quotes(&buf);
                    changed = true;
                }
                // 仅在进入重命名后的下一帧请求一次聚焦；此后不再抢焦点，
                // 这样点击别处时 TextEdit 能正常失焦 -> lost_focus 触发 -> 自动提交命名
                if *renaming_needs_focus {
                    // 进入重命名时清掉粘滞的 IME 合成态，避免上次组字残留状态误判本次回车
                    *ime_composing = false;
                    *last_ime_time = -10.0;
                    ui.ctx().memory_mut(|m| m.request_focus(edit_id));
                    *renaming_needs_focus = false;
                }
                // 方案 B（区分失焦原因）+ 方案 D（IME 泄漏 Enter 抢回焦点）：
                // egui 单行框按 Enter 会主动交出焦点，输入法确认候选泄漏的 Enter 也会触发失焦，
                // 若无条件用 lost_focus 提交，就会绕过 ime_active 守卫提前结束编辑。这里把
                // 「因按键失焦」和「因点击别处失焦」分开处理。
                let enter = ui.input(|i| i.key_pressed(egui::Key::Enter));
                let tab = ui.input(|i| i.key_pressed(egui::Key::Tab));
                let esc = ui.input(|i| i.key_pressed(egui::Key::Escape));
                let lost = r.lost_focus();
                // 点击别处失焦（非按键引起）：无论输入法状态，都提交
                let click_away = lost && !enter && !tab && !esc;
                // 按键提交：仅在输入法完全空闲时，Enter/Tab/Esc 才提交
                let key_commit = !ime_active && (enter || tab || esc);
                // 本帧刚由 Tab 创建的同级节点：它「读到的 Tab」就是创建它自己的那个键，
                // 不能让它再次提交/排队（否则一次 Tab 建出两个同级）。忽略即可，重命名态保持。
                let tab_fresh = tab && just_created.as_deref() == Some(node.id.as_str());
                if click_away || (key_commit && !tab_fresh) {
                    if node.title.trim().is_empty() {
                        node.title = "新任务".to_string();
                        changed = true;
                    }
                    *renaming = None;
                    // Tab 提交（且非刚创建的节点）：不直接退出重命名，而是把当前节点 id 带回，
                    // 由调用方创建「同级任务」并继续进入其重命名（大纲类软件的通用交互：
                    // Tab 在条目间顺延新建）。这样既不丢失本次输入，也不会让 egui 的 Tab
                    // 焦点遍历把焦点甩到顶栏/详情区导致退出重命名。
                    if tab && !tab_fresh {
                        *pending_sibling = Some(node.id.clone());
                    }
                } else if (enter || tab || esc) && ime_active {
                    // 输入法确认候选的回车/Tab/Esc：不提交。清掉粘滞合成态，使下一次按键
                    // 成为真正的提交；同时把焦点抢回，让用户继续把名字打完。
                    *ime_composing = false;
                    *last_ime_time = -10.0;
                    *renaming_needs_focus = true;
                } else if lost && ime_active {
                    // 仅因 IME 导致失焦（无显式按键）：抢回焦点继续编辑，不提交
                    *renaming_needs_focus = true;
                }
                tr = r;
            } else {
                // 标题区：点击选中、拖动移动（占满剩余宽度，整条行都可点/可拖）
                let row_h = ui.text_style_height(&egui::TextStyle::Body) + 6.0;
                let avail = ui.available_width();
                // 标题占满剩余宽度
                let title_w = (avail - 2.0).max(12.0);
                let (trect, tresp) = ui.allocate_exact_size(
                    egui::vec2(title_w, row_h),
                    egui::Sense::click_and_drag(),
                );
                // 选中态深底白字；悬停浅底
                if is_sel {
                    ui.painter().rect_filled(
                        trect,
                        egui::CornerRadius::same(4),
                        egui::Color32::from_rgb(86, 96, 128),
                    );
                } else if tresp.hovered() {
                    ui.painter().rect_filled(
                        trect,
                        egui::CornerRadius::same(4),
                        egui::Color32::from_rgb(226, 230, 237),
                    );
                }
                let text_col = if is_sel {
                    egui::Color32::WHITE
                } else {
                    egui::Color32::from_rgb(45, 50, 60)
                };
                let font_sz = ui.text_style_height(&egui::TextStyle::Body);
                // 标题文字裁切到标题矩形内，避免与右侧文件标志重叠
                ui.painter()
                    .with_clip_rect(trect)
                    .text(
                        egui::pos2(trect.min.x + 4.0, trect.center().y),
                        egui::Align2::LEFT_CENTER,
                        &node.title,
                        egui::FontId::proportional(font_sz),
                        text_col,
                    );
                if tresp.hovered() {
                    ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
                }
                if tresp.clicked() {
                    *selected = node.id.clone();
                }
                // 双击进入行内重命名
                if tresp.double_clicked() {
                    *renaming = Some(node.id.clone());
                    *renaming_needs_focus = true;
                }
                // 拖动整行即可移动（拖动瞬间自动选中，并触发拖拽）
                if node.id != "root" && tresp.drag_started() {
                    *selected = node.id.clone();
                    *drag_node = Some(node.id.clone());
                }
                tr = tresp;
            }
            title_resp = Some(tr);
        });
        let row_bottom = ui.cursor().min.y;
        let row_center = (row_top + row_bottom) / 2.0;

        // 整行矩形（用于拖拽落点几何判定 + 插入指示线），不注册交互层，
        // 以免与折叠按钮 / 状态点 / 标题的点击冲突（拖拽由左侧两列三点手柄发起）
        let row_rect = egui::Rect::from_min_max(
            egui::pos2(base_x, row_top),
            egui::pos2(base_x + row_w, row_bottom),
        );
        // 拖拽中：根据指针在悬停行内的纵向位置判定落点模式，并显示指示
        // 折叠态：上 25% → 插到前面（同级），下 25% → 插到后面（同级），中间 50% → 变为其下属子任务
        // 展开态：整行绝大部分 → 变为其下属子任务（拖到展开任务上即嵌套），仅顶部薄边 → 插到前面（同级）
        let hover_pos = ui.ctx().pointer_hover_pos();
        if let Some(d) = drag_node.clone() {
            if d != node.id && node.id != "root" && !invalid.contains(&node.id) {
                if let Some(p) = hover_pos {
                    if row_rect.contains(p) {
                        let h = row_bottom - row_top;
                        let rel = (p.y - row_top) / h; // 0=顶 1=底
                        // 展开态：整行绝大部分区域视为「变为其子任务」，仅顶部薄边可插到前面（同级）。
                        //   这样把任务拖到展开的任务上时，直观变成子任务，而不是落到底部 25% 被判成同级。
                        // 折叠态：维持「前 25% / 后 25% = 同级，中间 = 子任务」，与折叠时面积有限一致。
                        let expanded = !collapsed.contains(&node.id);
                        let (pos, indicator) = if expanded {
                            if rel < 0.15 {
                                (DropPos::Before, 0)
                            } else {
                                (DropPos::Child, 2)
                            }
                        } else if rel < 0.25 {
                            (DropPos::Before, 0)
                        } else if rel > 0.75 {
                            (DropPos::After, 1)
                        } else {
                            (DropPos::Child, 2)
                        };
                        *drop_target = Some((node.id.clone(), pos));
                        match indicator {
                            0 | 1 => {
                                // 同级插入：蓝色横线指示
                                let line_col = egui::Color32::from_rgb(26, 114, 214);
                                let y = if indicator == 1 { row_bottom } else { row_top };
                                ui.painter().line_segment(
                                    [egui::pos2(base_x, y), egui::pos2(base_x + row_w, y)],
                                    (2.0, line_col),
                                );
                            }
                            _ => {
                                // 变为子任务：整行绿色高亮 + 绿框
                                let hi = egui::Rect::from_min_max(
                                    egui::pos2(base_x, row_top),
                                    egui::pos2(base_x + row_w, row_bottom),
                                )
                                .expand(2.0);
                                ui.painter().rect_filled(
                                    hi,
                                    egui::CornerRadius::same(4),
                                    egui::Color32::from_rgba_premultiplied(46, 160, 92, 46),
                                );
                                ui.painter().rect_stroke(
                                    hi,
                                    egui::CornerRadius::same(4),
                                    egui::Stroke::new(2.0, egui::Color32::from_rgb(46, 160, 92)),
                                    egui::StrokeKind::Outside,
                                );
                                // 行尾提示
                                ui.painter().text(
                                    egui::pos2(base_x + row_w - 4.0, row_center),
                                    egui::Align2::RIGHT_CENTER,
                                    "↳ 子任务",
                                    egui::FontId::proportional(11.0),
                                    egui::Color32::from_rgb(46, 160, 92),
                                );
                            }
                        }
                    }
                }
            }
        }

        // 右键任务：弹出「添加子任务 / 重命名 / 删除该任务 / 展开或收缩全部子任务」菜单
        if let Some(tr) = title_resp {
            tr.context_menu(|ui| {
                if ui.button("➕ 添加子任务").clicked() {
                    *pending_add = Some(node.id.clone());
                    ui.close_menu();
                }
                if node.id != "root" {
                    if ui.button("重命名").clicked() {
                        *renaming = Some(node.id.clone());
                        *renaming_needs_focus = true;
                        ui.close_menu();
                    }
                    if ui.button("删除该任务").clicked() {
                        *pending_del = Some(node.id.clone());
                        ui.close_menu();
                    }
                }
                if has_children {
                    // 合并「展开/收缩全部子任务」为单一按钮：根据当前子树展开/收起状态自动判定动作。
                    // 规则：只要子任务下有任意一个处于展开（不在 collapsed），即视为"展开"→按钮显示「收缩全部子任务」并执行收起；
                    // 否则视为"收起"→按钮显示「展开全部子任务」并执行展开。
                    let expanded = subtree_expanded(node, collapsed);
                    let label = if expanded {
                        "收缩全部子任务"
                    } else {
                        "展开全部子任务"
                    };
                    if ui.button(label).clicked() {
                        if expanded {
                            *pending_collapse_all = Some(node.id.clone());
                        } else {
                            *pending_expand_all = Some(node.id.clone());
                        }
                        ui.close_menu();
                    }
                }
            });
        }

        // 状态切换选单（右键式弹出：实心点 + 冒号 + 状态名）
        if let Some(resp) = &dot_resp {
            let menu_id = egui::Id::new(("status_menu", node.id.clone()));
            if ui.memory(|mem| mem.is_popup_open(menu_id)) {
                let mut picked: Option<String> = None;
                egui::popup::popup_below_widget(
                    ui,
                    menu_id,
                    resp,
                    egui::popup::PopupCloseBehavior::CloseOnClickOutside,
                    |ui| {
                        ui.set_min_width(130.0);
                        let hover_bg = egui::Color32::from_rgb(226, 230, 236);
                        for s in ["todo", "doing", "failed", "done", "parked"] {
                            // 字号略小于正文，避免弹选单显得过大
                            let font_sz = (ui.text_style_height(&egui::TextStyle::Body) * 0.88).max(11.0);
                            let font_id = egui::FontId::proportional(font_sz);
                            let row_h = font_sz + 8.0;
                            let rect = egui::Rect::from_min_size(
                                ui.cursor().min,
                                egui::vec2(ui.available_width(), row_h),
                            );
                            // 整行唯一交互层：负责 hover 手型 + 点击。
                            // 圆点与文字改用 painter 直接画，避免嵌套 label 抢走 hover 导致光标回退成箭头。
                            let resp = ui
                                .interact(
                                    rect,
                                    ui.id().with(("status_hit", s)),
                                    egui::Sense::CLICK | egui::Sense::HOVER,
                                )
                                .on_hover_cursor(egui::CursorIcon::PointingHand);
                            if resp.hovered() {
                                ui.painter().rect_filled(
                                    rect,
                                    egui::CornerRadius::same(3),
                                    hover_bg,
                                );
                            }
                            // painter 画圆点 + ": 状态名"（不注册 widget，不抢 hover）
                            let pad = 6.0;
                            ui.painter().circle_filled(
                                egui::pos2(rect.min.x + pad + 5.0, rect.center().y),
                                5.0,
                                status_color(s),
                            );
                            ui.painter().text(
                                egui::pos2(rect.min.x + pad + 16.0, rect.center().y),
                                egui::Align2::LEFT_CENTER,
                                format!(": {}", status_label(s)),
                                font_id,
                                egui::Color32::BLACK,
                            );
                            if resp.clicked() {
                                picked = Some(s.to_string());
                            }
                            ui.advance_cursor_after_rect(rect);
                        }
                    },
                );
                if let Some(s) = picked {
                    let is_done = s == "done";
                    node.status = s;
                    node.updated = Some(now_secs());
                    // 父任务标为完成 → 整棵子树自动完成
                    if is_done {
                        mark_children_done(node);
                        // 仅当新状态为"已完成"才触发子→父级联：
                        // 把节点改回"待办/进行中/暂停"时不应再自动把它强制标回完成。
                        *pending_promote = true;
                    }
                    changed = true;
                    ui.memory_mut(|mem| mem.close_popup());
                }
            }
        }

        // 子节点 + 连线（竖脊 + 水平短横，父子层级一目了然）
        let mut child_centers: Vec<f32> = Vec::new();
        if has_children && expand {
            for c in &mut node.children {
                // 过滤时只递归可见子节点（连线几何自动正确：竖脊连到末个可见子节点）
                if visible.map_or(false, |v| !v.contains(&c.id)) {
                    continue;
                }
                let (ch, cy) = Self::render_tree(
                    ui,
                    c,
                    depth + 1,
                    selected,
                    collapsed,
                    color_by_level,
                    pending_add,
                    pending_del,
                    pending_expand_all,
                    pending_collapse_all,
                    drag_node,
                    drop_target,
                    renaming,
                    renaming_needs_focus,
                    ime_active,
                    invalid,
                    visible,
                    pending_promote,
                    ime_composing,
                    last_ime_time,
                    pending_sibling,
                    just_created,
                );
                if ch {
                    changed = true;
                }
                child_centers.push(cy);
            }
        }

        // 连线：父行中心 -> 末子节点中心 的竖脊，再向每个子节点画水平短横
        if has_children && expand && !child_centers.is_empty() {
            let painter = ui.painter();
            // 横短终点延伸到子节点状态圆点中心，使连线真正接到圆点
            let child_line_x = base_x + (depth + 1) as f32 * INDENT + 18.0 + 8.0;
            // 连线：1.5px 柔和蓝灰，轻盈精致但仍清晰可见（仅大纲，不影响导图）
            let tree_col = egui::Color32::from_rgb(176, 184, 200);
            let tree_lw = 1.5;
            painter.line_segment(
                [
                    egui::pos2(line_x, row_center),
                    egui::pos2(line_x, *child_centers.last().unwrap()),
                ],
                (tree_lw, tree_col),
            );
            for &cy in &child_centers {
                painter.line_segment(
                    [egui::pos2(line_x, cy), egui::pos2(child_line_x, cy)],
                    (tree_lw, tree_col),
                );
            }
        }

        (changed, row_center)
    }

    // 大纲视图复用 render_tree（含竖线 + 折叠 + 状态色圆点），与左侧任务树完全一致

    /// 为当前选中的节点添加一个子任务，并进入行内重命名（供「详情」标题旁的按钮调用）。
    /// 保存当前左右栏展开-收起状态到磁盘, 供下次启动恢复。
    fn save_ui_state(&self) {
        data::save_ui(&data::UiState {
            left_open: self.left_open,
            right_open: self.right_open,
            collapsed: self.collapsed.iter().cloned().collect(),
            project_idx: self.project_idx,
            right_width: Some(self.right_width),
        });
    }

    fn add_child_to_selected(&mut self) {
        let node_id = self.selected.clone();
        let id = format!(
            "task_{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        );
        let mut added = false;
        if let Some(proj) = self.current_project_mut() {
            if let Some(node) = find_node_mut(&mut proj.root, &node_id) {
                // 新建子任务默认红色(todo)状态
                node.children.push(TaskNode {
                    id: id.clone(),
                    title: String::new(),
                    status: "todo".to_string(),
                    summary: String::new(),
                    messages: vec![],
            task_info: String::new(),
                    created: Some(now_secs()),
                    updated: Some(now_secs()),
                    children: vec![],
                    intermediates: vec![],
                });
                node.updated = Some(now_secs());
                added = true;
            }
        }
        if added {
            self.selected = id.clone();
            self.renaming = Some(id);
            self.renaming_needs_focus = true;
            // 父任务若处于折叠态，自动展开该级，让新建子任务可见
            self.collapsed.remove(&node_id);
            self.save_ui_state();
            let _ = data::save(&self.store);
        }
    }

    // 为当前选中任务创建「同级任务」（插在其后），并自动进入行内重命名
    fn add_sibling_to_selected(&mut self) {
        let node_id = self.selected.clone();
        let id = format!(
            "task_{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        );
        let mut added = false;
        if let Some(proj) = self.current_project_mut() {
            if let Some(pid) = parent_id(&proj.root, &node_id) {
                if let Some(parent) = find_node_mut(&mut proj.root, &pid) {
                    if let Some(pos) = parent.children.iter().position(|c| c.id == node_id) {
                        parent.children.insert(
                            pos + 1,
                            TaskNode {
                                id: id.clone(),
                                title: String::new(),
                                status: "todo".to_string(),
                                summary: String::new(),
                                messages: vec![],
                                task_info: String::new(),
                                created: Some(now_secs()),
                                updated: Some(now_secs()),
                                children: vec![],
                                intermediates: vec![],
                            },
                        );
                        parent.updated = Some(now_secs());
                        added = true;
                    }
                }
            } else if let Some(node) = find_node_mut(&mut proj.root, &node_id) {
                // 选中的是根节点或无父节点：作为其子任务追加到末尾
                node.children.push(TaskNode {
                    id: id.clone(),
                    title: String::new(),
                    status: "todo".to_string(),
                    summary: String::new(),
                    messages: vec![],
            task_info: String::new(),
                    created: Some(now_secs()),
                    updated: Some(now_secs()),
                    children: vec![],
                    intermediates: vec![],
                });
                node.updated = Some(now_secs());
                added = true;
            }
        }
        if added {
            self.selected = id.clone();
            self.renaming = Some(id.clone());
            self.renaming_needs_focus = true;
            // 标记本帧刚创建，使该新节点的重命名框在本帧读到的 Tab 键被忽略
            // （否则同一个 Tab 会被新节点再次消费，误建出第二个同级任务）
            self.just_created_rename = Some(id);
            let _ = data::save(&self.store);
        }
    }

    // ===== 右侧详情 =====
    fn render_inspector(&mut self, ui: &mut egui::Ui) {
        let node_id = self.selected.clone();
        let mut changed = false;
        // 子→父级联本地标记（避免在闭包内借 self 与 node 冲突）
        let mut promote = false;
        // 中间结果：待执行的打开 / 打开目录 / 打开链接 / 待删除条目 id
        let mut im_file_open: Option<String> = None;
        let mut im_dir_open: Option<String> = None;
        let mut im_url_open: Option<String> = None;
        let mut im_delete_req: Option<String> = None;
        // 当前项目 id 与选中任务 id（中间结果工作目录与条目 id 需要）
        let pid = self
            .store
            .projects
            .get(self.project_idx)
            .map(|p| p.id.clone())
            .unwrap_or_default();
        let tid = self.selected.clone();
        if self.current_project_mut().is_none() {
            ui.label("暂无项目");
            return;
        }
        // ===== 任务级：名称 + 任务信息（详情顶部）=====
        if let Some(node) = find_node_mut(&mut self.current_project_mut().unwrap().root, &node_id) {
            ui.separator();
            ui.vertical(|ui| {
                ui.label(egui::RichText::new("名称").weak().small());
                // 名称可直接编辑（也可在左侧树中行内重命名）
                let mut title = node.title.clone();
                if ui
                    .add(
                        egui::TextEdit::singleline(&mut title)
                            .id(egui::Id::new("task_title_edit"))
                            .desired_width(f32::INFINITY),
                    )
                    .changed()
                {
                    node.title = normalize_quotes(&title);
                    changed = true;
                }
            });
            ui.horizontal(|ui| {
                let (r, _) =
                    ui.allocate_exact_size(egui::vec2(14.0, 14.0), egui::Sense::hover());
                ui.painter()
                    .circle_filled(r.center(), 5.0, status_color(&node.status));
                ui.label("状态:");
                egui::ComboBox::from_label("")
                    .selected_text(status_label(&node.status))
                    .show_ui(ui, |ui| {
                        for s in ["todo", "doing", "failed", "done", "parked"] {
                            if ui
                                .selectable_label(node.status == s, status_label(s))
                                .clicked()
                            {
                                node.status = s.to_string();
                                // 父任务标为完成 → 整棵子树自动完成
                                if s == "done" {
                                    mark_children_done(node);
                                    // 仅当新状态为"已完成"才触发子→父级联：
                                    // 否则用户把父任务改回"待办/进行中/暂停"时，
                                    // promote_done 会把"子全完成"的父任务又强制标回完成，导致改不回去。
                                    promote = true;
                                }
                                changed = true;
                            }
                        }
                    });
            });
            ui.separator();
            ui.label(egui::RichText::new("任务信息").weak());
            let mut task_info = node.task_info.clone();
            if ui
                .add(
                    egui::TextEdit::multiline(&mut task_info)
                        .id(egui::Id::new("task_info")),
                )
                .changed()
            {
                node.task_info = normalize_quotes(&task_info);
                changed = true;
            }
            if changed {
                node.updated = Some(now_secs());
            }
        }
        // ===== 项目级：里程碑（复用中间结果代码，仅标题不同）=====
        {
            let mut pchanged = false;
            if let Some(p) = self.current_project_mut() {
                render_intermediate_list(
                    ui,
                    "项目里程碑（过程产物）",
                    &mut p.intermediates,
                    &pid,
                    "project",
                    &mut pchanged,
                    &mut im_file_open,
                    &mut im_dir_open,
                    &mut im_url_open,
                    &mut im_delete_req,
                );
            }
            changed |= pchanged;
        }
        if let Some(node) = find_node_mut(&mut self.current_project_mut().unwrap().root, &node_id) {
            // ===== 中间结果（过程产物）：名字 + 内容 =====
            render_intermediate_list(
                ui,
                "中间结果（过程产物）",
                &mut node.intermediates,
                &pid,
                &tid,
                &mut changed,
                &mut im_file_open,
                &mut im_dir_open,
                &mut im_url_open,
                &mut im_delete_req,
            );
            if changed {
                node.updated = Some(now_secs());
            }
        } else {
            ui.label("未选中节点");
        }
        // 中间结果操作：打开文件 / 打开目录 / 打开链接；删除走弹窗确认
        if let Some(f) = im_file_open {
            open_file_external(&f);
        }
        if let Some(f) = im_dir_open {
            open_containing_dir(&f);
        }
        if let Some(u) = im_url_open {
            open_url_external(&u);
        }
        if let Some(del_id) = im_delete_req {
            self.confirm_delete_im = Some(del_id);
        }
        // 子→父级联：子任务全部完成时，把父任务（向上递归）自动标为完成
        if promote {
            self.promote_pending = true;
        }
        if self.promote_pending {
            if let Some(p) = self.current_project_mut() {
                promote_done(&mut p.root);
            }
            self.promote_pending = false;
            changed = true;
        }
        if changed {
            let _ = data::save(&self.store);
        }
    }

    // ===== 思维导图：分层布局（逻辑坐标） =====
    fn do_layout(
        node: &TaskNode,
        depth: usize,
        collapsed: &HashSet<String>,
        positions: &mut HashMap<String, (f32, f32)>,
        y_cursor: &mut f32,
    ) {
        let x = depth as f32 * LEVEL_W;
        let has = !node.children.is_empty();
        let is_col = collapsed.contains(&node.id);
        let y;
        if !has || is_col {
            y = *y_cursor;
            *y_cursor += ROW_H;
        } else {
            let start = *y_cursor;
            for c in &node.children {
                Self::do_layout(c, depth + 1, collapsed, positions, y_cursor);
            }
            let last = *y_cursor - ROW_H;
            y = (start + last) / 2.0;
        }
        positions.insert(node.id.clone(), (x, y));
    }

    // ===== 思维导图：自绘 + 交互 =====
    fn render_mindmap(&mut self, ui: &mut egui::Ui) {
        let (resp_rect, response) =
            ui.allocate_exact_size(ui.available_size(), egui::Sense::click_and_drag());
        ui.painter().rect_filled(resp_rect, 0, egui::Color32::from_rgb(250, 250, 252));

        let root = &self.store.projects[self.project_idx].root;

        // 1) 算布局（逻辑坐标）
        let mut positions: HashMap<String, (f32, f32)> = HashMap::new();
        let mut y_cursor = 0.0f32;
        Self::do_layout(root, 0, &self.collapsed, &mut positions, &mut y_cursor);

        // 2) 平移（拖拽空白）
        if response.dragged() {
            self.pan += response.drag_delta();
        }
        // 3) 缩放（滚轮，以鼠标为中心）
        let mut zoom_delta = 1.0f32;
        ui.ctx().input(|i| {
            for e in &i.events {
                if let egui::Event::MouseWheel { delta, .. } = e {
                    if delta.y > 0.0 {
                        zoom_delta *= 1.1;
                    } else {
                        zoom_delta /= 1.1;
                    }
                }
            }
        });
        if zoom_delta != 1.0 {
            let hover = response.hover_pos().unwrap_or(resp_rect.center()) - resp_rect.min;
            let lx = (hover.x - ORIGIN_X - self.pan.x) / self.zoom;
            let ly = (hover.y - ORIGIN_Y - self.pan.y) / self.zoom;
            self.zoom = (self.zoom * zoom_delta).clamp(0.2, 4.0);
            self.pan.x = hover.x - ORIGIN_X - lx * self.zoom;
            self.pan.y = hover.y - ORIGIN_Y - ly * self.zoom;
        }

        // 逻辑坐标 → 画布内坐标（不含 resp_rect.min）
        let tsc = |lx: f32, ly: f32| -> egui::Vec2 {
            egui::vec2(
                ORIGIN_X + lx * self.zoom + self.pan.x,
                ORIGIN_Y + ly * self.zoom + self.pan.y,
            )
        };

        // 4) 点击：先判折叠按钮，否则选中节点
        if response.clicked() {
            let click = response.interact_pointer_pos().unwrap_or(resp_rect.min) - resp_rect.min;
            let mut handled = false;
            for (id, &(lx, ly)) in &positions {
                let node = match root.find(id) {
                    Some(n) => n,
                    None => continue,
                };
                if node.children.is_empty() {
                    continue;
                }
                let bt = tsc(lx, ly) + egui::vec2(NODE_W * self.zoom / 2.0 + 9.0, 0.0);
                if (bt - click).length() <= 9.0 {
                    if self.collapsed.contains(&node.id) {
                        self.collapsed.remove(&node.id);
                    } else {
                        self.collapsed.insert(node.id.clone());
                    }
                    // 折叠状态变化，落盘以便下次启动恢复
                    self.save_ui_state();
                    handled = true;
                    break;
                }
            }
            if !handled {
                for (id, &(lx, ly)) in &positions {
                    let c = tsc(lx, ly);
                    if (c - click).x.abs() <= NODE_W * self.zoom / 2.0
                        && (c - click).y.abs() <= NODE_H * self.zoom / 2.0
                    {
                        self.selected = id.clone();
                        break;
                    }
                }
            }
        }

        let painter = ui.painter();

        // 5) 画连线（父右 → 子左，折线）
        for (id, &(lx, ly)) in &positions {
            let node = match root.find(id) {
                Some(n) => n,
                None => continue,
            };
            if self.collapsed.contains(&node.id) {
                continue;
            }
            let p_right = tsc(lx, ly) + egui::vec2(NODE_W * self.zoom / 2.0, 0.0);
            for c in &node.children {
                if let Some(&(cx, cy)) = positions.get(&c.id) {
                    let c_left = tsc(cx, cy) - egui::vec2(NODE_W * self.zoom / 2.0, 0.0);
                    let mid_x = (p_right.x + c_left.x) / 2.0;
                    painter.add(egui::Shape::line(
                        vec![
                            resp_rect.min + p_right,
                            resp_rect.min + egui::vec2(mid_x, p_right.y),
                            resp_rect.min + egui::vec2(mid_x, c_left.y),
                            resp_rect.min + c_left,
                        ],
                        (1.5, LINE_COLOR),
                    ));
                }
            }
        }

        // 6) 画节点框
        for (id, &(lx, ly)) in &positions {
            let node = match root.find(id) {
                Some(n) => n,
                None => continue,
            };
            let c = tsc(lx, ly);
            let r = egui::Rect::from_center_size(
                resp_rect.min + c,
                egui::vec2(NODE_W * self.zoom, NODE_H * self.zoom),
            );
            let is_sel = self.selected == node.id;
            let bg = if is_sel {
                egui::Color32::from_rgb(238, 237, 254)
            } else {
                egui::Color32::WHITE
            };
            painter.rect_filled(r, egui::CornerRadius::same(6), bg);
            painter.rect_stroke(
                r,
                egui::CornerRadius::same(6),
                (2.0, status_color(&node.status)),
                egui::StrokeKind::Inside,
            );
            // 字体随 zoom 等比缩放，避免缩小后文字戳出框外
            let font_sz = (13.0 * self.zoom).max(6.0);
            let fold_sz = (12.0 * self.zoom).max(6.0);
            let fold_r = (8.0 * self.zoom).max(4.0);
            let label = truncate(&node.title, 10);
            painter.text(
                resp_rect.min + c,
                egui::Align2::CENTER_CENTER,
                label,
                egui::FontId::proportional(font_sz),
                egui::Color32::BLACK,
            );
            // 折叠按钮
            if !node.children.is_empty() {
                let bt_center = resp_rect.min + c + egui::vec2(NODE_W * self.zoom / 2.0 + 9.0 * self.zoom, 0.0);
                painter.circle_filled(bt_center, fold_r, egui::Color32::from_rgb(240, 240, 245));
                painter.circle_stroke(bt_center, fold_r, (1.0, LINE_COLOR));
                painter.text(
                    bt_center,
                    egui::Align2::CENTER_CENTER,
                    if self.collapsed.contains(&node.id) { "+" } else { "−" },
                    egui::FontId::proportional(fold_sz),
                    egui::Color32::DARK_GRAY,
                );
            }
        }
    }
}

impl Default for App {
    fn default() -> Self {
        let store = data::load().unwrap_or_else(|_| empty_store());
        // 读取上次关闭时保存的界面状态(左/右栏、折叠集合、当前项目)，使启动时一致。
        let ui = data::load_ui();
        // 当前项目索引：限制在有效范围内，越界则回退到首个项目。
        let project_idx = ui
            .project_idx
            .min(store.projects.len().saturating_sub(1));
        // 选中节点跟随当前项目根（恢复上次查看的项目）。
        let selected = store
            .projects
            .get(project_idx)
            .map(|p| p.root.id.clone())
            .unwrap_or_default();
        App {
            store,
            selected,
            view: ViewMode::Outline,
            project_idx,
            pan: egui::Vec2::ZERO,
            zoom: 1.0,
            left_open: ui.left_open,
            right_open: ui.right_open,
            right_width: ui.right_width.unwrap_or(320.0),
            collapsed: ui.collapsed.iter().cloned().collect(),
            color_by_level: false,
            search: String::new(),
            status_filter: "all".to_string(),
            last_mtime: projects_mtime(),
            last_ui_mtime: ui_state_mtime(),
            renaming: None,
            renaming_needs_focus: false,
            just_created_rename: None,
            renaming_project: None,
            scroll_to_project: None,
            drag_node: None,
            drop_target: None,
            drag_project: None,
            drop_project: None,
            ime_composing: false,
            last_ime_time: -10.0,
            ime_active: false,
            confirm_delete_im: None,
            ime_pending_enter_swallow: false,
            debug_ime_events: Vec::new(),
            debug_ime_on: true,
            export_msg: None,
            export_msg_until: 0.0,
            promote_pending: false,
            promote_on_load: true,
        }
    }
}

fn empty_store() -> Store {
    Store {
        projects: vec![data::Project {
            id: "root".to_string(),
            name: "未命名项目".to_string(),
            root: TaskNode {
                id: "root".to_string(),
                title: "根".to_string(),
                status: "todo".to_string(),
                summary: String::new(),
                messages: vec![],
            task_info: String::new(),
                created: None,
                updated: None,
                children: vec![],
            intermediates: vec![],
            },
            cursor: String::new(),
            intermediates: vec![],
        }],
        current_id: "root".to_string(),
    }
}


impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 把编译时间戳写进窗口标题：详情面板调宽 bug 反复出现时，常因旧 exe 实例未真正关闭，
        // 点 .lnk 只是重新聚焦旧实例。看标题即可确认跑的是不是最新 build。
        ctx.send_viewport_cmd(egui::ViewportCommand::Title(format!(
            "任务树 · build {}",
            BUILD_TS
        )));

        // IME 合成态检测（粘滞跨帧）：输入法（拼音/日文/英文模式）组字过程中按下的回车/Tab
        // 是用来确认候选词的，不能当作「提交重命名」。微信等输入法在中文模式打英文时，确认英文
        // 是「静默提交」——按回车时不发 IME(Commit)，只发一个光秃秃的 Key(Enter)，且组字状态
        // 不会被清空。若只看「本帧」是否组字或 250ms 冷却窗，用户打完字母停顿一两秒再回车确认
        // 就会被误判为提交（已用日志复现：停顿 1.55s 后回车，ime_active 已掉回 false）。
        // 因此把「正在组字」做成跨帧粘滞状态：自最后一次非空 Preedit 起一直为 true，直到收到
        // 明确的清空事件（IME(Commit) / 空 Preedit / IME(Disabled)）才变 false。这样无论停顿多久，
        // 回车确认时合成态仍为 true，不会误提交；回车提交逻辑里再清掉合成态，使下一次按键成为真正提交。
        let mut ime_event_this_frame = false;
        ctx.input(|i| {
            for ev in &i.events {
                match ev {
                    egui::Event::Ime(ie) => {
                        ime_event_this_frame = true;
                        match ie {
                            egui::ImeEvent::Preedit(s) => {
                                self.ime_composing = !s.is_empty();
                            }
                            egui::ImeEvent::Commit(_) => {
                                self.ime_composing = false;
                            }
                            egui::ImeEvent::Disabled => {
                                self.ime_composing = false;
                            }
                            egui::ImeEvent::Enabled => {}
                        }
                    }
                    egui::Event::Key { .. } => {}
                    _ => {}
                }
            }
        });
        let now_t = ctx.input(|i| i.time);
        if ime_event_this_frame {
            self.last_ime_time = now_t;
        }
        // ime_active：粘滞合成态为真，或距上次 IME 事件 250ms 内（覆盖 Commit/Preedit 与 Enter 同帧差帧）。
        // 注意：不再用 request_repaint_after 在冷却窗内强制每帧重绘——IME 合成期间事件本就每帧产生，
        // 会把 250ms 窗口无限刷新成持续的 20fps 空转重绘（CPU 高占用的根因）。egui 在收到输入/动画时
        // 会自动重绘，ime_active 判定也不需要靠轮询刷新，故删除强制重绘。
        let ime_active =
            self.ime_composing || (now_t - self.last_ime_time) < 0.25;
        self.ime_active = ime_active;
        // —— 回车处理（分两类）——
        let focused_id = ctx.memory(|m| m.focused());
        let shift_held = ctx.input(|i| i.modifiers.shift);
        let enter_pressed = ctx.input(|i| i.key_pressed(egui::Key::Enter));
        // 「回车=确认」多行框：任务信息 + 中间结果文字。这些框里回车本意是「确认/上屏」，
        // 不应变成换行（用户实测英文确认回车会多出换行符）。
        let on_commit_box = focused_id.map_or(false, |id| {
            id == egui::Id::new("task_info")
                || self
                    .store
                    .projects
                    .get(self.project_idx)
                    .map_or(false, |p| {
                        p.intermediates.iter().any(|e| id == egui::Id::new(("im_text", &e.id)))
                            || p
                                .root
                                .find(&self.selected)
                                .map_or(false, |node| {
                                    node.intermediates
                                        .iter()
                                        .any(|e| id == egui::Id::new(("im_text", &e.id)))
                                })
                    })
        });

        // ── IME 哨兵标记：本帧有 IME 事件 + 焦点在 commit 框 → 下一次 Enter 视为确认键 ──
        if ime_event_this_frame && on_commit_box {
            self.ime_pending_enter_swallow = true;
        }
        // 焦点离开 commit 框 → 清哨兵（用户点了别处，输入会话结束）
        if !on_commit_box {
            self.ime_pending_enter_swallow = false;
        }

        // ── 诊断日志：每次按回车时记录全部关键状态 ──
        if enter_pressed {
            let dt = now_t - self.last_ime_time;
            let pending = self.ime_pending_enter_swallow;
            let reason = if self.ime_composing && focused_id.is_some() {
                "✅ 吞 (ime_composing 组字态)".to_string()
            } else if on_commit_box && !shift_held && pending {
                "✅ 吞 (哨兵触发)".to_string()
            } else if on_commit_box && !shift_held && !pending {
                "⏭ 放行 (哨兵未触发, dt=".to_owned() + &format!("{:.3}s", dt) + ")"
            } else if on_commit_box && shift_held {
                "⏭ 放行 (Shift+Enter)".to_string()
            } else if !on_commit_box {
                "⏭ 放行 (非commit框)".to_string()
            } else {
                "❓ 未知分支".to_string()
            };
            let fid = focused_id.map(|id| format!("{:?}", id)).unwrap_or_else(|| "None".into());
            let entry = format!(
                "[t={:.2}] composing={} active={} pending={} dt={:.3}s commit_box={} shift={} focus={} | {}",
                now_t, self.ime_composing, self.ime_active, pending, dt,
                on_commit_box, shift_held, &fid[..fid.len().min(40)], reason
            );
            self.debug_ime_events.push(entry);
            if self.debug_ime_events.len() > 20 {
                self.debug_ime_events.remove(0);
            }
        }

        // 1) 组字态（有非空预编辑串）：回车 / Tab 交给输入法自行上屏，吞掉避免多行框换行 / 单行框失焦。
        if self.ime_composing && focused_id.is_some() {
            ctx.input_mut(|i| {
                i.events.retain(|ev| {
                    !matches!(
                        ev,
                        egui::Event::Key { key, .. }
                            if *key == egui::Key::Enter || *key == egui::Key::Tab
                    )
                });
            });
            self.ime_composing = false;
            self.ime_pending_enter_swallow = false; // 吞过了，清哨兵
        }
        // 2) 哨兵触发：IME 参与过编辑（沉默提交型 IME），紧随的 Enter = 确认 → 吞一次
        else if on_commit_box && !shift_held && focused_id.is_some() {
            if self.ime_pending_enter_swallow {
                ctx.input_mut(|i| {
                    i.events.retain(|ev| {
                        !matches!(ev, egui::Event::Key { key, .. } if *key == egui::Key::Enter)
                    });
                });
                self.ime_pending_enter_swallow = false; // 吞一次就清，后续 Enter = 正常换行
            }
        }
        // 文件热重载：外部（MCP / agent / 其他编辑器）修改 projects.json 后实时生效，
        // 无需重启程序。仅替换 store，保留选中/折叠/视图/面板等所有 UI 会话状态。
        if let Some(mt) = projects_mtime() {
            if self.last_mtime != Some(mt) {
                if let Ok(store) = data::load() {
                    self.store = store;
                    // 项目被外部删除导致越界时，clamp 并重置选中到新当前项目的根
                    if self.project_idx >= self.store.projects.len() {
                        self.project_idx = self.store.projects.len().saturating_sub(1);
                        self.selected = self.store.projects
                            .get(self.project_idx)
                            .map(|p| p.root.id.clone())
                            .unwrap_or_default();
                    }
                }
                self.last_mtime = Some(mt);
            }
        }
        // UI 状态热重载：外部（MCP 等）修改 ui_state.json 的折叠集合后实时生效，
        // 无需重启程序。仅替换 self.collapsed，保留其它 UI 会话状态。
        if let Some(umt) = ui_state_mtime() {
            if self.last_ui_mtime != Some(umt) {
                let ui = data::load_ui();
                self.collapsed = ui.collapsed.iter().cloned().collect();
                if let Some(w) = ui.right_width {
                    self.right_width = w.clamp(240.0, 680.0);
                }
                self.last_ui_mtime = Some(umt);
            }
        }

        // 启动修正：首次加载时补齐历史数据中「子全部完成但父未标记」的节点
        if self.promote_on_load {
            for p in &mut self.store.projects {
                promote_done(&mut p.root);
            }
            let _ = data::save(&self.store);
            self.promote_on_load = false;
        }

        // 导出提示数秒后自动消失
        if self.export_msg_until > 0.0 && ctx.input(|i| i.time) > self.export_msg_until {
            self.export_msg = None;
            self.export_msg_until = 0.0;
        }

        egui::TopBottomPanel::top("topbar").show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.heading("Branch Task");
                ui.separator();
                // 大纲 / 导图切换：用 Button + Sense::CLICK（可点击但不可聚焦），
                // 避免 Tab 焦点遍历停留在顶栏按钮上出现待选框
                let tb = egui::Sense::CLICK;
                if ui
                    .add(
                        egui::Button::new("大纲")
                            .sense(tb)
                            .fill(if self.view == ViewMode::Outline {
                                egui::Color32::from_rgb(86, 96, 128)
                            } else {
                                egui::Color32::TRANSPARENT
                            }),
                    )
                    .clicked()
                {
                    self.view = ViewMode::Outline;
                }
                if ui
                    .add(
                        egui::Button::new("导图")
                            .sense(tb)
                            .fill(if self.view == ViewMode::Mindmap {
                                egui::Color32::from_rgb(86, 96, 128)
                            } else {
                                egui::Color32::TRANSPARENT
                            }),
                    )
                    .clicked()
                {
                    self.view = ViewMode::Mindmap;
                }
                // 大纲按层级着色开关
                if ui
                    .add(
                        egui::Button::new(if self.color_by_level {
                            "层级配色：开"
                        } else {
                            "层级配色：关"
                        })
                        .sense(egui::Sense::CLICK),
                    )
                    .on_hover_text("大纲按层级着色：同一层级同色，不同层级异色")
                    .clicked()
                {
                    self.color_by_level = !self.color_by_level;
                }
                // 导出当前项目树为 Markdown 文件
                if ui
                    .add(
                        egui::Button::new("导出MD")
                            .sense(egui::Sense::CLICK),
                    )
                    .on_hover_text("导出当前项目为 Markdown 文件（可选择保存路径）")
                    .clicked()
                {
                    if let Some(proj) = self.store.projects.get(self.project_idx) {
                        let md = export_markdown(proj);
                        let default_name = format!("{}.md", sanitize_filename(&proj.name));
                        // 弹出系统的“另存为”对话框，让用户选择保存路径
                        let picked = rfd::FileDialog::new()
                            .set_title("导出 Markdown")
                            .set_file_name(&default_name)
                            .add_filter("Markdown", &["md"])
                            .save_file();
                        match picked {
                            Some(path) => {
                                // 如果用户没带扩展名，补上 .md
                                let path = if path.extension().is_none() {
                                    path.with_extension("md")
                                } else {
                                    path
                                };
                                match std::fs::write(&path, md) {
                                    Ok(_) => {
                                        self.export_msg =
                                            Some(format!("已导出: {}", path.display()));
                                        self.export_msg_until = ctx.input(|i| i.time) + 5.0;
                                    }
                                    Err(e) => {
                                        self.export_msg = Some(format!("导出失败: {e}"));
                                        self.export_msg_until = ctx.input(|i| i.time) + 5.0;
                                    }
                                }
                            }
                            None => {
                                // 用户取消，不做任何操作
                            }
                        }
                    }
                }
                if let Some(msg) = &self.export_msg {
                    ui.label(msg);
                }
                // 搜索 / 筛选：空间足够时内联显示（搜索框宽度按剩余空间自适应）；
                // 放不下时收起为「...」按钮，点开是含搜索/状态/清除的下拉框，杜绝缩放重叠
                let avail = ui.available_width();
                const NON_SEARCH: f32 = 240.0; // 搜索框之外其余控件的估算宽度
                if avail >= NON_SEARCH + 70.0 {
                    let box_w = (avail - NON_SEARCH).min(180.0);
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.add(
                            egui::TextEdit::singleline(&mut self.search)
                                .hint_text("任务标题…")
                                .desired_width(box_w)
                                .id(egui::Id::new("top_search_box")),
                        );
                        ui.label("搜索:");
                        egui::ComboBox::from_label("状态")
                            .selected_text(status_label(&self.status_filter))
                            .show_ui(ui, |ui| {
                                for s in ["all", "todo", "doing", "failed", "done", "parked"] {
                                    if ui
                                        .selectable_label(
                                            self.status_filter.as_str() == s,
                                            status_label(s),
                                        )
                                        .clicked()
                                    {
                                        self.status_filter = s.to_string();
                                    }
                                }
                            });
                        if ui
                            .add(egui::Button::new("清除").sense(egui::Sense::CLICK))
                            .clicked()
                        {
                            self.search.clear();
                            self.status_filter = "all".to_string();
                        }
                    });
                } else {
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        let btn = ui.add(egui::Button::new("...").sense(egui::Sense::CLICK));
                        let popup_id = egui::Id::new("search_popup");
                        if btn.clicked() {
                            ui.ctx().memory_mut(|m| m.toggle_popup(popup_id));
                        }
                        // popup 仅在通过 toggle_popup 打开时渲染；CloseOnClickOutside
                        // 会在点击弹层外部时自动关闭，输入框内点击不受影响
                        let _ = egui::popup_below_widget(
                            ui,
                            popup_id,
                            &btn,
                            egui::popup::PopupCloseBehavior::CloseOnClickOutside,
                            |ui| {
                                ui.horizontal(|ui| {
                                    ui.label("搜索:");
                                    ui.add(
                                        egui::TextEdit::singleline(&mut self.search)
                                            .hint_text("任务标题…")
                                            .desired_width(160.0)
                                            .id(egui::Id::new("top_search_box")),
                                    );
                                });
                                ui.label("状态:");
                                ui.horizontal_wrapped(|ui| {
                                    for s in [
                                        "all",
                                        "todo",
                                        "doing",
                                        "failed",
                                        "done",
                                        "parked",
                                    ] {
                                        if ui
                                            .selectable_label(
                                                self.status_filter.as_str() == s,
                                                status_label(s),
                                            )
                                            .clicked()
                                        {
                                            self.status_filter = s.to_string();
                                            // 选中后关闭弹层（避免嵌套下拉被外层 CloseOnClickOutside 误关）
                                            ui.ctx().memory_mut(|m| m.close_popup());
                                        }
                                    }
                                });
                                if ui
                            .add(egui::Button::new("清除").sense(egui::Sense::CLICK))
                            .clicked()
                        {
                                    self.search.clear();
                                    self.status_filter = "all".to_string();
                                }
                            },
                        );
                    });
                }
            });
        });

        if self.left_open {
            egui::SidePanel::left("left")
                .default_width(200.0)
                .show(ctx, |ui| {
                    ui.horizontal(|ui| {
                        ui.heading("项目");
                        if ui
                            .button("+")
                            .on_hover_text("新建项目")
                            .clicked()
                        {
                            let pid = format!(
                                "proj_{}",
                                std::time::SystemTime::now()
                                    .duration_since(std::time::UNIX_EPOCH)
                                    .unwrap_or_default()
                                    .as_nanos()
                            );
                            self.store.projects.push(Project {
                                id: pid.clone(),
                                name: "新项目".to_string(),
                                root: TaskNode {
                                    id: "root".to_string(),
                                    title: "新项目".to_string(),
                                    status: "todo".to_string(),
                                    summary: String::new(),
                                    messages: vec![],
            task_info: String::new(),
                                    created: None,
                                    updated: None,
                                    children: vec![],
                                    intermediates: vec![],
                                },
                                cursor: String::new(),
                                intermediates: vec![],
                            });
                            let new_idx = self.store.projects.len() - 1;
                            self.project_idx = new_idx;
                            self.selected = "root".to_string();
                            self.save_ui_state();
                            // 新建项目后自动进入「行内重命名」，并滚动到可见位置
                            self.renaming_project = Some(new_idx);
                            self.scroll_to_project = Some(new_idx);
                            self.renaming_needs_focus = true;
                            let _ = data::save(&self.store);
                        }
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            if ui.button("◀ 收起").clicked() {
                                self.left_open = false;
                                self.save_ui_state();
                            }
                        });
                    });
                    ui.separator();
                    // 滚动条始终预留空间，避免从无到有时挤占宽度导致内容重排、滚动位置跳动
                    let mut pending_del_project: Option<usize> = None;
                    let mut need_save = false;
                    let mut need_save_ui = false;
                    let mut pending_move_project: Option<(usize, usize)> = None;
                    egui::ScrollArea::vertical()
                        .scroll_bar_visibility(egui::scroll_area::ScrollBarVisibility::AlwaysVisible)
                        .show(ui, |ui| {
                        ui.add_space(2.0); // 首行顶部留隙，避免文字被裁剪边界切掉
                        let q = self.search.trim().to_string();
                        for (i, p) in self.store.projects.iter_mut().enumerate() {
                            // 重命名中的项目始终显示（避免被搜索过滤隐藏）
                            let renaming_this = self.renaming_project == Some(i);
                            // 左栏：项目名含关键词，或项目内存在标题匹配的任务，则保留
                            if !q.is_empty() && !renaming_this {
                                let name_ok = p.name.contains(&q);
                                let task_ok = {
                                    let mut s = std::collections::HashSet::new();
                                    mark_visible(&p.root, &q, "all", &mut s)
                                };
                                if !name_ok && !task_ok {
                                    continue;
                                }
                            }
                            let is_cur = self.project_idx == i;
                            if renaming_this {
                                // 行内重命名：新建项目后自动进入，回车或点击别处提交
                                let edit_id = egui::Id::new(format!("proj_rename_{i}"));
                                let mut buf = p.name.clone();
                                let r = ui.add(
                                    egui::TextEdit::singleline(&mut buf)
                                        .id(edit_id)
                                        .desired_width(ui.available_width())
                                        .horizontal_align(egui::Align::LEFT)
                                        .lock_focus(true)
                                        .hint_text("项目名"),
                                );
                                if r.changed() {
                                    p.name = buf;
                                }
                                if self.renaming_needs_focus {
                                    // 进入重命名时清掉粘滞的 IME 合成态
                                    self.ime_composing = false;
                                    self.last_ime_time = -10.0;
                                    ui.ctx().memory_mut(|m| m.request_focus(edit_id));
                                    self.renaming_needs_focus = false;
                                }
                                let enter = ui.input(|inp| inp.key_pressed(egui::Key::Enter));
                                let tab = ui.input(|inp| inp.key_pressed(egui::Key::Tab));
                                let esc = ui.input(|inp| inp.key_pressed(egui::Key::Escape));
                                let lost = r.lost_focus();
                                let click_away = lost && !enter && !tab && !esc;
                                let key_commit = !ime_active && (enter || esc);
                                if click_away || key_commit {
                                    if p.name.trim().is_empty() {
                                        p.name = "新项目".to_string();
                                    }
                                    self.renaming_project = None;
                                    need_save = true;
                                } else if (enter || esc) && ime_active {
                                    // 输入法确认候选的回车/Esc：不提交。清掉粘滞合成态，
                                    // 使下一次按键成为真正提交；同时抢回焦点继续编辑
                                    self.ime_composing = false;
                                    self.last_ime_time = -10.0;
                                    self.renaming_needs_focus = true;
                                } else if lost && ime_active {
                                    // 仅因 IME 导致失焦（无显式按键）：抢回焦点继续编辑
                                    self.renaming_needs_focus = true;
                                }
                                // 新建项目当帧滚动到可见位置（仅一次）
                                if self.scroll_to_project == Some(i) {
                                    ui.scroll_to_rect(r.rect, Some(egui::Align::Center));
                                    self.scroll_to_project = None;
                                }
                                r.context_menu(|ui| {
                                    if ui.button("🗑 删除项目").clicked() {
                                        pending_del_project = Some(i);
                                        ui.close_menu();
                                    }
                                });
                            } else {
                                let row_w = ui.available_width();
                                let row_h = ui.spacing().interact_size.y;
                                let (row_rect, resp) = ui.allocate_exact_size(
                                    egui::vec2(row_w, row_h),
                                    egui::Sense::click_and_drag(),
                                );
                                let row_center = row_rect.center().y;
                                // 选中 / 悬停背景
                                if is_cur {
                                    ui.painter().rect_filled(
                                        row_rect,
                                        egui::CornerRadius::same(3),
                                        egui::Color32::from_rgb(86, 96, 128),
                                    );
                                } else if resp.hovered() {
                                    ui.painter().rect_filled(
                                        row_rect,
                                        egui::CornerRadius::same(3),
                                        ui.style()
                                            .visuals
                                            .widgets
                                            .noninteractive
                                            .bg_fill
                                            .linear_multiply(0.6),
                                    );
                                }
                                // 拖拽中：在悬停的目标项目行上画插入指示线，并记录落点
                                if let Some(src) = self.drag_project {
                                    if src != i {
                                        if let Some(pos) =
                                            ui.ctx().pointer_hover_pos()
                                        {
                                            if row_rect.contains(pos) {
                                                let after = pos.y > row_center;
                                                self.drop_project = Some((i, after));
                                                let y = if after {
                                                    row_rect.max.y
                                                } else {
                                                    row_rect.min.y
                                                };
                                                ui.painter().line_segment(
                                                    [
                                                        egui::pos2(row_rect.min.x, y),
                                                        egui::pos2(row_rect.max.x, y),
                                                    ],
                                                    (2.0, egui::Color32::from_rgb(26, 114, 214)),
                                                );
                                            }
                                        }
                                    }
                                }
                                // 文字用 painter 直接画（不是 widget，不会抢交互），锚点取行中心避免裁字
                                let txt_color = if is_cur {
                                    egui::Color32::WHITE
                                } else {
                                    ui.style().visuals.text_color()
                                };
                                ui.painter().text(
                                    egui::pos2(row_rect.min.x + 6.0, row_rect.center().y),
                                    egui::Align2::LEFT_CENTER,
                                    p.name.as_str(),
                                    egui::FontId::proportional(
                                        ui.style()
                                            .text_styles
                                            .get(&egui::TextStyle::Button)
                                            .map(|f| f.size)
                                            .unwrap_or(14.0),
                                    ),
                                    txt_color,
                                );
                                let resp = resp.on_hover_cursor(egui::CursorIcon::PointingHand);
                                // 拖动项目即可重排：拖动瞬间自动选中并进入拖拽
                                if resp.drag_started() {
                                    self.drag_project = Some(i);
                                    self.project_idx = i;
                                    self.selected = p.root.id.clone();
                                    need_save_ui = true;
                                }
                                if resp.clicked() {
                                    self.project_idx = i;
                                    self.selected = p.root.id.clone();
                                    need_save_ui = true;
                                }
                                // 滚动到可见位置（仅一次，用于新建项目定位）
                                if self.scroll_to_project == Some(i) {
                                    ui.scroll_to_rect(row_rect, Some(egui::Align::Center));
                                    self.scroll_to_project = None;
                                }
                                resp.context_menu(|ui| {
                                    if ui.button("🗑 删除项目").clicked() {
                                        pending_del_project = Some(i);
                                        ui.close_menu();
                                    }
                                });
                            }
                        }
                    // 项目拖拽排序：松开鼠标时记录移动（from -> insert 位置）
                    if self.drag_project.is_some() {
                        let released = ui.ctx().input(|i| i.pointer.any_released());
                        if released {
                            if let Some((t, after)) = self.drop_project {
                                let from = self.drag_project.unwrap();
                                if from != t {
                                    let mut insert = if after { t + 1 } else { t };
                                    if from < insert {
                                        insert -= 1;
                                    }
                                    pending_move_project = Some((from, insert));
                                }
                            }
                            self.drag_project = None;
                            self.drop_project = None;
                        }
                    }
                    });
                    // 重命名提交后落盘（放到迭代闭包外，避免可变借用冲突）
                    if need_save {
                        let _ = data::save(&self.store);
                    }
                    // 切换项目等界面状态变化，落盘以便下次启动恢复
                    if need_save_ui {
                        self.save_ui_state();
                    }
                    // 删除项目（在 ScrollArea 闭包外执行，避免迭代期间修改容器）
                    if let Some(di) = pending_del_project {
                        self.store.projects.remove(di);
                        if self.project_idx >= self.store.projects.len() {
                            self.project_idx = self.store.projects.len().saturating_sub(1);
                        }
                        self.selected = self.store.projects
                            .get(self.project_idx)
                            .map(|p| p.root.id.clone())
                            .unwrap_or_default();
                        self.renaming_project = None;
                        let _ = data::save(&self.store);
                        self.save_ui_state();
                    }
                    // 项目拖拽重排（在 ScrollArea 闭包外执行，避免迭代期间修改容器）
                    if let Some((from, insert)) = pending_move_project {
                        if from < self.store.projects.len()
                            && insert <= self.store.projects.len()
                        {
                            let proj = self.store.projects.remove(from);
                            self.store.projects.insert(insert, proj);
                            self.project_idx = insert;
                            self.selected =
                                self.store.projects[insert].root.id.clone();
                            let _ = data::save(&self.store);
                            self.save_ui_state();
                        }
                    }
                });
        } else {
            egui::SidePanel::left("left_open_btn")
                .default_width(36.0)
                .resizable(false)
                .show(ctx, |ui| {
                    ui.with_layout(egui::Layout::top_down(egui::Align::Center), |ui| {
                        ui.add_space(6.0);
                        if ui.button("▶").clicked() {
                            self.left_open = true;
                            self.save_ui_state();
                        }
                        ui.separator();
                        let mut pending_del_project: Option<usize> = None;
                        let mut need_save_ui = false;
                        // 折叠态：每个项目一个彩色小色块，上面写首字，点一下即切换
                        for (i, p) in self.store.projects.iter().enumerate() {
                            let size = 24.0;
                            let (rect, r) = ui
                                .allocate_exact_size(egui::vec2(size, size), egui::Sense::CLICK);
                            let resp = r.on_hover_cursor(egui::CursorIcon::PointingHand);
                            ui.painter()
                                .rect_filled(rect, egui::CornerRadius::same(5), project_color(i));
                            // 当前项目：白色描边高亮
                            if self.project_idx == i {
                                ui.painter().rect_stroke(
                                    rect,
                                    egui::CornerRadius::same(5),
                                    egui::Stroke::new(2.0, egui::Color32::WHITE),
                                    egui::StrokeKind::Inside,
                                );
                            }
                            let ch = p.name.chars().next().unwrap_or('?').to_string();
                            ui.painter().text(
                                rect.center(),
                                egui::Align2::CENTER_CENTER,
                                ch,
                                egui::FontId::proportional(14.0),
                                egui::Color32::WHITE,
                            );
                            if resp.clicked() {
                                self.project_idx = i;
                                self.selected = p.root.id.clone();
                                need_save_ui = true;
                            }
                            resp.context_menu(|ui| {
                                if ui.button("🗑 删除项目").clicked() {
                                    pending_del_project = Some(i);
                                    ui.close_menu();
                                }
                            });
                        }
                        // 切换项目的界面状态变化，循环外落盘
                        if need_save_ui {
                            self.save_ui_state();
                        }
                        // 折叠态同样支持删除项目
                        if let Some(di) = pending_del_project {
                            self.store.projects.remove(di);
                            if self.project_idx >= self.store.projects.len() {
                                self.project_idx = self.store.projects.len().saturating_sub(1);
                            }
                            self.selected = self.store.projects
                                .get(self.project_idx)
                                .map(|p| p.root.id.clone())
                                .unwrap_or_default();
                            self.renaming_project = None;
                            let _ = data::save(&self.store);
                            self.save_ui_state();
                        }
                    });
                });
        }

        if self.right_open {
            // 确定性方案：关闭原生 resizable 手柄（在你的环境/DPI 下松手会丢失 PanelState 弹回），
            // 改用 exact_width 把宽度范围钉成单点，强制绕过 egui 内部 PanelState，100% 由
            // self.right_width 驱动；面板左缘自管拖拽手柄更新 self.right_width。
            egui::SidePanel::right("right")
                .resizable(false)
                .exact_width(self.right_width.clamp(240.0, 680.0))
                .show(ctx, |ui| {
                    // 左缘拖拽手柄：在面板自身 ui 坐标系内交互（避免早期 Foreground Area 的坐标错位坑）。
                    let r = ui.max_rect();
                    let grab = ctx.style().interaction.resize_grab_radius_side.max(4.0);
                    let handle_rect = egui::Rect::from_x_y_ranges(
                        r.min.x..=(r.min.x + grab),
                        r.y_range(),
                    );
                    let handle = ui.interact(
                        handle_rect,
                        egui::Id::new("right_resize_handle"),
                        egui::Sense::drag(),
                    );
                    if handle.dragged() {
                        // 右栏：左缘右移(pointer delta x>0) => 宽度减小
                        let dx = ctx.input(|i| i.pointer.delta().x);
                        self.right_width = (self.right_width - dx).clamp(240.0, 680.0);
                    }
                    if handle.drag_stopped() {
                        self.save_ui_state();
                    }
                    if handle.hovered() || handle.dragged() {
                        ctx.set_cursor_icon(egui::CursorIcon::ResizeHorizontal);
                    }
                    ui.horizontal(|ui| {
                        if ui.button("▶ 收起").clicked() {
                            self.right_open = false;
                            self.save_ui_state();
                        }
                        ui.heading("详情");
                        ui.with_layout(
                            egui::Layout::right_to_left(egui::Align::BOTTOM),
                            |ui| {
                                if ui
                                    .button("➕ 子任务")
                                    .on_hover_text("为当前任务添加子任务")
                                    .clicked()
                                {
                                    self.add_child_to_selected();
                                }
                            },
                        );
                    });
                    ui.separator();
                    egui::ScrollArea::vertical().show(ui, |ui| {
                        self.render_inspector(ui);
                        // ── IME 回车诊断面板（测试完可删除）──
                        if self.debug_ime_on {
                            ui.separator();
                            ui.collapsing("🔧 IME 回车诊断", |ui| {
                                ui.label(egui::RichText::new("当前状态:").strong());
                                let now_t = ctx.input(|i| i.time);
                                let dt = now_t - self.last_ime_time;
                                ui.label(format!(
                                    "ime_composing={}  ime_active={}  dt={:.3}s  focused={:?}",
                                    self.ime_composing, self.ime_active, dt,
                                    ctx.memory(|m| m.focused())
                                ));
                                ui.horizontal(|ui| {
                                    if ui.button("关闭诊断").clicked() {
                                        self.debug_ime_on = false;
                                    }
                                    if ui.button("清空记录").clicked() {
                                        self.debug_ime_events.clear();
                                    }
                                });
                                ui.add_space(4.0);
                                for entry in self.debug_ime_events.iter().rev() {
                                    let color = if entry.contains("✅") {
                                        egui::Color32::GREEN
                                    } else if entry.contains("❌") {
                                        egui::Color32::RED
                                    } else {
                                        egui::Color32::GRAY
                                    };
                                    ui.label(
                                        egui::RichText::new(entry).color(color).size(11.0),
                                    );
                                }
                                if self.debug_ime_events.is_empty() {
                                    ui.label(
                                        egui::RichText::new(
                                            "(无记录，请去任务信息框打字后按回车)",
                                        )
                                        .weak(),
                                    );
                                }
                            });
                        }
                    });
                });
        } else {
            // 完全收起：不占任何空间，仅在右侧悬浮一个三角形按钮（无纵列空白）。
            // y 偏移下移到顶栏下方，避免与顶栏最右侧筛选「...」按钮位置重叠。
            egui::Area::new(egui::Id::new("right_open_btn"))
                .anchor(egui::Align2::RIGHT_TOP, egui::vec2(-8.0, 44.0))
                .show(ctx, |ui| {
                    if ui
                        .button("◀")
                        .on_hover_text("展开详情")
                        .clicked()
                    {
                        self.right_open = true;
                        self.save_ui_state();
                    }
                });
        }

        // 全局快捷键：选中任务时，Enter = 新建子任务，Tab = 新建同级任务（均自动进入行内重命名）
        // 关键修复（2026-07-29）：只要「任意文本输入框正聚焦」就禁用这两个快捷键，
        // 避免里程碑(标题/截止日/备注) / 任务信息 / 中间结果(标题/正文/链接) / 任务名内联编辑 等输入态下
        // 按回车或 Tab 误建任务。现改为「聚焦即禁用」的完备判定。
        // 顶栏按钮已统一设为不可聚焦（Sense::CLICK），Tab 不会再在顶栏产生待选框。
        let has_f = |id: egui::Id| ctx.memory(|m| m.has_focus(id));
        let editing_text = has_f(egui::Id::new("top_search_box"))
            || has_f(egui::Id::new("task_info"))
            || has_f(egui::Id::new("task_title_edit"))
            || self
                .store
                .projects
                .get(self.project_idx)
                .map_or(false, |p| {
                    p.intermediates.iter().any(|e| {
                        has_f(egui::Id::new(("im_text", &e.id)))
                            || has_f(egui::Id::new(("im_title", &e.id)))
                            || has_f(egui::Id::new(("im_link", &e.id)))
                    }) || p
                        .root
                        .find(&self.selected)
                        .map_or(false, |node| {
                            node.intermediates.iter().any(|e| {
                                has_f(egui::Id::new(("im_text", &e.id)))
                                    || has_f(egui::Id::new(("im_title", &e.id)))
                                    || has_f(egui::Id::new(("im_link", &e.id)))
                            })
                        })
                })
            || self.renaming.is_some()
            || self.renaming_project.is_some();
        if !editing_text && self.store.projects.get(self.project_idx).is_some() {
            if ctx.input(|i| i.key_pressed(egui::Key::Enter)) {
                self.add_child_to_selected();
            } else if ctx.input(|i| i.key_pressed(egui::Key::Tab)) {
                self.add_sibling_to_selected();
            }
        }

        // 中间结果删除确认弹窗：默认保留，仅点「删除」才移除条目（文件默认保留不删）
        if let Some(del_id) = self.confirm_delete_im.clone() {
            let mut do_delete = false;
            egui::Modal::new(egui::Id::new("confirm_delete_im")).show(ctx, |ui| {
                ui.heading("删除中间结果？");
                ui.label("该条目将从任务中移除；其归入工作目录的文件（如有）默认保留，不会被删除。");
                ui.horizontal(|ui| {
                    if ui.button("删除").clicked() {
                        do_delete = true;
                    }
                    if ui.button("保留").clicked() {
                        // 不删，关闭弹窗（外层统一清除标志）
                    }
                });
            });
            if do_delete {
                let sel = self.selected.clone();
                if let Some(proj) = self.current_project_mut() {
                    if let Some(node) = find_node_mut(&mut proj.root, &sel) {
                        if let Some(pos) =
                            node.intermediates.iter().position(|e| e.id == del_id)
                        {
                            node.intermediates.remove(pos);
                            let _ = data::save(&self.store);
                        }
                    }
                }
            }
            self.confirm_delete_im = None;
        }

        egui::CentralPanel::default().show(ctx, |ui| {
            if self.store.projects.is_empty() {
                ui.centered_and_justified(|ui| {
                    ui.vertical_centered(|ui| {
                        ui.heading("暂无项目");
                        ui.label("点击左侧「+」按钮新建一个项目");
                    });
                });
                return;
            }
            match self.view {
                ViewMode::Outline => {
                    egui::ScrollArea::vertical().show(ui, |ui| {
                            ui.separator();
                        // 计算被拖动节点子树的所有 id，作为非法落点（不能拖进自己或后代）
                        let invalid = if let Some(d) = &self.drag_node {
                            let mut s = std::collections::HashSet::new();
                            if let Some(dn) =
                                find_node_ref(&self.store.projects[self.project_idx].root, d)
                            {
                                collect_subtree_ids(dn, &mut s);
                            }
                            s
                        } else {
                            std::collections::HashSet::new()
                        };
                        // 计算过滤后需显示的节点集合（标题 + 状态）
                        let filtering =
                            !self.search.trim().is_empty() || self.status_filter.as_str() != "all";
                        let mut visible_set: std::collections::HashSet<String> =
                            std::collections::HashSet::new();
                        if filtering {
                            mark_visible(
                                &self.store.projects[self.project_idx].root,
                                self.search.trim(),
                                &self.status_filter,
                                &mut visible_set,
                            );
                        }
                        let visible: Option<&std::collections::HashSet<String>> =
                            if filtering { Some(&visible_set) } else { None };
                        // 取出需要跨递归传递的可变状态，避免对同一 struct 多次可变借用冲突
                        let mut selected = std::mem::take(&mut self.selected);
                        let mut collapsed = std::mem::take(&mut self.collapsed);
                        // 快照本帧渲染前的折叠集合，用于判断折叠是否真的变化（避免每帧落盘）
                        let prev_collapsed = collapsed.clone();
                        let mut renaming = std::mem::take(&mut self.renaming);
                        let mut renaming_needs_focus =
                            std::mem::take(&mut self.renaming_needs_focus);
                        let mut drag_node = std::mem::take(&mut self.drag_node);
                        let mut drop_target = std::mem::take(&mut self.drop_target);
                        let mut pending_add: Option<String> = None;
                        let mut pending_del: Option<String> = None;
                        let mut pending_expand_all: Option<String> = None;
                        let mut pending_collapse_all: Option<String> = None;
                        // Tab 在重命名态提交时带回的「当前节点 id」：调用方据此创建同级任务并继续重命名
                        let mut pending_sibling: Option<String> = None;
                        let (changed, _cy) = Self::render_tree(
                            ui,
                            &mut self.store.projects[self.project_idx].root,
                            0,
                            &mut selected,
                            &mut collapsed,
                            self.color_by_level,
                            &mut pending_add,
                            &mut pending_del,
                            &mut pending_expand_all,
                            &mut pending_collapse_all,
                            &mut drag_node,
                            &mut drop_target,
                            &mut renaming,
                            &mut renaming_needs_focus,
                            ime_active,
                            &invalid,
                            visible,
                            &mut self.promote_pending,
                            &mut self.ime_composing,
                            &mut self.last_ime_time,
                            &mut pending_sibling,
                            &self.just_created_rename,
                        );
                        // 还原会话状态
                        self.selected = selected;
                        self.collapsed = collapsed;
                        self.renaming = renaming;
                        self.renaming_needs_focus = renaming_needs_focus;
                        // Tab 在重命名态提交：创建「同级任务」并立即进入其重命名。
                        // add_sibling_to_selected 会把 self.selected 指到新节点、设
                        // renaming=Some(新id) 且 renaming_needs_focus=true —— 下一帧新条目
                        // 自动获焦，重命名态不中断。
                        if let Some(sib_id) = pending_sibling.take() {
                            self.selected = sib_id;
                            self.add_sibling_to_selected();
                        }
                        // 本帧的「刚创建」标记仅用于本次渲染屏蔽重复 Tab，渲染结束后清空，
                        // 这样下一帧再按 Tab 仍可继续顺延新建同级任务。
                        // Tab 的焦点遍历已由重命名框的 lock_focus(true) 在源头吸收
                        // （egui 的 EventFilter 让 begin_pass 不再把 Tab 当成 Next 方向），
                        // 不再需要占位 id 抢焦点，故删除 __rename_tab_guard。
                        self.just_created_rename = None;
                        // 右键「展开/收缩全部子任务」：在 update 里统一对 self.collapsed 操作，
                        // 避免闭包内直接改 &mut HashSet 跨帧失效（与 pending_add/pending_del 同一套可靠模式）
                        if let Some(eid) = pending_expand_all.take() {
                            self.collapsed.remove(&eid);
                            if let Some(n) =
                                find_node_ref(&self.store.projects[self.project_idx].root, &eid)
                            {
                                for d in Self::collect_descendants(n) {
                                    self.collapsed.remove(&d);
                                }
                            }
                        }
                        if let Some(cid) = pending_collapse_all.take() {
                            self.collapsed.insert(cid.clone());
                            if let Some(n) =
                                find_node_ref(&self.store.projects[self.project_idx].root, &cid)
                            {
                                for d in Self::collect_descendants(n) {
                                    self.collapsed.insert(d.clone());
                                }
                            }
                        }
                        // 折叠状态真正变化时才落盘；否则每帧写文件会触发文件监听→重绘死循环（CPU 100% 根因）
                        if self.collapsed != prev_collapsed {
                            self.save_ui_state();
                        }
                        self.drag_node = drag_node;
                        self.drop_target = drop_target;

                        let mut need_save = changed;
                        // 右键「添加子任务」：新建红色(todo)子任务并自动进入行内重命名
                        if let Some(pid) = pending_add {
                            if let Some(new_id) =
                                add_child(&mut self.store.projects[self.project_idx].root, &pid)
                            {
                                self.selected = new_id.clone();
                                self.renaming = Some(new_id);
                                self.renaming_needs_focus = true;
                                // 父任务若是折叠态，自动展开该级，让新建子任务可见
                                self.collapsed.remove(&pid);
                                self.save_ui_state();
                                need_save = true;
                            }
                        }
                        // 右键「删除该任务」
                        if let Some(did) = pending_del {
                            if delete_node(
                                &mut self.store.projects[self.project_idx].root,
                                &did,
                            ) {
                                if self.selected == did {
                                    self.selected =
                                        self.store.projects[self.project_idx].root.id.clone();
                                }
                                need_save = true;
                            }
                        }
                        // 拖拽落点处理：松开鼠标时把被拖动节点移动到目标位置
                        if let Some(drag_id) = self.drag_node.clone() {
                            let released = ctx.input(|i| i.pointer.any_released());
                            if released {
                                if let Some((target_id, pos)) = self.drop_target.clone() {
                                    // 安全校验：不能移动到自身或其子树中
                                    let invalid_move = target_id == drag_id
                                        || if let Some(dn) = find_node_ref(
                                            &self.store.projects[self.project_idx].root,
                                            &drag_id,
                                        ) {
                                            contains(dn, &target_id)
                                        } else {
                                            true
                                        };
                                    if !invalid_move {
                                        let mut moved = false;
                                        {
                                            let root =
                                                &mut self.store.projects[self.project_idx].root;
                                            if let Some(node) = detach(root, &drag_id) {
                                                match pos {
                                                    DropPos::Child => {
                                                        // 变为目标任务的子任务：追加到其 children 末尾
                                                        if let Some(t) =
                                                            find_node_mut(root, &target_id)
                                                        {
                                                            t.children.push(node);
                                                            t.updated = Some(now_secs());
                                                            moved = true;
                                                        } else {
                                                            insert_at(root, "root", 0, node);
                                                            moved = true;
                                                        }
                                                    }
                                                    DropPos::Before | DropPos::After => {
                                                        let after =
                                                            matches!(pos, DropPos::After);
                                                        if let Some(pid) =
                                                            parent_id(root, &target_id)
                                                        {
                                                            let idx = {
                                                                let p = find_node_mut(root, &pid)
                                                                    .unwrap();
                                                                let ppos = p
                                                                    .children
                                                                    .iter()
                                                                    .position(|c| {
                                                                        c.id == target_id
                                                                    })
                                                                    .unwrap_or(0);
                                                                if after { ppos + 1 } else { ppos }
                                                            };
                                                            insert_at(root, &pid, idx, node);
                                                            moved = true;
                                                        } else {
                                                            insert_at(root, "root", 0, node);
                                                            moved = true;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        if moved {
                                            self.selected = drag_id.clone();
                                            need_save = true;
                                        }
                                    }
                                }
                                self.drag_node = None;
                                self.drop_target = None;
                            }
                        }
                        // 子→父级联：子任务全部完成时，把父任务（向上递归）自动标为完成
                        if self.promote_pending {
                            promote_done(&mut self.store.projects[self.project_idx].root);
                            self.promote_pending = false;
                            need_save = true;
                        }
                        if need_save {
                            let _ = data::save(&self.store);
                        }
                    });
                }
                ViewMode::Mindmap => {
                    ui.label("操作：拖拽平移 · 滚轮缩放 · 点节点选中 · 点 ± 折叠");
                    ui.separator();
                    self.render_mindmap(ui);
                }
            }
        });
    }
}

/// 后台文件监听：projects.json 被外部(MCP/agent/编辑器)修改时，
/// 主动调用 ctx.request_repaint() 唤醒主线程的 update()，触发热重载。
/// egui 默认 idle 时 update 不跑，必须靠外部主动唤醒才能实时刷新。
fn spawn_file_watcher(ctx: egui::Context) {
    std::thread::spawn(move || {
        use notify::{Watcher, RecursiveMode};
        let (tx, rx) = std::sync::mpsc::channel::<()>();
        let mut watcher = match notify::recommended_watcher(
            move |res: notify::Result<notify::Event>| {
                if res.is_ok() {
                    let _ = tx.send(());
                }
            },
        ) {
            Ok(w) => w,
            Err(e) => {
                eprintln!("文件监听启动失败: {e}");
                return;
            }
        };
        let path = data::data_path();
        if watcher.watch(&path, RecursiveMode::NonRecursive).is_err() {
            if let Some(parent) = path.parent() {
                let _ = watcher.watch(parent, RecursiveMode::NonRecursive);
            }
        }
        // 同时监听 ui_state.json（折叠状态），外部(MCP)修改后主动唤醒主线程重载
        let ui_path = data::ui_state_path();
        if watcher.watch(&ui_path, RecursiveMode::NonRecursive).is_err() {
            if let Some(parent) = ui_path.parent() {
                let _ = watcher.watch(parent, RecursiveMode::NonRecursive);
            }
        }
        // 阻塞接收文件变化事件，逐个主动唤醒主线程
        // （watcher 在此作用域内保持存活，线程不退出）
        for _ in rx {
            ctx.request_repaint();
        }
    });
}

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        // 用 glow(OpenGL) 后端替代默认的 wgpu(DX12/Vulkan)：
        // wgpu 会预留数 GB 虚拟地址空间，glow 仅几百 MB，且更省内存。
        renderer: eframe::Renderer::Glow,
        // 窗口左上角与任务栏图标（与桌面 .lnk 图标一致，透明）
        viewport: egui::ViewportBuilder::default().with_icon(std::sync::Arc::new(egui::IconData {
            rgba: ICON_RGBA.to_vec(),
            width: ICON_W,
            height: ICON_H,
        })),
        ..Default::default()
    };
    eframe::run_native(
        "Branch Task",
        options,
        Box::new(|cc| {
            // 启动后台文件监听：projects.json 被外部(MCP/agent/编辑器)修改时，
            // 主动 request_repaint() 唤醒主线程 update()，实现真正的实时热重载
            // （egui 默认 idle 时 update 不跑，必须靠外部主动唤醒）。
            let ctx = cc.egui_ctx.clone();
            spawn_file_watcher(ctx);

            // 仅保留微软雅黑一个中文字体（simhei 作兜底已移除，减小字体图集体积）
            let font_paths = [
                r"C:\Windows\Fonts\msyh.ttc",
            ];
            for p in &font_paths {
                if let Ok(font_data) = std::fs::read(p) {
                    let mut fonts = egui::FontDefinitions::default();
                    fonts.font_data.insert(
                        "cjk".to_owned(),
                        egui::FontData::from_owned(font_data).into(),
                    );
                    fonts
                        .families
                        .entry(egui::FontFamily::Proportional)
                        .or_default()
                        .insert(0, "cjk".to_owned());
                    fonts
                        .families
                        .entry(egui::FontFamily::Monospace)
                        .or_default()
                        .insert(0, "cjk".to_owned());
                    cc.egui_ctx.set_fonts(fonts);
                    break;
                }
            }
            Ok(Box::new(App::default()))
        }),
    )
}
