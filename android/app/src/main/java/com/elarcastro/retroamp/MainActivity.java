package com.elarcastro.retroamp;

import android.Manifest;
import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.webkit.ConsoleMessage;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import org.json.JSONObject;

import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.List;

/** RetroAmp para Android: la misma página del programa de Windows dentro de un WebView, con su motor local. */
public class MainActivity extends Activity {
    static volatile MainActivity inst;
    static Http http;
    static Api api;
    static String token;

    WebView web;
    FrameLayout root;
    View fullView;
    WebChromeClient.CustomViewCallback fullCb;

    static synchronized void startEngine(Activity a) throws Exception {
        if (http != null) return;
        byte[] b = new byte[16]; new SecureRandom().nextBytes(b);
        StringBuilder sb = new StringBuilder(); for (byte x : b) sb.append(String.format("%02x", x));
        token = sb.toString();
        api = new Api(a, token);
        http = new Http(api);
    }
    static String base() { return "http://127.0.0.1:" + http.port(); }

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        inst = this;
        Window w = getWindow();
        w.setStatusBarColor(Color.BLACK);
        w.setNavigationBarColor(Color.BLACK);
        // el motor (servidor local) se abre en otro hilo: Android no deja usar la red en el hilo de la pantalla
        Thread th = new Thread(() -> { try { startEngine(this); } catch (Exception e) { Log.w("Motor: " + e); } });
        th.start();
        try { th.join(5000); } catch (InterruptedException ignored) { }

        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        web = new WebView(this);
        root.addView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        setContentView(root);
        // Android 15 dibuja la app detrás de las barras del sistema: se deja el espacio de la barra de arriba,
        // la de abajo y el teclado
        if (Build.VERSION.SDK_INT >= 30) {
            root.setOnApplyWindowInsetsListener((v, ins) -> {
                android.graphics.Insets i = ins.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime() | WindowInsets.Type.displayCutout());
                v.setPadding(i.left, i.top, i.right, i.bottom);
                return WindowInsets.CONSUMED;
            });
        } else root.setFitsSystemWindows(true);

        WebSettings ws = web.getSettings();
        ws.setJavaScriptEnabled(true);
        ws.setDomStorageEnabled(true);
        ws.setMediaPlaybackRequiresUserGesture(false);
        ws.setAllowFileAccess(false);
        ws.setTextZoom(100);
        ws.setUserAgentString(ws.getUserAgentString() + " RetroAmpAndroid/" + version());
        CookieManager.getInstance().setAcceptCookie(true);
        web.setBackgroundColor(Color.BLACK);
        web.addJavascriptInterface(new Bridge(), "RA");
        web.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest rq) {
                Uri u = rq.getUrl();
                if ("127.0.0.1".equals(u.getHost())) return false;
                openExternal(u.toString());
                return true;
            }
        });
        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onConsoleMessage(ConsoleMessage m) {
                if (m.messageLevel() == ConsoleMessage.MessageLevel.ERROR) Log.w("JS: " + m.message() + " (" + m.lineNumber() + ")");
                return true;
            }
            @Override
            public void onShowCustomView(View view, CustomViewCallback cb) {   // video a pantalla completa
                if (fullView != null) { cb.onCustomViewHidden(); return; }
                fullView = view; fullCb = cb;
                root.addView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
                web.setVisibility(View.GONE);
            }
            @Override
            public void onHideCustomView() {
                if (fullView == null) return;
                root.removeView(fullView); fullView = null;
                web.setVisibility(View.VISIBLE);
                if (fullCb != null) fullCb.onCustomViewHidden();
                fullCb = null;
            }
        });
        if (http != null) web.loadUrl(base() + "/?k=" + token);
        else web.loadData("<h2 style='color:#0f0;background:#000'>RetroAmp no pudo iniciar su motor.</h2>", "text/html", "utf-8");
        askNotifications();
    }

    void askNotifications() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 7);
    }

    String version() {
        try { return getPackageManager().getPackageInfo(getPackageName(), 0).versionName; } catch (Exception e) { return "?"; }
    }

    void openExternal(String url) {
        try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url))); } catch (ActivityNotFoundException ignored) { }
    }

    /** Ejecuta JavaScript en la página (desde cualquier hilo). */
    static void js(String code) {
        MainActivity a = inst;
        if (a == null || a.web == null) return;
        a.runOnUiThread(() -> { if (a.web != null) a.web.evaluateJavascript(code, null); });
    }

    @Override
    public void onBackPressed() {
        if (fullView != null) { js("document.exitFullscreen&&document.exitFullscreen()"); return; }
        web.evaluateJavascript("window.raBack?String(raBack()):'false'", v -> {
            if (!"\"true\"".equals(v) && !"true".equals(v)) moveTaskToBack(true);   // se sigue escuchando en segundo plano
        });
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] perms, int[] res) {
        super.onRequestPermissionsResult(code, perms, res);
        if (code == 9) {
            boolean ok = Lib.allowed(this);
            js("window.raPerm&&raPerm(" + ok + ")");
        }
    }

    @Override
    protected void onDestroy() {
        if (inst == this) inst = null;
        if (isFinishing()) {
            stopService(new Intent(this, PlayerService.class));
            if (web != null) { web.destroy(); web = null; }
        }
        super.onDestroy();
    }

    /** Lo que la página puede pedirle a Android. */
    final class Bridge {
        @JavascriptInterface public String version() { return MainActivity.this.version(); }
        @JavascriptInterface public void open(String url) { if (url != null && url.matches("^https?://.+")) runOnUiThread(() -> openExternal(url)); }
        @JavascriptInterface public boolean hasMedia() { return Lib.allowed(MainActivity.this); }
        @JavascriptInterface public void askMedia() {
            runOnUiThread(() -> {
                List<String> p = new ArrayList<>();
                if (Build.VERSION.SDK_INT >= 33) { p.add("android.permission.READ_MEDIA_AUDIO"); p.add("android.permission.READ_MEDIA_VIDEO"); }
                else p.add(Manifest.permission.READ_EXTERNAL_STORAGE);
                if (Build.VERSION.SDK_INT < 29) p.add(Manifest.permission.WRITE_EXTERNAL_STORAGE);
                requestPermissions(p.toArray(new String[0]), 9);
            });
        }
        @JavascriptInterface public void screenOn(boolean on) {
            runOnUiThread(() -> { if (on) getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); else getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); });
        }
        /** Lo que está sonando: título, artista, estado (play/pause/stop/load) y carátula. */
        @JavascriptInterface public void media(String json) {
            try {
                JSONObject o = new JSONObject(json);
                PlayerService.update(MainActivity.this, o.optString("title"), o.optString("artist"), o.optString("state"), o.optString("cover"));
            } catch (Exception e) { Log.w("media: " + e); }
        }
        @JavascriptInterface public void share(String text) {
            runOnUiThread(() -> {
                Intent i = new Intent(Intent.ACTION_SEND); i.setType("text/plain"); i.putExtra(Intent.EXTRA_TEXT, text);
                try { startActivity(Intent.createChooser(i, "Compartir")); } catch (Exception ignored) { }
            });
        }
        @JavascriptInterface public void exit() {
            runOnUiThread(() -> { stopService(new Intent(MainActivity.this, PlayerService.class)); finishAndRemoveTask(); });
        }
    }
}
