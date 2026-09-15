import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const root = path.resolve(import.meta.dirname, "..", "..");
const webRoot = path.join(root, "src", "CodexBridge.Host", "wwwroot");
const allowed = new Set(["transport.js", "remote-frame-codec.js", "remote-transport.js"]);
const server = createServer(async (request, response) => {
  const name = new URL(request.url, "http://localhost").pathname.slice(1);
  if (!name) {
    response.writeHead(200, { "Content-Type": "text/html" });
    response.end("<!doctype html><title>attachment test</title>");
    return;
  }
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
    const { RemoteTransport } = await import(`${base}/remote-transport.js`);
    const codec = await import(`${base}/remote-frame-codec.js`);
    class FakeChannel extends EventTarget {
      constructor() { super(); this.readyState = "open"; }
      send(encoded) {
        const requestFrame = codec.decodeRemoteFrame(encoded);
        const request = JSON.parse(new TextDecoder().decode(requestFrame.payload));
        if (request.method === codec.RpcMethod.image) {
          this.#respondImage(requestFrame, request.parameters.attachmentId);
        } else if (request.method === codec.RpcMethod.eventText) {
          this.#respondEventText(requestFrame, request.parameters.contentId);
        }
      }
      close() { this.readyState = "closed"; this.dispatchEvent(new Event("close")); }
      async #respondImage(request, mode) {
        const bytes = new TextEncoder().encode("verified-image-bytes");
        const digest = Array.from(
          new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)),
          (byte) => byte.toString(16).padStart(2, "0"),
        ).join("");
        this.#emit(codec.RemoteFrameKind.attachmentStart, request.requestId, 0n,
          new TextEncoder().encode(JSON.stringify({
            kind: "image", name: "image", contentType: "image/png", length: bytes.length,
          })));
        if (mode === "partial") {
          queueMicrotask(() => this.close());
          return;
        }
        this.#emit(codec.RemoteFrameKind.attachmentChunk, request.requestId, 1n, bytes);
        this.#emit(codec.RemoteFrameKind.attachmentComplete, request.requestId, 2n,
          new TextEncoder().encode(JSON.stringify({
            length: bytes.length,
            sha256: mode === "tampered" ? "0".repeat(64) : digest,
          })));
      }
      async #respondEventText(request, mode) {
        const bytes = mode === "invalid-utf8"
          ? Uint8Array.from([0xc3, 0x28])
          : new TextEncoder().encode("x".repeat(4 * 1024 * 1024));
        const digest = mode === "invalid-utf8"
          ? "eddf68639913a3cb8331cdfe7f87559e0beccf2c289c0d90ac4d89b3204004f8"
          : "baa7a6d36ffa957552df230235c2d51d735f28d49c58a5f3438a3a973a25a37d";
        this.#emit(codec.RemoteFrameKind.attachmentStart, request.requestId, 0n,
          new TextEncoder().encode(JSON.stringify({
            kind: "event-text", name: "event-text", contentType: "text/plain; charset=utf-8", length: bytes.length,
          })));
        let sequence = 1n;
        for (let offset = 0; offset < bytes.length; offset += 64 * 1024) {
          this.#emit(codec.RemoteFrameKind.attachmentChunk, request.requestId, sequence,
            bytes.slice(offset, offset + 64 * 1024));
          sequence += 1n;
        }
        this.#emit(codec.RemoteFrameKind.attachmentComplete, request.requestId, sequence,
          new TextEncoder().encode(JSON.stringify({ length: bytes.length, sha256: digest })));
      }
      #emit(kind, requestId, sequence, payload) {
        const encoded = codec.encodeRemoteFrame({ kind, requestId, sequence, payload });
        queueMicrotask(() => this.dispatchEvent(new MessageEvent("message", { data: encoded.buffer })));
      }
    }
    const channels = [];
    const transport = new RemoteTransport({
      connectPeer: async () => {
        const channel = new FakeChannel();
        channels.push(channel);
        return channel;
      },
    });
    await transport.connect();
    const blob = await transport.getImage("thread", "valid");
    const validText = await blob.text();
    const tampered = await transport.getImage("thread", "tampered").then(
      () => "accepted",
      (error) => error.code,
    );
    const eventText = await transport.getEventText("thread", 42, "valid-event-text");
    const invalidUtf8 = await transport.getEventText("thread", 42, "invalid-utf8").then(
      () => "accepted",
      (error) => error.code,
    );
    const partial = await transport.getImage("thread", "partial").then(
      () => "accepted",
      (error) => error.code,
    );
    return {
      validText,
      contentType: blob.type,
      tampered,
      partial,
      eventTextLength: eventText.length,
      invalidUtf8,
      remainingTransfers: transport.transfers.size,
    };
  }, baseUrl);
  if (
    result.validText !== "verified-image-bytes" ||
    result.contentType !== "image/png" ||
    result.tampered !== "attachment_integrity_failed" ||
    result.partial !== "transport_disconnected" ||
    result.eventTextLength !== 4 * 1024 * 1024 ||
    result.invalidUtf8 !== "attachment_integrity_failed" ||
    result.remainingTransfers !== 0
  ) {
    throw new Error(`attachment contract failed: ${JSON.stringify(result)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
