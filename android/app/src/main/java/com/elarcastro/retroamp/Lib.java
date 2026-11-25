package com.elarcastro.retroamp;

import android.content.ContentUris;
import android.content.Context;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.media.MediaMetadataRetriever;
import android.net.Uri;
import android.os.Build;
import android.provider.MediaStore;
import android.util.Size;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/** Música y videos del celular (lo que Android ya tiene indexado: no hace falta escanear carpetas). */
public final class Lib {
    public static final String PREFIX = "content://";

    public static boolean allowed(Context c) {
        String p = Build.VERSION.SDK_INT >= 33 ? "android.permission.READ_MEDIA_AUDIO" : "android.permission.READ_EXTERNAL_STORAGE";
        return c.checkSelfPermission(p) == android.content.pm.PackageManager.PERMISSION_GRANTED;
    }

    public static String json(Context c) throws Exception {
        JSONArray items = new JSONArray();
        if (allowed(c)) {
            add(c, items, MediaStore.Audio.Media.EXTERNAL_CONTENT_URI, false);
            boolean vid = Build.VERSION.SDK_INT >= 33
                    ? c.checkSelfPermission("android.permission.READ_MEDIA_VIDEO") == android.content.pm.PackageManager.PERMISSION_GRANTED : true;
            if (vid) add(c, items, MediaStore.Video.Media.EXTERNAL_CONTENT_URI, true);
        }
        JSONObject o = new JSONObject();
        o.put("ok", true);
        o.put("dir", allowed(c) ? "Música del celular" : "");
        o.put("dirs", new JSONArray());
        o.put("scanned", new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.ROOT).format(new Date()));
        o.put("needPerm", !allowed(c));
        o.put("items", items);
        return o.toString();
    }

    static void add(Context c, JSONArray items, Uri base, boolean video) {
        String[] cols = video
                ? new String[]{MediaStore.Video.Media._ID, MediaStore.Video.Media.TITLE, MediaStore.Video.Media.ARTIST, MediaStore.Video.Media.ALBUM, MediaStore.Video.Media.DURATION}
                : new String[]{MediaStore.Audio.Media._ID, MediaStore.Audio.Media.TITLE, MediaStore.Audio.Media.ARTIST, MediaStore.Audio.Media.ALBUM, MediaStore.Audio.Media.DURATION};
        String sel = video ? null : MediaStore.Audio.Media.IS_MUSIC + " != 0";
        try (Cursor cu = c.getContentResolver().query(base, cols, sel, null, MediaStore.MediaColumns.TITLE + " COLLATE NOCASE")) {
            if (cu == null) return;
            while (cu.moveToNext() && items.length() < 60000) {
                long id = cu.getLong(0);
                String title = cu.getString(1), artist = cu.getString(2), album = cu.getString(3);
                long dur = cu.isNull(4) ? 0 : cu.getLong(4);
                if (artist == null || "<unknown>".equals(artist)) artist = "";
                if (album == null || "<unknown>".equals(album)) album = "";
                JSONObject it = new JSONObject();
                it.put("id", "f:" + ContentUris.withAppendedId(base, id));
                it.put("title", title == null ? "" : title);
                it.put("ch", artist);
                it.put("alb", album);
                it.put("dur", dur / 1000);
                if (video) it.put("vid", 1);
                items.put(it);
            }
        } catch (Exception e) { Log.w("Biblioteca: " + e); }
    }

    public static String mime(Context c, Uri u) {
        String t = null;
        try { t = c.getContentResolver().getType(u); } catch (Exception ignored) { }
        return t == null ? "audio/mpeg" : t;
    }
    public static boolean isVideo(Context c, Uri u) { return mime(c, u).startsWith("video/") || u.toString().contains("/video/"); }

    /** Carátula del álbum (o la imagen guardada dentro de la canción). */
    public static byte[] cover(Context c, Uri u) {
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                Bitmap b = c.getContentResolver().loadThumbnail(u, new Size(300, 300), null);
                ByteArrayOutputStream bo = new ByteArrayOutputStream();
                b.compress(Bitmap.CompressFormat.JPEG, 88, bo);
                return bo.toByteArray();
            }
        } catch (Exception ignored) { }
        MediaMetadataRetriever mr = new MediaMetadataRetriever();
        try { mr.setDataSource(c, u); return mr.getEmbeddedPicture(); }
        catch (Exception e) { return null; }
        finally { try { mr.release(); } catch (Exception ignored) { } }
    }
}
