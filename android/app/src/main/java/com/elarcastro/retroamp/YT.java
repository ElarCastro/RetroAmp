package com.elarcastro.retroamp;

import org.schabi.newpipe.extractor.InfoItem;
import org.schabi.newpipe.extractor.ListExtractor;
import org.schabi.newpipe.extractor.MediaFormat;
import org.schabi.newpipe.extractor.NewPipe;
import org.schabi.newpipe.extractor.Page;
import org.schabi.newpipe.extractor.ServiceList;
import org.schabi.newpipe.extractor.StreamingService;
import org.schabi.newpipe.extractor.localization.ContentCountry;
import org.schabi.newpipe.extractor.localization.Localization;
import org.schabi.newpipe.extractor.playlist.PlaylistInfo;
import org.schabi.newpipe.extractor.playlist.PlaylistInfoItem;
import org.schabi.newpipe.extractor.search.SearchInfo;
import org.schabi.newpipe.extractor.linkhandler.SearchQueryHandler;
import org.schabi.newpipe.extractor.services.youtube.linkHandler.YoutubeSearchQueryHandlerFactory;
import org.schabi.newpipe.extractor.stream.AudioStream;
import org.schabi.newpipe.extractor.stream.DeliveryMethod;
import org.schabi.newpipe.extractor.stream.StreamInfo;
import org.schabi.newpipe.extractor.stream.StreamInfoItem;
import org.schabi.newpipe.extractor.stream.StreamType;
import org.schabi.newpipe.extractor.stream.VideoStream;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** YouTube en Android: buscar, listas, y las direcciones del audio, del video y de las transmisiones en vivo. */
public final class YT {
    static final Pattern VID = Pattern.compile("^[\\w-]{11}$");
    static final Pattern IN_URL = Pattern.compile("(?:youtube\\.com/(?:watch\\?(?:.*&)?v=|shorts/|embed/|live/)|youtu\\.be/)([\\w-]{11})", Pattern.CASE_INSENSITIVE);
    static final Pattern LIST_ID = Pattern.compile("[?&]list=([\\w-]{12,})");
    private static volatile boolean ready;

    static synchronized void init() {
        if (ready) return;
        NewPipe.init(new NpDownloader(), new Localization("es", "PE"), new ContentCountry("PE"));
        ready = true;
    }
    static StreamingService yt() { init(); return ServiceList.YouTube; }

    public static final class Item { public String id, ch = "", title = "", cid = ""; public int dur; public boolean live; }

    public static String idOf(String url) {
        if (url == null) return null;
        Matcher m = IN_URL.matcher(url);
        return m.find() ? m.group(1) : null;
    }
    static String watch(String id) { return "https://www.youtube.com/watch?v=" + id; }

    static Item fromStream(StreamInfoItem s) {
        Item it = new Item();
        it.id = idOf(s.getUrl());
        it.title = s.getName() == null ? "" : s.getName();
        it.ch = s.getUploaderName() == null ? "" : s.getUploaderName();
        long d = s.getDuration();
        it.dur = d > 0 ? (int) d : 0;
        StreamType t = s.getStreamType();
        it.live = t == StreamType.LIVE_STREAM || t == StreamType.AUDIO_LIVE_STREAM;
        String cu = s.getUploaderUrl();
        if (cu != null) { Matcher m = Pattern.compile("/channel/(UC[\\w-]{22})").matcher(cu); if (m.find()) it.cid = m.group(1); }
        return it;
    }

    public static List<Item> search(String q, int max) throws Exception {
        StreamingService s = yt();
        SearchQueryHandler qh = s.getSearchQHFactory().fromQuery(q, Collections.singletonList(YoutubeSearchQueryHandlerFactory.VIDEOS), "");
        SearchInfo si = SearchInfo.getInfo(s, qh);
        List<Item> out = new ArrayList<>();
        collect(si.getRelatedItems(), out, max, false);
        Page next = si.getNextPage();
        while (out.size() < max && Page.isValid(next)) {
            ListExtractor.InfoItemsPage<InfoItem> more = SearchInfo.getMoreItems(s, qh, next);
            int before = out.size();
            collect(more.getItems(), out, max, false);
            next = more.getNextPage();
            if (out.size() == before) break;
        }
        return out;
    }

    public static List<Item> searchLive(String q, int max) throws Exception {
        StreamingService s = yt();
        SearchQueryHandler qh = s.getSearchQHFactory().fromQuery(q, Collections.singletonList(YoutubeSearchQueryHandlerFactory.VIDEOS), "");
        SearchInfo si = SearchInfo.getInfo(s, qh);
        List<Item> out = new ArrayList<>();
        collect(si.getRelatedItems(), out, max, true);
        Page next = si.getNextPage();
        int pages = 0;
        while (out.size() < max && Page.isValid(next) && pages++ < 3) {
            ListExtractor.InfoItemsPage<InfoItem> more = SearchInfo.getMoreItems(s, qh, next);
            collect(more.getItems(), out, max, true);
            next = more.getNextPage();
        }
        return out;
    }

    static void collect(List<? extends InfoItem> items, List<Item> out, int max, boolean liveOnly) {
        for (InfoItem ii : items) {
            if (out.size() >= max) return;
            if (!(ii instanceof StreamInfoItem)) continue;
            Item it = fromStream((StreamInfoItem) ii);
            if (it.id == null) continue;
            if (liveOnly && !it.live) continue;
            out.add(it);
        }
    }

    public static List<Item> searchLists(String q, int max) throws Exception {
        StreamingService s = yt();
        SearchInfo si = SearchInfo.getInfo(s, s.getSearchQHFactory().fromQuery(q, Collections.singletonList(YoutubeSearchQueryHandlerFactory.PLAYLISTS), ""));
        List<Item> out = new ArrayList<>();
        for (InfoItem ii : si.getRelatedItems()) {
            if (out.size() >= max) break;
            if (!(ii instanceof PlaylistInfoItem)) continue;
            PlaylistInfoItem p = (PlaylistInfoItem) ii;
            Matcher m = LIST_ID.matcher(p.getUrl());
            if (!m.find()) continue;
            Item it = new Item();
            it.id = m.group(1); it.title = p.getName() == null ? "" : p.getName();
            it.ch = p.getUploaderName() == null ? "" : p.getUploaderName();
            out.add(it);
        }
        return out;
    }

    /** Canciones de un enlace: lista de YouTube o un video suelto. */
    public static List<Item> list(String url) throws Exception {
        StreamingService s = yt();
        List<Item> out = new ArrayList<>();
        Matcher lm = LIST_ID.matcher(url);
        if (lm.find()) {
            String pu = "https://www.youtube.com/playlist?list=" + lm.group(1);
            PlaylistInfo pi = PlaylistInfo.getInfo(s, pu);
            collect(pi.getRelatedItems(), out, 5000, false);
            Page next = pi.getNextPage();
            while (Page.isValid(next) && out.size() < 5000) {
                ListExtractor.InfoItemsPage<StreamInfoItem> more = PlaylistInfo.getMoreItems(s, pu, next);
                collect(more.getItems(), out, 5000, false);
                next = more.getNextPage();
            }
            return out;
        }
        String id = idOf(url);
        if (id != null) {
            StreamInfo si = info(id);
            Item it = new Item();
            it.id = id; it.title = si.getName(); it.ch = si.getUploaderName();
            it.dur = (int) Math.max(0, si.getDuration());
            out.add(it);
        }
        return out;
    }

    // ---- Información de un video (se guarda un rato: las direcciones duran unas horas) ----
    static final class Cached { long t; StreamInfo info; }
    static final Map<String, Cached> cache = new HashMap<>();

    public static StreamInfo info(String id) throws Exception {
        synchronized (cache) {
            Cached c = cache.get(id);
            if (c != null && System.currentTimeMillis() - c.t < 45 * 60 * 1000L) return c.info;
        }
        StreamInfo si = StreamInfo.getInfo(yt(), watch(id));
        synchronized (cache) {
            if (cache.size() > 200) cache.clear();
            Cached c = new Cached(); c.t = System.currentTimeMillis(); c.info = si; cache.put(id, c);
        }
        return si;
    }
    public static void forget(String id) { synchronized (cache) { cache.remove(id); } }

    public static final class Src { public String url, type; public long size; }

    /** El mejor audio que se puede pedir por partes (m4a primero: suena en todos los celulares). */
    public static Src audio(String id) throws Exception {
        StreamInfo si = info(id);
        AudioStream best = null; int bestScore = -1;
        for (AudioStream a : si.getAudioStreams()) {
            if (a.getDeliveryMethod() != DeliveryMethod.PROGRESSIVE_HTTP || !a.isUrl()) continue;
            MediaFormat f = a.getFormat();
            int score = (f == MediaFormat.M4A ? 1000000 : 0) + Math.max(0, a.getAverageBitrate());
            if (a.getAudioTrackType() != null && a.getAudioTrackType().name().contains("DUBBED")) score -= 500000;
            if (score > bestScore) { bestScore = score; best = a; }
        }
        if (best == null) throw new Exception("YouTube no dio un audio que se pueda reproducir");
        Src s = new Src();
        s.url = best.getContent();
        s.type = best.getFormat() == MediaFormat.WEBMA || best.getFormat() == MediaFormat.WEBMA_OPUS ? "audio/webm" : "audio/mp4";
        return s;
    }

    /** Imagen para la ventana Visualización: mp4 de hasta 720p (sin sonido: el sonido va por el audio). */
    public static Src video(String id) throws Exception {
        StreamInfo si = info(id);
        VideoStream best = null; int bestScore = -1;
        List<VideoStream> all = new ArrayList<>(si.getVideoOnlyStreams());
        all.addAll(si.getVideoStreams());
        for (VideoStream v : all) {
            if (v.getDeliveryMethod() != DeliveryMethod.PROGRESSIVE_HTTP || !v.isUrl()) continue;
            if (v.getFormat() != MediaFormat.MPEG_4) continue;
            int h = v.getHeight();
            if (h > 720) continue;
            String codec = v.getCodec() == null ? "" : v.getCodec();
            int score = h * 10 + (codec.startsWith("avc1") ? 5 : 0);
            if (score > bestScore) { bestScore = score; best = v; }
        }
        if (best == null) throw new Exception("YouTube no dio un video que se pueda mostrar");
        Src s = new Src(); s.url = best.getContent(); s.type = "video/mp4";
        return s;
    }

    /** Video con sonido (para descargar en video). */
    public static Src muxed(String id) throws Exception {
        StreamInfo si = info(id);
        VideoStream best = null;
        for (VideoStream v : si.getVideoStreams()) {
            if (v.getDeliveryMethod() != DeliveryMethod.PROGRESSIVE_HTTP || !v.isUrl() || v.getFormat() != MediaFormat.MPEG_4) continue;
            if (best == null || v.getHeight() > best.getHeight()) best = v;
        }
        if (best == null) throw new Exception("YouTube no dio un video con sonido para descargar");
        Src s = new Src(); s.url = best.getContent(); s.type = "video/mp4";
        return s;
    }

    /** Transmisión en vivo: la lista HLS. */
    public static String liveHls(String url) throws Exception {
        String id = idOf(url);
        StreamInfo si = id != null ? info(id) : StreamInfo.getInfo(yt(), url);
        StreamType t = si.getStreamType();
        String h = si.getHlsUrl();
        if (t != StreamType.LIVE_STREAM && t != StreamType.AUDIO_LIVE_STREAM) throw new Exception("no está transmitiendo en vivo en este momento");
        if (h == null || h.isEmpty()) throw new Exception("YouTube no ofrece esta transmisión en un formato compatible");
        return h;
    }

    /** Explica el error de YouTube en palabras simples. */
    public static String why(Throwable e) {
        String n = e.getClass().getSimpleName(), m = e.getMessage() == null ? "" : e.getMessage();
        if (n.contains("ReCaptcha") || n.contains("NotBot")) return "YouTube pidió verificar que no eres un robot (pasa con algunas redes); espera unos minutos o prueba con otra conexión (datos o wifi)";
        if (n.contains("AgeRestricted")) return "el video tiene restricción de edad";
        if (n.contains("GeographicRestriction")) return "el video no está disponible en tu país";
        if (n.contains("Private")) return "el video es privado";
        if (n.contains("ContentNotAvailable")) return "el video no está disponible" + (m.isEmpty() ? "" : " (" + m + ")");
        if (n.contains("Paid")) return "el video es de pago";
        if (e.getCause() instanceof java.net.UnknownHostException) return "no hay conexión a internet";
        return m.isEmpty() ? n : m;
    }
}
