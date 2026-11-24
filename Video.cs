// RetroAmp - videos y películas de la PC.
// Si WebView2 no puede abrir el archivo tal cual (MKV, AVI, VOB, HEVC, AC3...), ffmpeg lo pasa a MP4.
// La película empieza a verse a los pocos segundos ("en vivo", MP4 fragmentado) mientras se prepara;
// al terminar queda una copia normal en datos\cache y ya se puede adelantar/retroceder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RetroAmp
{
    class VideoJob
    {
        public volatile int Pct;
        public volatile bool Done, EncDone, Live, Cancelled;
        public volatile string Error;
        public string Part = "";
        public double Dur;
        public Process Proc;
    }

    class MediaInfo
    {
        public double Dur;
        public string VCodec = ""; public int VIndex = -1, Height; public bool V10;
        public List<string[]> Audio = new List<string[]>();   // { codec, idioma, "default"? }
        public int AIndex = -1;                                // pista de audio elegida
        public string ACodec { get { return AIndex >= 0 ? Audio[AIndex][0] : ""; } }
    }

    public partial class Server
    {
        readonly Dictionary<string, bool> directOk = new Dictionary<string, bool>();
        const long CacheMax = 20L * 1024 * 1024 * 1024;   // datos\cache guarda hasta ~20 GB de videos convertidos

        static readonly string[] VCopy = { "h264", "vp9", "av1" };          // caben en MP4 y WebView2 los reproduce
        static readonly string[] VDirect = { "h264", "vp8", "vp9", "av1", "theora" };
        static readonly string[] ACopy = { "aac", "mp3", "opus" };
        static readonly string[] ADirect = { "aac", "mp3", "opus", "vorbis", "flac" };

        static string KeyOf(FileInfo fi)
        {
            using (MD5 md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        MediaInfo Probe(string src)
        {
            RunResult info = Run(P.Ffmpeg, new string[] { "-hide_banner", "-i", src }, 60000);
            string txt = info.Err ?? "";
            MediaInfo mi = new MediaInfo();
            Match dm = Regex.Match(txt, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            if (dm.Success) mi.Dur = int.Parse(dm.Groups[1].Value) * 3600 + int.Parse(dm.Groups[2].Value) * 60 + double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
            int vi = 0;
            foreach (Match m in Regex.Matches(txt, @"Stream #\d+:\d+(?:\[[^\]]*\])?(?:\((\w+)\))?(?:\[[^\]]*\])?: (Video|Audio): (\w+)([^\r\n]*)"))
            {
                string lang = m.Groups[1].Value.ToLowerInvariant(), codec = m.Groups[3].Value.ToLowerInvariant(), rest = m.Groups[4].Value;
                if (m.Groups[2].Value == "Video")
                {
                    bool pic = rest.Contains("attached pic") || codec == "mjpeg" && rest.Contains("(attached") || codec == "png" || codec == "bmp";
                    if (!pic && mi.VIndex < 0)
                    {
                        mi.VIndex = vi; mi.VCodec = codec;
                        Match hm = Regex.Match(rest, @", (\d{2,5})x(\d{2,5})");
                        if (hm.Success) mi.Height = int.Parse(hm.Groups[2].Value);
                        mi.V10 = Regex.IsMatch(rest, @"p1[02](le|be)|10le|12le");
                    }
                    vi++;
                }
                else mi.Audio.Add(new string[] { codec, lang, rest.Contains("(default)") ? "1" : "" });
            }
            // Pista de audio: primero en español, si no la marcada por defecto, si no la primera
            for (int i = 0; i < mi.Audio.Count && mi.AIndex < 0; i++) if (Regex.IsMatch(mi.Audio[i][1], "^(spa|es|esp)$")) mi.AIndex = i;
            for (int i = 0; i < mi.Audio.Count && mi.AIndex < 0; i++) if (mi.Audio[i][2] == "1") mi.AIndex = i;
            if (mi.AIndex < 0 && mi.Audio.Count > 0) mi.AIndex = 0;
            return mi;
        }

        // ¿Un MP4/WEBM se puede abrir tal cual? (los MP4 de películas a veces traen HEVC o sonido AC3/DTS)
        bool DirectPlayable(FileInfo fi)
        {
            if (!Local.IsVideo(fi.FullName) || !File.Exists(P.Ffmpeg)) return true;
            string key = KeyOf(fi);
            lock (directOk) { bool v; if (directOk.TryGetValue(key, out v)) return v; }
            bool ok = true;
            try
            {
                MediaInfo mi = Probe(fi.FullName);
                if (mi.VIndex >= 0 && (Array.IndexOf(VDirect, mi.VCodec) < 0 || mi.V10)) ok = false;
                if (mi.AIndex >= 0 && Array.IndexOf(ADirect, mi.ACodec) < 0) ok = false;
                if (mi.AIndex > 0) ok = false;                 // el navegador tocaría la primera pista, no la elegida
            }
            catch { }
            lock (directOk) directOk[key] = ok;
            return ok;
        }

        // Devuelve la URL para reproducir, o null si todavía se está preparando (pending).
        // live: URL para ir viendo mientras se prepara (puede ser null los primeros segundos).
        string PrepLocalVideo(FileInfo fi, out bool pending, out int pct, out string live, out double dur)
        {
            pending = false; pct = 0; live = null; dur = 0;
            bool conv = Local.NeedsConvert(fi.FullName) || !DirectPlayable(fi);
            if (!conv) return "/local?p=" + Uri.EscapeDataString(fi.FullName);
            string key = KeyOf(fi);
            string name = "v-" + key + ".mp4";
            string outFile = Path.Combine(P.Cache, name);
            if (File.Exists(outFile)) { try { File.SetLastAccessTimeUtc(outFile, DateTime.UtcNow); } catch { } return "/video/" + name; }
            VideoJob job;
            lock (videoJobs)
            {
                if (videoJobs.TryGetValue(key, out job) && job.Done && job.Error != null)
                {
                    videoJobs.Remove(key);   // el siguiente intento vuelve a probar
                    if (!job.Cancelled) throw new Exception(job.Error);
                    job = null;
                }
                if (job == null || (job.Done && job.Error == null && !File.Exists(outFile)))
                {
                    // Solo una película a la vez: se detiene la que se estaba preparando y ya no se ve
                    foreach (VideoJob o in videoJobs.Values)
                        if (!o.Done && o.Live) { o.Cancelled = true; try { if (o.Proc != null) o.Proc.Kill(); } catch { } }
                    job = new VideoJob();
                    videoJobs[key] = job;
                    VideoJob j = job;
                    Thread t = new Thread(delegate () { ConvertVideo(fi.FullName, outFile, j); });
                    t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
                }
            }
            pending = true; pct = job.Pct; dur = job.Dur;
            if (job.Live && !job.EncDone)
            {
                long len = 0; try { len = new FileInfo(job.Part).Length; } catch { }
                if (len > 256 * 1024) live = "/videolive/" + key;
            }
            return null;
        }

        void ConvertVideo(string src, string dst, VideoJob job)
        {
            string tmp = dst + ".part.mp4", fin = dst + ".fin.mp4";
            try
            {
                if (!File.Exists(P.Ffmpeg)) Deps.EnsureFfmpeg(P, delegate (string m) { });
                MediaInfo mi = Probe(src);
                job.Dur = mi.Dur;
                bool hasVideo = mi.VIndex >= 0, hasAudio = mi.AIndex >= 0;
                if (!hasVideo && !hasAudio) throw new Exception("el archivo no tiene video ni sonido que se pueda leer");
                string ext = Path.GetExtension(src).ToLowerInvariant();
                List<string> a = new List<string> { "-hide_banner", "-nostats", "-y" };
                if (Regex.IsMatch(ext, @"^\.(avi|divx|xvid|vob|mpg|mpeg|mpe|m1v|m2v|mpv|mp2v|mod|tod)$")) a.AddRange(new string[] { "-fflags", "+genpts" });
                a.AddRange(new string[] { "-i", src });
                if (hasVideo) a.AddRange(new string[] { "-map", "0:v:" + mi.VIndex });
                if (hasAudio) a.AddRange(new string[] { "-map", "0:a:" + mi.AIndex });
                bool test = Environment.GetEnvironmentVariable("WYT_TEST_VP9") == "1";   // solo pruebas (Chromium sin H.264)
                if (!hasVideo) a.Add("-vn");
                else if (Array.IndexOf(VCopy, mi.VCodec) >= 0 && !mi.V10 && !test) a.AddRange(new string[] { "-c:v", "copy" });
                else
                {
                    // Recodificar: rápido para ir viendo la película casi en tiempo real; 4K se baja a 1080p
                    // (sin comillas ni comas dentro de las expresiones, para que el filtro llegue igual a ffmpeg)
                    string vf = "yadif=deint=interlaced," + (mi.Height > 1080 ? "scale=-2:1080" : "scale=trunc(iw/2)*2:trunc(ih/2)*2") + ",format=yuv420p";
                    if (test) a.AddRange(new string[] { "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "1M", "-vf", vf, "-g", "48" });
                    else a.AddRange(new string[] { "-c:v", "libx264", "-preset", mi.Height > 576 || mi.Height == 0 ? "superfast" : "veryfast", "-crf", "23", "-vf", vf, "-g", "48" });
                }
                if (hasAudio)
                {
                    if (Array.IndexOf(ACopy, mi.ACodec) >= 0) a.AddRange(new string[] { "-c:a", "copy" });
                    else a.AddRange(new string[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
                }
                // Con video: MP4 fragmentado, para poder verlo mientras se escribe
                if (hasVideo) { job.Part = tmp; job.Live = true; a.AddRange(new string[] { "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "2000000" }); }
                else a.AddRange(new string[] { "-movflags", "+faststart" });
                a.AddRange(new string[] { "-sn", "-dn", "-max_muxing_queue_size", "4096", "-avoid_negative_ts", "make_zero", "-progress", "pipe:1", tmp });

                RunFfmpeg(a, job, mi.Dur, "convertir el video");
                job.EncDone = true;
                if (job.Live)
                {
                    // Copia final normal (sin recodificar): permite adelantar y abre al instante la próxima vez
                    List<string> b = new List<string> { "-hide_banner", "-nostats", "-y", "-i", tmp, "-map", "0", "-c", "copy", "-movflags", "+faststart", fin };
                    RunFfmpeg(b, null, 0, "terminar el video");
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(fin, dst);
                    try { File.Delete(tmp); } catch { }
                }
                else
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                }
                job.Pct = 100;
                P.Log("Video preparado: " + src);
                PruneCache(dst);
            }
            catch (Exception ex)
            {
                job.Error = job.Cancelled ? "cancelado" : ex.Message;
                if (!job.Cancelled) P.Log("Video: " + src + ": " + ex.Message);
                job.EncDone = true;
                try { File.Delete(tmp); } catch { }
                try { File.Delete(fin); } catch { }
            }
            job.Done = true;
        }

        void RunFfmpeg(List<string> a, VideoJob job, double dur, string what)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = P.Ffmpeg;
            List<string> q = new List<string>();
            foreach (string x in a) q.Add(QuoteArg(x));
            psi.Arguments = string.Join(" ", q.ToArray());
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            StringBuilder errBuf = new StringBuilder();
            using (Process p = Process.Start(psi))
            {
                if (job != null) job.Proc = p;
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.ErrorDataReceived += delegate (object o, DataReceivedEventArgs e) { if (e.Data != null) lock (errBuf) { errBuf.AppendLine(e.Data); if (errBuf.Length > 20000) errBuf.Remove(0, 10000); } };
                p.BeginErrorReadLine();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    Match m = Regex.Match(line, @"^out_time_(?:us|ms)=(\d+)");
                    if (job != null && m.Success && dur > 0) job.Pct = (int)Math.Min(99, long.Parse(m.Groups[1].Value) / 1000000.0 / dur * 100);
                    if (stopping || (job != null && job.Cancelled)) { try { p.Kill(); } catch { } }
                }
                p.WaitForExit();
                if (job != null) job.Proc = null;
                if (job != null && job.Cancelled) throw new Exception("cancelado");
                if (p.ExitCode != 0) throw new Exception("ffmpeg no pudo " + what + " (" + ShortErr(errBuf.ToString()) + ")");
            }
        }

        // Envía el MP4 que se está escribiendo, siguiéndolo hasta que ffmpeg termina
        void SendLive(Stream s, string key)
        {
            VideoJob job = null;
            if (Regex.IsMatch(key, "^[0-9a-f]{16}$")) lock (videoJobs) videoJobs.TryGetValue(key, out job);
            if (job == null || !job.Live || job.Part == "" || !File.Exists(job.Part)) { Send404(s); return; }
            FileStream fs;
            try { fs = new FileStream(job.Part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch { Send404(s); return; }
            using (fs)
            {
                byte[] hb = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                s.Write(hb, 0, hb.Length);
                byte[] buf = new byte[65536];
                while (!stopping)
                {
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n > 0) { s.Write(buf, 0, n); continue; }
                    if (job.EncDone) { n = fs.Read(buf, 0, buf.Length); if (n > 0) { s.Write(buf, 0, n); continue; } break; }
                    s.Flush();
                    Thread.Sleep(250);
                }
                s.Flush();
            }
        }

        // Borra los videos convertidos más viejos si datos\cache pasa de ~20 GB
        void PruneCache(string keep)
        {
            try
            {
                List<FileInfo> fs = new List<FileInfo>(new DirectoryInfo(P.Cache).GetFiles("v-*.mp4"));
                long total = 0; foreach (FileInfo f in fs) total += f.Length;
                fs.Sort(delegate (FileInfo x, FileInfo y) { return Max(x).CompareTo(Max(y)); });
                foreach (FileInfo f in fs)
                {
                    if (total <= CacheMax) break;
                    if (string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase) || f.Name.Contains(".part") || f.Name.Contains(".fin")) continue;
                    try { long l = f.Length; f.Delete(); total -= l; P.Log("Cache: se borró un video viejo convertido (" + f.Name + ")"); } catch { }
                }
            }
            catch { }
        }
        static DateTime Max(FileInfo f) { return f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc; }
    }
}
