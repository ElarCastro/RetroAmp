// RetroAmp - musica de la PC: elegir archivos/carpetas, leer etiquetas (ID3) y duracion de MP3
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RetroAmp
{
    public class LocalTrack { public string Path, Title, Artist, Album; public int Dur; }

    public static class Local
    {
        public static readonly string[] Exts = { ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav", ".webm" };
        const int MaxFiles = 5000;

        public static bool IsAudio(string path)
        {
            string e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            return Array.IndexOf(Exts, e) >= 0;
        }

        // Video: lo mismo que abre VLC (los que WebView2 no abre tal cual se convierten a MP4 con ffmpeg)
        public static readonly string[] VideoExts = { ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".qt", ".hdmov", ".wmv", ".asf", ".flv", ".f4v",
            ".mpg", ".mpeg", ".mpe", ".m1v", ".m2v", ".mpv", ".mp2v", ".ts", ".m2t", ".m2ts", ".mts", ".tod", ".mod", ".vob", ".evo",
            ".3gp", ".3g2", ".ogv", ".ogx", ".ogm", ".webm", ".divx", ".xvid", ".dv", ".mjpeg", ".mjpg", ".mxf", ".gxf", ".nut", ".bik",
            ".rm", ".rmvb", ".wtv", ".dvr-ms", ".amv", ".nsv", ".h264", ".264", ".hevc", ".h265", ".265", ".y4m" };
        public static readonly string[] ConvertAudioExts = { ".wma", ".ac3", ".eac3", ".dts", ".aiff", ".aif", ".ape", ".amr", ".mka", ".mp2", ".mpa", ".mpc", ".wv", ".tta", ".ra", ".au", ".caf", ".spx" };
        static readonly string[] DirectVideo = { ".mp4", ".m4v", ".webm", ".ogv" };

        public static bool IsVideo(string path)
        {
            string e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            return Array.IndexOf(VideoExts, e) >= 0;
        }
        // Patrón para el diálogo de abrir: true = todo, false = solo música, null = solo video
        public static string Pattern(bool? all)
        {
            List<string> l = new List<string>();
            if (all != null) { foreach (string e in Exts) if (e != ".webm") l.Add("*" + e); foreach (string e in ConvertAudioExts) l.Add("*" + e); }
            if (all != false) foreach (string e in VideoExts) l.Add("*" + e);
            return string.Join(";", l.ToArray());
        }
        public static bool IsMedia(string path)
        {
            string e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            return IsAudio(path) || IsVideo(path) || Array.IndexOf(ConvertAudioExts, e) >= 0;
        }
        // Formatos que WebView2 no reproduce directamente: se convierten con ffmpeg (una vez, quedan en cache)
        public static bool NeedsConvert(string path)
        {
            string e = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            return (IsVideo(path) && Array.IndexOf(DirectVideo, e) < 0) || Array.IndexOf(ConvertAudioExts, e) >= 0;
        }

        public static string MimeOf(string path)
        {
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp4": case ".m4v": return "video/mp4";
                case ".ogv": return "video/ogg";
                case ".mp3": return "audio/mpeg";
                case ".m4a": case ".m4b": case ".aac": return "audio/mp4";
                case ".ogg": case ".oga": case ".opus": return "audio/ogg";
                case ".flac": return "audio/flac";
                case ".wav": return "audio/wav";
                case ".webm": return "video/webm";
            }
            return "application/octet-stream";
        }

        // Convierte rutas (archivos o carpetas) en la lista de canciones, en orden
        public static List<LocalTrack> Expand(IEnumerable<string> paths)
        {
            List<string> files = new List<string>();
            foreach (string raw in paths)
            {
                string p = (raw ?? "").Trim().Trim('"');
                if (p.Length == 0) continue;
                try
                {
                    if (Directory.Exists(p)) AddDir(p, files);
                    else if (File.Exists(p) && IsMedia(p)) files.Add(System.IO.Path.GetFullPath(p));
                }
                catch { }
                if (files.Count >= MaxFiles) break;
            }
            List<LocalTrack> list = new List<LocalTrack>();
            foreach (string f in files) list.Add(Read(f));
            return list;
        }

        static void AddDir(string dir, List<string> files)
        {
            string[] fs; string[] ds;
            try { fs = Directory.GetFiles(dir); ds = Directory.GetDirectories(dir); } catch { return; }
            Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(ds, StringComparer.OrdinalIgnoreCase);
            foreach (string f in fs) { if (files.Count >= MaxFiles) return; if (IsMedia(f)) files.Add(System.IO.Path.GetFullPath(f)); }
            foreach (string d in ds) { if (files.Count >= MaxFiles) return; AddDir(d, files); }
        }

        public static LocalTrack Read(string path)
        {
            LocalTrack t = new LocalTrack { Path = path, Title = "", Artist = "", Album = "" };
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long audioStart = 0;
                    if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    {
                        audioStart = ReadId3v2(fs, t);
                        if (t.Title == "" || t.Artist == "") ReadId3v1(fs, t);
                        t.Dur = Mp3Duration(fs, audioStart);
                    }
                }
            }
            catch { }
            if (t.Title == "")
            {
                // Sin etiquetas: usar el nombre del archivo ("Artista - Titulo")
                string n = System.IO.Path.GetFileNameWithoutExtension(path);
                int i = n.IndexOf(" - ");
                if (i > 0 && t.Artist == "") { t.Artist = n.Substring(0, i).Trim(); t.Title = n.Substring(i + 3).Trim(); }
                else t.Title = n;
            }
            return t;
        }

        // Solo audio (para la biblioteca)
        public static void CollectAudio(string dir, List<string> files, int max)
        {
            string[] fs; string[] ds;
            try { fs = Directory.GetFiles(dir); ds = Directory.GetDirectories(dir); } catch { return; }
            Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(ds, StringComparer.OrdinalIgnoreCase);
            foreach (string f in fs) { if (files.Count >= max) return; if (IsMedia(f)) files.Add(f); }
            foreach (string d in ds)
            {
                if (files.Count >= max) return;
                try { if ((new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue; } catch { continue; }
                CollectAudio(d, files, max);
            }
        }

        // Texto de una lista: UTF-8 (con o sin BOM), UTF-16, o Windows-1252
        public static string DecodeText(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch { return Encoding.GetEncoding(1252).GetString(b); }
        }

        // Caratula: la imagen dentro del MP3 (ID3 APIC) o cover.jpg / folder.jpg de la carpeta
        public static bool Cover(string path, out byte[] img, out string type)
        {
            img = null; type = "image/jpeg";
            try
            {
                if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        if (ReadApic(fs, out img, out type)) return true;
                string dir = System.IO.Path.GetDirectoryName(path);
                foreach (string n in new string[] { "cover", "folder", "front", "album", "albumart", "AlbumArtSmall" })
                    foreach (string ext in new string[] { ".jpg", ".jpeg", ".png" })
                    {
                        string f = System.IO.Path.Combine(dir, n + ext);
                        if (File.Exists(f)) { img = File.ReadAllBytes(f); type = ext == ".png" ? "image/png" : "image/jpeg"; return true; }
                    }
                foreach (string f in Directory.GetFiles(dir, "AlbumArt_*Large.jpg")) { img = File.ReadAllBytes(f); return true; }
            }
            catch { }
            return false;
        }

        static bool ReadApic(Stream s, out byte[] img, out string type)
        {
            img = null; type = "image/jpeg";
            byte[] h = new byte[10];
            s.Position = 0;
            if (s.Read(h, 0, 10) < 10 || h[0] != 'I' || h[1] != 'D' || h[2] != '3') return false;
            int ver = h[3];
            int size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
            if (size <= 0 || size > 64 * 1024 * 1024) return false;
            byte[] tag = new byte[size];
            int got = 0;
            while (got < size) { int k = s.Read(tag, got, size - got); if (k <= 0) break; got += k; }
            int pos = 0;
            if (ver >= 3 && (h[5] & 0x40) != 0 && size > 4)
            {
                int ext = ver == 4 ? Syncsafe(tag, 0) : (tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]) + 4;
                if (ext > 0 && ext < got) pos = ext;
            }
            int idLen = ver == 2 ? 3 : 4, hdrLen = ver == 2 ? 6 : 10;
            while (pos + hdrLen <= got)
            {
                if (tag[pos] == 0) break;
                string id = Encoding.ASCII.GetString(tag, pos, idLen);
                int fsz;
                if (ver == 2) fsz = tag[pos + 3] << 16 | tag[pos + 4] << 8 | tag[pos + 5];
                else if (ver == 4) fsz = Syncsafe(tag, pos + 4);
                else fsz = tag[pos + 4] << 24 | tag[pos + 5] << 16 | tag[pos + 6] << 8 | tag[pos + 7];
                int d = pos + hdrLen;
                if (fsz <= 0 || d + fsz > got) break;
                if (id == "APIC" || id == "PIC")
                {
                    int enc = tag[d], i = d + 1, end = d + fsz;
                    if (id == "PIC") { if (Encoding.ASCII.GetString(tag, i, 3).ToUpperInvariant() == "PNG") type = "image/png"; i += 3; }
                    else
                    {
                        int z = i; while (z < end && tag[z] != 0) z++;
                        string mime = Encoding.ASCII.GetString(tag, i, z - i).ToLowerInvariant();
                        if (mime.Contains("png")) type = "image/png";
                        i = z + 1;
                    }
                    i++; // tipo de imagen
                    if (enc == 1 || enc == 2) { while (i + 1 < end && !(tag[i] == 0 && tag[i + 1] == 0)) i += 2; i += 2; }
                    else { while (i < end && tag[i] != 0) i++; i++; }
                    if (i < end)
                    {
                        img = new byte[end - i];
                        Array.Copy(tag, i, img, 0, img.Length);
                        if (img.Length > 4 && img[0] == 0x89 && img[1] == 'P') type = "image/png";
                        return true;
                    }
                }
                pos = d + fsz;
            }
            return false;
        }

        // ---------------- ID3v2 (2.2, 2.3, 2.4) ----------------
        static long ReadId3v2(Stream s, LocalTrack t)
        {
            byte[] h = new byte[10];
            s.Position = 0;
            if (s.Read(h, 0, 10) < 10 || h[0] != 'I' || h[1] != 'D' || h[2] != '3') return 0;
            int ver = h[3];
            bool footer = (h[5] & 0x10) != 0;
            int size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
            long end = 10 + size + (footer ? 10 : 0);
            if (size <= 0 || size > 64 * 1024 * 1024) return end;
            byte[] tag = new byte[size];
            int got = 0;
            while (got < size) { int k = s.Read(tag, got, size - got); if (k <= 0) break; got += k; }
            int pos = 0;
            if (ver >= 3 && (h[5] & 0x40) != 0 && size > 4) // cabecera extendida
            {
                int ext = ver == 4 ? Syncsafe(tag, 0) : (tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]) + 4;
                if (ext > 0 && ext < got) pos = ext;
            }
            int idLen = ver == 2 ? 3 : 4, hdrLen = ver == 2 ? 6 : 10;
            while (pos + hdrLen <= got)
            {
                if (tag[pos] == 0) break;
                string id = Encoding.ASCII.GetString(tag, pos, idLen);
                int fsz;
                if (ver == 2) fsz = tag[pos + 3] << 16 | tag[pos + 4] << 8 | tag[pos + 5];
                else if (ver == 4) fsz = Syncsafe(tag, pos + 4);
                else fsz = tag[pos + 4] << 24 | tag[pos + 5] << 16 | tag[pos + 6] << 8 | tag[pos + 7];
                int data = pos + hdrLen;
                if (fsz <= 0 || data + fsz > got) break;
                if (id == "TIT2" || id == "TT2") { if (t.Title == "") t.Title = Text(tag, data, fsz); }
                else if (id == "TPE1" || id == "TP1") { if (t.Artist == "") t.Artist = Text(tag, data, fsz); }
                else if (id == "TPE2" || id == "TP2") { if (t.Artist == "") t.Artist = Text(tag, data, fsz); }
                else if (id == "TALB" || id == "TAL") { if (t.Album == "") t.Album = Text(tag, data, fsz); }
                pos = data + fsz;
            }
            return end;
        }

        static int Syncsafe(byte[] b, int i) { return (b[i] & 0x7f) << 21 | (b[i + 1] & 0x7f) << 14 | (b[i + 2] & 0x7f) << 7 | (b[i + 3] & 0x7f); }

        static string Text(byte[] b, int start, int len)
        {
            if (len < 1) return "";
            int enc = b[start];
            int i = start + 1, n = len - 1;
            string s;
            try
            {
                switch (enc)
                {
                    case 1: // UTF-16 con BOM
                        if (n >= 2 && b[i] == 0xFE && b[i + 1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(b, i + 2, (n - 2) & ~1);
                        else if (n >= 2 && b[i] == 0xFF && b[i + 1] == 0xFE) s = Encoding.Unicode.GetString(b, i + 2, (n - 2) & ~1);
                        else s = Encoding.Unicode.GetString(b, i, n & ~1);
                        break;
                    case 2: s = Encoding.BigEndianUnicode.GetString(b, i, n & ~1); break;
                    case 3: s = Encoding.UTF8.GetString(b, i, n); break;
                    default: s = Encoding.GetEncoding(28591).GetString(b, i, n); break; // ISO-8859-1
                }
            }
            catch { return ""; }
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            return s.Trim();
        }

        static void ReadId3v1(Stream s, LocalTrack t)
        {
            if (s.Length < 128) return;
            byte[] b = new byte[128];
            s.Position = s.Length - 128;
            if (s.Read(b, 0, 128) < 128 || b[0] != 'T' || b[1] != 'A' || b[2] != 'G') return;
            Encoding lat = Encoding.GetEncoding(1252);
            Func<int, int, string> f = delegate (int o, int l) { string x = lat.GetString(b, o, l); int z = x.IndexOf('\0'); return (z >= 0 ? x.Substring(0, z) : x).Trim(); };
            if (t.Title == "") t.Title = f(3, 30);
            if (t.Artist == "") t.Artist = f(33, 30);
            if (t.Album == "") t.Album = f(63, 30);
        }

        // ---------------- Duracion de un MP3 (cabecera Xing/Info/VBRI o tasa fija) ----------------
        static readonly int[,] Rates = {
            { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }, // V1 L1
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },    // V1 L2
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },     // V1 L3
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },    // V2 L1
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 } };       // V2 L2/L3

        static int Mp3Duration(Stream s, long start)
        {
            long len = s.Length;
            byte[] buf = new byte[64 * 1024];
            s.Position = start;
            int n = s.Read(buf, 0, buf.Length);
            for (int i = 0; i + 4 <= n; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                int verBits = (buf[i + 1] >> 3) & 3, layerBits = (buf[i + 1] >> 1) & 3;
                int brIdx = buf[i + 2] >> 4, srIdx = (buf[i + 2] >> 2) & 3, chMode = buf[i + 3] >> 6;
                if (verBits == 1 || layerBits == 0 || brIdx == 0 || brIdx == 15 || srIdx == 3) continue;
                bool v1 = verBits == 3;
                int layer = 4 - layerBits; // 1..3
                int[] srs = { 44100, 48000, 32000 };
                int sr = srs[srIdx] / (v1 ? 1 : (verBits == 2 ? 2 : 4));
                int row = v1 ? layer - 1 : (layer == 1 ? 3 : 4);
                int kbps = Rates[row, brIdx];
                int spf = layer == 1 ? 384 : (layer == 2 || v1 ? 1152 : 576);
                // Cabecera Xing / Info (VBR)
                int side = v1 ? (chMode == 3 ? 17 : 32) : (chMode == 3 ? 9 : 17);
                int x = i + 4 + side;
                if (x + 12 <= n)
                {
                    string tag = Encoding.ASCII.GetString(buf, x, 4);
                    if ((tag == "Xing" || tag == "Info") && (buf[x + 7] & 1) != 0)
                    {
                        long frames = (long)buf[x + 8] << 24 | (long)buf[x + 9] << 16 | (long)buf[x + 10] << 8 | buf[x + 11];
                        if (frames > 0) return (int)Math.Round(frames * spf / (double)sr);
                    }
                }
                int v = i + 4 + 32; // VBRI (Fraunhofer)
                if (v + 18 <= n && Encoding.ASCII.GetString(buf, v, 4) == "VBRI")
                {
                    long frames = (long)buf[v + 14] << 24 | (long)buf[v + 15] << 16 | (long)buf[v + 16] << 8 | buf[v + 17];
                    if (frames > 0) return (int)Math.Round(frames * spf / (double)sr);
                }
                if (kbps <= 0) return 0;
                long audioBytes = len - (start + i);
                return (int)Math.Round(audioBytes * 8 / (kbps * 1000.0));
            }
            return 0;
        }

        // ---------------- Ventanas de Windows para elegir archivos o una carpeta ----------------
        public static string[] Pick(bool folder)
        {
            string[] result = new string[0];
            Thread th = new Thread(delegate ()
            {
                try
                {
                    using (Form owner = new Form())
                    {
                        owner.ShowInTaskbar = false;
                        owner.FormBorderStyle = FormBorderStyle.None;
                        owner.StartPosition = FormStartPosition.CenterScreen;
                        owner.Size = new System.Drawing.Size(1, 1);
                        owner.Opacity = 0;
                        owner.TopMost = true;
                        owner.Show();
                        owner.Activate();
                        if (folder)
                        {
                            using (FolderBrowserDialog d = new FolderBrowserDialog())
                            {
                                d.Description = "Elige una carpeta con música (se añaden también sus subcarpetas)";
                                d.ShowNewFolderButton = false;
                                if (d.ShowDialog(owner) == DialogResult.OK) result = new string[] { d.SelectedPath };
                            }
                        }
                        else
                        {
                            using (OpenFileDialog d = new OpenFileDialog())
                            {
                                d.Title = "Añadir música de tu PC";
                                d.Multiselect = true;
                                d.Filter = "Música y video|" + Pattern(true) + "|Música (MP3, M4A, FLAC, OGG, WAV, WMA...)|" + Pattern(false) + "|Videos y películas (MP4, MKV, AVI, VOB...)|" + Pattern(null) + "|Todos los archivos|*.*";
                                if (d.ShowDialog(owner) == DialogResult.OK) result = d.FileNames;
                            }
                        }
                    }
                }
                catch { }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            th.Join();
            return result;
        }
    }
}
