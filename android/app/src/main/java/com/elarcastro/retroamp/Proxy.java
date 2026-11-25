package com.elarcastro.retroamp;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import javax.net.ssl.SSLSocketFactory;

import okhttp3.Request;
import okhttp3.RequestBody;
import org.schabi.newpipe.extractor.services.youtube.YoutubeParsingHelper;
import okhttp3.Response;
import okhttp3.ResponseBody;

/** Pasa por aquí el audio y el video de YouTube (por partes) y las radios: así el reproductor
 *  los ve como si fueran de la misma página (el ecualizador y el visualizador funcionan). */
public final class Proxy {
    static final Pattern RANGE = Pattern.compile("bytes=(\\d*)-(\\d*)");
    static final Pattern CRANGE = Pattern.compile("bytes (\\d+)-(\\d+)/(\\d+)");
    static final long PART = 1024 * 1024;   // YouTube entrega mejor en pedazos de ~1 MB

    public interface Resolver { YT.Src get(boolean fresh) throws Exception; }

    /** Envía un archivo de YouTube atendiendo "Range" (adelantar / retroceder). */
    public static void youtube(Http.Req r, Http.Res s, Resolver res) throws Exception {
        YT.Src src = res.get(false);
        long start = 0, end = -1;
        Matcher m = RANGE.matcher(r.h1("range"));
        boolean ranged = m.find();
        if (ranged) {
            if (!m.group(1).isEmpty()) { start = Long.parseLong(m.group(1)); if (!m.group(2).isEmpty()) end = Long.parseLong(m.group(2)); }
        }
        // Primer pedazo: también dice el tamaño total
        long[] info = new long[3];
        Response first;
        try { first = part(src.url, start, start + PART - 1); }
        catch (Net.HttpError e) {
            if (e.code != 403 && e.code != 410) throw e;
            src = res.get(true);   // la dirección venció: se pide otra
            first = part(src.url, start, start + PART - 1);
        }
        long total;
        try {
            String cr = first.header("Content-Range", "");
            Matcher cm = CRANGE.matcher(cr == null ? "" : cr);
            long clen = clen(src.url);
            if (cm.find()) total = Long.parseLong(cm.group(3));
            else if (clen > 0) total = clen;
            else { ResponseBody b0 = first.body(); total = b0 == null ? -1 : b0.contentLength(); if (total >= 0) total += start; }
            if (total < 0) throw new IOException("YouTube no dijo el tamaño");
            if (end < 0 || end >= total) end = total - 1;
            if (start >= total) { first.close(); s.bytes(416, "text/plain", new byte[0], "Content-Range: bytes */" + total + "\r\n"); return; }
            long count = end - start + 1;
            String extra = "Accept-Ranges: bytes\r\n" + (ranged ? "Content-Range: bytes " + start + "-" + end + "/" + total + "\r\n" : "");
            s.head(ranged ? 206 : 200, src.type, count, extra);
            OutputStream out = s.raw();
            long pos = start, left = count;
            Response cur = first;
            byte[] buf = new byte[65536];
            while (left > 0) {
                ResponseBody b = cur.body();
                if (b == null) throw new IOException("respuesta vacía");
                try (InputStream in = b.byteStream()) {
                    int n;
                    while (left > 0 && (n = in.read(buf, 0, (int) Math.min(buf.length, left))) > 0) {
                        out.write(buf, 0, n); pos += n; left -= n;
                    }
                }
                cur.close();
                if (left > 0) cur = part(src.url, pos, Math.min(pos + PART - 1, end));
            }
            out.flush();
        } finally { first.close(); }
    }

    static final String DESKTOP_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    static final java.util.concurrent.atomic.AtomicLong RN = new java.util.concurrent.atomic.AtomicLong();

    /** Tamaño total del archivo (viene en la dirección de YouTube como "clen"). */
    static long clen(String url) {
        Matcher m = Pattern.compile("[?&]clen=(\\d+)").matcher(url);
        return m.find() ? Long.parseLong(m.group(1)) : -1;
    }

    /** Un pedazo [a, z] del archivo de YouTube, pedido igual que lo hace NewPipe para cada tipo de dirección. */
    static Response part(String url, long a, long z) throws IOException {
        boolean web = false; String ua = DESKTOP_UA;
        try {
            web = YoutubeParsingHelper.isWebStreamingUrl(url);
            if (YoutubeParsingHelper.isAndroidStreamingUrl(url)) ua = YoutubeParsingHelper.getAndroidUserAgent(null);
            else if (YoutubeParsingHelper.isIosStreamingUrl(url)) ua = YoutubeParsingHelper.getIosUserAgent(null);
        } catch (Throwable ignored) { }
        try { return send(url, a, z, web, ua); }
        catch (Net.HttpError e) {
            if (e.code != 403) throw e;
            return send(url, a, z, !web, ua);   // se prueba la otra forma de pedirlo
        }
    }
    static Response send(String url, long a, long z, boolean post, String ua) throws IOException {
        Request.Builder b = new Request.Builder().header("User-Agent", ua).header("Accept", "*/*");
        if (post) {
            b.url(url + "&range=" + a + "-" + z + "&rn=" + RN.incrementAndGet())
             .post(RequestBody.create(new byte[]{0x78, 0}, null));
        } else b.url(url).header("Range", "bytes=" + a + "-" + z);
        Response resp = Net.STREAM.newCall(b.build()).execute();
        if (resp.code() != 200 && resp.code() != 206) { int c = resp.code(); resp.close(); throw new Net.HttpError(c); }
        return resp;
    }

    /** Envía un archivo del celular (content://) atendiendo "Range". */
    public static void content(Http.Req r, Http.Res s, android.content.Context ctx, android.net.Uri uri, String type) throws IOException {
        long len;
        try (android.content.res.AssetFileDescriptor fd = ctx.getContentResolver().openAssetFileDescriptor(uri, "r")) {
            if (fd == null) { s.notFound(); return; }
            len = fd.getLength();
        }
        long start = 0, end = len - 1; int code = 200;
        Matcher m = RANGE.matcher(r.h1("range"));
        if (m.find() && len > 0) {
            code = 206;
            if (!m.group(1).isEmpty()) { start = Long.parseLong(m.group(1)); if (!m.group(2).isEmpty()) end = Math.min(Long.parseLong(m.group(2)), len - 1); }
            else if (!m.group(2).isEmpty()) start = Math.max(0, len - Long.parseLong(m.group(2)));
        }
        if (len > 0 && (start >= len || start > end)) { s.bytes(416, "text/plain", new byte[0], "Content-Range: bytes */" + len + "\r\n"); return; }
        try (InputStream in = ctx.getContentResolver().openInputStream(uri)) {
            if (in == null) { s.notFound(); return; }
            long count = len > 0 ? end - start + 1 : -1;
            s.head(code, type, count, "Accept-Ranges: bytes\r\n" + (code == 206 ? "Content-Range: bytes " + start + "-" + end + "/" + len + "\r\n" : ""));
            long skip = start;
            while (skip > 0) { long k = in.skip(skip); if (k <= 0) break; skip -= k; }
            OutputStream out = s.raw();
            byte[] buf = new byte[65536]; long left = count < 0 ? Long.MAX_VALUE : count; int n;
            while (left > 0 && (n = in.read(buf, 0, (int) Math.min(buf.length, left))) > 0) { out.write(buf, 0, n); left -= n; }
            out.flush();
        }
    }

    /** Radio por internet: se reenvía la transmisión tal cual (también las radios antiguas "ICY 200 OK"). */
    public static void radio(Http.Res s, String url) throws IOException {
        Request rq = new Request.Builder().url(url).header("User-Agent", "RetroAmp").header("Icy-MetaData", "0").build();
        Response resp;
        try { resp = Net.STREAM.newCall(rq).execute(); }
        catch (IOException e) {
            String m = String.valueOf(e.getMessage());
            if (m.contains("ICY") || m.contains("status line")) { icy(s, url); return; }
            throw e;
        }
        try {
            if (!resp.isSuccessful()) throw new Net.HttpError(resp.code());
            ResponseBody b = resp.body();
            if (b == null) throw new IOException("la radio no envió sonido");
            String type = b.contentType() != null ? b.contentType().toString() : "audio/mpeg";
            if (type.toLowerCase(Locale.ROOT).contains("text/html")) throw new IOException("la dirección abre una página web, no una radio");
            if (type.toLowerCase(Locale.ROOT).startsWith("application/octet")) type = "audio/mpeg";
            s.head(200, type, -1, null);
            OutputStream out = s.raw();
            try (InputStream in = b.byteStream()) { pump(in, out); }
        } finally { resp.close(); }
    }

    static void pump(InputStream in, OutputStream out) throws IOException {
        byte[] buf = new byte[16384]; int n;
        while ((n = in.read(buf)) > 0) { out.write(buf, 0, n); out.flush(); }
    }

    /** Radio SHOUTcast antigua: responde "ICY 200 OK" (no es HTTP de verdad), se lee a mano. */
    static void icy(Http.Res s, String url) throws IOException {
        URL u = new URL(url);
        boolean tls = "https".equalsIgnoreCase(u.getProtocol());
        int port = u.getPort() > 0 ? u.getPort() : tls ? 443 : 80;
        Socket so = tls ? SSLSocketFactory.getDefault().createSocket() : new Socket();
        try {
            so.connect(new InetSocketAddress(u.getHost(), port), 15000);
            so.setSoTimeout(40000);
            String path = u.getFile().isEmpty() ? "/" : u.getFile();
            OutputStream o = so.getOutputStream();
            o.write(("GET " + path + " HTTP/1.0\r\nHost: " + u.getHost() + "\r\nUser-Agent: RetroAmp\r\nIcy-MetaData: 0\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.ISO_8859_1));
            o.flush();
            InputStream in = so.getInputStream();
            String l; String type = "audio/mpeg";
            while ((l = Http.line(in)) != null && !l.isEmpty()) {
                String low = l.toLowerCase(Locale.ROOT);
                if (low.startsWith("content-type:")) type = l.substring(13).trim();
            }
            s.head(200, type, -1, null);
            pump(in, s.raw());
        } finally { try { so.close(); } catch (IOException ignored) {} }
    }
}
