package online.braumg.codexbridge;

import java.net.URI;
import org.json.JSONException;
import org.json.JSONObject;

public record UpdateManifest(int versionCode, String versionName, String sha256, long size, String downloadUrl) {
    public static UpdateManifest parse(String json) {
        try {
            JSONObject value = new JSONObject(json);
            if (value.optInt("schemaVersion", -1) != 1) throw new IllegalArgumentException("update_schema_invalid");
            int versionCode = value.optInt("versionCode", -1);
            String versionName = value.optString("versionName", "");
            String sha256 = value.optString("sha256", "");
            long size = value.optLong("size", -1);
            String downloadUrl = value.optString("downloadUrl", "");
            if (versionCode < 1 || !versionName.matches("[0-9A-Za-z][0-9A-Za-z._-]{0,63}") ||
                !sha256.matches("[0-9a-f]{64}") || size < 1 || size > 200L * 1024 * 1024 ||
                !isTrustedDownload(downloadUrl)) throw new IllegalArgumentException("update_manifest_invalid");
            return new UpdateManifest(versionCode, versionName, sha256, size, downloadUrl);
        } catch (JSONException exception) {
            throw new IllegalArgumentException("update_json_invalid", exception);
        }
    }

    static boolean isTrustedDownload(String value) {
        try {
            URI uri = URI.create(value);
            return "https".equals(uri.getScheme()) && "remote.example.invalid".equals(uri.getHost()) &&
                uri.getPort() == 8443 && uri.getRawUserInfo() == null && uri.getRawQuery() == null &&
                uri.getRawFragment() == null && uri.getPath().startsWith("/android/") && uri.getPath().endsWith(".apk");
        } catch (IllegalArgumentException ignored) { return false; }
    }
}
