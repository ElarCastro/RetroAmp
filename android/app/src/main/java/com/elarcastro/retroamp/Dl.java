package com.elarcastro.retroamp;

import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import okhttp3.Response;
import okhttp3.ResponseBody;

/** Descargas de YouTube al celular: audio (Música/RetroAmp) o video (Películas/RetroAmp). */
public final class Dl {
    static final class Job { String id, title, ch, t, sub; boolean video; }

    final Context ctx;
    final List<Job> q = new ArrayList<>();
    String cur = "", lastErr = "";
    int done, failed;

    Dl(Context c) {
        ctx = c.getApplicationContext();
        Thread t = new Thread(this::worker, "ra-descargas"); t.setDaemon(true); t.start();
    }

    synchronized int add(String id, String title, String ch, String t, String sub, boolean video) {
        for (Job j : q) if (j.id.equals(id) && j.video == video) return q.size();
        Job j = new Job(); j.id = id; j.title = title; j.ch = ch; j.t = t; j.sub = sub; j.video = video;
        q.add(j); notifyAll();
        return q.size();
    }

    synchronized String status() {
        return "{\"ok\":true,\"pending\":" + q.size() + ",\"current\":" + Api.J(cur) + ",\"done\":" + done + ",\"failed\":" + failed
                + ",\"lastError\":" + Api.J(lastErr) + ",\"dir\":" + Api.J("Música/RetroAmp") + ",\"inApp\":false}";
    }

    private void worker() {
        while (true) {
            Job j;
            synchronized (this) {
                while (q.isEmpty()) { try { wait(); } catch (InterruptedException e) { return; } }
                j = q.get(0);
                cur = j.title == null || j.title.isEmpty() ? j.id : j.title;
            }
            try { save(j); synchronized (this) { done++; } }
            catch (Exception e) {
                String why = e instanceof Net.HttpError ? Net.why(e) : YT.why(e);
                synchronized (this) { failed++; lastErr = cur + ": " + why; }
                Log.w("Descarga " + j.id + ": " + why);
            }
            synchronized (this) { q.remove(j); cur = ""; }
        }
    }

    private void save(Job j) throws Exception {
        YT.Src src = j.video ? YT.muxed(j.id) : YT.audio(j.id);
        String ext = j.video ? "mp4" : src.type.contains("webm") ? "webm" : "m4a";
        String name = downloadName(j);
        if (name.isEmpty()) name = j.id;
        String mime = j.video ? "video/mp4" : src.type;
        String sub = j.sub == null ? "" : j.sub;
        OutputStream out; Uri item = null; File file = null;
        ContentResolver cr = ctx.getContentResolver();
        if (Build.VERSION.SDK_INT >= 29) {
            ContentValues v = new ContentValues();
            v.put(MediaStore.MediaColumns.DISPLAY_NAME, name + "." + ext);
            v.put(MediaStore.MediaColumns.MIME_TYPE, mime);
            v.put(MediaStore.MediaColumns.RELATIVE_PATH, (j.video ? Environment.DIRECTORY_MOVIES : Environment.DIRECTORY_MUSIC) + "/RetroAmp" + (sub.isEmpty() ? "" : "/" + sub));
            v.put(MediaStore.MediaColumns.IS_PENDING, 1);
            Uri coll = j.video ? MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) : MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY);
            item = cr.insert(coll, v);
            if (item == null) throw new Exception("Android no dejó crear el archivo");
            out = cr.openOutputStream(item);
        } else {
            File dir = new File(Environment.getExternalStoragePublicDirectory(j.video ? Environment.DIRECTORY_MOVIES : Environment.DIRECTORY_MUSIC), "RetroAmp" + (sub.isEmpty() ? "" : "/" + sub));
            //noinspection ResultOfMethodCallIgnored
            dir.mkdirs();
            file = new File(dir, name + "." + ext);
            out = new FileOutputStream(file);
        }
        boolean ok = false;
        try (OutputStream o = out) {
            long pos = 0, total = -1;
            byte[] buf = new byte[65536];
            while (total < 0 || pos < total) {
                Response r = Proxy.part(src.url, pos, pos + Proxy.PART - 1);
                try {
                    if (total < 0) {
                        Matcher m = Proxy.CRANGE.matcher(String.valueOf(r.header("Content-Range", "")));
                        total = m.find() ? Long.parseLong(m.group(3)) : -2;
                    }
                    ResponseBody b = r.body();
                    if (b == null) break;
                    long got = 0;
                    try (InputStream in = b.byteStream()) { int n; while ((n = in.read(buf)) > 0) { o.write(buf, 0, n); got += n; } }
                    pos += got;
                    if (got == 0 || total == -2) break;
                } finally { r.close(); }
            }
            ok = true;
        } finally {
            if (item != null) {
                if (ok) { ContentValues v = new ContentValues(); v.put(MediaStore.MediaColumns.IS_PENDING, 0); cr.update(item, v, null, null); }
                else try { cr.delete(item, null, null); } catch (Exception ignored) { }
            } else if (!ok && file != null) { //noinspection ResultOfMethodCallIgnored
                file.delete();
            } else if (file != null) {
                android.media.MediaScannerConnection.scanFile(ctx, new String[]{file.getAbsolutePath()}, null, null);
            }
        }
    }

    // ---- Nombre del archivo: "Artista - Canción" (igual que en Windows) ----
    static String cleanArtist(String ch) {
        String a = ch == null ? "" : ch.trim();
        a = a.replaceAll("(?i)\\s*-\\s*Topic$", "").replaceAll("(?i)\\s*VEVO$", "");
        a = a.replaceAll("(?i)^(Oficial|Official)(?=\\s|[A-ZÁÉÍÓÚÑ]|$)\\s*", "").replaceAll("(?i)\\s*(Oficial|Official)$", "");
        a = a.replaceAll("\\s+", " ").replaceAll("^[ \\-_.]+|[ \\-_.]+$", "");
        return a.isEmpty() ? (ch == null ? "" : ch.trim()) : a;
    }
    static String plain(String x) {
        String d = Normalizer.normalize(x == null ? "" : x.toLowerCase(Locale.ROOT), Normalizer.Form.NFD);
        StringBuilder sb = new StringBuilder();
        for (char c : d.toCharArray()) if (Character.isLetterOrDigit(c)) sb.append(c);
        return sb.toString();
    }
    static String safeFile(String x) {
        String a = (x == null ? "" : x).replaceAll("[\\\\/:*?\"<>|\\x00-\\x1f]", " ").replaceAll("\\s+", " ").trim().replaceAll("[. ]+$", "");
        if (a.length() > 120) a = a.substring(0, 120).replaceAll("[. ]+$", "");
        return a;
    }
    static String downloadName(Job j) {
        String t = j.t == null ? "" : j.t.trim();
        if (t.isEmpty()) return safeFile(j.title);
        String a = cleanArtist(j.ch);
        Matcher m = Pattern.compile("^(.+?)\\s+[-–—]\\s+(.+)$").matcher(t);
        if (m.find()) return safeFile(t);
        if (a.isEmpty() || (!plain(a).isEmpty() && plain(t).contains(plain(a)))) return safeFile(t);
        return safeFile(a + " - " + t);
    }
}
