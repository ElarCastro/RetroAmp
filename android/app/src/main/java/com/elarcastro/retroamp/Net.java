package com.elarcastro.retroamp;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.util.concurrent.TimeUnit;

import javax.net.ssl.SSLException;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

/** Conexiones a internet (un solo cliente para toda la app). */
public final class Net {
    public static final String UA = "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36 RetroAmp";

    public static final OkHttpClient C = new OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .followRedirects(true).followSslRedirects(true)
            .retryOnConnectionFailure(true)
            .build();

    /** Para radios: sin límite de lectura total (la transmisión no termina nunca). */
    public static final OkHttpClient STREAM = C.newBuilder().readTimeout(40, TimeUnit.SECONDS).build();

    public static final class Got { public byte[] body; public String type = "", url = ""; public int code; }

    public static Got get(String url, int timeoutMs, long maxBytes) throws IOException {
        OkHttpClient c = C.newBuilder().callTimeout(timeoutMs, TimeUnit.MILLISECONDS).build();
        Request rq = new Request.Builder().url(url).header("User-Agent", UA).build();
        try (Response resp = c.newCall(rq).execute()) {
            Got g = new Got();
            g.code = resp.code();
            if (!resp.isSuccessful()) throw new HttpError(resp.code());
            ResponseBody b = resp.body();
            g.type = b != null && b.contentType() != null ? b.contentType().toString() : "";
            g.url = resp.request().url().toString();
            g.body = b == null ? new byte[0] : readAll(b.byteStream(), maxBytes);
            return g;
        }
    }

    public static byte[] readAll(InputStream in, long max) throws IOException {
        ByteArrayOutputStream bo = new ByteArrayOutputStream();
        byte[] buf = new byte[65536]; int n;
        while ((n = in.read(buf)) > 0) { bo.write(buf, 0, n); if (bo.size() > max) throw new IOException("archivo demasiado grande"); }
        return bo.toByteArray();
    }

    public static final class HttpError extends IOException {
        public final int code;
        public HttpError(int code) { super("error " + code); this.code = code; }
    }

    /** Explica en palabras simples por qué falló (igual que en Windows). */
    public static String why(Exception ex) {
        if (ex instanceof HttpError) {
            int c = ((HttpError) ex).code;
            if (c == 404 || c == 410) return "ya no existe en esa dirección (error " + c + ")";
            if (c == 401 || c == 403) return "el servidor no dio permiso (error " + c + ")";
            return "el servidor respondió con error " + c;
        }
        if (ex instanceof UnknownHostException) return "no hay conexión con esa dirección (revisa tu internet)";
        if (ex instanceof SocketTimeoutException) return "el servidor tardó demasiado en responder";
        if (ex instanceof SSLException) return "no se pudo abrir la conexión segura con el servidor";
        String m = ex.getMessage();
        return m == null || m.isEmpty() ? ex.getClass().getSimpleName() : m;
    }
}
