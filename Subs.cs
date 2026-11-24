// RetroAmp - subtítulos de películas: archivos junto al video (SRT, VTT, ASS/SSA, SBV, SUB)
// y los que vienen dentro del archivo (MKV/MP4). Todo se entrega al reproductor como WebVTT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroAmp
{
    public static class Subs
    {
        public static readonly string[] Exts = { ".srt", ".vtt", ".ass", ".ssa", ".sbv", ".sub" };
        static readonly string[] SubDirs = { "subs", "sub", "subtitles", "subtitulos", "subtítulos" };

        public static bool IsSub(string path)
        {
            return Array.IndexOf(Exts, Path.GetExtension(path ?? "").ToLowerInvariant()) >= 0;
        }

        // Subtítulos con el mismo nombre que la película ("Pelicula.srt", "Pelicula.es.srt", "Pelicula.Spanish.ass"...),
        // junto a ella o en una carpeta Subs / Subtitulos
        public static List<string> FindFor(string video)
        {
            List<string> found = new List<string>();
            string dir = Path.GetDirectoryName(video), bn = Path.GetFileNameWithoutExtension(video);
            List<string> dirs = new List<string> { dir };
            try { foreach (string d in Directory.GetDirectories(dir)) if (Array.IndexOf(SubDirs, Path.GetFileName(d).ToLowerInvariant()) >= 0) dirs.Add(d); } catch { }
            int vids = 0;
            try { foreach (string f in Directory.GetFiles(dir)) if (Local.IsVideo(f)) vids++; } catch { }
            foreach (string d in dirs)
            {
                string[] fs; try { fs = Directory.GetFiles(d); } catch { continue; }
                Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
                foreach (string f in fs)
                {
                    if (!IsSub(f)) continue;
                    string n = Path.GetFileNameWithoutExtension(f);
                    bool same = n.Equals(bn, StringComparison.OrdinalIgnoreCase) || n.StartsWith(bn + ".", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith(bn + "_", StringComparison.OrdinalIgnoreCase) || n.StartsWith(bn + " ", StringComparison.OrdinalIgnoreCase);
                    // Si en la carpeta hay una sola película, también valen subtítulos con otro nombre
                    if (same || vids == 1) found.Add(f);
                }
            }
            return found;
        }

        public static string Lang(string label)
        {
            string l = (label ?? "").ToLowerInvariant();
            if (Regex.IsMatch(l, @"(^|[\W_])(es|spa|esp|español|espanol|spanish|castellano|latino|lat|es-mx|es-419)([\W_]|$)")) return "es";
            if (Regex.IsMatch(l, @"(^|[\W_])(en|eng|english|ingles|inglés)([\W_]|$)")) return "en";
            if (Regex.IsMatch(l, @"(^|[\W_])(pt|por|portuguese|português|portugues)([\W_]|$)")) return "pt";
            if (Regex.IsMatch(l, @"(^|[\W_])(fr|fre|fra|french|français|frances)([\W_]|$)")) return "fr";
            return "";
        }

        // ---------- Conversión a WebVTT ----------
        public static string ToVtt(string path, double fps)
        {
            string text = Local.DecodeText(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".vtt" || text.TrimStart().StartsWith("WEBVTT")) return text.TrimStart('﻿');
            if (ext == ".ass" || ext == ".ssa" || Regex.IsMatch(text, @"^\[Script Info\]", RegexOptions.Multiline)) return FromAss(text);
            if (ext == ".sbv") return FromSbv(text);
            if (ext == ".sub" && Regex.IsMatch(text, @"^\{\d+\}\{\d*\}", RegexOptions.Multiline)) return FromMicroDvd(text, fps);
            return FromSrt(text);
        }

        static string Ts(double s)
        {
            if (s < 0) s = 0;
            TimeSpan t = TimeSpan.FromMilliseconds(Math.Round(s * 1000));
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }

        static double Sec(string h, string m, string s, string frac, int fracDigits)
        {
            double f = 0;
            if (!string.IsNullOrEmpty(frac)) f = int.Parse(frac) / Math.Pow(10, frac.Length);
            return int.Parse(h) * 3600 + int.Parse(m) * 60 + int.Parse(s) + f;
        }

        static string Clean(string t)
        {
            // Solo se dejan <i> <b> <u>; lo demás (colores, fuentes) se quita
            t = Regex.Replace(t, @"<(?!/?[ibu]>)[^>]*>", "");
            t = Regex.Replace(t, @"\{[^}]*\}", "");
            t = Regex.Replace(t, @"&(?![a-zA-Z]+;|#\d+;)", "&amp;");
            return t.Trim('\n', ' ');
        }

        static string FromSrt(string text)
        {
            StringBuilder sb = new StringBuilder("WEBVTT\n\n");
            Regex tl = new Regex(@"(\d+):(\d{1,2}):(\d{1,2})(?:[,.](\d{1,3}))?\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})(?:[,.](\d{1,3}))?");
            foreach (string block in Regex.Split(text.Trim(), @"\n\s*\n"))
            {
                string[] lines = block.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    Match m = tl.Match(lines[i]);
                    if (!m.Success) continue;
                    double a = Sec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, 3);
                    double b = Sec(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value, 3);
                    string body = Clean(string.Join("\n", lines, i + 1, lines.Length - i - 1));
                    if (body != "") sb.Append(Ts(a)).Append(" --> ").Append(Ts(b)).Append('\n').Append(body).Append("\n\n");
                    break;
                }
            }
            return sb.ToString();
        }

        static string FromSbv(string text)
        {
            StringBuilder sb = new StringBuilder("WEBVTT\n\n");
            Regex tl = new Regex(@"^(\d+):(\d{1,2}):(\d{1,2})\.(\d{1,3}),(\d+):(\d{1,2}):(\d{1,2})\.(\d{1,3})\s*$");
            foreach (string block in Regex.Split(text.Trim(), @"\n\s*\n"))
            {
                string[] lines = block.Split('\n');
                Match m = tl.Match(lines[0].Trim());
                if (!m.Success) continue;
                double a = Sec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, 3);
                double b = Sec(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value, 3);
                string body = Clean(string.Join("\n", lines, 1, lines.Length - 1));
                if (body != "") sb.Append(Ts(a)).Append(" --> ").Append(Ts(b)).Append('\n').Append(body).Append("\n\n");
            }
            return sb.ToString();
        }

        static string FromAss(string text)
        {
            StringBuilder sb = new StringBuilder("WEBVTT\n\n");
            string[] fmt = null;
            bool events = false;
            foreach (string raw in text.Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.StartsWith("[")) { events = ln.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!events) continue;
                if (ln.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    fmt = ln.Substring(7).Split(',');
                    for (int i = 0; i < fmt.Length; i++) fmt[i] = fmt[i].Trim().ToLowerInvariant();
                    continue;
                }
                if (!ln.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                if (fmt == null) fmt = "layer,start,end,style,name,marginl,marginr,marginv,effect,text".Split(',');
                string[] p = ln.Substring(9).TrimStart().Split(new char[] { ',' }, fmt.Length);
                if (p.Length < fmt.Length) continue;
                int si = Array.IndexOf(fmt, "start"), ei = Array.IndexOf(fmt, "end"), ti = Array.IndexOf(fmt, "text");
                if (si < 0 || ei < 0 || ti < 0) continue;
                double a, b;
                if (!AssTime(p[si], out a) || !AssTime(p[ei], out b)) continue;
                string t = p[ti];
                t = Regex.Replace(t, @"\{\\i1\}", "<i>"); t = Regex.Replace(t, @"\{\\i0?\}", "</i>");
                t = Regex.Replace(t, @"\{\\b1\}", "<b>"); t = Regex.Replace(t, @"\{\\b0?\}", "</b>");
                t = Regex.Replace(t, @"\{[^}]*\}", "");          // efectos, posiciones, colores
                t = t.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
                if (Regex.Matches(t, "<i>").Count > Regex.Matches(t, "</i>").Count) t += "</i>";
                if (Regex.Matches(t, "<b>").Count > Regex.Matches(t, "</b>").Count) t += "</b>";
                t = Clean(t);
                if (t != "") sb.Append(Ts(a)).Append(" --> ").Append(Ts(b)).Append('\n').Append(t).Append("\n\n");
            }
            return sb.ToString();
        }

        static bool AssTime(string s, out double v)
        {
            v = 0;
            Match m = Regex.Match(s.Trim(), @"^(\d+):(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?$");
            if (!m.Success) return false;
            v = Sec(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, 2);
            return true;
        }

        // MicroDVD: {inicio}{fin}texto, en cuadros (frames)
        static string FromMicroDvd(string text, double fps)
        {
            StringBuilder sb = new StringBuilder("WEBVTT\n\n");
            foreach (string raw in text.Split('\n'))
            {
                Match m = Regex.Match(raw.Trim(), @"^\{(\d+)\}\{(\d*)\}(.*)$");
                if (!m.Success) continue;
                long fa = long.Parse(m.Groups[1].Value), fb = m.Groups[2].Value == "" ? fa + (long)(fps * 3) : long.Parse(m.Groups[2].Value);
                string t = m.Groups[3].Value;
                if (fa == 1 && fb == 1) { double f; if (double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f > 1) fps = f; continue; }
                t = Regex.Replace(t, @"\{y:i\}", "<i>", RegexOptions.IgnoreCase);
                bool ital = t.Contains("<i>");
                t = Clean(t.Replace("|", "\n"));
                if (ital && !t.Contains("</i>")) t += "</i>";
                if (t != "") sb.Append(Ts(fa / fps)).Append(" --> ").Append(Ts(fb / fps)).Append('\n').Append(t).Append("\n\n");
            }
            return sb.ToString();
        }
    }

    public partial class Server
    {
        // /api/subs?p=<video>  -> lista de subtítulos (archivos junto a la película + los que trae dentro)
        // /api/sub?p=<archivo> o ?v=<video>&emb=N -> el subtítulo en WebVTT
        // /api/subs/pick -> elegir un archivo de subtítulos a mano
        bool SubsRoute(Stream s, Req r)
        {
            string path = r.Path;
            if (path == "/api/subs")
            {
                string v = r.Q1("p");
                if (!Local.IsVideo(v) || !File.Exists(v)) { SendJson(s, Err("No encuentro el video")); return true; }
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"subs\":[");
                int n = 0;
                string bn = Path.GetFileNameWithoutExtension(v);
                foreach (string f in Subs.FindFor(v))
                {
                    string label = Path.GetFileName(f);
                    string rest = Path.GetFileNameWithoutExtension(f);
                    if (rest.StartsWith(bn, StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(bn.Length);
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(J("f:" + f)).Append(",\"label\":").Append(J(label)).Append(",\"lang\":").Append(J(Subs.Lang(rest + " " + Path.GetFileName(Path.GetDirectoryName(f))))).Append('}');
                }
                // Subtítulos de texto que vienen dentro del archivo (MKV, MP4...)
                if (File.Exists(P.Ffmpeg))
                {
                    RunResult info = Run(P.Ffmpeg, new string[] { "-hide_banner", "-i", v }, 30000);
                    int si = 0;
                    foreach (Match m in Regex.Matches(info.Err ?? "", @"Stream #\d+:\d+(?:\[[^\]]*\])?(?:\((\w+)\))?(?:\[[^\]]*\])?: Subtitle: (\w+)([^\r\n]*)"))
                    {
                        string codec = m.Groups[2].Value.ToLowerInvariant(), lang = m.Groups[1].Value;
                        if (Regex.IsMatch(codec, "^(subrip|srt|ass|ssa|mov_text|webvtt|text|microdvd)$"))
                        {
                            if (n++ > 0) sb.Append(',');
                            string lab = "Incluido " + (si + 1) + (lang != "" && lang != "und" ? " (" + lang + ")" : "") + (m.Groups[3].Value.Contains("(forced)") ? " forzado" : "");
                            sb.Append("{\"id\":").Append(J("e:" + si)).Append(",\"label\":").Append(J(lab)).Append(",\"lang\":").Append(J(Subs.Lang(lang))).Append('}');
                        }
                        si++;
                    }
                }
                SendJson(s, sb.Append("]}").ToString());
            }
            else if (path == "/api/sub")
            {
                string vtt;
                try
                {
                    string f = r.Q1("p"), v = r.Q1("v"), e = r.Q1("emb");
                    if (f != "")
                    {
                        if (!Subs.IsSub(f) || !File.Exists(f)) { Send404(s); return true; }
                        double fps = 23.976;
                        double.TryParse(r.Q1("fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out fps);
                        if (fps < 1) fps = 23.976;
                        vtt = Subs.ToVtt(f, fps);
                    }
                    else
                    {
                        int ei;
                        if (!Local.IsVideo(v) || !File.Exists(v) || !int.TryParse(e, out ei) || !File.Exists(P.Ffmpeg)) { Send404(s); return true; }
                        RunResult rr = Run(P.Ffmpeg, new string[] { "-hide_banner", "-loglevel", "error", "-i", v, "-map", "0:s:" + ei, "-f", "webvtt", "-" }, 180000);
                        if (rr.Code != 0 || !rr.Out.Contains("WEBVTT")) throw new Exception("no pude leer los subtítulos de la película (" + ShortErr(rr.Err) + ")");
                        vtt = rr.Out;
                    }
                }
                catch (Exception ex) { SendJson(s, Err(ex.Message)); return true; }
                SendBytes(s, 200, "text/vtt; charset=utf-8", Encoding.UTF8.GetBytes(vtt), "");
            }
            else if (path == "/api/subs/pick")
            {
                string[] f = Dialogs.Open("Elige el archivo de subtítulos", "Subtítulos (SRT, VTT, ASS, SSA, SBV, SUB)|*.srt;*.vtt;*.ass;*.ssa;*.sbv;*.sub|Todos los archivos|*.*", false);
                if (f.Length == 0 || !Subs.IsSub(f[0])) { SendJson(s, "{\"ok\":false}"); return true; }
                SendJson(s, "{\"ok\":true,\"id\":" + J("f:" + f[0]) + ",\"label\":" + J(Path.GetFileName(f[0])) + "}");
            }
            else return false;
            return true;
        }
    }
}
