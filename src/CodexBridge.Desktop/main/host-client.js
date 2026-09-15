const { randomUUID } = require('node:crypto');
const { spawn } = require('node:child_process');
const http = require('node:http');
const path = require('node:path');
const fs = require('node:fs');

const HOST_URL = 'http://127.0.0.1:5096';
const TOKEN_HEADER = 'X-CodexBridge-Management-Token';
const NONCE_HEADER = 'X-CodexBridge-Management-Nonce';

class HostClient {
  constructor({ hostExecutable, hostArguments = [], token, log }) {
    this.hostExecutable = hostExecutable;
    this.hostArguments = hostArguments;
    this.token = token;
    this.process = null;
    this.onExit = null;
    this.log = typeof log === 'function' ? log : () => {};
  }

  async start() {
    if (await this.isReady()) {
      try {
        await this.getDiagnostics();
        return;
      } catch {
        throw new Error('127.0.0.1:5096 已被其他 Host 占用，且不接受当前 Electron 会话的管理令牌。请先退出旧版 Codex Remote/Host 后重试。');
      }
    }
    // Packaged Electron launches the Host directly, so it does not pass through
    // scripts/start-host.ps1 where the remote defaults are normally provided.
    // Keep these defaults here to ensure pairing is enabled in installed builds.
    const environment = {
      ...process.env,
      CODEX_BRIDGE_MANAGEMENT_TOKEN: this.token,
      CODEX_BRIDGE_REMOTE_ENABLED: process.env.CODEX_BRIDGE_REMOTE_ENABLED || '1',
      CODEX_BRIDGE_SIGNAL_URL: process.env.CODEX_BRIDGE_SIGNAL_URL || 'wss://remote.example.invalid:8443/signal',
      CODEX_BRIDGE_REMOTE_APP_URL: process.env.CODEX_BRIDGE_REMOTE_APP_URL || 'https://remote.example.invalid:8443/remote/',
      CODEX_BRIDGE_ENTITLEMENT_URL: process.env.CODEX_BRIDGE_ENTITLEMENT_URL || 'https://remote.example.invalid:8443/',
    };
    this.log('info', 'host_starting', { executable: this.hostExecutable, arguments: this.hostArguments });
    this.process = spawn(this.hostExecutable, this.hostArguments, {
      cwd: path.dirname(this.hostExecutable),
      env: environment,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe']
    });
    this.process.stdout.setEncoding('utf8');
    this.process.stderr.setEncoding('utf8');
    this.process.stdout.on('data', chunk => this.log('info', 'host_stdout', { message: String(chunk).trim() }));
    this.process.stderr.on('data', chunk => this.log('error', 'host_stderr', { message: String(chunk).trim() }));
    this.process.on('error', error => this.log('error', 'host_spawn_failed', { message: error.message }));
    this.process.once('exit', (code, signal) => {
      this.log(code === 0 ? 'info' : 'error', 'host_exited', { code, signal });
      this.process = null;
      this.onExit?.({ code, signal });
    });
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
      if (await this.isReady()) return;
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    throw new Error('本机 Host 在 15 秒内未就绪。');
  }

  async isReady() {
    try { await this.request('/api/status'); return true; } catch { return false; }
  }

  request(route, { method = 'GET', body, management = false } = {}) {
    return new Promise((resolve, reject) => {
      const headers = {};
      if (body !== undefined) headers['Content-Type'] = 'application/json';
      if (management) {
        headers[TOKEN_HEADER] = this.token;
        headers[NONCE_HEADER] = randomUUID();
      }
      const request = http.request(HOST_URL + route, { method, headers, timeout: 10000 }, response => {
        let raw = '';
        response.setEncoding('utf8');
        response.on('data', chunk => { raw += chunk; });
        response.on('end', () => {
          const data = raw ? tryJson(raw) : null;
          if (response.statusCode < 200 || response.statusCode >= 300) {
            this.log('warn', 'host_api_failed', { route, method, statusCode: response.statusCode, error: data?.error || null });
            reject(new Error(data?.error || `Host 请求失败 (${response.statusCode})`));
            return;
          }
          resolve(data);
        });
      });
      request.on('timeout', () => request.destroy(new Error('本机 Host 请求超时。')));
      request.on('error', error => {
        this.log('warn', 'host_api_error', { route, method, message: error.message });
        reject(error);
      });
      if (body !== undefined) request.write(JSON.stringify(body));
      request.end();
    });
  }

  getStatus() { return this.request('/api/status'); }
  getDevices() { return this.request('/api/management/devices', { management: true }); }
  getDiagnostics() { return this.request('/api/management/diagnostics', { management: true }); }
  getProjects() { return this.request('/api/management/projects', { management: true }); }
  getDiscoveredProjects() { return this.request('/api/management/project-discovery', { management: true }); }
  updateProjects(paths, permissions) { return this.request('/api/management/projects', { method: 'PUT', body: { paths, permissions }, management: true }); }
  getPairing() { return this.request('/api/management/pairing', { management: true }); }
  updateDevicePermissions(transport, deviceId, canSend) {
    return this.request(`/api/management/devices/${encodeURIComponent(transport)}/${encodeURIComponent(deviceId)}/permissions`, {
      method: 'PUT', body: { canSend }, management: true,
    });
  }
  revokeDevice(transport, deviceId) {
    return this.request(`/api/management/devices/${encodeURIComponent(transport)}/${encodeURIComponent(deviceId)}`, { method: 'DELETE', management: true });
  }

  stop() {
    if (this.process && !this.process.killed) this.process.kill();
    this.process = null;
  }
}

function tryJson(value) {
  try { return JSON.parse(value); } catch { return { message: value }; }
}

module.exports = { HostClient };
