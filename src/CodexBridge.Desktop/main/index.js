const { app, BrowserWindow, Tray, Menu, ipcMain, nativeImage, dialog, screen } = require('electron');
const { randomBytes } = require('node:crypto');
const path = require('node:path');
const fs = require('node:fs');
const { HostClient } = require('./host-client');
const { EntitlementClient } = require('./entitlement-client');

if (process.platform === 'win32') app.setAppUserModelId('com.braumg.codexremote');

const singleInstance = app.requestSingleInstanceLock();
if (!singleInstance) app.quit();

let mainWindow;
let tray;
let hostClient;
let exiting = false;
let hostRecoveryTimer;
let hostRecoveryInFlight = false;
let hostRecoveryAttempts = 0;
let logFile;
let entitlementClient;

function pairingHostId(pairing) {
  if (pairing?.hostId || pairing?.HostId) return pairing.hostId || pairing.HostId;
  const url = pairing?.url || pairing?.Url;
  try {
    const encoded = new URL(url).hash.match(/^#pair=([^&]+)$/)?.[1];
    if (!encoded) return null;
    const json = Buffer.from(encoded, 'base64url').toString('utf8');
    const payload = JSON.parse(json);
    return typeof payload.hostId === 'string' ? payload.hostId : null;
  } catch { return null; }
}

function initializeLogging() {
  const directory = path.join(app.getPath('userData'), 'logs');
  fs.mkdirSync(directory, { recursive: true });
  logFile = path.join(directory, 'desktop.log');
  try {
    if (fs.existsSync(logFile) && fs.statSync(logFile).size > 5 * 1024 * 1024) {
      fs.renameSync(logFile, path.join(directory, `desktop-${Date.now()}.log`));
    }
  } catch {
    // Logging must never prevent the desktop client from starting.
  }
}

function log(level, event, details = {}) {
  const entry = JSON.stringify({ timestamp: new Date().toISOString(), level, event, ...details });
  try { fs.appendFileSync(logFile, `${entry}\n`, 'utf8'); } catch { /* best effort */ }
}

function createTrayIcon() {
  const iconPath = path.join(__dirname, '../assets/tray-icon.png');
  return nativeImage.createFromPath(iconPath).resize({ width: 32, height: 32 });
}

function hostExecutable() {
  const override = process.env.CODEX_BRIDGE_HOST_EXECUTABLE;
  if (override) return override;
  if (app.isPackaged) return path.join(process.resourcesPath, 'host', 'CodexBridge.Host.exe');
  return path.resolve(__dirname, '../../CodexBridge.Host/bin/Debug/net8.0-windows/CodexBridge.Host.exe');
}

function hostLaunchSpec() {
  const executable = hostExecutable();
  if (app.isPackaged) return { executable, args: [] };
  const projectRoot = path.resolve(__dirname, '../../..');
  const dotnet = path.join(projectRoot, '.tools/dotnet/dotnet.exe');
  const dll = path.join(projectRoot, 'src/CodexBridge.Host/bin/Debug/net8.0-windows/CodexBridge.Host.dll');
  if (fs.existsSync(dotnet) && fs.existsSync(dll)) return { executable: dotnet, args: [dll] };
  if (fs.existsSync(executable)) return { executable, args: [] };
  return { executable, args: [] };
}

function createWindow() {
  // Electron exposes work-area dimensions in device-independent pixels.  Constrain
  // the initial window to that area so 125%/150% Windows scaling never starts
  // with the right or bottom edge outside the visible desktop.
  const display = screen.getPrimaryDisplay();
  const { width: workWidth, height: workHeight } = display.workAreaSize;
  // Let Chromium inherit the Windows DPI scale. BrowserWindow dimensions are
  // already device-independent pixels, so forcing a scale factor makes the
  // whole client visibly too small at 125% and 150% system scaling.
  const width = Math.max(900, Math.min(1280, workWidth - 48));
  const height = Math.max(640, Math.min(820, workHeight - 48));
  mainWindow = new BrowserWindow({
    width,
    height,
    minWidth: 900,
    minHeight: 640,
    resizable: true,
    maximizable: true,
    minimizable: true,
    show: false,
    backgroundColor: '#F6F7F9',
    title: 'CR',
    icon: path.join(__dirname, '../assets/cr-icon.ico'),
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });
  mainWindow.setPosition(
    Math.max(display.workArea.x, display.workArea.x + Math.round((workWidth - width) / 2)),
    Math.max(display.workArea.y, display.workArea.y + Math.round((workHeight - height) / 2))
  );
  mainWindow.webContents.setZoomFactor(1);
  mainWindow.webContents.setVisualZoomLevelLimits(1, 1).catch(() => {});
  mainWindow.loadFile(path.join(__dirname, '../renderer/index.html'));
  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('close', event => {
    if (!exiting) { event.preventDefault(); mainWindow.hide(); }
  });
}

function createTray() {
  tray = new Tray(createTrayIcon());
  tray.setToolTip('Codex Remote · Host 已连接');
  tray.on('click', showWindow);
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Codex Remote', enabled: false },
    { label: 'Host 已连接', enabled: false },
    { type: 'separator' },
    { label: '打开主窗口', click: showWindow },
    { label: '打开诊断', click: () => { showWindow(); mainWindow?.webContents.send('app:navigate', 'diagnostics'); } },
    { label: '刷新状态', click: () => mainWindow?.webContents.send('app:refresh') },
    { type: 'separator' },
    { label: '退出 Codex Remote', click: () => { exiting = true; app.quit(); } }
  ]));
}

function showWindow() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  const bounds = mainWindow.getBounds();
  const display = screen.getDisplayMatching(bounds);
  const area = display.workArea;
  const visible = bounds.right > area.x && bounds.x < area.x + area.width &&
    bounds.bottom > area.y && bounds.y < area.y + area.height;
  if (!visible || bounds.x < area.x - bounds.width + 32 || bounds.y < area.y - bounds.height + 32) {
    mainWindow.setPosition(
      area.x + Math.max(0, Math.round((area.width - bounds.width) / 2)),
      area.y + Math.max(0, Math.round((area.height - bounds.height) / 2))
    );
  }
  mainWindow.show();
  mainWindow.focus();
}

function requireHost() {
  if (!hostClient) throw new Error('本机 Host 尚未启动。');
  return hostClient;
}

async function captureVerificationPages() {
  const captureDirectory = process.env.CODEX_BRIDGE_CAPTURE_DIR;
  if (!captureDirectory || !mainWindow || mainWindow.isDestroyed()) return;
  fs.mkdirSync(captureDirectory, { recursive: true });
  const pages = ['overview', 'pair', 'projects', 'devices', 'diagnostics'];
  for (const page of pages) {
    await mainWindow.webContents.executeJavaScript(`
      document.querySelector('[data-page="${page}"]')?.click();
      new Promise(resolve => setTimeout(resolve, 300));
    `);
    const image = await mainWindow.webContents.capturePage();
    fs.writeFileSync(path.join(captureDirectory, `electron-${page}.png`), image.toPNG());
  }
}

app.whenReady().then(async () => {
  initializeLogging();
  log('info', 'desktop_starting', { version: app.getVersion(), packaged: app.isPackaged });
  Menu.setApplicationMenu(null);
  const launch = hostLaunchSpec();
  if (!fs.existsSync(launch.executable)) throw new Error(`未找到 .NET Host：${launch.executable}`);
  hostClient = new HostClient({
    hostExecutable: launch.executable,
    hostArguments: launch.args,
    token: randomBytes(32).toString('base64'),
    log
  });
  entitlementClient = new EntitlementClient({
    baseUrl: process.env.CODEX_BRIDGE_ENTITLEMENT_URL || 'https://remote.example.invalid:8443',
    statePath: path.join(app.getPath('userData'), 'entitlement.json'),
    log
  });
  hostClient.onExit = ({ code, signal }) => {
    log('warn', 'host_exit_observed', { code, signal });
    if (exiting || hostRecoveryTimer) return;
    hostRecoveryAttempts = 0;
    const recover = async () => {
      if (exiting || hostRecoveryInFlight || hostClient.process) return;
      if (hostRecoveryAttempts >= 3) {
        mainWindow?.webContents.send('app:host-failed');
        hostRecoveryTimer = null;
        return;
      }
      hostRecoveryInFlight = true;
      hostRecoveryAttempts += 1;
      try {
        await hostClient.start();
        mainWindow?.webContents.send('app:refresh');
        hostRecoveryTimer = null;
      } catch (error) {
        log('warn', 'host_recovery_failed', { attempt: hostRecoveryAttempts, message: error.message });
        hostRecoveryInFlight = false;
        hostRecoveryTimer = setTimeout(recover, 1500 * hostRecoveryAttempts);
      } finally {
        hostRecoveryInFlight = false;
      }
    };
    hostRecoveryTimer = setTimeout(recover, 1000);
  };
  await hostClient.start();
  log('info', 'host_ready');
  ipcMain.handle('host:status', () => requireHost().getStatus());
  ipcMain.handle('host:devices', () => requireHost().getDevices());
  ipcMain.handle('host:diagnostics', () => requireHost().getDiagnostics());
  ipcMain.handle('host:projects', () => requireHost().getProjects());
  ipcMain.handle('host:discovered-projects', () => requireHost().getDiscoveredProjects());
  ipcMain.handle('host:update-projects', (_, paths, permissions) => requireHost().updateProjects(paths, permissions));
  ipcMain.handle('host:pairing', () => requireHost().getPairing());
  ipcMain.handle('host:update-device-permissions', (_, transport, deviceId, canSend) =>
    requireHost().updateDevicePermissions(transport, deviceId, canSend));
  ipcMain.handle('host:revoke-device', (_, transport, deviceId) => requireHost().revokeDevice(transport, deviceId));
  ipcMain.handle('account:status', () => entitlementClient.status());
  ipcMain.handle('account:register', (_, email, password) => entitlementClient.register(email, password));
  ipcMain.handle('account:verify', (_, email, code) => entitlementClient.verify(email, code));
  ipcMain.handle('account:password-reset-request', (_, email) => entitlementClient.requestPasswordReset(email));
  ipcMain.handle('account:password-reset-confirm', (_, email, code, newPassword) => entitlementClient.resetPassword(email, code, newPassword));
  ipcMain.handle('account:login', (_, email, password) => entitlementClient.login(email, password));
  ipcMain.handle('account:activate', async (_, code) => {
    const pairing = await requireHost().getPairing();
    const hostId = pairingHostId(pairing);
    if (!hostId) throw new Error('无法读取本机 Host 标识，请先刷新配对状态后重试。');
    return entitlementClient.activate(code, hostId);
  });
  ipcMain.handle('account:profile', () => entitlementClient.profile());
  ipcMain.handle('account:logout', () => entitlementClient.logout());
  const sendHeartbeat = async () => {
    try { const status = entitlementClient.status(); if (!status.activated) return; const pairing = await requireHost().getPairing(); const hostId = pairingHostId(pairing); if (hostId) await entitlementClient.heartbeat(hostId, app.getVersion()); } catch (error) { log('warn', 'entitlement_heartbeat_error', { message: error.message }); }
  };
  setInterval(sendHeartbeat, 60_000);
  sendHeartbeat();
  createWindow();
  createTray();
  mainWindow.once('ready-to-show', () => {
    setTimeout(() => captureVerificationPages().catch(() => {}), 800);
  });
}).catch(error => {
  log('error', 'desktop_start_failed', { message: error.message, stack: error.stack });
  dialog.showErrorBox('Codex Remote 无法启动', error.message);
  app.quit();
});

app.on('second-instance', showWindow);
app.on('window-all-closed', () => {});
app.on('before-quit', () => { exiting = true; hostClient?.stop(); });
