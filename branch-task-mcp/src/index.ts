import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

const STORE_DIR = path.join(os.homedir(), ".branch-task");
const STORE_FILE = path.join(STORE_DIR, "projects.json");
const LOCK_FILE = path.join(STORE_DIR, ".lock");

// ---------- types ----------
type Status = "todo" | "doing" | "done" | "parked";
interface Msg {
  id: string;
  role: "user" | "assistant";
  content: string;
  ts: number;
}
interface TaskNode {
  id: string;
  title: string;
  status: Status;
  summary: string;
  messages: Msg[];
  children: TaskNode[];
}
interface Project {
  id: string;
  name: string;
  root: TaskNode;
  cursor: string; // current node id; "root" = main line
}
interface Store {
  projects: Project[];
  currentId: string | null;
}

// ---------- storage ----------
// WorkBuddy may invoke multiple tools concurrently, each spawning a fresh server
// process that loads state from the file. We serialize ALL mutations with a
// cross-process file lock so concurrent calls can't clobber each other.
function load(): Store {
  try {
    const raw = fs.readFileSync(STORE_FILE, "utf-8");
    const s = JSON.parse(raw) as Store;
    if (!Array.isArray(s.projects)) s.projects = [];
    if (s.currentId == null && s.projects.length) s.currentId = s.projects[0].id;
    return s;
  } catch {
    return { projects: [], currentId: null };
  }
}
function save(s: Store) {
  fs.mkdirSync(STORE_DIR, { recursive: true });
  fs.writeFileSync(STORE_FILE, JSON.stringify(s, null, 2), "utf-8");
  notifyFrontend();
}
// 写入完成后主动通知前端刷新一次（事件驱动，不轮询）。失败静默忽略，
// 同步服务未启动时前端仍可走「↧ 从MCP同步」手动拉取。
function notifyFrontend() {
  const url = process.env.SYNC_NOTIFY_URL || "http://127.0.0.1:8080/api/notify";
  try {
    fetch(url, { method: "POST" }).catch(() => {});
  } catch {
    /* ignore */
  }
}
function sleep(ms: number) {
  return new Promise((r) => setTimeout(r, ms));
}
async function withLock<T>(fn: () => T): Promise<T> {
  const MAX = 600; // ~6s
  for (let i = 0; i < MAX; i++) {
    try {
      fs.openSync(LOCK_FILE, "wx"); // atomic create; fails if exists
      break;
    } catch {
      await sleep(10);
    }
  }
  try {
    return fn();
  } finally {
    try {
      fs.unlinkSync(LOCK_FILE);
    } catch {
      /* ignore */
    }
  }
}
function genId(): string {
  return "n_" + Math.random().toString(36).slice(2, 9);
}
function getProject(s: Store, id?: string): Project | null {
  const pid = id ?? s.currentId;
  return s.projects.find((p) => p.id === pid) ?? s.projects[0] ?? null;
}
function findNode(node: TaskNode, id: string): TaskNode | null {
  if (node.id === id) return node;
  for (const c of node.children) {
    const r = findNode(c, id);
    if (r) return r;
  }
  return null;
}
function findParent(node: TaskNode, id: string, parent: TaskNode | null = null): TaskNode | null {
  if (node.id === id) return parent;
  for (const c of node.children) {
    const r = findParent(c, id, node);
    if (r !== null) return r;
  }
  return null;
}

// ---------- UI state (collapse) ----------
// 折叠状态存在独立的 ui_state.json（与业务数据分离），MCP 通过读写它来控制展开/收起，
// egui 端监听该文件变化并实时重载 self.collapsed，从而实现「外部自动打开想看的任务」。
interface UiState {
  left_open?: boolean;
  right_open?: boolean;
  collapsed?: string[];
  project_idx?: number;
}
function loadUiState(): UiState {
  try {
    const raw = fs.readFileSync(path.join(STORE_DIR, "ui_state.json"), "utf-8");
    const s = JSON.parse(raw) as UiState;
    if (!Array.isArray(s.collapsed)) s.collapsed = [];
    return s;
  } catch {
    return { collapsed: [] };
  }
}
function saveUiState(s: UiState) {
  fs.mkdirSync(STORE_DIR, { recursive: true });
  fs.writeFileSync(path.join(STORE_DIR, "ui_state.json"), JSON.stringify(s, null, 2), "utf-8");
  notifyFrontend();
}
// 递归收集一棵子树所有节点 id
function collectNodeIds(node: TaskNode, acc: string[] = []): string[] {
  acc.push(node.id);
  for (const c of node.children) collectNodeIds(c, acc);
  return acc;
}
// 从某节点向上回溯到根，收集所有祖先节点 id
function ancestorsOf(root: TaskNode, id: string): string[] {
  const ids: string[] = [];
  let cur = id;
  let par = findParent(root, cur);
  while (par) {
    ids.push(par.id);
    cur = par.id;
    par = findParent(root, cur);
  }
  return ids;
}

// ---------- outline renderer ----------
function renderOutline(node: TaskNode, out: string, depth: number): string {
  const indent = "  ".repeat(depth);
  const sym = node.status === "done" ? "✓" : node.status === "doing" ? "●" : "○";
  out += `${indent}- ${sym} ${node.title}`;
  if (node.summary) out += ` → ${node.summary}`;
  out += "\n";
  for (const c of node.children) out = renderOutline(c, out, depth + 1);
  return out;
}

// ---------- server ----------
const server = new McpServer({ name: "branch-task", version: "1.2.0" });

server.tool("bt_list_projects", "列出所有项目", {}, async () => {
  return withLock(() => {
    const s = load();
    const list = s.projects.map((p) => ({ id: p.id, name: p.name, current: p.id === s.currentId }));
    return { content: [{ type: "text", text: JSON.stringify(list, null, 2) }] };
  });
});

server.tool(
  "bt_add_project",
  "新建一个项目（含一棵空白主线树），并切换为当前项目",
  { name: z.string().describe("项目名称，通常就是主线任务标题") },
  async ({ name }) => {
    return withLock(() => {
      const s = load();
      const id = genId();
      const root: TaskNode = { id: "root", title: name, status: "doing", summary: "", messages: [], children: [] };
      const proj: Project = { id, name, root, cursor: "root" };
      s.projects.push(proj);
      s.currentId = id;
      save(s);
      return { content: [{ type: "text", text: `已新建项目「${name}」(id=${id})，并切换为当前` }] };
    });
  }
);

server.tool(
  "bt_select_project",
  "切换当前项目（后续操作作用于该项目）",
  { id: z.string().describe("项目 id，可从 bt_list_projects 获取") },
  async ({ id }) => {
    return withLock(() => {
      const s = load();
      if (!s.projects.find((p) => p.id === id)) {
        return { content: [{ type: "text", text: `项目不存在: ${id}` }], isError: true };
      }
      s.currentId = id;
      save(s);
      return { content: [{ type: "text", text: `已切换到项目 ${id}` }] };
    });
  }
);

server.tool(
  "bt_delete_project",
  "删除一个项目",
  { id: z.string().describe("项目 id") },
  async ({ id }) => {
    return withLock(() => {
      const s = load();
      s.projects = s.projects.filter((p) => p.id !== id);
      if (s.currentId === id) s.currentId = s.projects[0]?.id ?? null;
      save(s);
      return { content: [{ type: "text", text: `已删除项目 ${id}` }] };
    });
  }
);

server.tool(
  "bt_start_branch",
  "在当前节点下开一个子分支，并把 cursor 下移到该分支（用于临时去处理一个支线任务）",
  {
    title: z.string().describe("分支标题"),
    status: z.enum(["todo", "doing", "done"]).optional().describe("分支状态，默认 doing"),
  },
  async ({ title, status }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目，请先用 bt_add_project" }], isError: true };
      const parent = findNode(p.root, p.cursor) ?? p.root;
      const node: TaskNode = { id: genId(), title, status: status ?? "doing", summary: "", messages: [], children: [] };
      parent.children.push(node);
      p.cursor = node.id;
      save(s);
      return { content: [{ type: "text", text: `已在「${parent.title}」下开分支「${title}」，cursor 已下移到该分支` }] };
    });
  }
);

server.tool(
  "bt_add_child",
  "在指定父节点下新建一个子任务（不移动 cursor，适合“在 X 下建 Y”这类任意位置插入）",
  {
    parentId: z.string().describe("父节点 id，可用 bt_get_tree 查询"),
    title: z.string().describe("子任务标题"),
    status: z.enum(["todo", "doing", "done"]).optional().describe("状态，默认 todo"),
  },
  async ({ parentId, title, status }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const parent = findNode(p.root, parentId);
      if (!parent) return { content: [{ type: "text", text: `父节点不存在: ${parentId}` }], isError: true };
      const node: TaskNode = { id: genId(), title, status: status ?? "todo", summary: "", messages: [], children: [] };
      parent.children.push(node);
      save(s);
      return { content: [{ type: "text", text: `已在「${parent.title}」下新建子任务「${title}」(id=${node.id})` }] };
    });
  }
);

server.tool(
  "bt_move",
  "把指定节点移动到另一个父节点下（跨层级移动，保持节点内容与 id 不变）；newParentId 传 root 表示移到主线顶层",
  {
    nodeId: z.string().describe("要移动的节点 id"),
    newParentId: z.string().describe("目标父节点 id（用 root 表示主线顶层）"),
  },
  async ({ nodeId, newParentId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (nodeId === "root") return { content: [{ type: "text", text: "根节点不能移动" }], isError: true };
      const node = findNode(p.root, nodeId);
      if (!node) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const target = newParentId === "root" ? p.root : findNode(p.root, newParentId);
      if (!target) return { content: [{ type: "text", text: `目标父节点不存在: ${newParentId}` }], isError: true };
      // 防止成环：不能移到自身或其子树内
      if (nodeId === newParentId || findNode(node, newParentId)) {
        return { content: [{ type: "text", text: "不能移动到自身或其子树内" }], isError: true };
      }
      const oldParent = findParent(p.root, nodeId);
      if (oldParent) oldParent.children = oldParent.children.filter((c) => c.id !== nodeId);
      target.children.push(node);
      save(s);
      return { content: [{ type: "text", text: `已将「${node.title}」移动到「${target.title}」下` }] };
    });
  }
);

server.tool(
  "bt_delete_node",
  "删除指定节点及其整棵子树",
  { nodeId: z.string().describe("要删除的节点 id") },
  async ({ nodeId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (nodeId === "root") return { content: [{ type: "text", text: "根节点不能删除" }], isError: true };
      const parent = findParent(p.root, nodeId);
      if (!parent) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const removed = findNode(p.root, nodeId);
      parent.children = parent.children.filter((c) => c.id !== nodeId);
      save(s);
      return { content: [{ type: "text", text: `已删除「${removed?.title ?? nodeId}」及其子树` }] };
    });
  }
);

server.tool(
  "bt_add_message",
  "给当前节点追加一条对话消息（用户或助手），用于把当时的关键对话留痕",
  { role: z.enum(["user", "assistant"]), content: z.string().describe("消息内容") },
  async ({ role, content }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const cur = findNode(p.root, p.cursor) ?? p.root;
      cur.messages.push({ id: genId(), role, content, ts: Date.now() });
      save(s);
      return { content: [{ type: "text", text: `已向「${cur.title}」追加 ${role} 消息` }] };
    });
  }
);

server.tool(
  "bt_record_note",
  "给当前节点写一句结论摘要（分支处理完的关键结论，回主线时回填用）",
  { text: z.string().describe("结论文本") },
  async ({ text }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const cur = findNode(p.root, p.cursor) ?? p.root;
      cur.summary = text;
      save(s);
      return { content: [{ type: "text", text: `已记录结论到「${cur.title}」：${text}` }] };
    });
  }
);

server.tool(
  "bt_set_status",
  "设置节点状态。默认作用于当前 cursor 节点；传入 nodeId 可指定任意节点（含子任务）。recursive=true 时级联应用到该节点全部后代子任务",
  {
    status: z.enum(["todo", "doing", "done", "parked"]).describe("目标状态"),
    nodeId: z
      .string()
      .optional()
      .describe("目标节点 id（可用 bt_get_tree 查询）；省略则作用于当前 cursor 节点"),
    recursive: z
      .boolean()
      .optional()
      .describe("是否级联应用到全部后代子任务，默认 false。设为 true 可一次性把某节点及其所有子任务改为同一状态"),
  },
  async ({ status, nodeId, recursive }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const target = nodeId
        ? findNode(p.root, nodeId)
        : findNode(p.root, p.cursor) ?? p.root;
      if (!target) return { content: [{ type: "text", text: `节点不存在: ${nodeId ?? p.cursor}` }], isError: true };
      if (recursive) {
        const apply = (n: TaskNode) => {
          n.status = status;
          for (const c of n.children) apply(c);
        };
        apply(target);
        save(s);
        return { content: [{ type: "text", text: `已将「${target.title}」及其全部后代状态 → ${status}` }] };
      }
      target.status = status;
      save(s);
      return { content: [{ type: "text", text: `「${target.title}」状态 → ${status}` }] };
    });
  }
);

server.tool(
  "bt_back_to_main",
  "回到上一层（cursor 上移到父节点），通常用于支线处理完回到主线/父分支",
  {},
  async () => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const par = findParent(p.root, p.cursor);
      if (par) p.cursor = par.id; // par may be root itself
      save(s);
      const cur = findNode(p.root, p.cursor) ?? p.root;
      return { content: [{ type: "text", text: `已回到「${cur.title}」` }] };
    });
  }
);

server.tool(
  "bt_get_tree",
  "返回当前项目的完整任务树（JSON），可用于回看结构或导入前端",
  {},
  async () => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      return { content: [{ type: "text", text: JSON.stringify(p.root, null, 2) }] };
    });
  }
);

server.tool(
  "bt_export_markdown",
  "导出当前项目的大纲（markdown 符号列表），便于贴给同事或存文档",
  {},
  async () => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      return { content: [{ type: "text", text: renderOutline(p.root, "", 0) }] };
    });
  }
);

function buildNode(t: any): TaskNode {
  return {
    id: genId(),
    title: t.title,
    status: (t.status ?? "doing") as Status,
    summary: t.summary ?? "",
    messages: (t.messages ?? []).map((m: any) => ({ id: genId(), role: m.role, content: m.content, ts: Date.now() })),
    children: (t.children ?? []).map((c: any) => buildNode(c)),
  };
}

server.tool(
  "bt_build_tree",
  "一次性构建一棵完整的任务树（新项目），用于初始化或整体重建。接收嵌套结构，无需多次调用，避免并发依赖问题",
  {
    name: z.string().describe("项目名称 / 主线标题"),
    tree: z.any().describe("嵌套结构: {title, status?, summary?, messages?:[{role,content}], children?:[...]}"),
  },
  async ({ name, tree }) => {
    return withLock(() => {
      const s = load();
      const id = genId();
      const root = buildNode(tree);
      root.id = "root";
      root.status = (tree.status ?? "doing") as Status;
      const proj: Project = { id, name, root, cursor: "root" };
      s.projects.push(proj);
      s.currentId = id;
      save(s);
      const count = (function cnt(n: TaskNode): number {
        return 1 + n.children.reduce((a, c) => a + cnt(c), 0);
      })(root);
      return { content: [{ type: "text", text: `已构建项目「${name}」(id=${id})，共 ${count} 个节点` }] };
    });
  }
);

server.tool(
  "bt_list_collapsed",
  "列出当前折叠(收起)的任务节点 id；可传 projectId 只列出该项目内的折叠节点。折叠=该节点被收起、其子任务不可见",
  { projectId: z.string().optional().describe("项目 id；省略则列出全部折叠节点") },
  async ({ projectId }) => {
    return withLock(() => {
      const ui = loadUiState();
      const collapsed = ui.collapsed ?? [];
      if (projectId) {
        const s = load();
        const p = getProject(s, projectId);
        if (!p) return { content: [{ type: "text", text: `项目不存在: ${projectId}` }], isError: true };
        const ids = new Set(collectNodeIds(p.root));
        const filtered = collapsed.filter((c) => ids.has(c));
        return { content: [{ type: "text", text: JSON.stringify(filtered, null, 2) }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(collapsed, null, 2) }] };
    });
  }
);

server.tool(
  "bt_set_collapsed",
  "设置某个任务的折叠状态：collapsed=true 收起(隐藏子任务)，false 展开。节点不存在会报错",
  {
    nodeId: z.string().describe("任务节点 id，用 bt_get_tree 查询"),
    collapsed: z.boolean().describe("true=收起, false=展开"),
    projectId: z.string().optional().describe("项目 id；省略用当前项目"),
  },
  async ({ nodeId, collapsed, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (!findNode(p.root, nodeId)) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const ui = loadUiState();
      const set = new Set(ui.collapsed ?? []);
      if (collapsed) set.add(nodeId);
      else set.delete(nodeId);
      ui.collapsed = [...set];
      saveUiState(ui);
      return { content: [{ type: "text", text: `已将「${nodeId}」${collapsed ? "收起" : "展开"}` }] };
    });
  }
);

server.tool(
  "bt_expand",
  "展开一个任务（显示其子任务）。等价于 bt_set_collapsed(nodeId, false)",
  {
    nodeId: z.string().describe("任务节点 id"),
    projectId: z.string().optional().describe("项目 id；省略用当前项目"),
  },
  async ({ nodeId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (!findNode(p.root, nodeId)) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const ui = loadUiState();
      const set = new Set(ui.collapsed ?? []);
      set.delete(nodeId);
      ui.collapsed = [...set];
      saveUiState(ui);
      return { content: [{ type: "text", text: `已展开「${nodeId}」` }] };
    });
  }
);

server.tool(
  "bt_collapse",
  "收起一个任务（隐藏其子任务）。等价于 bt_set_collapsed(nodeId, true)",
  {
    nodeId: z.string().describe("任务节点 id"),
    projectId: z.string().optional().describe("项目 id；省略用当前项目"),
  },
  async ({ nodeId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (!findNode(p.root, nodeId)) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const ui = loadUiState();
      const set = new Set(ui.collapsed ?? []);
      set.add(nodeId);
      ui.collapsed = [...set];
      saveUiState(ui);
      return { content: [{ type: "text", text: `已收起「${nodeId}」` }] };
    });
  }
);

server.tool(
  "bt_expand_to",
  "展开指定任务，并递归展开它到根节点的所有祖先，使该任务在树中完全可见。即「自动打开想看的任务」",
  {
    nodeId: z.string().describe("目标任务节点 id"),
    projectId: z.string().optional().describe("项目 id；省略用当前项目"),
  },
  async ({ nodeId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (!findNode(p.root, nodeId)) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const ui = loadUiState();
      const set = new Set(ui.collapsed ?? []);
      set.delete(nodeId);
      for (const a of ancestorsOf(p.root, nodeId)) set.delete(a);
      ui.collapsed = [...set];
      saveUiState(ui);
      return { content: [{ type: "text", text: `已展开并打通「${nodeId}」到根的整条路径，该任务现在可见` }] };
    });
  }
);

server.tool(
  "bt_expand_all",
  "展开指定项目的全部任务（清空该项目所有节点的折叠标记，整棵展开）",
  { projectId: z.string().optional().describe("项目 id；省略用当前项目") },
  async ({ projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const ids = new Set(collectNodeIds(p.root));
      const ui = loadUiState();
      ui.collapsed = (ui.collapsed ?? []).filter((c) => !ids.has(c));
      saveUiState(ui);
      return { content: [{ type: "text", text: `已展开项目「${p.name}」全部任务` }] };
    });
  }
);

server.tool(
  "bt_collapse_all",
  "收起指定项目的全部任务（除根节点外所有节点标记为折叠，整棵收起，仅显示主线标题）",
  { projectId: z.string().optional().describe("项目 id；省略用当前项目") },
  async ({ projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const ids = collectNodeIds(p.root).filter((id) => id !== "root");
      const ui = loadUiState();
      const set = new Set(ui.collapsed ?? []);
      for (const id of ids) set.add(id);
      ui.collapsed = [...set];
      saveUiState(ui);
      return { content: [{ type: "text", text: `已收起项目「${p.name}」全部任务` }] };
    });
  }
);

async function main() {
  // clear stale lock left by a crashed process
  try {
    fs.unlinkSync(LOCK_FILE);
  } catch {
    /* ignore */
  }
  const transport = new StdioServerTransport();
  await server.connect(transport);
  // stdio 是 JSON-RPC 通道，不要用 stdout 打日志；如需调试走 stderr
}
main().catch((e) => {
  process.stderr.write("FATAL: " + (e?.stack ?? e) + "\n");
  process.exit(1);
});
