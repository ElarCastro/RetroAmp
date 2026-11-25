package com.elarcastro.retroamp;

import android.content.Context;
import android.net.Uri;

import org.json.JSONObject;
import org.xmlpull.v1.XmlPullParser;
import org.xmlpull.v1.XmlPullParserFactory;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Calendar;
import java.util.Date;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TimeZone;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.GZIPInputStream;

/** Las mismas rutas /api/... que el motor de Windows, hechas para Android. */
public final class Api implements Http.Handler {
    static final Pattern HTTP_URL = Pattern.compile("^https?://[^\\s\"<>]+$", Pattern.CASE_INSENSITIVE);
    static final Pattern YT_URL = Pattern.compile("^https?://(www\\.|m\\.|music\\.)?(youtube\\.com|youtu\\.be)/", Pattern.CASE_INSENSITIVE);

    final Context ctx;
    final String token;
    final File dir, pls, iptv;
    final Dl dl;

    Api(Context c, String token) {
        ctx = c.getApplicationContext();
        this.token = token;
        dir = ctx.getFilesDir();
        pls = new File(dir, "listas"); pls.mkdirs();
        iptv = new File(dir, "iptv"); iptv.mkdirs();
        Log.file = new File(dir, "registro.txt");
        dl = new Dl(ctx);
    }

    // ---------------- JSON ----------------
    public static String J(String v) { return v == null ? "null" : JSONObject.quote(v); }
    public static String err(String m) { return "{\"ok\":false,\"error\":" + J(m) + "}"; }

    static String itemsJson(List<YT.Item> items, boolean dur) {
        StringBuilder sb = new StringBuilder("{\"ok\":true,\"items\":[");
        for (int i = 0; i < items.size(); i++) {
            YT.Item e = items.get(i);
            if (i > 0) sb.append(',');
            sb.append("{\"id\":").append(J(e.id));
            if (dur) sb.append(",\"dur\":").append(e.dur);
            sb.append(",\"ch\":").append(J(e.ch)).append(",\"title\":").append(J(e.title)).append('}');
        }
        return sb.append("]}").toString();
    }

    static String read(File f) throws IOException {
        try (InputStream in = new FileInputStream(f)) { return new String(Net.readAll(in, 256L * 1024 * 1024), StandardCharsets.UTF_8); }
    }
    static void write(File f, byte[] b) throws IOException {
        File tmp = new File(f.getPath() + ".tmp");
        try (FileOutputStream o = new FileOutputStream(tmp)) { o.write(b); }
        if (!tmp.renameTo(f)) { f.delete(); if (!tmp.renameTo(f)) throw new IOException("no pude guardar " + f.getName()); }
    }
    static String safeName(String n) {
        n = (n == null ? "" : n).replaceAll("[\\\\/:*?\"<>|\\x00-\\x1f]", "").trim().replaceAll("\\.+$", "");
        return n.length() > 60 ? n.substring(0, 60).trim() : n;
    }
    static String md5(String s) {
        try {
            byte[] h = MessageDigest.getInstance("MD5").digest(s.getBytes(StandardCharsets.UTF_8));
            StringBuilder sb = new StringBuilder(); for (byte b : h) sb.append(String.format("%02x", b)); return sb.toString();
        } catch (Exception e) { return Integer.toHexString(s.hashCode()); }
    }
    static String now() { return new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.ROOT).format(new Date()); }
    static String dateOf(File f) { return new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.ROOT).format(new Date(f.lastModified())); }
    static String enc(String s) { try { return URLEncoder.encode(s, "UTF-8").replace("+", "%20"); } catch (Exception e) { return s; } }

    // ---------------- Rutas ----------------
    @Override
    public void handle(Http.Req r, Http.Res s) throws Exception {
        String path = r.path;
        boolean viaQuery = token.equals(r.q1("k"));
        if (!viaQuery && !token.equals(r.cookie("wyt"))) { s.bytes(403, "text/plain; charset=utf-8", "RetroAmp: acceso no permitido".getBytes(StandardCharsets.UTF_8), null); return; }

        if (path.equals("/") || path.equals("/index.html") || path.equals("/RetroAmp.html")) {
            String cookie = viaQuery ? "Set-Cookie: wyt=" + token + "; Path=/; HttpOnly; SameSite=Strict\r\n" : null;
            s.bytes(200, "text/html; charset=utf-8", asset("www/RetroAmp.html"), cookie);
            return;
        }
        if (path.startsWith("/fonts/") || path.startsWith("/lib/") || path.equals("/favicon.ico")) { staticFile(s, path); return; }
        switch (path) {
            case "/api/ping": s.json("{\"ok\":true}"); return;
            case "/api/show": s.json("{\"ok\":true,\"host\":false,\"android\":true}"); return;
            case "/api/note": { String m = r.q1("m"); if (!m.isEmpty()) Log.w("Página: " + (m.length() > 600 ? m.substring(0, 600) : m)); s.json("{\"ok\":true}"); return; }
            case "/api/state": state(r, s); return;
            case "/api/search": search(r, s); return;
            case "/api/searchlists": searchLists(r, s); return;
            case "/api/list": list(r, s); return;
            case "/api/prep": prep(r, s); return;
            case "/api/video/yt": videoYt(r, s); return;
            case "/api/yt/live": ytLive(r, s); return;
            case "/api/pls": plsList(s); return;
            case "/api/pls/get": case "/api/pls/save": case "/api/pls/del": plsOne(r, s, path); return;
            case "/api/mp3": mp3(r, s); return;
            case "/api/mp3/status": s.json(dl.status()); return;
            case "/api/mp3/where": s.json("{\"ok\":true,\"dir\":\"Música/RetroAmp\",\"video\":\"Películas/RetroAmp\",\"old\":\"\"}"); return;
            case "/api/mp3/open": s.json("{\"ok\":true,\"dir\":\"Música/RetroAmp\"}"); return;
            case "/api/lib": s.json(Lib.json(ctx)); return;
            case "/api/lib/scan": s.json("{\"ok\":true,\"dir\":\"Música del celular\",\"missing\":[]}"); return;
            case "/api/lib/status": s.json("{\"ok\":true,\"scanning\":false,\"found\":0,\"dir\":\"Música del celular\"}"); return;
            case "/api/lib/prune": s.json("{\"ok\":true,\"removed\":0}"); return;
            case "/api/lib/dirs": s.json("{\"ok\":true,\"music\":\"Música del celular\",\"media\":[],\"exists\":[]}"); return;
            case "/api/local/prep": localPrep(r, s); return;
            case "/local": local(r, s); return;
            case "/api/local/cover": cover(r, s); return;
            case "/api/radio/prep": radioPrep(r, s); return;
            case "/stream": { String u = r.q1("u"); if (!HTTP_URL.matcher(u).matches()) { s.notFound(); return; } Proxy.radio(s, u); return; }
            case "/api/radio/search": radioSearch(r, s); return;
            case "/api/check": check(r, s); return;
            case "/hls": hls(r, s); return;
            case "/api/tv/get": tvGet(r, s); return;
            case "/api/tv/epg": tvEpg(r, s); return;
            case "/api/skins": s.json("{\"ok\":true,\"skins\":[]}"); return;
            default: break;
        }
        if (path.startsWith("/audio/")) { audio(r, s, path.substring(7)); return; }
        if (path.startsWith("/video/")) { video(r, s, path.substring(7)); return; }
        if (path.startsWith("/api/")) { s.json(err("Esta función no está en la versión para Android")); return; }
        s.notFound();
    }

    // ---------------- Archivos de la app ----------------
    byte[] asset(String name) throws IOException {
        try (InputStream in = ctx.getAssets().open(name)) { return Net.readAll(in, 64L * 1024 * 1024); }
    }
    void staticFile(Http.Res s, String path) throws IOException {
        String name = path.equals("/favicon.ico") ? "icono.png" : path.substring(1);
        if (!name.matches("^(fonts|lib)/[\\w.-]+$") && !name.equals("icono.png")) { s.notFound(); return; }
        byte[] b;
        try { b = asset("www/" + name); } catch (IOException e) { s.notFound(); return; }
        String type = name.endsWith(".js") ? "application/javascript; charset=utf-8" : name.endsWith(".ttf") ? "font/ttf"
                : name.endsWith(".woff2") ? "font/woff2" : name.endsWith(".png") ? "image/png" : "application/octet-stream";
        s.bytes(200, type, b, "Cache-Control: max-age=86400\r\n");
    }

    // ---------------- Estado y listas guardadas ----------------
    void state(Http.Req r, Http.Res s) throws IOException {
        File f = new File(dir, "estado.json");
        if (r.method.equals("POST")) {
            String b = r.bodyText();
            if (!b.startsWith("{")) { s.json(err("datos no validos")); return; }
            write(f, r.body); s.json("{\"ok\":true}");
        } else s.bytes(200, "application/json; charset=utf-8", f.exists() ? read(f).getBytes(StandardCharsets.UTF_8) : "{}".getBytes(StandardCharsets.UTF_8), null);
    }
    void plsList(Http.Res s) throws IOException {
        File[] fs = pls.listFiles((d, n) -> n.endsWith(".json"));
        if (fs == null) fs = new File[0];
        Arrays.sort(fs, (a, b) -> Long.compare(b.lastModified(), a.lastModified()));
        StringBuilder sb = new StringBuilder("{\"ok\":true,\"names\":[");
        for (int i = 0; i < fs.length; i++) { if (i > 0) sb.append(','); String n = fs[i].getName(); sb.append(J(n.substring(0, n.length() - 5))); }
        s.json(sb.append("]}").toString());
    }
    void plsOne(Http.Req r, Http.Res s, String path) throws IOException {
        String nm = safeName(r.q1("name"));
        if (nm.isEmpty()) { s.json(err("Nombre no valido")); return; }
        File f = new File(pls, nm + ".json");
        if (path.endsWith("/get")) { if (f.exists()) s.bytes(200, "application/json; charset=utf-8", read(f).getBytes(StandardCharsets.UTF_8), null); else s.json(err("No existe esa lista")); }
        else if (path.endsWith("/save")) {
            if (!r.bodyText().startsWith("{")) { s.json(err("datos no validos")); return; }
            write(f, r.body); s.json("{\"ok\":true,\"name\":" + J(nm) + "}");
        } else { //noinspection ResultOfMethodCallIgnored
            f.delete(); s.json("{\"ok\":true}");
        }
    }

    // ---------------- YouTube ----------------
    static String term(Http.Req r) { return r.q1("q").replaceAll("[\"\\r\\n]", " ").trim(); }

    void search(Http.Req r, Http.Res s) throws IOException {
        String q = term(r);
        if (q.isEmpty()) { s.json(err("Busqueda vacia")); return; }
        try { s.json(itemsJson(YT.search(q, 20), true)); }
        catch (Exception e) { Log.w("Buscar: " + e); s.json(err(YT.why(e))); }
    }
    void searchLists(Http.Req r, Http.Res s) throws IOException {
        String q = term(r);
        if (q.isEmpty()) { s.json(err("Busqueda vacia")); return; }
        try { s.json(itemsJson(YT.searchLists(q, 12), false)); }
        catch (Exception e) { s.json(err(YT.why(e))); }
    }
    void list(Http.Req r, Http.Res s) throws IOException {
        String u = r.q1("url");
        if (!u.matches("^https://[^\\s\"]+$")) { s.json(err("Enlace no valido")); return; }
        try {
            List<YT.Item> it = YT.list(u);
            s.json(it.isEmpty() ? err("No encontré canciones en ese enlace") : itemsJson(it, true));
        } catch (Exception e) { Log.w("Lista " + u + ": " + e); s.json(err(YT.why(e))); }
    }
    void prep(Http.Req r, Http.Res s) throws IOException {
        String id = r.q1("id");
        if (!YT.VID.matcher(id).matches()) { s.json(err("id no valido")); return; }
        try { YT.audio(id); s.json("{\"ok\":true,\"url\":" + J("/audio/" + id) + ",\"size\":0}"); }
        catch (Exception e) { Log.w("Audio " + id + ": " + e); s.json(err(YT.why(e))); }
    }
    void audio(Http.Req r, Http.Res s, String name) throws Exception {
        String id = name.replaceAll("\\..*$", "");
        if (!YT.VID.matcher(id).matches()) { s.notFound(); return; }
        Proxy.youtube(r, s, fresh -> { if (fresh) YT.forget(id); return YT.audio(id); });
    }
    void videoYt(Http.Req r, Http.Res s) throws IOException {
        String id = r.q1("id");
        if (!YT.VID.matcher(id).matches()) { s.json(err("id no valido")); return; }
        try { YT.video(id); s.json("{\"ok\":true,\"url\":" + J("/video/" + id + ".v.mp4") + "}"); }
        catch (Exception e) { s.json(err(YT.why(e))); }
    }
    void video(Http.Req r, Http.Res s, String name) throws Exception {
        String id = name.replaceAll("\\..*$", "");
        if (!YT.VID.matcher(id).matches()) { s.notFound(); return; }
        Proxy.youtube(r, s, fresh -> { if (fresh) YT.forget(id); return YT.video(id); });
    }
    void ytLive(Http.Req r, Http.Res s) throws IOException {
        String q = term(r);
        if (q.isEmpty()) { s.json(err("Búsqueda vacía")); return; }
        try {
            List<YT.Item> it = YT.searchLive(q, 40);
            StringBuilder sb = new StringBuilder("{\"ok\":true,\"items\":[");
            for (int i = 0; i < it.size(); i++) {
                YT.Item e = it.get(i);
                if (i > 0) sb.append(',');
                sb.append("{\"id\":").append(J(e.id)).append(",\"ch\":").append(J(e.ch)).append(",\"cid\":").append(J(e.cid)).append(",\"title\":").append(J(e.title)).append('}');
            }
            s.json(sb.append("]}").toString());
        } catch (Exception e) { s.json(err("No pude buscar en YouTube: " + YT.why(e))); }
    }
    void mp3(Http.Req r, Http.Res s) throws IOException {
        String id = r.q1("id");
        if (!YT.VID.matcher(id).matches()) { s.json(err("id no valido")); return; }
        int n = dl.add(id, r.q1("title"), r.q1("ch"), r.q1("t"), safeName(r.q1("sub")), "1".equals(r.q1("video")));
        s.json("{\"ok\":true,\"pending\":" + n + "}");
    }

    // ---------------- Música del celular ----------------
    static Uri contentUri(String p) {
        if (p == null || !p.startsWith(Lib.PREFIX)) return null;
        return Uri.parse(p);
    }
    void localPrep(Http.Req r, Http.Res s) throws IOException {
        Uri u = contentUri(r.q1("p"));
        if (u == null) { s.json(err("Ese archivo no se puede reproducir")); return; }
        boolean vid = Lib.isVideo(ctx, u);
        s.json("{\"ok\":true,\"url\":" + J("/local?p=" + enc(u.toString())) + ",\"size\":0,\"video\":" + vid + "}");
    }
    void local(Http.Req r, Http.Res s) throws IOException {
        Uri u = contentUri(r.q1("p"));
        if (u == null) { s.notFound(); return; }
        try { Proxy.content(r, s, ctx, u, Lib.mime(ctx, u)); }
        catch (SecurityException | IOException e) { if (!s.sent) s.notFound(); }
    }
    void cover(Http.Req r, Http.Res s) throws IOException {
        Uri u = contentUri(r.q1("p"));
        byte[] b = u == null ? null : Lib.cover(ctx, u);
        if (b == null) { s.notFound(); return; }
        s.bytes(200, b.length > 3 && b[0] == (byte) 0x89 ? "image/png" : "image/jpeg", b, null);
    }

    // ---------------- Radios ----------------
    static String resolveRadio(String url) throws IOException {
        String p = url.split("\\?")[0].toLowerCase(Locale.ROOT);
        if (!(p.endsWith(".pls") || p.endsWith(".m3u") || p.endsWith(".asx"))) return url;
        Net.Got g = Net.get(url, 15000, 1024 * 1024);
        Matcher m = Pattern.compile("(https?://[^\\s\"<>]+)", Pattern.CASE_INSENSITIVE).matcher(new String(g.body, StandardCharsets.UTF_8));
        if (!m.find()) throw new IOException("la lista de la radio no tiene enlaces");
        return m.group(1);
    }
    void radioPrep(Http.Req r, Http.Res s) throws IOException {
        String u = r.q1("u");
        if (!HTTP_URL.matcher(u).matches()) { s.json(err("Enlace de radio no valido")); return; }
        if (YT_URL.matcher(u).find()) {
            try { YT.liveHls(u); } catch (Exception e) { s.json(err("No pude abrir la radio: " + YT.why(e))); return; }
            s.json("{\"ok\":true,\"hls\":true,\"url\":" + J("/hls?a=1&u=" + enc(u)) + ",\"size\":0}"); return;
        }
        String real;
        try { real = resolveRadio(u); } catch (Exception e) { s.json(err("No pude abrir la radio: " + Net.why(e))); return; }
        Pattern m3u8 = Pattern.compile("\\.m3u8(\\?|$)", Pattern.CASE_INSENSITIVE);
        if (m3u8.matcher(real).find() || m3u8.matcher(u).find()) {
            s.json("{\"ok\":true,\"hls\":true,\"url\":" + J("/hls?u=" + enc(m3u8.matcher(u).find() ? u : real)) + ",\"size\":0}"); return;
        }
        s.json("{\"ok\":true,\"url\":" + J("/stream?u=" + enc(real)) + ",\"size\":0}");
    }
    static final String[] RADIO_HOSTS = {"https://de1.api.radio-browser.info", "https://de2.api.radio-browser.info", "https://fi1.api.radio-browser.info", "https://at1.api.radio-browser.info", "https://nl1.api.radio-browser.info", "https://all.api.radio-browser.info"};
    void radioSearch(Http.Req r, Http.Res s) throws IOException {
        String q = r.q1("q"), cc = r.q1("cc"), tag = r.q1("tag");
        if (!cc.matches("^[A-Za-z]{0,2}$")) cc = "";
        String qs = "limit=200&hidebroken=true&order=clickcount&reverse=true" + (q.isEmpty() ? "" : "&name=" + enc(q)) + (cc.isEmpty() ? "" : "&countrycode=" + cc.toUpperCase(Locale.ROOT)) + (tag.isEmpty() ? "" : "&tag=" + enc(tag));
        String why = "no contestó";
        for (String h : RADIO_HOSTS) {
            try {
                String txt = new String(Net.get(h + "/json/stations/search?" + qs, 15000, 32L * 1024 * 1024).body, StandardCharsets.UTF_8).trim();
                if (!txt.startsWith("[")) { why = "respuesta no válida"; continue; }
                s.json("{\"ok\":true,\"items\":" + txt + "}"); return;
            } catch (Exception e) { why = Net.why(e); }
        }
        s.json(err("No pude buscar radios: " + why));
    }

    // ---------------- ¿Funciona este canal o esta radio? ----------------
    void check(Http.Req r, Http.Res s) throws IOException {
        String u = r.q1("u");
        if (!HTTP_URL.matcher(u).matches()) { s.json(err("Dirección no válida")); return; }
        String why;
        try {
            String target = YT_URL.matcher(u).find() ? YT.liveHls(u) : "radio".equals(r.q1("k")) ? resolveRadio(u) : u;
            why = probe(target, 0);
        } catch (Exception e) { why = e instanceof IOException ? Net.why(e) : YT.why(e); }
        s.json(why == null ? "{\"ok\":true,\"live\":true}" : "{\"ok\":true,\"live\":false,\"why\":" + J(why) + "}");
    }
    static String probe(String url, int depth) {
        okhttp3.Request rq = new okhttp3.Request.Builder().url(url).header("User-Agent", Net.UA).build();
        try (okhttp3.Response resp = Net.C.newBuilder().callTimeout(15, java.util.concurrent.TimeUnit.SECONDS).build().newCall(rq).execute()) {
            if (!resp.isSuccessful()) return Net.why(new Net.HttpError(resp.code()));
            okhttp3.ResponseBody b = resp.body();
            if (b == null) return "respondió vacío";
            String type = b.contentType() == null ? "" : b.contentType().toString().toLowerCase(Locale.ROOT);
            byte[] buf = new byte[depth == 2 ? 2048 : 4096]; int got = 0, n;
            InputStream in = b.byteStream();
            while (got < buf.length && (n = in.read(buf, got, buf.length - got)) > 0) got += n;
            if (got == 0) return "respondió vacío";
            if (depth == 2) return null;
            String head = new String(buf, 0, Math.min(got, 16), StandardCharsets.UTF_8).replaceAll("^[\\uFEFF \\r\\n]+", "");
            if (head.startsWith("#EXTM3U")) {
                ByteArrayOutputStream bo = new ByteArrayOutputStream(); bo.write(buf, 0, got);
                byte[] b2 = new byte[16384];
                while (bo.size() < 2 * 1024 * 1024 && (n = in.read(b2)) > 0) bo.write(b2, 0, n);
                String txt = bo.toString("UTF-8");
                boolean master = txt.contains("#EXT-X-STREAM-INF");
                String next = null;
                for (String line : txt.split("\n")) { String l = line.trim(); if (l.isEmpty() || l.startsWith("#")) continue; next = l; if (master) break; }
                if (next == null || (depth >= 1 && master)) return "la lista del canal no trae video";
                String nu = URI.create(resp.request().url().toString()).resolve(next.trim()).toString();
                return probe(nu, master ? 1 : 2);
            }
            if (type.contains("text/html")) return "la dirección abre una página web, no un canal";
            return null;
        } catch (Exception e) {
            String m = String.valueOf(e.getMessage());
            if (m.contains("ICY")) return null;   // radio SHOUTcast antigua: sí contesta
            return Net.why(e);
        }
    }

    // ---------------- TV por internet (HLS) ----------------
    static final Map<String, byte[]> segCache = new HashMap<>();
    static final ArrayDeque<String> segOrder = new ArrayDeque<>();
    static final Map<String, long[]> segTime = new HashMap<>();

    static String rewriteHls(String text, String base) {
        URI bu = URI.create(base);
        StringBuilder sb = new StringBuilder();
        Pattern uri = Pattern.compile("URI=\"([^\"]+)\"");
        for (String ln : text.split("\r?\n")) {
            String l = ln.replaceAll("\\s+$", "");
            if (l.isEmpty()) { sb.append('\n'); continue; }
            if (l.startsWith("#")) {
                Matcher m = uri.matcher(l); StringBuffer o = new StringBuffer();
                while (m.find()) m.appendReplacement(o, Matcher.quoteReplacement("URI=\"" + wrap(bu, m.group(1)) + "\""));
                m.appendTail(o); l = o.toString();
            } else l = wrap(bu, l);
            sb.append(l).append('\n');
        }
        return sb.toString();
    }
    static String wrap(URI base, String u) {
        try { return "/hls?u=" + enc(base.resolve(u.trim().replace(" ", "%20")).toString()); } catch (Exception e) { return u; }
    }
    static boolean looksPlaylist(String type, String url, byte[] b) {
        if (type != null && type.toLowerCase(Locale.ROOT).contains("mpegurl")) return true;
        if (Pattern.compile("\\.m3u8?(\\?|$)", Pattern.CASE_INSENSITIVE).matcher(url).find()) return true;
        int i = b.length >= 3 && b[0] == (byte) 0xEF && b[1] == (byte) 0xBB && b[2] == (byte) 0xBF ? 3 : 0;
        return b.length - i >= 7 && new String(b, i, 7, StandardCharsets.US_ASCII).equals("#EXTM3U");
    }
    void hls(Http.Req r, Http.Res s) throws IOException {
        String u = r.q1("u");
        if (!HTTP_URL.matcher(u).matches()) { s.notFound(); return; }
        if (YT_URL.matcher(u).find()) {
            try { u = YT.liveHls(u); }
            catch (Exception e) { Log.w("YouTube en vivo " + r.q1("u") + ": " + YT.why(e)); s.bytes(502, "text/plain; charset=utf-8", YT.why(e).getBytes(StandardCharsets.UTF_8), null); return; }
        }
        byte[] body;
        synchronized (segCache) {
            body = segCache.get(u); long[] t = segTime.get(u);
            if (body != null && t != null && System.currentTimeMillis() - t[0] > 90000) body = null;
        }
        if (body != null) { s.bytes(200, "video/mp2t", body, null); return; }
        Net.Got g;
        try { g = Net.get(u, 20000, 64L * 1024 * 1024); }
        catch (Exception e) { s.bytes(502, "text/plain; charset=utf-8", ("No se pudo abrir: " + Net.why(e)).getBytes(StandardCharsets.UTF_8), null); return; }
        if (looksPlaylist(g.type, g.url, g.body)) {
            String txt = rewriteHls(new String(g.body, StandardCharsets.UTF_8), g.url);
            s.bytes(200, "application/vnd.apple.mpegurl", txt.getBytes(StandardCharsets.UTF_8), null);
            return;
        }
        if (g.body.length < 8 * 1024 * 1024) synchronized (segCache) {
            segCache.put(u, g.body); segTime.put(u, new long[]{System.currentTimeMillis()}); segOrder.add(u);
            while (segOrder.size() > 40) { String k = segOrder.poll(); segCache.remove(k); segTime.remove(k); }
        }
        s.bytes(200, g.type == null || g.type.isEmpty() ? "video/mp2t" : g.type, g.body, null);
    }

    void tvGet(Http.Req r, Http.Res s) throws IOException {
        String u = r.q1("u");
        boolean force = "1".equals(r.q1("refresh"));
        if (!HTTP_URL.matcher(u).matches()) { s.json(err("La dirección de la lista no es válida")); return; }
        String nm = r.q1("n").replaceAll("[\\\\/:*?\"<>|\\x00-\\x1f]", "").trim().replaceAll("\\.+$", "");
        if (nm.length() > 40) nm = nm.substring(0, 40);
        File cf = new File(iptv, (nm.isEmpty() ? "lista" : nm) + " - " + md5(u).substring(0, 6) + ".m3u");
        boolean have = cf.exists();
        if (have && !force && System.currentTimeMillis() - cf.lastModified() < 24 * 3600 * 1000L) {
            s.json("{\"ok\":true,\"text\":" + J(read(cf)) + ",\"date\":" + J(dateOf(cf)) + "}"); return;
        }
        try {
            Net.Got g = Net.get(u, 45000, 64L * 1024 * 1024);
            String txt = new String(g.body, StandardCharsets.UTF_8);
            if (!txt.replaceAll("^[\\uFEFF \\r\\n]+", "").startsWith("#EXTM3U") && !txt.contains("#EXTINF")) { s.json(err("Esa dirección no tiene una lista M3U de canales")); return; }
            write(cf, g.body);
            s.json("{\"ok\":true,\"text\":" + J(txt) + ",\"date\":" + J(now()) + "}");
        } catch (Exception e) {
            if (have) s.json("{\"ok\":true,\"stale\":true,\"error\":" + J(Net.why(e)) + ",\"text\":" + J(read(cf)) + ",\"date\":" + J(dateOf(cf)) + "}");
            else s.json(err(Net.why(e)));
        }
    }

    final Map<String, Object[]> epgCache = new HashMap<>();
    void tvEpg(Http.Req r, Http.Res s) throws IOException {
        String u = r.q1("u");
        if (!HTTP_URL.matcher(u).matches()) { s.json(err("La dirección de la guía no es válida")); return; }
        boolean force = "1".equals(r.q1("refresh"));
        File cf = new File(iptv, "guia - " + md5(u).substring(0, 6) + ".xmltv");
        boolean have = cf.exists(); String warn = null;
        if (!have || force || System.currentTimeMillis() - cf.lastModified() >= 12 * 3600 * 1000L) {
            try { Net.Got g = Net.get(u, 90000, 256L * 1024 * 1024); write(cf, g.body); have = true; }
            catch (Exception e) { if (!have) { s.json(err(Net.why(e))); return; } warn = "Sin conexión: se usa la guía guardada"; }
        }
        try { s.json(epgJson(cf, warn)); }
        catch (Exception e) { s.json(err("La guía no se pudo leer: " + e.getMessage())); }
    }
    String epgJson(File f, String warn) throws Exception {
        synchronized (epgCache) {
            Object[] c = epgCache.get(f.getPath());
            if (c != null && (Long) c[0] == f.lastModified() && warn == null) return (String) c[1];
        }
        long now = System.currentTimeMillis() / 1000, from = now - 3 * 3600, to = now + 12 * 3600;
        Map<String, List<String>> names = new LinkedHashMap<>();
        Map<String, List<String>> prog = new LinkedHashMap<>();
        try (InputStream raw = new FileInputStream(f)) {
            InputStream in = new java.io.BufferedInputStream(raw);
            in.mark(4); int b1 = in.read(), b2 = in.read(); in.reset();
            if (b1 == 0x1f && b2 == 0x8b) in = new GZIPInputStream(in);
            XmlPullParser x = XmlPullParserFactory.newInstance().newPullParser();
            x.setInput(in, null);
            String curId = null, curTitle = null; long a = 0, z = 0; List<String> curNames = null; boolean inProg = false;
            int ev;
            while ((ev = x.next()) != XmlPullParser.END_DOCUMENT) {
                if (ev == XmlPullParser.START_TAG) {
                    String n = x.getName();
                    if ("channel".equals(n)) { curId = x.getAttributeValue(null, "id"); curNames = new ArrayList<>(); inProg = false; }
                    else if ("display-name".equals(n) && curNames != null && !inProg) { String v = x.nextText().trim(); if (!v.isEmpty()) curNames.add(v); }
                    else if ("programme".equals(n)) {
                        inProg = true; curId = x.getAttributeValue(null, "channel"); curTitle = null;
                        a = xmltv(x.getAttributeValue(null, "start")); z = xmltv(x.getAttributeValue(null, "stop"));
                        if (curId == null || a == 0 || a > to || (z != 0 && z < from)) { skip(x); inProg = false; }
                    } else if ("title".equals(n) && inProg && curTitle == null) curTitle = x.nextText().trim();
                } else if (ev == XmlPullParser.END_TAG) {
                    String n = x.getName();
                    if ("channel".equals(n) && curId != null && curNames != null) { names.put(curId, curNames); curNames = null; }
                    else if ("programme".equals(n) && inProg) {
                        List<String> l = prog.get(curId); if (l == null) { l = new ArrayList<>(); prog.put(curId, l); }
                        if (l.size() < 60) l.add("[" + a + "," + z + "," + J(curTitle == null ? "" : curTitle) + "]");
                        inProg = false;
                    }
                }
            }
        }
        StringBuilder sb = new StringBuilder("{\"ok\":true");
        if (warn != null) sb.append(",\"warn\":").append(J(warn));
        sb.append(",\"date\":").append(J(dateOf(f))).append(",\"ch\":{");
        boolean first = true;
        for (Map.Entry<String, List<String>> kv : names.entrySet()) {
            if (!prog.containsKey(kv.getKey())) continue;
            if (!first) sb.append(','); first = false;
            sb.append(J(kv.getKey())).append(":[");
            for (int i = 0; i < kv.getValue().size(); i++) { if (i > 0) sb.append(','); sb.append(J(kv.getValue().get(i))); }
            sb.append(']');
        }
        sb.append("},\"pr\":{"); first = true;
        for (Map.Entry<String, List<String>> kv : prog.entrySet()) {
            if (!first) sb.append(','); first = false;
            sb.append(J(kv.getKey())).append(":[").append(String.join(",", kv.getValue())).append(']');
        }
        String res = sb.append("}}").toString();
        synchronized (epgCache) { epgCache.put(f.getPath(), new Object[]{f.lastModified(), res}); }
        return res;
    }
    static void skip(XmlPullParser x) throws Exception {
        int depth = 1;
        while (depth > 0) { int e = x.next(); if (e == XmlPullParser.START_TAG) depth++; else if (e == XmlPullParser.END_TAG) depth--; else if (e == XmlPullParser.END_DOCUMENT) return; }
    }
    /** "20261121183000 +0000" → segundos desde 1970 (UTC) */
    static long xmltv(String t) {
        if (t == null) return 0;
        t = t.trim();
        if (t.length() < 12) return 0;
        String d = t.split(" ")[0];
        if (d.length() > 14) d = d.substring(0, 14);
        try {
            Calendar c = Calendar.getInstance(TimeZone.getTimeZone("UTC"));
            c.clear();
            c.set(Integer.parseInt(d.substring(0, 4)), Integer.parseInt(d.substring(4, 6)) - 1, Integer.parseInt(d.substring(6, 8)),
                    Integer.parseInt(d.substring(8, 10)), Integer.parseInt(d.substring(10, 12)), d.length() >= 14 ? Integer.parseInt(d.substring(12, 14)) : 0);
            long sec = c.getTimeInMillis() / 1000;
            Matcher m = Pattern.compile("([+-])(\\d\\d)(\\d\\d)\\s*$").matcher(t);
            if (m.find()) { int off = Integer.parseInt(m.group(2)) * 3600 + Integer.parseInt(m.group(3)) * 60; sec += m.group(1).equals("+") ? -off : off; }
            return sec;
        } catch (Exception e) { return 0; }
    }
}
