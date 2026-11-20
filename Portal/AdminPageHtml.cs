using System.Text.Encodings.Web;

namespace TurboTick.Portal;

/// <summary>
/// The pages served at <c>/</c> and <c>/login</c>. One string each, no build step: an operator with a browser can see
/// a server and press buttons. Nothing is loaded from a CDN or a second file, so the page cannot break because this
/// machine is offline, and it cannot leak the room's address to a third party by requesting a font.
///
/// <para><b>Design brief.</b> This is a control terminal for one machine, not a dashboard and not a landing page. The
/// visual language is the game's own: amber work-light on dark iron, rust for destruction, moss for healthy. It is
/// deliberately <em>not</em> a SaaS admin - no card grid, no gradient decoration, no uppercase micro-labels. Every
/// reading is monospace because the readings are the machine talking; prose stays in the system sans so the two are
/// never confused.</para>
///
/// <para><b>Structure.</b> Navigation follows the order of an operator's actual work: look at the world, then add a
/// save, then reconcile its mods, then check the DLC and Steam, then read the log. Destroying the service is
/// separated from all of it and sits last, because it is the one action that takes this page away.</para>
///
/// <para><b>Safety in the script.</b> It only ever writes through <c>textContent</c>. Log lines, save names and mod
/// ids are attacker-shaped text on a shared server; inserting them as HTML would turn a mod log line into script in
/// this origin.</para>
/// </summary>
internal static class AdminPageHtml
{
    /// <summary>The login form. A real form POST, so it works with JavaScript disabled and with curl.</summary>
    internal const string LoginShell = """
    <!DOCTYPE html>
    <html lang="zh-CN">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="color-scheme" content="dark">
    <title>TurboTick · 登录</title>
    <style>
      :root {
        --bg: #14100c; --panel: #1e1813; --panel-2: #2a211a; --line: #3d3228;
        --text: #f0e6d8; --muted: #a89684; --accent: #e8a33d; --danger: #d9705a;
        --mono: ui-monospace, "JetBrains Mono", "Cascadia Code", Consolas, monospace;
        --sans: system-ui, -apple-system, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
      }
      * { box-sizing: border-box; }
      body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
        background: var(--bg); color: var(--text); font: 14px/1.55 var(--sans);
        background-image: radial-gradient(ellipse at 50% 0%, #241a11 0%, transparent 62%); }
      form { width: min(430px, 92vw); background: var(--panel); border: 1px solid var(--line); padding: 26px 26px 20px; }
      .brand { display: flex; align-items: center; gap: 9px; margin: 0 0 3px; }
      .brand b { font: 600 16px/1 var(--mono); letter-spacing: .04em; }
      .lamp { width: 8px; height: 8px; border-radius: 50%; background: var(--accent);
        box-shadow: 0 0 0 3px #e8a33d22; }
      p.sub { margin: 0 0 20px; color: var(--muted); font-size: 12.5px; }
      label { display: block; font-size: 12px; color: var(--muted); margin-bottom: 6px; }
      input[type=password] { width: 100%; background: #120e0a; border: 1px solid var(--line); color: var(--text);
        padding: 10px 12px; font: 14px var(--mono); margin-bottom: 14px; }
      input[type=password]:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; border-color: var(--accent); }
      button { width: 100%; background: var(--panel-2); border: 1px solid #55442f; color: var(--text);
        padding: 10px 14px; font: 500 14px var(--sans); cursor: pointer; }
      button:hover { background: #352a20; border-color: var(--accent); color: var(--accent); }
      button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
      .err { display: none; margin: 0 0 14px; padding: 9px 11px; border-left: 2px solid var(--danger);
        background: #2a1a16; color: #e8bdb2; font-size: 13px; }
      .err.show { display: block; }
      code { font: 12px var(--mono); color: var(--accent); }
      footer { color: var(--muted); font-size: 11.5px; margin-top: 16px; line-height: 1.6; }
    </style>
    </head>
    <body>
    <form method="post" action="/login">
      <p class="brand"><span class="lamp"></span><b>TurboTick</b></p>
      <p class="sub">管理台密码来自服务端的 <code>TURBOTICK_ADMIN_PASSWORD</code>。</p>
      <div class="err %ERROR_CLASS%" id="err">%ERROR%</div>
      <label for="password">管理密码</label>
      <input type="password" id="password" name="password" autocomplete="current-password"
             autofocus spellcheck="false">
      <button type="submit">登录</button>
      <footer>登录后发一个 HttpOnly + SameSite=Strict 的会话 cookie，12 小时后过期。
        密码不会写进 URL，也不会留在本页。</footer>
    </form>
    </body>
    </html>
    """;

    internal static string Login => LoginShell
        .Replace("%ERROR_CLASS%", "x", StringComparison.Ordinal)
        .Replace("%ERROR%", "", StringComparison.Ordinal);

    internal static string LoginError(string message)
    {
        var safe = HtmlEncoder.Default.Encode(message ?? "");
        return LoginShell
            .Replace("%ERROR_CLASS%", "show", StringComparison.Ordinal)
            .Replace("%ERROR%", safe, StringComparison.Ordinal);
    }

    internal const string Raw = """
    <!DOCTYPE html>
    <html lang="zh-CN">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="color-scheme" content="dark">
    <title>TurboTick · 控制台</title>
    <style>
      :root {
        --bg: #14100c; --panel: #1e1813; --panel-2: #2a211a; --line: #3d3228;
        --text: #f0e6d8; --muted: #a89684;
        --accent: #e8a33d; --ok: #7fa650; --warn: #d4913a; --danger: #d9705a;
        --mono: ui-monospace, "JetBrains Mono", "Cascadia Code", Consolas, monospace;
        --sans: system-ui, -apple-system, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
        --nav-w: 178px; --bar-h: 52px;
      }
      * { box-sizing: border-box; }
      html, body { height: 100%; }
      body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.55 var(--sans); }

      /* ---------- top bar: the one place that carries identity ---------- */
      .topbar { position: sticky; top: 0; z-index: 20; height: var(--bar-h); display: flex; align-items: center;
        gap: 14px; padding: 0 18px; background: var(--panel); border-bottom: 1px solid var(--line); }
      .brand { display: flex; align-items: center; gap: 9px; font: 600 15px/1 var(--mono); letter-spacing: .04em; }
      /* The single deliberate motion in the page: an amber work-light breathing while the world is live.
         Nothing else on this page animates without a user action. */
      .lamp { width: 9px; height: 9px; border-radius: 50%; background: var(--muted); flex: none;
        transition: background .3s ease, box-shadow .3s ease; }
      .lamp.live { background: var(--accent); animation: breathe 2.8s ease-in-out infinite; }
      .lamp.warn { background: var(--warn); }
      .lamp.dead { background: var(--danger); }
      @keyframes breathe {
        0%, 100% { box-shadow: 0 0 0 0 #e8a33d00; }
        50%      { box-shadow: 0 0 0 6px #e8a33d33; }
      }
      @media (prefers-reduced-motion: reduce) {
        .lamp.live { animation: none; box-shadow: 0 0 0 3px #e8a33d33; }
        * { transition: none !important; }
      }
      .stateText { font: 500 13px var(--mono); color: var(--text); }
      .stateText.sub { color: var(--muted); font-size: 12px; }
      .bind { font: 12px var(--mono); color: var(--muted); }
      .grow { flex: 1 1 auto; }
      .iconbtn { background: transparent; border: 1px solid var(--line); color: var(--muted);
        font: 12px var(--sans); padding: 5px 11px; cursor: pointer; }
      .iconbtn:hover { color: var(--accent); border-color: var(--accent); }
      .iconbtn:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

      /* ---------- shell ---------- */
      .shell { display: flex; align-items: flex-start; min-height: calc(100vh - var(--bar-h)); }
      .nav { position: sticky; top: var(--bar-h); width: var(--nav-w); flex: none; align-self: flex-start;
        padding: 14px 0 22px; border-right: 1px solid var(--line); min-height: calc(100vh - var(--bar-h));
        display: flex; flex-direction: column; }
      .nav a { display: flex; align-items: center; gap: 9px; padding: 8px 16px; color: var(--muted);
        text-decoration: none; font-size: 13px; border-left: 2px solid transparent; }
      .nav a:hover { color: var(--text); background: #1a1510; }
      .nav a[aria-current="page"] { color: var(--accent); border-left-color: var(--accent); background: #1a1510; }
      .nav a:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
      .nav .count { margin-left: auto; font: 11px var(--mono); color: var(--muted); }
      .nav .sep { flex: 1 1 auto; min-height: 18px; }
      .nav .zone { padding: 0; margin: 0; }
      .nav .zone a { color: var(--danger); }
      .nav .zone a:hover { color: #e2836d; background: #241713; }

      main { flex: 1 1 auto; min-width: 0; padding: 22px 26px 64px; max-width: 1080px; }
      .view[hidden] { display: none; }
      .view > h1 { margin: 0 0 2px; font: 600 17px/1.3 var(--sans); }
      .view > .lede { margin: 0 0 20px; color: var(--muted); font-size: 12.5px; max-width: 68ch; }

      /* ---------- readings: a monospace instrument strip, not a card grid ---------- */
      /* Four fixed columns, not auto-fit: seven readings at 150px each overflowed to a second row and left the
         last cell stranded. Two rows of four is stable at every width and reads like an instrument panel. */
      .strip { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr));
        border: 1px solid var(--line); background: var(--panel); }
      .strip .cell { padding: 11px 14px; border-right: 1px solid var(--line); min-width: 0; }
      .strip .cell:hover .v { white-space: normal; overflow: visible; }
      .strip .cell:nth-child(4n) { border-right: none; }
      .strip .cell:nth-child(n+5) { border-top: 1px solid var(--line); }
      @media (max-width: 720px) { .strip { grid-template-columns: repeat(2, minmax(0, 1fr)); }
        .strip .cell:nth-child(2n) { border-right: none; }
        .strip .cell:nth-child(n+3) { border-top: 1px solid var(--line); } }
      .strip .k { font-size: 11.5px; color: var(--muted); }
      /* Readings never wrap: a number split across two lines ("空载 · 等待 / 载入") is unreadable at a glance,
         which defeats the point of an instrument strip. Long values ellipsize and the full text stays in the title. */
      .strip .v { font: 600 19px/1.25 var(--mono); margin-top: 3px; font-variant-numeric: tabular-nums;
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .strip .v.sm { font-size: 14px; font-weight: 500; }
      .strip .u { font-size: 11px; color: var(--muted); font-family: var(--mono); margin-top: 1px; }

      /* ---------- section blocks ---------- */
      section.block { margin-top: 26px; }
      section.block > h2 { margin: 0 0 10px; font: 600 14px/1.4 var(--sans); color: var(--text);
        display: flex; align-items: baseline; gap: 10px; }
      section.block > h2 .aside { font: 400 12px var(--sans); color: var(--muted); }
      .controls { display: flex; gap: 9px; flex-wrap: wrap; align-items: center; }
      .controls + .controls { margin-top: 9px; }
      button { background: var(--panel-2); border: 1px solid #55442f; color: var(--text);
        padding: 7px 13px; font: 13px var(--sans); cursor: pointer; }
      button:hover:not(:disabled) { background: #352a20; border-color: var(--accent); color: var(--accent); }
      button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
      button:disabled { opacity: .4; cursor: not-allowed; }
      button.primary { border-color: var(--accent); color: var(--accent); }
      button.primary:hover:not(:disabled) { background: #33261591; }
      button.danger { border-color: #6b3a30; color: #dd8570; }
      button.danger:hover:not(:disabled) { background: #2a1a16; border-color: var(--danger); color: #f0a794; }
      input[type=text], input[type=password], input[type=file], select {
        background: #120e0a; border: 1px solid var(--line); color: var(--text);
        padding: 7px 10px; font: 13px var(--sans); min-width: 0; flex: 0 1 250px; }
      input:focus-visible, select:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
      input[type=file] { font-size: 12px; color: var(--muted); }
      .note { color: var(--muted); font-size: 12.5px; margin: 9px 0 0; max-width: 76ch; }
      .note code, .msg code { font: 12px var(--mono); color: var(--accent); }
      .msg { margin: 10px 0 0; font-size: 12.5px; color: var(--text); min-height: 1em; }
      .msg.err { color: #e8b0a1; }
      .msg.good { color: var(--ok); }
      .msg.warn { color: var(--warn); }
      /* The process row. Deliberately a definition list, not another .strip grid: these four facts are read
         together ("is a process up, and if not why"), and a grid invites scanning them as independent gauges. */
      .proc { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px 16px;
        margin: 12px 0 0; font-size: 12.5px; }
      .proc dt { color: var(--muted); }
      .proc dd { margin: 0; font-family: var(--mono); color: var(--text); overflow-wrap: anywhere; }
      .proc .state { display: inline-flex; align-items: center; gap: 7px; }
      .proc .state .lamp { width: 7px; height: 7px; }
      .proc dd.err { color: #e8b0a1; }

      .banner { display: none; margin: 0 0 16px; padding: 10px 13px; border-left: 2px solid var(--danger);
        background: #2a1a16; color: #e8bdb2; font-size: 13px; }
      .banner.show { display: block; }
      .banner.info { border-left-color: var(--ok); background: #1e2118; color: #cfd9b4; }

      /* ---------- tables ---------- */
      table.data { width: 100%; border-collapse: collapse; font-size: 12.5px; }
      table.data th { text-align: left; font: 500 11.5px var(--sans); color: var(--muted);
        padding: 7px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
      table.data td { padding: 8px 10px; border-bottom: 1px solid #2c231b; vertical-align: top; }
      table.data tr:hover td { background: #1a1510; }
      table.data td.mono { font-family: var(--mono); font-size: 12px; word-break: break-all; }
      table.data td.num { font-family: var(--mono); text-align: right; font-variant-numeric: tabular-nums; }
      /* Selection column: narrow and fixed, so the checkboxes form a straight edge instead of shifting with the
         first column's text. `position: sticky` keeps the header visible while scrolling a hundred rows. */
      table.data th.pick, table.data td.pick { width: 26px; padding-right: 0; text-align: center; }
      table.data thead th { position: sticky; top: 0; background: var(--bg, #14100c); z-index: 1; }
      table.data tr.picked td { background: #1d1a12; }
      .check { display: inline-flex; gap: 6px; align-items: center; cursor: pointer; font-size: 12.5px; }
      .tag { display: inline-block; font: 11px var(--mono); padding: 0 5px; border: 1px solid var(--line);
        color: var(--muted); }
      .tag.ok { border-color: #46603a; color: var(--ok); }
      .tag.warn { border-color: #6b5a2c; color: var(--warn); }
      .tag.bad { border-color: #6b3a30; color: var(--danger); }

      pre.log { margin: 0; padding: 12px 14px; background: #100c09; border: 1px solid var(--line);
        height: min(52vh, 460px); overflow: auto; font: 12px/1.6 var(--mono); white-space: pre-wrap;
        word-break: break-word; color: #d8cbb8; }
      pre.log .w { color: var(--warn); }
      pre.log .e { color: var(--danger); }

      progress { width: 100%; height: 8px; }

      /* ---------- mod list ---------- */
      .modlist { border: 1px solid var(--line); background: var(--panel); max-height: 440px; overflow: auto; }
      .modrow { display: grid; grid-template-columns: 22px 1fr auto; gap: 10px; align-items: baseline;
        padding: 7px 12px; border-bottom: 1px solid #2c231b; font-size: 12.5px; }
      .modrow:last-child { border-bottom: none; }
      .modrow.on { background: #1c1a13; }
      .modrow .pid { font: 12px var(--mono); word-break: break-all; }
      .modrow .pname { color: var(--muted); font-size: 11.5px; }
      .modrow input[type=checkbox] { accent-color: var(--accent); margin: 2px 0 0; }
      .empty { padding: 22px 16px; color: var(--muted); font-size: 13px; border: 1px dashed var(--line); }
      .empty b { color: var(--text); font-weight: 500; }

      /* ---------- dialogs waiting on a human ---------- */
      /* One block per question, because two mods can have two questions open at once and they are independent.
         The accent border is deliberate: this is the only element on the page that stops the world. */
      .dialog { border: 1px solid var(--line); border-left: 3px solid var(--accent); background: var(--panel);
        padding: 12px 14px; margin-bottom: 9px; }
      .dialog .dtitle { font: 600 13.5px var(--mono); color: var(--text); margin-bottom: 6px; }
      .dialog .dtype { font: 11.5px var(--mono); color: var(--muted); margin-bottom: 8px; word-break: break-all; }
      .dialog .dtext { font-size: 13px; color: var(--text); white-space: pre-wrap; max-width: 78ch;
        margin-bottom: 10px; line-height: 1.5; }
      .dialog .dtext.clip { max-height: 7.5em; overflow: hidden; }
      .dialog .dmore { background: none; border: none; color: var(--accent); cursor: pointer;
        font: 11.5px var(--mono); padding: 0 0 8px; }
      .dialog .dchoices { display: flex; gap: 9px; flex-wrap: wrap; }
      .dialog .dmeta { font: 11.5px var(--mono); color: var(--muted); margin-top: 8px; }
      .dialog .dmeta .danger { color: var(--danger); }
      /* A destructive choice is the button a mod labels as such; it must not look like the safe one. */
      .dialog button.destructive { border-color: var(--danger); color: var(--danger); }
      .dialog.done { border-left-color: var(--ok); opacity: 0.75; }

      /* ---------- mobile: nav becomes a tab strip ---------- */
      @media (max-width: 720px) {
        .shell { flex-direction: column; }
        .nav { position: sticky; top: var(--bar-h); width: 100%; min-height: 0; flex-direction: row;
          overflow-x: auto; border-right: none; border-bottom: 1px solid var(--line); padding: 0;
          background: var(--panel); z-index: 15; }
        .nav a { border-left: none; border-bottom: 2px solid transparent; white-space: nowrap; padding: 10px 13px; }
        .nav a[aria-current="page"] { border-left: none; border-bottom-color: var(--accent); }
        .nav .sep, .nav .count { display: none; }
        .nav .zone a { border-bottom-color: transparent; }
        main { padding: 16px 15px 56px; max-width: none; }
        .strip .cell { border-right: none; border-bottom: 1px solid var(--line); }
        .topbar { gap: 10px; padding: 0 13px; }
        .bind { display: none; }
      }
      @media (prefers-reduced-motion: reduce) { * { animation: none !important; } }
    </style>
    </head>
    <body>
    <div class="topbar">
      <span class="brand"><span class="lamp" id="lamp"></span>TurboTick</span>
      <span class="stateText" id="stateText">连接中</span>
      <span class="bind" id="bind"></span>
      <span class="grow"></span>
      <span class="bind" id="uptime"></span>
      <button class="iconbtn" id="logout" type="button">注销</button>
    </div>

    <div class="shell">
      <nav class="nav" aria-label="分区">
        <a href="#overview" data-view="overview" aria-current="page">总览</a>
        <a href="#saves" data-view="saves">存档 <span class="count" id="navSaves"></span></a>
        <a href="#mods" data-view="mods">Mod <span class="count" id="navMods"></span></a>
        <a href="#dlc" data-view="dlc">DLC <span class="count" id="navDlc"></span></a>
        <a href="#steam" data-view="steam">Steam</a>
        <a href="#logs" data-view="logs">日志</a>
        <span class="sep"></span>
        <span class="zone"><a href="#danger" data-view="danger">关闭服务</a></span>
      </nav>

      <main>
        <div class="banner" id="banner"></div>

        <!-- ============ overview ============ -->
        <div class="view" id="view-overview">
          <h1>当前世界</h1>
          <p class="lede">这台机器是唯一的权威副本。停世界只是停模拟，管理页会一直在这里；换档不需要先停。</p>

          <div class="strip">
            <div class="cell"><div class="k">状态</div><div class="v" id="state">—</div>
              <div class="u" id="failure"></div></div>
            <div class="cell"><div class="k">存档</div><div class="v sm" id="save">—</div>
              <div class="u" id="gameTick"></div></div>
            <div class="cell"><div class="k">权威 tick</div><div class="v" id="tick">—</div>
              <div class="u" id="checkpoint"></div></div>
            <div class="cell"><div class="k">副本 / 玩家</div><div class="v" id="replicas">—</div>
              <div class="u" id="players"></div></div>
            <div class="cell"><div class="k">模拟速率</div><div class="v" id="tps">—</div>
              <div class="u" id="fps"></div></div>
            <div class="cell"><div class="k">线路</div><div class="v sm" id="up">—</div>
              <div class="u" id="upRate"></div></div>
            <div class="cell"><div class="k">健康</div><div class="v sm" id="comp">—</div>
              <div class="u" id="health"></div></div>
            <div class="cell"><div class="k">目标速率</div><div class="v" id="targetTps">—</div>
              <div class="u" id="tickBudget"></div></div>
          </div>

          <section class="block" id="dialogBlock" hidden>
            <h2>等待你选择的对话框 <span class="aside">世界就是因为它停在这里</span></h2>
            <div id="dialogList"></div>
            <p class="note">mod 弹出了带选项的对话框，无头服务端没有人能点它，所以模拟停住了——这是对的，
              mod 的流程正等这个答案。选完会执行该按钮原本的回调，然后世界继续跑。
              只有「确定」的纯提示框会被自动关掉，并记在日志里。</p>
            <p class="msg" id="dialogResult"></p>
          </section>

          <section class="block">
            <h2>世界控制</h2>
            <div class="controls">
              <select id="saveSelect" aria-label="选择存档"><option value="">（读取存档列表…）</option></select>
              <button id="serverStart" class="primary" type="button" disabled>载入这个存档</button>
              <button id="serverStop" type="button" disabled>停止世界</button>
              <button id="refreshSaves" type="button">刷新列表</button>
            </div>
            <div class="controls">
              <button id="save2" type="button" disabled>立即检查点</button>
              <button id="reload" type="button" disabled>重读设置</button>
            </div>
            <p class="note"><b>停止世界</b>＝检查点后停掉模拟，服务端回到 <code>lobby</code>，本页照常可用，可以立刻载入另一个存档。
              <b>已经在跑别的存档时直接点「载入这个存档」就是换档</b>，会先给当前世界做检查点，不需要先停。</p>
            <p class="msg" id="serverResult"></p>
            <p class="msg" id="switchNote"></p>
            <p class="note" id="controlNote"></p>
            <p class="msg" id="commandResult"></p>
            <p class="note" id="checkpointNote"></p>
          </section>

          <section class="block">
            <h2>游戏进程 <span class="aside">本页是它的兄弟进程，不是它的一部分</span></h2>
            <dl class="proc">
              <dt>状态</dt>
              <dd><span class="state"><span class="lamp" id="procLamp"></span><span id="procState">—</span></span></dd>
              <dt>pid</dt><dd id="procPid">—</dd>
              <dt>运行时长</dt><dd id="procUptime">—</dd>
              <dt>最近退出</dt><dd id="procExit">—</dd>
            </dl>
            <div class="controls">
              <button id="gameStart" type="button" disabled>启动游戏进程</button>
              <button id="gameRestart" type="button" disabled>重启游戏进程</button>
              <button id="gameStop" type="button" disabled>停止游戏进程</button>
            </div>
            <p class="note"><b>重启游戏进程</b>不会影响本页：管理页跑在自己的进程里，登录状态也不变。
              换档靠的就是这个——游戏进程一次只能载入一个世界，第二个世界只能在新的进程里打开。</p>
            <p class="msg" id="procResult"></p>
            <p class="note" id="procNote"></p>
          </section>

          <section class="block">
            <h2>最近日志 <span class="aside">完整筛选在「日志」分区</span></h2>
            <pre class="log" id="logTail">（等待状态）</pre>
          </section>
        </div>

        <!-- ============ saves ============ -->
        <div class="view" id="view-saves" hidden>
          <h1>存档</h1>
          <p class="lede">在客户端里 <code>Esc → 保存</code>，然后把 <code>&lt;profile&gt;\Saves\*.rws</code> 拖进来。
            服务端会自己改名、校验 sha256，并读出这份存档需要哪些 mod。</p>

          <section class="block">
            <h2>上传</h2>
            <div class="controls">
              <input type="file" id="saveFile" accept=".rws,application/octet-stream" aria-label="选择存档文件">
              <button id="uploadSave" class="primary" type="button" disabled>上传 .rws</button>
            </div>
            <progress id="uploadProgress" value="0" max="100"></progress>
            <p class="note" id="uploadNote">上限 256 MB。上传完会就地显示这份存档需要的 mod。</p>
            <p class="msg" id="uploadResult"></p>
          </section>

          <section class="block">
            <h2>列表 <span class="aside" id="savesNote">存档只在服务端</span></h2>
            <!-- The delete control belongs above the table, not under it: the library reaches a hundred entries and
                 the button used to sit past the last row, so clearing out old imports meant scrolling to the bottom
                 every time. It also acts on the selection, and the selection is made in the header row's checkboxes
                 right below this line. -->
            <div class="controls" id="saveToolbar">
              <label class="check"><input type="checkbox" id="saveSelectAll" aria-label="全选"> 全选</label>
              <span class="aside" id="saveSelectedNote">未选中</span>
              <button id="deleteSave" class="danger" type="button" disabled>删除选中</button>
            </div>
            <div id="saveTable"></div>
            <p class="msg" id="saveResult"></p>
          </section>

          <section class="block">
            <h2>这份存档需要哪些 mod</h2>
            <div class="controls">
              <select id="syncSaveSelect" aria-label="选择存档"><option value="">（选一个存档）</option></select>
              <button id="inspectSaveMods" type="button" disabled>检查</button>
              <button id="applySaveMods" class="primary" type="button" disabled>下载并应用</button>
            </div>
            <p class="note">存档自己记着它用了哪些 mod（<code>modIds / modSteamIds / modNames</code>，三张表按索引对应）。
              启动时按这份清单写 <code>ModsConfig.xml</code>，顺序按存档原样（Core 一定在，但不挪到首位），<code>knownExpansions</code> 不动。
              工坊条目<strong>匿名下载</strong>，不需要 Steam 账号；<code>modSteamId=0</code> 的必须手工放进 mods 目录——缺任何一个都不启动。</p>
            <div id="syncTable"></div>
            <p class="msg" id="syncResult"></p>
          </section>
        </div>

        <!-- ============ mods ============ -->
        <div class="view" id="view-mods" hidden>
          <h1>Mod</h1>
          <p class="lede">勾选的顺序就是 <code>activeMods</code> 的顺序，页面不替你排序。改完必须重启服务端并清
            <code>&lt;data&gt;/state</code>，否则下次启动会以 <code>Persisted room differs</code> 退出。</p>

          <section class="block">
            <h2>已安装</h2>
            <div class="controls">
              <input type="text" id="modSearch" placeholder="搜索 packageId 或名称" spellcheck="false" aria-label="搜索 mod">
              <button id="refreshMods" type="button">刷新</button>
              <button id="applyMods" class="primary" type="button" disabled>保存启用列表</button>
            </div>
            <p class="note" id="modNote"></p>
            <div class="modlist" id="modList"></div>
            <p class="msg" id="modResult"></p>
          </section>
        </div>

        <!-- ============ dlc ============ -->
        <div class="view" id="view-dlc" hidden>
          <h1>DLC</h1>
          <p class="lede">三件事是分开的：磁盘上有没有内容、def 有没有真的加载、授权是怎么来的。三者不一致时下面会点出来。</p>
          <section class="block">
            <div id="dlcGrid"></div>
            <p class="msg" id="dlcNote"></p>
            <p class="note" id="dlcHint">凝聚力 DLC 让服务端在没买 DLC 的账号下也能加载内容（服务端侧补丁，客户端不用装）。</p>
          </section>
        </div>

        <!-- ============ steam ============ -->
        <div class="view" id="view-steam" hidden>
          <h1>Steam</h1>
          <p class="lede">工坊 mod <strong>匿名下载，无需账号</strong>。只有下载游戏本体（<code>app_update 294100</code>）才需要真实账号。</p>

          <section class="block">
            <h2>游戏本体下载</h2>
            <div class="controls">
              <select id="versionSelect" aria-label="选择版本分支"></select>
              <button id="download" class="primary" type="button" disabled>下载 / 校验</button>
              <button id="progressRefresh" type="button">刷新进度</button>
            </div>
            <progress id="progress" value="0" max="100"></progress>
            <p class="note" id="progressNote"></p>
            <p class="note" id="downloadNote"></p>
            <p class="msg" id="downloadResult"></p>
          </section>

          <section class="block">
            <h2>代理 <span class="aside" id="proxyEffective">（未配置代理）</span></h2>
            <div class="controls">
              <input type="text" id="steamProxy" placeholder="http://host:port（留空 = 直连）"
                     spellcheck="false" autocomplete="off" aria-label="代理地址">
              <button id="proxySave" class="primary" type="button" disabled>保存代理</button>
              <button id="proxyClear" type="button" disabled>清空（直连）</button>
              <button id="proxyTest" type="button" disabled>测试可达性</button>
            </div>
            <p class="note">工坊下载走 steamcmd 的子进程，它不读系统代理，所以这里设的值会直接注入该子进程的
              <code>HTTP_PROXY / HTTPS_PROXY</code>。留空表示直连；保存后立即对下一次下载生效，不需要重启容器。</p>
            <p class="note mono" id="proxyNote"></p>
            <p class="note mono" id="searchRoute"></p>
            <p class="msg" id="proxyResult"></p>
          </section>

          <section class="block">
            <h2>账号 <span class="aside">仅用于上面那件事</span></h2>
            <div class="controls">
              <input type="text" id="steamUser" placeholder="Steam 账号" autocomplete="username" spellcheck="false" aria-label="Steam 账号">
              <input type="password" id="steamPass" placeholder="Steam 密码" autocomplete="current-password" aria-label="Steam 密码">
              <button id="steamLogin" type="button" disabled>登录</button>
              <button id="steamRefresh" type="button">刷新状态</button>
            </div>
            <!-- Hidden until steamcmd actually asks for a code: showing an empty box over "未登录" reads as if a code
                 were expected for every account, and most accounts never see this prompt. -->
            <div class="controls" id="steamCodeRow" hidden>
              <input type="text" id="steamCode" placeholder="Steam Guard 验证码（邮箱/手机 App 收到的 5 位字符）"
                     spellcheck="false" autocomplete="one-time-code" inputmode="text" aria-label="Steam Guard 验证码">
              <button id="steamCodeSend" class="primary" type="button">提交验证码</button>
            </div>
            <p class="note" id="steamNote">密码只通过 stdin 交给 steamcmd，不落日志、不回显。Steam Guard 需要验证码时这里会出输入框。</p>
            <p class="msg" id="steamResult"></p>
          </section>
        </div>

        <!-- ============ logs ============ -->
        <div class="view" id="view-logs" hidden>
          <h1>日志</h1>
          <p class="lede">最新的在最下面。筛选只影响显示，不会丢掉任何一行。</p>
          <section class="block">
            <div class="controls">
              <button id="logAll" class="primary" type="button">全部</button>
              <button id="logWarn" type="button">只看警告</button>
              <button id="logError" type="button">只看错误</button>
              <button id="logPause" type="button">暂停滚动</button>
              <button id="logClear" type="button">清屏</button>
            </div>
            <pre class="log" id="log">（等待状态）</pre>
          </section>
        </div>

        <!-- ============ danger ============ -->
        <div class="view" id="view-danger" hidden>
          <h1>关闭服务</h1>
          <p class="lede">这是唯一会把这个页面一起关掉的操作。只想让殖民地停下来，用「总览」里的<strong>停止世界</strong>。</p>
          <section class="block">
            <div class="controls">
              <button id="shutdown" class="danger" type="button" disabled>关闭服务进程</button>
            </div>
            <p class="note">会先检查点、再持久化，然后退出进程。容器会按重启策略把它拉起来，期间本页打不开。</p>
            <p class="msg" id="shutdownResult"></p>
          </section>
        </div>
      </main>
    </div>

    <script>
    "use strict";
    var KIB = 1024;
    var numberFmt = new Intl.NumberFormat("zh-CN");
    var lastStatus = null;
    var lastLog = [];
    var logFilter = "all";
    var logPaused = false;
    var modOrder = [];
    var modActive = {};
    var modData = null;
    var selectedSave = "";
    // Multi-select state for the save library. `pickedSaves` survives re-renders of the table only as long as the
    // names still exist - `renderSaveTable` clears it, which is correct: after a delete the selection is stale.
    var pickedSaves = new Set();
    var saveNames = [];

    function $(id) { return document.getElementById(id); }
    function txt(id, value) {
      var e = $(id);
      if (!e) return;
      var text = (value === null || value === undefined || value === "") ? "—" : String(value);
      e.textContent = text;
      // A reading that had to be ellipsized stays readable on hover; without this the truncation hides the value
      // with no way to recover it, which is worse than wrapping.
      e.title = text;
    }
    function num(n) {
      return (n === null || n === undefined || isNaN(n)) ? "—" : numberFmt.format(n);
    }
    function bytes(n) {
      if (n === null || n === undefined) return "—";
      if (n < KIB) return n + " B";
      if (n < KIB * KIB) return (n / KIB).toFixed(1) + " KiB";
      if (n < KIB * KIB * KIB) return (n / KIB / KIB).toFixed(2) + " MiB";
      return (n / KIB / KIB / KIB).toFixed(2) + " GiB";
    }
    function rate(n) { return bytes(Math.round(n || 0)) + "/s"; }
    function uptime(seconds) {
      if (seconds === null || seconds === undefined) return "";
      var s = Math.floor(seconds);
      var d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
      if (d) return "已运行 " + d + " 天 " + h + " 小时";
      if (h) return "已运行 " + h + " 小时 " + m + " 分";
      return "已运行 " + m + " 分 " + (s % 60) + " 秒";
    }
    function msg(id, text, kind) {
      var e = $(id);
      if (!e) return;
      e.textContent = text || "";
      e.className = "msg" + (kind ? " " + kind : "");
    }
    function banner(text, info) {
      var e = $("banner");
      if (!e) return;
      e.textContent = text || "";
      e.className = "banner" + (text ? " show" : "") + (info ? " info" : "");
    }

    // ---------- navigation ----------
    var VIEWS = ["overview", "saves", "mods", "dlc", "steam", "logs", "danger"];
    function showView(name) {
      if (VIEWS.indexOf(name) < 0) name = "overview";
      VIEWS.forEach(function (view) {
        var el = $("view-" + view);
        if (el) el.hidden = view !== name;
      });
      Array.prototype.forEach.call(document.querySelectorAll(".nav a[data-view]"), function (link) {
        if (link.getAttribute("data-view") === name) link.setAttribute("aria-current", "page");
        else link.removeAttribute("aria-current");
      });
      if (name === "mods" && !modData) loadMods();
      if (name === "steam") { loadVersions(); loadSteam(); }
    }
    function currentView() {
      var hash = (location.hash || "").replace("#", "");
      return VIEWS.indexOf(hash) >= 0 ? hash : "overview";
    }
    window.addEventListener("hashchange", function () { showView(currentView()); });

    // ---------- credentials ----------
    var token = "";
    try { token = window.localStorage.getItem("turbotick-admin-token") || ""; } catch (e) { token = ""; }

    async function api(path, options) {
      var init = options || {};
      init.credentials = "same-origin";
      init.headers = init.headers || {};
      if (init.body && !init.headers["Content-Type"]) init.headers["Content-Type"] = "application/json";
      if (init.method && init.method !== "GET") init.headers["X-TurboTick-Csrf"] = "1";
      if (token) init.headers["X-TurboTick-Token"] = token;
      var response = await fetch(path, init);
      var text = await response.text();
      var data;
      try { data = text ? JSON.parse(text) : {}; } catch (e) { data = { ok: false, code: "bad-json", detail: text.slice(0, 200) }; }
      // One place decides what a failure says. The server has emitted `message` since the first version while the
      // page read `detail`, so every error rendered as its machine code; both names are now normalised here instead
      // of at each of the dozen call sites.
      if (data && !data.detail && data.message) data.detail = data.message;
      if (!response.ok && data && data.code === "session-expired") banner("会话已失效，请重新登录。", false);
      return data;
    }
    async function post(path, body) {
      return api(path, { method: "POST", body: JSON.stringify(body || {}) });
    }

    // ---------- status ----------
    function paintLamp(state) {
      var lamp = $("lamp");
      if (!lamp) return;
      lamp.className = "lamp";
      if (state === "playing" || state === "publishing-initial-world") lamp.classList.add("live");
      else if (state === "failed" || state === "faulted") lamp.classList.add("dead");
      else if (state === "lobby" || state === "loading-initial-save" || state === "restoring"
        || state === "switching") lamp.classList.add("warn");
    }

    function render(s) {
      lastStatus = s;
      var control = s.control || {};
      var loggedIn = !!(control && control.authenticated);
      var state = s.state || "unknown";
      var labels = {
        "playing": "运行中", "lobby": "空载 · 等待载入", "loading-initial-save": "载入存档…",
        "publishing-initial-world": "发布初始世界…", "restoring": "恢复中", "stopping": "正在停止",
        "stopped": "已停止", "failed": "失败", "faulted": "故障", "switching": "换档中",
        "starting": "启动中", "unknown": "未知"
      };
      paintLamp(state);
      txt("stateText", labels[state] || state);
      txt("bind", control.port ? (control.bindAddress || "127.0.0.1") + ":" + control.port : "");
      txt("uptime", uptime(s.uptimeSeconds));
      var health = s.health || {};
      var compat = s.compatibility || {};
      var wire = s.wire || {};
      txt("state", labels[state] || state);
      txt("failure", s.failure || "");
      txt("save", s.save || "（未命名世界）");
      txt("gameTick", s.gameTick === null || s.gameTick === undefined ? "" : "游戏内 tick " + num(s.gameTick));
      txt("tick", num(s.tick));
      txt("checkpoint", health.checkpointTick ? "检查点 " + num(health.checkpointTick) : "");
      txt("replicas", s.replicas === null || s.replicas === undefined ? "—" : num(s.replicas) + " / " + num(s.players || 0));
      txt("players", s.joinedPeers ? num(s.joinedPeers) + " 个会话在线" : "");
      txt("tps", s.tps === null || s.tps === undefined ? "—" : s.tps.toFixed(1) + " tps");
      txt("fps", s.fps === null || s.fps === undefined ? "无界面不限帧" : s.fps.toFixed(0) + " fps（无界面不限帧）");
      txt("up", bytes(wire.outBytes) + " 上行");
      txt("upRate", rate(wire.outBytesPerSecond));
      var compatible = compat.gameVersionMatches !== false && compat.modHashMatches !== false;
      txt("comp", compatible ? "一致" : "不一致");
      txt("health", health.summary || health.phase || "");
      // In `lobby` there is no simulation, so `targetTps` really is 0. Saying "—" would read as "unknown";
      // saying 0 reads as "broken". The honest third option is to name the reason.
      var simulating = s.state === "playing" || s.state === "restoring";
      txt("targetTps", s.targetTps ? s.targetTps.toFixed(1) + " tps" : (simulating ? "0.0 tps" : "未开始模拟"));
      txt("tickBudget", (simulating && s.targetTps) ? (s.tps / s.targetTps * 100).toFixed(0) + "% 预算已用" : "");

      var canWrite = !!(control.writeEnabled && loggedIn);
      ["save2", "reload", "shutdown", "serverStart", "serverStop", "uploadSave", "applyMods",
        "steamLogin", "download", "deleteSave", "inspectSaveMods", "applySaveMods",
        "proxySave", "proxyClear", "proxyTest"].forEach(function (id) {
          var el = $(id);
          if (!el) return;
          if (id === "shutdown") el.disabled = !loggedIn;
          else el.disabled = !canWrite;
        });
      // The start button additionally needs a target save, and the apply button a diff.
      if ($("serverStart")) $("serverStart").disabled = !canWrite || !$("saveSelect").value;
      if ($("applyMods")) $("applyMods").disabled = !canWrite || !modData;
      // Delete acts on the multi-select first and on the highlighted row second, so its enabled state is derived
      // from that set rather than from `selectedSave` alone.
      paintSaveToolbar();

      if (s.lastSwitch && s.lastSwitch.detail) {
        msg("switchNote", (s.lastSwitch.ok ? "" : "失败：") + s.lastSwitch.detail,
          s.lastSwitch.ok ? "" : "err");
      }

      if (s.failure) banner("服务端报错：" + s.failure, false);
      else if (s.preflight && s.preflight.ok === false) banner("preflight 未通过：" + (s.preflight.detail || s.preflight.reason || ""), false);
      else banner("");

      renderProcess(s.process, control);
      lastLog = s.log || [];
      renderLogs();
      if (s.dlc) renderDlc(s.dlc);
      renderDialogs(s.dialogs || []);
    }

    // ---------- dialogs waiting on a human ----------
    // A mod opens a dialog and waits: that is the mod's state machine, not a bug. In a headless process nobody can
    // click it, so before this block existed the guard cleared the pause flag and the world ran on with a question
    // hanging forever - the mod's flow stuck and nothing anywhere saying why. The world is *supposed* to stop here.
    var DIALOG_CHOICES = [
      { key: "A", label: "buttonA" },
      { key: "B", label: "buttonB" },
      { key: "C", label: "buttonC" }
    ];
    function renderDialogs(dialogs) {
      var block = $("dialogBlock");
      var list = $("dialogList");
      if (!block || !list) return;
      block.hidden = dialogs.length === 0;
      list.textContent = "";
      dialogs.forEach(function (dialog) {
        var item = document.createElement("div");
        item.className = "dialog" + (dialog.answer ? " done" : "");

        if (dialog.title) {
          var title = document.createElement("div");
          title.className = "dtitle";
          title.textContent = dialog.title;
          item.appendChild(title);
        }

        var type = document.createElement("div");
        type.className = "dtype";
        type.textContent = dialog.type + (dialog.repeats > 1 ? "  · 第 " + dialog.repeats + " 次弹出" : "");
        item.appendChild(type);

        if (dialog.text) {
          var text = document.createElement("div");
          text.className = "dtext";
          text.textContent = dialog.text;
          item.appendChild(text);
          // Long mod letters are the normal case; clip rather than let one dialog push every other control off
          // the page, and offer the full text instead of truncating it silently.
          if (dialog.text.length > 400) {
            text.classList.add("clip");
            var more = document.createElement("button");
            more.type = "button";
            more.className = "dmore";
            more.textContent = "展开全文";
            more.addEventListener("click", function () {
              var clipped = text.classList.toggle("clip");
              more.textContent = clipped ? "展开全文" : "收起";
            });
            item.appendChild(more);
          }
        }

        if (dialog.answer || dialog.autoClosed) {
          var state = document.createElement("div");
          state.className = "dmeta";
          state.textContent = dialog.autoClosed
            ? "只有「确定」，已自动关掉（记在日志里）"
            : "已选择 " + dialog.answer + (dialog.answer === "A" ? "（" + (dialog.buttonA || "确定") + "）" : "");
          item.appendChild(state);
        } else {
          var choices = document.createElement("div");
          choices.className = "dchoices";
          var writable = !!(lastStatus && lastStatus.control && lastStatus.control.writeEnabled
            && lastStatus.control.authenticated);
          DIALOG_CHOICES.forEach(function (choice) {
            var label = dialog[choice.label];
            if (!label) return;
            var button = document.createElement("button");
            button.type = "button";
            button.textContent = label;
            // `buttonADestructive` is the mod's own warning that this choice is the irreversible one.
            if (dialog.destructive && choice.key === "A") button.className = "destructive";
            button.disabled = !writable;
            button.addEventListener("click", function () { answerDialog(dialog.id, choice.key, button); });
            choices.appendChild(button);
          });
          item.appendChild(choices);
        }

        list.appendChild(item);
      });
    }

    async function answerDialog(id, choice, button) {
      var buttons = button.parentNode.querySelectorAll("button");
      Array.prototype.forEach.call(buttons, function (each) { each.disabled = true; });
      msg("dialogResult", "正在把选择交给游戏进程…", "");
      try {
        var data = await post("/api/dialogs/answer", { id: id, choice: choice });
        if (data && data.ok) {
          msg("dialogResult", "已选择 " + choice + "：" + (data.detail || "游戏进程已执行该按钮的回调"), "");
        } else {
          msg("dialogResult", "没能选择：" + ((data && (data.detail || data.message)) || "未知原因"), "err");
          Array.prototype.forEach.call(buttons, function (each) { each.disabled = false; });
        }
      } catch (error) {
        msg("dialogResult", "请求失败：" + error.message, "err");
        Array.prototype.forEach.call(buttons, function (each) { each.disabled = false; });
      }
      // The dialog closes on the game's next frame, and the status file is written by that process: give it a beat
      // before asking again, otherwise the page redraws the same question and looks like the click was ignored.
      setTimeout(poll, 400);
    }

    // ---------- game process ----------
    // `process` is the portal's own knowledge of its child: it is the one fact this page can report that the game
    // process could never report about itself (an exit code, and the absence of a process).
    var PROCESS_LABELS = {
      "running": "运行中", "starting": "正在启动", "restarting": "正在重启", "stopping": "正在停止",
      "stopped": "已停止", "failed": "启动失败", "unsupervised": "无 supervisor", "unknown": "未知"
    };
    function renderProcess(process, control) {
      if (!process) return;
      var state = process.state || "unknown";
      txt("procState", PROCESS_LABELS[state] || state);
      txt("procPid", process.pid ? String(process.pid) : "");
      txt("procUptime", process.running ? uptime(process.uptimeSeconds) : "");
      if (process.lastExitCode === null || process.lastExitCode === undefined) {
        // A process that has never exited has no exit code - but a blank cell reads as "the page failed to fill
        // this in", so it keeps the em dash the markup ships with.
        txt("procExit", "—");
      } else {
        txt("procExit", "exit " + process.lastExitCode + (process.lastExitDetail ? " · " + process.lastExitDetail : ""));
      }
      var lamp = $("procLamp");
      if (lamp) {
        lamp.className = "lamp";
        if (state === "running") lamp.classList.add("live");
        else if (state === "failed" || state === "stopped") lamp.classList.add("dead");
        else if (state === "restarting" || state === "starting" || state === "stopping") lamp.classList.add("warn");
      }
      var supervised = state !== "unsupervised";
      // `control` is passed in rather than read off `lastStatus`: being a parameter makes it impossible for this
      // function to see a partially-updated snapshot, and the buttons it gates are the destructive ones.
      var canWrite = !!(control && control.writeEnabled && control.authenticated);
      var running = !!process.running;
      if ($("gameStart")) $("gameStart").disabled = !canWrite || !supervised || running;
      if ($("gameRestart")) $("gameRestart").disabled = !canWrite || !supervised || !running;
      if ($("gameStop")) $("gameStop").disabled = !canWrite || !supervised || !running;
      // The detail is where a failed start explains itself, so it is styled as an error rather than as the neutral
      // footnote the same element is when the process is healthy.
      var note = $("procNote");
      if (note) {
        var detail = process.detail || "";
        note.textContent = detail || "—";
        note.className = "note" + (state === "failed" ? " err" : "");
      }
    }

    // ---------- logs ----------
    // `AdminLogLine` carries the game's own level (`Type`) and a repeat count, not a timestamp: the log reader in
    // the runtime copies text only, so inventing a clock here would be a lie the operator could act on.
    function logLine(line) {
      var repeat = line.repeats ? "  ×" + line.repeats : "";
      return (line.text || "") + repeat;
    }
    function isWarn(line) { return /warn/i.test(line.type || ""); }
    function isError(line) { return /error|exception|fatal/i.test(line.type || ""); }
    function renderLogs() {
      var rows = lastLog.filter(function (line) {
        if (logFilter === "warn") return isWarn(line) || isError(line);
        if (logFilter === "error") return isError(line);
        return true;
      });
      var text = rows.length ? rows.map(logLine).join("\n") : "（还没有日志）";
      ["log", "logTail"].forEach(function (id) {
        var el = $(id);
        if (!el) return;
        // textContent only: a log line is attacker-shaped text and must never become markup here.
        el.textContent = text;
        if (!logPaused) el.scrollTop = el.scrollHeight;
      });
    }

    // ---------- saves ----------
    async function loadSaves() {
      var data = await api("/api/saves");
      var saves = data.saves || [];
      txt("navSaves", saves.length ? String(saves.length) : "0");
      var select = $("saveSelect");
      var sync = $("syncSaveSelect");
      var keep = select.value;
      var keepSync = sync.value;
      select.textContent = "";
      sync.textContent = "";
      if (!saves.length) {
        var opt = document.createElement("option");
        opt.value = "";
        opt.textContent = "还没有存档，先上传一个";
        select.appendChild(opt);
        var opt2 = opt.cloneNode(true);
        sync.appendChild(opt2);
      }
      saves.forEach(function (save) {
        var o = document.createElement("option");
        o.value = save.name;
        o.textContent = save.name;
        select.appendChild(o);
        sync.appendChild(o.cloneNode(true));
      });
      if (keep) select.value = keep;
      if (keepSync) sync.value = keepSync;
      renderSaveTable(saves);
      txt("savesNote", saves.length
        ? "共 " + saves.length + " 个存档，都存在服务端的 /data/saves。"
        : "还没有存档。先在客户端保存一局，再把 .rws 拖到上面。");
      if ($("serverStart")) $("serverStart").disabled = !(saves.length && lastStatus && lastStatus.control && lastStatus.control.authenticated);
    }

    function renderSaveTable(saves) {
      var host = $("saveTable");
      if (!host) return;
      host.textContent = "";
      saveNames = saves.map(function (save) { return save.name; });
      pickedSaves.clear();
      if (!saves.length) {
        host.innerHTML = '<div class="empty"><b>还没有存档</b><br>在客户端里 Esc → 保存，再把生成的 .rws 拖到「上传」。</div>';
        paintSaveToolbar();
        return;
      }
      var table = document.createElement("table");
      table.className = "data";
      // The header is its own <thead> so `position: sticky` works; a bare <tr> in the table body scrolls away.
      var headGroup = document.createElement("thead");
      var head = document.createElement("tr");
      var pickHead = document.createElement("th");
      pickHead.className = "pick";
      var box = document.createElement("input");
      box.type = "checkbox";
      box.id = "savePickAll";
      box.title = "全选";
      box.addEventListener("change", function () { pickAll(box.checked); });
      pickHead.appendChild(box);
      head.appendChild(pickHead);
      ["存档", "大小", "修改时间", "sha256"].forEach(function (label) {
        var th = document.createElement("th");
        th.textContent = label;
        head.appendChild(th);
      });
      headGroup.appendChild(head);
      table.appendChild(headGroup);
      var body = document.createElement("tbody");
      saves.forEach(function (save) {
        var row = document.createElement("tr");
        row.style.cursor = "pointer";
        row.addEventListener("click", function (event) {
          // Clicking the checkbox is the checkbox's business; clicking anywhere else in the row picks that one row
          // for the start/delete actions that predate multi-select.
          if (event.target && event.target.type === "checkbox") return;
          selectedSave = save.name;
          paintSaveToolbar();
          Array.prototype.forEach.call(body.querySelectorAll("tr"), function (other) { other.style.outline = ""; });
          row.style.outline = "1px solid var(--accent)";
          if ($("syncSaveSelect")) $("syncSaveSelect").value = save.name;
          msg("saveResult", "已选中 " + save.name);
        });
        var pick = document.createElement("td");
        pick.className = "pick";
        var check = document.createElement("input");
        check.type = "checkbox";
        check.value = save.name;
        check.addEventListener("change", function () {
          if (check.checked) pickedSaves.add(save.name); else pickedSaves.delete(save.name);
          row.classList.toggle("picked", check.checked);
          paintSaveToolbar();
        });
        pick.appendChild(check);
        row.appendChild(pick);
        var name = document.createElement("td");
        name.className = "mono";
        name.textContent = save.name;
        row.appendChild(name);
        var size = document.createElement("td");
        size.className = "num";
        size.textContent = bytes(save.bytes);
        row.appendChild(size);
        var when = document.createElement("td");
        when.className = "mono";
        when.textContent = save.modifiedUtc || save.modified || "";
        row.appendChild(when);
        var hash = document.createElement("td");
        hash.className = "mono";
        hash.textContent = save.sha256 ? save.sha256.slice(0, 16) : "—";
        row.appendChild(hash);
        body.appendChild(row);
      });
      table.appendChild(body);
      host.appendChild(table);
      paintSaveToolbar();
    }

    function pickAll(checked) {
      var host = $("saveTable");
      if (!host) return;
      Array.prototype.forEach.call(host.querySelectorAll("tbody input[type=checkbox]"), function (box) {
        box.checked = checked;
        if (checked) pickedSaves.add(box.value); else pickedSaves.delete(box.value);
        var row = box.closest("tr");
        if (row) row.classList.toggle("picked", checked);
      });
      paintSaveToolbar();
    }

    /// The delete button acts on the multi-select when there is one, and on the single highlighted row otherwise, so
    /// the old "click a row, then delete" gesture still works for a one-off.
    function targetsForDelete() {
      if (pickedSaves.size) return Array.from(pickedSaves);
      return selectedSave ? [selectedSave] : [];
    }

    function paintSaveToolbar() {
      var targets = targetsForDelete();
      var writable = !!(lastStatus && lastStatus.control && lastStatus.control.authenticated);
      if ($("deleteSave")) $("deleteSave").disabled = !writable || targets.length === 0;
      if ($("saveSelectedNote")) {
        txt("saveSelectedNote", targets.length ? "已选中 " + targets.length + " 个" : "未选中");
      }
      var all = $("savePickAll");
      if (all) all.checked = saveNames.length > 0 && pickedSaves.size === saveNames.length;
      if ($("saveSelectAll")) $("saveSelectAll").checked = all ? all.checked : false;
    }

    function uploadSave() {
      var input = $("saveFile");
      if (!input.files || !input.files.length) { msg("uploadResult", "先选一个 .rws 文件", "err"); return; }
      var file = input.files[0];
      var request = new XMLHttpRequest();
      request.open("POST", "/api/saves/upload", true);
      request.setRequestHeader("Content-Type", "application/octet-stream");
      request.setRequestHeader("X-TurboTick-Csrf", "1");
      request.setRequestHeader("X-TurboTick-Filename", file.name);
      if (token) request.setRequestHeader("X-TurboTick-Token", token);
      msg("uploadResult", "上传中 " + bytes(file.size) + "…");
      request.upload.onprogress = function (event) {
        if (!event.lengthComputable) return;
        var pct = Math.round(event.loaded / event.total * 100);
        $("uploadProgress").value = pct;
        txt("uploadNote", "已上传 " + bytes(event.loaded) + " / " + bytes(event.total) + "（" + pct + "%）");
      };
      request.onload = async function () {
        var data;
        try { data = JSON.parse(request.responseText || "{}"); } catch (e) { data = { ok: false, detail: "响应不是 JSON" }; }
        if (request.status >= 200 && request.status < 300 && data.ok) {
          msg("uploadResult", "已上传为 " + data.name + "（sha256 " + String(data.sha256 || "").slice(0, 16) + "）", "good");
          txt("uploadNote", "上传完成。下面会显示它需要哪些 mod。");
          await loadSaves();
          if (data.name) {
            $("syncSaveSelect").value = data.name;
            inspectSaveMods();
          }
        } else {
          msg("uploadResult", "上传失败：" + (data.detail || data.code || ("HTTP " + request.status)), "err");
        }
      };
      request.onerror = function () { msg("uploadResult", "上传失败：网络错误", "err"); };
      request.send(file);
    }

    async function deleteSave() {
      var targets = targetsForDelete();
      if (!targets.length) { msg("saveResult", "先勾选要删的存档，或点一行选中它", "err"); return; }
      var question = targets.length === 1
        ? "删除存档「" + targets[0] + "」？\n\n权威端只有这一份，删了就没了。"
        : "删除这 " + targets.length + " 个存档？\n\n" + targets.join("\n") + "\n\n权威端只有这一份，删了就没了。";
      if (!confirm(question)) return;
      // One request for the whole selection: a hundred round trips with a per-row message is the shape that made
      // cleaning out old imports skip names halfway through.
      var data = await post("/api/saves/delete", { names: targets });
      if (data.ok) {
        msg("saveResult", "已删除 " + (data.deleted || []).length + " 个存档", "good");
      } else {
        var refused = (data.refused || []).map(function (entry) { return entry.name + "（" + entry.detail + "）"; });
        msg("saveResult", "已删除 " + ((data.deleted || []).length)
          + " 个；被拒绝 " + refused.length + " 个：" + refused.join("；"), refused.length ? "err" : "good");
      }
      selectedSave = "";
      await loadSaves();
    }

    // ---------- dlc ----------
    function renderDlc(dlc) {
      var host = $("dlcGrid");
      if (!host) return;
      var expansions = dlc.expansions || [];
      txt("navDlc", expansions.length ? String(expansions.length) : "");
      host.textContent = "";
      if (!expansions.length) {
        host.innerHTML = '<div class="empty"><b>还没有 DLC 信息</b><br>服务端启动后这里会列出每个 DLC 的三态。</div>';
        return;
      }
      var table = document.createElement("table");
      table.className = "data";
      var head = document.createElement("tr");
      ["DLC", "磁盘", "def 已加载", "授权", "说明"].forEach(function (label) {
        var th = document.createElement("th");
        th.textContent = label;
        head.appendChild(th);
      });
      table.appendChild(head);
      expansions.forEach(function (item) {
        var row = document.createElement("tr");
        var name = document.createElement("td");
        name.textContent = item.name || item.packageId || "?";
        row.appendChild(name);
        var onDisk = document.createElement("td");
        onDisk.innerHTML = '<span class="tag ' + (item.onDisk ? "ok" : "bad") + '">' + (item.onDisk ? "有" : "无") + '</span>';
        row.appendChild(onDisk);
        var loaded = document.createElement("td");
        loaded.innerHTML = '<span class="tag ' + (item.loaded ? "ok" : "bad") + '">' + (item.loaded ? "已加载" : "未加载") + '</span>';
        row.appendChild(loaded);
        var lic = document.createElement("td");
        var kind = item.status === "Active" ? "ok" : (item.status === "NotInstalled" ? "bad" : "warn");
        lic.innerHTML = '<span class="tag ' + kind + '">' + (item.status || "?") + '</span>';
        row.appendChild(lic);
        var said = document.createElement("td");
        said.className = "mono";
        said.textContent = item.note || (item.onDisk && !item.loaded ? "磁盘有内容但 def 没加载" : "");
        row.appendChild(said);
        table.appendChild(row);
      });
      host.appendChild(table);
      txt("dlcNote", dlc.summary || "");
    }

    // ---------- save mods ----------
    function renderSyncTable(report) {
      var host = $("syncTable");
      if (!host) return;
      host.textContent = "";
      if (!report) return;
      var required = report.required || [];
      if (!required.length) {
        host.innerHTML = '<div class="empty">这份存档没有声明任何 mod。</div>';
        return;
      }
      var table = document.createElement("table");
      table.className = "data";
      var head = document.createElement("tr");
      ["packageId", "名称", "工坊 ID", "状态"].forEach(function (label) {
        var th = document.createElement("th");
        th.textContent = label;
        head.appendChild(th);
      });
      table.appendChild(head);
      required.forEach(function (entry) {
        var row = document.createElement("tr");
        var id = document.createElement("td");
        id.className = "mono";
        id.textContent = entry.id;
        row.appendChild(id);
        var name = document.createElement("td");
        name.textContent = entry.name || "";
        row.appendChild(name);
        var ws = document.createElement("td");
        ws.className = "num";
        ws.textContent = entry.workshopId ? String(entry.workshopId) : "—";
        row.appendChild(ws);
        var state = document.createElement("td");
        // Self is the mod that *is* the server: green, and SelfMissing is the deployment being broken, not a
        // download to retry - red, with the refusal text below naming it.
        var kind = entry.disposition === "Present" || entry.disposition === "Self" ? "ok"
          : entry.disposition === "Downloadable" ? "warn"
          : entry.disposition === "Manual" ? "bad"
          : entry.disposition === "SelfMissing" ? "bad" : "";
        var tag = document.createElement("span");
        tag.className = "tag " + kind;
        tag.textContent = entry.disposition;
        state.appendChild(tag);
        row.appendChild(state);
        table.appendChild(row);
      });
      host.appendChild(table);
      if (report.refusal) msg("syncResult", report.refusal, "err");
      else msg("syncResult", "存档需要的 mod 都在。", "good");
    }

    async function inspectSaveMods() {
      var name = $("syncSaveSelect").value;
      if (!name) { msg("syncResult", "先选一个存档", "err"); return; }
      msg("syncResult", "读取 " + name + " 的 mod 清单…");
      var data = await api("/api/saves/mods?save=" + encodeURIComponent(name));
      if (data.report) renderSyncTable(data.report);
      else msg("syncResult", "读取失败：" + (data.detail || data.code), "err");
    }

    async function applySaveMods() {
      var name = $("syncSaveSelect").value;
      if (!name) { msg("syncResult", "先选一个存档", "err"); return; }
      msg("syncResult", "正在按存档写 mod 配置…");
      var data = await post("/api/saves/mods/apply", { save: name });
      if (data.ok) {
        msg("syncResult", (data.detail || "已应用") + (data.report && data.report.written ? "（已写盘）" : ""), "good");
        if (data.report) renderSyncTable(data.report);
      } else {
        msg("syncResult", "失败：" + (data.detail || data.code), "err");
        if (data.report) renderSyncTable(data.report);
      }
    }

    // ---------- mods ----------
    async function loadMods() {
      var data = await api("/api/mods");
      modData = data;
      // The payload names the enabled list `active` and also tags every mod with its own `active` flag; reading
      // only one of the two is how the list rendered with nothing ticked while 8 mods were enabled.
      modOrder = (data.active || data.activeMods || []).slice();
      modActive = {};
      (data.mods || []).forEach(function (mod) { if (mod.active) modActive[mod.packageId] = true; });
      modOrder.forEach(function (id) { modActive[id] = true; });
      txt("navMods", data.mods ? String(data.mods.length) : "");
      var enabled = modOrder.length || ((data.mods || []).filter(function (mod) { return mod.active; }).length);
      txt("modNote", (data.mods || []).length + " 个已安装，其中 " + enabled + " 个启用。"
        + (data.warning ? "  " + data.warning : ""));
      renderMods();
    }

    function renderMods() {
      var host = $("modList");
      if (!host) return;
      host.textContent = "";
      var all = (modData && modData.mods) || [];
      if (!all.length) {
        host.innerHTML = '<div class="empty"><b>没有已安装的 mod</b><br>把 mod 目录放进服务端的 mods 目录后点刷新。</div>';
        return;
      }
      var needle = ($("modSearch").value || "").toLowerCase();
      var rows = all.filter(function (mod) {
        if (!needle) return true;
        return (mod.packageId || "").toLowerCase().indexOf(needle) >= 0
          || (mod.name || "").toLowerCase().indexOf(needle) >= 0;
      });
      rows.sort(function (a, b) {
        var ai = modOrder.indexOf(a.packageId), bi = modOrder.indexOf(b.packageId);
        if (ai >= 0 && bi >= 0) return ai - bi;
        if (ai >= 0) return -1;
        if (bi >= 0) return 1;
        return (a.packageId || "").localeCompare(b.packageId || "");
      });
      rows.forEach(function (mod) {
        var row = document.createElement("div");
        row.className = "modrow" + (modActive[mod.packageId] ? " on" : "");
        var box = document.createElement("input");
        box.type = "checkbox";
        box.checked = !!modActive[mod.packageId];
        box.setAttribute("aria-label", "启用 " + (mod.name || mod.packageId));
        box.addEventListener("change", function () {
          if (box.checked) {
            modActive[mod.packageId] = true;
            modOrder.push(mod.packageId);
          } else {
            delete modActive[mod.packageId];
            modOrder = modOrder.filter(function (id) { return id !== mod.packageId; });
          }
          row.classList.toggle("on", box.checked);
        });
        row.appendChild(box);
        var label = document.createElement("div");
        var pid = document.createElement("div");
        pid.className = "pid";
        pid.textContent = mod.packageId;
        label.appendChild(pid);
        var pname = document.createElement("div");
        pname.className = "pname";
        pname.textContent = mod.name || "";
        label.appendChild(pname);
        row.appendChild(label);
        var meta = document.createElement("div");
        meta.className = "pname";
        meta.textContent = mod.source || "";
        row.appendChild(meta);
        host.appendChild(row);
      });
      if (!rows.length) host.innerHTML = '<div class="empty">没有匹配「' + needle + '」的 mod。</div>';
    }

    async function applyMods() {
      var before = ((modData && modData.activeMods) || []).slice();
      var added = modOrder.filter(function (id) { return before.indexOf(id) < 0; });
      var removed = before.filter(function (id) { return modOrder.indexOf(id) < 0; });
      if (!added.length && !removed.length && modOrder.join() === before.join()) {
        msg("modResult", "启用列表没有变化", "warn");
        return;
      }
      if (!confirm("写 ModsConfig.xml？\n\n新增 " + added.length + " 个，移除 " + removed.length + " 个。\n"
        + "改完必须重启服务端并清 state，否则下次启动会拒绝。")) return;
      var data = await post("/api/mods/apply", { activeMods: modOrder });
      msg("modResult", data.ok ? (data.detail || "已写入 ModsConfig.xml") : "失败：" + (data.detail || data.code),
        data.ok ? "good" : "err");
      if (data.ok) await loadMods();
    }

    // ---------- steam ----------
    async function loadVersions() {
      var data = await api("/api/versions");
      var select = $("versionSelect");
      if (!select || !data.versions) return;
      select.textContent = "";
      data.versions.forEach(function (version) {
        var option = document.createElement("option");
        option.value = version.branch || version.beta || version.id || version.version || "";
        option.textContent = version.label || version.branch || version.version || option.value;
        select.appendChild(option);
      });
      txt("downloadNote", "steamcmd +app_update 294100 -beta <分支> validate。分支取自上面的版本表。");
    }

    async function loadSteam() {
      var data = await api("/api/steam/status");
      var loggedIn = !!(data.login && data.login.loggedIn);
      txt("steamNote", loggedIn
        ? "已登录：" + (data.account || "?") + "。账号只用于下载游戏本体；工坊 mod 一直是匿名下载。"
        : "未登录。工坊 mod 匿名下载，不需要账号；只有下载游戏本体才要登录。");
      txt("steamResult", data.detail || "");
      // The proxy is part of the status rather than a separate fetch: "download failed" and "no proxy on a host
      // without egress" are one question, so both answers arrive together.
      paintProxy(data);
      paintSteamSession(data);
    }

    /// Renders the interactive login state: the phase, the code box, and the cleaned steamcmd transcript.
    /// `needsCode` decides whether the box appears — it is false for the mobile-authenticator variant, where the
    /// operator approves in the app and there is nothing to type. Both variants are phase `Code`, so the message is
    /// what tells them apart; showing an input box for the approval one would be a dead end.
    function paintSteamSession(data) {
      var session = (data && data.session) || {};
      var needs = !!(data && data.needsCode);
      var row = $("steamCodeRow");
      if (row) row.hidden = !needs;
      var phase = session.phase || "Idle";
      if (phase === "LoggedIn") msg("steamResult", "已登录：" + (session.account || data.account || "?"), "good");
      else if (phase === "Failed") msg("steamResult", "失败：" + (session.message || ""), "err");
      else if (phase === "Code") {
        msg("steamResult", needs
          ? (session.message || "需要 Steam Guard 验证码") + " —— 收到后填在下方并提交"
          : (session.message || "请在手机 Steam App 上批准这次登录"), "warn");
        if (needs && $("steamCode")) $("steamCode").focus();
      } else if (session.message && phase !== "Idle") txt("steamNote", session.message);
    }

    /// Polls the status while a login is in flight. 1.5 s because the Steam Guard prompt is the one thing this page
    /// must show promptly: a code expires, and a stale page is what makes it look like the login hangs. The ceiling
    /// is the server's own CodeTimeout (10 minutes) so an approval on the phone is never cut short by the page.
    var steamWatch = null;
    function watchSteamLogin() {
      if (steamWatch) clearInterval(steamWatch);
      var ticks = 0;
      steamWatch = setInterval(async function () {
        ticks++;
        var data = await api("/api/steam/status");
        paintSteamSession(data);
        var session = (data && data.session) || {};
        var done = session.phase === "LoggedIn" || session.phase === "Failed";
        if (done || ticks > 400) {
          clearInterval(steamWatch);
          steamWatch = null;
          loadSteam();
        }
      }, 1500);
    }

    function paintProxy(data) {
      var value = (data && data.proxy) || "";
      if ($("steamProxy") && document.activeElement !== $("steamProxy")) $("steamProxy").value = value;
      txt("proxyEffective", value ? "生效：" + value : "生效：直连（未使用代理）");
      // A proxy set in the deployment config shows here too; the field is still the operator's to change.
      txt("proxyNote", (data && data.proxyEffective) || "");
      // The workshop search is a different client from steamcmd; showing its route separately is the only way to
      // tell "the proxy is set but the search did not use it" from "the proxy is down".
      if ($("searchRoute")) txt("searchRoute", "工坊按名搜索：" + ((data && data.searchRoute) || "尚未发起"));
    }

    async function saveProxy(value) {
      var data = await post("/api/steam/proxy", { proxy: value });
      msg("proxyResult", data.ok ? (data.detail || "已保存") : "保存失败：" + (data.detail || data.code),
        data.ok ? "good" : "err");
      if (data.ok) paintProxy(data);
    }

    /// One real GET to steamcommunity through the same client the workshop search uses. The point is to find out
    /// here rather than when a save import fails — "the proxy is set" is not the same claim as "it works".
    async function probeWorkshop() {
      msg("proxyResult", "正在测试 steamcommunity 可达性…");
      var data = await api("/api/steam/probe");
      msg("proxyResult", (data.ok ? "可达：" : "不可达：") + (data.detail || data.code)
        + "  ·  路由 " + (data.route || "?"), data.ok ? "good" : "err");
    }

    // ---------- server control ----------
    async function startServer() {
      var name = $("saveSelect").value;
      if (!name) { msg("serverResult", "先选一个存档", "err"); return; }
      var switching = lastStatus && lastStatus.state === "playing" && lastStatus.save && lastStatus.save !== name;
      msg("serverResult", (switching ? "切换中：" : "载入中：") + name
        + (switching ? "（会先给当前世界做检查点）" : ""));
      var data = await post("/api/server/start", { save: name });
      msg("serverResult", (data.ok ? "" : "失败：") + (data.detail || data.code), data.ok ? "good" : "err");
      poll();
    }

    async function stopServer() {
      if (!confirm("停止当前世界？\n\n会先做检查点再停模拟，服务端回到 lobby。\n管理页会一直可用，可以随时再载入。")) return;
      msg("serverResult", "正在停止（检查点 → 停模拟）…");
      var data = await post("/api/server/stop", {});
      msg("serverResult", (data.ok ? "" : "失败：") + (data.detail || data.code), data.ok ? "good" : "err");
      poll();
    }

    // One handler for the three process controls: they differ only in the route and in what the operator is told
    // before it happens. Restart and stop both checkpoint first (server side), so none of them silently discards work.
    async function gamePower(path, verb, body) {
      if (verb === "停止" && !confirm("停止游戏进程？\n\n会先做检查点。本页保持在线，可以随时再启动它。\n只想让殖民地停下来而不关进程，请用上面的「停止世界」。")) return;
      if (verb === "重启" && !confirm("重启游戏进程？\n\n会先做检查点，然后换一个新进程。本页不受影响，登录状态不变。")) return;
      msg("procResult", "正在" + verb + "…");
      var data = await post(path, body || {});
      msg("procResult", (data.ok ? "" : "失败：") + (data.detail || data.code), data.ok ? "good" : "err");
      setTimeout(poll, 400);
    }

    async function shutdownService() {
      if (!confirm("关闭服务进程？\n\n管理页会立刻不可用，要等容器重启策略拉起（几十秒）。\n只想让殖民地停下来，请用「总览 → 停止世界」。")) return;
      if (!confirm("再确认一次：连同管理页一起关掉？")) return;
      msg("shutdownResult", "正在关闭…");
      try {
        var data = await post("/api/server/shutdown", {});
        msg("shutdownResult", "已请求关闭：" + (data.detail || data.code));
      } catch (error) {
        msg("shutdownResult", "请求可能已断开（进程退出中）：" + error.message, "warn");
      }
    }

    async function run(command) {
      var data = await post("/api/command", { name: command });
      msg("commandResult", (data.ok ? "" : "失败：") + (data.detail || data.code), data.ok ? "good" : "err");
      poll();
    }

    // ---------- polling ----------
    var polling = false;
    async function poll() {
      if (polling) return;
      polling = true;
      try {
        var data = await api("/api/status");
        if (data && data.state) render(data);
        else if (data && data.code) banner("读取状态失败：" + (data.detail || data.code), false);
      } catch (error) {
        banner("连不上服务端（" + error.message + "）。页面会自动重试。", false);
      } finally {
        polling = false;
      }
    }

    // ---------- wiring ----------
    function wire() {
      Array.prototype.forEach.call(document.querySelectorAll(".nav a[data-view]"), function (link) {
        link.addEventListener("click", function (event) {
          event.preventDefault();
          var name = link.getAttribute("data-view");
          if (location.hash !== "#" + name) location.hash = name;
          showView(name);
        });
      });
      $("logout").addEventListener("click", async function () {
        // `/logout` is its own route (it clears the cookie server-side); it is not a queued game command.
        try { await api("/logout", { method: "POST" }); } catch (error) { /* the redirect below is what matters */ }
        location.href = "/login";
      });

      $("serverStart").addEventListener("click", startServer);
      $("serverStop").addEventListener("click", stopServer);
      $("refreshSaves").addEventListener("click", loadSaves);
      $("saveSelect").addEventListener("change", function () {
        if ($("serverStart")) {
          $("serverStart").disabled = !(lastStatus && lastStatus.control && lastStatus.control.authenticated);
        }
      });
      $("save2").addEventListener("click", function () { run("save"); });
      $("reload").addEventListener("click", function () { run("reload-settings"); });
      $("shutdown").addEventListener("click", shutdownService);
      $("gameStart").addEventListener("click", function () { gamePower("/api/game/start", "启动", {}); });
      $("gameRestart").addEventListener("click", function () { gamePower("/api/game/restart", "重启", {}); });
      $("gameStop").addEventListener("click", function () { gamePower("/api/game/stop", "停止", {}); });

      $("uploadSave").addEventListener("click", uploadSave);
      $("saveFile").addEventListener("change", function () {
        var files = $("saveFile").files;
        txt("uploadNote", files && files.length ? "已选择 " + files[0].name + "（" + bytes(files[0].size) + "）" : "上限 256 MB。");
      });
      $("deleteSave").addEventListener("click", deleteSave);
      // The toolbar's own select-all and the table header's are two views of one set; both delegate here so they
      // cannot disagree about what "all" means.
      if ($("saveSelectAll")) $("saveSelectAll").addEventListener("change", function () { pickAll(this.checked); });
      $("inspectSaveMods").addEventListener("click", inspectSaveMods);
      $("applySaveMods").addEventListener("click", applySaveMods);
      $("syncSaveSelect").addEventListener("change", function () { renderSyncTable(null); });

      $("refreshMods").addEventListener("click", loadMods);
      $("applyMods").addEventListener("click", applyMods);
      $("modSearch").addEventListener("input", function () { if (modData) renderMods(); });

      $("steamRefresh").addEventListener("click", loadSteam);
      $("proxySave").addEventListener("click", function () { saveProxy($("steamProxy").value.trim()); });
      $("proxyTest").addEventListener("click", probeWorkshop);
      $("proxyClear").addEventListener("click", function () {
        // Clearing is an action, not a validation error: an empty box plus "保存" is ambiguous about whether the
        // operator meant to keep the old value, so the intent gets its own button.
        $("steamProxy").value = "";
        saveProxy("");
      });
      $("steamLogin").addEventListener("click", async function () {
        var user = $("steamUser").value.trim();
        var pass = $("steamPass").value;
        if (!user || !pass) { msg("steamResult", "账号和密码都要填", "err"); return; }
        msg("steamResult", "正在登录…");
        var data = await post("/api/steam/login", { account: user, password: pass });
        $("steamPass").value = "";
        if (!data.ok) {
          msg("steamResult", "失败：" + (data.message || data.detail || data.code), "err");
          return;
        }
        // The login is a conversation: steamcmd may answer with a Steam Guard prompt, and only a poll finds that
        // out. Starting it here means the operator does not have to click 刷新状态 to discover the code box.
        msg("steamResult", "已提交，等待 Steam 校验…");
        watchSteamLogin();
      });
      $("steamCodeSend").addEventListener("click", async function () {
        var code = $("steamCode").value.trim();
        if (!code) { msg("steamResult", "先填验证码", "err"); return; }
        msg("steamResult", "正在提交验证码…");
        var data = await post("/api/steam/code", { code: code });
        $("steamCode").value = "";
        if (!data.ok) { msg("steamResult", "验证码被拒：" + (data.message || data.detail || data.code), "err"); return; }
        msg("steamResult", "验证码已提交，等待校验…");
        watchSteamLogin();
      });
      $("download").addEventListener("click", async function () {
        var branch = $("versionSelect").value;
        msg("downloadResult", "已请求下载…");
        var data = await post("/api/game/download", { branch: branch });
        msg("downloadResult", data.ok ? (data.detail || "已开始") : "失败：" + (data.message || data.detail || data.code),
          data.ok ? "good" : "err");
      });
      $("progressRefresh").addEventListener("click", async function () {
        var data = await api("/api/game/progress");
        var progress = data.progress || {};
        $("progress").value = progress.percent || 0;
        txt("progressNote", (progress.percent || 0) + "%  " + (progress.detail || ""));
      });

      $("logAll").addEventListener("click", function () { logFilter = "all"; renderLogs(); });
      $("logWarn").addEventListener("click", function () { logFilter = "warn"; renderLogs(); });
      $("logError").addEventListener("click", function () { logFilter = "error"; renderLogs(); });
      $("logPause").addEventListener("click", function () {
        logPaused = !logPaused;
        $("logPause").textContent = logPaused ? "继续滚动" : "暂停滚动";
        $("logPause").classList.toggle("primary", logPaused);
        renderLogs();
      });
      $("logClear").addEventListener("click", function () {
        lastLog = [];
        renderLogs();
      });
    }

    wire();
    showView(currentView());
    poll();
    loadSaves();
    setInterval(poll, 1500);
    </script>
    </body>
    </html>
    """;
}
