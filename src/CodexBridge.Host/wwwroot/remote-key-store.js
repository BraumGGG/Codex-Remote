const DATABASE_NAME = "codex-bridge-remote";
const DATABASE_VERSION = 1;
const STORE_NAME = "device-identity";
const IDENTITY_KEY = "primary";
const CONNECTION_KEY = "connection";

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
    request.addEventListener("upgradeneeded", () => {
      if (!request.result.objectStoreNames.contains(STORE_NAME)) {
        request.result.createObjectStore(STORE_NAME);
      }
    });
    request.addEventListener("success", () => resolve(request.result));
    request.addEventListener("error", () => reject(request.error));
    request.addEventListener("blocked", () => reject(new Error("Device key database is blocked.")));
  });
}

function transact(database, mode, operation) {
  return new Promise((resolve, reject) => {
    const transaction = database.transaction(STORE_NAME, mode);
    const request = operation(transaction.objectStore(STORE_NAME));
    request.addEventListener("success", () => resolve(request.result));
    request.addEventListener("error", () => reject(request.error));
    transaction.addEventListener("abort", () => reject(transaction.error));
  });
}

async function createIdentity() {
  const keyPair = await crypto.subtle.generateKey(
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["sign", "verify"],
  );
  const publicKeySpki = new Uint8Array(await crypto.subtle.exportKey("spki", keyPair.publicKey));
  const hostHash = new Uint8Array(await crypto.subtle.digest("SHA-256", publicKeySpki));
  return {
    version: 1,
    deviceId: base64UrlEncode(hostHash),
    publicKeySpki: base64UrlEncode(publicKeySpki),
    privateKey: keyPair.privateKey,
  };
}

export async function getOrCreateDeviceIdentity() {
  if (window.CodexBridgeNative) return getNativeDeviceIdentity(window.CodexBridgeNative);
  const database = await openDatabase();
  try {
    let identity = await transact(database, "readonly", (store) => store.get(IDENTITY_KEY));
    if (identity) {
      validateIdentity(identity);
      return attachSigner(identity);
    }

    const created = await createIdentity();
    await transact(database, "readwrite", (store) => store.add(created, IDENTITY_KEY));
    identity = await transact(database, "readonly", (store) => store.get(IDENTITY_KEY));
    validateIdentity(identity);
    return attachSigner(identity);
  } finally {
    database.close();
  }
}

function attachSigner(identity) {
  validateIdentity(identity);
  return {
    ...identity,
    sign: async (bytes) => new Uint8Array(await crypto.subtle.sign(
      { name: "ECDSA", hash: "SHA-256" }, identity.privateKey, normalizeBytes(bytes),
    )),
  };
}

function getNativeDeviceIdentity(nativeBridge) {
  let parsed;
  try { parsed = JSON.parse(nativeBridge.getDeviceIdentity()); }
  catch { throw new Error("native_identity_invalid"); }
  if (parsed?.version !== 1 || !/^[A-Za-z0-9_-]{43}$/.test(parsed.deviceId || "") ||
      typeof parsed.publicKeySpki !== "string") {
    throw new Error("native_identity_invalid");
  }
  const publicKey = base64UrlDecode(parsed.publicKeySpki);
  if (publicKey.length < 80 || publicKey.length > 160 || typeof nativeBridge.sign !== "function") {
    throw new Error("native_identity_invalid");
  }
  const installationId = parsed.installationId == null
    ? null
    : /^[A-Za-z0-9_-]{43}$/.test(parsed.installationId)
      ? parsed.installationId
      : (() => { throw new Error("native_identity_invalid"); })();
  return {
    version: 1,
    deviceId: parsed.deviceId,
    publicKeySpki: parsed.publicKeySpki,
    installationId,
    sign: async (bytes) => {
      let encoded;
      try { encoded = nativeBridge.sign(base64UrlEncode(normalizeBytes(bytes))); }
      catch { throw new Error("native_signature_failed"); }
      const signature = base64UrlDecode(encoded);
      if (signature.length !== 64) throw new Error("native_signature_invalid");
      return signature;
    },
  };
}

function normalizeBytes(value) {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  throw new TypeError("Signing payload must be bytes.");
}

export async function clearDeviceIdentityForTests() {
  await new Promise((resolve, reject) => {
    const request = indexedDB.deleteDatabase(DATABASE_NAME);
    request.addEventListener("success", resolve);
    request.addEventListener("error", () => reject(request.error));
    request.addEventListener("blocked", () => reject(new Error("Device key database deletion is blocked.")));
  });
}

export async function getRemoteConnection() {
  const database = await openDatabase();
  try { return (await transact(database, "readonly", (store) => store.get(CONNECTION_KEY))) || null; }
  finally { database.close(); }
}

export async function saveRemoteConnection(connection) {
  if (!connection || connection.version !== 1 || typeof connection.signalUrl !== "string" ||
      typeof connection.hostPublicKeySpki !== "string" || !connection.ticket) {
    throw new Error("Remote connection is invalid.");
  }
  const database = await openDatabase();
  try { await transact(database, "readwrite", (store) => store.put(connection, CONNECTION_KEY)); }
  finally { database.close(); }
}

export async function clearRemoteConnection() {
  const database = await openDatabase();
  try { await transact(database, "readwrite", (store) => store.delete(CONNECTION_KEY)); }
  finally { database.close(); }
}

function validateIdentity(identity) {
  if (
    identity?.version !== 1 ||
    typeof identity.deviceId !== "string" ||
    typeof identity.publicKeySpki !== "string" ||
    !(identity.privateKey instanceof CryptoKey) ||
    identity.privateKey.type !== "private" ||
    identity.privateKey.extractable ||
    identity.privateKey.algorithm?.name !== "ECDSA" ||
    identity.privateKey.algorithm?.namedCurve !== "P-256" ||
    !identity.privateKey.usages.includes("sign")
  ) {
    throw new Error("Stored device identity is invalid.");
  }
}

export function base64UrlEncode(bytes) {
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary).replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_");
}

export function base64UrlDecode(value) {
  if (!/^[A-Za-z0-9_-]*$/.test(value) || value.length % 4 === 1) {
    throw new Error("Invalid base64url value.");
  }
  const padded = value.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((value.length + 3) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}
