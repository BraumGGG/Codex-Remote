import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const allowed = new Set(["remote-key-store.js", "signal-auth.js", "remote-peer-connector.js"]);
const server = createServer(async (request, response) => {
  const name = new URL(request.url, "http://localhost").pathname.slice(1);
  if (!name) return response.writeHead(200, { "Content-Type": "text/html" }).end("<!doctype html><title>connector</title>");
  if (!allowed.has(name)) return response.writeHead(404).end();
  response.writeHead(200, { "Content-Type": "text/javascript" });
  response.end(await readFile(path.join(webRoot, name)));
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const baseUrl = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ channel: "chrome", headless: true });

try {
  const page = await browser.newPage();
  await page.goto(baseUrl);
  const result = await page.evaluate(async (base) => {
    const keys = await import(`${base}/remote-key-store.js`);
    await keys.clearDeviceIdentityForTests();
    const host = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, false, ["sign", "verify"]);
    const hostSpki = keys.base64UrlEncode(new Uint8Array(await crypto.subtle.exportKey("spki", host.publicKey)));
    const hostId = keys.base64UrlEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", keys.base64UrlDecode(hostSpki))));
    const pair = keys.base64UrlEncode(new TextEncoder().encode(JSON.stringify({
      version: 1, signalUrl: "wss://signal.test/signal", hostId, hostPublicKeySpki: hostSpki,
      routeId: "route_1", secret: keys.base64UrlEncode(new Uint8Array(16).fill(7)),
    })));
    history.replaceState(null, "", `/#pair=${pair}`);
    let pairingCount = 0;
    let ticketCount = 0;
    let deviceProofCount = 0;

    class FakeSocket extends EventTarget {
      constructor() { super(); this.readyState = 1; queueMicrotask(() => this.dispatchEvent(new Event("open"))); }
      send(text) { this.handle(JSON.parse(text)); }
      close() { this.readyState = 3; }
      emit(value) { this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify(value) })); }
      async handle(message) {
        if (message.type === "pairing-client") { pairingCount++; return queueMicrotask(() => this.emit({ type: "authenticated" })); }
        if (message.type === "client-auth") {
          ticketCount++;
          return queueMicrotask(() => this.emit({ type: "device-challenge", challenge: keys.base64UrlEncode(new Uint8Array(32).fill(3)) }));
        }
        if (message.type === "device-auth") { deviceProofCount++; return queueMicrotask(() => this.emit({ type: "authenticated" })); }
        if (message.type === "turn-request") return queueMicrotask(() => this.emit({ type: "turn-credentials", urls: ["turn:test"], username: "u", credential: "c" }));
        if (message.type === "pairing-offer" || message.type === "offer") {
          const offerSdp = message.type === "offer" ? message.offerSdp :
            JSON.parse(new TextDecoder().decode(keys.base64UrlDecode(message.pairingPayloadBase64Url))).offerSdp;
          const hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(offerSdp))),
            (byte) => byte.toString(16).padStart(2, "0")).join("");
          const payload = new TextEncoder().encode(JSON.stringify({ type: "answer", answerSdp: "answer-sdp", offerSha256: hash }));
          const signature = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, host.privateKey, payload));
          const emitAnswer = () => this.emit({
            type: "answer",
            signed: { payloadBase64Url: keys.base64UrlEncode(payload), signatureBase64Url: keys.base64UrlEncode(signature) },
            ticket: { payloadBase64Url: "ticket", signatureBase64Url: "signature", hostPublicKeySpki: hostSpki },
          });
          if (message.type === "offer") setTimeout(emitAnswer, 25);
          else queueMicrotask(emitAnswer);
        }
      }
    }
    class FakeChannel extends EventTarget { constructor() { super(); this.readyState = "open"; } }
    class FakePeer {
      constructor(configuration) { this.configuration = configuration; this.iceGatheringState = "complete"; }
      createDataChannel() { this.channel = new FakeChannel(); return this.channel; }
      createOffer() {
        return {
          type: "offer",
          sdp: "v=0\r\na=candidate:1 1 udp 1 203.0.113.1 50000 typ relay raddr 0.0.0.0 rport 0\r\n",
        };
      }
      async setLocalDescription(value) { this.localDescription = value; }
      async setRemoteDescription(value) { this.remoteDescription = value; }
      addEventListener() {}
      close() {}
    }
    window.WebSocket = FakeSocket;
    window.RTCPeerConnection = FakePeer;
    const { createRemotePeerConnector } = await import(`${base}/remote-peer-connector.js`);
    const connectorOptions = {
      timeouts: { socket: 100, message: 10, answer: 100, ice: 100 },
    };
    await createRemotePeerConnector(connectorOptions)();
    const stored = await keys.getRemoteConnection();
    await createRemotePeerConnector(connectorOptions)();
    return { pairingCount, ticketCount, deviceProofCount, stored: Boolean(stored?.ticket), fragment: location.hash };
  }, baseUrl);
  if (result.pairingCount !== 1 || result.ticketCount !== 1 || result.deviceProofCount !== 1 || !result.stored || result.fragment) {
    throw new Error(`remote connector contract failed: ${JSON.stringify(result)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
