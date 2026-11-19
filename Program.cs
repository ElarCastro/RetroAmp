// RetroAmp - punto de entrada.
// Un solo ejecutable compilado: sin PowerShell, sin compilar codigo al vuelo
// y sin descargar programas al iniciar.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("RetroAmp")]
[assembly: AssemblyDescription("Reproductor retro inspirado en el Winamp clasico, para musica de YouTube")]
[assembly: AssemblyCompany("RetroAmp")]
[assembly: AssemblyProduct("RetroAmp")]
[assembly: AssemblyCopyright("RetroAmp - github.com/ElarCastro")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace RetroAmp
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool inicio = args.Any(a => a.Equals("--inicio", StringComparison.OrdinalIgnoreCase) || a.Equals("/inicio", StringComparison.OrdinalIgnoreCase));
            bool salir = args.Any(a => a.Equals("--salir", StringComparison.OrdinalIgnoreCase) || a.Equals("/salir", StringComparison.OrdinalIgnoreCase));

            App.Init(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'));

            // "Cerrar RetroAmp" desde el menu Inicio o el desinstalador
            if (salir) { TryGet(App.Url + "apagar", 3000); return 0; }

            // Una sola copia: si otra se esta abriendo, esperar a que este lista y mostrarla
            bool owns;
            Mutex appMutex = new Mutex(false, @"Local\RetroAmp_App");
            try { owns = appMutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            if (!owns)
            {
                for (int i = 0; i < 120; i++) { if (TryGet(App.Url + "api/show", 3000) != null) break; Thread.Sleep(500); }
                return 0;
            }

            // Si ya hay algo escuchando en el puerto (version anterior abierta), mostrarlo
            if (PortInUse())
            {
                string r = TryGet(App.Url + "api/show", 5000);
                if (r == null || !r.Contains("\"host\":true")) OpenBrowser();
                return 0;
            }

            try
            {
                foreach (string d in new[] { App.Bin, App.Cache, App.Pls, App.Prof }) Directory.CreateDirectory(d);
            }
            catch (Exception ex) { return Fail("No pude crear las carpetas de RetroAmp. (" + ex.Message + ")"); }

            if (!File.Exists(App.Ytdlp) || !File.Exists(App.Deno))
                return Fail("Faltan componentes del reproductor (bin\\yt-dlp.exe o bin\\deno.exe).\nVuelve a ejecutar el instalador de RetroAmp.");

            CleanCache();

            try { Server.Start(); }
            catch (Exception ex) { return Fail("No pude abrir el puerto " + App.Port + ". (" + ex.Message + ")"); }
            Mp3Worker.Start();

            string hostErr = null;
            string wvDir = Path.Combine(App.Bin, "webview2");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RETROAMP_NAVEGADOR")))
                hostErr = "Modo navegador forzado por RETROAMP_NAVEGADOR";
            else if (!File.Exists(Path.Combine(wvDir, "Microsoft.Web.WebView2.Core.dll")) || !File.Exists(Path.Combine(wvDir, "Microsoft.Web.WebView2.WinForms.dll")))
                hostErr = "Faltan los archivos de WebView2 en bin\\webview2";
            else
            {
                AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
                {
                    string n = new AssemblyName(e.Name).Name;
                    string p = Path.Combine(wvDir, n + ".dll");
                    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
                };
                App.HostMode = true;
                try { hostErr = RunHost(wvDir, inicio); }
                catch (Exception ex) { hostErr = ex.ToString(); }
                App.HostMode = false;
            }

            if (!string.IsNullOrEmpty(hostErr) && !App.Stop)
            {
                // Respaldo: abrir en Chrome/Edge y dejar un icono junto al reloj para salir
                App.Log(hostErr);
                OpenBrowser();
                RunTrayOnly();
            }

            App.Stop = true;
            Server.StopServer();
            try { appMutex.ReleaseMutex(); } catch { }
            return 0;
        }

        // Separado para que los tipos de WebView2 se carguen despues de registrar AssemblyResolve
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string RunHost(string wvDir, bool inicio)
        {
            string arch = string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase)
                ? "win-arm64" : (Environment.Is64BitProcess ? "win-x64" : "win-x86");
            string loaderDir = Path.Combine(wvDir, "runtimes", arch, "native");
            return Host.Run(App.Url, App.Prof, App.Root, loaderDir, inicio);
        }

        static void RunTrayOnly()
        {
            try { Application.EnableVisualStyles(); } catch { }
            NotifyIcon tray = new NotifyIcon();
            try { tray.Icon = new System.Drawing.Icon(Path.Combine(App.Root, "RetroAmp.ico"), SystemInformation.SmallIconSize); }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            tray.Text = "RetroAmp (en el navegador)";
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("Abrir reproductor", null, delegate { OpenBrowser(); });
            cm.Items.Add("Salir", null, delegate { App.Stop = true; });
            tray.ContextMenuStrip = cm;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenBrowser(); };
            tray.Visible = true;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 300;
            t.Tick += delegate
            {
                if (App.Show) { App.Show = false; OpenBrowser(); }
                if (App.Stop) { t.Stop(); tray.Visible = false; tray.Dispose(); Application.ExitThread(); }
            };
            t.Start();
            Application.Run();
        }

        static void CleanCache()
        {
            try
            {
                DateTime limit = DateTime.Now.AddDays(-30);
                foreach (FileInfo f in new DirectoryInfo(App.Cache).GetFiles())
                {
                    string n = f.Name;
                    if (n.EndsWith(".part") || n.EndsWith(".ytdl") || n.EndsWith(".temp") || n.Contains(".part-") || f.LastWriteTime < limit)
                    {
                        try { f.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        static bool PortInUse()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, App.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        static string TryGet(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.Proxy = null;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        public static void OpenBrowser()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? "";
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "";
            string lad = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            string[] candidates = {
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(lad, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")
            };
            string exe = candidates.FirstOrDefault(File.Exists);
            try
            {
                if (exe != null) Process.Start(exe, "--app=" + App.Url + " --window-size=500,760 --autoplay-policy=no-user-gesture-required");
                else Process.Start(App.Url);
            }
            catch { }
        }

        static int Fail(string m)
        {
            App.Log("ERROR: " + m);
            try { MessageBox.Show(m, "RetroAmp", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            return 1;
        }
    }
}
