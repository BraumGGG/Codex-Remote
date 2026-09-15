import { TransportError, requireOnline } from "./transport.js";
import { decodeRemoteFrame, encodeRemoteFrame, RemoteFrameKind, RpcMethod } from "./remote-frame-codec.js";

export class RemoteTransport extends EventTarget {
  // TURN/TCP/TLS can take longer than a normal LAN WebRTC handshake, especially
  // after a mobile network transition. Do not tear down a still-checking peer
  // at 20 seconds; the connector reports an explicit ICE failure when it is
  // terminal, while this timeout remains the final bounded guard.
  constructor({ connectPeer, requestTimeout = 5_000, channelOpenTimeout = 60_000,
    reconnectDelays = [1_000, 2_000, 4_000, 8_000, 16_000, 30_000] }) {
    super();
    this.connectPeer = connectPeer;
    this.requestTimeout = requestTimeout;
    this.channelOpenTimeout = channelOpenTimeout;
    this.reconnectDelays = reconnectDelays;
    this.reconnectAttempt = 0;
    this.state = "disconnected";
    this.channel = null;
    this.pending = new Map();
    this.subscriptions = new Map();
    this.transfers = new Map();
    this.reconnectTimer = null;
    this.connectionGeneration = 0;
    this.connectionAbort = null;
    this.connectPromise = null;
    this.handledDisconnectGeneration = 0;
    this.firstRpcSentGeneration = 0;
    this.firstRpcReceivedGeneration = 0;
    this.onlineHandler = () => {
      if (this.state !== "reconnecting" && this.state !== "failed") return;
      clearTimeout(this.reconnectTimer);
      this.reconnectAttempt = 0;
      this.state = "reconnecting";
      this.connect().catch(() => { });
    };
    window.addEventListener("online", this.onlineHandler);
  }

  connect() {
    // App resume, browser online, and the backoff timer can all request a
    // reconnect at the same time. Share one in-flight handshake instead of
    // aborting the first attempt with the second one.
    if (this.connectPromise) return this.connectPromise;
    const promise = this.#connectCore();
    const shared = promise.finally(() => {
      if (this.connectPromise === shared) this.connectPromise = null;
    });
    this.connectPromise = shared;
    return shared;
  }

  async #connectCore() {
    if (!new Set(["disconnected", "failed", "reconnecting"]).has(this.state)) return;
    const automaticReconnect = this.state === "reconnecting";
    const generation = ++this.connectionGeneration;
    this.connectionAbort?.abort();
    this.connectionAbort = new AbortController();
    const signal = this.connectionAbort.signal;
    clearTimeout(this.reconnectTimer);
    this.#setState(this.state === "disconnected" || this.state === "failed" ? "signaling" : "reconnecting");
    try {
      const channel = await this.connectPeer((phase) => {
        if (generation === this.connectionGeneration && phase === "connecting") this.#setState("connecting");
      }, signal);
      if (generation !== this.connectionGeneration || signal.aborted) {
        channel.close();
        return;
      }
      this.channel = channel;
      channel.binaryType = "arraybuffer";
      channel.addEventListener("message", (event) => this.#onMessage(event.data, generation, channel));
      channel.addEventListener("close", () => this.#handleDisconnect(
        "transport_disconnected", "reconnecting", generation, channel));
      channel.addEventListener("error", () => this.#handleDisconnect(
        "transport_failed", "reconnecting", generation, channel));
      if (channel.readyState !== "open") {
        await new Promise((resolve, reject) => {
          const timer = setTimeout(() => reject(new TransportError("channel_open_timeout")), this.channelOpenTimeout);
          channel.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
          channel.addEventListener("error", () => { clearTimeout(timer); reject(new TransportError("transport_failed")); }, { once: true });
          channel.addEventListener("close", () => { clearTimeout(timer); reject(new TransportError("channel_closed_before_open")); }, { once: true });
        });
      }
      if (generation !== this.connectionGeneration || signal.aborted) {
        channel.close();
        return;
      }
      this.reconnectAttempt = 0;
      this.#setState("online");
      this.#restoreSubscriptions().catch(() => { });
      return;
    } catch (error) {
      if (generation !== this.connectionGeneration || signal.aborted) return;
      this.#handleDisconnect(
        "transport_failed", "reconnecting", generation);
      throw error;
    }
  }

  status() { return this.#readRequest(RpcMethod.status, {}); }
  capabilities() { return this.#readRequest(RpcMethod.capabilities, {}); }
  listProjects() { return this.#readRequest(RpcMethod.projects, {}); }
  listThreads(projectId, cursor = null, pageSize = 30, query = null) {
    return this.#readRequest(RpcMethod.threads, { projectId, cursor, pageSize, query });
  }
  getEvents(threadId, beforeCursor = null, pageSize = 40, maximumBytes = 48 * 1024) {
    return this.#readRequest(RpcMethod.events, { threadId, beforeCursor, pageSize, maximumBytes });
  }
  getTextFile(threadId, fileId) { return this.#readRequest(RpcMethod.textFile, { threadId, fileId }); }
  getImage(threadId, attachmentId) { return this.#readRequest(RpcMethod.image, { threadId, attachmentId }); }
  getEventText(threadId, sequence, contentId) {
    return this.#readRequest(RpcMethod.eventText, { threadId, sequence, contentId });
  }

  submitMessage(threadId, text) {
    return this.#request(RpcMethod.submitText, {
      commandId: crypto.randomUUID(),
      threadId,
      text,
    }, true);
  }

  async subscribe(threadId, afterSequence, onEvent, onDisconnect) {
    const subscription = { threadId, afterSequence, onEvent, onDisconnect };
    this.subscriptions.set(threadId, subscription);
    await this.#request(RpcMethod.subscribe, { threadId, afterSequence });
    return {
      close: () => {
        // A late close from an older subscribe must not remove a newer one.
        const isCurrent = this.subscriptions.get(threadId) === subscription;
        if (isCurrent) this.subscriptions.delete(threadId);
        if (isCurrent && this.state === "online") {
          this.#request(RpcMethod.unsubscribe, { threadId }).catch(() => { });
        }
      },
    };
  }

  disconnect() {
    this.subscriptions.clear();
    const channel = this.channel;
    const generation = ++this.connectionGeneration;
    this.connectionAbort?.abort();
    this.connectionAbort = null;
    this.connectPromise = null;
    this.channel = null;
    clearTimeout(this.reconnectTimer);
    this.#handleDisconnect("transport_disconnected", "disconnected", generation);
    channel?.close();
  }

  recoverFromStaleConnection(code = "transport_disconnected") {
    if (this.state === "signaling" || this.state === "connecting") {
      return this.connectPromise || Promise.resolve();
    }
    if (this.state === "online") {
      const channel = this.channel;
      const generation = this.connectionGeneration;
      this.connectionAbort?.abort();
      this.connectionAbort = null;
      this.#handleDisconnect(code, "reconnecting", generation, channel);
      channel?.close();
    }
    if (this.state === "disconnected" || this.state === "failed") {
      this.#setState("reconnecting");
    }
    clearTimeout(this.reconnectTimer);
    return this.connect();
  }

  async #readRequest(method, parameters) {
    try {
      return await this.#request(method, parameters);
    } catch (error) {
      // Read RPCs are idempotent. Reconnect once after a relay loss, but
      // never apply this policy to SubmitText, whose result may be unknown.
      if (!new Set(["transport_disconnected", "transport_failed", "request_timeout", "channel_closed_before_open"]).has(error?.code)) throw error;
      await this.recoverFromStaleConnection(error.code);
      return this.#request(method, parameters);
    }
  }

  #request(method, parameters, resultUnknownOnFailure = false) {
    requireOnline(this.state);
    const requestId = crypto.randomUUID();
    const payload = new TextEncoder().encode(JSON.stringify({ method, parameters }));
    const frame = encodeRemoteFrame({
      kind: RemoteFrameKind.request,
      requestId,
      sequence: 0n,
      payload,
    });
    return new Promise((resolve, reject) => {
      const timeoutMs = method === RpcMethod.image || method === RpcMethod.textFile || method === RpcMethod.eventText
        ? Math.max(this.requestTimeout, 60_000)
        : this.requestTimeout;
      const timeout = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new TransportError(resultUnknownOnFailure ? "send_result_unknown" : "request_timeout"));
        if (!resultUnknownOnFailure) {
          this.recoverFromStaleConnection("request_timeout").catch(() => { });
        }
      }, timeoutMs);
      this.pending.set(requestId, { resolve, reject, timeout, resultUnknownOnFailure });
      try {
        this.channel.send(frame);
        if (this.firstRpcSentGeneration !== this.connectionGeneration) {
          this.firstRpcSentGeneration = this.connectionGeneration;
          this.channel.codexBridgeReportStage?.("first_rpc_sent");
        }
      } catch {
        clearTimeout(timeout);
        this.pending.delete(requestId);
        reject(new TransportError("send_result_unknown"));
      }
    });
  }

  #onMessage(data, generation = this.connectionGeneration, channel = this.channel) {
    if (generation !== this.connectionGeneration || channel !== this.channel) return;
    try {
      const frame = decodeRemoteFrame(data);
      if (this.firstRpcReceivedGeneration !== generation) {
        this.firstRpcReceivedGeneration = generation;
        channel.codexBridgeReportStage?.("first_rpc_received");
      }
      if (frame.kind === RemoteFrameKind.attachmentChunk) {
        this.#appendTransferChunk(frame);
        return;
      }
      const payload = JSON.parse(new TextDecoder().decode(frame.payload));
      if (frame.kind === RemoteFrameKind.event) {
        const subscription = this.subscriptions.get(payload.threadId);
        if (subscription) {
          subscription.afterSequence = Math.max(subscription.afterSequence, payload.sequence || 0);
          subscription.onEvent(payload);
        }
        return;
      }
      if (frame.kind === RemoteFrameKind.attachmentStart) {
        this.#startTransfer(frame, payload);
        return;
      }
      if (frame.kind === RemoteFrameKind.attachmentComplete) {
        this.#completeTransfer(frame, payload).catch(() => {
          this.#rejectTransfer(frame.requestId, "attachment_integrity_failed");
        });
        return;
      }
      const pending = this.pending.get(frame.requestId);
      if (!pending) return;
      clearTimeout(pending.timeout);
      this.pending.delete(frame.requestId);
      if (frame.kind === RemoteFrameKind.response && payload.success) pending.resolve(payload.result);
      else pending.reject(new TransportError(payload.errorCode || "request_failed"));
    } catch {
      this.#handleDisconnect("protocol_error", "failed", generation, channel);
    }
  }

  #handleDisconnect(code, nextState = "reconnecting", generation = this.connectionGeneration, channel = null) {
    if (generation !== this.connectionGeneration || (channel && this.channel !== channel)) return;
    if (this.handledDisconnectGeneration === generation) return;
    this.handledDisconnectGeneration = generation;
    if (channel) this.channel = null;
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout);
        pending.reject(new TransportError(
          pending.resultUnknownOnFailure ? "send_result_unknown" : code,
        ));
    }
    this.pending.clear();
    this.transfers.clear();
    for (const subscription of this.subscriptions.values()) subscription.onDisconnect?.();
    this.#setState(nextState);
    if (nextState === "reconnecting") {
      clearTimeout(this.reconnectTimer);
      const delay = this.reconnectDelays[Math.min(this.reconnectAttempt, this.reconnectDelays.length - 1)];
      this.reconnectAttempt++;
      this.reconnectTimer = setTimeout(() => this.connect().catch(() => { }), delay);
    }
  }

  async #restoreSubscriptions() {
    for (const subscription of this.subscriptions.values()) {
      await this.#request(RpcMethod.subscribe, {
        threadId: subscription.threadId,
        afterSequence: subscription.afterSequence,
      });
    }
  }

  #startTransfer(frame, metadata) {
    if (
      !this.pending.has(frame.requestId) ||
      this.transfers.has(frame.requestId) ||
      !new Set(["image", "text", "event-text"]).has(metadata.kind) ||
      !Number.isSafeInteger(metadata.length) ||
      metadata.length < 0 ||
      metadata.length > 4 * 1024 * 1024 ||
      (metadata.kind === "image" && metadata.length > 2 * 1024 * 1024) ||
      (metadata.kind === "text" && metadata.length > 32 * 1024)
    ) {
      this.#rejectTransfer(frame.requestId, "attachment_invalid");
      return;
    }
    this.transfers.set(frame.requestId, {
      metadata,
      chunks: [],
      received: 0,
      nextSequence: 1n,
    });
  }

  #appendTransferChunk(frame) {
    const transfer = this.transfers.get(frame.requestId);
    if (
      !transfer ||
      frame.sequence !== transfer.nextSequence ||
      frame.payload.byteLength > 64 * 1024 ||
      transfer.received + frame.payload.byteLength > transfer.metadata.length
    ) {
      this.#rejectTransfer(frame.requestId, "attachment_invalid");
      return;
    }
    transfer.chunks.push(frame.payload);
    transfer.received += frame.payload.byteLength;
    transfer.nextSequence += 1n;
  }

  async #completeTransfer(frame, completed) {
    const transfer = this.transfers.get(frame.requestId);
    if (
      !transfer ||
      frame.sequence !== transfer.nextSequence ||
      completed.length !== transfer.metadata.length ||
      transfer.received !== transfer.metadata.length ||
      !/^[0-9a-f]{64}$/.test(completed.sha256)
    ) {
      this.#rejectTransfer(frame.requestId, "attachment_integrity_failed");
      return;
    }
    const bytes = new Uint8Array(transfer.received);
    let offset = 0;
    for (const chunk of transfer.chunks) {
      bytes.set(chunk, offset);
      offset += chunk.byteLength;
    }
    const digest = Array.from(
      new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)),
      (byte) => byte.toString(16).padStart(2, "0"),
    ).join("");
    if (digest !== completed.sha256) {
      this.#rejectTransfer(frame.requestId, "attachment_integrity_failed");
      return;
    }

    const pending = this.pending.get(frame.requestId);
    if (!pending) return;
    let result;
    if (transfer.metadata.kind === "image") {
      result = new Blob([bytes], { type: transfer.metadata.contentType });
    } else if (transfer.metadata.kind === "text") {
      result = {
        name: transfer.metadata.name,
        kind: transfer.metadata.contentType,
        content: new TextDecoder("utf-8", { fatal: true }).decode(bytes),
      };
    } else {
      result = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    }
    clearTimeout(pending.timeout);
    this.pending.delete(frame.requestId);
    this.transfers.delete(frame.requestId);
    pending.resolve(result);
  }

  #rejectTransfer(requestId, code) {
    const pending = this.pending.get(requestId);
    if (pending) {
      clearTimeout(pending.timeout);
      pending.reject(new TransportError(code));
      this.pending.delete(requestId);
    }
    this.transfers.delete(requestId);
  }

  #setState(state) {
    this.state = state;
    this.dispatchEvent(new CustomEvent("statechange", { detail: state }));
  }
}
