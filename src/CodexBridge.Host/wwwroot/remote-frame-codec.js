export const REMOTE_PROTOCOL_VERSION = 1;
export const REMOTE_FRAME_HEADER_LENGTH = 32;
export const REMOTE_MAX_PAYLOAD_LENGTH = 64 * 1024;

export const RemoteFrameKind = Object.freeze({
  request: 1,
  response: 2,
  event: 3,
  attachmentStart: 4,
  attachmentChunk: 5,
  attachmentComplete: 6,
  attachmentCancel: 7,
  error: 8,
});

export const RpcMethod = Object.freeze({
  status: 1,
  capabilities: 2,
  projects: 3,
  threads: 4,
  events: 5,
  subscribe: 6,
  unsubscribe: 7,
  image: 8,
  textFile: 9,
  submitText: 10,
  cancelTransfer: 11,
  eventText: 12,
});

const validKinds = new Set(Object.values(RemoteFrameKind));

export function encodeRemoteFrame({ kind, requestId, sequence, payload, flags = 0 }) {
  const bytes = payload instanceof Uint8Array ? payload : new Uint8Array(payload);
  const requestBytes = uuidToBytes(requestId);
  validate(kind, requestBytes, sequence, flags, bytes.byteLength);
  const encoded = new Uint8Array(REMOTE_FRAME_HEADER_LENGTH + bytes.byteLength);
  const view = new DataView(encoded.buffer);
  view.setUint8(0, REMOTE_PROTOCOL_VERSION);
  view.setUint8(1, kind);
  view.setUint16(2, flags, false);
  encoded.set(requestBytes, 4);
  view.setBigInt64(20, BigInt(sequence), false);
  view.setInt32(28, bytes.byteLength, false);
  encoded.set(bytes, REMOTE_FRAME_HEADER_LENGTH);
  return encoded;
}

export function decodeRemoteFrame(value) {
  const encoded = value instanceof Uint8Array ? value : new Uint8Array(value);
  if (encoded.byteLength < REMOTE_FRAME_HEADER_LENGTH) fail("frame_too_short");
  const view = new DataView(encoded.buffer, encoded.byteOffset, encoded.byteLength);
  if (view.getUint8(0) !== REMOTE_PROTOCOL_VERSION) fail("unsupported_version");
  const kind = view.getUint8(1);
  const flags = view.getUint16(2, false);
  const requestBytes = encoded.slice(4, 20);
  const sequence = view.getBigInt64(20, false);
  const payloadLength = view.getInt32(28, false);
  validate(kind, requestBytes, sequence, flags, payloadLength);
  if (encoded.byteLength !== REMOTE_FRAME_HEADER_LENGTH + payloadLength) fail("length_mismatch");
  return {
    kind,
    requestId: bytesToUuid(requestBytes),
    sequence,
    flags,
    payload: encoded.slice(REMOTE_FRAME_HEADER_LENGTH),
  };
}

function validate(kind, requestBytes, sequence, flags, payloadLength) {
  if (!validKinds.has(kind)) fail("unknown_frame_kind");
  if (requestBytes.every((byte) => byte === 0)) fail("invalid_request_id");
  if (BigInt(sequence) < 0n) fail("invalid_sequence");
  if (flags !== 0) fail("unsupported_flags");
  if (!Number.isInteger(payloadLength) || payloadLength < 0 || payloadLength > REMOTE_MAX_PAYLOAD_LENGTH) {
    fail("payload_too_large");
  }
}

function uuidToBytes(value) {
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
    fail("invalid_request_id");
  }
  return Uint8Array.from(value.replaceAll("-", "").match(/../g), (part) => Number.parseInt(part, 16));
}

function bytesToUuid(bytes) {
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function fail(code) {
  const error = new Error(code);
  error.code = code;
  throw error;
}
