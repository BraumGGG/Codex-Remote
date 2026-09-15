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
    response.end("<!doctype html><title>remote transport test</title>");
    return;
  }
  if (!allowed.has(name)) {
    response.writeHead(404).end();
    return;
  }
  response.writeHead(200, { "Content-Type": "text/javascript", "Cache-Control": "no-store" });
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
    const channels = [];
    const sentMethods = [];

    class FakeChannel extends EventTarget {
      constructor() {
        super();
        this.readyState = "open";
      }
      send(value) {
        const frame = codec.decodeRemoteFrame(value);
        const request = JSON.parse(new TextDecoder().decode(frame.payload));
        sentMethods.push(request.method);
        if (request.method === codec.RpcMethod.submitText) return;
        const responsePayload = new TextEncoder().encode(JSON.stringify({ success: true, result: {} }));
        const response = codec.encodeRemoteFrame({
          kind: codec.RemoteFrameKind.response,
          requestId: frame.requestId,
          sequence: 0n,
          payload: responsePayload,
        });
        queueMicrotask(() => this.dispatchEvent(new MessageEvent("message", { data: response.buffer })));
      }
      close() {
        this.readyState = "closed";
        this.dispatchEvent(new Event("close"));
      }
    }

    const states = [];
    const transport = new RemoteTransport({
      connectPeer: async (phase) => {
        phase("connecting");
        const channel = new FakeChannel();
        channels.push(channel);
        return channel;
      },
      requestTimeout: 5_000,
    });
    transport.addEventListener("statechange", (event) => states.push(event.detail));
    await transport.connect();
    await transport.subscribe("thread-1", 9, () => {}, () => {});

    const submission = transport.submitMessage("thread-1", "do not retry").then(
      () => "resolved",
      (error) => error.code,
    );
    channels[0].close();
    const submissionResult = await submission;
    await transport.connect();

    return {
      states,
      submissionResult,
      submitCount: sentMethods.filter((method) => method === codec.RpcMethod.submitText).length,
      subscribeCount: sentMethods.filter((method) => method === codec.RpcMethod.subscribe).length,
      finalState: transport.state,
      reconnectAttempt: transport.reconnectAttempt,
    };
  }, baseUrl);

  if (result.submissionResult !== "send_result_unknown" || result.submitCount !== 1) {
    throw new Error(`submit retry contract failed: ${JSON.stringify(result)}`);
  }
  if (result.subscribeCount !== 2 || result.finalState !== "online") {
    throw new Error(`subscription restore contract failed: ${JSON.stringify(result)}`);
  }
  if (result.reconnectAttempt !== 0) throw new Error(`reconnect backoff did not reset: ${JSON.stringify(result)}`);
  for (const expected of ["signaling", "connecting", "online", "reconnecting"]) {
    if (!result.states.includes(expected)) throw new Error(`missing state ${expected}: ${JSON.stringify(result)}`);
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);

  const requestErrorResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?request-error`);
    const codec = await import(`${base}/remote-frame-codec.js?request-error`);
    class FakeChannel extends EventTarget {
      constructor() { super(); this.readyState = "open"; }
      send(value) {
        const frame = codec.decodeRemoteFrame(value);
        const request = JSON.parse(new TextDecoder().decode(frame.payload));
        const failed = request.method === codec.RpcMethod.events;
        const payload = new TextEncoder().encode(JSON.stringify(failed
          ? { success: false, result: null, errorCode: "response_too_large" }
          : { success: true, result: { desktopOnline: true }, errorCode: null }));
        const response = codec.encodeRemoteFrame({
          kind: failed ? codec.RemoteFrameKind.error : codec.RemoteFrameKind.response,
          requestId: frame.requestId,
          sequence: 0n,
          payload,
        });
        queueMicrotask(() => this.dispatchEvent(new MessageEvent("message", { data: response.buffer })));
      }
      close() { this.readyState = "closed"; }
    }
    const transport = new RemoteTransport({ connectPeer: async () => new FakeChannel() });
    await transport.connect();
    const errorCode = await transport.getEvents("large-thread").then(
      () => "resolved",
      (error) => error.code,
    );
    const status = await transport.status();
    return { errorCode, state: transport.state, desktopOnline: status.desktopOnline };
  }, baseUrl);
  if (requestErrorResult.errorCode !== "response_too_large" ||
      requestErrorResult.state !== "online" || !requestErrorResult.desktopOnline) {
    throw new Error(`request error closed transport: ${JSON.stringify(requestErrorResult)}`);
  }
  process.stdout.write(`${JSON.stringify(requestErrorResult)}\n`);

  const generationResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?generation`);
    class FakeChannel extends EventTarget {
      constructor() { super(); this.readyState = "open"; this.closeCount = 0; }
      close() { this.closeCount++; this.readyState = "closed"; }
    }
    let resolveFirst;
    const first = new Promise((resolve) => { resolveFirst = resolve; });
    const fresh = new FakeChannel();
    let calls = 0;
    const transport = new RemoteTransport({
      connectPeer: async () => (++calls === 1 ? first : fresh),
      reconnectDelays: [1],
    });
    const staleConnect = transport.connect().catch((error) => error.code || error.message);
    transport.disconnect();
    await transport.connect();
    const stale = new FakeChannel();
    resolveFirst(stale);
    await staleConnect;
    await new Promise((resolve) => setTimeout(resolve, 5));
    stale.dispatchEvent(new Event("close"));

    const oldOnline = new FakeChannel();
    const newOnline = new FakeChannel();
    let onlineCalls = 0;
    const replacement = new RemoteTransport({
      connectPeer: async () => (++onlineCalls === 1 ? oldOnline : newOnline),
      reconnectDelays: [1],
    });
    await replacement.connect();
    replacement.disconnect();
    await replacement.connect();
    oldOnline.dispatchEvent(new MessageEvent("message", { data: new ArrayBuffer(1) }));
    return {
      state: transport.state,
      currentIsFresh: transport.channel === fresh,
      staleClosed: stale.closeCount,
      calls,
      staleMessageIgnored: replacement.state === "online" && replacement.channel === newOnline,
    };
  }, baseUrl);
  if (generationResult.state !== "online" || !generationResult.currentIsFresh ||
      generationResult.staleClosed !== 1 || generationResult.calls !== 2 ||
      !generationResult.staleMessageIgnored) {
    throw new Error(`stale connection generation won: ${JSON.stringify(generationResult)}`);
  }
  process.stdout.write(`${JSON.stringify(generationResult)}\n`);

  const earlyFailureResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?early-failure`);
    class FakeChannel extends EventTarget {
      constructor() { super(); this.readyState = "open"; }
    }
    let attempts = 0;
    const transport = new RemoteTransport({
      connectPeer: async () => {
        attempts++;
        if (attempts === 1) throw new Error("signal_unavailable");
        return new FakeChannel();
      },
      reconnectDelays: [1],
    });
    transport.state = "reconnecting";
    await transport.connect().catch(() => {});
    await new Promise((resolve) => setTimeout(resolve, 10));
    return { attempts, state: transport.state };
  }, baseUrl);
  if (earlyFailureResult.attempts < 2 || earlyFailureResult.state !== "online") {
    throw new Error(`early reconnect was not scheduled: ${JSON.stringify(earlyFailureResult)}`);
  }
  process.stdout.write(`${JSON.stringify(earlyFailureResult)}\n`);

  const coldStartRecoveryResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?cold-start-recovery`);
    class FakeChannel extends EventTarget {
      constructor() { super(); this.readyState = "open"; }
      close() { this.readyState = "closed"; }
    }
    let attempts = 0;
    const transport = new RemoteTransport({
      connectPeer: async () => {
        attempts++;
        if (attempts < 3) throw new Error("remote_answer_no_relay");
        return new FakeChannel();
      },
      reconnectDelays: [1],
    });
    await transport.connect().catch(() => {});
    await new Promise((resolve) => setTimeout(resolve, 20));
    return { attempts, state: transport.state };
  }, baseUrl);
  if (coldStartRecoveryResult.attempts < 3 || coldStartRecoveryResult.state !== "online") {
    throw new Error(`cold start no-relay failure did not self-heal: ${JSON.stringify(coldStartRecoveryResult)}`);
  }
  process.stdout.write(`${JSON.stringify(coldStartRecoveryResult)}\n`);

  const staleChannelResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?stale-channel`);
    const codec = await import(`${base}/remote-frame-codec.js?stale-channel`);
    class FakeChannel extends EventTarget {
      constructor(respond) {
        super();
        this.respond = respond;
        this.readyState = "open";
        this.closeCount = 0;
      }
      send(value) {
        if (!this.respond) return;
        const frame = codec.decodeRemoteFrame(value);
        const payload = new TextEncoder().encode(JSON.stringify({
          success: true,
          result: { desktopOnline: true },
        }));
        const response = codec.encodeRemoteFrame({
          kind: codec.RemoteFrameKind.response,
          requestId: frame.requestId,
          sequence: 0n,
          payload,
        });
        queueMicrotask(() => this.dispatchEvent(new MessageEvent("message", { data: response.buffer })));
      }
      close() {
        this.closeCount++;
        this.readyState = "closed";
        this.dispatchEvent(new Event("close"));
      }
    }
    const stale = new FakeChannel(false);
    const fresh = new FakeChannel(true);
    let attempts = 0;
    const transport = new RemoteTransport({
      connectPeer: async () => (++attempts === 1 ? stale : fresh),
      requestTimeout: 10,
      reconnectDelays: [1],
    });
    await transport.connect();
    const timeoutCode = await transport.getEvents("thread-1").then(
      () => "resolved",
      (error) => error.code,
    );
    await new Promise((resolve) => setTimeout(resolve, 20));
    const status = await transport.status();
    return {
      timeoutCode,
      attempts,
      staleCloseCount: stale.closeCount,
      currentIsFresh: transport.channel === fresh,
      state: transport.state,
      desktopOnline: status.desktopOnline,
    };
  }, baseUrl);
  if (staleChannelResult.timeoutCode !== "request_timeout" ||
      staleChannelResult.attempts !== 2 || staleChannelResult.staleCloseCount !== 1 ||
      !staleChannelResult.currentIsFresh || staleChannelResult.state !== "online" ||
      !staleChannelResult.desktopOnline) {
    throw new Error(`stale open channel did not recover: ${JSON.stringify(staleChannelResult)}`);
  }
  process.stdout.write(`${JSON.stringify(staleChannelResult)}\n`);

  const submitTimeoutResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?submit-timeout`);
    class FakeChannel extends EventTarget {
      constructor() { super(); this.readyState = "open"; this.closeCount = 0; }
      send() { }
      close() { this.closeCount++; this.readyState = "closed"; }
    }
    const channel = new FakeChannel();
    let attempts = 0;
    const transport = new RemoteTransport({
      connectPeer: async () => { attempts++; return channel; },
      requestTimeout: 10,
      reconnectDelays: [1],
    });
    await transport.connect();
    const errorCode = await transport.submitMessage("thread-1", "do not retry").then(
      () => "resolved",
      (error) => error.code,
    );
    await new Promise((resolve) => setTimeout(resolve, 20));
    return { errorCode, attempts, closeCount: channel.closeCount, state: transport.state };
  }, baseUrl);
  if (submitTimeoutResult.errorCode !== "send_result_unknown" || submitTimeoutResult.attempts !== 1 ||
      submitTimeoutResult.closeCount !== 0 || submitTimeoutResult.state !== "online") {
    throw new Error(`submit timeout unexpectedly retried: ${JSON.stringify(submitTimeoutResult)}`);
  }
  process.stdout.write(`${JSON.stringify(submitTimeoutResult)}\n`);

  const readRetryResult = await page.evaluate(async (base) => {
    const { RemoteTransport } = await import(`${base}/remote-transport.js?read-retry`);
    const codec = await import(`${base}/remote-frame-codec.js?read-retry`);
    let attempts = 0;
    class FakeChannel extends EventTarget {
      constructor(respond) { super(); this.readyState = "open"; this.respond = respond; }
      send(value) {
        if (!this.respond) { this.dispatchEvent(new Event("close")); return; }
        const frame = codec.decodeRemoteFrame(value);
        const payload = new TextEncoder().encode(JSON.stringify({ success: true, result: { desktopOnline: true } }));
        const response = codec.encodeRemoteFrame({ kind: codec.RemoteFrameKind.response, requestId: frame.requestId, sequence: 0n, payload });
        queueMicrotask(() => this.dispatchEvent(new MessageEvent("message", { data: response.buffer })));
      }
      close() { this.readyState = "closed"; }
    }
    const transport = new RemoteTransport({ connectPeer: async () => new FakeChannel(++attempts > 1), reconnectDelays: [1] });
    await transport.connect();
    const status = await transport.status();
    return { attempts, state: transport.state, desktopOnline: status.desktopOnline };
  }, baseUrl);
  if (readRetryResult.attempts !== 2 || readRetryResult.state !== "online" || !readRetryResult.desktopOnline) {
    throw new Error(`idempotent read was not retried once: ${JSON.stringify(readRetryResult)}`);
  }
  process.stdout.write(`${JSON.stringify(readRetryResult)}\n`);
} finally {
  await browser.close();
  await new Promise((resolve) => server.close(resolve));
}
