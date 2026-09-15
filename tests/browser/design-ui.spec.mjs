import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const contentTypes = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".txt": "text/plain" };
const server = createServer(async (request, response) => {
  const pathname = new URL(request.url, "http://localhost").pathname;
  const relative = pathname === "/" || !path.extname(pathname) ? "index.html" : pathname.slice(1);
  try {
    const body = await readFile(path.join(webRoot, relative));
    response.writeHead(200, { "Content-Type": contentTypes[path.extname(relative)] || "application/octet-stream", "Cache-Control": "no-store" });
    response.end(body);
  } catch {
    response.writeHead(404).end();
  }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const browser = await chromium.launch({ channel: "chrome", headless: true });
const port = server.address().port;
try {
  const checks = [];
  for (const viewport of [{ width: 360, height: 740 }, { width: 390, height: 844 }, { width: 1440, height: 900 }]) {
    const page = await browser.newPage({ viewport });
    page.on("pageerror", (error) => console.error("PAGEERROR", error.message));
    for (const route of ["/remote/connect", "/remote/sessions", "/desktop/overview", "/desktop/diagnostics", "/cloud/admin", "/install"]) {
      await page.goto(`http://127.0.0.1:${port}${route}`, { waitUntil: "domcontentloaded" });
      await page.waitForTimeout(250);
      const result = await page.evaluate(async () => ({
        route: location.pathname,
        moduleRoute: (await import("/ui-routes.js?probe")).resolveUiRoute(),
        title: document.querySelector("#route-view:not([hidden]) h1, #pair-view:not([hidden]) h1, #conversation-name")?.textContent || "",
        routeVisible: !document.querySelector("#route-view").hidden,
        routeText: document.querySelector("#route-view")?.textContent.trim().slice(0, 40) || "",
        overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
        visible: [...document.querySelectorAll("main, #route-view, #pair-view, #workspace-view")].some((node) => !node.hidden && getComputedStyle(node).display !== "none"),
      }));
      if (result.overflow || !result.visible) throw new Error(`${viewport.width} ${route}: ${JSON.stringify(result)}`);
      checks.push({ viewport: viewport.width, ...result });
    }
    await page.close();
  }
  process.stdout.write(`${JSON.stringify({ checks: checks.length, checks })}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
