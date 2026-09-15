import { createServer } from "node:http";
import { readFile, mkdir } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const output = path.join(root, "docs", "acceptance", "screenshots", "m2-android");
await mkdir(output, { recursive: true });

const fakeConfig = `
class FakeTransport extends EventTarget {
  async connect() {
    if (new URLSearchParams(location.search).get("failure") === "1") throw new Error("signal_timeout");
    this.dispatchEvent(new CustomEvent("statechange", { detail: "signaling" }));
    this.dispatchEvent(new CustomEvent("statechange", { detail: "connecting" }));
    this.dispatchEvent(new CustomEvent("statechange", { detail: "online" }));
  }
  status() { return Promise.resolve({ desktopOnline: true }); }
  capabilities() { return Promise.resolve({ canSend: false }); }
  listProjects() { return Promise.resolve([{ id: "p1", name: "测试会话", threadCount: 2 }]); }
  listThreads() { return Promise.resolve({ items: [
    { id: "t1", title: "测试会话1", preview: "Android 公网只读验收", status: "running" },
    { id: "t2", title: "测试会话2", preview: "文件预览与实时输出", status: "idle" },
  ], nextCursor: null, hasMore: false, totalApproximate: 2 }); }
  getEvents() { return Promise.resolve({ items: [
    { sequence: 1, kind: "UserMessage", text: "检查公网连接和移动端布局", timestamp: "2026-08-17T02:00:00Z", images: [] },
    { sequence: 2, kind: "AgentMessage", text: "连接正常。当前设备保持免费只读权限，输出会实时同步。", timestamp: "2026-08-17T02:00:04Z", images: [{ id: "img1" }], files: [{ id: "f1", name: "验收记录.md", kind: "markdown" }] },
    { sequence: 3, kind: "TaskCompleted", timestamp: "2026-08-17T02:00:05Z" },
  ], previousCursor: null, hasMoreBefore: false, latestSequence: 3 }); }
  getImage() { return Promise.resolve(new Blob(['<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360" viewBox="0 0 640 360"><rect width="640" height="360" fill="#111827"/><rect x="32" y="32" width="576" height="296" rx="8" fill="#f8fafc"/><circle cx="108" cy="108" r="36" fill="#2563eb"/><path d="M84 196h472v84H84z" fill="#dbeafe"/><text x="164" y="120" font-family="sans-serif" font-size="28" fill="#111827">Codex Bridge</text><text x="108" y="247" font-family="sans-serif" font-size="24" fill="#1e3a8a">公网图片预览验收</text></svg>'], { type: "image/svg+xml" })); }
  getTextFile() { return Promise.resolve({ name: "验收记录.md", kind: "markdown", content: "# M2 验收记录\\n\\n- 公网加密通道\\n- 免费设备只读\\n- 文件可在线预览" }); }
  subscribe() { return Promise.resolve({ close() {} }); }
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
    const file = path.join(webRoot, relative);
    const content = await readFile(file);
    response.writeHead(200, { "Content-Type": mime.get(path.extname(file)) || "application/octet-stream", "Cache-Control": "no-store" });
    response.end(content);
  } catch {
    if (!response.headersSent) response.writeHead(404);
    response.end();
  }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}/remote/`;
const browser = await chromium.launch({ channel: "chrome", headless: true });

async function assertNoOverflow(page, label) {
  const result = await page.evaluate(() => ({
    body: document.body.scrollWidth - document.documentElement.clientWidth,
    clipped: [...document.querySelectorAll("button, h1, .thread-title, .file-name")].filter((element) => element.scrollWidth > element.clientWidth + 1).map((element) => element.textContent),
    lanText: document.body.innerText.includes("局域网") || document.body.innerText.includes("配对码"),
    sendVisible: !document.querySelector("#message-form").hidden,
    readonlyVisible: !document.querySelector("#readonly-bar").hidden,
  }));
  if (result.body > 1 || result.clipped.length || result.lanText || result.sendVisible || !result.readonlyVisible) {
    throw new Error(`${label} failed: ${JSON.stringify(result)}`);
  }
}

try {
  for (const viewport of [{ width: 390, height: 844 }, { width: 412, height: 915 }, { width: 844, height: 390 }]) {
    const page = await browser.newPage({ viewport });
    await page.goto(base);
    await page.locator(".project-heading").first().click();
    await page.locator(".thread-button").first().click();
    await page.locator(".message.agent").waitFor();
    await assertNoOverflow(page, `${viewport.width}x${viewport.height}`);
    await page.screenshot({ path: path.join(output, `workspace-${viewport.width}x${viewport.height}.png`), fullPage: true });
    await page.locator(".file-button").click();
    await page.locator("#file-preview-markdown:not([hidden])").waitFor();
    await page.screenshot({ path: path.join(output, `preview-${viewport.width}x${viewport.height}.png`), fullPage: true });
    await page.close();
  }

  const largeText = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await largeText.goto(base);
  await largeText.locator(".project-heading").first().click();
  await largeText.locator(".thread-button").first().click();
  await largeText.addStyleTag({ content: "body { zoom: 1.3; }" });
  await assertNoOverflow(largeText, "large-text");
  await largeText.screenshot({ path: path.join(output, "workspace-large-text.png"), fullPage: true });
  await largeText.close();

  const failure = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await failure.goto(`${base}?failure=1`);
  await failure.getByText("连接信令服务器超时", { exact: false }).waitFor();
  await failure.locator("#retry-button:visible").waitFor();
  await failure.screenshot({ path: path.join(output, "connection-failure.png"), fullPage: true });
  await failure.close();
  process.stdout.write(JSON.stringify({ viewports: 3, largeText: true, failureRecovery: true, lanRemoved: true, freeReadOnly: true }) + "\n");
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
