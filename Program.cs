// RetroAmp - punto de entrada del .exe portable
// Reune lo que antes hacian Iniciar-RetroAmp.bat + servidor.ps1 + host.cs (compilado al vuelo).
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("RetroAmp")]
[assembly: AssemblyProduct("RetroAmp")]
[assembly: AssemblyDescription("RetroAmp - reproductor retro de musica y video")]
[assembly: AssemblyCompany("Elar Castro")]
[assembly: AssemblyCopyright("Elar Castro - github.com/ElarCastro/RetroAmp")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2")]

namespace RetroAmp
{
    static class Program
    {
        const string ShowEventName = "Local\\RetroAmp_Mostrar";
        const string StopEventName = "Local\\RetroAmp_Salir";
        static string webViewDir;
        // --inicio (Iniciar con Windows). Va aquí y no en Host: Main no debe tocar nada de WebView2
        // antes de registrar OnResolve, que es quien encuentra sus DLL en datos\bin\webview2.
        internal static bool StartMinimized;

        [STAThread]
        static int Main(string[] args)
        {
            string root = Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location));
            webViewDir = Path.Combine(root, "datos\\bin\\webview2");
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;

            // 1.2: actualización del programa (la hace este mismo .exe, desde la carpeta de la versión nueva)
            if (args.Length > 0 && args[0].ToLowerInvariant().TrimStart('/', '-') == "aplicar-actualizacion")
                return AppUpdate.Apply(args);

            bool quit = false;
            foreach (string a in args)
            {
                string x = a.ToLowerInvariant().TrimStart('/', '-');
                if (x == "salir" || x == "detener" || x == "stop") quit = true;
                if (x == "inicio") StartMinimized = true;   // "Iniciar con Windows": abre minimizado
            }
            if (quit)
            {
                try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting(StopEventName)) ev.Set(); } catch { }
                return 0;
            }

            bool created;
            using (Mutex single = new Mutex(true, "Local\\RetroAmp_Instancia", out created))
            {
                if (!created)
                {
                    // Ya esta abierto: solo mostrar la ventana existente
                    try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    return 0;
                }
                try
                {
                    Ordenar(root);
                    NombreViejo(root);
                    return Start(root);
                }
                catch (Exception ex)
                {
                    try { Directory.CreateDirectory(Path.Combine(root, "datos")); File.AppendAllText(Path.Combine(root, "datos\\registro.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] ERROR: " + ex + Environment.NewLine); } catch { }
                    MessageBox.Show("RetroAmp no pudo arrancar:\n\n" + ex.Message, "RetroAmp", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }

        // 1.2: ya no se borra la marca "descargado de internet" (Zone.Identifier) de los archivos: no hace falta,
        // porque las DLL de WebView2 se cargan con UnsafeLoadFrom y las herramientas se abren sin el Explorador,
        // y borrarla es justo lo que los antivirus vigilan en los programas maliciosos.

        static Assembly OnResolve(object sender, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { try { if (a.GetName().Name == n) return a; } catch { } }
            string p = Path.Combine(webViewDir, n + ".dll");
            // UnsafeLoadFrom: carga aunque Windows marque la DLL como "descargada de internet" (sin necesitar RetroAmp.exe.config)
            return File.Exists(p) ? Assembly.UnsafeLoadFrom(p) : null;
        }

        // Desde 2.8 todo lo del programa vive en la subcarpeta "datos"; junto al .exe solo quedan LEEME.txt y Media.
        // Si la carpeta viene de una versión anterior, se acomoda sola la primera vez (no se pierde nada).
        static readonly string[] ProgramItems = { "bin", "fonts", "RetroAmp.html", "Detener-RetroAmp.bat", "WinampYT.html", "Detener-WinampYT.bat", "WinampYT.exe.config",
                                                  "RetroAmp.exe.config", "RetroAmp.ico" };   // RetroAmp 1.0/1.1 tenía todo junto al .exe
        static readonly string[] DataItems = { "cache", "perfil", "playlists", "estado.json", "biblioteca.json", "ventana.txt", "ventanas.txt",
                                               "spotify.txt", "spotify-youtube.txt", "registro.txt" };
        static void Ordenar(string root)
        {
            string dat = Path.Combine(root, "datos");
            bool any = false;
            foreach (string n in ProgramItems) if (File.Exists(Path.Combine(root, n)) || Directory.Exists(Path.Combine(root, n))) any = true;
            foreach (string n in DataItems) if (File.Exists(Path.Combine(root, n)) || Directory.Exists(Path.Combine(root, n))) any = true;
            if (!any) return;
            try { Directory.CreateDirectory(dat); } catch { return; }
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            foreach (string n in ProgramItems) MoveItem(root, dat, n, true, log);
            foreach (string n in DataItems) MoveItem(root, dat, n, false, log);
            if (log.Length > 0)
                try { File.AppendAllText(Path.Combine(dat, "registro.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Carpeta ordenada (todo pasa a \\datos): " + log + Environment.NewLine); } catch { }
        }

        // 1.2: el programa se llama RetroAmp. Se quitan los archivos del programa con los nombres anteriores
        // (WinampYT.exe, WINAMP_Plus.exe y lo suyo en datos); los datos del usuario no cambian de lugar.
        static void NombreViejo(string root)
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            string me = "";
            try { me = Path.GetFileName(Assembly.GetEntryAssembly().Location); } catch { }
            string[] olds = { "WinampYT.exe", "WinampYT.exe.config", "datos\\WinampYT.html", "datos\\Detener-WinampYT.bat",
                              "WINAMP_Plus.exe", "WINAMP_Plus.exe.config", "datos\\WINAMP_Plus.html", "datos\\Detener-WINAMP_Plus.bat",
                              "datos\\RetroAmp.exe.config", "datos\\RetroAmp.ico" };
            if (!File.Exists(Path.Combine(root, "datos\\RetroAmp.html"))) return;
            foreach (string n in olds)
            {
                string f = Path.Combine(root, n);
                if (Path.GetFileName(f).Equals(me, StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) continue;
                try { File.Delete(f); log.Append(n + " "); } catch (Exception ex) { log.Append(n + " (no se pudo: " + ex.Message + ") "); }
            }
            if (log.Length > 0)
                try { File.AppendAllText(Path.Combine(root, "datos\\registro.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Ahora se llama RetroAmp; quitado lo del nombre anterior: " + log + Environment.NewLine); } catch { }
        }

        // Cambia el nombre de una carpeta a minúsculas (Windows no distingue mayúsculas: se pasa por un nombre temporal)
        static void ToLowerDir(string parent, string name, System.Text.StringBuilder log)
        {
            string[] ds; try { ds = Directory.GetDirectories(parent); } catch { return; }
            foreach (string d in ds)
            {
                string n = Path.GetFileName(d);
                if (!n.Equals(name, StringComparison.OrdinalIgnoreCase) || n == name) continue;
                string tmp = Path.Combine(parent, name + ".renombrando");
                try { Directory.Move(d, tmp); Directory.Move(tmp, Path.Combine(parent, name)); log.Append(n + "->" + name + " "); }
                catch (Exception ex) { log.Append(n + " (no se pudo: " + ex.Message + ") "); try { if (Directory.Exists(tmp)) Directory.Move(tmp, d); } catch { } }
                return;
            }
        }

        static void Minusculas(string mediaBase, Paths p)
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            ToLowerDir(mediaBase, "media", log);
            string media = Path.Combine(mediaBase, "media");
            if (Directory.Exists(media)) { ToLowerDir(media, "musica", log); ToLowerDir(media, "video", log); }
            if (log.Length > 0) p.Log("Carpetas en minúsculas: " + log);
        }

        // 2.16: las listas guardadas pasan de datos\playlists a media\playlists (no se pierde ninguna)
        static void MoverListas(string old, Paths p)
        {
            if (!Directory.Exists(old)) return;
            int n = 0, kept = 0;
            try
            {
                Directory.CreateDirectory(p.Pls);
                foreach (string f in Directory.GetFiles(old))
                {
                    string dst = Path.Combine(p.Pls, Path.GetFileName(f));
                    try { if (File.Exists(dst)) kept++; else { File.Move(f, dst); n++; } } catch { kept++; }
                }
                if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old);
            }
            catch (Exception ex) { p.Log("No pude mover las listas a media\\playlists: " + ex.Message); return; }
            if (n > 0 || kept > 0) p.Log("Listas guardadas movidas a media\\playlists: " + n + (kept > 0 ? " (" + kept + " ya existían y se dejaron en datos\\playlists)" : ""));
        }

        // 2.16: estado, biblioteca, Spotify y ventanas pasan de datos a media\perfil (si ya hay uno en media\perfil, se respeta)
        static readonly string[] ConfItems = { "estado.json", "biblioteca.json", "spotify.txt", "spotify-youtube.txt", "ventana.txt", "ventanas.txt" };
        static void MoverAjustes(Paths p)
        {
            try { Directory.CreateDirectory(p.Conf); } catch (Exception ex) { p.Log("No pude crear media\\perfil: " + ex.Message); p.Conf = p.Data; return; }
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            foreach (string n in ConfItems)
            {
                string src = Path.Combine(p.Data, n), dst = Path.Combine(p.Conf, n);
                if (!File.Exists(src)) continue;
                try { if (File.Exists(dst)) log.Append(n + " (ya estaba; se dejó el de datos) "); else { File.Move(src, dst); log.Append(n + " "); } }
                catch (Exception ex) { log.Append(n + " (no se pudo: " + ex.Message + ") "); }
            }
            if (log.Length > 0) p.Log("Pasados a media\\perfil: " + log);
        }

        // 2.17: media más ordenada. Grabaciones juntas en media\grabaciones; lo que es configuración, dentro de media\perfil.
        // Nada se borra: si en el lugar nuevo ya existe un archivo con el mismo nombre, el viejo se queda donde estaba.
        static void Ordenar217(string mediaBase, Paths p)
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            string media = Path.Combine(mediaBase, "media");
            string[,] moves = {
                { Path.Combine(media, "tv"), Path.Combine(media, "grabaciones", "tv") },
                { Path.Combine(media, "radio"), Path.Combine(media, "grabaciones", "radio") },
                { Path.Combine(media, "Consola DJ"), Path.Combine(media, "grabaciones", "Consola DJ") },
                { Path.Combine(media, "iptv"), Path.Combine(p.Conf, "iptv") },
                { Path.Combine(p.Data, "perfil-web"), Path.Combine(p.Conf, "web-envivo") } };
            for (int i = 0; i < moves.GetLength(0); i++) MoveTree(moves[i, 0], moves[i, 1], log);
            foreach (string n in new string[] { "spotify.txt", "spotify-youtube.txt" })
                MoveOne(Path.Combine(p.Conf, n), Path.Combine(p.Conf, "spotify", n), log);
            // skins que agregó el usuario (los que no vienen con el programa) pasan a media\perfil\skins
            string sk = Path.Combine(p.Data, "skins");
            if (Directory.Exists(sk))
                foreach (string f in Directory.GetFiles(sk))
                    if (Array.IndexOf(Server.BundledSkins, Path.GetFileName(f)) < 0 && (f.EndsWith(".wsz", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        MoveOne(f, Path.Combine(p.Conf, "skins", Path.GetFileName(f)), log);
            if (log.Length > 0) p.Log("Carpetas reacomodadas (2.17): " + log);
        }
        static void MoveOne(string src, string dst, System.Text.StringBuilder log)
        {
            if (!File.Exists(src)) return;
            try
            {
                if (File.Exists(dst)) { log.Append(Path.GetFileName(src) + " (ya estaba en el lugar nuevo; se dejó el viejo) "); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Move(src, dst); log.Append(Path.GetFileName(src) + " ");
            }
            catch (Exception ex) { log.Append(Path.GetFileName(src) + " (no se pudo: " + ex.Message + ") "); }
        }
        static void MoveTree(string src, string dst, System.Text.StringBuilder log)
        {
            if (!Directory.Exists(src)) return;
            try
            {
                if (!Directory.Exists(dst)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)); Directory.Move(src, dst); log.Append(src + " -> " + dst + " "); return; }
                foreach (string f in Directory.GetFiles(src)) MoveOne(f, Path.Combine(dst, Path.GetFileName(f)), log);
                foreach (string d in Directory.GetDirectories(src)) MoveTree(d, Path.Combine(dst, Path.GetFileName(d)), log);
                if (Directory.GetFileSystemEntries(src).Length == 0) Directory.Delete(src);
            }
            catch (Exception ex) { log.Append(src + " (no se pudo: " + ex.Message + ") "); }
        }

        static void MoveItem(string root, string dat, string n, bool program, System.Text.StringBuilder log)
        {
            string src = Path.Combine(root, n), dst = Path.Combine(dat, n);
            bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src)) return;
            try
            {
                bool there = isDir ? Directory.Exists(dst) : File.Exists(dst);
                if (n.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase)) { File.Delete(src); log.Append(n + " (ya no se usa) "); return; }
                if (!there)
                {
                    if (isDir) Directory.Move(src, dst); else File.Move(src, dst);
                    log.Append(n + " ");
                }
                else if (isDir) { MergeDir(src, dst, program); log.Append(n + "* "); }
                else if (program) { File.Delete(src); log.Append(n + "(viejo) "); }
                else
                {
                    // Los dos existen: se queda el de datos y el de afuera se guarda aparte por si acaso
                    string old = Path.Combine(dat, "anterior"); Directory.CreateDirectory(old);
                    string t = Path.Combine(old, n); if (File.Exists(t)) File.Delete(t);
                    File.Move(src, t); log.Append(n + "->anterior ");
                }
            }
            catch (Exception ex) { log.Append(n + "(no se pudo: " + ex.Message + ") "); }
        }

        // Junta una carpeta vieja con la nueva: lo que falta se mueve; lo repetido del programa se descarta
        static void MergeDir(string src, string dst, bool program)
        {
            foreach (string f in Directory.GetFiles(src))
            {
                string t = Path.Combine(dst, Path.GetFileName(f));
                if (!File.Exists(t)) File.Move(f, t);
                else if (program || File.GetLastWriteTimeUtc(f) <= File.GetLastWriteTimeUtc(t)) File.Delete(f);
                else { File.Delete(t); File.Move(f, t); }
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                string t = Path.Combine(dst, Path.GetFileName(d));
                if (!Directory.Exists(t)) Directory.Move(d, t); else MergeDir(d, t, program);
            }
            try { Directory.Delete(src, false); } catch { }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string t = Path.Combine(dir, ".prueba-escritura");
                File.WriteAllText(t, "ok");
                File.Delete(t);
                return true;
            }
            catch { return false; }
        }

        static Paths MakePaths(string root)
        {
            Paths p = new Paths();
            p.Root = root;
            string app = Path.Combine(root, "datos");
            p.Data = app;
            if (!CanWrite(app))
            {
                // Carpeta protegida (p. ej. Archivos de programa o una USB bloqueada): los datos van al perfil del usuario
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                p.Data = Path.Combine(lad, "RetroAmp");
                foreach (string old in new string[] { "WINAMP_Plus", "WinampYT" })   // datos de antes del cambio de nombre
                    if (!Directory.Exists(p.Data) && Directory.Exists(Path.Combine(lad, old))) p.Data = Path.Combine(lad, old);
                Directory.CreateDirectory(p.Data);
            }
            p.WebView = Path.Combine(app, "bin\\webview2");
            p.Html = Path.Combine(app, "RetroAmp.html");
            p.Fonts = Path.Combine(app, "fonts");
            // Herramientas: se usan las de la carpeta del programa si existen; si no, se descargan a la carpeta de datos
            string rootBin = Path.Combine(app, "bin");
            p.Bin = (p.Data == app || File.Exists(Path.Combine(rootBin, "yt-dlp.exe"))) ? rootBin : Path.Combine(p.Data, "bin");
            p.Cache = Path.Combine(p.Data, "cache");
            p.Profile = Path.Combine(p.Data, "perfil");
            p.LogFile = Path.Combine(p.Data, "registro.txt");
            string music = "";
            try { music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); } catch { }
            if (string.IsNullOrEmpty(music)) music = p.Data;
            p.MusicUser = Path.Combine(music, "RetroAmp");          // donde RetroAmp 1.0/1.1 guardaba los MP3 (solo para importar)
            if (!Directory.Exists(p.MusicUser)) p.MusicUser = Path.Combine(music, "WinampYT");   // y antes WinampYT
            string mediaBase = p.Data == app ? root : p.Data;        // Media queda a la vista, junto al .exe
            Minusculas(mediaBase, p);                                 // 2.9: Media\Musica\Video pasan a media\musica\video
            p.MusicApp = Path.Combine(mediaBase, "media\\musica");    // descargas MP3, dentro del programa
            p.VideoApp = Path.Combine(mediaBase, "media\\video");     // descargas de video, dentro del programa
            p.Pls = Path.Combine(mediaBase, "media\\playlists");      // listas guardadas: con media se respalda todo
            try { Directory.CreateDirectory(p.MusicApp); Directory.CreateDirectory(p.VideoApp); } catch { }
            MoverListas(Path.Combine(p.Data, "playlists"), p);
            p.Conf = Path.Combine(mediaBase, "media\\perfil");      // lista actual, ajustes, biblioteca y Spotify: también se respaldan con media
            MoverAjustes(p);
            Ordenar217(mediaBase, p);
            p.State = Path.Combine(p.Conf, "estado.json");
            foreach (string d in new string[] { p.Cache, p.Pls, p.Profile }) Directory.CreateDirectory(d);
            if (p.Data != app) p.Log("La carpeta del programa no permite escribir; los datos se guardan en " + p.Data);
            return p;
        }

        static void CleanCache(Paths p)
        {
            try
            {
                foreach (FileInfo f in new DirectoryInfo(p.Cache).GetFiles())
                {
                    string n = f.Name;
                    if (n.EndsWith(".part") || n.EndsWith(".ytdl") || n.EndsWith(".temp") || n.Contains(".part-") || f.LastWriteTime < DateTime.Now.AddDays(-30))
                        try { f.Delete(); } catch { }
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static int Start(string root)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDPIAware(); } catch { } }
            try { Application.EnableVisualStyles(); } catch { }
            // 1.2: el paquete "_actualizacion.zip" es solo para el actualizador interno (no trae datos\bin).
            // Si alguien lo abre directo, se avisa en vez de abrir el reproductor a medias en el navegador.
            if (!Directory.Exists(Path.Combine(root, "datos\\bin\\webview2")) && Environment.GetEnvironmentVariable("RETROAMP_NAVEGADOR") == null)
            {
                MessageBox.Show("A esta copia de RetroAmp le falta la carpeta datos\\bin (la ventana del programa y sus herramientas).\n\n" +
                    "Seguramente abriste el paquete «actualizacion», que solo usa el actualizador interno.\n\n" +
                    "Instala RetroAmp con «RetroAmp-1.2-Instalador.zip» o usa «RetroAmp-1.2-portable.zip», de github.com/ElarCastro/RetroAmp/releases",
                    "RetroAmp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            Paths p = MakePaths(root);

            if (Deps.Missing(p) && !Deps.EnsureWithSplash(p)) return 1;
            Deps.UpdateYtDlpInBackground(p);
            CleanCache(p);

            Hashtable ctx = Hashtable.Synchronized(new Hashtable());
            ctx["Stop"] = false; ctx["Show"] = false; ctx["HostMode"] = false;
            Server srv = new Server(p, ctx);
            srv.Start();

            using (EventWaitHandle showEv = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            using (EventWaitHandle stopEv = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName))
            {
                Thread watch = new Thread(delegate ()
                {
                    WaitHandle[] hs = new WaitHandle[] { showEv, stopEv };
                    while (true)
                    {
                        int i = WaitHandle.WaitAny(hs);
                        if (i == 0) ctx["Show"] = true;
                        else { ctx["Stop"] = true; return; }
                    }
                });
                watch.IsBackground = true;
                watch.Start();

                string hostErr = null;
                if (Environment.GetEnvironmentVariable("RETROAMP_NAVEGADOR") != null) hostErr = "Modo navegador forzado por RETROAMP_NAVEGADOR";
                else if (!File.Exists(Path.Combine(p.WebView, "Microsoft.Web.WebView2.Core.dll")) || !File.Exists(Path.Combine(p.WebView, "Microsoft.Web.WebView2.WinForms.dll")))
                    hostErr = "Faltan los archivos de WebView2 en datos\\bin\\webview2";
                else
                {
                    string ver = WebView2Version(p);
                    if (ver == null)
                    {
                        hostErr = "WebView2 Runtime no esta instalado en este equipo";
                        if (MessageBox.Show("Este equipo no tiene Microsoft Edge WebView2, que RetroAmp usa para su ventana propia.\n\n" +
                            "Voy a abrir el reproductor en Chrome o Edge (sin modo fantasma ni botones en la barra de tareas).\n\n" +
                            "¿Quieres abrir la pagina de descarga de WebView2 (gratis, de Microsoft)?",
                            "RetroAmp", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            try { Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); } catch { }
                    }
                }

                if (hostErr == null)
                {
                    ctx["HostMode"] = true;
                    try { hostErr = RunHost(srv.StartUrl, p, ctx); if (hostErr == "") hostErr = null; }
                    catch (Exception ex) { hostErr = ex.ToString(); }
                    ctx["HostMode"] = false;
                }

                if (hostErr != null && !(bool)ctx["Stop"])
                {
                    p.Log(hostErr);
                    BrowserMode(srv, ctx);
                }
                srv.Stop();
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string WebView2Version(Paths p)
        {
            try { Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(LoaderDir(p)); } catch { }
            try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex) { p.Log("WebView2: " + ex.Message); return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string RunHost(string url, Paths p, Hashtable ctx)
        {
            Host.ConfDir = p.Conf;
            return Host.Run(url, p.Profile, p.Data, ctx, LoaderDir(p));
        }

        static string LoaderDir(Paths p)
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "win-arm64" : (Environment.Is64BitProcess ? "win-x64" : "win-x86");
            return Path.Combine(p.WebView, "runtimes\\" + arch + "\\native");
        }

        // ---------------- Respaldo: abrir en Chrome/Edge ----------------
        static void OpenBrowser(string url)
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";
            string lad = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            string[] candidates = {
                Path.Combine(pf, "Google\\Chrome\\Application\\chrome.exe"),
                Path.Combine(pf86, "Google\\Chrome\\Application\\chrome.exe"),
                Path.Combine(lad, "Google\\Chrome\\Application\\chrome.exe"),
                Path.Combine(pf86, "Microsoft\\Edge\\Application\\msedge.exe"),
                Path.Combine(pf, "Microsoft\\Edge\\Application\\msedge.exe")
            };
            string exe = null;
            foreach (string c in candidates) if (File.Exists(c)) { exe = c; break; }
            try
            {
                if (exe != null) Process.Start(exe, "--app=" + url + " --window-size=500,760 --autoplay-policy=no-user-gesture-required");
                else Process.Start(url);
            }
            catch { MessageBox.Show("Abre en tu navegador: " + url, "RetroAmp"); }
        }

        static void BrowserMode(Server srv, Hashtable ctx)
        {
            OpenBrowser(srv.StartUrl);
            NotifyIcon tray = new NotifyIcon();
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add(new ToolStripMenuItem("Abrir reproductor", null, delegate { OpenBrowser(srv.StartUrl); }));
            cm.Items.Add(new ToolStripMenuItem("Salir", null, delegate { ctx["Stop"] = true; }));
            tray.ContextMenuStrip = cm;
            tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            tray.Text = "RetroAmp (en el navegador)";
            tray.Visible = true;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenBrowser(srv.StartUrl); };
            tray.ShowBalloonTip(6000, "RetroAmp", "Se abrio en el navegador. Para cerrar RetroAmp usa este icono > Salir.", ToolTipIcon.Info);
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 300;
            t.Tick += delegate
            {
                if (ctx["Show"] is bool && (bool)ctx["Show"]) { ctx["Show"] = false; OpenBrowser(srv.StartUrl); }
                if (ctx["Stop"] is bool && (bool)ctx["Stop"]) Application.ExitThread();
            };
            t.Start();
            Application.Run();
            t.Stop();
            tray.Visible = false;
            tray.Dispose();
        }
    }
}
