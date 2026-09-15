import { createServer } from "node:http";
import { readFile, mkdir } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const output = path.join(root, "docs", "acceptance", "screenshots", "m4-entitlements");
await mkdir(output, { recursive: true });
const fakeConfig = `
class FakeTransport extends EventTarget {
  async connect() { this.dispatchEvent(new CustomEvent("statechange", { detail: "online" })); }
  status() { return Promise.resolve({ desktopOnline: true }); }
  capabilities() {
    const expired = new URLSearchParams(location.search).get("expired") === "1";
    return Promise.resolve(expired
      ? { canSend: false, entitlementState: "expired", expiresAt: "2026-08-16T00:00:00Z" }
      : { canSend: true, entitlementState: "offlinegrace", expiresAt: "2026-08-17T00:00:00Z" });
  }
  listProjects() { return Promise.resolve([{ id: "p1", name: "授权验收", threadCount: 1, canSend: true }]); }
  listThreads() { return Promise.resolve({ items: [{ id: "t1", title: "授权状态", preview: "Host 最终裁决", status: "idle" }], nextCursor: null, hasMore: false, totalApproximate: 1 }); }
  getEvents() { return Promise.resolve({ items: [{ sequence: 1, kind: "AgentMessage", text: "免费只读始终可用", timestamp: "2026-08-17T00:00:00Z" }], previousCursor: null, hasMoreBefore: false, latestSequence: 1 }); }
  subscribe() { return Promise.resolve({ close() {} }); }
  submitMessage() { window.__submitCalls = (window.__submitCalls || 0) + 1; return Promise.resolve({}); }
  disconnect() {}
}
export async function createConfiguredTransport() { return { mode: "remote", transport: new FakeTransport() }; }
`;
const mime = new Map([[".html", "text/html"], [".css", "text/css"], [".js", "text/javascript"], [".mjs", "text/javascript"]]);
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, "http://localhost");
    const name = url.pathname.endsWith("/") ? "index.html" : path.basename(url.pathname);
    if (name === "config.js") { response.writeHead(200, { "Content-Type": "text/javascript" }); return response.end(fakeConfig); }
    const relative = url.pathname.includes("/vendor/") ? path.join("vendor", name) : name;
    response.writeHead(200, { "Content-Type": mime.get(path.extname(relative)) || "application/octet-stream" });
    response.end(await readFile(path.join(webRoot, relative)));
  } catch { if (!response.headersSent) response.writeHead(404); response.end(); }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}/remote/`;
const browser = await chromium.launch({ channel: "chrome", headless: true });

try {
  const grace = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await grace.goto(base);
  await grace.locator("#tier-badge").filter({ hasText: "Pro 离线" }).waitFor();
  await grace.locator(".project-heading").click();
  await grace.locator(".thread-button").click();
  if (!await grace.locator("#message-form").isVisible()) throw new Error("offline grace should retain signed Pro capability");
  await grace.screenshot({ path: path.join(output, "offline-grace-390x844.png"), fullPage: true });
  await grace.close();

  const expired = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await expired.goto(`${base}?expired=1&canSend=true`);
  await expired.locator("#tier-badge").filter({ hasText: "只读" }).waitFor();
  await expired.locator(".project-heading").click();
  await expired.locator(".thread-button").click();
  const state = await expired.evaluate(() => ({
    formVisible: !document.querySelector("#message-form").hidden,
    readonlyVisible: !document.querySelector("#readonly-bar").hidden,
    calls: window.__submitCalls || 0,
  }));
  if (state.formVisible || !state.readonlyVisible || state.calls !== 0) throw new Error(`expired UI unsafe: ${JSON.stringify(state)}`);
  await expired.screenshot({ path: path.join(output, "expired-readonly-390x844.png"), fullPage: true });
  await expired.close();
  process.stdout.write(JSON.stringify({ offlineGrace: true, expiredReadOnly: true, queryTamperIgnored: true, screenshots: 2 }) + "\n");
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
