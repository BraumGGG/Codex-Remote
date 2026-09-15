import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const allowed = new Set(["remote-key-store.js", "signal-auth.js"]);
const server = createServer(async (request, response) => {
  const name = new URL(request.url, "http://localhost").pathname.slice(1);
  if (!name) return response.writeHead(200, { "Content-Type": "text/html" }).end("<!doctype html><title>native identity</title>");
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
    const keyPair = await crypto.subtle.generateKey(
      { name: "ECDSA", namedCurve: "P-256" }, false, ["sign", "verify"],
    );
    const spki = new Uint8Array(await crypto.subtle.exportKey("spki", keyPair.publicKey));
    const toUrl = (bytes) => {
      let binary = "";
      for (const byte of bytes) binary += String.fromCharCode(byte);
      return btoa(binary).replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_");
    };
    const fromUrl = (value) => Uint8Array.from(atob(value.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((value.length + 3) % 4)), (c) => c.charCodeAt(0));
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", spki));
    let signCalls = 0;
    window.CodexBridgeNative = {
      getDeviceIdentity: () => JSON.stringify({ version: 1, deviceId: toUrl(digest), publicKeySpki: toUrl(spki) }),
      sign: (payload) => {
        signCalls++;
        return crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, keyPair.privateKey, fromUrl(payload))
          .then((signature) => toUrl(new Uint8Array(signature)));
      },
    };
    // Android bridge is synchronous. Simulate its returned signature by pre-signing the known payload.
    const payload = new TextEncoder().encode("native offer");
    const prepared = toUrl(new Uint8Array(await crypto.subtle.sign(
      { name: "ECDSA", hash: "SHA-256" }, keyPair.privateKey, payload,
    )));
    window.CodexBridgeNative.sign = () => { signCalls++; return prepared; };
    const keys = await import(`${base}/remote-key-store.js`);
    const auth = await import(`${base}/signal-auth.js`);
    const identity = await keys.getOrCreateDeviceIdentity();
    const signed = await auth.signOffer(payload);
    const valid = await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" }, keyPair.publicKey,
      keys.base64UrlDecode(signed.signatureBase64Url), payload,
    );
    return { valid, signCalls, hasPrivateKey: Object.hasOwn(identity, "privateKey"), signatureLength: keys.base64UrlDecode(signed.signatureBase64Url).length };
  }, baseUrl);
  if (!result.valid || result.signCalls !== 1 || result.hasPrivateKey || result.signatureLength !== 64) {
    throw new Error(`native identity contract failed: ${JSON.stringify(result)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
