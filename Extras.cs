// RetroAmp 2.3 - funciones extra del motor: listas M3U/PLS, radios, caratulas, video y biblioteca
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RetroAmp
{
    // ---------------- Ventanas de Windows (abrir / guardar / carpeta) ----------------
    public static class Dialogs
    {
        public delegate object Body(Form owner);

        public static object RunSta(Body body)
        {
            object result = null;
            Thread th = new Thread(delegate ()
            {
                try
                {
                    using (Form owner = new Form())
                    {
                        owner.ShowInTaskbar = false;
                        owner.FormBorderStyle = FormBorderStyle.None;
                        owner.StartPosition = FormStartPosition.CenterScreen;
                        owner.Size = new System.Drawing.Size(1, 1);
                        owner.Opacity = 0;
                        owner.TopMost = true;
                        owner.Show();
                        owner.Activate();
                        result = body(owner);
                    }
                }
                catch { }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            th.Join();
            return result;
        }

        public static string[] Open(string title, string filter, bool multi)
        {
            object r = RunSta(delegate (Form owner)
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Title = title; d.Filter = filter; d.Multiselect = multi;
                    return d.ShowDialog(owner) == DialogResult.OK ? d.FileNames : null;
                }
            });
            return r as string[] ?? new string[0];
        }

        public static string Save(string title, string filter, string name)
        {
            return RunSta(delegate (Form owner)
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Title = title; d.Filter = filter; d.FileName = name; d.OverwritePrompt = true;
                    return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
                }
            }) as string;
        }

        public static string Folder(string description)
        {
            return RunSta(delegate (Form owner)
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = description; d.ShowNewFolderButton = false;
                    return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
                }
            }) as string;
        }
    }


    public partial class Server
    {
        readonly Dictionary<string, VideoJob> videoJobs = new Dictionary<string, VideoJob>();
        readonly object libLock = new object();
        volatile bool libScanning;
        volatile int libFound;
        string libDir = "";

        static readonly Regex YtUrl = new Regex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/)|youtu\.be/)([\w-]{11})", RegexOptions.IgnoreCase);

        bool RouteExtra(Stream s, Req r, string path)
        {
            if (path == "/api/m3u/open")
            {
                string[] f = Dialogs.Open("Abrir lista de reproducción", "Listas (M3U, M3U8, PLS)|*.m3u;*.m3u8;*.pls|Todos los archivos|*.*", false);
                if (f.Length == 0) { SendJson(s, "{\"ok\":true,\"items\":[]}"); return true; }
                SendJson(s, ReadPlaylistFile(f[0]));
            }
            else if (path == "/api/m3u/save" && r.Method == "POST")
            {
                string name = SafeName(r.Q1("name"));
                string file = Dialogs.Save("Guardar lista como", "Lista M3U8 (UTF-8)|*.m3u8|Lista M3U|*.m3u", name == "" ? "Mi lista" : name);
                if (file == null) { SendJson(s, "{\"ok\":false,\"cancel\":true}"); return true; }
                string text = r.Body;
                string baseDir = Path.GetDirectoryName(file);
                // Rutas relativas cuando la cancion esta en la misma carpeta o debajo (asi la lista viaja con la musica)
                StringBuilder sb = new StringBuilder();
                foreach (string ln in Regex.Split(text, "\r?\n"))
                {
                    string l = ln;
                    if (l.Length > 0 && !l.StartsWith("#") && l.StartsWith(baseDir + "\\", StringComparison.OrdinalIgnoreCase)) l = l.Substring(baseDir.Length + 1);
                    sb.Append(l).Append("\r\n");
                }
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(file.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)));
                SendJson(s, "{\"ok\":true,\"file\":" + J(file) + "}");
            }
            else if (path == "/api/radio/prep")
            {
                string u = r.Q1("u");
                if (!Regex.IsMatch(u, "^https?://[^\\s\"]+$")) { SendJson(s, Err("Enlace de radio no valido")); return true; }
                if (IsYt(u))
                {   // YouTube en vivo como radio: solo el audio, por el mismo paso que la TV
                    try { YtLive(u, true); } catch (Exception ex) { SendJson(s, Err("No pude abrir la radio: " + ex.Message)); return true; }
                    SendJson(s, "{\"ok\":true,\"hls\":true,\"url\":" + J("/hls?a=1&u=" + Uri.EscapeDataString(u)) + ",\"size\":0}");
                    return true;
                }
                string real = u;
                try { real = ResolveRadio(u); } catch (Exception ex) { SendJson(s, Err("No pude abrir la radio: " + ex.Message)); return true; }
                if (Regex.IsMatch(real, @"\.m3u8(\?|$)", RegexOptions.IgnoreCase) || Regex.IsMatch(u, @"\.m3u8(\?|$)", RegexOptions.IgnoreCase))
                {   // radio HLS (.m3u8): se escucha con el mismo paso que la TV por internet
                    SendJson(s, "{\"ok\":true,\"hls\":true,\"url\":" + J("/hls?u=" + Uri.EscapeDataString(Regex.IsMatch(u, @"\.m3u8(\?|$)", RegexOptions.IgnoreCase) ? u : real)) + ",\"size\":0}");
                    return true;
                }
                SendJson(s, "{\"ok\":true,\"url\":" + J("/stream?u=" + Uri.EscapeDataString(real)) + ",\"size\":0}");
            }
            else if (path == "/api/radio/open")
            {
                // La radio se baja aquí y el reproductor la pide en pedazos con tamaño fijo: así no se queda
                // "cargando" en equipos cuyo antivirus o filtro de red retiene las descargas que nunca terminan
                string u = r.Q1("u");
                if (!Regex.IsMatch(u, "^https?://[^\\s\"]+$")) { SendJson(s, Err("Enlace de radio no valido")); return true; }
                RadioBuf rb = new RadioBuf();
                int id;
                lock (radios) { id = ++radioSeq; radios[id] = rb; foreach (int k in new List<int>(radios.Keys)) if (radios[k].Stale) radios.Remove(k); }
                Thread th = new Thread(() => ProxyStream(null, u, rb)); th.IsBackground = true; th.Start();
                rb.Ready.WaitOne(30000);
                if (rb.Error != null) { SendJson(s, Err("No pude abrir la radio: " + rb.Error)); return true; }
                if (rb.Type == null) { rb.Done = true; SendJson(s, Err("la radio no contestó")); return true; }
                SendJson(s, "{\"ok\":true,\"id\":" + id + ",\"type\":" + J(rb.Type) + "}");
            }
            else if (path == "/api/radio/chunk")
            {
                RadioBuf rb; int id; long pos;
                int.TryParse(r.Q1("id"), out id); long.TryParse(r.Q1("p"), out pos);
                lock (radios) radios.TryGetValue(id, out rb);
                if (rb == null) { Send404(s); return true; }
                long next; byte[] data = rb.Take(pos, 4000, out next);
                if (data == null) { SendBytes(s, 410, "text/plain", Encoding.UTF8.GetBytes(rb.Error ?? "fin"), null); return true; }
                SendBytes(s, 200, "application/octet-stream", data, "X-Next: " + next + "\r\n");
            }
            else if (path == "/stream")
            {
                string u = r.Q1("u");
                if (!Regex.IsMatch(u, "^https?://[^\\s\"]+$")) { Send404(s); return true; }
                ProxyStream(s, u);
            }
            else if (path == "/api/local/cover")
            {
                byte[] img; string type;
                if (!Local.IsMedia(r.Q1("p")) || !Local.Cover(r.Q1("p"), out img, out type)) { Send404(s); return true; }
                SendBytes(s, 200, type, img, null);
            }
            else if (path == "/api/video/yt")
            {
                string id = r.Q1("id");
                if (!VideoId.IsMatch(id)) { SendJson(s, Err("id no valido")); return true; }
                object lk;
                lock (prepLocks) { if (!prepLocks.TryGetValue(id + ".v", out lk)) { lk = new object(); prepLocks[id + ".v"] = lk; } }
                string found = null, err = "";
                lock (lk)
                {
                    found = FindVideo(id);
                    if (found == null)
                    {
                        string outTpl = Path.Combine(P.Cache, "%(id)s.v.%(ext)s");
                        RunResult x = YtDlp(new string[] { "-f", "bestvideo[height<=720][vcodec^=avc1]/bestvideo[height<=720][ext=mp4]/bestvideo[height<=720]/best[height<=720]",
                            "--no-playlist", "--no-progress", "--no-mtime", "-q", "-o", outTpl, "https://www.youtube.com/watch?v=" + id }, 600000);
                        found = FindVideo(id);
                        if (found == null) err = ShortErr(x.Err);
                    }
                }
                SendJson(s, found != null ? "{\"ok\":true,\"url\":" + J("/video/" + found) + "}" : Err(err));
            }
            else if (path.StartsWith("/videolive/")) SendLive(s, path.Substring(11));
            else if (path.StartsWith("/video/"))
            {
                string name = path.Substring(7);
                Match m = Regex.Match(name, @"^(?:[\w-]{11}\.v|v-[0-9a-f]{16})\.(mp4|webm)$");
                string fp = Path.Combine(P.Cache, name);
                if (!m.Success || !File.Exists(fp)) { Send404(s); return true; }
                SendFile(s, r, fp, m.Groups[1].Value == "webm" ? "video/webm" : "video/mp4");
            }
            else if (path == "/api/lib/pick")
            {   // 2.17: Preferencias › Medios locales: solo elegir la carpeta (sin escanear todavía)
                string d = Dialogs.Folder("Elige una carpeta con tu música o tus videos");
                SendJson(s, d == null ? "{\"ok\":false,\"cancel\":true}" : "{\"ok\":true,\"dir\":" + J(d) + "}");
            }
            else if (path == "/api/lib/dirs")
            {   // qué carpetas existen (una USB o una PC distinta puede no tenerlas) y cuál es la carpeta Música de esta PC
                string mus = ""; try { mus = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); } catch { }
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"music\":" + J(mus ?? "") + ",\"media\":[" + J(MusicDir) + "," + J(VideoDir) + "],\"exists\":[");
                string[] ds = (r.Q1("dirs") ?? "").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < ds.Length; i++) { bool ok = false; try { ok = Directory.Exists(ds[i]); } catch { } sb.Append(i > 0 ? "," : "").Append(J(ok)); }
                SendJson(s, sb.Append("]}").ToString());
            }
            else if (path == "/api/lib/prune")
            {   // quita de la biblioteca los archivos que ya no existen (sin volver a escanear)
                SendJson(s, LibPrune());
            }
            else if (path == "/api/lib/scan" && r.Q1("dirs") != null)
            {   // 2.17: varias carpetas (Preferencias › Medios locales); las que no existen se saltan
                List<string> ds = new List<string>(), miss = new List<string>();
                foreach (string d in r.Q1("dirs").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                { bool ok = false; try { ok = Directory.Exists(d); } catch { } if (ok) ds.Add(d); else miss.Add(d); }
                StartLibScan(ds);
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"dir\":" + J(ds.Count > 0 ? ds[0] : "") + ",\"missing\":[");
                for (int i = 0; i < miss.Count; i++) sb.Append(i > 0 ? "," : "").Append(J(miss[i]));
                SendJson(s, sb.Append("]}").ToString());
            }
            else if (path == "/api/lib/scan")
            {
                string dir = r.Q1("dir");
                if (r.Q1("pick") == "1")
                {
                    dir = Dialogs.Folder("Elige la carpeta de tu biblioteca de música");
                    if (dir == null) { SendJson(s, "{\"ok\":false,\"cancel\":true}"); return true; }
                }
                if (dir == "") { try { dir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); } catch { } if (string.IsNullOrEmpty(dir)) dir = P.Data; }
                if (!Directory.Exists(dir)) { SendJson(s, Err("No existe la carpeta " + dir)); return true; }
                StartLibScan(dir);
                SendJson(s, "{\"ok\":true,\"dir\":" + J(dir) + "}");
            }
            else if (path == "/api/lib/status")
            {
                SendJson(s, "{\"ok\":true,\"scanning\":" + J(libScanning) + ",\"found\":" + libFound + ",\"dir\":" + J(libDir) + "}");
            }
            else if (path == "/api/lib")
            {
                string f = Path.Combine(P.Conf, "biblioteca.json");
                SendFileJson(s, f);
            }
            else return false;
            return true;
        }

        string FindVideo(string id)
        {
            foreach (string ext in new string[] { "mp4", "webm" })
                if (File.Exists(Path.Combine(P.Cache, id + ".v." + ext))) return id + ".v." + ext;
            return null;
        }

        // ---------------- Listas M3U / PLS ----------------
        internal string ReadPlaylistFile(string file)
        {
            string text;
            byte[] raw = File.ReadAllBytes(file);
            text = Local.DecodeText(raw);
            string baseDir = Path.GetDirectoryName(file);
            List<string> entries = new List<string>();
            Dictionary<int, string> titles = new Dictionary<int, string>();
            Dictionary<int, int> durs = new Dictionary<int, int>();
            bool pls = file.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase);
            if (pls)
            {
                SortedDictionary<int, string> files = new SortedDictionary<int, string>();
                foreach (string ln in Regex.Split(text, "\r?\n"))
                {
                    Match m = Regex.Match(ln.Trim(), @"^(File|Title|Length)(\d+)=(.*)$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    int n = int.Parse(m.Groups[2].Value);
                    string k = m.Groups[1].Value.ToLowerInvariant(), v = m.Groups[3].Value.Trim();
                    if (k == "file") files[n] = v;
                    else if (k == "title") titles[n] = v;
                    else { int d; if (int.TryParse(v, out d)) durs[n] = d; }
                }
                Dictionary<int, string> t2 = new Dictionary<int, string>(); Dictionary<int, int> d2 = new Dictionary<int, int>();
                foreach (KeyValuePair<int, string> kv in files)
                {
                    int idx = entries.Count;
                    entries.Add(kv.Value);
                    if (titles.ContainsKey(kv.Key)) t2[idx] = titles[kv.Key];
                    if (durs.ContainsKey(kv.Key)) d2[idx] = durs[kv.Key];
                }
                titles = t2; durs = d2;
            }
            else
            {
                string pendTitle = null; int pendDur = 0;
                foreach (string ln0 in Regex.Split(text, "\r?\n"))
                {
                    string ln = ln0.Trim();
                    if (ln.Length == 0) continue;
                    Match m = Regex.Match(ln, @"^#EXTINF:\s*(-?\d+)[^,]*,(.*)$", RegexOptions.IgnoreCase);
                    if (m.Success) { pendTitle = m.Groups[2].Value.Trim(); int.TryParse(m.Groups[1].Value, out pendDur); continue; }
                    if (ln.StartsWith("#")) continue;
                    int idx = entries.Count;
                    entries.Add(ln);
                    if (pendTitle != null) { titles[idx] = pendTitle; if (pendDur > 0) durs[idx] = pendDur; }
                    pendTitle = null; pendDur = 0;
                }
            }

            StringBuilder sb = new StringBuilder("{\"ok\":true,\"name\":" + J(Path.GetFileNameWithoutExtension(file)) + ",\"items\":[");
            int count = 0;
            for (int i = 0; i < entries.Count && count < 10000; i++)
            {
                string e = entries[i];
                string title = titles.ContainsKey(i) ? titles[i] : "", artist = "";
                int dur = durs.ContainsKey(i) ? durs[i] : 0;
                int sep = title.IndexOf(" - ");
                if (sep > 0) { artist = title.Substring(0, sep).Trim(); title = title.Substring(sep + 3).Trim(); }
                string id;
                Match yt = YtUrl.Match(e);
                if (yt.Success) id = yt.Groups[1].Value;
                else if (Regex.IsMatch(e, @"^https?://\S+\.m3u8(\?|$)", RegexOptions.IgnoreCase)) id = "tv:" + e;   // canal de TV (HLS)
                else if (Regex.IsMatch(e, "^https?://", RegexOptions.IgnoreCase)) id = "u:" + e;
                else
                {
                    string p = e;
                    if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) { try { p = new Uri(p).LocalPath; } catch { continue; } }
                    p = p.Replace('/', '\\');
                    if (!Path.IsPathRooted(p)) p = Path.Combine(baseDir, p);
                    try { p = Path.GetFullPath(p); } catch { continue; }
                    if (!Local.IsMedia(p)) continue;
                    id = "f:" + p;
                    if (File.Exists(p) && (title == "" || dur == 0))
                    {
                        LocalTrack t = Local.Read(p);
                        if (title == "") { title = t.Title; artist = t.Artist; }
                        if (dur == 0) dur = t.Dur;
                    }
                }
                if (count++ > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J(id)).Append(",\"title\":").Append(J(title)).Append(",\"ch\":").Append(J(artist)).Append(",\"dur\":").Append(Math.Max(0, dur)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        // ---------------- Radios por internet ----------------
        static string ResolveRadio(string url)
        {
            string path = url.Split('?')[0].ToLowerInvariant();
            if (!(path.EndsWith(".pls") || path.EndsWith(".m3u") || path.EndsWith(".asx"))) return url;
            string text;
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "RetroAmp";
                wc.Proxy = SysProxy();
                text = wc.DownloadString(url);
            }
            Match m = Regex.Match(text, @"(https?://[^\s""<>]+)", RegexOptions.IgnoreCase);
            if (!m.Success) throw new Exception("la lista de la radio no tiene enlaces");
            return m.Groups[1].Value;
        }

        // Proxy de Windows (redes de empresa) con las credenciales del usuario
        static IWebProxy SysProxy()
        {
            try { IWebProxy px = WebRequest.GetSystemWebProxy(); px.Credentials = CredentialCache.DefaultCredentials; return px; } catch { return null; }
        }

        // Abre la radio; si el servidor (o el proxy) no contesta en 20 s se corta, para no quedarse "cargando" para siempre
        static HttpWebResponse OpenRadio(string url, IWebProxy px)
        {
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "RetroAmp";
            rq.Timeout = 15000; rq.ReadWriteTimeout = 30000;
            rq.AllowAutoRedirect = true;
            rq.Proxy = px;
            using (new System.Threading.Timer(_ => { try { rq.Abort(); } catch { } }, null, 20000, Timeout.Infinite))
            {
                try { return (HttpWebResponse)rq.GetResponse(); }
                catch (WebException ex) { if (ex.Status == WebExceptionStatus.RequestCanceled) throw new WebException("la radio no contestó en 20 segundos", WebExceptionStatus.Timeout); throw; }
            }
        }

        void ProxyStream(Stream s, string url) { ProxyStream(s, url, null); }
        void ProxyStream(Stream s, string url, RadioBuf rb)
        {
            long sent = 0;
            if (rb != null) s = rb;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            Stream src = null; string type = "audio/mpeg";
            HttpWebResponse resp = null;
            TcpClient raw = null;
            try
            {
                try
                {
                    // Primero como lo indique Windows (proxy de la empresa, si hay); si así no llega nada, directo.
                    IWebProxy px = SysProxy();
                    Uri pu = null; try { if (px != null && !px.IsBypassed(new Uri(url))) pu = px.GetProxy(new Uri(url)); } catch { }
                    bool viaProxy = pu != null && pu.Host != new Uri(url).Host;
                    P.Log("Radio " + url + ": conectando" + (viaProxy ? " por el proxy " + pu.Host + ":" + pu.Port : " directo"));
                    try { resp = OpenRadio(url, viaProxy ? px : null); }
                    catch (WebException e1)
                    {
                        if (!viaProxy || e1.Status == WebExceptionStatus.ServerProtocolViolation) throw;
                        P.Log("Radio " + url + ": por el proxy no se pudo (" + e1.Message + "); se intenta directo");
                        resp = OpenRadio(url, null);
                    }
                    src = resp.GetResponseStream();
                    if (!string.IsNullOrEmpty(resp.ContentType)) type = resp.ContentType;
                    P.Log("Radio " + url + ": respondió " + (int)resp.StatusCode + " " + type);
                }
                catch (WebException wex)
                {
                    // Servidores SHOUTcast antiguos responden "ICY 200 OK": se habla HTTP a mano
                    if (wex.Status != WebExceptionStatus.ServerProtocolViolation || !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw;
                    Uri u = new Uri(url);
                    raw = new TcpClient(u.Host, u.Port);
                    raw.ReceiveTimeout = 30000;
                    NetworkStream ns = raw.GetStream();
                    byte[] rqb = Encoding.ASCII.GetBytes("GET " + u.PathAndQuery + " HTTP/1.0\r\nHost: " + u.Host + "\r\nUser-Agent: RetroAmp\r\nIcy-MetaData: 0\r\nConnection: close\r\n\r\n");
                    ns.Write(rqb, 0, rqb.Length);
                    string status = ReadLine(ns);
                    if (status == null || !Regex.IsMatch(status, @"^(ICY|HTTP/\d\.\d) 200")) throw new Exception("la radio respondio: " + status);
                    string h;
                    while (!string.IsNullOrEmpty(h = ReadLine(ns)))
                        if (h.StartsWith("content-type:", StringComparison.OrdinalIgnoreCase)) type = h.Substring(13).Trim();
                    src = ns;
                }
                if (rb != null) { rb.Type = type; rb.Ready.Set(); }
                else
                {
                    byte[] hb = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    s.Write(hb, 0, hb.Length);
                }
                byte[] buf = new byte[16384];
                int n;
                DateTime t0 = DateTime.Now; bool told = false;
                while (!stopping && (n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    try { s.Write(buf, 0, n); }
                    catch (Exception wex2) { P.Log("Radio " + url + ": el reproductor dejó de pedir audio tras " + sent / 1024 + " KB en " + (int)(DateTime.Now - t0).TotalSeconds + " s (" + wex2.Message + ")"); sent = Math.Max(sent, 1); return; }
                    if (sent == 0) P.Log("Radio " + url + ": ya llega audio");
                    sent += n;
                    if (!told && (DateTime.Now - t0).TotalSeconds >= 10) { told = true; P.Log("Radio " + url + ": " + sent / 1024 + " KB enviados al reproductor en 10 s"); }
                }
                P.Log("Radio " + url + ": terminó tras " + sent / 1024 + " KB en " + (int)(DateTime.Now - t0).TotalSeconds + " s");
                if (sent == 0) P.Log("Radio " + url + ": la radio no envio audio");
            }
            catch (IOException ex) { P.Log("Radio " + url + ": " + (sent == 0 ? "sin datos" : "se cortó tras " + sent / 1024 + " KB") + " (" + ex.Message + ")"); if (rb != null && sent == 0) rb.Error = "la radio no envió audio"; }
            catch (Exception ex) { P.Log("Radio " + url + ": " + ex.Message); if (rb != null) rb.Error = ex.Message; }
            finally
            {
                if (rb != null) { rb.Done = true; rb.Ready.Set(); }
                try { if (src != null) src.Close(); } catch { }
                try { if (resp != null) resp.Close(); } catch { }
                try { if (raw != null) raw.Close(); } catch { }
            }
        }

        readonly Dictionary<int, RadioBuf> radios = new Dictionary<int, RadioBuf>();
        int radioSeq;

        // ---------------- Biblioteca ----------------
        void StartLibScan(string dir) { StartLibScan(new List<string> { dir }); }
        void StartLibScan(List<string> dirs)
        {
            string dir = dirs.Count > 0 ? dirs[0] : "";
            lock (libLock)
            {
                if (libScanning) return;
                libScanning = true; libFound = 0; libDir = dir;
            }
            Thread t = new Thread(delegate ()
            {
                try
                {
                    List<string> files = new List<string>();
                    HashSet<string> seen0 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string d in dirs)
                    {
                        if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                        List<string> got = new List<string>(); Local.CollectAudio(d, got, 50000);
                        foreach (string f in got) { string k; try { k = Path.GetFullPath(f); } catch { k = f; } if (seen0.Add(k) && files.Count < 60000) files.Add(f); }
                    }
                    // siempre se agrega lo de media\musica y media\video (descargas y lo que copies ahí), sin repetir
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files) { try { seen.Add(Path.GetFullPath(f)); } catch { seen.Add(f); } }
                    foreach (string md in new string[] { MusicDir, VideoDir })
                    {
                        if (string.IsNullOrEmpty(md) || !Directory.Exists(md)) continue;
                        List<string> more = new List<string>();
                        Local.CollectAudio(md, more, 50000);
                        foreach (string f in more) { string k; try { k = Path.GetFullPath(f); } catch { k = f; } if (seen.Add(k) && files.Count < 60000) files.Add(f); }
                    }
                    StringBuilder dj = new StringBuilder("["); for (int i = 0; i < dirs.Count; i++) dj.Append(i > 0 ? "," : "").Append(J(dirs[i])); dj.Append(']');
                    StringBuilder sb = new StringBuilder("{\"ok\":true,\"dir\":" + J(dir) + ",\"dirs\":" + dj + ",\"scanned\":" + J(DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + ",\"items\":[");
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (stopping) return;
                        LocalTrack tr = Local.Read(files[i]);
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"id\":").Append(J("f:" + tr.Path)).Append(",\"title\":").Append(J(tr.Title)).Append(",\"ch\":").Append(J(tr.Artist))
                          .Append(",\"alb\":").Append(J(tr.Album)).Append(",\"dur\":").Append(tr.Dur).Append(Local.IsVideo(tr.Path) ? ",\"vid\":1" : "").Append('}');
                        libFound = i + 1;
                    }
                    sb.Append("]}");
                    SaveText(Path.Combine(P.Conf, "biblioteca.json"), sb.ToString());
                }
                catch (Exception ex) { P.Log("Biblioteca: " + ex.Message); }
                finally { libScanning = false; }
            });
            t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
        }
        string LibPrune()
        {
            string f = Path.Combine(P.Conf, "biblioteca.json");
            if (libScanning || !File.Exists(f)) return "{\"ok\":true,\"removed\":0}";
            try
            {
                var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f, Encoding.UTF8));
                object io; if (root == null || !root.TryGetValue("items", out io) || !(io is System.Collections.ArrayList)) return "{\"ok\":true,\"removed\":0}";
                var items = (System.Collections.ArrayList)io; int before = items.Count;
                var keep = new System.Collections.ArrayList();
                foreach (object o in items)
                {
                    var d = o as Dictionary<string, object>; object id;
                    if (d == null || !d.TryGetValue("id", out id) || !(id is string) || !((string)id).StartsWith("f:")) { keep.Add(o); continue; }
                    bool ok = true; try { ok = File.Exists(((string)id).Substring(2)); } catch { }
                    if (ok) keep.Add(o);
                }
                int removed = before - keep.Count;
                if (removed > 0) { root["items"] = keep; SaveText(f, js.Serialize(root)); P.Log("Biblioteca: se quitaron " + removed + " archivos que ya no existen"); }
                return "{\"ok\":true,\"removed\":" + removed + "}";
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }
}

namespace RetroAmp
{
    // Lo último que mandó una radio (hasta 4 MB), para entregarlo al reproductor en pedazos
    class RadioBuf : Stream
    {
        readonly object k = new object();
        readonly MemoryStream mem = new MemoryStream();
        long baseOff;
        DateTime lastTake = DateTime.Now;
        public readonly ManualResetEvent Ready = new ManualResetEvent(false);
        public volatile string Type, Error;
        public volatile bool Done;
        public bool Stale { get { return Done || (DateTime.Now - lastTake).TotalSeconds > 30; } }

        public override void Write(byte[] b, int o, int n)
        {
            lock (k)
            {
                if (Done || (DateTime.Now - lastTake).TotalSeconds > 30) throw new IOException("nadie la escucha");
                mem.Write(b, o, n);
                if (mem.Length > 4 << 20)
                {   // se queda con los últimos 2 MB
                    byte[] all = mem.ToArray(); int keep = 2 << 20;
                    mem.SetLength(0); mem.Write(all, all.Length - keep, keep); baseOff += all.Length - keep;
                }
                Monitor.PulseAll(k);
            }
        }
        // Lo que hay desde "pos" (espera hasta que llegue algo); null si la radio ya terminó
        public byte[] Take(long pos, int waitMs, out long next)
        {
            lock (k)
            {
                lastTake = DateTime.Now;
                DateTime end = DateTime.Now.AddMilliseconds(waitMs);
                while (pos >= baseOff + mem.Length && !Done)
                {
                    int left = (int)(end - DateTime.Now).TotalMilliseconds; if (left <= 0) break;
                    Monitor.Wait(k, left);
                }
                if (pos < baseOff) pos = baseOff;
                long have = baseOff + mem.Length - pos;
                if (have <= 0) { next = pos; return Done ? null : new byte[0]; }
                int n = (int)Math.Min(have, 1 << 20);
                byte[] r = new byte[n];
                Array.Copy(mem.GetBuffer(), (int)(pos - baseOff), r, 0, n);
                next = pos + n; lastTake = DateTime.Now;
                return r;
            }
        }
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int n) { throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
    }
}
