import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const goExe = process.env.GO_EXE || path.join(root, ".tools", "go", "bin", "go.exe");
const transportRoot = path.join(root, "src", "CodexBridge.Transport");
const webRoot = path.join(root, "tests", "browser");
const tailscaleIp = process.env.TAILSCALE_IP;
if (!tailscaleIp) throw new Error("TAILSCALE_IP is required for the isolated diagnostic test.");

function startInterop() {
  return new Promise((resolve, reject) => {
    const child = spawn(goExe, ["run", "./cmd/interop", "--web-root", webRoot,
      "--listen-address", `${tailscaleIp}:0`], {
      cwd: transportRoot,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
      env: { ...process.env, GOMODCACHE: path.join(root, ".tools", "go-mod-cache"),
        GOPATH: path.join(root, ".tools", "go") },
    });
    let stderr = "";
    const timeout = setTimeout(() => reject(new Error(`interop startup timed out: ${stderr}`)), 30_000);
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.stdout.on("data", (chunk) => {
      const match = chunk.toString().match(/INTEROP_URL=(http:\/\/[^\s]+)/);
      if (!match) return;
      clearTimeout(timeout);
      resolve({ child, url: match[1], stderr: () => stderr });
    });
    child.once("exit", (code) => {
      clearTimeout(timeout);
      if (code !== 0) reject(new Error(`interop exited with ${code}: ${stderr}`));
    });
  });
}

const interop = await startInterop();
const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  let page = await browser.newPage();
  const results = [];
  for (let generation = 1; generation <= 3; generation++) {
    await page.goto(`${interop.url}?generation=${generation}`);
    await page.waitForFunction(() => window.__interopResult?.state === "complete", null, { timeout: 30_000 });
    const result = await page.evaluate(() => window.__interopResult);
    if (result.received !== "pion->browser" || !result.candidatePair) {
      throw new Error(`Tailscale direct ICE generation ${generation} failed: ${JSON.stringify(result)}`);
    }
    results.push({ generation, ...result });
    await page.close();
    if (generation < 3) {
      const next = await browser.newPage();
      page = next;
    }
  }
  process.stdout.write(`${JSON.stringify({ state: "complete", generations: results })}\n`);
  const cleanupPage = await browser.newPage();
  await cleanupPage.request.post(new URL("/shutdown", interop.url).href);
  await cleanupPage.close();
} finally {
  await browser.close();
  await new Promise((resolve) => {
    if (interop.child.exitCode !== null) return resolve();
    const timeout = setTimeout(() => { interop.child.kill(); resolve(); }, 5_000);
    interop.child.once("exit", () => { clearTimeout(timeout); resolve(); });
  });
}
