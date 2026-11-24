// RetroAmp - descarga y verificacion de las herramientas externas (yt-dlp, Deno, ffmpeg)
using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RetroAmp
{
    public delegate void Progress(string message);

    public static class Deps
    {
        const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        const string YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
        const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
        const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        const string FfmpegSums = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";

        static void Prepare()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // TLS 1.2
        }

        static string GetText(string url)
        {
            Prepare();
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "RetroAmp";
                return wc.DownloadString(url);
            }
        }

        static void Download(string url, string file, string label, Progress p)
        {
            Prepare();
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "RetroAmp";
            rq.AllowAutoRedirect = true;
            rq.Timeout = 60000;
            rq.ReadWriteTimeout = 60000;
            string tmp = file + ".part";
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            using (Stream src = rs.GetResponseStream())
            using (FileStream dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                long total = rs.ContentLength, got = 0;
                byte[] buf = new byte[81920];
                int n, lastPct = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : -1;
                    if (pct != lastPct && p != null)
                    {
                        lastPct = pct;
                        p(label + (pct >= 0 ? "  " + pct + "%" : "  " + (got / 1048576) + " MB"));
                    }
                }
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        static string Sha256(string file)
        {
            using (SHA256 h = SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
                return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        // Busca el hash de "name" en un archivo de sumas (formato "hash  nombre")
        static string HashFor(string sums, string name)
        {
            foreach (string ln in Regex.Split(sums, "\r?\n"))
            {
                Match m = Regex.Match(ln.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (m.Success && m.Groups[2].Value.Trim() == name) return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }

        static void Verify(string file, string expected, string what)
        {
            if (expected == null) throw new Exception("No encontre la firma (SHA-256) de " + what);
            string got = Sha256(file);
            if (got != expected)
            {
                try { File.Delete(file); } catch { }
                throw new Exception("La descarga de " + what + " no coincide con su firma SHA-256 (archivo danado o alterado).");
            }
        }

        public static void DownloadFile(string url, string file, Progress p) { Download(url, file, "Descargando", p); }
        public static void VerifyFile(string file, string expected, string what) { Verify(file, expected, what); }
        public static string SumFor(string sumsUrl, string name) { return HashFor(GetText(sumsUrl), name); }

        public static bool Missing(Paths p) { return !File.Exists(p.YtDlp) || !File.Exists(p.Deno); }

        public static void EnsureYtDlp(Paths p, Progress pr)
        {
            if (File.Exists(p.YtDlp)) return;
            Directory.CreateDirectory(p.Bin);
            string tmp = p.YtDlp + ".new";
            pr("Descargando yt-dlp...");
            Download(YtDlpUrl, tmp, "Descargando yt-dlp", pr);
            Verify(tmp, HashFor(GetText(YtDlpSums), "yt-dlp.exe"), "yt-dlp");
            File.Move(tmp, p.YtDlp);
        }

        public static void EnsureDeno(Paths p, Progress pr)
        {
            if (File.Exists(p.Deno)) return;
            Directory.CreateDirectory(p.Bin);
            string zip = Path.Combine(p.Bin, "deno.zip");
            try
            {
                pr("Descargando Deno...");
                Download(DenoUrl, zip, "Descargando Deno (unos 45 MB)", pr);
                // deno publica un .sha256sum con formato "Hash : XXXX"
                string sums = GetText(DenoUrl + ".sha256sum");
                Match m = Regex.Match(sums, "([0-9a-fA-F]{64})");
                Verify(zip, m.Success ? m.Groups[1].Value.ToLowerInvariant() : null, "Deno");
                pr("Preparando Deno...");
                ExtractOne(zip, "deno.exe", p.Deno);
            }
            finally { try { File.Delete(zip); } catch { } }
        }

        public static void EnsureFfmpeg(Paths p, Progress pr)
        {
            if (File.Exists(p.Ffmpeg)) return;
            Directory.CreateDirectory(p.Bin);
            string zip = Path.Combine(p.Bin, "ffmpeg.zip");
            try
            {
                pr("Descargando el conversor MP3 (solo la primera vez, unos 150 MB)...");
                Download(FfmpegUrl, zip, "Descargando el conversor MP3 (solo la primera vez)", pr);
                Verify(zip, HashFor(GetText(FfmpegSums), "ffmpeg-master-latest-win64-gpl.zip"), "ffmpeg");
                pr("Preparando el conversor MP3...");
                ExtractOne(zip, "ffprobe.exe", Path.Combine(p.Bin, "ffprobe.exe"));
                ExtractOne(zip, "ffmpeg.exe", p.Ffmpeg);
            }
            finally { try { File.Delete(zip); } catch { } }
            if (!File.Exists(p.Ffmpeg)) throw new Exception("No pude instalar el conversor MP3 (ffmpeg)");
        }

        // Saca del zip solo el archivo pedido (sin desempacar todo)
        static void ExtractOne(string zip, string name, string dest)
        {
            using (ZipArchive za = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry e in za.Entries)
                {
                    if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        string tmp = dest + ".new";
                        using (Stream s = e.Open())
                        using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                            s.CopyTo(fs);
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(tmp, dest);
                        return;
                    }
                }
            }
            throw new Exception("No encontre " + name + " dentro de la descarga");
        }

        // Actualiza yt-dlp en segundo plano (YouTube cambia seguido; yt-dlp verifica su propia firma)
        public static void UpdateYtDlpInBackground(Paths p)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    Thread.Sleep(5000);
                    lock (YtUpdate.Lock)
                    {
                        YtUpdate.Cleanup(p); AppUpdate.Cleanup(p);
                        Server.RunResult r = Server.Run(p.YtDlp, new string[] { "-U" }, 180000);
                        if (r.Code != 0) p.Log("Actualizar yt-dlp: " + Server.ShortErr(r.Err + "\n" + r.Out));
                        else if ((r.Out ?? "").Contains("Updated yt-dlp")) p.Log("yt-dlp se actualizo solo al iniciar");
                    }
                }
                catch (Exception ex) { p.Log("Actualizar yt-dlp: " + ex.Message); }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        // Ventana "Preparando RetroAmp" mientras se descarga lo que falta la primera vez.
        // Devuelve true si todo quedo listo.
        public static bool EnsureWithSplash(Paths p)
        {
            while (true)
            {
                Exception error = null;
                using (Form f = new Form())
                {
                    f.FormBorderStyle = FormBorderStyle.None;
                    f.StartPosition = FormStartPosition.CenterScreen;
                    f.ClientSize = new Size(420, 110);
                    f.BackColor = Color.FromArgb(25, 25, 36);
                    f.ShowInTaskbar = true;
                    f.Text = "RetroAmp";
                    f.TopMost = true;
                    Label title = new Label();
                    title.Text = "RetroAmp  ·  preparando la primera vez";
                    title.ForeColor = Color.FromArgb(216, 184, 80);
                    title.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
                    title.SetBounds(16, 14, 390, 22);
                    Label msg = new Label();
                    msg.Text = "Revisando...";
                    msg.ForeColor = Color.FromArgb(0, 230, 0);
                    msg.Font = new Font("Consolas", 9.5f);
                    msg.SetBounds(16, 44, 390, 20);
                    Label note = new Label();
                    note.Text = "Se descargan yt-dlp y Deno desde GitHub (solo una vez).";
                    note.ForeColor = Color.FromArgb(150, 150, 170);
                    note.Font = new Font("Segoe UI", 8.5f);
                    note.SetBounds(16, 76, 390, 20);
                    f.Controls.Add(title); f.Controls.Add(msg); f.Controls.Add(note);
                    Progress pr = delegate (string m)
                    {
                        try { f.BeginInvoke((MethodInvoker)delegate { msg.Text = m; }); } catch { }
                    };
                    f.Shown += delegate
                    {
                        Thread t = new Thread(delegate ()
                        {
                            try { EnsureYtDlp(p, pr); EnsureDeno(p, pr); }
                            catch (Exception ex) { error = ex; }
                            try { f.BeginInvoke((MethodInvoker)delegate { f.Close(); }); } catch { }
                        });
                        t.IsBackground = true;
                        t.Start();
                    };
                    Application.Run(f);
                }
                if (error == null && !Missing(p)) return true;
                string why = error != null ? error.Message : "La descarga no termino.";
                p.Log("Descargas iniciales: " + why);
                DialogResult dr = MessageBox.Show(
                    "No pude descargar las herramientas que RetroAmp necesita para YouTube.\n\n" + why +
                    "\n\nRevisa tu conexion a internet (o si tu red bloquea github.com) y pulsa Reintentar.\n" +
                    "Tambien puedes copiar yt-dlp.exe y deno.exe a mano en la carpeta:\n" + p.Bin,
                    "RetroAmp", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (dr != DialogResult.Retry) return false;
            }
        }
    }
}
