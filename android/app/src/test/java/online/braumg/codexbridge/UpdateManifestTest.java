package online.braumg.codexbridge;

import org.junit.Test;
import static org.junit.Assert.*;

public final class UpdateManifestTest {
    @Test public void acceptsOnlyTrustedApkLocation() {
        assertTrue(UpdateManifest.isTrustedDownload("https://remote.example.invalid:8443/android/CodexBridge-1.apk"));
        assertFalse(UpdateManifest.isTrustedDownload("http://remote.example.invalid:8443/android/CodexBridge-1.apk"));
        assertFalse(UpdateManifest.isTrustedDownload("https://remote.example.invalid/android/CodexBridge-1.apk"));
        assertFalse(UpdateManifest.isTrustedDownload("https://remote.example.invalid.evil:8443/android/CodexBridge-1.apk"));
        assertFalse(UpdateManifest.isTrustedDownload("https://remote.example.invalid:8443/android/CodexBridge-1.apk?next=evil"));
        assertFalse(UpdateManifest.isTrustedDownload("https://remote.example.invalid:8443/remote/CodexBridge-1.apk"));
    }
}
