import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");

const pairingUrl = process.env.CODEX_BRIDGE_PUBLIC_PAIRING_URL;
if (!pairingUrl?.startsWith("https://remote.example.invalid:8443/remote/#pair="))
  throw new Error("CODEX_BRIDGE_PUBLIC_PAIRING_URL is required");

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));

  await page.goto(pairingUrl, { waitUntil: "domcontentloaded", timeout: 30_000 });
  await page.locator("#workspace-view").waitFor({ state: "visible", timeout: 60_000 });
  const first = await readWorkspace(page);

  await page.reload({ waitUntil: "domcontentloaded", timeout: 30_000 });
  await page.locator("#workspace-view").waitFor({ state: "visible", timeout: 60_000 });
  const reconnect = await readWorkspace(page);

  for (const result of [first, reconnect]) {
    if (result.tier !== "只读" || result.projects !== 1 || result.threads !== 2) {
      throw new Error(`unexpected workspace: ${JSON.stringify(result)}`);
    }
  }
  if (errors.length) throw new Error(`browser errors: ${errors.join(" | ")}`);
  process.stdout.write(`${JSON.stringify({ first, reconnect })}\n`);
} finally {
  await browser.close();
}

async function readWorkspace(page) {
  await page.locator(".project-heading").first().click();
  await page.locator(".thread-button").first().waitFor();
  return page.evaluate(() => ({
    tier: document.querySelector("#tier-badge")?.textContent,
    projects: document.querySelectorAll(".project-group").length,
    threads: document.querySelectorAll(".thread-button").length,
    hostState: document.querySelector("#host-state")?.textContent,
  }));
}
