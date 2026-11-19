// RetroAmp - servidor local (solo 127.0.0.1) y conversion a MP3
// Reemplaza a servidor.ps1: todo corre dentro de RetroAmp.exe, sin PowerShell.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RetroAmp
{
    // Estado compartido entre el servidor, el conversor MP3 y la ventana
    public static class App
    {
        public const int Port = 8765;
        public static readonly string Url = "http://127.0.0.1:" + Port + "/";
        public static string Root, Bin, Cache, Pls, Prof, State, Html, Ytdlp, Deno, MusicDir, LogFile;
        public static volatile bool Stop, Show, HostMode;

        public static readonly object Mp3Lock = new object();
        public static readonly List<Mp3Job> Mp3Q = new List<Mp3Job>();
        public static string Mp3Cur = "", Mp3Err = "";
        public static int Mp3Done, Mp3Fail;

        public static void Init(string root)
        {
            Root = root;
            Bin = Path.Combine(root, "bin");
            Cache = Path.Combine(root, "cache");
            Pls = Path.Combine(root, "playlists");
            Prof = Path.Combine(root, "perfil");
            State = Path.Combine(root, "estado.json");
            Html = Path.Combine(root, "RetroAmp.html");
            Ytdlp = Path.Combine(Bin, "yt-dlp.exe");
            Deno = Path.Combine(Bin, "deno.exe");
            LogFile = Path.Combine(root, "registro.txt");
            string music = "";
            try { music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); } catch { }
            if (string.IsNullOrEmpty(music))
            {
                string up = Environment.GetEnvironmentVariable("USERPROFILE");
                music = string.IsNullOrEmpty(up) ? root : Path.Combine(up, "Music");
            }
            MusicDir = Path.Combine(music, "RetroAmp");
        }

        public static void Log(string m)
        {
            try { File.AppendAllText(LogFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + m + Environment.NewLine); } catch { }
        }
    }

    public class Mp3Job { public string Id, Title, Dir; }

    // JSON minimo (solo lo que necesita el reproductor)
    public static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder b = new StringBuilder(s.Length + 2);
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
            return b.ToString();
        }

        // Pares clave/valor: string, bool, int, long u otro texto ya en JSON (RawJson)
        public static string Obj(params object[] kv)
        {
            StringBuilder b = new StringBuilder("{");
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (i > 0) b.Append(',');
                b.Append(Str((string)kv[i])).Append(':').Append(Val(kv[i + 1]));
            }
            return b.Append('}').ToString();
        }

        static string Val(object v)
        {
            if (v == null) return "null";
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is int || v is long) return Convert.ToString(v, CultureInfo.InvariantCulture);
            if (v is RawJson) return ((RawJson)v).Text;
            return Str(v.ToString());
        }
    }

    public class RawJson { public string Text; public RawJson(string t) { Text = t; } }

    public static class Server
    {
        static TcpListener listener;
        const string Tpl = "%(id)s|~|%(duration|0)s|~|%(channel,uploader|)s|~|%(title)s";
        static readonly Regex IdRx = new Regex(@"^[\w-]{11}$");
        static readonly string[] AudioExt = { ".m4a", ".webm", ".mp4", ".opus", ".ogg", ".mp3" };
        static readonly Dictionary<string, object> prepLocks = new Dictionary<string, object>();

        public static void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, App.Port);
            listener.Start();
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "RetroAmp-servidor";
            t.Start();
        }

        public static void StopServer()
        {
            try { listener.Stop(); } catch { }
        }

        static void AcceptLoop()
        {
            while (!App.Stop)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (App.Stop) break; Thread.Sleep(50); continue; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
            }
        }

        // ---------- utilidades HTTP ----------
        static void SendBytes(Stream s, int code, string type, byte[] body, string extra = "")
        {
            string status;
            switch (code)
            {
                case 200: status = "OK"; break;
                case 206: status = "Partial Content"; break;
                case 400: status = "Bad Request"; break;
                case 404: status = "Not Found"; break;
                case 416: status = "Range Not Satisfiable"; break;
                default: status = "Internal Server Error"; break;
            }
            string hdr = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                         "\r\nCache-Control: no-store\r\n" + extra + "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(hdr);
            s.Write(h, 0, h.Length);
            if (body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }

        static void SendJson(Stream s, string json)
        {
            SendBytes(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        static void SendErr(Stream s, string msg)
        {
            SendJson(s, Json.Obj("ok", false, "error", msg));
        }

        static void Send404(Stream s) { SendBytes(s, 404, "text/plain", new byte[0]); }

        static void SendRaw(Stream s, string file)
        {
            if (File.Exists(file)) SendBytes(s, 200, "application/json; charset=utf-8", File.ReadAllBytes(file));
            else SendBytes(s, 200, "application/json; charset=utf-8", Encoding.ASCII.GetBytes("{}"));
        }

        static void SaveText(string file, string text)
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, Encoding.ASCII);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        static Dictionary<string, string> GetQuery(string qs)
        {
            Dictionary<string, string> q = new Dictionary<string, string>();
            foreach (string kv in qs.Split('&'))
            {
                if (kv.Length == 0) continue;
                int i = kv.IndexOf('=');
                string k = i < 0 ? kv : kv.Substring(0, i);
                string v = i < 0 ? "" : kv.Substring(i + 1);
                q[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return q;
        }

        static string Q(Dictionary<string, string> q, string k)
        {
            string v;
            return q.TryGetValue(k, out v) ? v : "";
        }

        public static string SafeName(string n)
        {
            n = Regex.Replace(n ?? "", "[\\\\/:*?\"<>|\\x00-\\x1f]", "").Trim().TrimEnd('.');
            if (n.Length > 60) n = n.Substring(0, 60).Trim();
            return n;
        }

        // ---------- yt-dlp ----------
        public static string QuoteArg(string a)
        {
            if (!string.IsNullOrEmpty(a) && !Regex.IsMatch(a, "[\\s\"]")) return a;
            a = Regex.Replace(a ?? "", "(\\\\*)\"", "$1$1\\\"");
            a = Regex.Replace(a, "(\\\\+)$", "$1$1");
            return "\"" + a + "\"";
        }

        public class RunResult { public int Code; public string Out = "", Err = ""; }

        public static RunResult RunTool(string exe, IEnumerable<string> args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = string.Join(" ", args.Select(QuoteArg).ToArray());
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
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
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

        public static RunResult RunYtdlp(IEnumerable<string> args, int timeoutMs)
        {
            List<string> all = new List<string> { "--no-warnings", "--encoding", "utf-8", "--js-runtimes", "deno:" + App.Deno };
            all.AddRange(args);
            return RunTool(App.Ytdlp, all, timeoutMs);
        }

        public static string ShortErr(string err)
        {
            err = err ?? "";
            string[] lines = Regex.Split(err, "\r?\n");
            string t = lines.Where(l => l.Contains("ERROR")).LastOrDefault();
            if (t == null) t = Regex.Split(err.Trim(), "\r?\n").Last();
            if (string.IsNullOrEmpty(t)) t = "error desconocido";
            return Regex.Replace(t, @"^ERROR:\s*", "").Trim();
        }

        static string ParseEntries(string text, bool lists)
        {
            List<string> items = new List<string>();
            Regex idRx = lists ? new Regex(@"^[\w-]{12,}$") : IdRx;
            foreach (string ln in Regex.Split(text ?? "", "\r?\n"))
            {
                string[] p = ln.Split(new[] { "|~|" }, StringSplitOptions.None);
                if (p.Length < 4 || !idRx.IsMatch(p[0])) continue;
                string ch = p[2] == "NA" ? "" : p[2];
                string title = string.Join("|~|", p.Skip(3).ToArray());
                if (lists)
                {
                    items.Add(Json.Obj("id", p[0], "ch", ch, "title", title));
                }
                else
                {
                    double d;
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                    items.Add(Json.Obj("id", p[0], "dur", (int)Math.Round(d), "ch", ch, "title", title));
                }
            }
            return items.Count == 0 ? null : "{\"ok\":true,\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        static FileInfo FindAudio(string id)
        {
            try
            {
                foreach (string ext in AudioExt)
                {
                    string f = Path.Combine(App.Cache, id + ext);
                    if (File.Exists(f)) return new FileInfo(f);
                }
            }
            catch { }
            return null;
        }

        static object PrepLock(string id)
        {
            lock (prepLocks)
            {
                object o;
                if (!prepLocks.TryGetValue(id, out o)) { o = new object(); prepLocks[id] = o; }
                return o;
            }
        }

        // ---------- atencion de cada peticion ----------
        static void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 30000;
                NetworkStream stream = client.GetStream();
                StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 8192);
                string line = reader.ReadLine();
                Dictionary<string, string> headers = new Dictionary<string, string>();
                while (true)
                {
                    string h = reader.ReadLine();
                    if (string.IsNullOrEmpty(h)) break;
                    int i = h.IndexOf(':');
                    if (i > 0) headers[h.Substring(0, i).Trim().ToLowerInvariant()] = h.Substring(i + 1).Trim();
                }
                if (line == null) return;
                Match m = Regex.Match(line, @"^([A-Z]+)\s+(\S+)");
                if (!m.Success) return;
                string method = m.Groups[1].Value, target = m.Groups[2].Value, body = "";
                string cl;
                if (method == "POST" && headers.TryGetValue("content-length", out cl))
                {
                    int n;
                    if (int.TryParse(cl, out n) && n > 0 && n <= 8 * 1024 * 1024)
                    {
                        char[] buf = new char[n]; int got = 0;
                        while (got < n) { int k = reader.Read(buf, got, n - got); if (k <= 0) break; got += k; }
                        body = new string(buf, 0, got);
                    }
                }
                string path; Dictionary<string, string> q;
                int qi = target.IndexOf('?');
                if (qi >= 0) { path = target.Substring(0, qi); q = GetQuery(target.Substring(qi + 1)); }
                else { path = target; q = new Dictionary<string, string>(); }

                Route(stream, method, path, q, headers, body);
            }
            catch { /* conexion cortada por el navegador u otro error: se ignora */ }
            finally { try { client.Close(); } catch { } }
        }

        static void Route(Stream s, string method, string path, Dictionary<string, string> q, Dictionary<string, string> headers, string body)
        {
            if (path == "/" || path == "/index.html" || path == "/RetroAmp.html")
            {
                SendBytes(s, 200, "text/html; charset=utf-8", File.ReadAllBytes(App.Html));
            }
            else if (path == "/api/ping")
            {
                SendJson(s, Json.Obj("ok", true));
            }
            else if (path == "/api/search" || path == "/api/searchlists")
            {
                string term = Regex.Replace(Q(q, "q"), "[\"\r\n]", " ").Trim();
                if (term.Length == 0) { SendErr(s, "Busqueda vacia"); return; }
                bool lists = path == "/api/searchlists";
                RunResult r = lists
                    ? RunYtdlp(new[] { "--flat-playlist", "--playlist-end", "12", "--print", Tpl,
                        "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(term) + "&sp=EgIQAw%253D%253D" }, 90000)
                    : RunYtdlp(new[] { "--flat-playlist", "--print", Tpl, "ytsearch20:" + term }, 90000);
                string json = ParseEntries(r.Out, lists);
                if (json != null) SendJson(s, json); else SendErr(s, ShortErr(r.Err));
            }
            else if (path == "/api/list")
            {
                string u = Q(q, "url");
                if (!Regex.IsMatch(u, "^https://[^\\s\"]+$")) { SendErr(s, "Enlace no valido"); return; }
                RunResult r = RunYtdlp(new[] { "--flat-playlist", "--print", Tpl, u }, 180000);
                string json = ParseEntries(r.Out, false);
                if (json != null) SendJson(s, json); else SendErr(s, ShortErr(r.Err));
            }
            else if (path == "/api/prep")
            {
                string id = Q(q, "id");
                if (!IdRx.IsMatch(id)) { SendErr(s, "id no valido"); return; }
                object lk = PrepLock(id);
                bool got = Monitor.TryEnter(lk, 300000);
                try
                {
                    FileInfo f = FindAudio(id);
                    string errMsg = "";
                    if (f == null)
                    {
                        string outTpl = Path.Combine(App.Cache, "%(id)s.%(ext)s");
                        RunResult r = RunYtdlp(new[] { "-f", "bestaudio[ext=m4a]/bestaudio/best", "--no-playlist", "--no-progress", "--no-mtime", "-q",
                            "-o", outTpl, "https://www.youtube.com/watch?v=" + id }, 300000);
                        f = FindAudio(id);
                        if (f == null) errMsg = ShortErr(r.Err);
                    }
                    if (f != null) SendJson(s, Json.Obj("ok", true, "url", "/audio/" + f.Name, "size", f.Length));
                    else SendErr(s, errMsg);
                }
                finally { if (got) Monitor.Exit(lk); }
            }
            else if (path.StartsWith("/audio/"))
            {
                SendAudio(s, path.Substring(7), headers);
            }
            else if (path == "/api/show")
            {
                App.Show = true;
                SendJson(s, Json.Obj("ok", true, "host", App.HostMode));
            }
            else if (path == "/api/state")
            {
                if (method == "POST")
                {
                    if (body.StartsWith("{")) { SaveText(App.State, body); SendJson(s, Json.Obj("ok", true)); }
                    else SendErr(s, "datos no validos");
                }
                else SendRaw(s, App.State);
            }
            else if (path == "/api/pls")
            {
                string[] names = new string[0];
                try
                {
                    names = new DirectoryInfo(App.Pls).GetFiles("*.json")
                        .OrderByDescending(f => f.LastWriteTime)
                        .Select(f => Json.Str(Path.GetFileNameWithoutExtension(f.Name))).ToArray();
                }
                catch { }
                SendJson(s, "{\"ok\":true,\"names\":[" + string.Join(",", names) + "]}");
            }
            else if (path.StartsWith("/api/pls/"))
            {
                string nm = SafeName(Q(q, "name"));
                if (nm.Length == 0) { SendErr(s, "Nombre no valido"); return; }
                string file = Path.Combine(App.Pls, nm + ".json");
                if (path == "/api/pls/get")
                {
                    if (File.Exists(file)) SendRaw(s, file); else SendErr(s, "No existe esa lista");
                }
                else if (path == "/api/pls/save" && method == "POST")
                {
                    if (body.StartsWith("{")) { SaveText(file, body); SendJson(s, Json.Obj("ok", true, "name", nm)); }
                    else SendErr(s, "datos no validos");
                }
                else if (path == "/api/pls/del")
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    SendJson(s, Json.Obj("ok", true));
                }
                else Send404(s);
            }
            else if (path == "/api/mp3")
            {
                string id = Q(q, "id");
                if (!IdRx.IsMatch(id)) { SendErr(s, "id no valido"); return; }
                string dir = App.MusicDir;
                string sub = SafeName(Q(q, "sub"));
                if (sub.Length > 0) dir = Path.Combine(dir, sub);
                int pending;
                lock (App.Mp3Lock)
                {
                    if (!App.Mp3Q.Any(j => j.Id == id && j.Dir == dir))
                        App.Mp3Q.Add(new Mp3Job { Id = id, Title = Q(q, "title"), Dir = dir });
                    pending = App.Mp3Q.Count;
                }
                SendJson(s, Json.Obj("ok", true, "pending", pending));
            }
            else if (path == "/api/mp3/status")
            {
                lock (App.Mp3Lock)
                {
                    SendJson(s, Json.Obj("ok", true, "pending", App.Mp3Q.Count, "current", App.Mp3Cur, "done", App.Mp3Done,
                        "failed", App.Mp3Fail, "lastError", App.Mp3Err, "dir", App.MusicDir));
                }
            }
            else if (path == "/api/mp3/open")
            {
                Directory.CreateDirectory(App.MusicDir);
                string dir = App.MusicDir;
                string sub = SafeName(Q(q, "sub"));
                if (sub.Length > 0 && Directory.Exists(Path.Combine(dir, sub))) dir = Path.Combine(dir, sub);
                try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
                SendJson(s, Json.Obj("ok", true, "dir", dir));
            }
            else if (path == "/apagar")
            {
                SendBytes(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("RetroAmp detenido"));
                App.Stop = true;
            }
            else Send404(s);
        }

        static void SendAudio(Stream s, string name, Dictionary<string, string> headers)
        {
            Match nm = Regex.Match(name, @"^[\w-]{11}\.(m4a|webm|mp4|opus|ogg|mp3)$");
            if (!nm.Success) { Send404(s); return; }
            string fp = Path.Combine(App.Cache, name);
            if (!File.Exists(fp)) { Send404(s); return; }
            string type;
            switch (nm.Groups[1].Value)
            {
                case "webm": type = "audio/webm"; break;
                case "opus": case "ogg": type = "audio/ogg"; break;
                case "mp3": type = "audio/mpeg"; break;
                default: type = "audio/mp4"; break;
            }
            using (FileStream fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length, start = 0, end = len - 1;
                int code = 200;
                string range;
                if (headers.TryGetValue("range", out range))
                {
                    Match rm = Regex.Match(range, @"bytes=(\d*)-(\d*)");
                    if (rm.Success)
                    {
                        code = 206;
                        if (rm.Groups[1].Value != "")
                        {
                            start = long.Parse(rm.Groups[1].Value);
                            if (rm.Groups[2].Value != "") end = Math.Min(long.Parse(rm.Groups[2].Value), len - 1);
                        }
                        else if (rm.Groups[2].Value != "") start = Math.Max(0, len - long.Parse(rm.Groups[2].Value));
                    }
                }
                if (start >= len || start > end)
                {
                    SendBytes(s, 416, "text/plain", new byte[0], "Content-Range: bytes */" + len + "\r\n");
                    return;
                }
                long count = end - start + 1;
                string hdr = "HTTP/1.1 " + code + (code == 206 ? " Partial Content" : " OK") + "\r\nContent-Type: " + type +
                             "\r\nContent-Length: " + count + "\r\nAccept-Ranges: bytes\r\n";
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
    }

    // ---------- Conversion a MP3 en segundo plano ----------
    public static class Mp3Worker
    {
        public static void Start()
        {
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "RetroAmp-mp3";
            t.Start();
        }

        static void SetCur(string s) { lock (App.Mp3Lock) App.Mp3Cur = s; }

        static void Loop()
        {
            string ffmpeg = Path.Combine(App.Bin, "ffmpeg.exe");
            while (!App.Stop)
            {
                Mp3Job job = null;
                lock (App.Mp3Lock)
                {
                    if (App.Mp3Q.Count > 0) { job = App.Mp3Q[0]; App.Mp3Q.RemoveAt(0); }
                }
                if (job == null) { Thread.Sleep(300); continue; }
                string label = string.IsNullOrEmpty(job.Title) ? job.Id : job.Title;
                try
                {
                    // ffmpeg solo se descarga cuando el usuario pide su primer MP3 (no al abrir el programa)
                    if (!File.Exists(ffmpeg)) InstallFfmpeg();
                    SetCur(label);
                    Directory.CreateDirectory(job.Dir);
                    List<string> a = new List<string> {
                        "--no-warnings", "--js-runtimes", "deno:" + App.Deno, "--ffmpeg-location", App.Bin,
                        "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0",
                        "--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg",
                        "--no-playlist", "--no-progress", "-q", "--no-mtime",
                        "-o", Path.Combine(job.Dir, "%(title)s.%(ext)s"), "https://www.youtube.com/watch?v=" + job.Id };
                    Server.RunResult r = Server.RunTool(App.Ytdlp, a, 900000);
                    if (r.Code == -1) throw new Exception(r.Err);
                    if (r.Code != 0)
                    {
                        string t = Regex.Split(r.Err ?? "", "\r?\n").Where(l => l.Contains("ERROR")).LastOrDefault() ?? "error desconocido";
                        throw new Exception(Regex.Replace(t, @"^ERROR:\s*", "").Trim());
                    }
                    lock (App.Mp3Lock) App.Mp3Done++;
                }
                catch (Exception ex)
                {
                    lock (App.Mp3Lock) { App.Mp3Fail++; App.Mp3Err = label + ": " + ex.Message; }
                }
                SetCur("");
            }
        }

        static void InstallFfmpeg()
        {
            SetCur("Descargando el conversor MP3 (solo la primera vez, unos 150 MB)...");
            string zip = Path.Combine(App.Bin, "ffmpeg.zip");
            string tmp = Path.Combine(App.Bin, "ffmpeg-tmp");
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "RetroAmp";
                    wc.DownloadFile("https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zip);
                }
                SetCur("Preparando el conversor MP3...");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                ZipFile.ExtractToDirectory(zip, tmp);
                foreach (string exe in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    string f = Directory.GetFiles(tmp, exe, SearchOption.AllDirectories).FirstOrDefault();
                    if (f != null) File.Copy(f, Path.Combine(App.Bin, exe), true);
                }
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            if (!File.Exists(Path.Combine(App.Bin, "ffmpeg.exe"))) throw new Exception("No pude instalar el conversor MP3 (ffmpeg)");
        }
    }
}
