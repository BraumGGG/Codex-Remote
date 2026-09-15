const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

class EntitlementClient {
  constructor({ baseUrl, statePath, log }) { this.baseUrl = baseUrl.replace(/\/$/, ''); this.statePath = statePath; this.log = log || (() => {}); this.state = this.load(); }
  load() { try { return JSON.parse(fs.readFileSync(this.statePath, 'utf8')); } catch { return { deviceId: randomUUID() }; } }
  save() { fs.mkdirSync(path.dirname(this.statePath), { recursive: true }); fs.writeFileSync(this.statePath, JSON.stringify(this.state), 'utf8'); }
  async request(route, body) { const response = await fetch(this.baseUrl + route, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }); const data = await response.json().catch(() => ({})); if (!response.ok) { const messages = { account_exists: '该邮箱已经注册，请直接登录。', email_unverified: '邮箱尚未验证，请先完成邮箱验证。', login_invalid: '邮箱或密码错误。', verification_invalid: '邮箱验证码无效或已过期。', invalid_email: '请输入有效的邮箱地址。', invalid_password: '密码长度必须为 8 到 128 位。', reset_invalid: '密码重置验证码无效或已过期。', session_invalid: '登录状态已失效，请重新登录。', invite_unavailable: '激活码不可用、已过期或已被使用。', host_limit_reached: '该激活码已绑定其他电脑。', invalid_binding: '本机标识读取失败，请刷新客户端后重试。' }; throw new Error(messages[data.error] || data.error || `授权服务请求失败 (${response.status})`); } return data; }
  status() { return { activated: Boolean(this.state.entitlementToken), email: this.state.email || null, deviceId: this.state.deviceId }; }
  register(email, password) { return this.request('/api/v1/accounts/register', { email, password }); }
  verify(email, code) { return this.request('/api/v1/accounts/verify', { email, code }); }
  requestPasswordReset(email) { return this.request('/api/v1/accounts/password-reset/request', { email }); }
  resetPassword(email, code, newPassword) { return this.request('/api/v1/accounts/password-reset/confirm', { email, code, newPassword }); }
  async login(email, password) { const result = await this.request('/api/v1/accounts/login', { email, password }); this.state = { ...this.state, ...result, email }; this.save(); return this.status(); }
  async activate(code, hostId) { if (!this.state.sessionToken) throw new Error('请先登录账号。'); const result = await this.request('/api/v1/accounts/activate', { sessionToken: this.state.sessionToken, code, hostId, deviceId: this.state.deviceId, email: this.state.email }); this.state = { ...this.state, entitlementToken: result.entitlementToken, licenseId: result.licenseId }; this.save(); return this.status(); }
  async profile() { if (!this.state.sessionToken) throw new Error('请先登录账号。'); return this.request('/api/v1/accounts/profile', { sessionToken: this.state.sessionToken }); }
  async logout() { if (this.state.sessionToken) { try { await this.request('/api/v1/accounts/logout', { sessionToken: this.state.sessionToken }); } catch {} } this.state = { deviceId: this.state.deviceId }; this.save(); return this.status(); }
  async heartbeat(hostId, version) { if (!this.state.sessionToken || !this.state.entitlementToken) return; try { await this.request('/api/v1/accounts/heartbeat', { sessionToken: this.state.sessionToken, hostId, deviceId: this.state.deviceId, client: 'desktop', version }); } catch (error) { this.log('warn', 'entitlement_heartbeat_failed', { message: error.message }); } }
}
module.exports = { EntitlementClient };
