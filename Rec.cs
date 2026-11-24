// RetroAmp - grabar lo que está en vivo: canales de TV en media\tv y radios en media\radio (con ffmpeg).
// La TV se guarda tal cual llega (sin volver a codificar) en MP4 fragmentado: si se corta, lo grabado sirve igual.
// La radio se guarda en MP3.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RetroAmp
{
    public class Recording
    {
        public string Id, Kind, Name, File, ItemId, Url;
        public DateTime Start, PartStart;
        public Process Proc;
        public volatile bool Done;
        public string State = "rec";          // rec, pausing, pause, stopping, saving, done
        public double SecDone;                // segundos de las partes ya cerradas
        public List<string> Parts = new List<string>();
        public string Error = "";
        public StringBuilder Log = new StringBuilder();
        public int Sec { get { return (int)(SecDone + (State == "rec" || State == "pausing" ? (DateTime.Now - PartStart).TotalSeconds : 0)); } }
    }

    public partial class Server
    {
        static readonly Dictionary<string, Recording> recs = new Dictionary<string, Recording>();
        static int recSeq;
        const int RecMax = 5;   // grabaciones al mismo tiempo (las que están en pausa también cuentan)

        string MediaDir(string sub) { string d = Path.Combine(Path.GetDirectoryName(P.MusicApp), "grabaciones", sub); Directory.CreateDirectory(d); return d; }   // media\grabaciones\tv, radio, Consola DJ

        // Pausar: se cierra la parte que se estaba grabando. Continuar: empieza otra parte.
        // Terminar: se unen todas las partes (sin volver a codificar) en un solo archivo.
        string RecStart(string kind, string itemId, string name)
        {
            bool tv = kind == "tv";
            string u = itemId.Substring(tv ? 3 : 2);
            if (!HttpUrl.IsMatch(u)) throw new Exception("la dirección no es válida");
            lock (recs) foreach (Recording x in recs.Values) if (!x.Done && x.ItemId == itemId) throw new Exception("ya se está grabando");
            int busy = 0; lock (recs) foreach (Recording x in recs.Values) if (!x.Done) busy++;
            if (busy >= RecMax) throw new Exception("ya hay " + RecMax + " grabaciones al mismo tiempo (es el máximo, para no saturar tu internet ni tu computadora). Termina una para grabar otra");
            if (!File.Exists(P.Ffmpeg)) throw new Exception("falta ffmpeg en datos\\bin");
            string safe = Regex.Replace(name ?? "", @"[\\/:*?""<>|\x00-\x1f]", " ").Trim().TrimEnd('.');
            if (safe.Length > 60) safe = safe.Substring(0, 60).Trim();
            if (safe == "") safe = tv ? "Canal" : "Radio";
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
            string file = Path.Combine(MediaDir(tv ? "tv" : "radio"), safe + " " + stamp + (tv ? ".mp4" : ".mp3"));
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(Path.GetDirectoryName(file), safe + " " + stamp + " (" + i + ")" + (tv ? ".mp4" : ".mp3"));
            Recording rec = new Recording();
            rec.Kind = kind; rec.Name = safe; rec.File = file; rec.ItemId = itemId; rec.Url = u; rec.Start = DateTime.Now;
            lock (recs) { rec.Id = (++recSeq).ToString(); recs[rec.Id] = rec; }
            P.Log("Grabando " + (tv ? "TV" : "radio") + ": " + u + " -> " + file);
            try { lock (rec) RecPart(rec); }
            catch { lock (recs) recs.Remove(rec.Id); throw; }
            return rec.Id;
        }

        List<string> RecArgs(Recording rec, string outFile)
        {
            bool tv = rec.Kind == "tv"; string u = rec.Url;
            string local = "http://127.0.0.1:" + Port + "/hls?k=" + Token + "&u=" + Uri.EscapeDataString(u);
            List<string> a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "warning", "-y" };
            List<string> auth = new List<string> { "-headers", "Cookie: wyt=" + Token + "\r\n" };   // las partes de la lista también piden permiso al programa
            if (tv)
            {
                if (IsYt(u))
                {   // YouTube: imagen y sonido pueden venir separados
                    string v = YtLive(u, false);
                    if (v.StartsWith("mux:"))
                        a.AddRange(new string[] { "-re", "-headers", "Cookie: wyt=" + Token + "\r\n", "-i", local + "&v=1", "-re", "-headers", "Cookie: wyt=" + Token + "\r\n", "-i", local + "&a=1", "-map", "0:v:0", "-map", "1:a:0" });
                    else { a.AddRange(auth); a.AddRange(new string[] { "-re", "-i", local }); }
                }
                else { a.AddRange(auth); a.AddRange(new string[] { "-re", "-i", local }); }
                a.AddRange(new string[] { "-c", "copy", "-bsf:a", "aac_adtstoasc", "-f", "mp4", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", outFile });
            }
            else
            {
                string src;
                if (IsYt(u) || Regex.IsMatch(u, @"\.m3u8(\?|$)", RegexOptions.IgnoreCase)) src = local + (IsYt(u) ? "&a=1" : "");
                else src = ResolveRadio(u);
                if (src.StartsWith("http://127.0.0.1:" + Port)) a.AddRange(auth);
                else a.AddRange(new string[] { "-user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RetroAmp", "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "10" });
                if (src.StartsWith("http://127.0.0.1:" + Port)) a.Add("-re");   // HLS: sin esto bajaría de golpe lo que ya pasó
                a.AddRange(new string[] { "-i", src, "-vn", "-c:a", "libmp3lame", "-b:a", "160k", "-f", "mp3", outFile });
            }
            return a;
        }

        static string PartName(Recording rec, int n)
        {
            if (n == 1) return rec.File;
            return Path.Combine(Path.GetDirectoryName(rec.File), Path.GetFileNameWithoutExtension(rec.File) + " (parte " + n + ")" + Path.GetExtension(rec.File));
        }

        // Empieza una parte nueva (al grabar y al continuar después de una pausa). Se llama con rec bloqueado.
        void RecPart(Recording rec)
        {
            string part = PartName(rec, rec.Parts.Count + 1);
            List<string> q = new List<string>(); foreach (string x in RecArgs(rec, part)) q.Add(QuoteArg(x));
            ProcessStartInfo psi = new ProcessStartInfo(P.Ffmpeg, string.Join(" ", q.ToArray()));
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            Process pr = Process.Start(psi); rec.Proc = pr;
            rec.Parts.Add(part); rec.State = "rec"; rec.PartStart = DateTime.Now;
            pr.ErrorDataReceived += delegate (object o, DataReceivedEventArgs e) { if (e.Data != null) lock (rec.Log) { if (rec.Log.Length < 4000) rec.Log.AppendLine(e.Data); } };
            pr.OutputDataReceived += delegate { };
            pr.BeginErrorReadLine(); pr.BeginOutputReadLine();
            pr.EnableRaisingEvents = true;
            pr.Exited += delegate
            {
                bool fin;
                lock (rec)
                {
                    rec.SecDone += (DateTime.Now - rec.PartStart).TotalSeconds;
                    long size = 0; try { size = new FileInfo(part).Length; } catch { }
                    if (size < 1024) { try { File.Delete(part); } catch { } rec.Parts.Remove(part); }
                    if (rec.State == "pausing") { rec.State = "pause"; fin = false; }
                    else { rec.State = "saving"; fin = true; }   // terminar, o la transmisión se cortó sola
                }
                if (fin) RecFinish(rec);
            };
        }

        // Une las partes en un solo archivo y deja la grabación terminada
        void RecFinish(Recording rec)
        {
            List<string> parts; lock (rec) { rec.State = "saving"; parts = new List<string>(rec.Parts); }
            try
            {
                if (parts.Count == 0) rec.Error = "no llegó nada que grabar";
                else if (parts.Count > 1)
                {
                    string dir = Path.GetDirectoryName(rec.File), ext = Path.GetExtension(rec.File);
                    string list = Path.Combine(dir, "." + rec.Id + "-partes.txt"), tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(rec.File) + " (uniendo)" + ext);
                    StringBuilder sb = new StringBuilder();
                    foreach (string x in parts) sb.Append("file '" + Path.GetFullPath(x).Replace("\\", "/").Replace("'", "'\\''") + "'\n");
                    File.WriteAllText(list, sb.ToString(), new UTF8Encoding(false));
                    List<string> a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy" };
                    if (rec.Kind == "tv") a.AddRange(new string[] { "-movflags", "+faststart", "-f", "mp4" }); else a.AddRange(new string[] { "-f", "mp3" });
                    a.Add(tmp);
                    List<string> q = new List<string>(); foreach (string x in a) q.Add(QuoteArg(x));
                    ProcessStartInfo psi = new ProcessStartInfo(P.Ffmpeg, string.Join(" ", q.ToArray()));
                    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true; psi.StandardErrorEncoding = Encoding.UTF8;
                    Process pr = Process.Start(psi); string err = pr.StandardError.ReadToEnd(); pr.WaitForExit(10 * 60 * 1000);
                    long sz = 0; try { sz = new FileInfo(tmp).Length; } catch { }
                    try { File.Delete(list); } catch { }
                    if (pr.ExitCode == 0 && sz > 1024)
                    {
                        foreach (string x in parts) try { File.Delete(x); } catch { }
                        File.Move(tmp, rec.File);
                    }
                    else
                    {   // si no se pudieron unir, se quedan las partes por separado
                        try { File.Delete(tmp); } catch { }
                        P.Log("Grabación: no se pudieron unir las partes de " + rec.File + ": " + err.Trim());
                        if (!File.Exists(rec.File) && File.Exists(parts[0])) try { File.Move(parts[0], rec.File); } catch { }
                    }
                }
                else if (parts[0] != rec.File) File.Move(parts[0], rec.File);
            }
            catch (Exception ex) { P.Log("Grabación: " + ex.Message); }
            long size = 0; try { size = new FileInfo(rec.File).Length; } catch { }
            string log; lock (rec.Log) log = rec.Log.ToString().Trim();
            P.Log("Grabación " + (rec.Error == "" ? "guardada" : "falló") + ": " + rec.File + " (" + (size / 1024) + " KB, " + (int)rec.SecDone + " s, " + parts.Count + " parte" + (parts.Count == 1 ? "" : "s") + ")" + (log != "" ? "\r\n" + (log.Length > 1500 ? log.Substring(log.Length - 1500) : log) : ""));
            lock (rec) { rec.State = "done"; rec.Done = true; }
        }

        static void KillPart(Process p)
        {
            try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
            ThreadPool.QueueUserWorkItem(delegate { try { if (!p.WaitForExit(8000)) p.Kill(); } catch { } });
        }

        void RecPause(Recording r, bool pause)
        {
            if (r == null) return;
            lock (r)
            {
                if (pause && r.State == "rec") { r.State = "pausing"; KillPart(r.Proc); }
                else if (!pause && r.State == "pause") RecPart(r);
            }
        }

        void RecStop(Recording r)
        {
            if (r == null) return;
            bool fin = false;
            lock (r)
            {
                if (r.State == "rec") { r.State = "stopping"; KillPart(r.Proc); }
                else if (r.State == "pausing") r.State = "stopping";
                else if (r.State == "pause") { r.State = "saving"; fin = true; }
            }
            if (fin) ThreadPool.QueueUserWorkItem(delegate { RecFinish(r); });
        }

        // Al cerrar el programa se terminan bien las grabaciones (y se unen sus partes)
        public void RecStopAll()
        {
            List<Recording> l; lock (recs) l = new List<Recording>(recs.Values);
            foreach (Recording r in l) RecStop(r);
            DateTime lim = DateTime.Now.AddSeconds(30);
            foreach (Recording r in l) while (!r.Done && DateTime.Now < lim) Thread.Sleep(100);
        }

        string RecJson()
        {
            StringBuilder sb = new StringBuilder("{\"ok\":true,\"items\":[");
            bool first = true;
            lock (recs)
            {
                List<string> old = new List<string>();
                foreach (Recording r in recs.Values)
                {
                    long size = 0; lock (r) foreach (string x in r.Parts) try { size += new FileInfo(x).Length; } catch { }
                    if (r.Done) try { size = new FileInfo(r.File).Length; } catch { }
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"id\":" + J(r.Id) + ",\"item\":" + J(r.ItemId) + ",\"kind\":" + J(r.Kind) + ",\"name\":" + J(r.Name) + ",\"file\":" + J(Path.GetFileName(r.File)) + ",\"path\":" + J(Path.GetFullPath(r.File)) +
                        ",\"sec\":" + r.Sec + ",\"kb\":" + (size / 1024) + ",\"state\":" + J(r.State) + ",\"parts\":" + r.Parts.Count + ",\"done\":" + J(r.Done) + ",\"error\":" + J(r.Error) + "}");
                    if (r.Done && (DateTime.Now - r.Start).TotalMinutes > 1) old.Add(r.Id);
                }
                if (recs.Count > 20) foreach (string k in old) recs.Remove(k);
            }
            return sb.Append("]}").ToString();
        }

        bool RecRoute(Stream s, Req r)
        {
            if (!r.Path.StartsWith("/api/rec/")) return false;
            if (r.Path == "/api/rec/start")
            {
                try { string id = RecStart(r.Q1("k") == "radio" ? "radio" : "tv", r.Q1("id"), r.Q1("n")); SendJson(s, "{\"ok\":true,\"id\":" + J(id) + "}"); }
                catch (Exception ex) { P.Log("Grabar " + r.Q1("id") + ": " + ex.Message); SendJson(s, Err("No se pudo grabar: " + ex.Message)); }
            }
            else if (r.Path == "/api/rec/stop")
            {
                Recording rec; lock (recs) recs.TryGetValue(r.Q1("id"), out rec);
                RecStop(rec);
                SendJson(s, "{\"ok\":true}");
            }
            else if (r.Path == "/api/rec/pause" || r.Path == "/api/rec/resume")
            {
                Recording rec; lock (recs) recs.TryGetValue(r.Q1("id"), out rec);
                try { RecPause(rec, r.Path == "/api/rec/pause"); SendJson(s, "{\"ok\":true}"); }
                catch (Exception ex) { P.Log("Grabación: no se pudo continuar: " + ex.Message); if (rec != null) RecStop(rec); SendJson(s, Err("No se pudo continuar la grabación: " + ex.Message)); }
            }
            else if (r.Path == "/api/rec/list") SendJson(s, RecJson());
            else if (r.Path == "/api/rec/open")
            {
                string dir = MediaDir(r.Q1("k") == "radio" ? "radio" : "tv");
                try { if (Path.DirectorySeparatorChar == '\\') Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
                SendJson(s, "{\"ok\":true,\"dir\":" + J(dir) + "}");
            }
            else Send404(s);
            return true;
        }
    }
}
