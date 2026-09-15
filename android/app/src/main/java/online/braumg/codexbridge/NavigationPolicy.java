package online.braumg.codexbridge;

import java.net.URI;

public final class NavigationPolicy {
    private static final String HOST = "remote.example.invalid";
    private static final int PORT = 8443;
    private static final String PATH = "/remote/";

    private NavigationPolicy() { }

    public static boolean isAllowedPage(String value) {
        URI uri = parse(value);
        return uri != null && "https".equals(uri.getScheme()) && HOST.equals(uri.getHost())
            && uri.getPort() == PORT && PATH.equals(uri.getPath()) && uri.getRawUserInfo() == null;
    }

    public static boolean isValidPairingUrl(String value) {
        URI uri = parse(value);
        if (!isAllowedPage(value) || uri == null || uri.getRawQuery() != null || uri.getRawFragment() == null) return false;
        String fragment = uri.getRawFragment();
        if (!fragment.startsWith("pair=") || fragment.indexOf('&') >= 0) return false;
        String pair = fragment.substring(5);
        return pair.length() >= 32 && pair.length() <= 8192 && pair.matches("[A-Za-z0-9_-]+");
    }

    public static String createPairingNavigationUrl(String pairingUrl, long nonce) {
        if (!isValidPairingUrl(pairingUrl) || nonce <= 0)
            throw new IllegalArgumentException("Invalid pairing navigation input.");
        URI uri = URI.create(pairingUrl);
        try {
            return new URI(
                uri.getScheme(),
                null,
                uri.getHost(),
                uri.getPort(),
                uri.getPath(),
                "pairingReload=" + nonce,
                uri.getRawFragment()).toString();
        } catch (java.net.URISyntaxException exception) {
            throw new IllegalArgumentException("Invalid pairing URL.", exception);
        }
    }

    private static URI parse(String value) {
        if (value == null || value.length() > 12_000 || value.indexOf('\\') >= 0) return null;
        try { return URI.create(value); }
        catch (IllegalArgumentException ignored) { return null; }
    }
}
