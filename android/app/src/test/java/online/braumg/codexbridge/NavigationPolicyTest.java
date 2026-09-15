package online.braumg.codexbridge;

import java.net.URI;
import org.junit.Test;
import static org.junit.Assert.*;

public final class NavigationPolicyTest {
    @Test public void allowsOnlyExactPublicPage() {
        assertTrue(NavigationPolicy.isAllowedPage("https://remote.example.invalid:8443/remote/"));
        assertTrue(NavigationPolicy.isAllowedPage("https://remote.example.invalid:8443/remote/#pair=" + "a".repeat(64)));
        assertFalse(NavigationPolicy.isAllowedPage("http://remote.example.invalid:8443/remote/"));
        assertFalse(NavigationPolicy.isAllowedPage("https://remote.example.invalid/remote/"));
        assertFalse(NavigationPolicy.isAllowedPage("https://evil.example/remote/"));
        assertFalse(NavigationPolicy.isAllowedPage("https://remote.example.invalid:8443/remote/child"));
        assertFalse(NavigationPolicy.isAllowedPage("https://remote.example.invalid@evil.example:8443/remote/"));
        assertFalse(NavigationPolicy.isAllowedPage("https://remote.example.invalid:8443\\@evil.example/remote/"));
    }

    @Test public void pairingRequiresSingleBoundedFragment() {
        String good = "https://remote.example.invalid:8443/remote/#pair=" + "Abc_123-".repeat(8);
        assertTrue(NavigationPolicy.isValidPairingUrl(good));
        assertFalse(NavigationPolicy.isValidPairingUrl("https://remote.example.invalid:8443/remote/"));
        assertFalse(NavigationPolicy.isValidPairingUrl(good + "&next=https://evil.example"));
        assertFalse(NavigationPolicy.isValidPairingUrl("https://remote.example.invalid:8443/remote/#pair=short"));
        assertFalse(NavigationPolicy.isValidPairingUrl("https://evil.example:8443/remote/#pair=" + "a".repeat(64)));
        assertFalse(NavigationPolicy.isValidPairingUrl(
            "https://remote.example.invalid:8443/remote/?source=untrusted#pair=" + "a".repeat(64)));
    }

    @Test public void pairingNavigationForcesReloadWithoutLeakingSecretToQuery() {
        String secret = "Abc_123-".repeat(8);
        String pairingUrl = "https://remote.example.invalid:8443/remote/#pair=" + secret;

        String navigationUrl = NavigationPolicy.createPairingNavigationUrl(pairingUrl, 123456789L);
        URI uri = URI.create(navigationUrl);

        assertNotEquals(pairingUrl, navigationUrl);
        assertEquals("pairingReload=123456789", uri.getRawQuery());
        assertFalse(uri.getRawQuery().contains(secret));
        assertEquals("pair=" + secret, uri.getRawFragment());
        assertTrue(NavigationPolicy.isAllowedPage(navigationUrl));
    }

    @Test public void pairingNavigationChangesForEachScanAndRejectsInvalidInputs() {
        String pairingUrl = "https://remote.example.invalid:8443/remote/#pair=" + "a".repeat(64);

        assertNotEquals(
            NavigationPolicy.createPairingNavigationUrl(pairingUrl, 1L),
            NavigationPolicy.createPairingNavigationUrl(pairingUrl, 2L));
        assertThrows(IllegalArgumentException.class,
            () -> NavigationPolicy.createPairingNavigationUrl("https://evil.example/#pair=" + "a".repeat(64), 1L));
        assertThrows(IllegalArgumentException.class,
            () -> NavigationPolicy.createPairingNavigationUrl(pairingUrl, 0L));
    }
}
