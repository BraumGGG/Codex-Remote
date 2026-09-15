const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('codexBridge', Object.freeze({
  getStatus: () => ipcRenderer.invoke('host:status'),
  getDevices: () => ipcRenderer.invoke('host:devices'),
  getDiagnostics: () => ipcRenderer.invoke('host:diagnostics'),
  getProjects: () => ipcRenderer.invoke('host:projects'),
  getDiscoveredProjects: () => ipcRenderer.invoke('host:discovered-projects'),
  updateProjects: (paths, permissions) => ipcRenderer.invoke('host:update-projects', paths, permissions),
  getPairing: () => ipcRenderer.invoke('host:pairing'),
  updateDevicePermissions: (transport, deviceId, canSend) => ipcRenderer.invoke('host:update-device-permissions', transport, deviceId, canSend),
  revokeDevice: (transport, deviceId) => ipcRenderer.invoke('host:revoke-device', transport, deviceId),
  accountStatus: () => ipcRenderer.invoke('account:status'),
  accountRegister: (email, password) => ipcRenderer.invoke('account:register', email, password),
  accountVerify: (email, code) => ipcRenderer.invoke('account:verify', email, code),
  accountRequestPasswordReset: email => ipcRenderer.invoke('account:password-reset-request', email),
  accountResetPassword: (email, code, newPassword) => ipcRenderer.invoke('account:password-reset-confirm', email, code, newPassword),
  accountLogin: (email, password) => ipcRenderer.invoke('account:login', email, password),
  accountActivate: code => ipcRenderer.invoke('account:activate', code),
  accountProfile: () => ipcRenderer.invoke('account:profile'),
  accountLogout: () => ipcRenderer.invoke('account:logout'),
  onRefresh: callback => {
    const listener = () => callback();
    ipcRenderer.on('app:refresh', listener);
    return () => ipcRenderer.removeListener('app:refresh', listener);
  },
  onHostFailed: callback => {
    const listener = () => callback();
    ipcRenderer.on('app:host-failed', listener);
    return () => ipcRenderer.removeListener('app:host-failed', listener);
  },
  onNavigate: callback => {
    const listener = (_, page) => callback(page);
    ipcRenderer.on('app:navigate', listener);
    return () => ipcRenderer.removeListener('app:navigate', listener);
  }
}));
