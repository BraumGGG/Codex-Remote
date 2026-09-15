export class TransportError extends Error {
  constructor(code, status = 0) {
    super(code || "request_failed");
    this.name = "TransportError";
    this.code = code || "request_failed";
    this.status = status;
  }
}

export function requireOnline(state) {
  if (state !== "online") throw new TransportError("transport_offline");
}
