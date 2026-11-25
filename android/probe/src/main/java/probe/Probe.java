package probe;

import com.elarcastro.retroamp.Net;
import com.elarcastro.retroamp.YT;

import java.util.List;

import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

import org.schabi.newpipe.extractor.services.youtube.YoutubeParsingHelper;

public class Probe {
    static void p(String s) { System.out.println(s); }
    public static void main(String[] a) {
        String id = null;
        try {
            List<YT.Item> r = YT.search("Yiyo Sarante Corazon de Acero", 5);
            p("BUSCAR: " + r.size() + " resultados");
            for (YT.Item it : r) p("  " + it.id + " | " + it.dur + "s | " + it.ch + " | " + it.title);
            if (!r.isEmpty()) id = r.get(0).id;
        } catch (Throwable e) { p("BUSCAR ERROR: " + e); e.printStackTrace(System.out); }
        try { p("LISTAS: " + YT.searchLists("salsa", 3).size()); } catch (Throwable e) { p("LISTAS ERROR: " + e); }
        if (id != null) {
            try {
                YT.Src s = YT.audio(id);
                p("AUDIO: " + s.type + " web=" + YoutubeParsingHelper.isWebStreamingUrl(s.url) + " android=" + YoutubeParsingHelper.isAndroidStreamingUrl(s.url) + " ios=" + YoutubeParsingHelper.isIosStreamingUrl(s.url));
                p("  url: " + s.url.replaceAll("(sig|signature|n|lsig)=[^&]+", "$1=…").substring(0, Math.min(220, s.url.length())));
                fetch("GET-Range", s.url, false);
                fetch("POST-range", s.url, true);
            } catch (Throwable e) { p("AUDIO ERROR: " + e); e.printStackTrace(System.out); }
            try { YT.Src v = YT.video(id); p("VIDEO: ok"); fetch("VIDEO GET-Range", v.url, false); fetch("VIDEO POST", v.url, true); } catch (Throwable e) { p("VIDEO ERROR: " + e); }
        }
        try {
            List<YT.Item> lv = YT.searchLive("noticias en vivo", 3);
            p("EN VIVO: " + lv.size());
            if (!lv.isEmpty()) { String h = YT.liveHls("https://www.youtube.com/watch?v=" + lv.get(0).id); p("  hls: " + h.substring(0, Math.min(120, h.length()))); }
        } catch (Throwable e) { p("EN VIVO ERROR: " + e); }
        System.exit(0);
    }
    static void fetch(String name, String url, boolean post) {
        try {
            Request.Builder b = new Request.Builder().header("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            if (YoutubeParsingHelper.isAndroidStreamingUrl(url)) b.header("User-Agent", YoutubeParsingHelper.getAndroidUserAgent(null));
            if (YoutubeParsingHelper.isIosStreamingUrl(url)) b.header("User-Agent", YoutubeParsingHelper.getIosUserAgent(null));
            if (post) b.url(url + "&range=0-1048575&rn=1").post(RequestBody.create(new byte[]{0x78, 0}, null));
            else b.url(url).header("Range", "bytes=0-1048575");
            try (Response r = Net.C.newCall(b.build()).execute()) {
                long n = r.body() == null ? -1 : r.body().bytes().length;
                p("  " + name + ": " + r.code() + " bytes=" + n + " range=" + r.header("Content-Range") + " type=" + r.header("Content-Type"));
            }
        } catch (Throwable e) { p("  " + name + " ERROR: " + e); }
    }
}
