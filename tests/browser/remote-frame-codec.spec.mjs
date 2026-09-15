import path from "node:path";
import { readFile } from "node:fs/promises";

const root = path.resolve(import.meta.dirname, "..", "..");
const source = await readFile(
  path.join(root, "src", "CodexBridge.Host", "wwwroot", "remote-frame-codec.js"),
  "utf8",
);
const codec = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

const payload = new TextEncoder().encode("abc");
const encoded = codec.encodeRemoteFrame({
  kind: codec.RemoteFrameKind.request,
  requestId: "00112233-4455-6677-8899-aabbccddeeff",
  sequence: 1n,
  payload,
});
const hex = Array.from(encoded, (byte) => byte.toString(16).padStart(2, "0")).join("").toUpperCase();
const expected = "0101000000112233445566778899AABBCCDDEEFF000000000000000100000003616263";
if (hex !== expected) throw new Error(`cross-language vector mismatch: ${hex}`);

const decoded = codec.decodeRemoteFrame(encoded);
if (
  decoded.kind !== codec.RemoteFrameKind.request ||
  decoded.requestId !== "00112233-4455-6677-8899-aabbccddeeff" ||
  decoded.sequence !== 1n ||
  new TextDecoder().decode(decoded.payload) !== "abc"
) {
  throw new Error(`round trip failed: ${JSON.stringify(decoded, (_, value) => typeof value === "bigint" ? value.toString() : value)}`);
}

for (const dangerous of ["delete", "archive", "rename", "pin", "shell", "genericRpc"]) {
  if (Object.hasOwn(codec.RpcMethod, dangerous)) throw new Error(`dangerous RPC exposed: ${dangerous}`);
}

const oversized = { ...decoded, payload: new Uint8Array(codec.REMOTE_MAX_PAYLOAD_LENGTH + 1) };
try {
  codec.encodeRemoteFrame(oversized);
  throw new Error("oversized payload was accepted");
} catch (error) {
  if (error.code !== "payload_too_large") throw error;
}

process.stdout.write(`${JSON.stringify({ vector: hex, dangerousRpcExposed: false })}\n`);
