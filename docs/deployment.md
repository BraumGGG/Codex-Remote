# Self-hosted deployment

This document describes a generic deployment. It intentionally contains no
production hostname, IP address, SSH target, certificate, account, release
identifier, or secret from any project instance.

## Topology

Run the following components on infrastructure you control:

- `CodexBridge.Signal`: WebSocket signaling service.
- `CodexBridge.Entitlement.Server`: optional account and entitlement API.
- Caddy or another reverse proxy for HTTPS and WebSocket forwarding.
- coturn for TURN relay when direct WebRTC connectivity is unavailable.
- A Windows Electron client running the local .NET Host.

Use a placeholder domain such as `remote.example.invalid` while adapting the
configuration. Replace it only in your private deployment files.

## Secrets and configuration

Never commit real values. Supply these through the service manager, a secret
store, or an untracked environment file:

- TLS certificate and private key
- Signal signing keys
- Entitlement signing keys
- TURN username/password or shared secret
- Administrator token
- SMTP credentials
- Database or data-directory credentials

Start from the `*.example` files in `deploy/` and keep private overrides
outside Git. The repository must remain usable without access to any author's
infrastructure.

## Reverse proxy

Configure your proxy to forward:

- `/signal` to the Signal service with WebSocket upgrade support.
- `/api/` to the entitlement service when enabled.
- `/remote/` and static assets to the web host.

Terminate TLS at the proxy using a certificate issued for your own domain.
Do not reuse certificates, keys, or host-specific snippets from another
installation.

## Services

Build the solution with the .NET 8 SDK and build the Go transport sidecar from
`src/CodexBridge.Transport`. Run each service under a dedicated non-root user,
restrict file permissions, and configure restart and health checks with your
service manager. Keep service data outside the Git checkout.

## TURN

Install coturn according to its documentation, then copy
`deploy/webrtc/turnserver.conf.example` to a private configuration path. Set
your own realm, listener addresses, certificate paths, and authentication
secret. Open only the ports required by your network policy.

## Windows client

Build the Electron client locally using the scripts under `scripts/`. Configure
the public Signal and API endpoints through the client environment/configuration
mechanism. Do not bake private signing keys or administrator credentials into
the client.

## Verification checklist

1. Verify that every public URL points to your own domain.
2. Verify TLS certificate names and renewal.
3. Verify WebSocket upgrade and WebRTC/TURN connectivity.
4. Verify that secrets are injected at runtime and absent from Git.
5. Run the repository safety scripts and tests before exposing the service.
6. Keep backups, logs, monitoring, and incident response outside this repo.

This project provides software, not an operated service. You are responsible
for securing and maintaining any deployment you create.
