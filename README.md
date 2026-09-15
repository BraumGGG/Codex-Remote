# Codex Remote

Codex Remote 是一个开源桥接项目，让 Android 手机可以通过经过身份验证的 HTTPS、WebSocket 信令和 WebRTC，查看并控制官方 Codex Desktop 会话。

Windows 客户端采用 Electron，并运行本机 .NET Host。Host 只读取用户明确授权的项目和会话。已配对设备默认只能查看；发送能力由服务端签发的授权控制，并且始终针对真实的 thread UUID。

## 项目组成

- `src/CodexBridge.Desktop`：Electron Windows 客户端。
- `src/CodexBridge.Host`、`src/CodexBridge.Windows`：本机 Host 与 Codex 数据集成。
- `src/CodexBridge.Signal`、`src/CodexBridge.Transport`：信令与 WebRTC 传输。
- `src/CodexBridge.Entitlement.Server`：可选的账号与授权 API。
- `android/`：Android 客户端。
- `tests/`：.NET、Go 以及浏览器端测试。

## 开发环境

需要安装：.NET 8 SDK、Node.js/npm、Go，以及 Android 开发所需的 Android SDK。Electron 客户端和 Windows 集成需要 Windows 系统。

```powershell
dotnet restore CodexBridge.sln
dotnet test CodexBridge.sln --no-restore
node --check src/CodexBridge.Desktop/main/index.js
node --check src/CodexBridge.Desktop/renderer/app.js
```

安全脚本使用 fake sender 和隔离状态进行测试。请勿将测试指向真实 Codex 会话。

## 自建服务

请阅读 [部署说明](docs/deployment.md)。该文档只描述通用部署方式，不包含任何生产基础设施或凭据。部署者必须通过私有配置提供所有环境参数。

## 安全问题

请不要在公开 Issue 中报告安全漏洞。请通过仓库维护者提供的私密渠道报告，并且不要附带凭据或生产日志。

## 许可证

本项目采用 [Apache License 2.0](LICENSE) 开源。
