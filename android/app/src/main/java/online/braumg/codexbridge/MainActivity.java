package online.braumg.codexbridge;

import android.annotation.SuppressLint;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.SslErrorHandler;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.webkit.ConsoleMessage;
import android.util.Log;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.Space;
import android.widget.TextView;
import androidx.activity.OnBackPressedCallback;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.app.AlertDialog;
import androidx.webkit.WebSettingsCompat;
import androidx.webkit.WebViewFeature;
import com.journeyapps.barcodescanner.ScanContract;
import com.journeyapps.barcodescanner.ScanOptions;

public final class MainActivity extends AppCompatActivity {
    private WebView webView;
    private ProgressBar progress;
    private LinearLayout errorPanel;
    private TextView errorText;
    private boolean exitPrepared;
    private void diagnostic(String event, String detail) {
        String line = System.currentTimeMillis() + "\t" + BuildConfig.UI_BUILD + "\t" + event + "\t" + detail + "\n";
        try (FileOutputStream out = openFileOutput("codex-remote.log", MODE_APPEND)) {
            out.write(line.getBytes(StandardCharsets.UTF_8));
        } catch (Exception ignored) { }
        Log.d("CodexRemote", event + ": " + detail);
    }
    private final UpdateChecker updateChecker = new UpdateChecker();
    private final ScanContract scanContract = new ScanContract();

    private final androidx.activity.result.ActivityResultLauncher<ScanOptions> scanner =
        registerForActivityResult(scanContract, result -> {
            if (result.getContents() == null) return;
            if (!NavigationPolicy.isValidPairingUrl(result.getContents())) {
                showError("二维码不是有效的 Codex Remote 公网配对链接");
                return;
            }
            webView.loadUrl(NavigationPolicy.createPairingNavigationUrl(
                result.getContents(),
                SystemClock.elapsedRealtimeNanos()));
        });

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        buildLayout();
        configureWebView();
        getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) {
            @Override public void handleOnBackPressed() {
                webView.evaluateJavascript(
                    "window.CodexBridgeApp?.handleBack?.() === true",
                    value -> {
                        if ("true".equals(value)) return;
                        showExitConfirmation();
                    });
            }
        });
        loadIntentOrHome();
        updateChecker.check(manifest -> runOnUiThread(() -> showUpdate(manifest)));
    }

    @Override
    protected void onNewIntent(android.content.Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        loadIntentOrHome();
    }

    @Override protected void onResume() {
        super.onResume();
        if (webView != null) {
            webView.postDelayed(() -> webView.evaluateJavascript(
                "window.CodexBridgeApp?.resume?.()", null), 250);
        }
    }

    private void showExitConfirmation() {
        new AlertDialog.Builder(this)
            .setTitle("确认退出")
            .setMessage("是否确认退出 Codex Remote？")
            .setNegativeButton("否", (dialog, which) -> moveTaskToBack(true))
            .setPositiveButton("是", (dialog, which) -> {
                prepareWebConnectionForExit(this::finishAndRemoveTask);
            })
            .show();
    }

    private void prepareWebConnectionForExit(Runnable completion) {
        if (exitPrepared || webView == null) {
            completion.run();
            return;
        }
        exitPrepared = true;
        webView.evaluateJavascript(
            "window.CodexBridgeApp?.prepareForExit?.()",
            ignored -> completion.run());
    }

    private void loadIntentOrHome() {
        Uri data = getIntent().getData();
        String url = data == null ? BuildConfig.REMOTE_URL : data.toString();
        diagnostic("load_url", url);
        if (data != null && NavigationPolicy.isValidPairingUrl(url)) {
            webView.loadUrl(NavigationPolicy.createPairingNavigationUrl(
                url,
                SystemClock.elapsedRealtimeNanos()));
        } else if (NavigationPolicy.isAllowedPage(url)) webView.loadUrl(url);
        else showError("链接不属于 Codex Remote 公网服务");
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void configureWebView() {
        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setCacheMode(WebSettings.LOAD_NO_CACHE);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setMediaPlaybackRequiresUserGesture(true);
        settings.setSupportMultipleWindows(false);
        settings.setJavaScriptCanOpenWindowsAutomatically(false);
        if (WebViewFeature.isFeatureSupported(WebViewFeature.ALGORITHMIC_DARKENING)) {
            WebSettingsCompat.setAlgorithmicDarkeningAllowed(settings, false);
        }
        android.webkit.CookieManager.getInstance().setAcceptThirdPartyCookies(webView, false);
        webView.clearCache(false);
        webView.addJavascriptInterface(new NativeBridge(new DeviceIdentity(getApplicationContext()), this::startScan), "CodexBridgeNative");
        webView.setWebChromeClient(new WebChromeClient() {
            @Override public boolean onConsoleMessage(ConsoleMessage message) {
                diagnostic("console", message.messageLevel() + ":" + message.sourceId() + ":" + message.lineNumber() + ":" + message.message());
                return true;
            }
            @Override public void onProgressChanged(WebView view, int value) {
                progress.setProgress(value);
                progress.setVisibility(value < 100 ? View.VISIBLE : View.GONE);
            }
        });
        webView.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return !request.isForMainFrame() || !NavigationPolicy.isAllowedPage(request.getUrl().toString());
            }
            @Override public void onPageFinished(WebView view, String url) {
                diagnostic("page_finished", url);
                if (NavigationPolicy.isAllowedPage(url)) hideError();
            }
            @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                diagnostic("web_error", request.getUrl() + " code=" + error.getErrorCode() + " " + error.getDescription());
                if (request.isForMainFrame()) showError("无法连接公网服务，请检查网络后重试");
            }
            @Override public void onReceivedSslError(WebView view, SslErrorHandler handler, android.net.http.SslError error) {
                diagnostic("ssl_error", error.toString());
                handler.cancel();
                showError("安全证书验证失败，连接已阻止");
            }
        });
    }

    private void startScan() {
        runOnUiThread(() -> scanner.launch(new ScanOptions()
            .setPrompt("扫描 Windows 端显示的公网配对二维码")
            .setBeepEnabled(false)
            .setOrientationLocked(false)
            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)));
    }

    private void buildLayout() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.WHITE);
        webView = new WebView(this);
        root.addView(webView, new FrameLayout.LayoutParams(-1, -1));

        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(100);
        root.addView(progress, new FrameLayout.LayoutParams(-1, dp(3), Gravity.TOP));

        errorPanel = new LinearLayout(this);
        errorPanel.setOrientation(LinearLayout.VERTICAL);
        errorPanel.setGravity(Gravity.CENTER);
        errorPanel.setPadding(dp(24), dp(32), dp(24), dp(32));
        errorPanel.setBackgroundColor(Color.rgb(248, 249, 251));

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(32), dp(32), dp(32), dp(32));
        card.setElevation(dp(8));
        card.setBackground(roundedBackground(Color.WHITE, Color.rgb(228, 228, 231), 16));
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(-1, ViewGroup.LayoutParams.WRAP_CONTENT);
        cardParams.setMargins(0, 0, 0, 0);
        errorPanel.addView(card, cardParams);

        LinearLayout brand = new LinearLayout(this);
        brand.setGravity(Gravity.CENTER_VERTICAL);
        TextView mark = new TextView(this);
        mark.setText("C");
        mark.setTextColor(Color.WHITE);
        mark.setTextSize(14);
        mark.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        mark.setGravity(Gravity.CENTER);
        mark.setBackground(roundedBackground(Color.rgb(99, 102, 241), Color.TRANSPARENT, 9));
        brand.addView(mark, new LinearLayout.LayoutParams(dp(36), dp(36)));
        TextView product = new TextView(this);
        product.setText("Codex Remote");
        product.setTextColor(Color.rgb(10, 10, 11));
        product.setTextSize(15);
        product.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        LinearLayout.LayoutParams productParams = new LinearLayout.LayoutParams(-2, -2);
        productParams.setMargins(dp(12), 0, 0, 0);
        brand.addView(product, productParams);
        card.addView(brand);

        Space brandGap = new Space(this);
        card.addView(brandGap, new LinearLayout.LayoutParams(1, dp(42)));
        TextView eyebrow = new TextView(this);
        eyebrow.setText("SECURE REMOTE ACCESS");
        eyebrow.setTextColor(Color.rgb(79, 70, 229));
        eyebrow.setTextSize(11);
        eyebrow.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        card.addView(eyebrow);
        TextView title = new TextView(this);
        title.setText("连接未完成");
        title.setTextColor(Color.rgb(10, 10, 11));
        title.setTextSize(25);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(-1, -2);
        titleParams.setMargins(0, dp(8), 0, 0);
        card.addView(title, titleParams);
        errorText = new TextView(this);
        errorText.setTextColor(Color.rgb(113, 113, 122));
        errorText.setTextSize(14);
        errorText.setGravity(Gravity.START);
        errorText.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES);
        LinearLayout.LayoutParams errorParams = new LinearLayout.LayoutParams(-1, ViewGroup.LayoutParams.WRAP_CONTENT);
        errorParams.setMargins(0, dp(10), 0, dp(18));
        card.addView(errorText, errorParams);
        Button retry = actionButton("重试", "重新连接公网服务");
        retry.setTextColor(Color.WHITE);
        retry.setBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.rgb(99, 102, 241)));
        retry.setOnClickListener(view -> webView.loadUrl(BuildConfig.REMOTE_URL));
        card.addView(retry, buttonParams());
        Button scan = actionButton("扫码配对", "扫描 Windows 端公网配对二维码");
        scan.setTextColor(Color.rgb(79, 70, 229));
        scan.setBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.rgb(238, 242, 255)));
        scan.setOnClickListener(view -> startScan());
        card.addView(scan, buttonParams());
        errorPanel.setVisibility(View.GONE);
        root.addView(errorPanel, new FrameLayout.LayoutParams(-1, -1));
        setContentView(root);
    }

    private Button actionButton(String text, String description) {
        Button button = new Button(this);
        button.setText(text);
        button.setContentDescription(description);
        button.setMinHeight(dp(48));
        button.setAllCaps(false);
        return button;
    }

    private LinearLayout.LayoutParams buttonParams() {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(52));
        params.setMargins(0, dp(16), 0, 0);
        return params;
    }

    private GradientDrawable roundedBackground(int fill, int stroke, int radiusDp) {
        GradientDrawable background = new GradientDrawable();
        background.setColor(fill);
        background.setCornerRadius(dp(radiusDp));
        if (stroke != Color.TRANSPARENT) background.setStroke(dp(1), stroke);
        return background;
    }

    private void showError(String message) {
        runOnUiThread(() -> {
            errorText.setText(message);
            errorPanel.setVisibility(View.VISIBLE);
            errorText.announceForAccessibility(message);
        });
    }

    private void hideError() { errorPanel.setVisibility(View.GONE); }
    private void showUpdate(UpdateManifest manifest) {
        String shortHash = manifest.sha256().substring(0, 16) + "..." + manifest.sha256().substring(56);
        new AlertDialog.Builder(this)
            .setTitle("发现新版本 " + manifest.versionName())
            .setMessage("安装包 SHA-256\n" + shortHash + "\n\n下载后请核对完整哈希。")
            .setNegativeButton("稍后", null)
            .setPositiveButton("打开下载页", (dialog, which) -> startActivity(new android.content.Intent(
                android.content.Intent.ACTION_VIEW, Uri.parse(manifest.downloadUrl()))))
            .show();
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    @Override protected void onDestroy() {
        if (webView != null) {
            webView.removeJavascriptInterface("CodexBridgeNative");
            webView.stopLoading();
            webView.destroy();
            webView = null;
        }
        updateChecker.close();
        super.onDestroy();
    }
}
