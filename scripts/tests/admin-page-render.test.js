// Renders the real page script against a fake DOM and asserts the things an operator can actually observe.
//
// This is not a browser replacement. It exists because the bug that started this file - `loggedIn` reading an
// HttpOnly cookie, so every button was disabled and a warning banner showed permanently - has a判断 that is worth
// turning into an executable assertion: after a real status with `control.authenticated: true`, the write controls
// must be enabled and the banner gone. The reverse case is asserted too, otherwise the test can pass by accident.
//
//   node admin-page-render.test.js <page.html> <status.json> [mods.json]
const fs = require("fs");
const path = process.argv[2];
const statusPath = process.argv[3];
const modsPath = process.argv[4];
const html = fs.readFileSync(path, "utf8");
const source = html.split("<script>")[1].split("</script>")[0];
const status = JSON.parse(fs.readFileSync(statusPath, "utf8"));
const modsPayload = modsPath && fs.existsSync(modsPath) ? JSON.parse(fs.readFileSync(modsPath, "utf8")) : null;

function makeElement(id) {
  const element = {
    id, className: "", value: "", checked: false, disabled: false, hidden: false,
    style: {}, files: [], children: [], _text: "",
    get textContent() { return this._text; },
    set textContent(value) { this._text = String(value); this.children.length = 0; },
    appendChild(child) { this.children.push(child); return child; },
    addEventListener() {},
    removeAttribute(name) { if (name === "aria-current") this.ariaCurrent = null; },
    setAttribute(name, value) { if (name === "aria-current") this.ariaCurrent = value; if (name === "aria-label") this.ariaLabel = value; },
    getAttribute(name) { return name === "data-view" ? this.dataView : null; },
    querySelectorAll() { return []; },
    cloneNode() { return makeElement(id); },
    // A real class set, not a no-op: `className` and `classList` are written by both the lamp painters and the
    // message helpers, and a stub that swallows `add` made every lamp assertion vacuously true.
    classList: {
      add(name) {
        const parts = this.host.className ? this.host.className.split(/\s+/).filter(Boolean) : [];
        if (parts.indexOf(name) < 0) parts.push(name);
        this.host.className = parts.join(" ");
      },
      remove(name) {
        const parts = this.host.className ? this.host.className.split(/\s+/).filter(Boolean) : [];
        this.host.className = parts.filter((part) => part !== name).join(" ");
      },
      toggle(name, force) {
        const on = force === undefined ? !this.contains(name) : !!force;
        if (on) this.add(name); else this.remove(name);
      },
      contains(name) {
        return (" " + this.host.className + " ").replace(/\s+/g, " ").indexOf(" " + name + " ") >= 0;
      }
    }
  };
  element.classList.host = element;
  return element;
}

const elements = new Map();
const navLinks = [
  "overview", "saves", "mods", "dlc", "steam", "logs", "danger"
].map((view) => {
  const link = makeElement("nav-" + view);
  link.dataView = view;
  return link;
});

const document = {
  getElementById(id) { if (!elements.has(id)) elements.set(id, makeElement(id)); return elements.get(id); },
  createElement(tag) { return makeElement(tag); },
  querySelectorAll(selector) { return selector.indexOf("data-view") >= 0 ? navLinks : []; },
  cookie: ""
};

function store(prefix) {
  const map = new Map();
  return {
    getItem: (key) => (map.has(prefix + key) ? map.get(prefix + key) : null),
    setItem: (key, value) => map.set(prefix + key, value),
    removeItem: (key) => map.delete(prefix + key)
  };
}
const sessionStorage = store("s");
const localStorage = store("l");

const respond = async (url) => {
  if (url.indexOf("/api/mods") === 0 && modsPayload) {
    return { ok: true, status: 200, text: async () => JSON.stringify(modsPayload) };
  }
  if (url.indexOf("/api/saves") === 0) {
    return { ok: true, status: 200, text: async () => JSON.stringify({ ok: true, saves: [] }) };
  }
  return { ok: true, status: 200, text: async () => JSON.stringify({ ok: true }) };
};
const fetch = async (url) => respond(url);
const XMLHttpRequest = function () { this.upload = {}; this.open = () => {}; this.send = () => {}; this.setRequestHeader = () => {}; };
const confirm = () => true;
const setInterval = () => 0;
const location = { hash: "", href: "" };
const alert = () => {};
const Intl = globalThis.Intl;

const runner = new Function("document", "sessionStorage", "localStorage", "fetch", "XMLHttpRequest",
  "confirm", "setInterval", "location", "alert", "window", "Intl",
  source + "\n; window.__render = render; window.__showView = showView; window.__renderDlc = renderDlc;" +
  "\n; window.__renderSyncTable = renderSyncTable; window.__showMods = renderMods;" +
  "\n; window.__renderDialogs = renderDialogs;" +
  "\n; window.__setModData = function (value) { modData = value; };");
const window = {
  addEventListener() {},
  localStorage
};
runner(document, sessionStorage, localStorage, fetch, XMLHttpRequest, confirm, setInterval, location, alert, window, Intl);

const checks = [];
function check(name, ok, detail) { checks.push({ name, ok, detail: detail || "" }); }

window.__render(status);

// ---------- the acceptance pair that motivated this file ----------
// Buttons that depend only on authentication. `serverStart` additionally needs a chosen save, `applyMods` loaded
// mod data and `deleteSave` a selected row, so those three are asserted to follow their own precondition below
// instead of being lumped in here.
const writeIds = ["save2", "reload", "serverStop", "uploadSave", "steamLogin", "download",
  "inspectSaveMods", "applySaveMods", "shutdown"];
const stillDisabled = writeIds.filter((id) => document.getElementById(id).disabled === true);
check("登录后写操作全部可用", stillDisabled.length === 0,
  stillDisabled.length ? "仍被禁用：" + stillDisabled.join(",") : writeIds.length + "/" + writeIds.length + " enabled");
check("没有警告黄条", document.getElementById("banner").className === "banner",
  "banner.className = " + JSON.stringify(document.getElementById("banner").className));
check("banner 文本为空", !document.getElementById("banner").textContent,
  "text=" + JSON.stringify(document.getElementById("banner").textContent));

// ---------- readings ----------
check("状态读数已填（映射为中文标签）", document.getElementById("state").textContent !== "—"
  && document.getElementById("state").textContent.length > 0,
  "state=" + document.getElementById("state").textContent + " raw=" + status.state);
check("顶栏状态文字不是占位符", document.getElementById("stateText").textContent !== "连接中",
  "stateText=" + document.getElementById("stateText").textContent);
check("权威 tick 有值", document.getElementById("tick").textContent !== "—",
  "tick=" + document.getElementById("tick").textContent);
check("运行时长已渲染", /已运行/.test(document.getElementById("uptime").textContent),
  "uptime=" + document.getElementById("uptime").textContent);
check("绑定地址显示端口", /:\d+$/.test(document.getElementById("bind").textContent),
  "bind=" + document.getElementById("bind").textContent);
check("健康摘要已填", !!document.getElementById("health").textContent,
  "health=" + document.getElementById("health").textContent);

// ---------- structure: one nav, seven views, no orphan ids ----------
check("导航有 7 个分区", navLinks.length === 7, "links=" + navLinks.length);
check("默认高亮总览", navLinks[0].ariaCurrent === "page", "aria-current=" + navLinks[0].ariaCurrent);
window.__showView("saves");
check("切到存档后高亮转移", navLinks[1].ariaCurrent === "page" && !navLinks[0].ariaCurrent,
  "saves=" + navLinks[1].ariaCurrent + " overview=" + navLinks[0].ariaCurrent);
check("切分区会隐藏其它视图", document.getElementById("view-overview").hidden === true,
  "view-overview.hidden=" + document.getElementById("view-overview").hidden);
check("切分区会显示目标视图", document.getElementById("view-saves").hidden === false,
  "view-saves.hidden=" + document.getElementById("view-saves").hidden);
window.__showView("danger");
check("关闭服务在最后一分区", document.getElementById("view-danger").hidden === false, "");

// ---------- DLC: the three facts are shown separately ----------
window.__renderDlc(status.dlc);
const dlcHost = document.getElementById("dlcGrid");
check("DLC 表格渲染出全部条目", dlcHost.children.length === 1,
  "children=" + dlcHost.children.length);
if (dlcHost.children.length) {
  const table = dlcHost.children[0];
  check("DLC 表头有 5 列（含磁盘/def/授权分开）", table.children[0].children.length === 5,
    "headers=" + table.children[0].children.length);
  check("DLC 表体条数等于 expansions", table.children.length - 1 === status.dlc.expansions.length,
    "rows=" + (table.children.length - 1) + " / " + status.dlc.expansions.length);
}
check("DLC 摘要有内容", !!document.getElementById("dlcNote").textContent,
  "note=" + document.getElementById("dlcNote").textContent);

// ---------- save-mod sync table ----------
window.__renderSyncTable({
  save: "test", required: [
    { id: "brrainz.harmony", name: "Harmony", workshopId: 0, disposition: "Present" },
    { id: "some.workshopmod", name: "Workshop Mod", workshopId: 2195452496, disposition: "Downloadable" },
    { id: "local.mod", name: "Local", workshopId: 0, disposition: "Manual" }
  ], refusal: "存档需要 1 个服务端没有的 mod"
});
const syncHost = document.getElementById("syncTable");
check("同步表渲染出一张表", syncHost.children.length === 1, "children=" + syncHost.children.length);
if (syncHost.children.length) {
  check("同步表列出全部 3 个 mod", syncHost.children[0].children.length - 1 === 3,
    "rows=" + (syncHost.children[0].children.length - 1));
}
check("缺失 mod 时给拒绝理由", /服务端没有/.test(document.getElementById("syncResult").textContent),
  "result=" + document.getElementById("syncResult").textContent);
check("拒绝理由被判为错误样式", document.getElementById("syncResult").className.indexOf("err") >= 0,
  "class=" + document.getElementById("syncResult").className);

// The server's own mod shows up in every save a client wrote. It is neither a download nor a manual copy, so it
// needs its own colour: green when it is the thing serving you, red when the deployment lost it.
window.__renderSyncTable({
  save: "test", required: [
    { id: "lumidust.turbotick", name: "TurboTick", workshopId: 0, disposition: "Self" },
    { id: "lumidust.turbotick", name: "TurboTick", workshopId: 0, disposition: "SelfMissing" }
  ], refusal: null
});
const selfTags = document.getElementById("syncTable").children[0].children;
const selfRow = selfTags[1].children[3].children[0];
check("服务端自身的 mod 判为正常色", selfRow.className.indexOf("ok") >= 0, "class=" + selfRow.className);
const missingRow = selfTags[2].children[3].children[0];
check("服务端自身缺失判为错误色", missingRow.className.indexOf("bad") >= 0, "class=" + missingRow.className);
check("没有缺失时给成功提示", /都在/.test(document.getElementById("syncResult").textContent),
  "result=" + document.getElementById("syncResult").textContent);

// ---------- mods ----------
if (modsPayload) {
  window.__setModData(modsPayload);
  window.__showMods();
  const list = document.getElementById("modList");
  check("mod 列表渲染出行", list.children.length === (modsPayload.mods || []).length,
    "rows=" + list.children.length + " / " + (modsPayload.mods || []).length);
  // `navMods` is filled by loadMods() (which runs after its own fetch), so what is assertable here is that
  // renderMods() painted every row rather than the nav count.
  check("mod 行数与 payload 一致", document.getElementById("modList").children.length === (modsPayload.mods || []).length,
    "rows=" + document.getElementById("modList").children.length);
}

// ---------- the reverse case: an unauthenticated read must disable writes and explain itself ----------
const anonymous = JSON.parse(JSON.stringify(status));
anonymous.control.authenticated = false;
anonymous.failure = "boom";
window.__render(anonymous);
const enabledAnyway = writeIds.filter((id) => !document.getElementById(id).disabled);
check("未登录时写操作全部禁用（反向对照）", enabledAnyway.length === 0,
  enabledAnyway.length ? "仍可点：" + enabledAnyway.join(",") : writeIds.length + "/" + writeIds.length + " disabled");
check("服务端报错时黄条出现（反向对照）", document.getElementById("banner").className.indexOf("show") >= 0,
  "banner=" + document.getElementById("banner").className);
check("黄条文字点名错误", /boom/.test(document.getElementById("banner").textContent),
  "text=" + document.getElementById("banner").textContent);

// ---------- the game process row ----------
// Re-render the signed-in snapshot first: the reverse-control case above deliberately disables every write, and
// the trio below is about what a signed-in operator can press.
window.__render(status);
// New with the split: the page and the game are siblings, so "is a process up" is a fact this page owns rather
// than one it reads out of the game. Asserted because the section is the visible half of the supervisor work.
check("游戏进程状态已渲染", document.getElementById("procState").textContent === "运行中",
  "state=" + JSON.stringify(document.getElementById("procState").textContent));
// Asserted against the fixture rather than a literal: the fixture is a live capture, so a hardcoded pid only
// ever tests that the file once contained that number.
const procPid = status.process && status.process.pid;
check("游戏进程 pid 已渲染", document.getElementById("procPid").textContent === (procPid ? String(procPid) : ""),
  "pid=" + JSON.stringify(document.getElementById("procPid").textContent) + " / fixture " + JSON.stringify(procPid));
check("游戏进程运行时长已渲染", /已运行/.test(document.getElementById("procUptime").textContent),
  "uptime=" + JSON.stringify(document.getElementById("procUptime").textContent));
// Never-exited and exited-with-zero are different facts: the live capture has `lastExitCode: null` while the
// process is up, so the assertion follows the fixture instead of demanding an exit code that has not happened.
const procExit = status.process ? status.process.lastExitCode : null;
const exitText = document.getElementById("procExit").textContent;
check("最近退出码已渲染", procExit === null || procExit === undefined
    ? exitText === "—"
    : exitText.indexOf("exit " + procExit) === 0,
  "exit=" + JSON.stringify(exitText) + " / fixture " + JSON.stringify(procExit));
check("游戏进程灯是活的", document.getElementById("procLamp").className === "lamp live",
  "className=" + JSON.stringify(document.getElementById("procLamp").className));
// Running ⇒ restart and stop are pressable, start is not. This is the trio the operator actually decides between.
check("运行中时不能重复启动", document.getElementById("gameStart").disabled === true,
  "gameStart.disabled=" + document.getElementById("gameStart").disabled);
check("运行中时可以重启", document.getElementById("gameRestart").disabled === false,
  "gameRestart.disabled=" + document.getElementById("gameRestart").disabled);
check("运行中时可以停止进程", document.getElementById("gameStop").disabled === false,
  "gameStop.disabled=" + document.getElementById("gameStop").disabled);


// ---------- dialogs waiting on a human ----------
// A mod dialog with real callbacks stops the world; the page is the only place it can be answered. Three things
// have to hold or the feature is worse than the bug it replaced: the block appears only when a question exists,
// every button the mod defined is offered, and an announcement (no callbacks) is never presented as a choice.
window.__renderDialogs([]);
check("没有待决对话框时区块隐藏", document.getElementById("dialogBlock").hidden === true,
  "hidden=" + document.getElementById("dialogBlock").hidden);
check("没有待决对话框时不渲染行", document.getElementById("dialogList").children.length === 0,
  "rows=" + document.getElementById("dialogList").children.length);

window.__renderDialogs([
  { id: 7, type: "Verse.Dialog_MessageBox", title: "选一个", text: "两个按钮都在这里。",
    buttonA: "销毁", buttonB: "保留", buttonC: null, destructive: true, autoClosed: false,
    answer: null, repeats: 1 },
  { id: 8, type: "Verse.Dialog_MessageBox", title: "", text: "只有确定的公告。",
    buttonA: "OK", buttonB: null, buttonC: null, destructive: false, autoClosed: true,
    answer: "A", repeats: 2 }
]);
const dialogRows = document.getElementById("dialogList").children;
check("有待决对话框时区块显示", document.getElementById("dialogBlock").hidden === false,
  "hidden=" + document.getElementById("dialogBlock").hidden);
check("每个对话框一行", dialogRows.length === 2, "rows=" + dialogRows.length);
if (dialogRows.length === 2) {
  const first = dialogRows[0];
  // title + type + text + choices = 4 children for a question; an answered one drops the choices row.
  check("待决对话框给出标题与按钮", first.children.length === 4, "children=" + first.children.length);
  const choices = first.children[first.children.length - 1];
  check("两个按钮都提供（A 和 B）", choices.children.length === 2, "buttons=" + choices.children.length);
  check("破坏性按钮有独立样式", first.children.length === 4
    && choices.children[0].className === "destructive", "class=" + (choices.children[0] || {}).className);
  check("公告型不显示按钮", dialogRows[1].children.length === 3, "children=" + dialogRows[1].children.length);
  const state = dialogRows[1].children[dialogRows[1].children.length - 1];
  check("公告型说明它已被自动关掉", /自动关掉/.test(state.textContent), "text=" + state.textContent);
}

// ---------- design tokens actually shipped ----------
check("使用暖色底而不是蓝黑", html.indexOf("--bg: #14100c") >= 0, "");
check("琥珀强调色已定义", html.indexOf("--accent: #e8a33d") >= 0, "");
check("没有蓝色强调残留", html.indexOf("#4c9ff2") < 0, "");
check("没有加载外部字体或 CDN", !/@import|fonts\.googleapis|cdn\./.test(html), "");
check("尊重 prefers-reduced-motion", html.indexOf("prefers-reduced-motion") >= 0, "");

let failed = 0;
for (const item of checks) {
  console.log((item.ok ? "PASS " : "FAIL ") + item.name + "  " + item.detail);
  if (!item.ok) failed++;
}
console.log("RESULT " + (checks.length - failed) + " passed, " + failed + " failed");
process.exit(failed ? 1 : 0);
