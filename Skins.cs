// RetroAmp - skins clásicos (.wsz).
// Los que trae el programa están en datos\skins; los que agrega el usuario se guardan en media\perfil\skins
// (así se respaldan con media). Los incluidos no se pueden borrar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroAmp
{
    public partial class Server
    {
        string SkinDir { get { return Path.Combine(P.Data, "skins"); } }           // incluidos
        string UserSkinDir { get { return Path.Combine(P.Conf, "skins"); } }       // los del usuario
        // Los skins que vienen con el programa. 1.3: se llaman RetroAmp_* y en sus imágenes dice RETROAMP (no WINAMP);
        // se suma Bento Classified. Base 2.91 = el antiguo BaseOLD, Classified 3 y 5 = los Classified v5.5 de Winamp3 y Winamp5.
        public static readonly string[] BundledSkins = { "RetroAmp_AmpBoy.wsz", "RetroAmp_Amstrad_skullz.wsz", "RetroAmp_Axonamp.wsz",
            "RetroAmp_Base_2.91.wsz", "RetroAmp_Bento_Classified.wsz", "RetroAmp_Classified_3.wsz",
            "RetroAmp_Classified_5.wsz", "RetroAmp_DOSamp.wsz", "RetroAmp_Daria.wsz",
            "RetroAmp_Dexteramp_.wsz", "RetroAmp_FRYamp.wsz", "RetroAmp_Interamp.wsz",
            "RetroAmp_Napster1.wsz", "RetroAmp_Napster2.wsz", "RetroAmp_Sony_R909blue.wsz",
            "RetroAmp_Sonyamp_CDX-MP3.wsz", "RetroAmp_SuperMario.wsz", "RetroAmp_TexasInstruments.wsz",
            "RetroAmp_White_and_Black.wsz", "RetroAmp_Win98amp.wsz", "RetroAmp_Windows9x.wsz",
            "RetroAmp_XPamp.wsz", "RetroAmp_XPampBlue.wsz", "RetroAmp_black.wsz" };
        // Los nombres de antes (Winamp_*): se borran de datos\\skins al abrir para que no queden repetidos ni pasen a "del usuario"
        public static readonly string[] RemovedSkins = { "Winamp_AmpBoy.wsz", "Winamp_Amstrad_skullz.wsz", "Winamp_Axonamp.wsz",
            "Winamp_BaseOLD.wsz", "Winamp_Classified.wsz", "Winamp_Classified_v5.5.wsz",
            "Winamp_DOSamp.wsz", "Winamp_Daria.wsz", "Winamp_Dexteramp_.wsz",
            "Winamp_FRYamp.wsz", "Winamp_Interamp.wsz", "Winamp_Napster1.wsz",
            "Winamp_Napster2.wsz", "Winamp_Sony_R909blue.wsz", "Winamp_Sonyamp_CDX-MP3.wsz",
            "Winamp_SuperMario.wsz", "Winamp_TexasInstruments.wsz", "Winamp_White_and_Black.wsz",
            "Winamp_Win98amp.wsz", "Winamp_Windows9x.wsz", "Winamp_XPamp.wsz",
            "Winamp_XPampBlue.wsz", "Winamp_black.wsz" };

        static string SkinName(string n)
        {
            n = Path.GetFileNameWithoutExtension(n ?? "");
            n = Regex.Replace(n, @"[\\/:*?""<>|]", "_").Trim();
            if (n.Length > 80) n = n.Substring(0, 80);
            return n;
        }
        static bool IsSkinFile(string f) { return f.EndsWith(".wsz", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase); }
        string SkinFile(string n)
        {
            n = Path.GetFileName(n ?? "");
            string u = Path.Combine(UserSkinDir, n), d = Path.Combine(SkinDir, n);
            return File.Exists(u) ? u : File.Exists(d) ? d : null;
        }

        bool SkinRoute(Stream s, Req r)
        {
            string path = r.Path;
            if (path == "/api/skins")
            {
                SortedDictionary<string, bool> all = new SortedDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (string dir in new string[] { SkinDir, UserSkinDir })
                    if (Directory.Exists(dir)) foreach (string f in Directory.GetFiles(dir)) if (IsSkinFile(f)) all[Path.GetFileName(f)] = true;
                StringBuilder sb = new StringBuilder("{\"ok\":true,\"dir\":" + J(UserSkinDir) + ",\"skins\":[");
                int n = 0;
                foreach (string k in all.Keys) { if (n++ > 0) sb.Append(','); sb.Append(J(k)); }
                SendJson(s, sb.Append("]}").ToString());
                return true;
            }
            if (path == "/api/skin")
            {
                string f = SkinFile(r.Q1("n"));
                if (f == null) { SendBytes(s, 404, "text/plain", Encoding.UTF8.GetBytes("no"), null); return true; }
                SendBytes(s, 200, "application/zip", File.ReadAllBytes(f), null);
                return true;
            }
            if (path == "/api/skin/save" && r.Method == "POST")
            {
                // cuerpo: nombre del archivo, salto de línea y el .wsz en base64
                int nl = r.Body.IndexOf('\n');
                string name = SkinName(nl > 0 ? r.Body.Substring(0, nl) : "");
                byte[] data;
                try { data = Convert.FromBase64String(nl > 0 ? r.Body.Substring(nl + 1).Trim() : ""); }
                catch { SendJson(s, Err("El skin llegó dañado")); return true; }
                if (name == "" || data.Length < 22 || data[0] != 'P' || data[1] != 'K') { SendJson(s, Err("Ese archivo no es un skin .wsz")); return true; }
                try
                {
                    Directory.CreateDirectory(UserSkinDir);
                    File.WriteAllBytes(Path.Combine(UserSkinDir, name + ".wsz"), data);
                    P.Log("Skin agregado: " + name + ".wsz (media\\perfil\\skins)");
                    SendJson(s, "{\"ok\":true,\"name\":" + J(name + ".wsz") + "}");
                }
                catch (Exception ex) { SendJson(s, Err("No pude guardar el skin: " + ex.Message)); }
                return true;
            }
            if (path == "/api/skin/del" && r.Method == "POST")
            {
                string n = Path.GetFileName(r.Body.Trim());
                string f = Path.Combine(UserSkinDir, n);
                if (Array.IndexOf(BundledSkins, n) >= 0 || (!File.Exists(f) && File.Exists(Path.Combine(SkinDir, n)))) { SendJson(s, Err("Los skins que trae el programa no se borran.")); return true; }
                try { if (File.Exists(f)) File.Delete(f); SendJson(s, "{\"ok\":true}"); }
                catch (Exception ex) { SendJson(s, Err(ex.Message)); }
                return true;
            }
            return false;
        }
    }
}
