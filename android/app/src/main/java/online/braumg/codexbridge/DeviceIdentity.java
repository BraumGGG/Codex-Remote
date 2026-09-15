package online.braumg.codexbridge;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import java.nio.ByteBuffer;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.Signature;
import java.security.spec.ECGenParameterSpec;
import java.util.Base64;

public final class DeviceIdentity {
    private static final String STORE = "AndroidKeyStore";
    private static final String ALIAS = "codex-bridge-device-v1";
    private static final String PREFERENCES = "codex-bridge-device";
    private static final String INSTALLATION_ID = "installation-id-v1";
    private final SharedPreferences preferences;

    public DeviceIdentity(Context context) {
        preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE);
    }

    public record PublicIdentity(int version, String deviceId, String publicKeySpki, String installationId) { }

    public PublicIdentity getPublicIdentity() throws Exception {
        KeyPair pair = getOrCreate();
        byte[] spki = pair.getPublic().getEncoded();
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(spki);
        return new PublicIdentity(1, encode(digest), encode(spki), getOrCreateInstallationId(preferences));
    }

    public String sign(String payloadBase64Url) throws Exception {
        byte[] payload = decode(payloadBase64Url);
        if (payload.length == 0 || payload.length > 1024 * 1024) throw new IllegalArgumentException("invalid_payload");
        Signature signer = Signature.getInstance("SHA256withECDSA");
        signer.initSign(getOrCreate().getPrivate());
        signer.update(payload);
        return encode(derToP1363(signer.sign()));
    }

    private static KeyPair getOrCreate() throws Exception {
        KeyStore store = KeyStore.getInstance(STORE);
        store.load(null);
        if (store.containsAlias(ALIAS)) {
            KeyStore.PrivateKeyEntry entry = (KeyStore.PrivateKeyEntry) store.getEntry(ALIAS, null);
            return new KeyPair(entry.getCertificate().getPublicKey(), entry.getPrivateKey());
        }
        KeyPairGenerator generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, STORE);
        generator.initialize(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setUserAuthenticationRequired(false)
            .build());
        return generator.generateKeyPair();
    }

    static String createInstallationId() {
        byte[] bytes = new byte[32];
        new SecureRandom().nextBytes(bytes);
        return encode(bytes);
    }

    private static String getOrCreateInstallationId(SharedPreferences preferences) {
        String existing = preferences.getString(INSTALLATION_ID, null);
        if (existing != null && existing.matches("[A-Za-z0-9_-]{43}")) return existing;
        String created = createInstallationId();
        if (!preferences.edit().putString(INSTALLATION_ID, created).commit()) {
            throw new IllegalStateException("installation_id_unavailable");
        }
        return created;
    }

    static byte[] derToP1363(byte[] der) {
        if (der == null || der.length < 8) throw new IllegalArgumentException("invalid_der");
        ByteBuffer buffer = ByteBuffer.wrap(der);
        if ((buffer.get() & 0xff) != 0x30) throw new IllegalArgumentException("invalid_der");
        int sequenceLength = readLength(buffer);
        if (sequenceLength != buffer.remaining()) throw new IllegalArgumentException("invalid_der");
        byte[] r = readInteger(buffer);
        byte[] s = readInteger(buffer);
        if (buffer.hasRemaining()) throw new IllegalArgumentException("invalid_der");
        byte[] output = new byte[64];
        copyInteger(r, output, 0);
        copyInteger(s, output, 32);
        return output;
    }

    private static byte[] readInteger(ByteBuffer buffer) {
        if (!buffer.hasRemaining() || (buffer.get() & 0xff) != 0x02) throw new IllegalArgumentException("invalid_der");
        int length = readLength(buffer);
        if (length < 1 || length > 33 || length > buffer.remaining()) throw new IllegalArgumentException("invalid_der");
        byte[] value = new byte[length];
        buffer.get(value);
        if ((value[0] & 0x80) != 0 || (length > 1 && value[0] == 0 && (value[1] & 0x80) == 0)) {
            throw new IllegalArgumentException("invalid_der");
        }
        return value;
    }

    private static int readLength(ByteBuffer buffer) {
        if (!buffer.hasRemaining()) throw new IllegalArgumentException("invalid_der");
        int first = buffer.get() & 0xff;
        if (first < 0x80) return first;
        int count = first & 0x7f;
        if (count < 1 || count > 2 || count > buffer.remaining()) throw new IllegalArgumentException("invalid_der");
        int value = 0;
        for (int i = 0; i < count; i++) value = (value << 8) | (buffer.get() & 0xff);
        if (value < 0x80) throw new IllegalArgumentException("invalid_der");
        return value;
    }

    private static void copyInteger(byte[] source, byte[] target, int offset) {
        int start = source.length == 33 ? 1 : 0;
        int length = source.length - start;
        if (length > 32) throw new IllegalArgumentException("invalid_der");
        System.arraycopy(source, start, target, offset + 32 - length, length);
    }

    private static String encode(byte[] value) {
        return Base64.getUrlEncoder().withoutPadding().encodeToString(value);
    }

    private static byte[] decode(String value) {
        if (value == null || !value.matches("[A-Za-z0-9_-]+")) throw new IllegalArgumentException("invalid_base64url");
        return Base64.getUrlDecoder().decode(value);
    }
}
