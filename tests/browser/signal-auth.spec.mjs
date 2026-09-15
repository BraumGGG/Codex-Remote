import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");

const server = createServer(async (request, response) => {
  try {
    const name = new URL(request.url, "http://localhost").pathname.slice(1);
    if (!name) {
      response.writeHead(200, { "Content-Type": "text/html", "Cache-Control": "no-store" });
      response.end("<!doctype html><meta charset=utf-8><title>signal auth test</title>");
      return;
    }
    if (!new Set(["remote-key-store.js", "signal-auth.js"]).has(name)) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { "Content-Type": "text/javascript", "Cache-Control": "no-store" });
    response.end(await readFile(path.join(webRoot, name)));
  } catch {
    response.writeHead(500).end();
  }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const { port } = server.address();
const baseUrl = `http://127.0.0.1:${port}`;
const browser = await chromium.launch({ channel: "chrome", headless: true });

try {
  const page = await browser.newPage();
  await page.goto(baseUrl);
  const result = await page.evaluate(async (moduleUrl) => {
    const keys = await import(`${moduleUrl}/remote-key-store.js`);
    const auth = await import(`${moduleUrl}/signal-auth.js`);
    await keys.clearDeviceIdentityForTests();
    const first = await keys.getOrCreateDeviceIdentity();
    const second = await keys.getOrCreateDeviceIdentity();
    let exportRejected = false;
    try {
      await crypto.subtle.exportKey("pkcs8", first.privateKey);
    } catch {
      exportRejected = true;
    }

    const signed = await auth.signOffer("browser offer");
    const publicKey = await crypto.subtle.importKey(
      "spki",
      keys.base64UrlDecode(signed.publicKeySpki),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    const signatureValid = await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      publicKey,
      keys.base64UrlDecode(signed.signatureBase64Url),
      keys.base64UrlDecode(signed.payloadBase64Url),
    );

    const pair = keys.base64UrlEncode(new TextEncoder().encode(JSON.stringify({ routeId: "route", secret: "secret" })));
    history.replaceState(null, "", `/#pair=${pair}`);
    const pairing = auth.consumePairingFragment();
    return {
      stable: first.deviceId === second.deviceId,
      privateExtractable: first.privateKey.extractable,
      exportRejected,
      signatureValid,
      fragmentCleared: location.hash === "",
      pairing,
      localStorageLength: localStorage.length,
    };
  }, baseUrl);

  if (!result.stable || result.privateExtractable || !result.exportRejected || !result.signatureValid) {
    throw new Error(`device identity checks failed: ${JSON.stringify(result)}`);
  }
  if (!result.fragmentCleared || result.pairing?.secret !== "secret" || result.localStorageLength !== 0) {
    throw new Error(`pairing fragment checks failed: ${JSON.stringify(result)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
