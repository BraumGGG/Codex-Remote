package online.braumg.codexbridge;

import java.io.ByteArrayOutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Consumer;

public final class UpdateChecker implements AutoCloseable {
    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    public void check(Consumer<UpdateManifest> onUpdate) {
        executor.execute(() -> {
            try {
                UpdateManifest manifest = UpdateManifest.parse(download(BuildConfig.UPDATE_URL));
                if (manifest.versionCode() > BuildConfig.VERSION_CODE) onUpdate.accept(manifest);
            } catch (Exception ignored) {
                // Updates are advisory and must never block the primary remote-control workflow.
            }
        });
    }

    private static String download(String address) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(address).openConnection();
        connection.setConnectTimeout(8_000);
        connection.setReadTimeout(8_000);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestProperty("Accept", "application/json");
        connection.connect();
        try {
            if (connection.getResponseCode() != 200 || connection.getContentLengthLong() > 16 * 1024) {
                throw new IllegalStateException("update_unavailable");
            }
            try (var input = connection.getInputStream(); var output = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[4096];
                int total = 0;
                for (int count; (count = input.read(buffer)) != -1;) {
                    total += count;
                    if (total > 16 * 1024) throw new IllegalStateException("update_too_large");
                    output.write(buffer, 0, count);
                }
                return new String(output.toByteArray(), StandardCharsets.UTF_8);
            }
        } finally { connection.disconnect(); }
    }

    @Override public void close() { executor.shutdownNow(); }
}
