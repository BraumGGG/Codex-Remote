import {
  base64UrlDecode, base64UrlEncode, getOrCreateDeviceIdentity,
  getRemoteConnection, saveRemoteConnection,
} from "./remote-key-store.js";
import { consumePairingFragment, verifyAnswer } from "./signal-auth.js";

const encoder = new TextEncoder();
// Mobile WebView processes often need several seconds to recreate a TURN
// allocation after the app was fully killed. Keep this bounded, but do not
// discard each fresh PeerConnection after only a few seconds.
const DEFAULT_TIMEOUTS = Object.freeze({ socket: 10_000, message: 12_000, answer: 90_000, ice: 60_000 });

export async function hasRemoteConfiguration() {
  return Boolean(window.location.pathname.startsWith("/remote") || window.location.hash || await getRemoteConnection());
}

export function createRemotePeerConnector({
      // Public connections must not depend on private/Tailscale candidates. Use the
      // authenticated TURN path by default; relay=0 remains a local diagnostics switch.
      forceRelay = new URLSearchParams(window.location.search).get("relay") !== "0",
  timeouts = DEFAULT_TIMEOUTS,
} = {}) {
  let pairing = consumePairingFragment();
  return async (onPhase, signal) => {
    throwIfAborted(signal);
    const stored = await getRemoteConnection();
    throwIfAborted(signal);
    const config = pairing || stored;
    if (!config) throw new Error("remote_pairing_required");
    const identity = await getOrCreateDeviceIdentity();
    const socket = new WebSocket(config.signalUrl);
    let peer;
    let reportStage = () => {};
    const abort = () => {
      socket.close();
      peer?.close();
    };
    signal?.addEventListener("abort", abort, { once: true });
    try {
      await waitSocket(socket, timeouts.socket, signal);
      if (pairing) {
        socket.send(JSON.stringify({ type: "pairing-client", routeId: config.routeId, deviceId: identity.deviceId }));
        await expectType(socket, "authenticated", timeouts.message, signal);
      } else {
        socket.send(JSON.stringify({ type: "client-auth", ticket: config.ticket }));
        const challenge = await expectType(socket, "device-challenge", timeouts.message, signal);
        const signature = await identity.sign(base64UrlDecode(challenge.challenge));
        socket.send(JSON.stringify({ type: "device-auth", signature: base64UrlEncode(signature) }));
        await expectType(socket, "authenticated", timeouts.message, signal);
      }
      reportStage = createStageReporter(socket);
      reportStage("signal_authenticated");

      socket.send(JSON.stringify({ type: "turn-request" }));
      reportStage("turn_request_sent");
      const turn = await expectType(
        socket, "turn-credentials", timeouts.message, signal, "turn_credentials_timeout");
      reportStage("turn_credentials_received");
      const relayOffer = await createOfferWithCandidates({
        turn, forceRelay, timeoutMs: timeouts.ice, signal, reportStage,
      });
      peer = relayOffer.peer;
      const channel = relayOffer.channel;
      reportStage("ice_gathering_completed");
      const offerSdp = relayOffer.offerSdp;
      reportCandidateTelemetry(socket, offerSdp);
      onPhase?.("connecting");

      if (pairing) {
        const pairingBytes = encoder.encode(JSON.stringify({
          routeId: config.routeId,
          devicePublicKeySpki: identity.publicKeySpki,
          deviceName: navigator.userAgentData?.platform || "Android",
          installationId: identity.installationId || undefined,
          offerSdp,
        }));
        const proofKey = await crypto.subtle.importKey(
          "raw", base64UrlDecode(config.secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"],
        );
        const proof = new Uint8Array(await crypto.subtle.sign("HMAC", proofKey, pairingBytes));
        reportStage("pairing_offer_sent");
        socket.send(JSON.stringify({
          type: "pairing-offer",
          pairingPayloadBase64Url: base64UrlEncode(pairingBytes),
          proof: base64UrlEncode(proof),
        }));
      } else {
        reportStage("offer_sent");
        socket.send(JSON.stringify({ type: "offer", offerSdp }));
      }

      const answerMessage = await expectType(
        socket, "answer", timeouts.answer, signal, "answer_timeout");
      if (!await verifyAnswer(answerMessage.signed, config.hostPublicKeySpki)) {
        throw new Error("answer_signature_invalid");
      }
      reportStage("answer_verified");
      const answerBytes = base64UrlDecode(answerMessage.signed.payloadBase64Url);
      const answer = JSON.parse(new TextDecoder().decode(answerBytes));
      if (answer.offerSha256 !== await sha256Hex(encoder.encode(offerSdp))) {
        throw new Error("answer_offer_mismatch");
      }
      await peer.setRemoteDescription({ type: "answer", sdp: answer.answerSdp });
      reportStage("remote_description_set");
      throwIfAborted(signal);
      if (pairing) {
        await saveRemoteConnection({
          version: 1,
          signalUrl: config.signalUrl,
          hostId: config.hostId,
          hostPublicKeySpki: config.hostPublicKeySpki,
          ticket: answerMessage.ticket,
        });
        pairing = null;
      }
      peer.addEventListener("iceconnectionstatechange", () => {
        if (peer.iceConnectionState === "connected" || peer.iceConnectionState === "completed") {
          reportStage("ice_connected");
        } else if (peer.iceConnectionState === "failed") {
          reportStage("peer_failed");
        }
      });
      peer.addEventListener("connectionstatechange", () => {
        if (peer.connectionState === "connected") reportStage("peer_connected");
        if (peer.connectionState === "failed") reportStage("peer_failed");
      });
      channel.addEventListener("open", () => reportStage("datachannel_open"), { once: true });
      channel.addEventListener("error", () => reportStage("datachannel_error"), { once: true });
      channel.addEventListener("close", () => {
        reportStage("channel_closed");
        setTimeout(() => socket.close(), 0);
        peer.close();
      }, { once: true });
      Object.defineProperty(channel, "codexBridgeReportStage", { value: reportStage });
      return channel;
    } catch (error) {
      const code = classifyConnectionError(error);
      if (socket.readyState === WebSocket.OPEN) reportStage(`error_${code}`);
      socket.close();
      peer?.close();
      throw error;
    } finally {
      signal?.removeEventListener("abort", abort);
    }
  };
}

function classifyConnectionError(error) {
  const message = String(error?.message || error || "connection_failed");
  if (message === "turn_credentials_timeout" || message === "answer_timeout") return message;
  if (message === "ice_gathering_timeout") return "ice_gathering_timeout";
  if (message === "ice_no_relay_candidate") return "ice_no_relay_candidate";
  if (message === "native_identity_invalid" || message === "native_signature_failed" ||
      message === "native_signature_invalid") return "native_identity_failed";
  if (message === "send_result_unknown") return "offer_send_failed";
  return message.replace(/[^A-Za-z0-9_]+/g, "_").slice(0, 48) || "connection_failed";
}

function createStageReporter(socket) {
  const observed = new Set();
  return (stage) => {
    if (observed.has(stage) || socket.readyState !== WebSocket.OPEN) return;
    observed.add(stage);
    socket.send(JSON.stringify({ type: "client-telemetry", stage }));
  };
}

function reportCandidateTelemetry(socket, sdp) {
  if (socket.readyState !== WebSocket.OPEN) return;
  const types = candidateTypesFromSdp(sdp);
  const stage = types.size === 1 && types.has("relay")
    ? "offer_relay_only"
    : types.has("host")
      ? "offer_contains_host"
      : types.has("srflx") || types.has("prflx")
        ? "offer_contains_srflx"
        : "offer_contains_unknown";
  socket.send(JSON.stringify({ type: "client-telemetry", stage }));
}

export function candidateTypesFromSdp(sdp) {
  const types = new Set();
  for (const line of String(sdp || "").split(/\r?\n/)) {
    const match = line.match(/^a=candidate:[^\s]+\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+typ\s+(host|srflx|prflx|relay)(?:\s|$)/);
    if (match) types.add(match[1]);
  }
  return types;
}

export function buildTurnUrlAttempts(urls) {
  const values = Array.isArray(urls) ? urls : [urls];
  const unique = [...new Set(values.filter((value) => typeof value === "string" && value))];
  if (unique.length <= 1) return unique.map((url) => [url]);
  return [unique, ...unique.map((url) => [url])];
}

export function restoreObservedRelayCandidates(sdp, gatheredCandidates) {
  if (candidateTypesFromSdp(sdp).has("relay")) return sdp;
  const candidates = gatheredCandidates
    .filter((candidate) => typeof candidate === "string" && candidate.includes(" typ relay "))
    .map((candidate) => `a=${candidate}`);
  if (!candidates.length) return sdp;
  const lines = String(sdp || "").split(/\r?\n/);
  const insertAt = lines.findIndex((line) => line === "a=end-of-candidates");
  lines.splice(insertAt >= 0 ? insertAt : lines.length, 0, ...candidates);
  return lines.join("\r\n");
}

async function createOfferWithCandidates({ turn, forceRelay, timeoutMs, signal, reportStage }) {
  const urlAttempts = buildTurnUrlAttempts(turn.urls);
  // Android WebView can report ICE gathering complete before the first TURN
  // allocation has produced a relay candidate after a cold process start.
  // Retry the same authenticated URL a few times instead of failing the whole
  // signaling connection immediately. Keep the total budget bounded.
  const attempts = urlAttempts.length === 1
    ? [urlAttempts[0], urlAttempts[0], urlAttempts[0]]
    : urlAttempts;
  if (!attempts.length) throw new Error("ice_no_relay_candidate");
  const attemptTimeout = Math.max(10_000, Math.floor(timeoutMs / attempts.length));
  for (let index = 0; index < attempts.length; index++) {
    throwIfAborted(signal);
    const peer = new RTCPeerConnection({
      iceServers: [{ urls: attempts[index], username: turn.username, credential: turn.credential }],
      iceTransportPolicy: forceRelay ? "relay" : "all",
    });
    const channel = peer.createDataChannel("codex-bridge-v1", { ordered: true });
    const gatheredCandidates = [];
    peer.addEventListener("icecandidate", (event) => {
      const candidate = event.candidate?.candidate;
      if (candidate) gatheredCandidates.push(candidate);
    });
    try {
      const offer = await peer.createOffer();
      reportStage("offer_created");
      await peer.setLocalDescription(offer);
      await waitIceGathering(peer, attemptTimeout, signal);
      let offerSdp = peer.localDescription?.sdp || "";
      let types = candidateTypesFromSdp(offerSdp);
      // Some Android WebView builds report ICE gathering complete before the
      // final candidates are reflected in localDescription. Reinsert only
      // candidates observed through the standard event, preserving the SDP
      // structure and keeping all credential/URL data out of diagnostics.
      if (!types.has("relay")) {
        offerSdp = restoreObservedRelayCandidates(offerSdp, gatheredCandidates);
        types = candidateTypesFromSdp(offerSdp);
      }
      if ((forceRelay && types.has("relay")) || (!forceRelay && types.size > 0)) {
        return { peer, channel, offerSdp };
      }
    } catch (error) {
      peer.close();
      if (String(error?.message || error) !== "ice_gathering_timeout" || index === attempts.length - 1) throw error;
    }
    peer.close();
    if (index < attempts.length - 1) {
      reportStage("ice_candidate_retry");
      await waitBeforeIceRetry(500, signal);
    }
  }
  throw new Error("ice_no_relay_candidate");
}

function waitBeforeIceRetry(timeoutMs, signal) {
  return new Promise((resolve, reject) => {
    const finish = (action) => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", onAbort);
      action();
    };
    const timer = setTimeout(() => finish(resolve), timeoutMs);
    const onAbort = () => {
      finish(() => reject(new Error("connection_cancelled")));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

function waitSocket(socket, timeoutMs, signal) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("signal_timeout")), timeoutMs);
    socket.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
    socket.addEventListener("error", () => { clearTimeout(timer); reject(new Error("signal_unavailable")); }, { once: true });
    signal?.addEventListener("abort", () => { clearTimeout(timer); reject(new Error("connection_cancelled")); }, { once: true });
  });
}

function receiveJson(socket, timeoutMs, signal, timeoutCode = "signal_message_timeout") {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(timeoutCode)), timeoutMs);
    socket.addEventListener("message", (event) => {
      clearTimeout(timer);
      try { resolve(JSON.parse(event.data)); } catch { reject(new Error("signal_invalid")); }
    }, { once: true });
    socket.addEventListener("close", () => { clearTimeout(timer); reject(new Error("signal_closed")); }, { once: true });
    signal?.addEventListener("abort", () => { clearTimeout(timer); reject(new Error("connection_cancelled")); }, { once: true });
  });
}

async function expectType(socket, type, timeoutMs, signal, timeoutCode) {
  const message = await receiveJson(socket, timeoutMs, signal, timeoutCode);
  if (message?.type !== type) throw new Error(message?.code || "signal_invalid");
  return message;
}

function throwIfAborted(signal) {
  if (signal?.aborted) throw new Error("connection_cancelled");
}

function waitIceGathering(peer, timeoutMs, signal) {
  if (peer.iceGatheringState === "complete") return Promise.resolve();
  return new Promise((resolve, reject) => {
    const finish = (action) => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", onAbort);
      action();
    };
    const onAbort = () => finish(() => reject(new Error("connection_cancelled")));
    const timer = setTimeout(() => finish(() => reject(new Error("ice_gathering_timeout"))), timeoutMs);
    peer.addEventListener("icegatheringstatechange", () => {
      if (peer.iceGatheringState === "complete") finish(resolve);
    });
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

async function sha256Hex(bytes) {
  return Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)),
    (byte) => byte.toString(16).padStart(2, "0")).join("");
}
