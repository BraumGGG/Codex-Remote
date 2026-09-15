'use strict';

const state = { status: null, devices: [], diagnostics: null, projects: [], discoveredProjects: [], pairing: null, selectedDevice: null, revokeTarget: null, projectUpdateInFlight: false };
const $ = selector => document.querySelector(selector);
let accountState = null;
let activePage = 'overview';
let pairingRefreshTimer = null;
let accountRefreshTimer = null;

function showAuth(message = '', view = 'login') { $('#auth-gate').hidden = false; document.querySelectorAll('.auth-view').forEach(item => { item.hidden = item.dataset.authView !== view; }); $('#auth-message').textContent = message; document.querySelector('.app-shell').style.visibility = 'hidden'; }
function hideAuth() { $('#auth-gate').hidden = true; document.querySelector('.app-shell').style.visibility = ''; }
async function refreshAccount() { accountState = await window.codexBridge.accountStatus(); if (accountState.activated) hideAuth(); else showAuth(accountState.email ? '账号未激活，请输入激活码。' : '', accountState.email ? 'activate' : 'login'); return accountState; }
async function authAction(action) { try { let result; if (action === 'register') { await window.codexBridge.accountRegister($('#auth-register-email').value.trim(), $('#auth-register-password').value); $('#auth-message').textContent = '验证码已发送，有效期 15 分钟，请查收邮箱。'; return; } if (action === 'verify') { await window.codexBridge.accountVerify($('#auth-register-email').value.trim(), $('#auth-register-code').value.trim()); showAuth('注册成功，请重新输入账号密码登录。', 'login'); return; } if (action === 'login') { result = await window.codexBridge.accountLogin($('#auth-login-email').value.trim(), $('#auth-login-password').value); await refreshAccount(); if (accountState?.activated) await refresh(); else showAuth('登录成功，请输入激活码。', 'activate'); return; } if (action === 'activate') result = await window.codexBridge.accountActivate($('#auth-activate-code').value.trim()); if (action === 'reset-request') { await window.codexBridge.accountRequestPasswordReset($('#auth-reset-email').value.trim()); $('#auth-message').textContent = '验证码已发送，有效期 15 分钟，请查收邮箱。'; return; } if (action === 'reset-confirm') { await window.codexBridge.accountResetPassword($('#auth-reset-email').value.trim(), $('#auth-reset-code').value.trim(), $('#auth-reset-password').value); showAuth('密码已重置，请登录。', 'login'); return; } $('#auth-message').textContent = action === 'activate' ? '激活成功，正在加载客户端。' : '操作成功。'; await refreshAccount(); if (accountState?.activated) await refresh(); } catch (error) { $('#auth-message').textContent = error.message; } }

function formatTime() {
  return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date());
}

function setConnection(ok, message) {
  $('#toolbar-status').textContent = message;
  $('#status-text').textContent = message;
  for (const element of [$('#toolbar-dot'), $('#status-dot')]) {
    element.className = ok ? 'online' : 'error';
  }
}

function toast(message) {
  const target = $('#toast');
  target.textContent = message;
  target.classList.add('show');
  window.clearTimeout(toast.timer);
  toast.timer = window.setTimeout(() => target.classList.remove('show'), 3200);
}

function text(value) {
  return value === null || value === undefined || value === '' ? '--' : String(value);
}

function deviceName(device) {
  return device.displayName || device.name || device.deviceName || device.deviceId || '未命名设备';
}

function deviceId(device) {
  return device.deviceId || device.id || '--';
}

function deviceTransport(device) {
  return device.transport || device.kind || 'remote';
}

function readableTime(value) {
  if (!value) return '暂无活动记录';
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? text(value) : new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function renderDeviceDetail() {
  const detail = $('#device-detail');
  const device = state.selectedDevice;
  if (!device) {
    detail.innerHTML = '<span class="section-kicker">设备详情</span><div class="detail-empty"><div class="detail-icon">▤</div><strong>选择一台设备</strong><p>从左侧列表查看授权方式、设备标识和最近活动。</p></div>';
    return;
  }
  detail.replaceChildren();
  const heading = document.createElement('div');
  heading.className = 'detail-heading';
  heading.innerHTML = '<span class="section-kicker">设备详情</span>';
  const title = document.createElement('h2');
  title.textContent = deviceName(device);
  const badge = document.createElement('span');
  badge.className = 'state-badge online';
  badge.textContent = '已授权';
  heading.append(title, badge);
  const items = [['设备标识', deviceId(device)], ['传输方式', deviceTransport(device)], ['最近活动', readableTime(device.lastSeenAt || device.lastActiveAt || device.updatedAt)]];
  const list = document.createElement('dl');
  list.className = 'detail-list';
  for (const [label, value] of items) { const term = document.createElement('dt'); term.textContent = label; const desc = document.createElement('dd'); desc.textContent = value; list.append(term, desc); }
  const permissions = document.createElement('label');
  permissions.className = 'device-send-access';
  const sendToggle = document.createElement('input');
  sendToggle.type = 'checkbox';
  sendToggle.checked = Boolean(device.canSend ?? device.CanSend);
  const sendCopy = document.createElement('span');
  sendCopy.textContent = sendToggle.checked ? '允许此设备发送指令' : '此设备仅可只读访问';
  sendToggle.addEventListener('change', async () => {
    sendToggle.disabled = true;
    try {
      await window.codexBridge.updateDevicePermissions(deviceTransport(device), deviceId(device), sendToggle.checked);
      toast(sendToggle.checked ? '设备已允许发送指令' : '设备已切换为只读');
      await refresh();
    } catch (error) {
      sendToggle.checked = !sendToggle.checked;
      toast(`更新设备权限失败：${error.message}`);
    } finally { sendToggle.disabled = false; }
  });
  permissions.append(sendToggle, sendCopy);
  const revoke = document.createElement('button');
  revoke.className = 'detail-revoke'; revoke.type = 'button'; revoke.textContent = '撤销设备授权';
  revoke.addEventListener('click', () => showRevoke(device));
  detail.append(heading, list, permissions, revoke);
}

function showRevoke(device) { state.revokeTarget = device; $('#revoke-device-name').textContent = deviceName(device); $('#revoke-modal').hidden = false; $('#revoke-confirm').focus(); }
function closeRevoke() { state.revokeTarget = null; $('#revoke-modal').hidden = true; }

function renderStatus() {
  const status = state.status;
  const desktopOnline = Boolean(status && (status.desktopOnline ?? status.DesktopOnline));
  $('#version').textContent = text(status && (status.version ?? status.Version));
  $('#host-state').textContent = status ? '正在运行' : '不可用';
  $('#host-detail').textContent = status ? '127.0.0.1:5096' : '检查 Host 启动状态';
  $('#desktop-state').textContent = desktopOnline ? '已连接' : '未连接';
  $('#host-version').textContent = text(status && (status.version ?? status.Version));
  $('#overview-badge').textContent = status ? 'Host 已连接' : '连接失败';
  $('#overview-badge').className = `state-badge ${status ? 'online' : 'error'}`;
}

function renderDevices() {
  const devices = Array.isArray(state.devices) ? state.devices : [];
  const query = ($('#devices-search').value || '').trim().toLocaleLowerCase();
  const visibleDevices = devices.filter(device => deviceName(device).toLocaleLowerCase().includes(query));
  $('#device-count').textContent = String(devices.length);
  $('#overview-device-count').textContent = String(devices.length);
  $('#devices-updated').textContent = `更新于 ${formatTime()}`;
  $('#devices-summary').textContent = `共 ${devices.length} 台已配对设备`;
  const list = $('#device-list');
  list.replaceChildren();
  if (!visibleDevices.length) {
    list.innerHTML = `<div class="table-placeholder">${devices.length ? '没有匹配的设备' : '当前没有已配对设备'}</div>`;
    return;
  }
  for (const device of visibleDevices) {
    const row = document.createElement('div');
    row.className = 'device-row';
    const name = document.createElement('div');
    name.className = 'device-name';
    const title = document.createElement('b');
    title.textContent = deviceName(device);
    const id = document.createElement('span');
    id.textContent = deviceId(device);
    name.append(title, id);
    const transport = document.createElement('span');
    transport.textContent = deviceTransport(device);
    const active = document.createElement('span');
    active.textContent = text(device.lastSeenAt || device.lastActiveAt || device.updatedAt);
    const action = document.createElement('button');
    action.className = 'revoke'; action.type = 'button'; action.textContent = '撤销';
    action.addEventListener('click', event => { event.stopPropagation(); showRevoke(device); });
    row.addEventListener('click', () => { state.selectedDevice = device; renderDeviceDetail(); document.querySelectorAll('.device-row').forEach(item => item.classList.toggle('selected', item === row)); });
    row.append(name, transport, active, action);
    list.append(row);
  }
  renderDeviceDetail();
}

function formatProjectTime(value) {
  const timestamp = Number(value);
  if (!Number.isFinite(timestamp) || timestamp <= 0) return '暂无会话活动';
  return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(timestamp));
}

function renderProjects() {
  const query = ($('#projects-search').value || '').trim().toLocaleLowerCase();
  const projects = (Array.isArray(state.discoveredProjects) ? state.discoveredProjects : []).filter(project =>
    String(project.name || project.Name || '').toLocaleLowerCase().includes(query));
  $('#projects-updated').textContent = `更新于 ${formatTime()}`;
  const authorizedCount = state.discoveredProjects.filter(project => Boolean(project.authorized ?? project.Authorized)).length;
  $('#projects-summary').textContent = `发现 ${state.discoveredProjects.length} 个本机项目，已授权 ${authorizedCount} 个`;
  const list = $('#project-list');
  list.replaceChildren();
  if (!projects.length) {
    list.innerHTML = `<div class="table-placeholder">${state.discoveredProjects.length ? '没有匹配的项目' : '尚未发现可授权的 Codex 项目'}</div>`;
    return;
  }
  for (const project of projects) {
    const row = document.createElement('div');
    row.className = 'project-row';
    const name = document.createElement('div');
    name.className = 'project-name';
    const title = document.createElement('b');
    title.textContent = text(project.name ?? project.Name);
    const id = document.createElement('code');
    id.textContent = text(project.path ?? project.Path);
    name.append(title, id);
    const threads = document.createElement('div');
    threads.className = 'project-stat';
    const threadCount = document.createElement('strong');
    threadCount.textContent = String(project.threadCount ?? project.ThreadCount ?? 0);
    const threadLabel = document.createElement('span');
    threadLabel.textContent = '个会话';
    threads.append(threadCount, threadLabel);
    const activity = document.createElement('span');
    activity.className = 'project-activity';
    activity.textContent = formatProjectTime(project.lastActivityAtMs ?? project.LastActivityAtMs);
    const access = document.createElement('label');
    access.className = 'project-access';
    const toggle = document.createElement('input');
    toggle.type = 'checkbox';
    toggle.checked = Boolean(project.authorized ?? project.Authorized);
    toggle.disabled = state.projectUpdateInFlight;
    const label = document.createElement('span');
    label.textContent = toggle.checked ? '已授权' : '未授权';
    toggle.addEventListener('change', () => updateProjectAuthorization(project, toggle.checked));
    access.append(toggle, label);
    const sendAccess = document.createElement('label'); sendAccess.className = 'project-access project-send-access';
    const sendToggle = document.createElement('input'); sendToggle.type = 'checkbox'; sendToggle.checked = Boolean(project.canSend ?? project.CanSend); sendToggle.disabled = state.projectUpdateInFlight || !toggle.checked;
    const sendLabel = document.createElement('span'); sendLabel.textContent = sendToggle.checked ? '可发送' : '只读';
    sendToggle.addEventListener('change', async () => { const current = state.discoveredProjects.filter(item => Boolean(item.authorized ?? item.Authorized)); const permissions = current.map(item => ({ path: item.path ?? item.Path, canSend: item === project ? sendToggle.checked : Boolean(item.canSend ?? item.CanSend) })); await window.codexBridge.updateProjects(permissions.map(item => item.path), permissions); await refresh(); }); sendAccess.append(sendToggle, sendLabel);
    row.append(name, threads, activity, access, sendAccess);
    list.append(row);
  }
}

async function updateProjectAuthorization(project, authorized) {
  if (state.projectUpdateInFlight) return;
  const projectPath = project.path ?? project.Path;
  const current = state.discoveredProjects
    .filter(item => Boolean(item.authorized ?? item.Authorized))
    .map(item => item.path ?? item.Path);
  const next = authorized ? [...new Set([...current, projectPath])] : current.filter(item => item !== projectPath);
  state.projectUpdateInFlight = true;
  renderProjects();
  try {
    const permissions = state.discoveredProjects
      .filter(item => next.includes(item.path ?? item.Path))
      .map(item => ({ path: item.path ?? item.Path, canSend: Boolean(item.canSend ?? item.CanSend) }));
    await window.codexBridge.updateProjects(next, permissions);
    toast(authorized ? '项目已授权。' : '项目授权已撤销。');
    await refresh();
  } catch (error) {
    toast(`更新项目授权失败：${error.message}`);
    await refresh();
  } finally {
    state.projectUpdateInFlight = false;
    renderProjects();
  }
}

async function renderPairingQr(value) {
  const frame = $('#pair-qr');
  frame.replaceChildren();
  if (!value) {
    const message = document.createElement('span');
    message.textContent = '当前没有可用的配对会话';
    frame.append(message);
    return;
  }
  const canvas = document.createElement('canvas');
  canvas.setAttribute('aria-label', '配对二维码');
  frame.append(canvas);
  if (!window.QRCode) {
    frame.replaceChildren(Object.assign(document.createElement('span'), { textContent: '二维码组件加载失败' }));
    return;
  }
  try {
    await window.QRCode.toCanvas(canvas, value, { errorCorrectionLevel: 'M', margin: 1, width: 250, color: { dark: '#181c24', light: '#ffffff' } });
  } catch {
    frame.replaceChildren(Object.assign(document.createElement('span'), { textContent: '二维码生成失败' }));
  }
}

async function renderPairing() {
  const pairing = state.pairing || {};
  const enabled = Boolean(pairing.enabled ?? pairing.Enabled);
  const url = pairing.url ?? pairing.Url ?? '';
  const expiresAt = pairing.expiresAt ?? pairing.ExpiresAt;
  $('#pair-state').textContent = enabled ? '等待扫码配对' : '远程配对未开启';
  $('#pair-detail').textContent = enabled ? 'Host 已签发一次性配对会话。' : '请先在 Host 配置中启用远程服务。';
  $('#pair-badge').textContent = enabled ? '已开启' : '未开启';
  $('#pair-badge').className = `state-badge ${enabled ? 'online' : 'error'}`;
  $('#pair-status-dot').className = enabled ? 'online' : 'error';
  $('#pair-expiry').textContent = enabled && expiresAt
    ? `该配对会话将在 ${new Intl.DateTimeFormat('zh-CN', { timeStyle: 'medium' }).format(new Date(expiresAt))} 失效`
    : '未生成可用的配对地址';
  await renderPairingQr(enabled ? url : '');
  const devices = Array.isArray(state.devices) ? state.devices : [];
  $('#pair-device-total').textContent = `${devices.length} 台`;
  const list = $('#pair-device-list');
  list.replaceChildren();
  if (!devices.length) { list.innerHTML = '<div class="table-placeholder">暂无已配对设备</div>'; return; }
  for (const device of devices.slice(0, 4)) {
    const item = document.createElement('div');
    item.className = 'compact-device';
    const dot = document.createElement('i');
    const details = document.createElement('div');
    const name = document.createElement('b');
    name.textContent = deviceName(device);
    const activity = document.createElement('span');
    activity.textContent = text(device.lastSeenAt || device.lastActiveAt || device.updatedAt);
    details.append(name, activity);
    item.append(dot, details);
    list.append(item);
  }
}

function flattenDiagnostics(value, prefix = '') {
  if (value === null || value === undefined || typeof value !== 'object') return [[prefix || '状态', text(value)]];
  const output = [];
  for (const [key, item] of Object.entries(value)) {
    const title = prefix ? `${prefix} · ${key}` : key;
    if (item && typeof item === 'object' && !Array.isArray(item)) output.push(...flattenDiagnostics(item, title));
    else output.push([title, Array.isArray(item) ? item.join(', ') : text(item)]);
  }
  return output;
}

function diagnosticLabel(value) {
  return String(value).replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('.', ' / ');
}
function diagnosticTone(value) {
  const normalized = String(value).toLowerCase();
  if (normalized === 'online') return 'online';
  if (normalized === 'offline' || normalized === 'error') return 'error';
  if (normalized === 'disabled') return 'neutral';
  return 'starting';
}

function renderDiagnostics() {
  const entries = flattenDiagnostics(state.diagnostics);
  const stateEntries = entries.filter(([label]) => /(^| · )state$/i.test(label));
  const services = $('#diagnostics-services'); services.replaceChildren();
  const serviceEntries = stateEntries.filter(([, value]) => String(value).toLowerCase() !== 'disabled');
  $('#diagnostics-count').textContent = `${serviceEntries.length} 项`;
  $('#diagnostics-count').className = `state-badge ${serviceEntries.length ? 'online' : 'error'}`;
  $('#diagnostics-summary-text').textContent = serviceEntries.length ? `已确认 ${serviceEntries.length} 项服务正在运行。` : '当前没有可用的服务状态。';
  if (!serviceEntries.length) { services.innerHTML = '<div class="table-placeholder">Host 未返回正在运行的服务</div>'; return; }
  for (const [label, value] of serviceEntries) {
    const row = document.createElement('div'); row.className = 'service-row';
    const name = document.createElement('strong'); name.textContent = diagnosticLabel(label);
    const status = document.createElement('span'); status.className = `service-status ${diagnosticTone(value)}`; status.textContent = text(value);
    row.append(name, status); services.append(row);
  }
}

async function refresh() {
  if (!accountState?.activated) { await refreshAccount(); if (!accountState?.activated) return; }
  $('#refresh-button').disabled = true;
  try {
    const [status, devices, diagnostics, projects, discoveredProjects, pairing] = await Promise.all([
      window.codexBridge.getStatus(),
      window.codexBridge.getDevices(),
      window.codexBridge.getDiagnostics(),
      window.codexBridge.getProjects(),
      window.codexBridge.getDiscoveredProjects(),
      window.codexBridge.getPairing()
    ]);
    state.status = status;
    state.devices = devices;
    state.diagnostics = diagnostics;
    state.projects = projects;
    state.discoveredProjects = discoveredProjects;
    state.pairing = pairing;
    renderStatus();
    renderDevices();
    renderDiagnostics();
    renderProjects();
    await renderPairing();
    $('#updated-at').textContent = `更新于 ${formatTime()}`;
    setConnection(true, 'Host 已连接');
  } catch (error) {
    state.status = null;
    renderStatus();
    setConnection(false, 'Host 连接失败');
    toast(`无法读取本机 Host：${error.message}`);
  } finally {
    $('#refresh-button').disabled = false;
  }
}

function stopPageRefreshTimers() {
  if (pairingRefreshTimer) { window.clearInterval(pairingRefreshTimer); pairingRefreshTimer = null; }
  if (accountRefreshTimer) { window.clearInterval(accountRefreshTimer); accountRefreshTimer = null; }
}

function startPageRefreshTimer(page) {
  stopPageRefreshTimers();
  if (page === 'pair') {
    pairingRefreshTimer = window.setInterval(() => { if (activePage === 'pair' && accountState?.activated) refresh(); }, 60 * 1000);
  } else if (page === 'account') {
    accountRefreshTimer = window.setInterval(() => { if (activePage === 'account' && accountState?.activated) refreshAccountProfile(); }, 5 * 60 * 1000);
  }
}

function navigate(page) {
  activePage = page;
  document.querySelectorAll('.nav-item').forEach(item => item.classList.toggle('active', item.dataset.page === page));
  document.querySelectorAll('.page').forEach(item => item.classList.toggle('active', item.dataset.page === page));
  startPageRefreshTimer(page);
  if (page === 'pair' && accountState?.activated) refresh();
  if (page === 'account' && accountState?.activated) refreshAccountProfile();
}

function renderAccountProfile(profile) {
  $('#account-email').textContent = profile.email || '--';
  $('#account-status').textContent = profile.emailVerified ? '邮箱已验证 · 账号正常' : '邮箱未验证';
  $('#account-created-at').textContent = profile.createdAt ? new Date(profile.createdAt).toLocaleString('zh-CN') : '--';
  $('#account-verified').textContent = profile.emailVerified ? '已验证' : '未验证';
  $('#account-device-id').textContent = accountState?.deviceId || '--';
  const list = $('#account-license-list'); list.replaceChildren();
  const licenses = profile.licenses || [];
  $('#account-license-summary').textContent = `${licenses.length} 台电脑授权`;
  if (!licenses.length) { list.innerHTML = '<div class="table-placeholder">暂无电脑授权</div>'; return; }
  for (const item of licenses) {
    const row = document.createElement('div'); row.className = 'account-license-row';
    const host = document.createElement('span'); host.textContent = `${item.hostId} / ${item.deviceId}`;
    const plan = document.createElement('span'); plan.textContent = String(item.plan || '').toUpperCase();
    const expires = document.createElement('span'); expires.textContent = item.expiresAt ? new Date(item.expiresAt).toLocaleDateString('zh-CN') : '--';
    const seen = document.createElement('span'); seen.textContent = item.lastSeenAt ? new Date(item.lastSeenAt).toLocaleString('zh-CN') : '暂无记录';
    const status = document.createElement('span'); status.className = `state-badge ${item.revoked ? 'error' : new Date(item.expiresAt) > new Date() ? 'online' : 'error'}`; status.textContent = item.revoked ? '已撤销' : new Date(item.expiresAt) > new Date() ? '有效' : '已到期';
    row.append(host, plan, expires, seen, status); list.append(row);
  }
}

async function refreshAccountProfile() {
  try { renderAccountProfile(await window.codexBridge.accountProfile()); }
  catch (error) { $('#account-status').textContent = error.message; }
}

document.querySelectorAll('.nav-item').forEach(item => item.addEventListener('click', () => navigate(item.dataset.page)));
$('#account-refresh').addEventListener('click', refreshAccountProfile);
$('#account-logout').addEventListener('click', async () => { $('#account-logout').disabled = true; try { await window.codexBridge.accountLogout(); accountState = null; showAuth('', 'login'); } catch (error) { toast(`退出登录失败：${error.message}`); } finally { $('#account-logout').disabled = false; } });
$('#refresh-button').addEventListener('click', refresh);
$('#diag-button').addEventListener('click', () => navigate('diagnostics'));
$('#diagnostics-refresh').addEventListener('click', refresh);
$('#pair-refresh').addEventListener('click', refresh);
$('#projects-search').addEventListener('input', renderProjects);
$('#devices-search').addEventListener('input', renderDevices);
$('#revoke-cancel').addEventListener('click', closeRevoke);
$('#revoke-modal').addEventListener('click', event => { if (event.target === event.currentTarget) closeRevoke(); });
$('#revoke-confirm').addEventListener('click', async () => {
  const target = state.revokeTarget; if (!target) return;
  const button = $('#revoke-confirm'); button.disabled = true;
  try { await window.codexBridge.revokeDevice(deviceTransport(target), deviceId(target)); state.selectedDevice = null; closeRevoke(); toast('设备授权已撤销'); await refresh(); }
  catch (error) { toast(`撤销失败：${error.message}`); }
  finally { button.disabled = false; }
});
$('#auth-show-register').addEventListener('click', () => showAuth('', 'register'));
$('#auth-show-reset').addEventListener('click', () => showAuth('', 'reset'));
document.querySelectorAll('[data-auth-back]').forEach(button => button.addEventListener('click', () => showAuth('', button.dataset.authBack)));
$('#auth-register').addEventListener('click', () => authAction('register'));
$('#auth-verify').addEventListener('click', () => authAction('verify'));
$('#auth-login').addEventListener('click', () => authAction('login'));
$('#auth-activate').addEventListener('click', () => authAction('activate'));
$('#auth-reset-request').addEventListener('click', () => authAction('reset-request'));
$('#auth-reset-confirm').addEventListener('click', () => authAction('reset-confirm'));
window.codexBridge.onRefresh(refresh);
window.codexBridge.onHostFailed(() => {
  setConnection(false, 'Host 已停止，自动恢复失败');
  toast('本机 Host 已停止，请点击“刷新”重试。');
});
refreshAccount().then(() => { navigate(activePage); return refresh(); });
