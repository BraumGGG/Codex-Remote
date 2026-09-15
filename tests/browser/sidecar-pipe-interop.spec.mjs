import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const dotnet = path.join(root, ".tools", "dotnet", "dotnet.exe");
const harnessDll = path.join(
  root, "tests", "CodexBridge.TransportHarness", "bin", "Release", "net8.0-windows",
  "CodexBridge.TransportHarness.dll",
);

function startHarness() {
  return new Promise((resolve, reject) => {
    const args = [harnessDll, root];
    if (process.env.CODEX_BRIDGE_TRANSPORT_EXE) args.push(process.env.CODEX_BRIDGE_TRANSPORT_EXE);
    const child = spawn(dotnet, args, {
      cwd: root,
      windowsHide: true,
      env: { ...process.env, DOTNET_CLI_HOME: path.join(root, ".dotnet-home") },
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stderr = "";
    const timeout = setTimeout(() => reject(new Error(`harness startup timed out: ${stderr}`)), 30_000);
    child.stderr.on("data", (chunk) => { stderr += chunk.toString(); });
    child.stdout.on("data", (chunk) => {
      const match = chunk.toString().match(/HARNESS_URL=(http:\/\/[^\s]+)/);
      if (!match) return;
      clearTimeout(timeout);
      resolve({ child, url: match[1], stderr: () => stderr });
    });
    child.once("exit", (code) => {
      clearTimeout(timeout);
      if (code !== 0) reject(new Error(`harness exited with ${code}: ${stderr}`));
    });
  });
}

const harness = await startHarness();
const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const page = await browser.newPage();
  await page.addInitScript(({ urls, username, credential, hostUsername, hostCredential }) => {
    globalThis.__turnUrls = urls;
    globalThis.__turnUsername = username;
    globalThis.__turnCredential = credential;
    globalThis.__hostTurnUsername = hostUsername;
    globalThis.__hostTurnCredential = hostCredential;
  }, {
    urls: process.env.TURN_CHECK_URLS || "[]",
    username: process.env.TURN_CHECK_USERNAME || "",
    credential: process.env.TURN_CHECK_CREDENTIAL || "",
    hostUsername: process.env.TURN_CHECK_USERNAME_2 || process.env.TURN_CHECK_USERNAME || "",
    hostCredential: process.env.TURN_CHECK_CREDENTIAL_2 || process.env.TURN_CHECK_CREDENTIAL || "",
  });
  await page.goto(harness.url);
  let result;
  try {
    result = await page.evaluate(async () => {
    const withTimeout = (promise, stage, milliseconds = 15_000) => Promise.race([
      promise,
      new Promise((_, reject) => setTimeout(() => reject(new Error(`${stage} timed out`)), milliseconds)),
    ]);
    const turnUrls = JSON.parse(globalThis.__turnUrls || "[]");
    const iceServers = turnUrls.length ? [{
      urls: turnUrls,
      username: globalThis.__turnUsername,
      credential: globalThis.__turnCredential,
    }] : undefined;
    const peer = new RTCPeerConnection({
      iceServers,
      iceTransportPolicy: turnUrls.length ? "relay" : "all",
    });
    const channel = peer.createDataChannel("codex-bridge-v1");
    channel.binaryType = "arraybuffer";
    const opened = new Promise((resolve, reject) => {
      channel.addEventListener("open", resolve, { once: true });
      channel.addEventListener("error", reject, { once: true });
    });
    await peer.setLocalDescription(await peer.createOffer());
    if (peer.iceGatheringState !== "complete") {
      await withTimeout(new Promise((resolve) => peer.addEventListener("icegatheringstatechange", () => {
        if (peer.iceGatheringState === "complete") resolve();
      })), "ice gathering");
    }
    const answer = await withTimeout(fetch("/offer", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        type: peer.localDescription.type,
        sdp: peer.localDescription.sdp,
        iceServers: turnUrls.length ? [{
          urls: turnUrls,
          username: globalThis.__hostTurnUsername,
          credential: globalThis.__hostTurnCredential,
        }] : undefined,
      }),
    }), "offer request", 45_000);
    if (!answer.ok) throw new Error(`offer rejected: ${answer.status}`);
    await peer.setRemoteDescription(await answer.json());
    await withTimeout(opened, "DataChannel open");

    const requestId = crypto.getRandomValues(new Uint8Array(16));
    requestId[6] = (requestId[6] & 0x0f) | 0x40;
    requestId[8] = (requestId[8] & 0x3f) | 0x80;
    const payload = new TextEncoder().encode("browser-pipe-dotnet");
    const frame = new Uint8Array(32 + payload.length);
    const view = new DataView(frame.buffer);
    frame[0] = 1;
    frame[1] = 1;
    frame.set(requestId, 4);
    view.setBigInt64(20, 7n, false);
    view.setInt32(28, payload.length, false);
    frame.set(payload, 32);
    const responsePromise = new Promise((resolve) => channel.addEventListener("message", resolve, { once: true }));
    channel.send(frame);
    const response = new Uint8Array((await withTimeout(responsePromise, "echo response")).data);
    return {
      kind: response[1],
      sequence: new DataView(response.buffer).getBigInt64(20, false).toString(),
      payload: new TextDecoder().decode(response.slice(32)),
    };
    });
  } catch (error) {
    throw new Error(`${error.message}; harness=${harness.stderr()}`);
  }
  if (result.kind !== 2 || result.sequence !== "7" || result.payload !== "browser-pipe-dotnet") {
    throw new Error(`end-to-end frame mismatch: ${JSON.stringify(result)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
  await page.request.post(new URL("/shutdown", harness.url).href);
} finally {
  await browser.close();
  await new Promise((resolve) => {
    if (harness.child.exitCode !== null) return resolve();
    const timeout = setTimeout(() => {
      harness.child.kill();
      resolve();
    }, 5_000);
    harness.child.once("exit", () => {
      clearTimeout(timeout);
      resolve();
    });
  });
}
