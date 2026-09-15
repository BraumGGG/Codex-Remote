import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const file = path.join(root, "src", "CodexBridge.Host", "wwwroot", "ui-routes.js");
const server = createServer(async (_request, response) => {
  response.writeHead(200, { "Content-Type": "text/javascript", "Cache-Control": "no-store" });
  response.end(await readFile(file));
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  const result = await page.evaluate(async () => {
    const { resolveUiRoute, renderStaticRoute } = await import("/ui-routes.js?test");
    const paths = ["/remote/connect", "/remote/sessions", "/remote/chat/thread-1", "/remote/settings", "/desktop/overview", "/desktop/pairing", "/desktop/projects", "/desktop/devices", "/desktop/diagnostics", "/cloud/admin", "/install", "/design"];
    const routes = paths.map((path) => resolveUiRoute(path));
    const host = document.createElement("div");
    renderStaticRoute(host, resolveUiRoute("/desktop/diagnostics"));
    document.body.append(host);
    return { routes, hasHeading: Boolean(host.querySelector("h1")), cards: host.querySelectorAll(".design-card").length };
  });
  if (result.routes.length !== 12 || result.routes[2].threadId !== "thread-1" || result.routes[11].group !== "design" || !result.hasHeading || result.cards !== 3) {
    throw new Error(JSON.stringify(result));
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
