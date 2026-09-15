"use strict";

import { createConfiguredTransport } from "./config.js?v=2026090107";
import { createExecutionStatus, reduceExecutionStatus } from "./execution-status.mjs?v=2026090107";
import { navigateUiRoute, renderStaticRoute, resolveUiRoute } from "./ui-routes.js?v=2026090107";

const STORAGE_KEY = "codexBridgeDevice";
const NAVIGATION_KEY = "codexBridgeNavigation";
const HISTORY_WINDOW_SIZE = 120;
const HISTORY_ESTIMATED_HEIGHT = 112;
const state = {
  credential: readCredential(),
  canSend: false,
  effectiveCanSend: false,
  entitlementState: "free",
  entitlementExpiresAt: null,
  desktopOnline: false,
  projects: [],
  threads: new Map(),
  selectedProject: null,
  selectedThread: null,
  activeProjectId: null,
  lastSequence: 0,
  source: null,
  reconnectTimer: null,
  syncTimer: null,
  syncGeneration: 0,
  imageUrls: new Set(),
  previewRequestId: 0,
  previewTrigger: null,
  transportMode: "remote",
  lastRemoteError: null,
  workspaceGeneration: 0,
  workspaceLoaded: false,
  historyItems: [],
  historyCursor: null,
  historyHasMore: false,
  historyLoadingOlder: false,
  historyWindow: "latest",
  historyWindowStart: 0,
  initializing: true,
  remoteReady: false,
  remoteSyncPromise: null,
  manualDisconnect: false,
  executionStatus: null,
};

const elements = {
  pairView: document.querySelector("#pair-view"),
  pairHostStatus: document.querySelector("#pair-host-status"),
  connectionActions: document.querySelector("#connection-actions"),
  retryButton: document.querySelector("#retry-button"),
  scanButton: document.querySelector("#scan-button"),
  pairSettingsButton: document.querySelector("#pair-settings-button"),
  workspaceView: document.querySelector("#workspace-view"),
  projectList: document.querySelector("#project-list"),
  hostState: document.querySelector("#host-state"),
  hostLabel: document.querySelector("#host-label"),
  onlineDot: document.querySelector("#online-dot"),
  tierBadge: document.querySelector("#tier-badge"),
  disconnectButton: document.querySelector("#disconnect-button"),
  mobileRefreshButton: document.querySelector("#mobile-refresh-button"),
  mobileNavButtons: document.querySelectorAll("[data-mobile-tab]"),
  devicesView: document.querySelector("#devices-view"),
  devicesSettingsButton: document.querySelector("#devices-settings-button"),
  currentDeviceButton: document.querySelector("#current-device-button"),
  addDeviceButton: document.querySelector("#add-device-button"),
  mobileDeviceName: document.querySelector("#mobile-device-name"),
  mobileDeviceState: document.querySelector("#mobile-device-state"),
  mobileDeviceBadge: document.querySelector("#mobile-device-badge"),
  settingsView: document.querySelector("#settings-view"),
  settingsCloseButton: document.querySelector("#settings-close-button"),
  settingsDisconnectButton: document.querySelector("#settings-disconnect-button"),
  notificationToggle: document.querySelector("#notification-toggle"),
  themePicker: document.querySelector("#theme-picker"),
  networkDiagnosticsButton: document.querySelector("#network-diagnostics-button"),
  settingsEntitlement: document.querySelector("#settings-entitlement"),
  backButton: document.querySelector("#back-button"),
  refreshButton: document.querySelector("#refresh-button"),
  conversationProject: document.querySelector("#conversation-project"),
  conversationName: document.querySelector("#conversation-name"),
  emptyState: document.querySelector("#empty-state"),
  messageList: document.querySelector("#message-list"),
  jumpLatest: document.querySelector("#jump-latest"),
  composer: document.querySelector("#composer"),
  messageForm: document.querySelector("#message-form"),
  messageInput: document.querySelector("#message-input"),
  sendButton: document.querySelector("#send-button"),
  readonlyBar: document.querySelector("#readonly-bar"),
  composerStatus: document.querySelector("#composer-status"),
  executionStatus: document.querySelector("#execution-status"),
  executionStatusText: document.querySelector("#execution-status-text"),
  toast: document.querySelector("#toast"),
  routeView: document.querySelector("#route-view"),
  filePreview: document.querySelector("#file-preview"),
  filePreviewClose: document.querySelector("#file-preview-close"),
  filePreviewTitle: document.querySelector("#file-preview-title"),
  filePreviewKind: document.querySelector("#file-preview-kind"),
  filePreviewState: document.querySelector("#file-preview-state"),
  filePreviewMarkdown: document.querySelector("#file-preview-markdown"),
  filePreviewText: document.querySelector("#file-preview-text"),
};

let transport;

elements.retryButton.addEventListener("click", retryRemoteConnection);
elements.scanButton.addEventListener("click", () => window.CodexBridgeNative?.scanPairingCode());
elements.scanButton.hidden = typeof window.CodexBridgeNative?.scanPairingCode !== "function";
elements.disconnectButton.addEventListener("click", disconnect);
elements.pairSettingsButton.addEventListener("click", () => navigateUiRoute({ group: "remote", page: "settings" }));
elements.settingsCloseButton.addEventListener("click", () => {
  if (window.history.length > 1) window.history.back();
  else navigateUiRoute({ group: "remote", page: state.workspaceLoaded ? "sessions" : "connect" });
});
elements.settingsDisconnectButton.addEventListener("click", () => {
  if (window.confirm("确认断开当前连接？断开后需要重新扫描二维码或粘贴配对链接。")) disconnect();
});
elements.mobileRefreshButton.addEventListener("click", async () => {
  elements.mobileRefreshButton.disabled = true;
  try { await loadWorkspace(); showToast("项目已刷新"); }
  catch (error) { showToast(friendlyError(error.code || error.message)); }
  finally { elements.mobileRefreshButton.disabled = false; }
});
elements.devicesSettingsButton.addEventListener("click", () => navigateUiRoute({ group: "remote", page: "settings" }));
elements.currentDeviceButton.addEventListener("click", () => {
  if (!state.desktopOnline) { showToast("Windows 电脑当前离线"); return; }
  navigateUiRoute({ group: "remote", page: "sessions" });
});
elements.addDeviceButton.addEventListener("click", () => window.CodexBridgeNative?.scanPairingCode());
elements.mobileNavButtons.forEach((button) => button.addEventListener("click", () => handleMobileTab(button.dataset.mobileTab)));
elements.notificationToggle.checked = localStorage.getItem("codexBridgeNotifications") !== "off";
elements.notificationToggle.addEventListener("change", () => {
  localStorage.setItem("codexBridgeNotifications", elements.notificationToggle.checked ? "on" : "off");
  showToast(elements.notificationToggle.checked ? "通知已开启" : "通知已关闭");
});
elements.themePicker.addEventListener("click", (event) => {
  const button = event.target.closest("[data-theme-choice]");
  if (!button) return;
  setMobileTheme(button.dataset.themeChoice);
});
elements.networkDiagnosticsButton.addEventListener("click", () => {
  const stateText = state.desktopOnline ? "当前 Windows 电脑在线，远程通道可用" : "当前 Windows 电脑离线，请检查 Host";
  showToast(stateText);
});
elements.backButton.addEventListener("click", closeConversation);
elements.refreshButton.addEventListener("click", () => loadConversation(true));
elements.messageForm.addEventListener("submit", submitMessage);
elements.messageInput.addEventListener("input", resizeComposer);
elements.messageList.addEventListener("scroll", scheduleHistoryWindowUpdate, { passive: true });
elements.jumpLatest.addEventListener("click", jumpToLatest);
elements.filePreviewClose.addEventListener("click", closeFilePreview);
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !elements.filePreview.hidden) {
    closeFilePreview();
  }
});

initialize();

function applyUiRoute(route = resolveUiRoute()) {
  // A manual disconnect is a security boundary, not merely a visual state.
  // History navigation must never reveal the cached workspace afterwards.
  if (state.manualDisconnect && route.group === "remote" && !["connect", "devices"].includes(route.page)) {
    navigateUiRoute({ group: "remote", page: "devices" });
    return;
  }
  updateMobileNav(route);
  const isStaticPage = route.group !== "remote";
  elements.routeView.hidden = !isStaticPage;
  elements.settingsView.hidden = route.page !== "settings" || isStaticPage;
  elements.devicesView.hidden = route.page !== "devices" || isStaticPage;
  if (isStaticPage) {
    elements.pairView.hidden = true;
    elements.workspaceView.hidden = true;
    renderStaticRoute(elements.routeView, route);
    elements.routeView.querySelectorAll("[data-route]").forEach((button) => {
      button.addEventListener("click", () => {
        const target = resolveUiRoute(button.dataset.route);
        navigateUiRoute(target);
      });
    });
    return;
  }
  if (route.page === "settings") {
    elements.pairView.hidden = true;
    elements.workspaceView.hidden = true;
    renderMobileSettings();
    return;
  }
  if (route.page === "devices") {
    elements.pairView.hidden = true;
    elements.workspaceView.hidden = true;
    renderMobileDevices();
    return;
  }
  if (route.page === "connect") {
    if (!state.workspaceLoaded) showPairView();
  } else if (route.page === "sessions") {
    if (state.workspaceLoaded) {
      document.body.classList.remove("conversation-open");
      elements.pairView.hidden = true;
      elements.workspaceView.hidden = false;
    }
  }
}

function handleMobileTab(tab) {
  if (tab === "settings") {
    navigateUiRoute({ group: "remote", page: "settings" });
    return;
  }
  if (tab === "devices") {
    navigateUiRoute({ group: "remote", page: "devices" });
    return;
  }
  document.body.classList.remove("conversation-open");
  navigateUiRoute({ group: "remote", page: "sessions" });
}

function updateMobileNav(route = resolveUiRoute()) {
  const active = route.page === "settings" ? "settings"
    : route.page === "devices" ? "devices"
      : route.page === "chat" ? "sessions" : "projects";
  elements.mobileNavButtons.forEach((button) => {
    button.classList.toggle("active", button.dataset.mobileTab === active);
  });
}

function setMobileTheme(choice) {
  localStorage.setItem("codexBridgeMobileTheme", choice);
  const resolved = choice === "system"
    ? (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light")
    : choice;
  document.body.dataset.mobileTheme = resolved;
  elements.themePicker.querySelectorAll("[data-theme-choice]").forEach((button) => {
    button.classList.toggle("active", button.dataset.themeChoice === choice);
  });
}

function renderMobileSettings() {
  const choice = localStorage.getItem("codexBridgeMobileTheme") || "system";
  setMobileTheme(choice);
  elements.settingsEntitlement.textContent = state.effectiveCanSend
    ? "Pro 权限已启用"
    : state.workspaceLoaded ? "免费只读权限" : "尚未建立连接";
}

function renderMobileDevices() {
  const label = elements.hostLabel.textContent || "Windows 电脑";
  const online = state.desktopOnline;
  elements.mobileDeviceName.textContent = label;
  elements.mobileDeviceState.textContent = online ? "在线 · 端到端加密连接" : "离线 · 等待 Host 响应";
  elements.mobileDeviceBadge.textContent = online ? "在线" : "离线";
  elements.mobileDeviceBadge.classList.toggle("online", online);
}

window.addEventListener("popstate", () => applyUiRoute());
window.addEventListener("codex-route-change", (event) => applyUiRoute(event.detail));
applyUiRoute();

window.CodexBridgeApp = {
  diagnostics() {
    return {
      uiBuild: document.querySelector('meta[name="codex-bridge-ui-build"]')?.content || 'unknown',
      url: window.location.href,
      workspaceLoaded: state.workspaceLoaded,
      manualDisconnect: state.manualDisconnect,
      remoteReady: state.remoteReady
    };
  },
  handleBack() {
    if (elements.filePreview && !elements.filePreview.hidden) {
      closeFilePreview();
      return true;
    }
    if (state.selectedThread || document.body.classList.contains("conversation-open")) {
      closeConversation();
      return true;
    }
    if (state.activeProjectId) {
      state.activeProjectId = null;
      renderProjects();
      return true;
    }
    const route = resolveUiRoute();
    if (route.page === "settings" || route.page === "devices") {
      navigateUiRoute({ group: "remote", page: state.workspaceLoaded ? "sessions" : "connect" });
      return true;
    }
    if (route.page === "sessions" && window.history.length > 1) {
      window.history.back();
      return true;
    }
    return false;
  },
  resume() {
    // Android can emit pageshow/visibilitychange while the initial handshake
    // is still in flight. Do not start a second workspace load or handshake.
    if (!state.initializing && !state.manualDisconnect && !state.remoteReady && transport && transport.state !== "online") {
      transport.connect().catch(() => { });
    }
  },
  prepareForExit() {
    transport?.disconnect();
  },
};

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) window.CodexBridgeApp.resume();
});
window.addEventListener("pageshow", () => window.CodexBridgeApp.resume());

async function initialize() {
  try {
    const initialRoute = resolveUiRoute();
    if (initialRoute.group !== "remote" || initialRoute.page === "settings") {
      applyUiRoute(initialRoute);
      return;
    }
    const configured = await createConfiguredTransport({
    getCredential: () => state.credential,
    onUnauthorized: () => {
      localStorage.removeItem(STORAGE_KEY);
      state.credential = null;
      showPairView();
    },
    });
    transport = configured.transport;
    state.transportMode = configured.mode;
    if (configured.mode === "remote") {
      state.credential = { remote: true };
      transport.addEventListener("statechange", handleRemoteStateChange);
      showRemoteConnecting();
      try {
        await transport.connect();
      } catch (error) {
        showRemoteFailure(error.message);
      }
      return;
    }
    await updateStatus();
    if (!state.credential) {
      showPairView();
      return;
    }

    try {
      await loadWorkspace();
    } catch (error) {
      if (error.status !== 401) {
        showToast(error.message);
      }
    }
  } finally {
    state.initializing = false;
  }
}

function handleRemoteStateChange(event) {
  const phase = event.detail;
  if (state.transportMode === "remote" && !state.workspaceLoaded) {
    elements.pairView.hidden = false;
  }
  if (phase === "online") {
    state.remoteReady = true;
    state.lastRemoteError = null;
    elements.pairHostStatus.textContent = "已连接，正在同步工作区";
    const syncPromise = (async () => {
      try {
        await updateStatus();
        await loadWorkspace();
      } catch (error) {
        showRemoteFailure(error.code || error.message);
      }
    })();
    state.remoteSyncPromise = syncPromise;
    syncPromise.finally(() => {
      if (state.remoteSyncPromise === syncPromise) state.remoteSyncPromise = null;
    });
    return;
  }
  state.remoteReady = false;
  if (phase === "signaling") elements.pairHostStatus.textContent = "正在连接安全信令";
  if (phase === "connecting") elements.pairHostStatus.textContent = "正在建立加密通道";
  if (phase === "reconnecting") {
    elements.pairHostStatus.textContent = state.lastRemoteError
      ? `连接中断，正在自动重试（错误码：${state.lastRemoteError}）`
      : "连接中断，正在自动重试";
  }
  elements.hostState.textContent = phase === "connecting" ? "正在建立加密通道" : "正在重连";
  elements.onlineDot.classList.remove("online");
}

async function updateStatus() {
  try {
    const status = await transport.status();
    state.desktopOnline = status.desktopOnline;
    elements.pairHostStatus.textContent = status.desktopOnline ? "Codex Desktop 在线" : "Codex Desktop 离线";
    elements.hostState.textContent = status.desktopOnline ? "Codex Desktop 在线" : "Codex Desktop 离线";
    elements.onlineDot.classList.toggle("online", status.desktopOnline);
  } catch {
    elements.pairHostStatus.textContent = "Host 无响应";
    elements.hostState.textContent = "Host 无响应";
    elements.onlineDot.classList.remove("online");
  }
}

async function retryRemoteConnection() {
  state.manualDisconnect = false;
  elements.retryButton.disabled = true;
  showRemoteConnecting();
  try {
    await transport.connect();
  } catch (error) {
    showRemoteFailure(error.code || error.message);
  } finally {
    elements.retryButton.disabled = false;
  }
}

async function loadWorkspace() {
  const workspaceGeneration = ++state.workspaceGeneration;
  const selectedProjectId = state.selectedProject?.id || null;
  const selectedThread = state.selectedThread;
  const selectedThreadId = state.selectedThread?.id || null;
  const [capabilities, projects] = await Promise.all([
    transport.capabilities(),
    transport.listProjects(),
  ]);
  state.canSend = capabilities.canSend;
  state.effectiveCanSend = state.canSend && (!state.selectedProject || projectCanSend(state.selectedProject));
  state.entitlementState = capabilities.entitlementState || (capabilities.canSend ? "pro" : "free");
  state.entitlementExpiresAt = capabilities.expiresAt || null;
  state.projects = projects;
  state.threads.clear();
  for (const project of projects) state.threads.set(project.id, createThreadPageState());

  if (workspaceGeneration !== state.workspaceGeneration) return;

  if (state.activeProjectId && !projects.some((project) => project.id === state.activeProjectId)) {
    state.activeProjectId = null;
  }
  renderProjects();
  renderTier();
  state.workspaceLoaded = true;
  showWorkspaceView();
  if (selectedThreadId) {
    const project = state.projects.find((item) => item.id === selectedProjectId);
    if (project && selectedThread && state.selectedThread?.id === selectedThreadId) {
      await selectThread(project, selectedThread);
      return;
    }
  }
  if (!state.selectedThread) restoreNavigationState();
}

function restoreNavigationState() {
  let saved;
  try {
    saved = JSON.parse(localStorage.getItem(NAVIGATION_KEY) || "null");
  } catch {
    saved = null;
  }
  if (!saved?.threadId) return;
  const project = state.projects.find((item) => item.id === saved.projectId);
  if (!project) return;
  state.activeProjectId = project.id;
  loadThreadPage(project, true).then(() => {
    const thread = getThreadPage(project.id).items.find((item) => item.id === saved.threadId);
    if (thread && !state.selectedThread) selectThread(project, thread);
  }).catch(() => {});
}

function createThreadPageState() {
  return { items: [], cursor: null, hasMore: true, query: "", loading: false, generation: 0, loaded: false };
}

function getThreadPage(projectId) {
  if (!state.threads.has(projectId)) state.threads.set(projectId, createThreadPageState());
  return state.threads.get(projectId);
}

async function loadThreadPage(project, reset = false) {
  const page = getThreadPage(project.id);
  if (page.loading || (!reset && !page.hasMore)) return;
  if (reset) {
    page.items = [];
    page.cursor = null;
    page.hasMore = true;
    page.loaded = false;
  }
  page.loading = true;
  const generation = ++page.generation;
  renderProjects();
  try {
    let result;
    try {
      result = await transport.listThreads(project.id, page.cursor, 10, page.query || null);
    } catch (error) {
      if (error.code !== "response_too_large") throw error;
      result = await transport.listThreads(project.id, page.cursor, 10, page.query || null);
    }
    if (generation !== page.generation) return;
    const known = new Set(page.items.map((item) => item.id));
    page.items.push(...(result.items || []).filter((item) => !known.has(item.id)));
    page.cursor = result.nextCursor || null;
    page.hasMore = Boolean(result.hasMore);
    page.loaded = true;
  } finally {
    if (generation === page.generation) page.loading = false;
    renderProjects();
  }
}

function saveNavigationState() {
  if (!state.selectedThread) {
    localStorage.removeItem(NAVIGATION_KEY);
    return;
  }
  localStorage.setItem(NAVIGATION_KEY, JSON.stringify({
    projectId: state.selectedProject?.id || null,
    threadId: state.selectedThread.id,
  }));
}

function renderProjects() {
  elements.projectList.replaceChildren();
  for (const project of state.projects) {
    const section = document.createElement("section");
    section.className = "project-group";

    const heading = document.createElement("div");
    heading.className = "project-heading";
    heading.setAttribute("role", "button");
    heading.tabIndex = 0;
    heading.setAttribute("aria-expanded", String(state.activeProjectId === project.id));
    const activateProject = () => {
      state.activeProjectId = state.activeProjectId === project.id ? null : project.id;
      renderProjects();
      if (state.activeProjectId === project.id && !getThreadPage(project.id).loaded) {
        loadThreadPage(project, true).catch((error) => showToast(friendlyError(error.code || error.message)));
      }
    };
    heading.addEventListener("click", activateProject);
    heading.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        activateProject();
      }
    });
    const name = document.createElement("span");
    name.textContent = project.name;
    const count = document.createElement("span");
    count.className = "project-count";
    count.textContent = String(project.threadCount);
    heading.append(name, count);

    const list = document.createElement("div");
    list.className = "thread-list";
    list.hidden = state.activeProjectId !== project.id;
    const page = getThreadPage(project.id);
    const search = document.createElement("input");
    search.type = "search";
    search.className = "thread-search";
    search.placeholder = "搜索会话";
    search.setAttribute("aria-label", `搜索 ${project.name} 的会话`);
    search.value = page.query;
    let searchTimer;
    search.addEventListener("input", () => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => {
        page.query = search.value.trim();
        loadThreadPage(project, true).catch((error) => showToast(friendlyError(error.code || error.message)));
      }, 250);
    });
    list.append(search);
    for (const thread of page.items) {
      list.append(createThreadButton(project, thread));
    }
    const more = document.createElement("button");
    more.type = "button";
    more.className = "thread-more secondary-button";
    more.textContent = page.loading ? "正在加载" : page.hasMore ? "加载更多" : page.items.length ? "已加载全部" : "暂无会话";
    more.disabled = page.loading || !page.hasMore;
    more.addEventListener("click", () => loadThreadPage(project).catch((error) => showToast(friendlyError(error.code || error.message))));
    list.append(more);

    section.append(heading, list);
    elements.projectList.append(section);
  }
}

function createThreadButton(project, thread) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "thread-button";
  button.dataset.threadId = thread.id;
  button.addEventListener("click", () => selectThread(project, thread));

  const title = document.createElement("span");
  title.className = "thread-title";
  title.textContent = thread.displayName || thread.title;
  const preview = document.createElement("span");
  preview.className = "thread-preview";
  // Codex often stores the same first user message as both the title and the
  // preview. Do not render that long text twice in the mobile project list.
  const titleText = String(thread.displayName || thread.title || "").trim();
  const previewText = String(thread.preview || "").trim();
  preview.textContent = previewText && !isDuplicatePreview(titleText, previewText)
    ? previewText
    : "";
  preview.hidden = !preview.textContent;
  const status = document.createElement("span");
  status.className = `thread-state ${thread.status}`;
  status.textContent = thread.status === "running" ? "执行中" : "空闲";
  button.append(title, preview, status);
  return button;
}

function isDuplicatePreview(title, preview) {
  if (!title || !preview) return false;
  const normalize = (value) => value.replace(/\s+/gu, " ").trim();
  const normalizedTitle = normalize(title);
  const normalizedPreview = normalize(preview);
  return normalizedTitle === normalizedPreview ||
    normalizedPreview.startsWith(normalizedTitle) ||
    normalizedTitle.startsWith(normalizedPreview);
}

async function selectThread(project, thread) {
  closeFilePreview();
  stopStream();
  stopConversationSync();
  state.selectedProject = project;
  state.selectedThread = thread;
  state.effectiveCanSend = state.canSend && projectCanSend(project);
  state.activeProjectId = project.id;
  saveNavigationState();
  const selectionGeneration = ++state.syncGeneration;
  state.lastSequence = 0;
  state.historyItems = [];
  state.historyCursor = null;
  state.historyHasMore = false;
  state.historyWindowStart = 0;
  state.executionStatus = createExecutionStatus(thread.id);
  document.querySelectorAll(".thread-button").forEach((button) => {
    button.classList.toggle("active", button.dataset.threadId === thread.id);
  });
  elements.conversationProject.textContent = project.name;
  elements.conversationName.textContent = thread.displayName || thread.title;
  elements.refreshButton.disabled = false;
  elements.emptyState.hidden = true;
  elements.messageList.hidden = false;
  elements.composer.hidden = false;
  document.body.classList.add("conversation-open");
  navigateUiRoute({ group: "remote", page: "chat", threadId: thread.id });
  renderComposer();
  renderExecutionStatus();
  renderConversationLoading();
  await loadConversation(false, selectionGeneration, thread.id);
}

async function loadConversation(showFeedback, selectionGeneration = state.syncGeneration, threadId = state.selectedThread?.id, recoveryAttempt = 0) {
  if (!state.selectedThread) {
    return;
  }

  try {
    let events;
    try {
      events = await transport.getEvents(threadId, null, 40, 48 * 1024);
    } catch (initialError) {
      // Large conversations are retried with smaller pages so one oversized
      // event does not make the whole conversation appear unavailable.
      if (initialError.code !== "response_too_large") throw initialError;
      events = await transport.getEvents(threadId, null, 10, 48 * 1024);
    }
    if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
    releaseImageUrls();
    state.historyItems = events.items || [];
    state.historyCursor = events.previousCursor || null;
    state.historyHasMore = Boolean(events.hasMoreBefore);
    state.historyWindowStart = Math.max(0, state.historyItems.length - HISTORY_WINDOW_SIZE);
    state.lastSequence = events.latestSequence || 0;
    state.executionStatus = restoreExecutionStatus(threadId, state.historyItems);
    renderExecutionStatus();
    renderHistory();
    scrollMessages();
    await connectStream(threadId, selectionGeneration);
    if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
    if (showFeedback) {
      setComposerStatus("已刷新");
    }
  } catch (error) {
    if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
    if (error.code === "request_timeout" && recoveryAttempt === 0) {
      renderConversationLoading();
      try {
        await transport.recoverFromStaleConnection("request_timeout");
        await state.remoteSyncPromise;
        if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
        await loadConversation(showFeedback, selectionGeneration, threadId, 1);
        return;
      } catch (recoveryError) {
        error = recoveryError;
      }
    }
    renderConversationError(error.code || error.message, selectionGeneration, threadId);
  }
}

function renderConversationLoading() {
  releaseImageUrls();
  elements.messageList.replaceChildren();
  const status = document.createElement("div");
  status.className = "conversation-load-state";
  status.setAttribute("role", "status");
  status.textContent = "正在加载会话";
  elements.messageList.append(status);
}

function renderConversationError(code, selectionGeneration, threadId) {
  elements.messageList.replaceChildren();
  const status = document.createElement("div");
  status.className = "conversation-load-state error";
  status.setAttribute("role", "alert");
  const message = document.createElement("p");
  message.textContent = friendlyError(code);
  const retry = document.createElement("button");
  retry.type = "button";
  retry.className = "secondary-button conversation-retry";
  retry.textContent = "重试加载";
  retry.addEventListener("click", async () => {
    if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
    retry.disabled = true;
    renderConversationLoading();
    try {
      if (transport.state !== "online") {
        await transport.recoverFromStaleConnection(code);
      }
      await state.remoteSyncPromise;
      if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
      await loadConversation(false, selectionGeneration, threadId);
    } catch (error) {
      if (selectionGeneration !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
      renderConversationError(error.code || error.message, selectionGeneration, threadId);
    }
  });
  status.append(message, retry);
  elements.messageList.append(status);
}

function appendEvent(item, followLatest = true) {
  const sequence = item.sequence || 0;
  if (sequence > 0 && sequence <= state.lastSequence) {
    return false;
  }

  state.lastSequence = Math.max(state.lastSequence, item.sequence || 0);
  setExecutionStatus({
    type: "conversation_event",
    kind: item.kind,
    turnId: item.turnId,
  });
  state.historyItems.push(item);
  if (item.kind === "UserMessage" || item.kind === "AgentMessage") {
    if (item.kind === "UserMessage") {
      reconcilePendingMessage(item);
    }
    if (followLatest) state.historyWindowStart = Math.max(0, state.historyItems.length - HISTORY_WINDOW_SIZE);
    renderHistory();
    return true;
  } else if (item.kind === "TaskStarted" || item.kind === "TaskCompleted") {
    if (followLatest) state.historyWindowStart = Math.max(0, state.historyItems.length - HISTORY_WINDOW_SIZE);
    renderHistory();
    return true;
  }
  return false;
}

function createMessage(item, pending = false) {
  const article = document.createElement("article");
  article.className = `message ${item.kind === "UserMessage" ? "user" : "agent"}${pending ? " pending" : ""}`;
  if (pending) {
    article.dataset.pendingMessage = item.text;
    article.dataset.pendingAfterSequence = String(state.lastSequence);
    article.dataset.pendingAt = String(Date.now());
  }

  const meta = document.createElement("div");
  meta.className = "message-meta";
  const author = document.createElement("span");
  author.textContent = item.kind === "UserMessage" ? "你" : "Codex";
  const time = document.createElement("time");
  time.textContent = formatTime(item.timestamp);
  meta.append(author, time);

  const body = document.createElement("div");
  body.className = "message-body";
  body.textContent = item.text ?? item.textPreview ?? "";
  article.append(meta, body);
  if (!pending && item.textContentId) {
    const expand = document.createElement("button");
    expand.type = "button";
    expand.className = "message-expand secondary-button";
    expand.textContent = "展开完整内容";
    expand.addEventListener("click", async () => {
      expand.disabled = true;
      expand.textContent = "正在加载";
      try {
        body.textContent = await transport.getEventText(
          state.selectedThread?.id, item.sequence, item.textContentId);
        expand.remove();
      } catch (error) {
        expand.disabled = false;
        expand.textContent = "重试展开";
        showToast(friendlyError(error.code || error.message));
      }
    });
    article.append(expand);
  }
  if (!pending && Array.isArray(item.images) && item.images.length > 0) {
    const gallery = document.createElement("div");
    gallery.className = "message-images";
    for (const attachment of item.images) {
      const image = document.createElement("img");
      image.alt = "会话图片";
      image.loading = "lazy";
      gallery.append(image);
      loadImageAttachment(image, attachment.id, state.selectedThread?.id).catch(() => {
        image.remove();
      });
    }
    article.append(gallery);
  }
  if (!pending && Array.isArray(item.files) && item.files.length > 0) {
    article.append(createFileAttachments(item.files, state.selectedThread?.id));
  }
  return article;
}

function createFileAttachments(files, threadId) {
  const list = document.createElement("div");
  list.className = "message-files";
  for (const file of files) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "file-button";
    button.addEventListener("click", () => openFilePreview(file, threadId, button));

    const type = document.createElement("span");
    type.className = "file-type-badge";
    type.textContent = file.kind === "markdown" ? "MD" : file.name.split(".").pop()?.toUpperCase() || "TXT";
    const name = document.createElement("span");
    name.className = "file-name";
    name.textContent = file.name;
    const action = document.createElement("span");
    action.className = "file-action";
    action.textContent = "预览";
    button.append(type, name, action);
    list.append(button);
  }
  return list;
}

async function connectStream(expectedThreadId = state.selectedThread?.id, selectionGeneration = state.syncGeneration) {
  stopStream();
  if (!state.selectedThread || state.selectedThread.id !== expectedThreadId || selectionGeneration !== state.syncGeneration) {
    return;
  }

  const selectedId = expectedThreadId;
  const source = await transport.subscribe(
    selectedId,
    state.lastSequence,
    (item) => {
      const followLatest = isNearLatest();
      if (appendEvent(item, followLatest)) {
        if (followLatest) scrollMessages();
        else elements.jumpLatest.hidden = false;
      }
    },
    () => {
      if (state.source === source && state.selectedThread?.id === selectedId) {
        state.reconnectTimer = window.setTimeout(() => connectStream().catch(() => {}), 1500);
      }
    },
  );
  if (!state.selectedThread || state.selectedThread.id !== selectedId || selectionGeneration !== state.syncGeneration) {
    source.close();
    return;
  }
  state.source = source;
}

async function submitMessage(event) {
  event.preventDefault();
  if (!state.effectiveCanSend || !state.selectedThread) {
    return;
  }

  const text = elements.messageInput.value.trim();
  if (!text || text.includes("\n") || text.includes("\r")) {
    setComposerStatus("仅支持单行文本", true);
    return;
  }

  const pending = createMessage({
    kind: "UserMessage",
    text,
    timestamp: new Date().toISOString(),
  }, true);
  elements.messageList.append(pending);
  scrollMessages();
  elements.sendButton.disabled = true;
  setExecutionStatus({ type: "submission_started" });
  setComposerStatus("正在提交");
  try {
    await transport.submitMessage(state.selectedThread.id, text);
    elements.messageInput.value = "";
    resizeComposer();
    setExecutionStatus({ type: "submission_accepted" });
    setComposerStatus("Windows 已接收");
  } catch (error) {
    pending.remove();
    setExecutionStatus({ type: "submission_failed" });
    setComposerStatus(friendlyError(error.code), true);
  } finally {
    elements.sendButton.disabled = false;
  }
}

function renderTier() {
  elements.tierBadge.textContent = state.entitlementState === "offlinegrace"
    ? "Pro 离线"
    : state.effectiveCanSend ? "Pro" : "只读";
  elements.tierBadge.classList.toggle("pro", state.effectiveCanSend);
  elements.tierBadge.title = state.entitlementExpiresAt
    ? `授权有效期至 ${new Date(state.entitlementExpiresAt).toLocaleString("zh-CN")}`
    : state.effectiveCanSend ? "Pro 授权" : "免费只读";
}

function renderComposer() {
  elements.messageForm.hidden = !state.effectiveCanSend;
  elements.readonlyBar.hidden = state.effectiveCanSend;
  elements.composerStatus.textContent = "";
}

function restoreExecutionStatus(threadId, items) {
  let status = createExecutionStatus(threadId);
  for (const item of items) {
    status = reduceExecutionStatus(status, {
      type: "conversation_event",
      kind: item.kind,
      turnId: item.turnId,
    });
  }
  return status.active ? status : createExecutionStatus(threadId);
}

function setExecutionStatus(event) {
  const threadId = state.selectedThread?.id;
  if (!threadId) return;
  const current = state.executionStatus?.threadId === threadId
    ? state.executionStatus
    : createExecutionStatus(threadId);
  state.executionStatus = reduceExecutionStatus(current, event);
  renderExecutionStatus();
}

function renderExecutionStatus() {
  const status = state.executionStatus;
  const phase = status?.phase || "idle";
  elements.executionStatus.hidden = phase === "idle";
  elements.executionStatus.dataset.phase = phase;
  elements.executionStatusText.textContent = executionStatusText(phase);
}

function executionStatusText(phase) {
  const messages = {
    submitting: "正在发送到 Windows",
    accepted: "Windows 已接收，Codex 正在处理",
    working: "Codex 正在执行",
    responding: "Codex 正在回复",
    completed: "已完成",
    failed: "发送失败",
  };
  return messages[phase] || "";
}

function projectCanSend(project) {
  return Boolean(project?.canSend ?? project?.CanSend);
}

function showPairView() {
  elements.settingsView.hidden = true;
  elements.pairView.hidden = false;
  elements.workspaceView.hidden = true;
  document.querySelector("#pair-title").textContent = "连接电脑";
  document.body.classList.remove("conversation-open");
}

function showRemoteFailure(code) {
  state.lastRemoteError = code || "request_failed";
  elements.pairView.hidden = state.workspaceLoaded;
  elements.workspaceView.hidden = !state.workspaceLoaded;
  elements.pairHostStatus.textContent = `${friendlyError(state.lastRemoteError)}（错误码：${state.lastRemoteError}）`;
  elements.connectionActions.hidden = false;
  document.querySelector("#pair-title").textContent = "连接未完成";
}

function showRemoteConnecting() {
  elements.pairView.hidden = false;
  elements.workspaceView.hidden = true;
  elements.pairHostStatus.textContent = "正在连接安全信令";
  elements.connectionActions.hidden = true;
  document.querySelector("#pair-title").textContent = "正在连接电脑";
}

function showWorkspaceView() {
  if (state.manualDisconnect) {
    showPairView();
    return;
  }
  elements.settingsView.hidden = true;
  elements.pairView.hidden = true;
  elements.workspaceView.hidden = false;
  applyUiRoute(resolveUiRoute());
}

function disconnect() {
  state.manualDisconnect = true;
  closeFilePreview();
  stopStream();
  stopConversationSync();
  transport.disconnect();
  localStorage.removeItem(STORAGE_KEY);
  state.credential = null;
  state.workspaceLoaded = false;
  state.projects = [];
  state.threads.clear();
  state.activeProjectId = null;
  state.selectedProject = null;
  state.selectedThread = null;
  state.executionStatus = null;
  localStorage.removeItem(NAVIGATION_KEY);
  releaseImageUrls();
  window.history.replaceState({ group: "remote", page: "devices" }, "", "/remote/devices");
  window.dispatchEvent(new CustomEvent("codex-route-change", { detail: { group: "remote", page: "devices" } }));
  applyUiRoute({ group: "remote", page: "devices" });
}

function closeConversation() {
  closeFilePreview();
  stopStream();
  stopConversationSync();
  document.body.classList.remove("conversation-open");
  state.selectedThread = null;
  state.executionStatus = null;
  state.lastSequence = 0;
  state.historyItems = [];
  state.historyCursor = null;
  state.historyHasMore = false;
  elements.emptyState.hidden = false;
  elements.messageList.hidden = true;
  elements.jumpLatest.hidden = true;
  elements.composer.hidden = true;
  elements.refreshButton.disabled = true;
  document.querySelectorAll(".thread-button").forEach((button) => button.classList.remove("active"));
  saveNavigationState();
  if (window.location.pathname.startsWith("/remote/chat/") && window.history.length > 1) window.history.back();
}

function stopConversationSync() {
  state.syncGeneration++;
  if (state.syncTimer) {
    clearTimeout(state.syncTimer);
    state.syncTimer = null;
  }
}

function renderHistory() {
  releaseImageUrls();
  elements.messageList.replaceChildren();
  if (state.historyHasMore) {
    const older = document.createElement("button");
    older.type = "button";
    older.className = "history-more secondary-button";
    older.disabled = state.historyLoadingOlder;
    older.textContent = state.historyLoadingOlder ? "正在加载更早消息" : "加载更早消息";
    older.addEventListener("click", loadOlderHistory);
    elements.messageList.append(older);
  }
  const start = Math.max(0, Math.min(
    state.historyWindowStart,
    Math.max(0, state.historyItems.length - HISTORY_WINDOW_SIZE),
  ));
  const end = Math.min(state.historyItems.length, start + HISTORY_WINDOW_SIZE);
  const topSpacer = document.createElement("div");
  topSpacer.className = "history-spacer";
  topSpacer.style.height = `${start * HISTORY_ESTIMATED_HEIGHT}px`;
  elements.messageList.append(topSpacer);
  for (const item of state.historyItems.slice(start, end)) {
    const node = createEventNode(item);
    if (node) elements.messageList.append(node);
  }
  const bottomSpacer = document.createElement("div");
  bottomSpacer.className = "history-spacer";
  bottomSpacer.style.height = `${Math.max(0, state.historyItems.length - end) * HISTORY_ESTIMATED_HEIGHT}px`;
  elements.messageList.append(bottomSpacer);
}

function createEventNode(item) {
  if (item.kind === "UserMessage" || item.kind === "AgentMessage") return createMessage(item);
  if (item.kind === "TaskStarted" || item.kind === "TaskCompleted") {
    const row = document.createElement("div");
    row.className = `task-event ${item.kind === "TaskStarted" ? "running" : ""}`;
    row.textContent = item.kind === "TaskStarted" ? "Codex 开始执行" : "Codex 已完成";
    return row;
  }
  return null;
}

async function loadOlderHistory() {
  if (!state.selectedThread || !state.historyHasMore || state.historyLoadingOlder) return;
  const generation = state.syncGeneration;
  const threadId = state.selectedThread.id;
  state.historyLoadingOlder = true;
  renderHistory();
  const previousHeight = elements.messageList.scrollHeight;
  try {
    let page;
    try {
      page = await transport.getEvents(threadId, state.historyCursor);
    } catch (initialError) {
      if (initialError.code !== "response_too_large") throw initialError;
      page = await transport.getEvents(threadId, state.historyCursor, 12, 48 * 1024);
    }
    if (generation !== state.syncGeneration || state.selectedThread?.id !== threadId) return;
    const known = new Set(state.historyItems.map((item) => item.sequence));
    const older = (page.items || []).filter((item) => !known.has(item.sequence));
    state.historyItems.unshift(...older);
    state.historyWindowStart += older.length;
    state.historyCursor = page.previousCursor || null;
    state.historyHasMore = Boolean(page.hasMoreBefore);
    state.historyLoadingOlder = false;
    renderHistory();
    elements.messageList.scrollTop += elements.messageList.scrollHeight - previousHeight;
  } catch (error) {
    state.historyLoadingOlder = false;
    renderHistory();
    showToast(friendlyError(error.code || error.message));
  }
}

let historyFrame = 0;
function scheduleHistoryWindowUpdate() {
  if (historyFrame || state.historyItems.length <= HISTORY_WINDOW_SIZE) return;
  historyFrame = requestAnimationFrame(() => {
    historyFrame = 0;
    const maximum = Math.max(1, elements.messageList.scrollHeight - elements.messageList.clientHeight);
    const ratio = elements.messageList.scrollTop / maximum;
    const next = Math.round(ratio * Math.max(0, state.historyItems.length - HISTORY_WINDOW_SIZE));
    if (Math.abs(next - state.historyWindowStart) < 20) return;
    const previousHeight = elements.messageList.scrollHeight;
    const previousTop = elements.messageList.scrollTop;
    state.historyWindowStart = next;
    renderHistory();
    elements.messageList.scrollTop = previousTop * (elements.messageList.scrollHeight / Math.max(1, previousHeight));
  });
}

function isNearLatest() {
  return elements.messageList.scrollHeight - elements.messageList.scrollTop - elements.messageList.clientHeight < 96;
}

function jumpToLatest() {
  state.historyWindowStart = Math.max(0, state.historyItems.length - HISTORY_WINDOW_SIZE);
  renderHistory();
  elements.jumpLatest.hidden = true;
  scrollMessages();
}

function reconcilePendingMessage(item) {
  const pendingMessages = [...elements.messageList.querySelectorAll("[data-pending-message]")];
  if (pendingMessages.length === 0) {
    return;
  }

  const normalizedText = normalizeMessageText(item.text);
  const exact = pendingMessages.find(
    (pending) => normalizeMessageText(pending.dataset.pendingMessage) === normalizedText,
  );
  if (exact) {
    exact.remove();
    return;
  }

  const now = Date.now();
  const eligible = pendingMessages.find((pending) =>
    (item.sequence || 0) > Number(pending.dataset.pendingAfterSequence || 0) &&
    now - Number(pending.dataset.pendingAt || 0) <= 30000,
  );
  eligible?.remove();
}

function normalizeMessageText(value) {
  return String(value || "").replace(/\s+/gu, " ").trim();
}

function stopStream() {
  if (state.source) {
    state.source.close();
    state.source = null;
  }
  if (state.reconnectTimer) {
    clearTimeout(state.reconnectTimer);
    state.reconnectTimer = null;
  }
}

async function loadImageAttachment(image, attachmentId, threadId) {
  if (!threadId || !attachmentId) {
    throw new Error("Image attachment is unavailable.");
  }
  const url = URL.createObjectURL(await transport.getImage(threadId, attachmentId));
  if (!image.isConnected || state.selectedThread?.id !== threadId) {
    URL.revokeObjectURL(url);
    return;
  }

  state.imageUrls.add(url);
  image.src = url;
}

function releaseImageUrls() {
  for (const url of state.imageUrls) {
    URL.revokeObjectURL(url);
  }
  state.imageUrls.clear();
}

async function openFilePreview(file, threadId, trigger) {
  if (!threadId) {
    return;
  }

  const requestId = ++state.previewRequestId;
  state.previewTrigger = trigger;
  elements.filePreview.hidden = false;
  document.body.classList.add("file-preview-open");
  elements.filePreviewTitle.textContent = file.name;
  elements.filePreviewKind.textContent = file.kind === "markdown" ? "Markdown" : "文本";
  elements.filePreviewState.textContent = "正在加载";
  elements.filePreviewState.hidden = false;
  elements.filePreviewMarkdown.hidden = true;
  elements.filePreviewText.hidden = true;
  elements.filePreviewMarkdown.replaceChildren();
  elements.filePreviewText.textContent = "";
  elements.filePreviewClose.focus();

  try {
    const preview = await transport.getTextFile(threadId, file.id);
    if (requestId !== state.previewRequestId || elements.filePreview.hidden) {
      return;
    }

    elements.filePreviewState.hidden = true;
    if (preview.kind === "markdown") {
      renderMarkdownPreview(preview.content);
      elements.filePreviewMarkdown.hidden = false;
    } else {
      elements.filePreviewText.textContent = preview.content;
      elements.filePreviewText.hidden = false;
    }
  } catch (error) {
    if (requestId === state.previewRequestId && !elements.filePreview.hidden) {
      elements.filePreviewState.textContent = friendlyError(error.code);
      elements.filePreviewState.hidden = false;
    }
  }
}

function renderMarkdownPreview(content) {
  const parsed = marked.parse(content, { gfm: true });
  const fragment = DOMPurify.sanitize(parsed, {
    RETURN_DOM_FRAGMENT: true,
    FORBID_TAGS: ["form", "input", "button", "iframe", "object", "embed", "img", "style"],
    FORBID_ATTR: ["style"],
  });
  fragment.querySelectorAll("a").forEach((link) => {
    link.removeAttribute("href");
    link.removeAttribute("target");
    link.removeAttribute("rel");
  });
  elements.filePreviewMarkdown.replaceChildren(fragment);
}

function closeFilePreview() {
  if (!elements.filePreview || elements.filePreview.hidden) {
    return;
  }

  state.previewRequestId++;
  elements.filePreview.hidden = true;
  document.body.classList.remove("file-preview-open");
  elements.filePreviewMarkdown.replaceChildren();
  elements.filePreviewText.textContent = "";
  if (state.previewTrigger?.isConnected) {
    state.previewTrigger.focus();
  }
  state.previewTrigger = null;
}

function readCredential() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY));
  } catch {
    localStorage.removeItem(STORAGE_KEY);
    return null;
  }
}

function resizeComposer() {
  elements.messageInput.style.height = "auto";
  elements.messageInput.style.height = `${Math.min(elements.messageInput.scrollHeight, 128)}px`;
}

function scrollMessages() {
  requestAnimationFrame(() => {
    elements.messageList.scrollTop = elements.messageList.scrollHeight;
  });
}

function setComposerStatus(text, isError = false) {
  elements.composerStatus.textContent = text;
  elements.composerStatus.style.color = isError ? "var(--red)" : "";
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.hidden = false;
  window.setTimeout(() => {
    elements.toast.hidden = true;
  }, 3600);
}

function friendlyError(code) {
  const messages = {
    pairing_invalid: "配对码无效",
    pairing_expired: "配对码已过期",
    pairing_locked: "配对码已锁定",
    pairing_used: "配对码已使用",
    pairing_failed: "电脑未能完成配对，请扫描 Windows 上更新后的二维码",
    invalid_pairing: "二维码已失效，请扫描 Windows 上更新后的二维码",
    invalid_device_name: "设备名称无效",
    device_store_unavailable: "Host 无法保存设备，请稍后重试",
    forbidden_read_only: "当前设备为只读权限",
    thread_not_found: "会话不可用",
    invalid_message: "消息格式无效",
    desktop_unavailable: "Codex Desktop 未运行",
    interactive_session_unavailable: "Windows 当前不可交互",
    desktop_version_unsupported: "当前 Desktop 版本暂不兼容",
    send_failed: "发送失败",
    command_id_conflict: "命令标识冲突，已阻止重复发送",
    send_result_unknown: "发送结果未知，为避免重复提交请先在 Desktop 中确认",
    entitlement_expired: "Pro 授权已到期，当前保持只读",
    entitlement_invalid: "Pro 授权无效，当前保持只读",
    request_timeout: "请求超时，请重试",
    response_too_large: "会话内容较多，当前版本无法一次载入",
    unauthorized: "设备认证已失效",
    file_not_found: "文件不可用",
    remote_pairing_required: "需要从电脑端重新发起配对",
    signal_unavailable: "无法连接信令服务器，正在自动重试",
    signal_timeout: "连接信令服务器超时，请检查网络后重试",
    signal_message_timeout: "电脑响应超时，请确认 Windows Host 在线",
    answer_timeout: "电脑正在尝试建立中继通道，但响应超时，请稍后重试",
    signal_closed: "信令连接已中断，正在自动重试",
    ice_gathering_timeout: "网络协商超时，请切换网络后重试",
    ice_no_relay_candidate: "当前网络无法建立中继通道，请切换 Wi-Fi 或移动网络后重试",
    channel_open_timeout: "加密通道打开超时，请重试",
    native_identity_invalid: "设备安全身份不可用，请重启 App",
    native_signature_failed: "设备安全签名失败，请重启 App",
    native_signature_invalid: "设备安全签名格式无效，请更新 App",
    host_offline_or_capacity: "电脑端暂时离线，正在自动重试",
    transport_failed: "加密通道建立失败，正在自动重试",
    transport_disconnected: "连接已中断，正在自动重试",
  };
  return messages[code] || "请求失败";
}

function formatTime(value) {
  if (!value) {
    return "";
  }
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}

