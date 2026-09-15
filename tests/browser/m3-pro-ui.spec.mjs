import { createServer } from "node:http";
import { readFile, mkdir } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const output = path.join(root, "docs", "acceptance", "screenshots", "m3-pro");
await mkdir(output, { recursive: true });

const fakeConfig = `
class FakeTransport extends EventTarget {
  async connect() { this.dispatchEvent(new CustomEvent("statechange", { detail: "online" })); }
  status() { return Promise.resolve({ desktopOnline: true }); }
  capabilities() { return Promise.resolve({ canSend: true }); }
  listProjects() { return Promise.resolve([{ id: "p1", name: "隔离测试项目", threadCount: 1, canSend: true }]); }
  listThreads() { return Promise.resolve({ items: [{ id: "t1", title: "隔离 Pro 会话", preview: "安全发送验收", status: "idle" }], nextCursor: null, hasMore: false, totalApproximate: 1 }); }
  getEvents() { return Promise.resolve({ items: [{ sequence: 1, kind: "AgentMessage", text: "隔离环境已就绪", timestamp: "2026-08-17T03:00:00Z" }], previousCursor: null, hasMoreBefore: false, latestSequence: 1 }); }
  subscribe() { return Promise.resolve({ close() {} }); }
  submitMessage(threadId, text) {
    window.__submitCalls = (window.__submitCalls || 0) + 1;
    if (new URLSearchParams(location.search).get("unknown") === "1") {
      return Promise.reject({ code: "send_result_unknown" });
    }
    return new Promise((resolve) => { window.__resolveSubmit = resolve; });
  }
  disconnect() {}
}
export async function createConfiguredTransport() { return { mode: "remote", transport: new FakeTransport() }; }
`;

const mime = new Map([[".html", "text/html"], [".css", "text/css"], [".js", "text/javascript"], [".mjs", "text/javascript"]]);
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, "http://localhost");
    const name = url.pathname.endsWith("/") ? "index.html" : path.basename(url.pathname);
    if (name === "config.js") {
      response.writeHead(200, { "Content-Type": "text/javascript", "Cache-Control": "no-store" });
      return response.end(fakeConfig);
    }
    const relative = url.pathname.includes("/vendor/") ? path.join("vendor", name) : name;
    const content = await readFile(path.join(webRoot, relative));
    response.writeHead(200, { "Content-Type": mime.get(path.extname(relative)) || "application/octet-stream", "Cache-Control": "no-store" });
    response.end(content);
  } catch {
    if (!response.headersSent) response.writeHead(404);
    response.end();
  }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}/remote/`;
const browser = await chromium.launch({ channel: "chrome", headless: true });

try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await page.goto(base);
  await page.locator(".project-heading").first().click();
  await page.locator(".thread-button").first().click();
  await page.locator("#message-form:visible").waitFor();
  if (await page.locator("#readonly-bar").isVisible()) throw new Error("Pro device still shows read-only bar");
  await page.locator("#message-input").fill("isolated pro command");
  await page.locator("#send-button").click();
  if (!await page.locator("#send-button").isDisabled()) throw new Error("send button was not disabled");
  await page.locator("#send-button").click({ force: true });
  const during = await page.evaluate(() => ({ calls: window.__submitCalls, pending: document.querySelectorAll(".message.pending").length }));
  if (during.calls !== 1 || during.pending !== 1) throw new Error(`duplicate submit was not blocked: ${JSON.stringify(during)}`);
  await page.evaluate(() => window.__resolveSubmit({ state: "completed" }));
  await page.locator("#composer-status").filter({ hasText: "已提交到 Desktop" }).waitFor();
  await page.screenshot({ path: path.join(output, "pro-submitted-390x844.png"), fullPage: true });
  await page.close();

  const unknown = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await unknown.goto(`${base}?unknown=1`);
  await unknown.locator(".project-heading").first().click();
  await unknown.locator(".thread-button").first().click();
  await unknown.locator("#message-input").fill("do not retry automatically");
  await unknown.locator("#send-button").click();
  await unknown.locator("#composer-status").filter({ hasText: "发送结果未知" }).waitFor();
  const unknownState = await unknown.evaluate(() => ({
    calls: window.__submitCalls,
    value: document.querySelector("#message-input").value,
    pending: document.querySelectorAll(".message.pending").length,
    status: document.querySelector("#composer-status").textContent,
  }));
  if (unknownState.calls !== 1 || unknownState.value !== "do not retry automatically" || unknownState.pending !== 0 || !unknownState.status.includes("Desktop")) {
    throw new Error(`unknown result UI is unsafe: ${JSON.stringify(unknownState)}`);
  }
  await unknown.screenshot({ path: path.join(output, "pro-result-unknown-390x844.png"), fullPage: true });
  await unknown.close();
  process.stdout.write(JSON.stringify({ proComposer: true, duplicateBlocked: true, unknownResultSafe: true, screenshots: 2 }) + "\n");
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
