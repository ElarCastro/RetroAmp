package com.elarcastro.retroamp;

import java.io.IOException;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import okhttp3.OkHttpClient;
import okhttp3.RequestBody;
import okhttp3.ResponseBody;

import org.schabi.newpipe.extractor.downloader.Downloader;
import org.schabi.newpipe.extractor.downloader.Request;
import org.schabi.newpipe.extractor.downloader.Response;
import org.schabi.newpipe.extractor.exceptions.ReCaptchaException;

/** Puente entre NewPipeExtractor (lee YouTube) y OkHttp. */
public final class NpDownloader extends Downloader {
    private final OkHttpClient c = Net.C.newBuilder().readTimeout(30, TimeUnit.SECONDS).build();

    @Override
    public Response execute(Request request) throws IOException, ReCaptchaException {
        String method = request.httpMethod();
        byte[] data = request.dataToSend();
        RequestBody body = data != null ? RequestBody.create(data) : null;
        okhttp3.Request.Builder b = new okhttp3.Request.Builder().url(request.url()).method(method, body)
                .header("User-Agent", Net.UA.replace(" RetroAmp", ""));
        for (Map.Entry<String, List<String>> e : request.headers().entrySet()) {
            String k = e.getKey();
            b.removeHeader(k);
            for (String v : e.getValue()) b.addHeader(k, v);
        }
        try (okhttp3.Response r = c.newCall(b.build()).execute()) {
            if (r.code() == 429) throw new ReCaptchaException("YouTube pidió verificar que no eres un robot", request.url());
            ResponseBody rb = r.body();
            String text = rb == null ? "" : rb.string();
            return new Response(r.code(), r.message(), r.headers().toMultimap(), text, r.request().url().toString());
        }
    }
}
