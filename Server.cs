// RetroAmp - motor del reproductor (servidor local, antes servidor.ps1)
// Atiende la interfaz en http://127.0.0.1:<puerto>/ y usa yt-dlp + Deno para YouTube.
using System;
using System.Collections;
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

namespace RetroAmp
{
    public class Paths
    {
        public string Root;      // carpeta del .exe (archivos del programa)
        public string Data;      // donde se guardan los datos (normalmente = Root)
        public string Bin;       // herramientas descargadas (yt-dlp, deno, ffmpeg)
        public string WebView;   // librerias de WebView2
        public string Html;
        public string Fonts;
        public string Cache;
        public string Pls;
        public string Profile;
        public string State;
        public string LogFile;
        public string Conf;      // media\perfil: lista actual, ajustes, biblioteca, Spotify y posición de las ventanas
        public string MusicUser; // Musica\WinampYT (versiones viejas)
        public string MusicApp;  // <datos>\Media\Musica
        public string VideoApp;  // <datos>\Media\Video

        public string YtDlp { get { return Path.Combine(Bin, "yt-dlp.exe"); } }
        public string Deno { get { return Path.Combine(Bin, "deno.exe"); } }
        public string Ffmpeg { get { return Path.Combine(Bin, "ffmpeg.exe"); } }

        public void Log(string m)
        {
            try { File.AppendAllText(LogFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + m + Environment.NewLine); } catch { }
        }
    }

    class Mp3Job { public string Id, Title, Ch, T, Dir; public bool Video; }

    public partial class Server
    {
        readonly Paths P;
        readonly Hashtable ctx;
        TcpListener listener;
        public int Port;
        public string Token;
        volatile bool stopping;

        readonly object mp3Lock = new object();
        readonly List<Mp3Job> mp3Q = new List<Mp3Job>();
        string mp3Cur = "", mp3Err = "";
        int mp3Done, mp3Fail;
        volatile bool mp3InApp;
        readonly Dictionary<string, object> prepLocks = new Dictionary<string, object>();

        const string Tpl = "%(id)s|~|%(duration|0)s|~|%(channel,uploader|)s|~|%(title)s";
        static readonly Regex VideoId = new Regex(@"^[\w-]{11}$");

        public Server(Paths p, Hashtable ctx) { P = p; this.ctx = ctx; }

        public string Url { get { return "http://127.0.0.1:" + Port + "/"; } }
        public string StartUrl { get { return Url + "?k=" + Token; } }
        // Las descargas siempre van dentro de la carpeta del programa (portable en una USB)
        public string MusicDir { get { return P.MusicApp; } }
        public string VideoDir { get { return P.VideoApp; } }

        public void Start()
        {
            byte[] b = new byte[16];
            using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b);
            Token = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            try { listener = new TcpListener(IPAddress.Loopback, 8765); listener.Start(); }
            catch { listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread t = new Thread(AcceptLoop); t.IsBackground = true; t.Name = "servidor"; t.Start();
            Thread w = new Thread(Mp3Worker); w.IsBackground = true; w.Name = "mp3"; w.Start();
        }

        public void Stop()
        {
            try { RecStopAll(); } catch { } try { DjRecStopAll(); } catch { }   // primero se cierran bien las grabaciones (usan este servidor)
            stopping = true;
            try { listener.Stop(); } catch { }
        }

        void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { if (stopping) return; Thread.Sleep(50); continue; }
                Thread t = new Thread(delegate () { Handle(c); });
                t.IsBackground = true;
                t.Start();
            }
        }

        // ---------------- HTTP ----------------
        class Req
        {
            public string Method, Path;
            public Dictionary<string, string> Q = new Dictionary<string, string>();
            public Dictionary<string, string> H = new Dictionary<string, string>();
            public string Body = "";
            public byte[] Raw = new byte[0];
            public string Q1(string k) { string v; return Q.TryGetValue(k, out v) ? v : ""; }
            public string H1(string k) { string v; return H.TryGetValue(k, out v) ? v : ""; }
        }

        static string ReadLine(Stream s)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
                if (sb.Length > 16384) throw new IOException("cabecera demasiado larga");
            }
            return sb.ToString();
        }

        static Dictionary<string, string> ParseQuery(string qs)
        {
            Dictionary<string, string> q = new Dictionary<string, string>();
            foreach (string kv in qs.Split('&'))
            {
                if (kv.Length == 0) continue;
                int i = kv.IndexOf('=');
                string k = i < 0 ? kv : kv.Substring(0, i), v = i < 0 ? "" : kv.Substring(i + 1);
                try { q[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            }
            return q;
        }

        void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;
                NetworkStream s = client.GetStream();
                string line = ReadLine(s);
                if (line == null) return;
                Match m = Regex.Match(line, @"^([A-Z]+)\s+(\S+)");
                if (!m.Success) return;
                Req r = new Req();
                r.Method = m.Groups[1].Value;
                string target = m.Groups[2].Value;
                while (true)
                {
                    string h = ReadLine(s);
                    if (string.IsNullOrEmpty(h)) break;
                    int i = h.IndexOf(':');
                    if (i > 0) r.H[h.Substring(0, i).Trim().ToLowerInvariant()] = h.Substring(i + 1).Trim();
                }
                int len;
                if (r.Method == "POST" && int.TryParse(r.H1("content-length"), out len) && len > 0 && len <= 8 * 1024 * 1024)
                {
                    byte[] buf = new byte[len]; int got = 0;
                    while (got < len) { int k = s.Read(buf, got, len - got); if (k <= 0) break; got += k; }
                    r.Body = Encoding.UTF8.GetString(buf, 0, got);
                    if (got < len) Array.Resize(ref buf, got); r.Raw = buf;
                }
                int qi = target.IndexOf('?');
                if (qi >= 0) { r.Path = target.Substring(0, qi); r.Q = ParseQuery(target.Substring(qi + 1)); }
                else r.Path = target;
                Route(s, r);
            }
            catch (IOException) { /* conexion cortada por el navegador */ }
            catch (Exception ex) { P.Log("Servidor: " + ex); }
            finally { try { client.Close(); } catch { } }
        }

        bool Authorized(Req r, out bool viaQuery)
        {
            viaQuery = false;
            // Solo se aceptan peticiones dirigidas a 127.0.0.1/localhost (evita "DNS rebinding")
            string host = r.H1("host");
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) return false;
            if (r.Q1("k") == Token) { viaQuery = true; return true; }
            foreach (string part in r.H1("cookie").Split(';'))
            {
                string c = part.Trim();
                if (c == "wyt=" + Token) return true;
            }
            return false;
        }

        static void SendBytes(Stream s, int code, string type, byte[] body, string extra)
        {
            string status;
            switch (code)
            {
                case 200: status = "OK"; break;
                case 206: status = "Partial Content"; break;
                case 403: status = "Forbidden"; break;
                case 404: status = "Not Found"; break;
                case 416: status = "Range Not Satisfiable"; break;
                default: status = "Error"; break;
            }
            string hdr = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" + (extra ?? "") + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(hdr);
            s.Write(h, 0, h.Length);
            if (body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }
        static void SendJson(Stream s, string json) { SendBytes(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), null); }
        static void Send404(Stream s) { SendBytes(s, 404, "text/plain", new byte[0], null); }

        // ---------------- JSON minimo ----------------
        public static string J(string v)
        {
            if (v == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in v)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
        static string J(bool b) { return b ? "true" : "false"; }
        static string Err(string msg) { return "{\"ok\":false,\"error\":" + J(msg) + "}"; }

        class Entry { public string Id, Ch, Title; public int Dur; }

        static string ItemsJson(List<Entry> items, bool withDur)
        {
            StringBuilder sb = new StringBuilder("{\"ok\":true,\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                Entry e = items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J(e.Id));
                if (withDur) sb.Append(",\"dur\":").Append(e.Dur);
                sb.Append(",\"ch\":").Append(J(e.Ch)).Append(",\"title\":").Append(J(e.Title)).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        static List<Entry> ParseEntries(string text, bool lists)
        {
            List<Entry> list = new List<Entry>();
            Regex idRx = lists ? new Regex(@"^[\w-]{12,}$") : VideoId;
            foreach (string ln in Regex.Split(text ?? "", "\r?\n"))
            {
                string[] p = ln.Split(new string[] { "|~|" }, StringSplitOptions.None);
                if (p.Length < 4 || !idRx.IsMatch(p[0])) continue;
                double d;
                double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                string ch = p[2] == "NA" ? "" : p[2];
                string title = string.Join("|~|", p, 3, p.Length - 3);
                list.Add(new Entry { Id = p[0], Dur = (int)d, Ch = ch, Title = title });
            }
            return list;
        }

        // ---------------- yt-dlp ----------------
        // ---- Nombre de las descargas: "Artista - Canción" ----
        public static string CleanArtist(string ch)
        {
            string a = (ch ?? "").Trim();
            a = Regex.Replace(a, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"\s*VEVO$", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"^(Oficial|Official)(?=\s|[A-ZÁÉÍÓÚÑ]|$)\s*", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"\s*(Oficial|Official)$", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"\s+", " ").Trim(' ', '-', '_', '.');
            return a == "" ? (ch ?? "").Trim() : a;
        }
        static string Plain(string x)
        {
            string d = (x ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder();
            foreach (char c in d) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
        public static string SafeFile(string x)
        {
            string a = Regex.Replace(x ?? "", "[\\/:*?\"<>|\x00-\x1f]", " ");
            a = Regex.Replace(a, @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (a.Length > 150) a = a.Substring(0, 150).TrimEnd('.', ' ');
            return a;
        }
        // texto fijo para --parse-metadata de yt-dlp (como plantilla, sin campos)
        static string MetaLit(string v) { return v.Replace("\\", " ").Replace("%", "%%") + "%(wyt_nada|)s"; }
        internal static string DownloadName(Mp3Job job, out string artist, out string title)
        {
            string ch = job.Ch ?? "", t = (job.T ?? "").Trim();
            artist = ""; title = "";
            if (t == "") return SafeFile(job.Title);
            string a = CleanArtist(ch);
            Match m = Regex.Match(t, @"^(.+?)\s+[-–—]\s+(.+)$");
            string name;
            if (m.Success) { name = t; artist = m.Groups[1].Value; title = m.Groups[2].Value; }   // el título ya trae "Artista - Canción"
            else if (a == "" || (Plain(a).Length > 0 && Plain(t).Contains(Plain(a)))) { name = t; artist = a; title = t; }
            else { name = a + " - " + t; artist = a; title = t; }
            artist = SafeFile(artist); title = SafeFile(title);
            return SafeFile(name);
        }

        public static string QuoteArg(string a)
        {
            if (!string.IsNullOrEmpty(a) && !Regex.IsMatch(a, "[\\s\"]")) return a;
            a = Regex.Replace(a ?? "", "(\\\\*)\"", "$1$1\\\"");
            a = Regex.Replace(a, "(\\\\+)$", "$1$1");
            return "\"" + a + "\"";
        }

        public class RunResult { public int Code; public string Out = "", Err = ""; }

        public static RunResult Run(string exe, IEnumerable<string> args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            List<string> q = new List<string>();
            foreach (string a in args) q.Add(QuoteArg(a));
            psi.Arguments = string.Join(" ", q.ToArray());
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            RunResult r = new RunResult();
            using (Process p = Process.Start(psi))
            {
                System.Threading.Tasks.Task<string> o = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    r.Code = -1; r.Err = "Se agoto el tiempo de espera";
                    return r;
                }
                p.WaitForExit();
                r.Code = p.ExitCode; r.Out = o.Result; r.Err = e.Result;
            }
            return r;
        }

        RunResult YtDlp(string[] args, int timeoutMs)
        {
            List<string> all = new List<string> { "--no-warnings", "--encoding", "utf-8", "--js-runtimes", "deno:" + P.Deno };
            all.AddRange(args);
            try { return Run(P.YtDlp, all, timeoutMs); }
            catch (Exception ex) { return new RunResult { Code = -1, Err = "ERROR: no pude ejecutar yt-dlp (" + ex.Message + ")" }; }
        }

        public static string ShortErr(string err)
        {
            string[] lines = Regex.Split((err ?? "").Trim(), "\r?\n");
            string t = "";
            foreach (string l in lines) if (l.Contains("ERROR")) t = l;
            if (t == "" && lines.Length > 0) t = lines[lines.Length - 1];
            t = Regex.Replace(t, @"^ERROR:\s*", "").Trim();
            return t == "" ? "error desconocido" : t;
        }

        FileInfo FindAudio(string id)
        {
            string[] exts = { ".m4a", ".webm", ".mp4", ".opus", ".ogg", ".mp3" };
            foreach (string ext in exts)
            {
                FileInfo f = new FileInfo(Path.Combine(P.Cache, id + ext));
                if (f.Exists) return f;
            }
            return null;
        }

        static string SafeName(string n)
        {
            n = Regex.Replace(n ?? "", "[\\\\/:*?\"<>|\\x00-\\x1f]", "").Trim().TrimEnd('.');
            if (n.Length > 60) n = n.Substring(0, 60).Trim();
            return n;
        }

        static void SaveText(string file, string text)
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        static void SendFileJson(Stream s, string file)
        {
            byte[] b = File.Exists(file) ? File.ReadAllBytes(file) : Encoding.ASCII.GetBytes("{}");
            SendBytes(s, 200, "application/json; charset=utf-8", b, null);
        }

        // ---------------- Rutas ----------------
        void Route(Stream s, Req r)
        {
            bool viaQuery;
            if (SpCallback(s, r)) return;
            if (!Authorized(r, out viaQuery)) { SendBytes(s, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("RetroAmp: acceso no permitido"), null); return; }
            string path = r.Path;
            if (SpRoute(s, r)) return;
            if (UpdRoute(s, r)) return;
            if (AppUpdRoute(s, r)) return;
            if (RecRoute(s, r)) return;
            if (DjRecRoute(s, r)) return;
            if (r.Path == "/api/note")
            {   // la página anota en registro.txt por qué no abrió un canal (para poder corregirlo)
                string m = Regex.Replace(r.Q1("m"), @"[\x00-\x1f]", " "); if (m.Length > 600) m = m.Substring(0, 600);
                if (m != "") P.Log("Página: " + m);
                SendJson(s, "{\"ok\":true}"); return;
            }
            if (SubsRoute(s, r)) return;
            if (SkinRoute(s, r)) return;
            if (RouteTv(s, r, path)) return;

            if (path == "/" || path == "/index.html" || path == "/RetroAmp.html" || path == "/WinampYT.html")
            {
                string cookie = viaQuery ? "Set-Cookie: wyt=" + Token + "; Path=/; HttpOnly; SameSite=Strict\r\n" : null;
                byte[] html = File.Exists(P.Html) ? File.ReadAllBytes(P.Html) : Encoding.UTF8.GetBytes("<h1>Falta RetroAmp.html</h1>");
                SendBytes(s, 200, "text/html; charset=utf-8", html, cookie);
            }
            else if (path.StartsWith("/fonts/"))
            {
                string name = path.Substring(7);
                string fp = Path.Combine(P.Fonts, name);
                if (!Regex.IsMatch(name, @"^[\w.-]+\.(ttf|woff2?)$") || !File.Exists(fp)) { Send404(s); return; }
                string type = name.EndsWith(".ttf") ? "font/ttf" : name.EndsWith(".woff2") ? "font/woff2" : "font/woff";
                SendBytes(s, 200, type, File.ReadAllBytes(fp), null);
            }
            else if (path == "/api/ping") SendJson(s, "{\"ok\":true}");
            else if (path == "/api/search")
            {
                string term = Regex.Replace(r.Q1("q"), "[\"\\r\\n]", " ").Trim();
                if (term == "") { SendJson(s, Err("Busqueda vacia")); return; }
                RunResult x = YtDlp(new string[] { "--flat-playlist", "--print", Tpl, "ytsearch20:" + term }, 90000);
                List<Entry> items = ParseEntries(x.Out, false);
                SendJson(s, items.Count > 0 ? ItemsJson(items, true) : Err(ShortErr(x.Err)));
            }
            else if (path == "/api/searchlists")
            {
                string term = Regex.Replace(r.Q1("q"), "[\"\\r\\n]", " ").Trim();
                if (term == "") { SendJson(s, Err("Busqueda vacia")); return; }
                string su = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(term) + "&sp=EgIQAw%253D%253D";
                RunResult x = YtDlp(new string[] { "--flat-playlist", "--playlist-end", "12", "--print", Tpl, su }, 90000);
                List<Entry> items = ParseEntries(x.Out, true);
                SendJson(s, items.Count > 0 ? ItemsJson(items, false) : Err(ShortErr(x.Err)));
            }
            else if (path == "/api/list")
            {
                string u = r.Q1("url");
                if (!Regex.IsMatch(u, "^https://[^\\s\"]+$")) { SendJson(s, Err("Enlace no valido")); return; }
                RunResult x = YtDlp(new string[] { "--flat-playlist", "--print", Tpl, u }, 180000);
                List<Entry> items = ParseEntries(x.Out, false);
                SendJson(s, items.Count > 0 ? ItemsJson(items, true) : Err(ShortErr(x.Err)));
            }
            else if (path == "/api/prep")
            {
                string id = r.Q1("id");
                if (!VideoId.IsMatch(id)) { SendJson(s, Err("id no valido")); return; }
                object lk;
                lock (prepLocks) { if (!prepLocks.TryGetValue(id, out lk)) { lk = new object(); prepLocks[id] = lk; } }
                FileInfo f; string errMsg = "";
                lock (lk)
                {
                    f = FindAudio(id);
                    if (f == null)
                    {
                        string outTpl = Path.Combine(P.Cache, "%(id)s.%(ext)s");
                        RunResult x = YtDlp(new string[] { "-f", "bestaudio[ext=m4a]/bestaudio/best", "--no-playlist", "--no-progress", "--no-mtime", "-q", "-o", outTpl, "https://www.youtube.com/watch?v=" + id }, 300000);
                        f = FindAudio(id);
                        if (f == null) errMsg = ShortErr(x.Err);
                    }
                }
                if (f != null) SendJson(s, "{\"ok\":true,\"url\":" + J("/audio/" + f.Name) + ",\"size\":" + f.Length + "}");
                else SendJson(s, Err(errMsg));
            }
            else if (path.StartsWith("/audio/")) SendAudio(s, r, path.Substring(7));
            else if (path == "/api/show")
            {
                ctx["Show"] = true;
                SendJson(s, "{\"ok\":true,\"host\":" + J(ctx["HostMode"] is bool && (bool)ctx["HostMode"]) + "}");
            }
            else if (path == "/api/state")
            {
                if (r.Method == "POST")
                {
                    if (r.Body.StartsWith("{")) { SaveText(P.State, r.Body); SendJson(s, "{\"ok\":true}"); }
                    else SendJson(s, Err("datos no validos"));
                }
                else SendFileJson(s, P.State);
            }
            else if (path == "/api/pls")
            {
                List<FileInfo> files = new List<FileInfo>();
                try { foreach (FileInfo fi in new DirectoryInfo(P.Pls).GetFiles("*.json")) files.Add(fi); } catch { }
                files.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"names\":[");
                for (int i = 0; i < files.Count; i++) { if (i > 0) sb.Append(','); sb.Append(J(Path.GetFileNameWithoutExtension(files[i].Name))); }
                SendJson(s, sb.Append("]}").ToString());
            }
            else if (path.StartsWith("/api/pls/"))
            {
                string nm = SafeName(r.Q1("name"));
                if (nm == "") { SendJson(s, Err("Nombre no valido")); return; }
                string file = Path.Combine(P.Pls, nm + ".json");
                if (path == "/api/pls/get")
                {
                    if (File.Exists(file)) SendFileJson(s, file); else SendJson(s, Err("No existe esa lista"));
                }
                else if (path == "/api/pls/save" && r.Method == "POST")
                {
                    if (r.Body.StartsWith("{")) { SaveText(file, r.Body); SendJson(s, "{\"ok\":true,\"name\":" + J(nm) + "}"); }
                    else SendJson(s, Err("datos no validos"));
                }
                else if (path == "/api/pls/del")
                {
                    try { File.Delete(file); } catch { }
                    SendJson(s, "{\"ok\":true}");
                }
                else Send404(s);
            }
            else if (path == "/api/mp3")
            {
                string id = r.Q1("id");
                if (!VideoId.IsMatch(id)) { SendJson(s, Err("id no valido")); return; }
                bool video = r.Q1("video") == "1";
                string dir = video ? VideoDir : MusicDir;
                string sub = SafeName(r.Q1("sub"));
                if (sub != "") dir = Path.Combine(dir, sub);
                int pending;
                lock (mp3Lock)
                {
                    bool dup = false;
                    foreach (Mp3Job j in mp3Q) if (j.Id == id && j.Dir == dir) dup = true;
                    if (!dup) mp3Q.Add(new Mp3Job { Id = id, Title = r.Q1("title"), Ch = r.Q1("ch"), T = r.Q1("t"), Dir = dir, Video = video });
                    pending = mp3Q.Count;
                }
                SendJson(s, "{\"ok\":true,\"pending\":" + pending + "}");
            }
            else if (path == "/api/mp3/status")
            {
                string json;
                lock (mp3Lock)
                    json = "{\"ok\":true,\"pending\":" + mp3Q.Count + ",\"current\":" + J(mp3Cur) + ",\"done\":" + mp3Done + ",\"failed\":" + mp3Fail +
                        ",\"lastError\":" + J(mp3Err) + ",\"dir\":" + J(MusicDir) + ",\"inApp\":" + J(mp3InApp) + "}";
                SendJson(s, json);
            }
            else if (path == "/api/mp3/where")
            {
                SendJson(s, "{\"ok\":true,\"dir\":" + J(MusicDir) + ",\"video\":" + J(VideoDir) + ",\"old\":" + J(Directory.Exists(P.MusicUser) ? P.MusicUser : "") + "}");
            }
            else if (path == "/api/mp3/import")
            {
                // Copia (no mueve) lo que se descargaba antes en Musica\WinampYT a Media\Musica y Media\Video
                int n = 0, skip = 0;
                try
                {
                    if (Directory.Exists(P.MusicUser))
                        foreach (string f in Directory.GetFiles(P.MusicUser, "*", SearchOption.AllDirectories))
                        {
                            if (!Local.IsMedia(f)) continue;
                            string rel = f.Substring(P.MusicUser.Length).TrimStart('\\', '/');
                            string dest = Path.Combine(Local.IsVideo(f) && !f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? VideoDir : MusicDir, rel);
                            if (File.Exists(dest)) { skip++; continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            File.Copy(f, dest); n++;
                        }
                    SendJson(s, "{\"ok\":true,\"copied\":" + n + ",\"skipped\":" + skip + ",\"dir\":" + J(MusicDir) + "}");
                }
                catch (Exception ex) { P.Log("Importar MP3: " + ex.Message); SendJson(s, Err("No pude copiar: " + ex.Message + " (se copiaron " + n + ")")); }
            }
            else if (path == "/api/mp3/open")
            {
                string dir = r.Q1("video") == "1" ? VideoDir : MusicDir;
                if (r.Q1("old") == "1" && Directory.Exists(P.MusicUser)) dir = P.MusicUser;
                try { Directory.CreateDirectory(dir); } catch { }
                string sub = SafeName(r.Q1("sub"));
                if (sub != "" && Directory.Exists(Path.Combine(dir, sub))) dir = Path.Combine(dir, sub);
                try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
                SendJson(s, "{\"ok\":true,\"dir\":" + J(dir) + "}");
            }
            else if (path == "/api/local/pick")
            {
                string[] picked = Local.Pick(r.Q1("mode") == "folder");
                SendJson(s, LocalJson(Local.Expand(picked)));
            }
            else if (path == "/api/local/add" && r.Method == "POST")
            {
                SendJson(s, LocalJson(Local.Expand(Regex.Split(r.Body, "\r?\n"))));
            }
            else if (path == "/api/local/prep")
            {
                string fp = r.Q1("p");
                if (!Local.IsMedia(fp)) { SendJson(s, Err("Ese tipo de archivo no se puede reproducir")); return; }
                FileInfo fi = new FileInfo(fp);
                if (!fi.Exists) { SendJson(s, Err("No encuentro el archivo (¿se movió o se borró?): " + fp)); return; }
                bool pending; int pct; string url, live; double dur;
                try { url = PrepLocalVideo(fi, out pending, out pct, out live, out dur); }
                catch (Exception ex) { SendJson(s, Err(ex.Message)); return; }
                if (pending)
                {
                    SendJson(s, "{\"ok\":false,\"pending\":true,\"pct\":" + pct + ",\"dur\":" + ((int)dur).ToString() + (live != null ? ",\"live\":" + J(live) : "") + "}");
                    return;
                }
                SendJson(s, "{\"ok\":true,\"url\":" + J(url) + ",\"size\":" + fi.Length + ",\"video\":" + J(Local.IsVideo(fi.FullName)) + "}");
            }
            else if (path == "/local")
            {
                string fp = r.Q1("p");
                if (!Local.IsMedia(fp) || !File.Exists(fp)) { Send404(s); return; }
                SendFile(s, r, fp, Local.MimeOf(fp));
            }
            else if (!RouteExtra(s, r, path)) Send404(s);
        }

        static string LocalJson(List<LocalTrack> list)
        {
            StringBuilder sb = new StringBuilder("{\"ok\":true,\"items\":[");
            for (int i = 0; i < list.Count; i++)
            {
                LocalTrack t = list[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J("f:" + t.Path)).Append(",\"title\":").Append(J(t.Title)).Append(",\"ch\":").Append(J(t.Artist))
                  .Append(",\"alb\":").Append(J(t.Album)).Append(",\"dur\":").Append(t.Dur).Append('}');
            }
            return sb.Append("]}").ToString();
        }

        void SendAudio(Stream s, Req r, string name)
        {
            Match nm = Regex.Match(name, @"^[\w-]{11}\.(m4a|webm|mp4|opus|ogg|mp3)$");
            if (!nm.Success) { Send404(s); return; }
            string fp = Path.Combine(P.Cache, name);
            if (!File.Exists(fp)) { Send404(s); return; }
            string ext = nm.Groups[1].Value;
            string type = ext == "webm" ? "audio/webm" : (ext == "opus" || ext == "ogg") ? "audio/ogg" : ext == "mp3" ? "audio/mpeg" : "audio/mp4";
            SendFile(s, r, fp, type);
        }

        void SendFile(Stream s, Req r, string fp, string type)
        {
            using (FileStream fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length, start = 0, end = len - 1;
                int code = 200;
                Match rg = Regex.Match(r.H1("range"), @"bytes=(\d*)-(\d*)");
                if (rg.Success)
                {
                    code = 206;
                    if (rg.Groups[1].Value != "")
                    {
                        start = long.Parse(rg.Groups[1].Value);
                        if (rg.Groups[2].Value != "") end = Math.Min(long.Parse(rg.Groups[2].Value), len - 1);
                    }
                    else if (rg.Groups[2].Value != "") start = Math.Max(0, len - long.Parse(rg.Groups[2].Value));
                }
                if (start >= len || start > end) { SendBytes(s, 416, "text/plain", new byte[0], "Content-Range: bytes */" + len + "\r\n"); return; }
                long count = end - start + 1;
                string hdr = "HTTP/1.1 " + code + (code == 206 ? " Partial Content" : " OK") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + count + "\r\nAccept-Ranges: bytes\r\n";
                if (code == 206) hdr += "Content-Range: bytes " + start + "-" + end + "/" + len + "\r\n";
                hdr += "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(hdr);
                s.Write(hb, 0, hb.Length);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[65536];
                long left = count;
                while (left > 0)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    s.Write(buf, 0, n);
                    left -= n;
                }
                s.Flush();
            }
        }

        // ---------------- Conversion a MP3 en segundo plano ----------------
        void Mp3Worker()
        {
            while (!stopping)
            {
                Mp3Job job = null;
                lock (mp3Lock) { if (mp3Q.Count > 0) { job = mp3Q[0]; mp3Q.RemoveAt(0); } }
                if (job == null) { Thread.Sleep(300); continue; }
                try
                {
                    if (!File.Exists(P.Ffmpeg))
                    {
                        Deps.EnsureFfmpeg(P, delegate (string m) { lock (mp3Lock) mp3Cur = m; });
                    }
                    lock (mp3Lock) mp3Cur = string.IsNullOrEmpty(job.Title) ? job.Id : job.Title;
                    Directory.CreateDirectory(job.Dir);
                    List<string> args = new List<string> { "--no-warnings", "--js-runtimes", "deno:" + P.Deno, "--ffmpeg-location", P.Bin };
                    if (job.Video)
                        args.AddRange(new string[] { "-f", "bestvideo[height<=1080][vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                            "--merge-output-format", "mp4", "--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg" });
                    else
                        args.AddRange(new string[] { "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0",
                            "--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg" });
                    // Nombre del archivo: "Artista - Canción" (el artista es el canal, sin VEVO, - Topic ni Oficial)
                    string art, tit, nm = DownloadName(job, out art, out tit);
                    if (art != "") args.AddRange(new string[] { "--parse-metadata", MetaLit(art) + ":%(artist)s" });
                    if (tit != "") args.AddRange(new string[] { "--parse-metadata", MetaLit(tit) + ":%(title)s" });
                    args.AddRange(new string[] { "--no-playlist", "--no-progress", "-q", "--no-mtime",
                        "-o", Path.Combine(job.Dir, nm == "" ? "%(title)s.%(ext)s" : nm.Replace("%", "%%") + ".%(ext)s"), "https://www.youtube.com/watch?v=" + job.Id });
                    RunResult x = Run(P.YtDlp, args, 900000);
                    if (x.Code != 0) throw new Exception(ShortErr(x.Err));
                    lock (mp3Lock) mp3Done++;
                }
                catch (Exception ex)
                {
                    lock (mp3Lock)
                    {
                        mp3Fail++;
                        mp3Err = (string.IsNullOrEmpty(job.Title) ? job.Id : job.Title) + ": " + ex.Message;
                    }
                    P.Log("MP3: " + mp3Err);
                }
                lock (mp3Lock) mp3Cur = "";
            }
        }
    }
}
