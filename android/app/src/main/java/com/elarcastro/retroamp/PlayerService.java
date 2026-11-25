package com.elarcastro.retroamp;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.support.v4.media.MediaMetadataCompat;
import android.support.v4.media.session.MediaSessionCompat;
import android.support.v4.media.session.PlaybackStateCompat;

import androidx.core.app.NotificationCompat;
import androidx.media.app.NotificationCompat.MediaStyle;

import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

/** Mantiene sonando RetroAmp con la pantalla apagada y pone los controles en la notificación y la pantalla de bloqueo. */
public class PlayerService extends Service {
    static final String CH = "reproduccion";
    static final int ID = 1;
    static final String A_TOGGLE = "ra.toggle", A_NEXT = "ra.next", A_PREV = "ra.prev", A_STOP = "ra.stop", A_UPDATE = "ra.update";

    static String title = "", artist = "", state = "stop", coverUrl = "";
    static Bitmap cover;
    static boolean running;

    MediaSessionCompat session;
    PowerManager.WakeLock wake;

    /** Lo llama la página cada vez que cambia la canción o el estado. */
    static void update(Context c, String t, String a, String st, String cv) {
        title = t == null ? "" : t; artist = a == null ? "" : a; state = st == null ? "stop" : st;
        if (cv != null && !cv.equals(coverUrl)) { coverUrl = cv; cover = null; if (!cv.isEmpty()) loadCover(c, cv); }
        boolean active = "play".equals(state) || "load".equals(state) || "pause".equals(state);
        if (!active && !running) return;
        Intent i = new Intent(c, PlayerService.class).setAction(A_UPDATE);
        try {
            if (!running && Build.VERSION.SDK_INT >= 26) c.startForegroundService(i); else c.startService(i);
        } catch (Exception e) { Log.w("Servicio: " + e); }
    }

    static void loadCover(Context c, String url) {
        new Thread(() -> {
            try {
                String u = url.startsWith("/") && MainActivity.http != null ? MainActivity.base() + url : url;
                Request.Builder b = new Request.Builder().url(u).header("User-Agent", Net.UA);
                if (u.startsWith("http://127.0.0.1")) b.header("Cookie", "wyt=" + MainActivity.token);
                try (Response r = Net.C.newCall(b.build()).execute()) {
                    ResponseBody body = r.body();
                    if (!r.isSuccessful() || body == null) return;
                    Bitmap bm = BitmapFactory.decodeStream(body.byteStream());
                    if (bm != null && url.equals(coverUrl)) {
                        cover = bm;
                        if (running) c.startService(new Intent(c, PlayerService.class).setAction(A_UPDATE));
                    }
                }
            } catch (Exception ignored) { }
        }).start();
    }

    @Override
    public void onCreate() {
        super.onCreate();
        NotificationManager nm = getSystemService(NotificationManager.class);
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel ch = new NotificationChannel(CH, "Reproducción", NotificationManager.IMPORTANCE_LOW);
            ch.setShowBadge(false);
            nm.createNotificationChannel(ch);
        }
        session = new MediaSessionCompat(this, "RetroAmp");
        session.setCallback(new MediaSessionCompat.Callback() {
            @Override public void onPlay() { MainActivity.js("window.raCmd&&raCmd('play')"); }
            @Override public void onPause() { MainActivity.js("window.raCmd&&raCmd('pause')"); }
            @Override public void onSkipToNext() { MainActivity.js("window.raCmd&&raCmd('next')"); }
            @Override public void onSkipToPrevious() { MainActivity.js("window.raCmd&&raCmd('prev')"); }
            @Override public void onStop() { MainActivity.js("window.raCmd&&raCmd('stop')"); }
        });
        session.setActive(true);
        PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
        wake = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "RetroAmp:reproduccion");
        wake.setReferenceCounted(false);
        running = true;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String a = intent == null ? A_UPDATE : String.valueOf(intent.getAction());
        switch (a) {
            case A_TOGGLE: MainActivity.js("window.raCmd&&raCmd('toggle')"); break;
            case A_NEXT: MainActivity.js("window.raCmd&&raCmd('next')"); break;
            case A_PREV: MainActivity.js("window.raCmd&&raCmd('prev')"); break;
            case A_STOP: MainActivity.js("window.raCmd&&raCmd('stop')"); state = "stop"; break;
            default: break;
        }
        Notification n = build();
        if (Build.VERSION.SDK_INT >= 29) startForeground(ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
        else startForeground(ID, n);
        boolean playing = "play".equals(state) || "load".equals(state);
        if (playing) wake.acquire(6 * 60 * 60 * 1000L); else if (wake.isHeld()) wake.release();
        if ("stop".equals(state)) {
            if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE); else stopForeground(true);
            stopSelf();
        }
        return START_NOT_STICKY;
    }

    Notification build() {
        boolean playing = "play".equals(state) || "load".equals(state);
        MediaMetadataCompat.Builder mb = new MediaMetadataCompat.Builder()
                .putString(MediaMetadataCompat.METADATA_KEY_TITLE, title.isEmpty() ? "RetroAmp" : title)
                .putString(MediaMetadataCompat.METADATA_KEY_ARTIST, artist);
        if (cover != null) mb.putBitmap(MediaMetadataCompat.METADATA_KEY_ALBUM_ART, cover);
        session.setMetadata(mb.build());
        session.setPlaybackState(new PlaybackStateCompat.Builder()
                .setActions(PlaybackStateCompat.ACTION_PLAY | PlaybackStateCompat.ACTION_PAUSE | PlaybackStateCompat.ACTION_PLAY_PAUSE
                        | PlaybackStateCompat.ACTION_SKIP_TO_NEXT | PlaybackStateCompat.ACTION_SKIP_TO_PREVIOUS | PlaybackStateCompat.ACTION_STOP)
                .setState(playing ? PlaybackStateCompat.STATE_PLAYING : PlaybackStateCompat.STATE_PAUSED, PlaybackStateCompat.PLAYBACK_POSITION_UNKNOWN, 1f)
                .build());
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        NotificationCompat.Builder b = new NotificationCompat.Builder(this, CH)
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle(title.isEmpty() ? "RetroAmp" : title)
                .setContentText(artist)
                .setContentIntent(open)
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setOnlyAlertOnce(true)
                .setOngoing(playing)
                .setDeleteIntent(act(A_STOP, 4))
                .addAction(R.drawable.ic_prev, "Anterior", act(A_PREV, 1))
                .addAction(playing ? R.drawable.ic_pause : R.drawable.ic_play, playing ? "Pausa" : "Reproducir", act(A_TOGGLE, 2))
                .addAction(R.drawable.ic_next, "Siguiente", act(A_NEXT, 3))
                .setStyle(new MediaStyle().setMediaSession(session.getSessionToken()).setShowActionsInCompactView(0, 1, 2));
        if (cover != null) b.setLargeIcon(cover);
        return b.build();
    }

    PendingIntent act(String a, int code) {
        return PendingIntent.getService(this, code, new Intent(this, PlayerService.class).setAction(a), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    @Override
    public void onDestroy() {
        running = false;
        if (wake != null && wake.isHeld()) wake.release();
        if (session != null) { session.setActive(false); session.release(); }
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
