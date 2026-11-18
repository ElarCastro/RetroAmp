// RetroAmp.exe - lanzador sin ventana de consola
// Uso: RetroAmp.exe            abre el reproductor (o muestra el que ya esta abierto)
//      RetroAmp.exe --inicio   igual, pero empieza minimizado (al iniciar Windows)
//      RetroAmp.exe --salir    cierra RetroAmp
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;

[assembly: AssemblyTitle("RetroAmp")]
[assembly: AssemblyProduct("RetroAmp")]
[assembly: AssemblyDescription("Reproductor retro inspirado en el Winamp clasico, para musica de YouTube")]
[assembly: AssemblyCompany("RetroAmp")]
[assembly: AssemblyCopyright("RetroAmp")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Lanzador
{
    const string Url = "http://127.0.0.1:8765/";

    static bool Call(string path, int timeoutMs)
    {
        try
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(Url + path);
            r.Timeout = timeoutMs; r.Proxy = null;
            using (WebResponse w = r.GetResponse()) { }
            return true;
        }
        catch { return false; }
    }

    static bool IsRunning()
    {
        Mutex m = null;
        try { return Mutex.TryOpenExisting("Local\\RetroAmp_App", out m); }
        catch { return true; }
        finally { if (m != null) m.Dispose(); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool inicio = false, salir = false;
        foreach (string a in args)
        {
            string x = a.Trim().ToLowerInvariant();
            if (x == "--inicio" || x == "/inicio") inicio = true;
            else if (x == "--salir" || x == "/salir") salir = true;
        }

        if (salir) { Call("apagar", 4000); return 0; }

        if (IsRunning())
        {
            // Ya esta abierto (o abriendose): solo mostrar la ventana
            for (int i = 0; i < 60; i++) { if (Call("api/show", 3000)) break; Thread.Sleep(500); }
            return 0;
        }

        string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string script = Path.Combine(root, "servidor.ps1");
        if (!File.Exists(script)) return 2;
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) ps = "powershell.exe";

        // Primera vez sin los programas auxiliares: mostrar la consola para ver la descarga
        string bin = Path.Combine(root, "bin");
        bool firstRun = !File.Exists(Path.Combine(bin, "yt-dlp.exe")) || !File.Exists(Path.Combine(bin, "deno.exe"));

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = ps;
        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass " + (firstRun ? "" : "-WindowStyle Hidden ") + "-File \"" + script + "\"" + (inicio ? " -Inicio" : "");
        psi.WorkingDirectory = root;
        psi.UseShellExecute = true;   // consola oculta, pero se puede mostrar si hay un error
        psi.WindowStyle = firstRun ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden;
        try { Process.Start(psi); } catch { return 1; }
        return 0;
    }
}
