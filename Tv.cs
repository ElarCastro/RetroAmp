// Internet TV (IPTV): listas M3U de canales y un intermediario para ver los canales HLS (.m3u8) en el programa.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Xml;
using System.Globalization;

namespace RetroAmp
{
    public partial class Server
    {
        static readonly Regex HttpUrl = new Regex("^https?://[^\\s\"<>]+$", RegexOptions.IgnoreCase);
        // ---------- YouTube en vivo: yt-dlp da la lista HLS de la transmisión (dura unas horas; se guarda 45 min) ----------
        static readonly Regex YtLiveUrl = new Regex(@"^https?://(www\.|m\.)?(youtube\.com|youtu\.be)/", RegexOptions.IgnoreCase);
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> ytLive = new Dictionary<string, KeyValuePair<DateTime, string>>();
        static readonly HashSet<string> ytFmtLogged = new HashSet<string>();
        static string Clip(string t, int n) { return t.Length > n ? t.Substring(0, n) + "…" : t; }
        public static bool IsYt(string u) { return YtLiveUrl.IsMatch(u ?? ""); }
        string YtLive(string url, bool audio)
        {
            string key = (audio ? "a|" : "v|") + url;
            lock (ytLive) { KeyValuePair<DateTime, string> c; if (ytLive.TryGetValue(key, out c) && (DateTime.UtcNow - c.Key).TotalMinutes < 45) return c.Value; }
            // Del más ligero al más completo; al final "lo que haya" para canales que no ofrecen los números de siempre
            string f = audio ? "bestaudio[protocol^=m3u8]/233/234/91/92/93/best[protocol^=m3u8]/bestaudio/best"
                             : "95/94/300/93/best[height<=720][protocol^=m3u8]/best[protocol^=m3u8]/232+234/231+234/230+233/bv*[height<=720][protocol^=m3u8]+ba[protocol^=m3u8]/bv*[protocol^=m3u8]+ba[protocol^=m3u8]/best[height<=720]/best";
            RunResult x = YtDlp(new string[] { "-f", f, "-g", "--no-playlist", "--match-filter", "is_live", url }, 90000);
            // Desde nov. 2026 YouTube da muchas transmisiones con la imagen y el sonido por separado: yt-dlp entrega dos direcciones
            List<string> urls = new List<string>();
            foreach (string l in Regex.Split(x.Out ?? "", "\r?\n")) if (l.Trim().StartsWith("http")) urls.Add(l.Trim());
            string m = urls.Count > 0 ? urls[0] : null;
            if (Regex.IsMatch((x.Err ?? "") + (x.Out ?? ""), "Requested format is not available", RegexOptions.IgnoreCase))
            {
                // Se anota una vez qué formatos ofrece, para poder corregirlo
                bool first; lock (ytFmtLogged) first = ytFmtLogged.Add(url);
                if (first) { try { RunResult F = YtDlp(new string[] { "-F", "--no-playlist", url }, 90000); P.Log("YouTube sin formato compatible: " + url + "\r\n" + Clip((F.Out ?? "") + (F.Err ?? ""), 4000)); } catch { } }
                throw new Exception("YouTube no ofrece un formato que el programa pueda reproducir (formato no compatible)");
            }
            if (m != null && urls.Count > 1 && !audio) m = "mux:" + urls[0] + "\n" + urls[1];
            if (m != null && !Regex.IsMatch(m, "m3u8|/hls|manifest", RegexOptions.IgnoreCase))
                throw new Exception("YouTube no ofrece esta transmisión en un formato compatible (formato no compatible)");
            if (m == null)
            {
                string e = (x.Err ?? "") + "\n" + (x.Out ?? "");
                if (x.Code == 0 || Regex.IsMatch(e, "not currently live|is offline|will begin|Premieres in|does not pass filter|is not a live|live event will begin|no longer live", RegexOptions.IgnoreCase))
                    throw new Exception("no está transmitiendo en vivo en este momento");
                throw new Exception("YouTube no lo abrió (" + ShortErr(e) + ")");
            }
            lock (ytLive) { ytLive[key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, m); if (ytLive.Count > 300) ytLive.Clear(); }
            return m;
        }
        // Pedazos de video recientes: el sonido y la imagen piden los mismos, así se bajan una sola vez
        static readonly Dictionary<string, KeyValuePair<DateTime, byte[]>> segCache = new Dictionary<string, KeyValuePair<DateTime, byte[]>>();
        static readonly Queue<string> segOrder = new Queue<string>();

        static string Md5(string s)
        {
            using (MD5 m = MD5.Create())
            {
                byte[] h = m.ComputeHash(Encoding.UTF8.GetBytes(s)); StringBuilder sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static byte[] Download(string url, out string type, out string finalUrl, int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RetroAmp";
            rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs; rq.AllowAutoRedirect = true;
            rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            IWebProxy px = SysProxy(); if (px != null) rq.Proxy = px;
            using (HttpWebResponse resp = (HttpWebResponse)rq.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                type = resp.ContentType ?? ""; finalUrl = resp.ResponseUri != null ? resp.ResponseUri.ToString() : url;
                byte[] buf = new byte[65536]; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 64L * 1024 * 1024) throw new Exception("archivo demasiado grande"); }
                return ms.ToArray();
            }
        }

        // Prueba un canal (HLS .m3u8) o una radio: null si responde con contenido, o el motivo si no
        static string Probe(string url, int depth)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RetroAmp";
            rq.Timeout = 10000; rq.ReadWriteTimeout = 10000; rq.AllowAutoRedirect = true;
            rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            IWebProxy px = SysProxy(); if (px != null) rq.Proxy = px;
            rq.KeepAlive = false;   // cada prueba usa su propia conexión y la cierra: una radio que nunca termina no deja conexiones colgadas
            try { if (rq.ServicePoint.ConnectionLimit < 24) rq.ServicePoint.ConnectionLimit = 24; } catch { }
            try { return ProbeRead(rq, url, depth); }
            finally { try { rq.Abort(); } catch { } }
        }
        static string ProbeRead(HttpWebRequest rq, string url, int depth)
        {
            int n;
            using (new System.Threading.Timer(_ => { try { rq.Abort(); } catch { } }, null, 15000, System.Threading.Timeout.Infinite))
            using (HttpWebResponse resp = (HttpWebResponse)rq.GetResponse())
            using (Stream src = resp.GetResponseStream())
            {
                string type = (resp.ContentType ?? "").ToLowerInvariant();
                byte[] buf = new byte[depth == 2 ? 2048 : 4096]; int got = 0;
                while (got < buf.Length && (n = src.Read(buf, got, buf.Length - got)) > 0) got += n;
                if (got == 0) return "respondió vacío";
                if (depth == 2) return null;   // un pedazo del video llegó: el canal funciona
                string head = Encoding.UTF8.GetString(buf, 0, Math.Min(got, 16)).TrimStart('﻿', ' ', '\r', '\n');
                if (head.StartsWith("#EXTM3U"))
                {
                    // lista HLS: se lee completa y se prueba un pedazo (o la primera calidad, si es la lista maestra)
                    MemoryStream ms = new MemoryStream(); ms.Write(buf, 0, got);
                    byte[] b2 = new byte[16384];
                    while (ms.Length < 2 * 1024 * 1024 && (n = src.Read(b2, 0, b2.Length)) > 0) ms.Write(b2, 0, n);
                    string txt = Encoding.UTF8.GetString(ms.ToArray());
                    Uri bu = resp.ResponseUri ?? new Uri(url);
                    string next = null; bool master = txt.Contains("#EXT-X-STREAM-INF");
                    foreach (string line in txt.Split('\n'))
                    {
                        string l = line.Trim();
                        if (l.Length == 0 || l.StartsWith("#")) continue;
                        next = l; if (master) break;   // maestra: la primera calidad; lista de pedazos: el último (el más reciente)
                    }
                    if (next == null) return "la lista del canal no trae video";
                    if (depth >= 1 && master) return "la lista del canal no trae video";
                    Uri nu; if (!Uri.TryCreate(bu, next, out nu)) return "la lista del canal está mal formada";
                    return Probe(nu.ToString(), master ? 1 : 2);
                }
                if (type.Contains("text/html")) return "la dirección abre una página web, no un canal";
                return null;
            }
        }
        static string WhyProbe(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null)
                {
                    int c = (int)hr.StatusCode;
                    if (c == 404 || c == 410) return "ya no existe en esa dirección (error " + c + ")";
                    if (c == 401 || c == 403) return "no da permiso desde aquí (error " + c + ")";
                    return "respondió con error " + c;
                }
                if (we.Status == WebExceptionStatus.ServerProtocolViolation) return null;   // radio SHOUTcast antigua ("ICY 200 OK"): sí contesta
                if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure) return "no hay conexión con esa dirección";
                if (we.Status == WebExceptionStatus.Timeout || we.Status == WebExceptionStatus.RequestCanceled) return "no contestó a tiempo";
                if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure) return "falló la conexión segura";
            }
            return ex.Message;
        }

        // Explica en palabras simples por qué no se pudo bajar algo
        static string WhyFail(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null)
                {
                    int c = (int)hr.StatusCode;
                    if (c == 404 || c == 410) return "la lista ya no existe en esa dirección (error " + c + ")";
                    if (c == 401 || c == 403) return "el servidor no dio permiso (error " + c + "); revisa el usuario y la contraseña o si la lista caducó";
                    return "el servidor respondió con error " + c;
                }
                if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure || we.Status == WebExceptionStatus.ProxyNameResolutionFailure)
                    return "no hay conexión con esa dirección (revisa tu internet; algunas redes, como la del trabajo, la bloquean)";
                if (we.Status == WebExceptionStatus.Timeout) return "el servidor tardó demasiado en responder";
                if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure) return "no se pudo abrir la conexión segura con el servidor";
            }
            return ex.Message;
        }

        static bool LooksPlaylist(string type, string url, byte[] b)
        {
            if (Regex.IsMatch(type ?? "", "mpegurl", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(url, @"\.m3u8?(\?|$)", RegexOptions.IgnoreCase)) return true;
            int i = 0; if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) i = 3;
            return b.Length - i >= 7 && Encoding.ASCII.GetString(b, i, 7) == "#EXTM3U";
        }

        // Las direcciones de la lista HLS se cambian para que también pasen por aquí (así no hay problemas de permisos del navegador)
        public static string RewriteHls(string text, string baseUrl)
        {
            Uri bu = new Uri(baseUrl);
            Func<string, string> wrap = u => { Uri abs; if (!Uri.TryCreate(bu, u.Trim(), out abs)) return u; return "/hls?u=" + Uri.EscapeDataString(abs.ToString()); };
            StringBuilder sb = new StringBuilder();
            foreach (string ln in Regex.Split(text, "\r?\n"))
            {
                string l = ln.TrimEnd();
                if (l.Length == 0) { sb.Append('\n'); continue; }
                if (l.StartsWith("#")) l = Regex.Replace(l, "URI=\"([^\"]+)\"", m => "URI=\"" + wrap(m.Groups[1].Value) + "\"");
                else l = wrap(l);
                sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }

        public static string[] RadioHosts = { "https://de1.api.radio-browser.info", "https://de2.api.radio-browser.info", "https://fi1.api.radio-browser.info", "https://at1.api.radio-browser.info", "https://nl1.api.radio-browser.info", "https://all.api.radio-browser.info" };
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> epgCache = new Dictionary<string, KeyValuePair<DateTime, string>>();
        static string EpgJson(string file, string warn)
        {
            DateTime mt = File.GetLastWriteTimeUtc(file);
            lock (epgCache)
            {
                KeyValuePair<DateTime, string> c;
                if (epgCache.TryGetValue(file, out c) && c.Key == mt && warn == null) return c.Value;
            }
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds, from = now - 3 * 3600, to = now + 12 * 3600;
            Dictionary<string, List<string>> names = new Dictionary<string, List<string>>();
            Dictionary<string, List<string>> prog = new Dictionary<string, List<string>>();
            using (FileStream fs = File.OpenRead(file))
            {
                Stream src = fs;
                int b1 = fs.ReadByte(), b2 = fs.ReadByte(); fs.Position = 0;
                if (b1 == 0x1f && b2 == 0x8b) src = new GZipStream(fs, CompressionMode.Decompress);
                XmlReaderSettings xs = new XmlReaderSettings(); xs.DtdProcessing = DtdProcessing.Ignore; xs.XmlResolver = null; xs.IgnoreWhitespace = true;
                using (XmlReader x = XmlReader.Create(src, xs))
                {
                    while (x.Read())
                    {
                        if (x.NodeType != XmlNodeType.Element) continue;
                        if (x.Name == "channel")
                        {
                            string id = x.GetAttribute("id") ?? ""; List<string> l = new List<string>();
                            using (XmlReader sub = x.ReadSubtree()) while (sub.Read()) if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name") { string v = sub.ReadElementContentAsString().Trim(); if (v != "") l.Add(v); }
                            if (id != "") names[id] = l;
                        }
                        else if (x.Name == "programme")
                        {
                            string id = x.GetAttribute("channel") ?? "";
                            long a = XmltvTime(x.GetAttribute("start")), z = XmltvTime(x.GetAttribute("stop"));
                            if (id == "" || a == 0 || a > to || (z != 0 && z < from)) { x.Skip(); continue; }
                            string title = "";
                            using (XmlReader sub = x.ReadSubtree()) while (sub.Read()) if (sub.NodeType == XmlNodeType.Element && sub.Name == "title" && title == "") title = sub.ReadElementContentAsString().Trim();
                            List<string> l; if (!prog.TryGetValue(id, out l)) prog[id] = l = new List<string>();
                            if (l.Count < 60) l.Add("[" + a + "," + z + "," + J(title) + "]");
                        }
                    }
                }
            }
            StringBuilder sb = new StringBuilder("{\"ok\":true");
            if (warn != null) sb.Append(",\"warn\":").Append(J(warn));
            sb.Append(",\"date\":").Append(J(File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm")));
            sb.Append(",\"ch\":{"); bool f = true;
            foreach (KeyValuePair<string, List<string>> kv in names)
            {
                if (!prog.ContainsKey(kv.Key)) continue;
                if (!f) sb.Append(','); f = false;
                sb.Append(J(kv.Key)).Append(":["); for (int i = 0; i < kv.Value.Count; i++) { if (i > 0) sb.Append(','); sb.Append(J(kv.Value[i])); } sb.Append(']');
            }
            sb.Append("},\"pr\":{"); f = true;
            foreach (KeyValuePair<string, List<string>> kv in prog)
            {
                if (!f) sb.Append(','); f = false;
                sb.Append(J(kv.Key)).Append(":[").Append(string.Join(",", kv.Value.ToArray())).Append(']');
            }
            sb.Append("}}");
            string res = sb.ToString();
            lock (epgCache) epgCache[file] = new KeyValuePair<DateTime, string>(mt, res);
            return res;
        }
        // "20261121183000 +0000" -> segundos desde 1970 (UTC)
        static long XmltvTime(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 12) return 0;
            DateTime d;
            if (!DateTime.TryParseExact(t.Substring(0, Math.Min(14, t.Trim().Split(' ')[0].Length)), new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d)) return 0;
            long sec = (long)(d - new DateTime(1970, 1, 1)).TotalSeconds;
            Match m = Regex.Match(t, @"([+-])(\d\d)(\d\d)\s*$");
            if (m.Success) { int off = int.Parse(m.Groups[2].Value) * 3600 + int.Parse(m.Groups[3].Value) * 60; sec += m.Groups[1].Value == "+" ? -off : off; }
            return sec;
        }

        bool RouteTv(Stream s, Req r, string path)
        {
            if (path == "/hls")
            {
                string u = r.Q1("u");
                if (!HttpUrl.IsMatch(u)) { Send404(s); return true; }
                if (IsYt(u))
                {   // canal o video en vivo de YouTube: se cambia por su lista HLS
                    try { u = YtLive(u, r.Q1("a") == "1"); }
                    catch (Exception ex) { P.Log("YouTube en vivo " + r.Q1("u") + ": " + ex.Message); SendBytes(s, 502, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message), null); return true; }
                }
                if (u.StartsWith("mux:") && r.Q1("v") == "1") u = u.Substring(4).Split('\n')[0];   // la imagen (va sin sonido): solo la pista de video
                if (u.StartsWith("mux:"))
                {
                    string[] vu = u.Substring(4).Split('\n');
                    string master = "#EXTM3U\n#EXT-X-INDEPENDENT-SEGMENTS\n" +
                        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",NAME=\"audio\",DEFAULT=YES,AUTOSELECT=YES,URI=\"" + vu[1] + "\"\n" +
                        "#EXT-X-STREAM-INF:BANDWIDTH=3000000,CODECS=\"avc1.4d401f,mp4a.40.2\",AUDIO=\"a\"\n" + vu[0] + "\n";
                    SendBytes(s, 200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(RewriteHls(master, vu[0])), "Cache-Control: no-store\r\n");
                    return true;
                }
                byte[] body = null; string type = "";
                lock (segCache)
                {
                    KeyValuePair<DateTime, byte[]> c;
                    if (segCache.TryGetValue(u, out c) && (DateTime.UtcNow - c.Key).TotalSeconds < 90) body = c.Value;
                }
                if (body != null) { SendBytes(s, 200, "video/mp2t", body, "Cache-Control: no-store\r\n"); return true; }
                string fin;
                try { body = Download(u, out type, out fin, 20000); }
                catch (Exception ex) { SendBytes(s, 502, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No se pudo abrir: " + WhyFail(ex)), null); return true; }
                if (LooksPlaylist(type, fin, body))
                {
                    string txt = RewriteHls(Encoding.UTF8.GetString(body), fin);
                    SendBytes(s, 200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(txt), "Cache-Control: no-store\r\n");
                    return true;
                }
                if (body.Length < 8 * 1024 * 1024)
                    lock (segCache)
                    {
                        segCache[u] = new KeyValuePair<DateTime, byte[]>(DateTime.UtcNow, body); segOrder.Enqueue(u);
                        while (segOrder.Count > 60) segCache.Remove(segOrder.Dequeue());
                    }
                SendBytes(s, 200, string.IsNullOrEmpty(type) ? "video/mp2t" : type, body, "Cache-Control: no-store\r\n");
                return true;
            }
            // 1.2: RetroAmp solo reproduce YouTube. Ya no se bajan listas de canales (IPTV), guías de programación
            // ni el directorio de radios; YouTube en vivo (/api/yt/live, /hls, /api/check) sigue igual.
            if (path == "/api/tv/get" || path == "/api/tv/epg" || path == "/api/tv/pick" || path == "/api/radio/search")
            {
                SendJson(s, "{\"ok\":false,\"error\":\"RetroAmp solo reproduce YouTube\"}");
                return true;
            }
            if (path == "/api/tv/get")
            {
                // Una lista de canales: de internet (u) o de un archivo (p). Se guarda una copia en media\perfil\iptv para abrirla rápido y sin internet.
                string u = r.Q1("u"), p = r.Q1("p");
                bool force = r.Q1("refresh") == "1";
                string dir = Path.Combine(P.Conf, "iptv"); try { Directory.CreateDirectory(dir); } catch { }   // media\perfil\iptv: las listas viajan con el programa
                if (p != "")
                {
                    if (!File.Exists(p)) { SendJson(s, Err("No encontré el archivo de la lista")); return true; }
                    SendJson(s, "{\"ok\":true,\"text\":" + J(File.ReadAllText(p)) + ",\"date\":" + J(File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm")) + "}");
                    return true;
                }
                if (!HttpUrl.IsMatch(u)) { SendJson(s, Err("La dirección de la lista no es válida")); return true; }
                // Nombre legible: el de la lista + un código corto de su dirección (por si dos listas se llaman igual)
                string nm = Regex.Replace(r.Q1("n"), @"[\\/:*?""<>|\x00-\x1f]", "").Trim().TrimEnd('.');
                if (nm.Length > 40) nm = nm.Substring(0, 40);
                string h = Md5(u), cf = Path.Combine(dir, (nm != "" ? nm + " - " : "lista - ") + h.Substring(0, 6) + ".m3u");
                if (!File.Exists(cf)) { string old = Path.Combine(dir, h + ".m3u"); try { if (File.Exists(old)) File.Move(old, cf); } catch { } }
                bool have = File.Exists(cf);
                if (have && !force && (DateTime.Now - File.GetLastWriteTime(cf)).TotalHours < 24)
                {
                    SendJson(s, "{\"ok\":true,\"text\":" + J(File.ReadAllText(cf)) + ",\"date\":" + J(File.GetLastWriteTime(cf).ToString("yyyy-MM-dd HH:mm")) + "}");
                    return true;
                }
                try
                {
                    string t, fin; byte[] b = Download(u, out t, out fin, 45000);
                    string txt = Encoding.UTF8.GetString(b);
                    if (!txt.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U") && !txt.Contains("#EXTINF")) { SendJson(s, Err("Esa dirección no tiene una lista M3U de canales")); return true; }
                    File.WriteAllText(cf, txt, new UTF8Encoding(false));
                    SendJson(s, "{\"ok\":true,\"text\":" + J(txt) + ",\"date\":" + J(DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + "}");
                }
                catch (Exception ex)
                {
                    if (have) SendJson(s, "{\"ok\":true,\"stale\":true,\"error\":" + J(WhyFail(ex)) + ",\"text\":" + J(File.ReadAllText(cf)) + ",\"date\":" + J(File.GetLastWriteTime(cf).ToString("yyyy-MM-dd HH:mm")) + "}");
                    else SendJson(s, Err(WhyFail(ex)));
                }
                return true;
            }
            if (path == "/api/tv/epg")
            {
                // Guía de programación (XMLTV, también .gz): se guarda en media\perfil\iptv y se entrega solo lo de ahora a 12 horas
                string u = r.Q1("u");
                if (!HttpUrl.IsMatch(u)) { SendJson(s, Err("La dirección de la guía no es válida")); return true; }
                bool force = r.Q1("refresh") == "1";
                string dir = Path.Combine(P.Conf, "iptv"); try { Directory.CreateDirectory(dir); } catch { }
                string cf = Path.Combine(dir, "guia - " + Md5(u).Substring(0, 6) + ".xmltv");
                bool have = File.Exists(cf); string warn = null;
                if (!have || force || (DateTime.Now - File.GetLastWriteTime(cf)).TotalHours >= 12)
                {
                    try { string t, fin; byte[] b = Download(u, out t, out fin, 90000); File.WriteAllBytes(cf, b); have = true; }
                    catch (Exception ex) { if (!have) { SendJson(s, Err(WhyFail(ex))); return true; } warn = "Sin conexión: se usa la guía guardada"; }
                }
                try { SendJson(s, EpgJson(cf, warn)); }
                catch (Exception ex) { SendJson(s, Err("La guía no se pudo leer: " + ex.Message)); }
                return true;
            }
            if (path == "/api/radio/search")
            {
                // Directorio gratuito de radios (radio-browser.info): por nombre, país (código de 2 letras) y género
                string q = r.Q1("q"), cc = r.Q1("cc"), tag = r.Q1("tag");
                if (!Regex.IsMatch(cc, "^[A-Za-z]{0,2}$")) cc = "";
                string qs = "limit=200&hidebroken=true&order=clickcount&reverse=true" + (q != "" ? "&name=" + Uri.EscapeDataString(q) : "") + (cc != "" ? "&countrycode=" + cc.ToUpperInvariant() : "") + (tag != "" ? "&tag=" + Uri.EscapeDataString(tag) : "");
                string err = "no contestó";
                foreach (string host in RadioHosts)
                {
                    try
                    {
                        string t, fin; byte[] b = Download(host + "/json/stations/search?" + qs, out t, out fin, 15000);
                        string txt = Encoding.UTF8.GetString(b).Trim();
                        if (!txt.StartsWith("[")) { err = "respuesta no válida"; continue; }
                        SendJson(s, "{\"ok\":true,\"items\":" + txt + "}");
                        return true;
                    }
                    catch (Exception ex) { err = WhyFail(ex); }
                }
                SendJson(s, Err("No pude buscar radios: " + err));
                return true;
            }
            if (path == "/api/check")
            {
                // ¿Funciona este canal o esta radio? Se abre y se lee un poquito, sin reproducirlo
                string u = r.Q1("u");
                if (!HttpUrl.IsMatch(u)) { SendJson(s, Err("Dirección no válida")); return true; }
                string why;
                try { if (ServicePointManager.DefaultConnectionLimit < 24) ServicePointManager.DefaultConnectionLimit = 24; } catch { }
                try { why = Probe(IsYt(u) ? Regex.Replace(YtLive(u, r.Q1("k") == "radio"), "^mux:([^\n]+)\n.*$", "$1", RegexOptions.Singleline) : r.Q1("k") == "radio" ? ResolveRadio(u) : u, 0); }
                catch (Exception ex) { why = ex is WebException ? WhyProbe(ex) : ex.Message; }
                SendJson(s, why == null ? "{\"ok\":true,\"live\":true}" : "{\"ok\":true,\"live\":false,\"why\":" + J(why) + "}");
                return true;
            }
            if (path == "/api/yt/live")
            {
                // Buscar transmisiones que están en vivo ahora en YouTube (filtro "En directo" de la búsqueda)
                string term = Regex.Replace(r.Q1("q"), "[\"\\r\\n]", " ").Trim();
                if (term == "") { SendJson(s, Err("Búsqueda vacía")); return true; }
                string su = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(term) + "&sp=EgJAAQ%253D%253D";
                RunResult x = YtDlp(new string[] { "--flat-playlist", "--playlist-end", "40", "--print", "%(id)s|~|%(channel,uploader|)s|~|%(channel_id|)s|~|%(live_status|)s|~|%(title)s", su }, 90000);
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"items\":["); int n = 0;
                foreach (string line in Regex.Split(x.Out ?? "", "\r?\n"))
                {
                    string[] f = line.Split(new string[] { "|~|" }, StringSplitOptions.None);
                    if (f.Length < 5 || !Regex.IsMatch(f[0], "^[\\w-]{11}$")) continue;
                    if (f[3] != "" && f[3] != "NA" && f[3] != "is_live") continue;   // solo lo que está al aire
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(J(f[0])).Append(",\"ch\":").Append(J(f[1] == "NA" ? "" : f[1])).Append(",\"cid\":").Append(J(Regex.IsMatch(f[2], "^UC[\\w-]{22}$") ? f[2] : "")).Append(",\"title\":").Append(J(f[4])).Append('}');
                }
                sb.Append("]}");
                if (n == 0 && x.Code != 0) SendJson(s, Err("No pude buscar en YouTube: " + ShortErr(x.Err)));
                else SendJson(s, sb.ToString());
                return true;
            }
            if (path == "/api/tv/pick")
            {
                string[] f = Dialogs.Open("Abrir lista de canales (IPTV)", "Listas de canales (M3U, M3U8)|*.m3u;*.m3u8|Todos los archivos|*.*", false);
                if (f.Length == 0) { SendJson(s, "{\"ok\":false,\"cancel\":true}"); return true; }
                SendJson(s, "{\"ok\":true,\"file\":" + J(f[0]) + ",\"name\":" + J(Path.GetFileNameWithoutExtension(f[0])) + "}");
                return true;
            }
            if (path.StartsWith("/lib/"))
            {
                // Bibliotecas de JavaScript que vienen con el programa (hls.js para la TV por internet)
                string name = path.Substring(5), fp = Path.Combine(Path.Combine(Path.GetDirectoryName(P.Html), "lib"), name);
                if (!Regex.IsMatch(name, @"^[\w.-]+\.js$") || !File.Exists(fp)) { Send404(s); return true; }
                SendBytes(s, 200, "application/javascript; charset=utf-8", File.ReadAllBytes(fp), "Cache-Control: max-age=86400\r\n");
                return true;
            }
            return false;
        }
    }
}
