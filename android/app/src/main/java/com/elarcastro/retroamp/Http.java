package com.elarcastro.retroamp;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Servidor HTTP mínimo en 127.0.0.1 (como el motor de Windows): la página pide /api/... y aquí se responde. */
public final class Http {
    public interface Handler { void handle(Req r, Res s) throws Exception; }

    public static final class Req {
        public String method = "GET", path = "/", query = "";
        public final Map<String, String> q = new HashMap<>();
        public final Map<String, String> h = new HashMap<>();
        public byte[] body = new byte[0];
        public String q1(String k) { String v = q.get(k); return v == null ? "" : v; }
        public boolean has(String k) { return q.containsKey(k); }
        public String h1(String k) { String v = h.get(k.toLowerCase(Locale.ROOT)); return v == null ? "" : v; }
        public String bodyText() { return new String(body, StandardCharsets.UTF_8); }
        public String cookie(String name) {
            for (String part : h1("cookie").split(";")) {
                String p = part.trim();
                if (p.startsWith(name + "=")) return p.substring(name.length() + 1);
            }
            return "";
        }
    }

    public static final class Res {
        final OutputStream out;
        boolean sent;
        Res(OutputStream out) { this.out = out; }
        public OutputStream raw() { sent = true; return out; }
        public void head(int code, String type, long len, String extra) throws IOException {
            sent = true;
            StringBuilder sb = new StringBuilder("HTTP/1.1 ").append(code).append(' ').append(status(code)).append("\r\n");
            if (type != null) sb.append("Content-Type: ").append(type).append("\r\n");
            if (len >= 0) sb.append("Content-Length: ").append(len).append("\r\n");
            sb.append("X-Content-Type-Options: nosniff\r\n");
            if (extra != null) sb.append(extra);
            if (extra == null || !extra.contains("Cache-Control")) sb.append("Cache-Control: no-store\r\n");
            sb.append("Connection: close\r\n\r\n");
            out.write(sb.toString().getBytes(StandardCharsets.ISO_8859_1));
        }
        public void bytes(int code, String type, byte[] b, String extra) throws IOException {
            head(code, type, b.length, extra);
            out.write(b); out.flush();
        }
        public void json(String j) throws IOException { bytes(200, "application/json; charset=utf-8", j.getBytes(StandardCharsets.UTF_8), null); }
        public void text(int code, String t) throws IOException { bytes(code, "text/plain; charset=utf-8", t.getBytes(StandardCharsets.UTF_8), null); }
        public void notFound() throws IOException { bytes(404, "text/plain", new byte[0], null); }
    }

    static String status(int c) {
        switch (c) {
            case 200: return "OK"; case 206: return "Partial Content"; case 302: return "Found";
            case 403: return "Forbidden"; case 404: return "Not Found"; case 410: return "Gone";
            case 416: return "Range Not Satisfiable"; case 502: return "Bad Gateway";
            default: return "Error";
        }
    }

    private final ServerSocket ss;
    private final ExecutorService pool = Executors.newCachedThreadPool();
    private final Handler handler;
    private volatile boolean stop;

    public Http(Handler handler) throws IOException {
        this.handler = handler;
        ss = new ServerSocket(0, 64, InetAddress.getByName("127.0.0.1"));
        Thread t = new Thread(this::loop, "ra-http"); t.setDaemon(true); t.start();
    }
    public int port() { return ss.getLocalPort(); }
    public void close() { stop = true; try { ss.close(); } catch (IOException ignored) {} pool.shutdownNow(); }

    private void loop() {
        while (!stop) {
            try {
                Socket c = ss.accept();
                pool.execute(() -> serve(c));
            } catch (IOException e) { if (stop) return; }
        }
    }

    private void serve(Socket c) {
        try (Socket sock = c) {
            sock.setSoTimeout(60000);
            InputStream in = new BufferedInputStream(sock.getInputStream());
            OutputStream out = sock.getOutputStream();
            Req r = parse(in);
            if (r == null) return;
            Res s = new Res(out);
            try { handler.handle(r, s); }
            catch (Throwable e) {
                Log.w("HTTP " + r.path + ": " + e);
                if (!s.sent) { try { s.json(Api.err("Error interno: " + e.getMessage())); } catch (IOException ignored) {} }
            }
            try { out.flush(); } catch (IOException ignored) {}
        } catch (IOException ignored) { }
    }

    static String line(InputStream in) throws IOException {
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        int ch;
        while ((ch = in.read()) != -1) {
            if (ch == '\n') break;
            if (ch != '\r') b.write(ch);
            if (b.size() > 65536) throw new IOException("línea demasiado larga");
        }
        if (ch == -1 && b.size() == 0) return null;
        return b.toString("ISO-8859-1");
    }

    static Req parse(InputStream in) throws IOException {
        String first = line(in);
        if (first == null || first.isEmpty()) return null;
        String[] p = first.split(" ");
        if (p.length < 2) return null;
        Req r = new Req();
        r.method = p[0].toUpperCase(Locale.ROOT);
        String target = p[1];
        int qi = target.indexOf('?');
        r.path = qi >= 0 ? target.substring(0, qi) : target;
        r.query = qi >= 0 ? target.substring(qi + 1) : "";
        for (String kv : r.query.split("&")) {
            if (kv.isEmpty()) continue;
            int e = kv.indexOf('=');
            String k = dec(e >= 0 ? kv.substring(0, e) : kv), v = e >= 0 ? dec(kv.substring(e + 1)) : "";
            if (!r.q.containsKey(k)) r.q.put(k, v);
        }
        String l;
        while ((l = line(in)) != null && !l.isEmpty()) {
            int i = l.indexOf(':');
            if (i > 0) r.h.put(l.substring(0, i).trim().toLowerCase(Locale.ROOT), l.substring(i + 1).trim());
        }
        String cl = r.h1("content-length");
        if (!cl.isEmpty()) {
            int n = Integer.parseInt(cl);
            if (n > 64 * 1024 * 1024) throw new IOException("cuerpo demasiado grande");
            byte[] b = new byte[n]; int got = 0;
            while (got < n) { int k = in.read(b, got, n - got); if (k < 0) break; got += k; }
            r.body = b;
        }
        return r;
    }

    static String dec(String s) {
        try { return URLDecoder.decode(s, "UTF-8"); } catch (Exception e) { return s; }
    }
}
