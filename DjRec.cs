// RetroAmp - grabar la Consola DJ: la página manda el sonido de la mezcla (PCM de 16 bits, estéreo)
// y ffmpeg lo guarda en MP3 en media\Consola DJ. Al pausar, la página deja de mandar sonido y el archivo
// sigue abierto: al continuar se graba en el mismo MP3. El nombre es la fecha y hora en que se pulsó GRABAR.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RetroAmp
{
    public partial class Server
    {
        class DjRec
        {
            public string Id, File; public int Rate; public long Bytes; public DateTime Start;
            public Process Proc; public Stream In; public volatile bool Done; public string Error = "";
            public StringBuilder Log = new StringBuilder();
        }
        static DjRec djRec;
        static readonly object djLock = new object();

        DjRec DjStart(int rate)
        {
            if (!File.Exists(P.Ffmpeg)) throw new Exception("falta ffmpeg en datos\\bin");
            if (rate < 8000 || rate > 192000) rate = 48000;
            string dir = MediaDir("Consola DJ");
            DateTime now = DateTime.Now;
            string file = Path.Combine(dir, now.ToString("yyyy-MM-dd-HH-mm-ss") + ".mp3");
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(dir, now.ToString("yyyy-MM-dd-HH-mm-ss") + " (" + i + ").mp3");
            string[] a = { "-hide_banner", "-nostats", "-loglevel", "warning", "-y", "-f", "s16le", "-ar", rate.ToString(), "-ac", "2", "-i", "pipe:0",
                "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3", "-metadata", "title=Mezcla " + now.ToString("yyyy-MM-dd HH:mm"), "-metadata", "artist=RetroAmp · Consola DJ", "-f", "mp3", file };
            List<string> q = new List<string>(); foreach (string x in a) q.Add(QuoteArg(x));
            ProcessStartInfo psi = new ProcessStartInfo(P.Ffmpeg, string.Join(" ", q.ToArray()));
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            DjRec r = new DjRec(); r.File = file; r.Rate = rate; r.Start = now; r.Id = now.Ticks.ToString();
            r.Proc = Process.Start(psi);
            r.In = r.Proc.StandardInput.BaseStream;
            r.Proc.ErrorDataReceived += delegate (object o, DataReceivedEventArgs e) { if (e.Data != null) lock (r.Log) { if (r.Log.Length < 4000) r.Log.AppendLine(e.Data); } };
            r.Proc.OutputDataReceived += delegate { };
            r.Proc.BeginErrorReadLine(); r.Proc.BeginOutputReadLine();
            r.Proc.EnableRaisingEvents = true;
            r.Proc.Exited += delegate
            {
                r.Done = true;
                long size = 0; try { size = new FileInfo(file).Length; } catch { }
                if (size < 1024) { r.Error = "no se grabó sonido"; try { File.Delete(file); } catch { } }
                string log; lock (r.Log) log = r.Log.ToString().Trim();
                P.Log("Consola DJ: grabación " + (r.Error == "" ? "guardada" : "vacía") + ": " + file + " (" + (size / 1024) + " KB, " + (r.Bytes / (rate * 4L)) + " s de sonido)" + (log != "" ? "\r\n" + (log.Length > 1500 ? log.Substring(log.Length - 1500) : log) : ""));
            };
            P.Log("Consola DJ: grabando en " + file + " (" + rate + " Hz)");
            return r;
        }

        static void DjFinish(DjRec r)
        {
            if (r == null) return;
            try { lock (r) r.In.Close(); } catch { }
            ThreadPool.QueueUserWorkItem(delegate { try { if (!r.Proc.WaitForExit(15000)) r.Proc.Kill(); } catch { } });
        }

        public static void DjRecStopAll()
        {
            DjRec r; lock (djLock) { r = djRec; djRec = null; }
            if (r == null) return;
            DjFinish(r);
            try { r.Proc.WaitForExit(8000); } catch { }
        }

        string DjJson(DjRec r, string extra)
        {
            long size = 0; try { size = new FileInfo(r.File).Length; } catch { }
            return "{\"ok\":true,\"id\":" + J(r.Id) + ",\"file\":" + J(Path.GetFileName(r.File)) + ",\"sec\":" + (r.Bytes / (r.Rate * 4L)) + ",\"kb\":" + (size / 1024) + ",\"done\":" + J(r.Done) + extra + "}";
        }

        bool DjRecRoute(Stream s, Req q)
        {
            if (!q.Path.StartsWith("/api/djrec/")) return false;
            DjRec r; lock (djLock) r = djRec;
            if (q.Path == "/api/djrec/start")
            {
                try
                {
                    int rate; int.TryParse(q.Q1("sr"), out rate);
                    lock (djLock) { if (djRec != null) DjFinish(djRec); djRec = r = DjStart(rate); }
                    SendJson(s, DjJson(r, ""));
                }
                catch (Exception ex) { P.Log("Consola DJ: no se pudo grabar: " + ex.Message); SendJson(s, Err("No se pudo grabar: " + ex.Message)); }
            }
            else if (q.Path == "/api/djrec/data")
            {
                if (r == null || r.Id != q.Q1("id") || r.Done) { SendJson(s, Err("La grabación ya terminó.")); return true; }
                try { lock (r) { r.In.Write(q.Raw, 0, q.Raw.Length & ~3); r.In.Flush(); r.Bytes += q.Raw.Length & ~3; } SendJson(s, DjJson(r, "")); }
                catch (Exception ex) { P.Log("Consola DJ: se cortó la grabación: " + ex.Message); SendJson(s, Err("Se cortó la grabación: " + ex.Message)); }
            }
            else if (q.Path == "/api/djrec/stop")
            {
                if (r != null && r.Id == q.Q1("id")) { lock (djLock) djRec = null; DjFinish(r); SendJson(s, DjJson(r, ",\"dir\":" + J(Path.GetDirectoryName(r.File)))); }
                else SendJson(s, "{\"ok\":true}");
            }
            else if (q.Path == "/api/djrec/open")
            {
                string dir = MediaDir("Consola DJ");
                try { if (Path.DirectorySeparatorChar == '\\') Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
                SendJson(s, "{\"ok\":true,\"dir\":" + J(dir) + "}");
            }
            else return false;
            return true;
        }
    }
}
