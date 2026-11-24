// RetroAmp - buscar y aplicar actualizaciones de yt-dlp desde el programa.
// Solo se reemplaza bin\yt-dlp.exe, después de comprobar su firma SHA-256 publicada por yt-dlp.
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace RetroAmp
{
    public static class YtUpdate
    {
        public static readonly object Lock = new object();     // evita dos actualizaciones a la vez
        static readonly string Base = Environment.GetEnvironmentVariable("WYT_YTDLP_BASE") ?? "https://github.com/yt-dlp/yt-dlp/releases";

        public static volatile string State = "idle";          // idle | checking | downloading | verifying | done | error
        public static volatile string Msg = "";
        public static volatile int Pct;
        public static volatile string Local = "", Latest = "";
        public static DateTime LastCheck = DateTime.MinValue;

        public static string LocalVersion(Paths p)
        {
            if (!File.Exists(p.YtDlp)) return "";
            Server.RunResult r = Server.Run(p.YtDlp, new string[] { "--version" }, 30000);
            string v = (r.Out ?? "").Trim();
            Match m = Regex.Match(v, @"\d{4}\.\d{1,2}\.\d{1,2}(\.\d+)?");
            return m.Success ? m.Value : v;
        }

        // La última versión publicada: GitHub redirige /latest a /tag/<versión>
        public static string LatestVersion()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(Base + "/latest");
            rq.Method = "HEAD"; rq.AllowAutoRedirect = false; rq.UserAgent = "RetroAmp"; rq.Timeout = 20000;
            HttpWebResponse rs;
            try { rs = (HttpWebResponse)rq.GetResponse(); }
            catch (WebException we) { rs = we.Response as HttpWebResponse; if (rs == null) throw new Exception("sin conexión con GitHub (" + we.Status + ")"); }
            using (rs)
            {
                string loc = rs.Headers["Location"] ?? "";
                Match m = Regex.Match(loc, @"/tag/([^/?#]+)");
                if (!m.Success) throw new Exception("GitHub no indicó la última versión (" + (int)rs.StatusCode + ")");
                return Uri.UnescapeDataString(m.Groups[1].Value);
            }
        }

        // -1: a es más vieja que b
        public static int Compare(string a, string b)
        {
            string[] x = Regex.Split(a ?? "", @"[^\d]+"), y = Regex.Split(b ?? "", @"[^\d]+");
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long p = 0, q = 0;
                if (i < x.Length) long.TryParse(x[i], out p);
                if (i < y.Length) long.TryParse(y[i], out q);
                if (p != q) return p < q ? -1 : 1;
            }
            return 0;
        }

        public static void Check(Paths p)
        {
            State = "checking"; Msg = "Buscando la última versión…";
            Local = LocalVersion(p);
            Latest = LatestVersion();
            LastCheck = DateTime.Now;
            State = "idle"; Msg = "";
        }

        // Descarga la versión indicada, comprueba su SHA-256 y reemplaza bin\yt-dlp.exe
        public static void Apply(Paths p)
        {
            lock (Lock)
            {
                string tmp = Path.Combine(p.Bin, "yt-dlp-nuevo.exe"), old = Path.Combine(p.Bin, "yt-dlp-viejo.exe");
                try
                {
                    State = "checking"; Msg = "Buscando la última versión…"; Pct = 0;
                    Local = LocalVersion(p);
                    Latest = LatestVersion();
                    LastCheck = DateTime.Now;
                    if (Local != "" && Compare(Local, Latest) >= 0) { State = "done"; Msg = "Ya tienes la última versión (" + Local + ")."; return; }
                    string dl = Base + "/download/" + Uri.EscapeDataString(Latest) + "/";
                    State = "downloading"; Msg = "Descargando yt-dlp " + Latest + "…";
                    Deps.DownloadFile(dl + "yt-dlp.exe", tmp, delegate (string m) { Match mm = Regex.Match(m, @"(\d+)%"); if (mm.Success) Pct = int.Parse(mm.Groups[1].Value); });
                    State = "verifying"; Msg = "Comprobando la firma SHA-256…";
                    Deps.VerifyFile(tmp, Deps.SumFor(dl + "SHA2-256SUMS", "yt-dlp.exe"), "yt-dlp");
                    if (Path.DirectorySeparatorChar == '/') { try { System.Diagnostics.Process.Start("chmod", "+x \"" + tmp + "\"").WaitForExit(); } catch { } } // solo pruebas fuera de Windows
                    // Prueba que la nueva copia arranque antes de cambiarla
                    Server.RunResult t = Server.Run(tmp, new string[] { "--version" }, 60000);
                    if (t.Code != 0) throw new Exception("la versión descargada no arrancó");
                    // Windows deja renombrar un .exe aunque esté en uso: el viejo se aparta y se borra después
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    if (File.Exists(p.YtDlp)) File.Move(p.YtDlp, old);
                    try { File.Move(tmp, p.YtDlp); }
                    catch { if (!File.Exists(p.YtDlp) && File.Exists(old)) File.Move(old, p.YtDlp); throw; }
                    try { File.Delete(old); } catch { }
                    string was = Local;
                    Local = LocalVersion(p);
                    State = "done"; Pct = 100;
                    Msg = "yt-dlp actualizado" + (was != "" ? " de " + was : "") + " a " + Local + ".";
                    p.Log("yt-dlp actualizado desde el programa: " + was + " -> " + Local);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    State = "error"; Msg = "No se pudo actualizar: " + ex.Message;
                    p.Log("Actualizar yt-dlp: " + ex.Message);
                }
            }
        }

        public static void Cleanup(Paths p)
        {
            foreach (string n in new string[] { "yt-dlp-viejo.exe", "yt-dlp-nuevo.exe", "yt-dlp-nuevo.exe.part" })
                try { string f = Path.Combine(p.Bin, n); if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    public partial class Server
    {
        bool UpdRoute(Stream s, Req r)
        {
            string path = r.Path;
            if (!path.StartsWith("/api/ytdlp/")) return false;
            if (path == "/api/ytdlp/check")
            {
                try
                {
                    if (Monitor.TryEnter(YtUpdate.Lock)) { try { YtUpdate.Check(P); } finally { Monitor.Exit(YtUpdate.Lock); } }
                    else { SendJson(s, UpdJson()); return true; }
                }
                catch (Exception ex) { YtUpdate.State = "idle"; SendJson(s, "{\"ok\":false,\"local\":" + J(YtUpdate.Local) + ",\"error\":" + J("No pude revisar: " + ex.Message) + "}"); return true; }
                SendJson(s, UpdJson());
            }
            else if (path == "/api/ytdlp/update")
            {
                string st = YtUpdate.State;
                if (st != "checking" && st != "downloading" && st != "verifying")
                {
                    YtUpdate.State = "checking"; YtUpdate.Msg = "Buscando la última versión…";
                    Thread t = new Thread(delegate () { YtUpdate.Apply(P); });
                    t.IsBackground = true; t.Start();
                }
                SendJson(s, UpdJson());
            }
            else if (path == "/api/ytdlp/status") SendJson(s, UpdJson());
            else Send404(s);
            return true;
        }

        static string UpdJson()
        {
            string l = YtUpdate.Local, n = YtUpdate.Latest;
            bool newer = l != "" && n != "" && YtUpdate.Compare(l, n) < 0;
            return "{\"ok\":true,\"local\":" + J(l) + ",\"latest\":" + J(n) + ",\"update\":" + J(newer) + ",\"state\":" + J(YtUpdate.State) +
                ",\"pct\":" + YtUpdate.Pct + ",\"msg\":" + J(YtUpdate.Msg) + "}";
        }
    }
}
