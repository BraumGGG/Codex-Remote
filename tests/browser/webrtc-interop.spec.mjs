import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import path from "node:path";
import process from "node:process";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const goExe = process.env.GO_EXE || path.join(root, ".tools", "go", "bin", "go.exe");
const transportRoot = path.join(root, "src", "CodexBridge.Transport");
const webRoot = path.join(root, "tests", "browser");

function startInterop() {
  return new Promise((resolve, reject) => {
    const child = spawn(goExe, ["run", "./cmd/interop", "--web-root", webRoot], {
      cwd: transportRoot,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    let stderr = "";
    const timeout = setTimeout(() => reject(new Error(`interop startup timed out: ${stderr}`)), 60_000);
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
  const page = await browser.newPage();
  await page.goto(interop.url);
  await page.waitForFunction(() => window.__interopResult?.state === "complete", null, {
    timeout: 30_000,
  });
  const result = await page.evaluate(() => window.__interopResult);
  if (result.received !== "pion->browser") {
    throw new Error(`unexpected DataChannel response: ${JSON.stringify(result)}`);
  }
  if (!result.candidatePair) {
    throw new Error(`selected candidate pair was not reported: ${JSON.stringify(result)}`);
  }

  const serverResult = await (await page.request.get(new URL("/result", interop.url).href)).json();
  if (!serverResult.channelOpen || !serverResult.messageSeen) {
    throw new Error(`Pion did not observe the channel exchange: ${JSON.stringify(serverResult)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
  await page.request.post(new URL("/shutdown", interop.url).href);
} finally {
  await browser.close();
  await new Promise((resolve) => {
    if (interop.child.exitCode !== null) return resolve();
    const timeout = setTimeout(() => {
      interop.child.kill();
      resolve();
    }, 5_000);
    interop.child.once("exit", () => {
      clearTimeout(timeout);
      resolve();
    });
  });
}
