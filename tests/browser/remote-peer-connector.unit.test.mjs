import test from "node:test";
import assert from "node:assert/strict";

import {
  buildTurnUrlAttempts,
  candidateTypesFromSdp,
  restoreObservedRelayCandidates,
} from "../../src/CodexBridge.Host/wwwroot/remote-peer-connector.js";

test("candidateTypesFromSdp recognizes relay candidates", () => {
  const sdp = "a=candidate:1 1 udp 1677734910 203.0.113.1 50000 typ relay raddr 0.0.0.0 rport 0\r\n";
  assert.deepEqual([...candidateTypesFromSdp(sdp)], ["relay"]);
  assert.equal(candidateTypesFromSdp("v=0\r\n").size, 0);
});

test("buildTurnUrlAttempts retries each TURN route independently", () => {
  const urls = [
    "turn:remote.example.invalid:3478?transport=udp",
    "turn:remote.example.invalid:3478?transport=tcp",
    "turns:remote.example.invalid:5349?transport=tcp",
  ];
  assert.deepEqual(buildTurnUrlAttempts(urls), [urls, [urls[0]], [urls[1]], [urls[2]]]);
});

test("buildTurnUrlAttempts removes invalid and duplicate entries", () => {
  assert.deepEqual(buildTurnUrlAttempts(["turn:test", "turn:test", "", null]), [["turn:test"]]);
});

test("restoreObservedRelayCandidates repairs a WebView localDescription gap", () => {
  const sdp = "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=end-of-candidates\r\n";
  const restored = restoreObservedRelayCandidates(sdp, [
    "candidate:1 1 udp 1 203.0.113.1 50000 typ relay raddr 0.0.0.0 rport 0",
  ]);
  assert.deepEqual([...candidateTypesFromSdp(restored)], ["relay"]);
  assert.match(restored, /a=candidate:1 .* typ relay .*\r\na=end-of-candidates/);
});

test("restoreObservedRelayCandidates does not duplicate an existing relay candidate", () => {
  const sdp = "v=0\r\na=candidate:1 1 udp 1 203.0.113.1 50000 typ relay raddr 0.0.0.0 rport 0\r\n";
  assert.equal(restoreObservedRelayCandidates(sdp, [
    "candidate:2 1 udp 1 203.0.113.2 50001 typ relay raddr 0.0.0.0 rport 0",
  ]), sdp);
});
