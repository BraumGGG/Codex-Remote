const STATIC_ROUTES = new Set([
  "/desktop/overview", "/desktop/pairing", "/desktop/projects", "/desktop/devices",
  "/desktop/diagnostics", "/cloud/admin", "/install", "/remote/settings", "/remote/devices",
]);

export function resolveUiRoute(pathname = window.location.pathname) {
  const path = pathname.replace(/\/+$/, "") || "/remote/connect";
  const chat = path.match(/^\/remote\/chat\/([^/]+)$/);
  if (chat) return { group: "remote", page: "chat", threadId: decodeURIComponent(chat[1]) };
  if (path === "/remote/sessions") return { group: "remote", page: "sessions" };
  if (path === "/remote/settings") return { group: "remote", page: "settings" };
  if (path === "/remote/devices") return { group: "remote", page: "devices" };
  if (path === "/remote/connect" || path === "/remote") return { group: "remote", page: "connect" };
  if (path === "/install") return { group: "install", page: "install" };
  if (path === "/design" || path === "/design-prototype.html") return { group: "design", page: "prototype" };
  if (STATIC_ROUTES.has(path)) {
    const [group, page] = path.slice(1).split("/");
    return { group, page };
  }
  return { group: "remote", page: "connect" };
}

export function navigateUiRoute(route) {
  const path = route.threadId
    ? `/remote/chat/${encodeURIComponent(route.threadId)}`
    : `/${route.group}/${route.page}`;
  const target = `${path}${window.location.search}`;
  if (window.location.pathname + window.location.search !== target) window.history.pushState({}, "", target);
  window.dispatchEvent(new CustomEvent("codex-route-change", { detail: resolveUiRoute(path) }));
}

export function renderStaticRoute(container, route) {
  if (route.group === "design") {
    container.innerHTML = `<iframe class="design-prototype-frame" title="Codex Remote 完整设计稿" src="/design-prototype.html"></iframe>`;
    return;
  }
  const pages = {
    overview: { title: "Windows Host", subtitle: "常驻代理总览", kicker: "DESKTOP / OVERVIEW", hero: "工作区准备就绪", copy: "Codex Desktop 在线，已授权项目和远程设备均可安全访问。", stats: [["活跃设备", "1"], ["授权项目", "3"], ["最近同步", "刚刚"]] },
    pairing: { title: "配对中心", subtitle: "扫码、设备和安全信令", kicker: "DESKTOP / PAIRING", hero: "等待设备配对", copy: "二维码为一次性短期凭据，设备完成握手后立即失效。", stats: [["当前状态", "等待扫码"], ["二维码有效期", "02:41"], ["信令通道", "在线"]] },
    projects: { title: "授权项目", subtitle: "管理可访问的 Codex 工作区", kicker: "DESKTOP / PROJECTS", hero: "3 个项目已授权", copy: "访问边界绑定到项目根目录，新的 Codex 会话会自动出现在列表中。", stats: [["已授权", "3"], ["待索引", "0"], ["只读数据库", "开启"]] },
    devices: { title: "设备", subtitle: "远程设备与授权状态", kicker: "DESKTOP / DEVICES", hero: "设备连接正常", copy: "每台设备均使用独立密钥和服务端签名授权，可随时撤销。", stats: [["在线设备", "1"], ["Pro 授权", "1"], ["最近活动", "刚刚"]] },
    diagnostics: { title: "诊断", subtitle: "连接链路与组件健康", kicker: "DESKTOP / DIAGNOSTICS", hero: "所有组件运行正常", copy: "Signal、TURN、Sidecar 与 Desktop 均通过最近一次健康检查。", stats: [["Signal", "在线"], ["TURN", "relay"], ["Desktop", "在线"]] },
    admin: { title: "云端授权后台", subtitle: "Entitlement 与 Pro 授权", kicker: "CLOUD / ADMIN", hero: "授权服务在线", copy: "签发、撤销和查询 Pro 授权，所有变更保留可审计记录。", stats: [["有效授权", "24"], ["离线宽限", "2"], ["服务状态", "健康"]] },
    install: { title: "安装向导", subtitle: "5 步完成 Codex Bridge 部署", kicker: "INSTALL / WIZARD", hero: "安装步骤 1 / 5", copy: "确认安装目录和启动项，安装过程不会覆盖已有设备身份或项目授权。", stats: [["安装目录", "Codex Bridge"], ["版本", "beta.69"], ["数据策略", "保留"]] },
    settings: { title: "设置", subtitle: "连接、安全与显示偏好", kicker: "REMOTE / SETTINGS", hero: "连接设置", copy: "管理设备连接、显示密度和诊断信息。业务数据仍由端到端加密通道承载。", stats: [["通道", "WebRTC relay"], ["显示", "舒适"], ["通知", "开启"]] },
  };
  const page = pages[route.page] || pages.overview;
  const nav = route.group === "desktop"
    ? ["overview:总览", "pairing:配对", "projects:项目", "devices:设备", "diagnostics:诊断"]
    : route.group === "cloud" ? ["admin:授权后台"] : route.group === "install" ? ["install:安装向导"] : ["settings:设置"];
  if (route.group === "desktop") {
    container.innerHTML = `
      <div class="static-shell desktop-scene">
        <header class="static-topbar">
          <div class="brand-lockup"><span class="brand-mark" aria-hidden="true">C</span><span>Codex Remote</span></div>
          <span class="static-context">Windows Host</span>
          <button class="icon-button static-back" type="button" aria-label="返回会话" data-route="/remote/sessions">←</button>
        </header>
        <main class="desktop-stage">
          <div class="static-heading"><div><p class="eyebrow">${page.kicker}</p><h1>${page.title} · ${page.subtitle}</h1><p>正常运行 / 启动失败 / 已停止 · Codex Remote Host 管理控制台</p></div><span class="status-pill ok"><span class="status-dot online"></span>系统在线</span></div>
          <section class="host-window" aria-label="Codex Remote Host">
            <div class="host-window-bar"><span class="window-dot red"></span><span class="window-dot green"></span><span class="window-dot amber"></span><span class="window-title">⌁　Codex Remote · Host</span></div>
            <div class="host-window-body">
              <aside class="host-sidebar">
                <div class="host-brand"><span class="brand-mark" aria-hidden="true">C</span><div><strong>Codex Remote</strong><small>v0.9.1 · Host</small></div></div>
                <p class="host-nav-label">导航</p>
                ${nav.map((item) => { const [key, label] = item.split(":"); const target = `/${route.group}/${key}`; return `<button class="host-nav-item ${key === route.page ? "active" : ""}" data-route="${target}"><span class="host-nav-icon">${key === "overview" ? "⌂" : key === "diagnostics" ? "⌁" : key === "pairing" ? "⌗" : key === "devices" ? "▣" : "□"}</span>${label}<span class="host-nav-count">${key === "projects" ? "3" : key === "devices" ? "1" : ""}</span></button>`; }).join("")}
                <p class="host-nav-label account">账户</p><button class="host-nav-item" data-route="/remote/settings"><span class="host-nav-icon">⚙</span>设置</button>
              </aside>
              <section class="host-main">
                <div class="host-main-heading"><h2>${route.page === "overview" ? "总览" : page.title}</h2><p>${page.subtitle} · Host 当前状态、配对概况和最近活动</p></div>
                <article class="design-card host-status-card"><div class="host-status-icon">${route.page === "diagnostics" ? "⌁" : route.page === "pairing" ? "⌗" : "✓"}</div><div><strong>${page.hero}</strong><p>${page.copy}</p></div><button class="secondary-button" data-route="/desktop/diagnostics">查看诊断</button></article>
                <div class="host-stats">${page.stats.slice(0, 4).map(([label, value]) => `<article class="host-stat"><span>${label}</span><b>${value}</b><small>${route.page === "diagnostics" ? "最近检查通过" : "活跃中"}</small></article>`).join("")}</div>
                <article class="design-card host-module-card"><div class="host-table-head"><span>模块</span><span>状态</span><span>说明</span></div>${["Host", "Codex Desktop", "Signal 服务", "TURN 中继"].map((name, index) => `<div class="host-module-row"><span><i class="module-icon ${index === 3 ? "warn" : "ok"}">${index === 3 ? "!" : "✓"}</i>${name}</span><span class="status-pill ${index === 3 ? "warn" : "ok"}"><span class="status-dot"></span>${index === 3 ? "降级" : "运行中"}</span><span>${index === 3 ? "主路径不可用，已保留备用" : "正常 · 最近一次检查通过"}</span></div>`).join("")}</article>
                <article class="design-card host-setting"><span><strong>登录 Windows 后自动运行</strong><small>关闭窗口只会最小化到托盘，不会真正退出</small></span><span class="toggle on" aria-label="已开启"></span></article>
              </section>
            </div>
          </section>
        </main>
      </div>`;
    return;
  }
  container.innerHTML = `
    <div class="static-shell">
      <header class="static-topbar">
        <div class="brand-lockup"><span class="brand-mark" aria-hidden="true">C</span><span>Codex Remote</span></div>
        <span class="static-context">${route.group === "desktop" ? "Windows Host" : route.group === "cloud" ? "Cloud Console" : "Remote App"}</span>
        <button class="icon-button static-back" type="button" aria-label="返回会话" data-route="/remote/sessions">←</button>
      </header>
      <div class="static-layout">
        <aside class="static-nav" aria-label="页面导航">
          ${nav.map((item) => { const [key, label] = item.split(":"); const target = route.group === "install" ? "/install" : `/${route.group}/${key}`; return `<button class="static-nav-item ${key === route.page ? "active" : ""}" data-route="${target}">${label}</button>`; }).join("")}
        </aside>
        <main class="static-main">
          <div class="static-heading"><div><p class="eyebrow">${page.kicker}</p><h1>${page.title}</h1><p>${page.subtitle}</p></div><span class="status-pill ok"><span class="status-dot online"></span>系统在线</span></div>
          <section class="static-grid">
            <article class="design-card hero-card"><div class="card-kicker">当前状态</div><strong>${page.hero}</strong><p>${page.copy}</p><div class="hero-actions"><button class="primary-button" type="button" data-route="/remote/sessions">打开会话</button><button class="secondary-button" type="button" data-route="/remote/settings">查看设置</button></div></article>
            <article class="design-card"><div class="card-kicker">概览</div>${page.stats.map(([label, value]) => `<div class="stat-row"><span>${label}</span><b>${value}</b></div>`).join("")}</article>
            <article class="design-card"><div class="card-kicker">快捷操作</div><button class="list-action" data-route="/remote/settings">连接设置 <span>›</span></button><button class="list-action" data-route="/desktop/diagnostics">查看诊断 <span>›</span></button><button class="list-action" data-route="/install">安装向导 <span>›</span></button></article>
          </section>
        </main>
      </div>
    </div>`;
}
