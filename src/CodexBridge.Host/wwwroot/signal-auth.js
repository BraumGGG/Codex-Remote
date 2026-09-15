import { base64UrlDecode, base64UrlEncode, getOrCreateDeviceIdentity } from "./remote-key-store.js";

const encoder = new TextEncoder();

export async function signOffer(payload) {
  const bytes = toBytes(payload);
  const identity = await getOrCreateDeviceIdentity();
  const signature = await identity.sign(bytes);
  return {
    deviceId: identity.deviceId,
    publicKeySpki: identity.publicKeySpki,
    payloadBase64Url: base64UrlEncode(bytes),
    signatureBase64Url: base64UrlEncode(signature),
  };
}

export async function verifyAnswer(signedPayload, hostPublicKeySpki) {
  try {
    const key = await crypto.subtle.importKey(
      "spki",
      base64UrlDecode(hostPublicKeySpki),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      key,
      base64UrlDecode(signedPayload.signatureBase64Url),
      base64UrlDecode(signedPayload.payloadBase64Url),
    );
  } catch {
    return false;
  }
}

export function consumePairingFragment(location = window.location, history = window.history) {
  const fragment = location.hash.startsWith("#") ? location.hash.slice(1) : location.hash;
  history.replaceState(null, "", `${location.pathname}${location.search}`);
  if (!fragment) return null;

  const parameters = new URLSearchParams(fragment);
  const encoded = parameters.get("pair");
  if (!encoded) return null;
  const parsed = JSON.parse(new TextDecoder().decode(base64UrlDecode(encoded)));
  if (!parsed || typeof parsed !== "object") {
    throw new Error("Invalid pairing data.");
  }
  return parsed;
}

function toBytes(payload) {
  if (typeof payload === "string") return encoder.encode(payload);
  if (payload instanceof Uint8Array) return payload;
  if (payload instanceof ArrayBuffer) return new Uint8Array(payload);
  throw new TypeError("Payload must be a string, Uint8Array, or ArrayBuffer.");
}
