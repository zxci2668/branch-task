import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

const STORE_DIR = path.join(os.homedir(), ".branch-task");
const STORE_FILE = path.join(STORE_DIR, "projects.json");
const LOCK_FILE = path.join(STORE_DIR, ".lock");

// ────────── types (aligned with WPF Models/*.cs JsonPropertyName) ──────────
type Status = "" | "doing" | "done" | "blocked" | "todo" | "parked";
interface Msg {
  id: string;
  role: "user" | "assistant";
  content: string;
  ts?: number;
}
interface IntermediateEntry {
  id: string;
  title: string;
  kind: "text" | "file" | "link";
  content: string;
  file_path?: string | null;
  link?: string | null;
  created?: number | null;
}
interface TaskNode {
  id: string;
  title: string;
  status: Status;
  summary: string;
  messages: Msg[];
  task_info?: string;
  children: TaskNode[];
  intermediates?: IntermediateEntry[];
}
interface Project {
  id: string;
  name: string;
  root: TaskNode;
  cursor: string;
  intermediates?: IntermediateEntry[];
}
interface Store {
  projects: Project[];
  currentId?: string | null;
}

// ────────── storage ──────────
function load(): Store {
  try {
    const raw = fs.readFileSync(STORE_FILE, "utf-8");
    const s = JSON.parse(raw) as Store;
    if (!Array.isArray(s.projects)) s.projects = [];
    if (!s.currentId && s.projects.length) s.currentId = s.projects[0].id;
    return s;
  } catch {
    return { projects: [], currentId: null };
  }
}
function save(s: Store) {
  fs.mkdirSync(STORE_DIR, { recursive: true });
  const tmp = STORE_FILE + ".tmp";
  fs.writeFileSync(tmp, JSON.stringify(s, null, 2), "utf-8");
  fs.renameSync(tmp, STORE_FILE); // atomic replace
  notifyFrontend();
}
function notifyFrontend() {
  try { fetch("http://127.0.0.1:8080/api/notify", { method: "POST" }).catch(() => {}); } catch {}
}
function sleep(ms: number) { return new Promise((r) => setTimeout(r, ms)); }
async function withLock<T>(fn: () => T): Promise<T> {
  for (let i = 0; i < 600; i++) {
    try { fs.openSync(LOCK_FILE, "wx"); break; }
    catch { await sleep(10); }
  }
  try { return fn(); }
  finally { try { fs.unlinkSync(LOCK_FILE); } catch {} }
}
function genId(): string { return "n_" + Math.random().toString(36).slice(2, 9); }
function genImId(): string { return "im_" + Math.random().toString(36).slice(2, 9); }
function getProject(s: Store, id?: string): Project | null {
  const pid = id ?? s.currentId;
  return s.projects.find((p) => p.id === pid) ?? s.projects[0] ?? null;
}
function findNode(node: TaskNode, id: string): TaskNode | null {
  if (node.id === id) return node;
  for (const c of node.children) { const r = findNode(c, id); if (r) return r; }
  return null;
}
function findParent(node: TaskNode, id: string, parent: TaskNode | null = null): TaskNode | null {
  if (node.id === id) return parent;
  for (const c of node.children) { const r = findParent(c, id, node); if (r !== null) return r; }
  return null;
}

// ────────── UI state (collapse) ──────────
interface UiState { collapsed?: string[] }
function loadUiState(): UiState {
  try {
    const raw = fs.readFileSync(path.join(STORE_DIR, "ui_state.json"), "utf-8");
    const s = JSON.parse(raw) as UiState;
    if (!Array.isArray(s.collapsed)) s.collapsed = [];
    return s;
  } catch { return { collapsed: [] }; }
}
function saveUiState(s: UiState) {
  fs.mkdirSync(STORE_DIR, { recursive: true });
  fs.writeFileSync(path.join(STORE_DIR, "ui_state.json"), JSON.stringify(s, null, 2), "utf-8");
  notifyFrontend();
}
function collectNodeIds(node: TaskNode, acc: string[] = []): string[] {
  acc.push(node.id);
  for (const c of node.children) collectNodeIds(c, acc);
  return acc;
}
function ancestorsOf(root: TaskNode, id: string): string[] {
  const ids: string[] = [];
  let cur = id;
  let par = findParent(root, cur);
  while (par) { ids.push(par.id); cur = par.id; par = findParent(root, cur); }
  return ids;
}

// ────────── outline renderer ──────────
function renderOutline(node: TaskNode, out: string, depth: number): string {
  const indent = "  ".repeat(depth);
  const sym = node.status === "done" ? "✓" : node.status === "doing" ? "●" : "○";
  out += `${indent}- ${sym} ${node.title}`;
  if (node.summary) out += ` → ${node.summary}`;
  out += "\n";
  for (const c of node.children) out = renderOutline(c, out, depth + 1);
  return out;
}

// ────────── server ──────────
const server = new McpServer({ name: "branch-task-wpf", version: "1.0.0" });

// ═══════════════ 项目管理 ═══════════════
server.tool("bt_list_projects", "列出所有项目", {}, async () => {
  return withLock(() => {
    const s = load();
    const list = s.projects.map((p) => ({ id: p.id, name: p.name, current: p.id === s.currentId }));
    return { content: [{ type: "text", text: JSON.stringify(list, null, 2) }] };
  });
});

server.tool(
  "bt_add_project", "新建一个项目（含一棵空白主线树），并切换为当前项目",
  { name: z.string().describe("项目名称") },
  async ({ name }) => {
    return withLock(() => {
      const s = load();
      const id = genId();
      const root: TaskNode = { id: "root", title: name, status: "doing", summary: "", messages: [], children: [] };
      const proj: Project = { id, name, root, cursor: "root", intermediates: [] };
      s.projects.push(proj);
      s.currentId = id;
      save(s);
      return { content: [{ type: "text", text: `已新建项目「${name}」(id=${id})` }] };
    });
  }
);

server.tool(
  "bt_select_project", "切换当前项目",
  { id: z.string().describe("项目 id") },
  async ({ id }) => {
    return withLock(() => {
      const s = load();
      if (!s.projects.find((p) => p.id === id))
        return { content: [{ type: "text", text: `项目不存在: ${id}` }], isError: true };
      s.currentId = id;
      save(s);
      return { content: [{ type: "text", text: `已切换到项目 ${id}` }] };
    });
  }
);

server.tool(
  "bt_delete_project", "删除一个项目",
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

// ═══════════════ 任务树操作 ═══════════════
server.tool(
  "bt_start_branch", "在当前节点下开一个子分支，并把 cursor 下移到该分支",
  {
    title: z.string().describe("分支标题"),
    status: z.enum(["todo", "doing", "done"]).optional().describe("分支状态，默认 doing"),
  },
  async ({ title, status }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const parent = findNode(p.root, p.cursor) ?? p.root;
      const node: TaskNode = { id: genId(), title, status: (status ?? "doing") as Status, summary: "", messages: [], children: [] };
      parent.children.push(node);
      p.cursor = node.id;
      save(s);
      return { content: [{ type: "text", text: `已在「${parent.title}」下开分支「${title}」，cursor 已下移` }] };
    });
  }
);

server.tool(
  "bt_add_child", "在指定父节点下新建一个子任务（不移动 cursor）",
  {
    parentId: z.string().describe("父节点 id"),
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
      const node: TaskNode = { id: genId(), title, status: (status ?? "todo") as Status, summary: "", messages: [], children: [] };
      parent.children.push(node);
      save(s);
      return { content: [{ type: "text", text: `已在「${parent.title}」下新建「${title}」(id=${node.id})` }] };
    });
  }
);

server.tool(
  "bt_move", "把指定节点移动到另一个父节点下（跨层级移动）",
  {
    nodeId: z.string().describe("要移动的节点 id"),
    newParentId: z.string().describe("目标父节点 id（root 表示顶层）"),
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
      if (nodeId === newParentId || findNode(node, newParentId))
        return { content: [{ type: "text", text: "不能移动到自身或其子树内" }], isError: true };
      const oldParent = findParent(p.root, nodeId);
      if (oldParent) oldParent.children = oldParent.children.filter((c) => c.id !== nodeId);
      target.children.push(node);
      save(s);
      return { content: [{ type: "text", text: `已将「${node.title}」移动到「${target.title}」下` }] };
    });
  }
);

server.tool(
  "bt_delete_node", "删除指定节点及其整棵子树",
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

server.tool("bt_get_tree", "返回当前项目的完整任务树（JSON）", {}, async () => {
  return withLock(() => {
    const s = load();
    const p = getProject(s);
    if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
    return { content: [{ type: "text", text: JSON.stringify(p.root, null, 2) }] };
  });
});

server.tool("bt_export_markdown", "导出当前项目的大纲（markdown）", {}, async () => {
  return withLock(() => {
    const s = load();
    const p = getProject(s);
    if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
    return { content: [{ type: "text", text: renderOutline(p.root, "", 0) }] };
  });
});

function buildNode(t: any): TaskNode {
  return {
    id: t.title === "root" ? "root" : genId(),
    title: t.title,
    status: (t.status ?? "doing") as Status,
    summary: t.summary ?? "",
    messages: (t.messages ?? []).map((m: any) => ({ id: genId(), role: m.role, content: m.content, ts: Date.now() })),
    task_info: t.task_info ?? "",
    children: (t.children ?? []).map((c: any) => buildNode(c)),
  };
}

server.tool(
  "bt_build_tree", "一次性构建一棵完整的任务树（新项目）",
  {
    name: z.string().describe("项目名称"),
    tree: z.any().describe("嵌套结构: {title, status?, summary?, children?:[...], task_info?}"),
  },
  async ({ name, tree }) => {
    return withLock(() => {
      const s = load();
      const id = genId();
      const root = buildNode(tree);
      root.id = "root";
      root.status = (tree.status ?? "doing") as Status;
      const proj: Project = { id, name, root, cursor: "root", intermediates: [] };
      s.projects.push(proj);
      s.currentId = id;
      save(s);
      const count = (function cnt(n: TaskNode): number { return 1 + n.children.reduce((a, c) => a + cnt(c), 0); })(root);
      return { content: [{ type: "text", text: `已构建项目「${name}」(id=${id})，共 ${count} 个节点` }] };
    });
  }
);

// ═══════════════ 状态与消息 ═══════════════
server.tool(
  "bt_add_message", "给当前节点追加一条对话消息",
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
  "bt_record_note", "给当前节点写一句结论摘要",
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
  "bt_set_status", "设置节点状态，recursive=true 时级联应用到全部后代",
  {
    status: z.enum(["todo", "doing", "done", "parked", "blocked"]).describe("目标状态"),
    nodeId: z.string().optional().describe("目标节点 id；省略则作用于当前 cursor"),
    recursive: z.boolean().optional().describe("是否级联应用到全部后代，默认 false"),
  },
  async ({ status, nodeId, recursive }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const target = nodeId ? findNode(p.root, nodeId) : findNode(p.root, p.cursor) ?? p.root;
      if (!target) return { content: [{ type: "text", text: `节点不存在: ${nodeId ?? p.cursor}` }], isError: true };
      if (recursive) {
        const apply = (n: TaskNode) => { n.status = status as Status; for (const c of n.children) apply(c); };
        apply(target);
        save(s);
        return { content: [{ type: "text", text: `已将「${target.title}」及其全部后代状态 → ${status}` }] };
      }
      target.status = status as Status;
      save(s);
      return { content: [{ type: "text", text: `「${target.title}」状态 → ${status}` }] };
    });
  }
);

server.tool(
  "bt_back_to_main", "回到上一层（cursor 上移到父节点）", {},
  async () => {
    return withLock(() => {
      const s = load();
      const p = getProject(s);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      const par = findParent(p.root, p.cursor);
      if (par) p.cursor = par.id;
      save(s);
      const cur = findNode(p.root, p.cursor) ?? p.root;
      return { content: [{ type: "text", text: `已回到「${cur.title}」` }] };
    });
  }
);

// ═══════════════ 折叠 / 展开控制 ═══════════════
server.tool(
  "bt_list_collapsed", "列出当前折叠的节点 id",
  { projectId: z.string().optional().describe("项目 id；省略则列出全部") },
  async ({ projectId }) => {
    return withLock(() => {
      const ui = loadUiState();
      const collapsed = ui.collapsed ?? [];
      if (projectId) {
        const s = load();
        const p = getProject(s, projectId);
        if (!p) return { content: [{ type: "text", text: `项目不存在: ${projectId}` }], isError: true };
        const ids = new Set(collectNodeIds(p.root));
        return { content: [{ type: "text", text: JSON.stringify(collapsed.filter((c) => ids.has(c)), null, 2) }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(collapsed, null, 2) }] };
    });
  }
);

server.tool(
  "bt_set_collapsed", "设置节点的折叠状态",
  {
    nodeId: z.string().describe("节点 id"),
    collapsed: z.boolean().describe("true=收起, false=展开"),
    projectId: z.string().optional().describe("项目 id"),
  },
  async ({ nodeId, collapsed, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      if (!findNode(p.root, nodeId)) return { content: [{ type: "text", text: `节点不存在: ${nodeId}` }], isError: true };
      const ui = loadUiState();
      const set = new Set(ui.collapsed ?? []);
      if (collapsed) set.add(nodeId); else set.delete(nodeId);
      ui.collapsed = [...set];
      saveUiState(ui);
      return { content: [{ type: "text", text: `已将「${nodeId}」${collapsed ? "收起" : "展开"}` }] };
    });
  }
);

server.tool(
  "bt_expand", "展开一个任务（显示其子任务）",
  { nodeId: z.string(), projectId: z.string().optional() },
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
  "bt_collapse", "收起一个任务",
  { nodeId: z.string(), projectId: z.string().optional() },
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
  "bt_expand_to", "展开指定任务及其到根的所有祖先，使之完全可见",
  { nodeId: z.string(), projectId: z.string().optional() },
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
      return { content: [{ type: "text", text: `已展开并打通「${nodeId}」到根的路径` }] };
    });
  }
);

server.tool(
  "bt_expand_all", "展开项目全部任务",
  { projectId: z.string().optional() },
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
  "bt_collapse_all", "收起项目全部子任务（仅显示主线标题）",
  { projectId: z.string().optional() },
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

// ═══════════════ 中间结果 / 项目里程碑（WPF 新增）═══════════════
server.tool(
  "bt_list_intermediates", "列出项目或任务下的所有中间结果（项目里程碑 / 任务中间结果）",
  {
    parentId: z.string().optional().describe("任务节点 id；省略则列出当前项目级里程碑"),
    projectId: z.string().optional().describe("项目 id；省略用当前项目"),
  },
  async ({ parentId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };
      let ims: IntermediateEntry[];
      let scope: string;
      if (parentId) {
        const node = findNode(p.root, parentId);
        if (!node) return { content: [{ type: "text", text: `节点不存在: ${parentId}` }], isError: true };
        ims = node.intermediates ?? [];
        scope = `「${node.title}」`;
      } else {
        ims = p.intermediates ?? [];
        scope = `项目「${p.name}」`;
      }
      return { content: [{ type: "text", text: JSON.stringify({ scope, intermediates: ims }, null, 2) }] };
    });
  }
);

server.tool(
  "bt_add_intermediate", "向项目或任务添加中间结果（文字/文件/链接）",
  {
    kind: z.enum(["text", "file", "link"]).describe("类型：text=纯文字, file=文件路径, link=链接URL"),
    title: z.string().describe("条目名称"),
    content: z.string().optional().describe("文字内容（kind=text 时用）"),
    link: z.string().optional().describe("链接 URL（kind=link 时用）"),
    filePath: z.string().optional().describe("文件路径（kind=file 时用）"),
    parentId: z.string().optional().describe("任务节点 id；省略则添加到项目级（项目里程碑）"),
    projectId: z.string().optional().describe("项目 id"),
  },
  async ({ kind, title, content, link, filePath, parentId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };

      const im: IntermediateEntry = {
        id: genImId(),
        title,
        kind,
        content: content ?? "",
        file_path: filePath ?? null,
        link: link ?? null,
        created: Math.floor(Date.now() / 1000),
      };

      if (parentId) {
        const node = findNode(p.root, parentId);
        if (!node) return { content: [{ type: "text", text: `节点不存在: ${parentId}` }], isError: true };
        if (!node.intermediates) node.intermediates = [];
        node.intermediates.push(im);
        save(s);
        return { content: [{ type: "text", text: `已向「${node.title}」添加${kind}中间结果「${title}」(id=${im.id})` }] };
      } else {
        if (!p.intermediates) p.intermediates = [];
        p.intermediates.push(im);
        save(s);
        return { content: [{ type: "text", text: `已向项目「${p.name}」添加${kind}里程碑「${title}」(id=${im.id})` }] };
      }
    });
  }
);

server.tool(
  "bt_update_intermediate", "更新中间结果的标题/内容/链接/文件路径",
  {
    imId: z.string().describe("中间结果 id（从 bt_list_intermediates 获取）"),
    title: z.string().optional().describe("新标题"),
    content: z.string().optional().describe("新文字内容"),
    link: z.string().optional().describe("新链接 URL"),
    filePath: z.string().optional().describe("新文件路径"),
    parentId: z.string().optional().describe("所在任务节点 id；省略则搜索项目级"),
    projectId: z.string().optional().describe("项目 id"),
  },
  async ({ imId, title, content, link, filePath, parentId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };

      const findIm = (list: IntermediateEntry[] | undefined) => list?.find((x) => x.id === imId);

      let im: IntermediateEntry | undefined;
      let scope: string;
      if (parentId) {
        const node = findNode(p.root, parentId);
        if (!node) return { content: [{ type: "text", text: `节点不存在: ${parentId}` }], isError: true };
        im = findIm(node.intermediates);
        scope = `「${node.title}」`;
      } else {
        im = findIm(p.intermediates);
        scope = `项目「${p.name}」`;
      }

      if (!im) return { content: [{ type: "text", text: `中间结果不存在: ${imId}` }], isError: true };
      if (title !== undefined) im.title = title;
      if (content !== undefined) im.content = content;
      if (link !== undefined) im.link = link;
      if (filePath !== undefined) im.file_path = filePath;
      save(s);
      return { content: [{ type: "text", text: `已更新 ${scope} 的「${im.title}」` }] };
    });
  }
);

server.tool(
  "bt_delete_intermediate", "删除一个中间结果",
  {
    imId: z.string().describe("中间结果 id"),
    parentId: z.string().optional().describe("所在任务节点 id；省略则搜索项目级"),
    projectId: z.string().optional().describe("项目 id"),
  },
  async ({ imId, parentId, projectId }) => {
    return withLock(() => {
      const s = load();
      const p = getProject(s, projectId);
      if (!p) return { content: [{ type: "text", text: "无当前项目" }], isError: true };

      if (parentId) {
        const node = findNode(p.root, parentId);
        if (!node) return { content: [{ type: "text", text: `节点不存在: ${parentId}` }], isError: true };
        const before = node.intermediates?.length ?? 0;
        if (node.intermediates) node.intermediates = node.intermediates.filter((x) => x.id !== imId);
        if ((node.intermediates?.length ?? 0) === before)
          return { content: [{ type: "text", text: `中间结果不存在: ${imId}` }], isError: true };
        save(s);
        return { content: [{ type: "text", text: `已从「${node.title}」删除中间结果 ${imId}` }] };
      } else {
        if (!p.intermediates) return { content: [{ type: "text", text: `中间结果不存在: ${imId}` }], isError: true };
        const before = p.intermediates.length;
        p.intermediates = p.intermediates.filter((x) => x.id !== imId);
        if (p.intermediates.length === before)
          return { content: [{ type: "text", text: `中间结果不存在: ${imId}` }], isError: true };
        save(s);
        return { content: [{ type: "text", text: `已从项目「${p.name}」删除里程碑 ${imId}` }] };
      }
    });
  }
);

// ═══════════════ 入口 ═══════════════
async function main() {
  try { fs.unlinkSync(LOCK_FILE); } catch {}
  const transport = new StdioServerTransport();
  await server.connect(transport);
}
main().catch((e) => {
  process.stderr.write("FATAL: " + (e?.stack ?? e) + "\n");
  process.exit(1);
});
