# Codex Remote

Codex Remote is an open-source bridge for viewing and controlling an official
Codex Desktop session from an Android device over authenticated HTTPS,
WebSocket signaling, and WebRTC.

The Windows client is Electron and runs a local .NET Host. The Host reads only
explicitly authorized projects and sessions. Read-only access is available to
paired devices; sending is guarded by server-issued entitlement and always
targets a real thread UUID.

## Components

- `src/CodexBridge.Desktop`: Electron Windows client.
- `src/CodexBridge.Host`, `src/CodexBridge.Windows`: local Host integration.
- `src/CodexBridge.Signal`, `src/CodexBridge.Transport`: signaling and transport.
- `src/CodexBridge.Entitlement.Server`: optional account and entitlement API.
- `android/`: Android client.
- `tests/`: .NET, Go, and browser-facing tests.

## Development

Requirements: .NET 8 SDK, Node.js/npm, Go, and (for Android) the Android SDK.
Windows is required for the Electron client and Windows integration.

```powershell
dotnet restore CodexBridge.sln
dotnet test CodexBridge.sln --no-restore
node --check src/CodexBridge.Desktop/main/index.js
node --check src/CodexBridge.Desktop/renderer/app.js
```

Safety scripts use fake senders and isolated state. Never point them at a real
Codex session for testing.

## Self-hosting

See [docs/deployment.md](docs/deployment.md). It is intentionally generic and
contains no production infrastructure or credentials. Operators must provide
all deployment values through private configuration.

## Security

Do not report vulnerabilities in public issues. Use a private security channel
provided by the repository maintainers and never include credentials or
production logs.

## License

Licensed under the [Apache License 2.0](LICENSE).
