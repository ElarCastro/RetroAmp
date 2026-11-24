// RetroAmp - buscar la versión más reciente del programa en GitHub (ElarCastro/RetroAmp) y actualizarlo con un clic.
// 1.2: el paquete se comprueba con su SHA-256 (archivo SHA256SUMS.txt del Release) y el cambio lo hace el propio
// RetroAmp.exe nuevo (sin scripts .cmd ocultos).
// Solo se cambian los archivos del programa (el .exe, la página, fuentes, skins incluidos, LEEME y el manual):
// nunca se toca media\ (tus listas, ajustes y descargas) ni datos\bin (yt-dlp, deno, ffmpeg, WebView2).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace RetroAmp
{
    public static class AppUpdate
    {
        public const string Repo = "ElarCastro/RetroAmp";
        static readonly string Api = Environment.GetEnvironmentVariable("WYT_APP_API") ?? "https://api.github.com/repos/" + Repo + "/releases/latest";
        public static readonly object Lock = new object();
        public static volatile string State = "idle";   // idle | checking | downloading | extracting | ready | error
        public static volatile string Msg = "";
        public static volatile int Pct;
        public static string Latest = "", Notes = "", Page = "", AssetUrl = "", AssetName = "", SumsUrl = "";
        public static long AssetSize;
        static string stagedDir = "", newExe = "";

        public static string Local
        {
            get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : ""); }
        }

        public static bool Newer { get { return Latest != "" && YtUpdate.Compare(Local, Latest) < 0; } }

        static string Get(string url)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "RetroAmp/" + Local; rq.Accept = "application/vnd.github+json"; rq.Timeout = 20000; rq.ReadWriteTimeout = 20000;
            try
            {
                using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
                using (StreamReader sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
            catch (WebException we)
            {
                HttpWebResponse rs = we.Response as HttpWebResponse;
                if (rs == null) throw new Exception("sin conexión con GitHub (" + we.Status + ")");
                int c = (int)rs.StatusCode; rs.Close();
                if (c == 404) throw new Exception("todavía no hay versiones publicadas en GitHub");
                if (c == 403 || c == 429) throw new Exception("GitHub pidió esperar un rato antes de volver a preguntar");
                throw new Exception("GitHub respondió " + c);
            }
        }

        public static void Check()
        {
            State = "checking"; Msg = "Buscando la versión más reciente…";
            Dictionary<string, object> r = new JavaScriptSerializer().DeserializeObject(Get(Api)) as Dictionary<string, object>;
            if (r == null) throw new Exception("GitHub no respondió lo esperado");
            string tag = Convert.ToString(r.ContainsKey("tag_name") ? r["tag_name"] : "");
            Match m = Regex.Match(tag, @"\d+(\.\d+)+");
            if (!m.Success) throw new Exception("la versión publicada no tiene número (" + tag + ")");
            Latest = m.Value;
            Notes = Convert.ToString(r.ContainsKey("body") ? r["body"] : "") ?? "";
            if (Notes.Length > 6000) Notes = Notes.Substring(0, 6000) + "…";
            Page = Convert.ToString(r.ContainsKey("html_url") ? r["html_url"] : "") ?? "";
            AssetUrl = ""; AssetName = ""; AssetSize = 0; SumsUrl = "";
            object[] assets = r.ContainsKey("assets") ? r["assets"] as object[] : null;
            // Se prefiere el paquete chico "_actualizacion.zip" (solo el programa); si no está, el portable completo
            int best = 0;
            if (assets != null)
                foreach (object o in assets)
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>; if (a == null) continue;
                    string n = Convert.ToString(a["name"]);
                    string url = Convert.ToString(a["browser_download_url"]);
                    if (n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) { SumsUrl = url; continue; }
                    int rank = Regex.IsMatch(n, @"actualizacion\.zip$", RegexOptions.IgnoreCase) ? 2
                             : Regex.IsMatch(n, @"portable(-completo)?\.zip$", RegexOptions.IgnoreCase) ? 1 : 0;
                    if (rank <= best) continue;
                    best = rank; AssetName = n; AssetUrl = url; AssetSize = 0;
                    try { AssetSize = Convert.ToInt64(a["size"]); } catch { }
                }
            State = "idle"; Msg = "";
        }

        // Descarga el zip, lo abre y deja listo lo que se va a cambiar. El cambio se hace al cerrar el programa.
        public static void Prepare(Paths p)
        {
            lock (Lock)
            {
                string work = Path.Combine(p.Cache, "actualizar");
                try
                {
                    Pct = 0;
                    Check();
                    if (!Newer) { State = "error"; Msg = "Ya tienes la versión más reciente (" + Local + ")."; return; }
                    if (AssetUrl == "") throw new Exception("la versión " + Latest + " no trae el archivo _portable.zip del programa");
                    if (SumsUrl == "") throw new Exception("la versión " + Latest + " no trae SHA256SUMS.txt para comprobar la descarga");
                    if (!FromGitHub(AssetUrl) || !FromGitHub(SumsUrl))
                        throw new Exception("la descarga no viene de GitHub");
                    string expected = Deps.SumFor(SumsUrl, AssetName);
                    try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
                    Directory.CreateDirectory(work);
                    string zip = Path.Combine(work, "paquete.zip");
                    State = "downloading"; Msg = "Descargando la versión " + Latest + "…";
                    Deps.DownloadFile(AssetUrl, zip, delegate (string s) { Match mm = Regex.Match(s, @"(\d+)%"); if (mm.Success) Pct = int.Parse(mm.Groups[1].Value); });
                    if (AssetSize > 0 && new FileInfo(zip).Length != AssetSize) throw new Exception("la descarga quedó incompleta");
                    Msg = "Comprobando la firma SHA-256…";
                    Deps.VerifyFile(zip, expected, AssetName);
                    State = "extracting"; Msg = "Abriendo el paquete…"; Pct = 100;
                    string all = Path.Combine(work, "zip");
                    ZipFile.ExtractToDirectory(zip, all);
                    string root = FindRoot(all);
                    if (root == null) throw new Exception("el paquete no trae el programa (falta el .exe o la carpeta datos)");
                    // Lo que se cambia: el programa y sus archivos, nunca media ni datos\bin
                    string stage = Path.Combine(work, "listo"); Directory.CreateDirectory(stage);
                    string exe = null; int n = 0;
                    foreach (string f in Directory.GetFiles(root))
                    {
                        string nm = Path.GetFileName(f);
                        if (nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = nm;
                        File.Copy(f, Path.Combine(stage, nm)); n++;
                    }
                    string dsrc = Path.Combine(root, "datos"), ddst = Path.Combine(stage, "datos");
                    Directory.CreateDirectory(ddst);
                    foreach (string f in Directory.GetFiles(dsrc)) { File.Copy(f, Path.Combine(ddst, Path.GetFileName(f))); n++; }
                    foreach (string d in new string[] { "fonts", "skins", "lib" })   // lib: hls.js para Internet TV
                        if (Directory.Exists(Path.Combine(dsrc, d))) n += CopyDir(Path.Combine(dsrc, d), Path.Combine(ddst, d));
                    if (exe == null) throw new Exception("el paquete no trae el .exe");
                    try { File.Delete(zip); Directory.Delete(all, true); } catch { }
                    stagedDir = stage; newExe = exe;
                    State = "ready"; Pct = 100;
                    Msg = "La versión " + Latest + " está lista (" + n + " archivos). Pulsa REINICIAR Y ACTUALIZAR: el programa se cierra, se cambia y se abre solo.";
                    p.Log("Actualización del programa preparada: " + Local + " -> " + Latest + " (" + AssetName + ")");
                }
                catch (Exception ex)
                {
                    State = "error"; Msg = "No se pudo actualizar: " + ex.Message;
                    p.Log("Actualizar el programa: " + ex.Message);
                }
            }
        }

        static bool FromGitHub(string url)
        {
            if (Environment.GetEnvironmentVariable("WYT_APP_API") != null) return true;   // solo para pruebas
            return Regex.IsMatch(url, @"^https://(github\.com/|objects\.githubusercontent\.com/)", RegexOptions.IgnoreCase);
        }

        static string FindRoot(string dir)
        {
            for (int depth = 0; depth < 3 && dir != null; depth++)
            {
                if (Directory.Exists(Path.Combine(dir, "datos")) && Directory.GetFiles(dir, "*.exe").Length > 0) return dir;
                string[] sub = Directory.GetDirectories(dir);
                dir = sub.Length == 1 ? sub[0] : null;
            }
            return null;
        }

        static int CopyDir(string src, string dst)
        {
            int n = 0; Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); n++; }
            foreach (string d in Directory.GetDirectories(src)) n += CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            return n;
        }

        // Abre el RetroAmp.exe NUEVO (el de la carpeta preparada) con /aplicar-actualizacion: ese espera a que este
        // se cierre, copia los archivos nuevos encima y vuelve a abrir el programa. Sin .cmd ni ventanas ocultas.
        public static ProcessStartInfo Launcher(Paths p)
        {
            if (State != "ready" || stagedDir == "" || !Directory.Exists(stagedDir)) throw new Exception("primero hay que descargar la versión nueva");
            string exe = Path.Combine(stagedDir, newExe);
            if (!File.Exists(exe)) throw new Exception("falta " + newExe + " en la versión descargada");
            string root = p.Root.TrimEnd('\\', '/');
            ProcessStartInfo si = new ProcessStartInfo(exe,
                "/aplicar-actualizacion " + Server.QuoteArg(stagedDir) + " " + Server.QuoteArg(root) + " " + Process.GetCurrentProcess().Id + " " + Server.QuoteArg(newExe));
            si.UseShellExecute = false;
            si.WorkingDirectory = stagedDir;
            return si;
        }

        // Lo que hace el .exe nuevo cuando se abre con /aplicar-actualizacion <preparado> <programa> <pid> <exe>
        public static int Apply(string[] args)
        {
            string stage = args.Length > 1 ? args[1] : "", root = args.Length > 2 ? args[2] : "", exe = args.Length > 4 ? args[4] : "RetroAmp.exe";
            int pid; int.TryParse(args.Length > 3 ? args[3] : "0", out pid);
            string log = Path.Combine(root, "datos", "registro.txt");
            Action<string> L = delegate (string m) { try { File.AppendAllText(log, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + m + Environment.NewLine); } catch { } };
            if (!Directory.Exists(stage) || !Directory.Exists(root) || !File.Exists(Path.Combine(stage, exe)))
            { L("Actualizar: faltan las carpetas (" + stage + " -> " + root + ")"); return 1; }
            try { if (pid > 0) using (Process old = Process.GetProcessById(pid)) old.WaitForExit(60000); } catch { /* ya se cerró */ }
            string ver = "";
            try { ver = FileVersionInfo.GetVersionInfo(Path.Combine(stage, exe)).FileVersion; } catch { }
            int n = 0; Exception last = null;
            for (int intento = 0; intento < 10; intento++)
            {
                try { n = CopyDir(stage, root); last = null; break; }
                catch (Exception ex) { last = ex; Thread.Sleep(1000); }   // el .exe viejo puede tardar un poco en soltarse
            }
            if (last != null) L("Actualizar: no se pudo copiar la versión nueva (" + last.Message + ")");
            else L("Actualizado a la versión " + ver + " (" + n + " archivos)");
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(Path.Combine(root, exe));
                si.UseShellExecute = false; si.WorkingDirectory = root;
                Process.Start(si);
            }
            catch (Exception ex) { L("Actualizar: no se pudo abrir el programa (" + ex.Message + ")"); }
            return last == null ? 0 : 1;
        }

        public static void Cleanup(Paths p)
        {
            try { string w = Path.Combine(p.Cache, "actualizar"); if (Directory.Exists(w)) Directory.Delete(w, true); } catch { }
        }
    }

    public partial class Server
    {
        bool AppUpdRoute(Stream s, Req r)
        {
            string path = r.Path;
            if (!path.StartsWith("/api/app/")) return false;
            if (path == "/api/app/check")
            {
                if (Monitor.TryEnter(AppUpdate.Lock))
                {
                    try { AppUpdate.Check(); }
                    catch (Exception ex) { AppUpdate.State = "idle"; SendJson(s, "{\"ok\":false,\"local\":" + J(AppUpdate.Local) + ",\"error\":" + J("No pude revisar: " + ex.Message) + "}"); return true; }
                    finally { Monitor.Exit(AppUpdate.Lock); }
                }
                SendJson(s, AppJson());
            }
            else if (path == "/api/app/update")
            {
                string st = AppUpdate.State;
                if (st != "checking" && st != "downloading" && st != "extracting" && st != "ready")
                {
                    AppUpdate.State = "checking"; AppUpdate.Msg = "Buscando la versión más reciente…";
                    Thread t = new Thread(delegate () { AppUpdate.Prepare(P); });
                    t.IsBackground = true; t.Start();
                }
                SendJson(s, AppJson());
            }
            else if (path == "/api/app/status") SendJson(s, AppJson());
            else if (path == "/api/app/restart")
            {
                ProcessStartInfo si;
                try { si = AppUpdate.Launcher(P); }
                catch (Exception ex) { SendJson(s, "{\"ok\":false,\"error\":" + J(ex.Message) + "}"); return true; }
                P.Log("Reiniciando para actualizar a " + AppUpdate.Latest);
                if (Path.DirectorySeparatorChar == '/') { SendJson(s, "{\"ok\":true,\"script\":" + J(si.FileName + " " + si.Arguments) + ",\"test\":true}"); return true; }   // pruebas fuera de Windows
                Process.Start(si);
                SendJson(s, "{\"ok\":true}");
                Thread q = new Thread(delegate () { Thread.Sleep(700); ctx["Stop"] = true; });
                q.IsBackground = true; q.Start();
            }
            else Send404(s);
            return true;
        }

        static string AppJson()
        {
            return "{\"ok\":true,\"local\":" + J(AppUpdate.Local) + ",\"latest\":" + J(AppUpdate.Latest) + ",\"update\":" + J(AppUpdate.Newer) +
                ",\"asset\":" + J(AppUpdate.AssetName) + ",\"size\":" + AppUpdate.AssetSize + ",\"notes\":" + J(AppUpdate.Notes) + ",\"page\":" + J(AppUpdate.Page) +
                ",\"state\":" + J(AppUpdate.State) + ",\"pct\":" + AppUpdate.Pct + ",\"msg\":" + J(AppUpdate.Msg) + "}";
        }
    }
}
