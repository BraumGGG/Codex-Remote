import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");

const fakeConfig = `
const metrics = { projectCalls: 0, threadCalls: [], eventCalls: [], eventTextCalls: 0, recoverCalls: 0 };
window.__largeMetrics = metrics;
class FakeTransport extends EventTarget {
  constructor() { super(); this.state = "disconnected"; this.failNextEvent = false; window.__largeTransport = this; }
  async connect() { this.state = "online"; this.dispatchEvent(new CustomEvent("statechange", { detail: "online" })); }
  async recoverFromStaleConnection() { metrics.recoverCalls++; await this.connect(); }
  status() { return Promise.resolve({ desktopOnline: true }); }
  capabilities() { return Promise.resolve({ canSend: false }); }
  listProjects() {
    metrics.projectCalls++;
    return Promise.resolve(Array.from({ length: 100 }, (_, index) => ({
      id: "project-" + index, name: "项目 " + index, threadCount: 10000,
      lastActivityAtMs: 100000 - index, runningThreadCount: 0, indexState: "ready",
    })));
  }
  listThreads(projectId, cursor, pageSize, query) {
    metrics.threadCalls.push({ projectId, cursor, pageSize, query });
    const offset = cursor ? Number(cursor) : 0;
    const items = Array.from({ length: pageSize }, (_, index) => {
      const value = offset + index;
      return { id: projectId + "-thread-" + value, title: (query || "会话") + " " + value,
        preview: "大型项目移动端分页预览 " + value, updatedAtMs: 100000 - value, status: "idle" };
    });
    const next = offset + pageSize;
    return Promise.resolve({ items, nextCursor: next < 10000 ? String(next) : null,
      hasMore: next < 10000, totalApproximate: 10000 });
  }
  getEvents(threadId, beforeCursor) {
    metrics.eventCalls.push({ threadId, beforeCursor });
    if (this.failNextEvent) {
      this.failNextEvent = false;
      this.state = "reconnecting";
      this.dispatchEvent(new CustomEvent("statechange", { detail: "reconnecting" }));
      return Promise.reject({ code: "request_timeout" });
    }
    const before = beforeCursor ? Number(beforeCursor) : 100001;
    const start = Math.max(1, before - 40);
    const items = Array.from({ length: before - start }, (_, index) => {
      const sequence = start + index;
      return { sequence, kind: sequence % 9 === 0 ? "UserMessage" : "AgentMessage",
        text: sequence === 100000 ? null : "事件 " + sequence,
        textPreview: sequence === 100000 ? "长消息预览" : null,
        textContentId: sequence === 100000 ? "content-100000" : null,
        textLength: sequence === 100000 ? 100000 : 8,
        timestamp: "2026-08-22T00:00:00Z", images: [], files: [] };
    });
    return Promise.resolve({ items, previousCursor: start > 1 ? String(start) : null,
      hasMoreBefore: start > 1, latestSequence: 100000 });
  }
  getEventText() { metrics.eventTextCalls++; return Promise.resolve("完整正文" + "x".repeat(100000)); }
  subscribe(threadId, afterSequence, onEvent) { window.__emitLargeEvent = onEvent; return Promise.resolve({ close() {} }); }
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
  await page.locator(".project-heading").nth(99).waitFor();
  const startup = await page.evaluate(() => ({ ...window.__largeMetrics }));
  if (startup.projectCalls !== 1 || startup.threadCalls.length !== 0) {
    throw new Error(`startup eagerly loaded threads: ${JSON.stringify(startup)}`);
  }

  await page.locator(".project-heading").first().click();
  await page.locator(".thread-button").nth(9).waitFor();
  await page.locator(".thread-more:visible").click();
  await page.locator(".thread-button").nth(19).waitFor();
  await page.locator(".thread-more:visible").click();
  await page.locator(".thread-button").nth(29).waitFor();
  await page.locator(".thread-search:visible").fill("needle");
  await page.waitForTimeout(350);
  await page.locator(".thread-button").first().waitFor();
  const afterSearch = await page.evaluate(() => window.__largeMetrics.threadCalls);
  if (afterSearch.length !== 4 || afterSearch.at(-1).query !== "needle") {
    throw new Error(`thread paging/search contract failed: ${JSON.stringify(afterSearch)}`);
  }

  await page.locator(".thread-button").first().click();
  await page.locator(".message").first().waitFor();
  await page.locator(".message-expand").click();
  await page.waitForFunction(() => [...document.querySelectorAll(".message-body")]
    .some((element) => element.textContent?.startsWith("完整正文")));
  for (let index = 0; index < 3; index++) {
    await page.locator(".history-more").click();
    await page.waitForFunction(() => !document.querySelector(".history-more")?.disabled);
  }
  const history = await page.evaluate(() => ({
    rendered: document.querySelectorAll(".message, .task-event").length,
    bodyOverflow: document.body.scrollWidth - document.documentElement.clientWidth,
    eventCalls: window.__largeMetrics.eventCalls.length,
  }));
  if (history.rendered > 120 || history.bodyOverflow > 1 || history.eventCalls !== 4) {
    throw new Error(`history virtualization failed: ${JSON.stringify(history)}`);
  }

  await page.locator("#message-list").evaluate((element) => { element.scrollTop = 0; });
  await page.evaluate(() => window.__emitLargeEvent({ sequence: 100001, kind: "AgentMessage",
    text: "实时新增", timestamp: "2026-08-22T00:01:00Z", images: [], files: [] }));
  if (!await page.locator("#jump-latest").isVisible()) throw new Error("jump-to-latest was not shown");
  await page.locator("#jump-latest").click();
  if (await page.locator("#jump-latest").isVisible()) throw new Error("jump-to-latest did not dismiss");

  const final = await page.evaluate(() => ({
    eventTextCalls: window.__largeMetrics.eventTextCalls,
    rendered: document.querySelectorAll(".message, .task-event").length,
  }));
  if (final.eventTextCalls !== 1 || final.rendered > 120) {
    throw new Error(`event text expansion failed: ${JSON.stringify(final)}`);
  }

  await page.evaluate(() => { window.__largeTransport.failNextEvent = true; });
  await page.locator("#refresh-button").click();
  await page.locator(".message").first().waitFor();
  const recovered = await page.evaluate(() => ({
    recoverCalls: window.__largeMetrics.recoverCalls,
    state: window.__largeTransport.state,
    selectedTitle: document.querySelector("#conversation-name")?.textContent,
    hasError: Boolean(document.querySelector(".conversation-load-state.error")),
  }));
  if (recovered.recoverCalls !== 1 || recovered.state !== "online" || !recovered.selectedTitle || recovered.hasError) {
    throw new Error(`conversation retry did not wait for recovery: ${JSON.stringify(recovered)}`);
  }
  process.stdout.write(JSON.stringify({ startupSummaryOnly: true, threads: 10000, projects: 100,
    historyEvents: 100000, rendered: final.rendered, eventText: true, networkRecovery: true }) + "\n");
  await page.close();
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
