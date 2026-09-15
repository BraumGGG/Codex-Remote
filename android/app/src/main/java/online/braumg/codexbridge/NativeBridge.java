package online.braumg.codexbridge;

import android.webkit.JavascriptInterface;
import org.json.JSONObject;

public final class NativeBridge {
    public interface Scanner { void scan(); }
    private final DeviceIdentity identity;
    private final Scanner scanner;

    NativeBridge(DeviceIdentity identity, Scanner scanner) {
        this.identity = identity;
        this.scanner = scanner;
    }

    @JavascriptInterface
    public String getDeviceIdentity() {
        try {
            DeviceIdentity.PublicIdentity value = identity.getPublicIdentity();
            return new JSONObject().put("version", value.version()).put("deviceId", value.deviceId())
                .put("publicKeySpki", value.publicKeySpki()).put("installationId", value.installationId()).toString();
        } catch (Exception exception) {
            throw new IllegalStateException("identity_unavailable", exception);
        }
    }

    @JavascriptInterface
    public String sign(String payloadBase64Url) {
        try { return identity.sign(payloadBase64Url); }
        catch (Exception exception) { throw new IllegalStateException("signature_failed", exception); }
    }

    @JavascriptInterface
    public void scanPairingCode() { scanner.scan(); }
}
