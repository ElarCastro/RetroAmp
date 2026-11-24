// RetroAmp - importar listas de Spotify (solo lectura) y buscarlas en YouTube.
// Inicio de sesión "Authorization Code + PKCE": no hace falta contraseña ni secreto,
// solo el Client ID de una app registrada en developer.spotify.com.
// Todo se guarda en spotify.txt, en media\perfil.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RetroAmp
{
    public partial class Server
    {
        // Se pueden cambiar con variables de entorno solo para pruebas
        static readonly string SpAccounts = Environment.GetEnvironmentVariable("WYT_SP_ACCOUNTS") ?? "https://accounts.spotify.com";
        static readonly string SpApi = Environment.GetEnvironmentVariable("WYT_SP_API") ?? "https://api.spotify.com/v1";
        const string SpScopes = "playlist-read-private playlist-read-collaborative user-library-read";

        readonly object spLock = new object();
        string spClient = "", spRefresh = "", spAccess = "", spUser = "";
        DateTime spExpires = DateTime.MinValue;
        string spVerifier = "", spState = "";
        bool spLoaded;
        int spLogins;
        Dictionary<string, string> spYt;          // id de Spotify -> id de YouTube (memoria de búsquedas)

        string SpFile { get { return Path.Combine(P.Conf, "spotify", "spotify.txt"); } }   // media\perfil\spotify
        string SpYtFile { get { return Path.Combine(P.Conf, "spotify", "spotify-youtube.txt"); } }
        string SpRedirect { get { return "http://127.0.0.1:" + Port + "/spotify"; } }

        void SpLoad()
        {
            if (spLoaded) return;
            spLoaded = true;
            try
            {
                if (File.Exists(SpFile))
                    foreach (string ln in File.ReadAllLines(SpFile))
                    {
                        int i = ln.IndexOf('='); if (i < 0) continue;
                        string k = ln.Substring(0, i).Trim(), v = ln.Substring(i + 1).Trim();
                        if (k == "client") spClient = v; else if (k == "refresh") spRefresh = v; else if (k == "user") spUser = v;
                    }
            }
            catch (Exception ex) { P.Log("Spotify: no pude leer spotify.txt: " + ex.Message); }
        }
        void SpSave()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SpFile));
                SaveText(SpFile, "# RetroAmp - conexion con Spotify (no compartas este archivo)\r\nclient=" + spClient + "\r\nrefresh=" + spRefresh + "\r\nuser=" + spUser + "\r\n");
            }
            catch (Exception ex) { P.Log("Spotify: no pude guardar spotify.txt: " + ex.Message); }
        }

        static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        static string RandUrl(int bytes) { byte[] b = new byte[bytes]; using (RandomNumberGenerator g = RandomNumberGenerator.Create()) g.GetBytes(b); return B64Url(b); }

        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        static Dictionary<string, object> JObj(object o) { return o as Dictionary<string, object>; }
        static string JStr(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        static long JNum(Dictionary<string, object> o, string k) { object v; long n; return o != null && o.TryGetValue(k, out v) && v != null && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? n : 0; }
        static object JGet(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }

        // Petición HTTP; devuelve el código y el texto (no lanza excepción por 4xx/5xx)
        int SpHttp(string method, string url, string form, string bearer, out string body)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = method;
            rq.Timeout = 30000; rq.ReadWriteTimeout = 30000;
            rq.UserAgent = "RetroAmp";
            IWebProxy px = SysProxy(); if (px != null) rq.Proxy = px;
            if (bearer != null) rq.Headers["Authorization"] = "Bearer " + bearer;
            if (form != null)
            {
                byte[] b = Encoding.ASCII.GetBytes(form);
                rq.ContentType = "application/x-www-form-urlencoded";
                rq.ContentLength = b.Length;
                using (Stream st = rq.GetRequestStream()) st.Write(b, 0, b.Length);
            }
            HttpWebResponse rs;
            try { rs = (HttpWebResponse)rq.GetResponse(); }
            catch (WebException we)
            {
                rs = we.Response as HttpWebResponse;
                if (rs == null) throw new Exception("no hay conexión con Spotify (" + we.Status + ")");
            }
            using (rs)
            using (StreamReader sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) { body = sr.ReadToEnd(); return (int)rs.StatusCode; }
        }

        static string SpErrText(string body)
        {
            try
            {
                Dictionary<string, object> o = JObj(Js.DeserializeObject(body));
                object e = JGet(o, "error");
                if (e is string) { string d = JStr(o, "error_description"); return d != "" ? d : (string)e; }
                string m = JStr(JObj(e), "message"); if (m != "") return m;
            }
            catch { }
            return body.Length > 160 ? body.Substring(0, 160) : body;
        }

        // Pide tokens (código nuevo o renovación)
        void SpToken(string form)
        {
            string body;
            int code = SpHttp("POST", SpAccounts + "/api/token", form, null, out body);
            if (code != 200) throw new Exception("Spotify no aceptó el inicio de sesión: " + SpErrText(body));
            Dictionary<string, object> o = JObj(Js.DeserializeObject(body));
            spAccess = JStr(o, "access_token");
            long exp = JNum(o, "expires_in"); if (exp <= 0) exp = 3600;
            spExpires = DateTime.UtcNow.AddSeconds(exp - 60);
            string rt = JStr(o, "refresh_token"); if (rt != "") spRefresh = rt;
            if (spAccess == "") throw new Exception("Spotify no devolvió el permiso de acceso");
        }

        string SpAccessToken()
        {
            lock (spLock)
            {
                SpLoad();
                if (spAccess != "" && DateTime.UtcNow < spExpires) return spAccess;
                if (spRefresh == "" || spClient == "") throw new Exception("No has iniciado sesión en Spotify");
                try { SpToken("grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(spRefresh) + "&client_id=" + Uri.EscapeDataString(spClient)); }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("invalid_grant") || ex.Message.ToLowerInvariant().Contains("revoked") || ex.Message.Contains("Invalid refresh token"))
                    { spRefresh = ""; spAccess = ""; SpSave(); throw new Exception("La sesión de Spotify caducó; vuelve a pulsar CONECTAR"); }
                    throw;
                }
                SpSave();
                return spAccess;
            }
        }

        Dictionary<string, object> SpGet(string urlOrPath)
        {
            string url = urlOrPath.StartsWith("http") ? urlOrPath : SpApi + urlOrPath;
            string body; int code = SpHttp("GET", url, null, SpAccessToken(), out body);
            if (code == 401) { lock (spLock) spAccess = ""; code = SpHttp("GET", url, null, SpAccessToken(), out body); }
            if (code == 403) throw new Exception("Spotify no da permiso (" + SpErrText(body) + "). Revisa que tu correo esté en 'User Management' de tu app de Spotify");
            if (code == 404) throw new SpNotFound();
            if (code == 429) throw new Exception("Spotify pide esperar un momento (demasiadas peticiones); intenta en un minuto");
            if (code != 200) throw new Exception("Spotify respondió " + code + ": " + SpErrText(body));
            return JObj(Js.DeserializeObject(body));
        }
        class SpNotFound : Exception { public SpNotFound() : base("Spotify no encontró esa lista") { } }

        string SpStatusJson()
        {
            lock (spLock)
            {
                SpLoad();
                return "{\"ok\":true,\"client\":" + J(spClient) + ",\"logged\":" + J(spRefresh != "") + ",\"user\":" + J(spUser) +
                    ",\"redirect\":" + J(SpRedirect) + ",\"port\":" + Port + ",\"logins\":" + spLogins + "}";
            }
        }

        // ---------- Rutas ----------
        // Spotify vuelve aquí desde el navegador al terminar de iniciar sesión (no lleva la cookie del programa;
        // se valida con el "state" aleatorio de un solo uso).
        bool SpCallback(System.IO.Stream s, Req r)
        {
            if (r.Path != "/spotify") return false;
            string host = r.H1("host");
            if (host != "127.0.0.1:" + Port) { SendBytes(s, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("RetroAmp: acceso no permitido"), null); return true; }
            string title, text;
            string st = r.Q1("state"), code = r.Q1("code"), err = r.Q1("error");
            string verifier;
            lock (spLock) { verifier = (st != "" && st == spState) ? spVerifier : null; if (verifier != null) { spState = ""; spVerifier = ""; } }
            if (verifier == null) { title = "No se pudo conectar"; text = "Este enlace ya se usó o no viene de RetroAmp. Vuelve a RetroAmp y pulsa CONECTAR otra vez."; }
            else if (err != "") { title = "Conexión cancelada"; text = err == "access_denied" ? "No diste permiso a RetroAmp. Puedes intentarlo otra vez cuando quieras." : "Spotify respondió: " + err; }
            else
            {
                try
                {
                    lock (spLock)
                    {
                        SpLoad();
                        SpToken("grant_type=authorization_code&code=" + Uri.EscapeDataString(code) + "&redirect_uri=" + Uri.EscapeDataString(SpRedirect) +
                                "&client_id=" + Uri.EscapeDataString(spClient) + "&code_verifier=" + Uri.EscapeDataString(verifier));
                    }
                    string who = "";
                    try { Dictionary<string, object> me = SpGet("/me"); who = JStr(me, "display_name"); if (who == "") who = JStr(me, "id"); } catch (Exception ex) { P.Log("Spotify /me: " + ex.Message); }
                    lock (spLock) { spUser = who; spLogins++; SpSave(); }
                    title = "¡Listo!"; text = "RetroAmp ya está conectado a tu cuenta de Spotify" + (who != "" ? " (" + who + ")" : "") + ". Puedes cerrar esta pestaña y volver a RetroAmp.";
                    ctx["Show"] = true;
                }
                catch (Exception ex) { P.Log("Spotify token: " + ex.Message); title = "No se pudo conectar"; text = ex.Message; }
            }
            string html = "<!doctype html><meta charset=utf-8><title>RetroAmp · Spotify</title><style>body{background:#111;color:#cfc;font:16px Segoe UI,sans-serif;display:grid;place-items:center;height:90vh}div{max-width:520px;border:1px solid #3a3;padding:24px 28px;background:#1b1b1b}h1{color:#6f6;font-size:22px;margin:0 0 10px}</style><div><h1>" +
                WebUtility.HtmlEncode(title) + "</h1><p>" + WebUtility.HtmlEncode(text) + "</p></div>";
            SendBytes(s, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null);
            return true;
        }

        bool SpRoute(System.IO.Stream s, Req r)
        {
            string path = r.Path;
            if (!path.StartsWith("/api/sp/")) return false;
            try
            {
                if (path == "/api/sp/status") SendJson(s, SpStatusJson());
                else if (path == "/api/sp/client")
                {
                    string id = (r.Q1("id") ?? "").Trim().ToLowerInvariant();
                    if (!Regex.IsMatch(id, "^[0-9a-f]{32}$")) { SendJson(s, Err("El Client ID son 32 letras y números (cópialo del panel de Spotify)")); return true; }
                    lock (spLock) { SpLoad(); if (id != spClient) { spClient = id; spRefresh = ""; spAccess = ""; spUser = ""; } SpSave(); }
                    SendJson(s, SpStatusJson());
                }
                else if (path == "/api/sp/login")
                {
                    string url;
                    lock (spLock)
                    {
                        SpLoad();
                        if (spClient == "") { SendJson(s, Err("Primero pega tu Client ID")); return true; }
                        spVerifier = RandUrl(48); spState = RandUrl(16);
                        string challenge;
                        using (SHA256 h = SHA256.Create()) challenge = B64Url(h.ComputeHash(Encoding.ASCII.GetBytes(spVerifier)));
                        url = SpAccounts + "/authorize?response_type=code&client_id=" + Uri.EscapeDataString(spClient) +
                              "&scope=" + Uri.EscapeDataString(SpScopes) + "&redirect_uri=" + Uri.EscapeDataString(SpRedirect) +
                              "&state=" + spState + "&code_challenge_method=S256&code_challenge=" + challenge;
                    }
                    bool opened = false;
                    if (r.Q1("open") != "0") { try { Process.Start(url); opened = true; } catch (Exception ex) { P.Log("Spotify: no pude abrir el navegador: " + ex.Message); } }
                    SendJson(s, "{\"ok\":true,\"url\":" + J(url) + ",\"opened\":" + J(opened) + "}");
                }
                else if (path == "/api/sp/logout")
                {
                    lock (spLock) { SpLoad(); spRefresh = ""; spAccess = ""; spUser = ""; SpSave(); }
                    SendJson(s, SpStatusJson());
                }
                else if (path == "/api/sp/playlists") SendJson(s, SpPlaylists());
                else if (path == "/api/sp/tracks")
                {
                    string id = r.Q1("id");
                    if (id != "liked" && !Regex.IsMatch(id, "^[A-Za-z0-9]{10,40}$")) { SendJson(s, Err("lista no válida")); return true; }
                    SendJson(s, SpTracks(id));
                }
                else if (path == "/api/sp/find") SendJson(s, SpFind(r.Q1("sid"), r.Q1("q"), r.Q1("dur")));
                else Send404(s);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException)) P.Log("Spotify " + path + ": " + ex.Message);
                try { SendJson(s, Err(ex.Message)); } catch { }
            }
            return true;
        }

        string SpPlaylists()
        {
            StringBuilder sb = new StringBuilder("{\"ok\":true,\"lists\":[");
            int n = 0;
            // "Tus me gusta" primero
            try
            {
                Dictionary<string, object> liked = SpGet("/me/tracks?limit=1");
                sb.Append("{\"id\":\"liked\",\"name\":").Append(J("Canciones que te gustan")).Append(",\"owner\":\"\",\"count\":").Append(JNum(liked, "total")).Append('}');
                n++;
            }
            catch (Exception ex) { P.Log("Spotify me gusta: " + ex.Message); }
            string next = "/me/playlists?limit=50";
            int pages = 0;
            while (!string.IsNullOrEmpty(next) && pages++ < 20)
            {
                Dictionary<string, object> o = SpGet(next);
                object[] items = JGet(o, "items") as object[];
                if (items == null) { ArrayList al = JGet(o, "items") as ArrayList; if (al != null) items = al.ToArray(); }
                if (items != null)
                    foreach (object x in items)
                    {
                        Dictionary<string, object> p = JObj(x); if (p == null) continue;
                        string id = JStr(p, "id"); if (id == "") continue;
                        // el total viene en "items" (API nueva) o en "tracks" (API anterior)
                        long count = JNum(JObj(JGet(p, "items")), "total"); if (count == 0) count = JNum(JObj(JGet(p, "tracks")), "total");
                        if (n++ > 0) sb.Append(',');
                        sb.Append("{\"id\":").Append(J(id)).Append(",\"name\":").Append(J(JStr(p, "name")))
                          .Append(",\"owner\":").Append(J(JStr(JObj(JGet(p, "owner")), "display_name"))).Append(",\"count\":").Append(count).Append('}');
                    }
                next = JStr(o, "next");
            }
            return sb.Append("]}").ToString();
        }

        string SpTracks(string id)
        {
            List<string> rows = new List<string>();
            int skipped = 0;
            string next;
            if (id == "liked") next = "/me/tracks?limit=50";
            else next = "/playlists/" + id + "/items?limit=50&additional_types=track";
            bool triedOld = false;
            int pages = 0;
            while (!string.IsNullOrEmpty(next) && pages++ < 100)
            {
                Dictionary<string, object> o;
                try { o = SpGet(next); }
                catch (SpNotFound)
                {
                    // API anterior: /playlists/{id}/tracks
                    if (triedOld || id == "liked" || pages > 1) throw;
                    triedOld = true; pages = 0; next = "/playlists/" + id + "/tracks?limit=50&additional_types=track"; continue;
                }
                object raw = JGet(o, "items");
                object[] items = raw as object[];
                if (items == null) { ArrayList al = raw as ArrayList; if (al != null) items = al.ToArray(); }
                if (items != null)
                    foreach (object x in items)
                    {
                        Dictionary<string, object> e = JObj(x); if (e == null) continue;
                        Dictionary<string, object> t = JObj(JGet(e, "item")) ?? JObj(JGet(e, "track"));
                        if (t == null || JStr(t, "type") == "episode" || JStr(t, "id") == "") { skipped++; continue; }
                        List<string> arts = new List<string>();
                        object ar = JGet(t, "artists");
                        IEnumerable list = ar as IEnumerable;
                        if (list != null && !(ar is string)) foreach (object a in list) { string nm = JStr(JObj(a), "name"); if (nm != "") arts.Add(nm); }
                        rows.Add("{\"id\":" + J("s:" + JStr(t, "id")) + ",\"title\":" + J(JStr(t, "name")) + ",\"ch\":" + J(string.Join(", ", arts.ToArray())) +
                                 ",\"alb\":" + J(JStr(JObj(JGet(t, "album")), "name")) + ",\"dur\":" + (JNum(t, "duration_ms") / 1000) + "}");
                    }
                next = JStr(o, "next");
            }
            return "{\"ok\":true,\"skipped\":" + skipped + ",\"items\":[" + string.Join(",", rows.ToArray()) + "]}";
        }

        // Busca la canción en YouTube y elige el resultado más parecido (misma duración, canal del artista…)
        string SpFind(string sid, string q, string durS)
        {
            q = Regex.Replace(q ?? "", "[\"\\r\\n]", " ").Trim();
            if (q == "") return Err("canción sin nombre");
            if (!Regex.IsMatch(sid ?? "", "^[A-Za-z0-9]{10,40}$")) sid = "";
            lock (spLock)
            {
                if (spYt == null)
                {
                    spYt = new Dictionary<string, string>();
                    try { if (File.Exists(SpYtFile)) foreach (string ln in File.ReadAllLines(SpYtFile)) { string[] p = ln.Split('\t'); if (p.Length == 2 && VideoId.IsMatch(p[1])) spYt[p[0]] = p[1]; } } catch { }
                }
                string known;
                if (sid != "" && spYt.TryGetValue(sid, out known)) return "{\"ok\":true,\"id\":" + J(known) + ",\"cached\":true}";
            }
            int dur; int.TryParse(durS, out dur);
            RunResult x = YtDlp(new string[] { "--flat-playlist", "--print", Tpl, "ytsearch6:" + q }, 90000);
            List<Entry> found = ParseEntries(x.Out, false);
            if (found.Count == 0) return Err(x.Err != "" ? ShortErr(x.Err) : "no apareció en YouTube");
            string ql = q.ToLowerInvariant();
            string artist = ql.Contains(" - ") ? ql.Substring(0, ql.IndexOf(" - ")) : "";
            string firstArtist = artist.Split(',')[0].Trim();
            Entry best = null; double bestScore = double.MinValue;
            string[] bad = { "live", "en vivo", "cover", "karaoke", "remix", "8d", "slowed", "sped up", "reaction", "tutorial", "instrumental", "letra", "lyrics" };
            for (int i = 0; i < found.Count; i++)
            {
                Entry e = found[i];
                string tl = (e.Title ?? "").ToLowerInvariant(), cl = (e.Ch ?? "").ToLowerInvariant();
                double sc = -i * 2.0;                                       // el orden de YouTube cuenta
                if (dur > 0 && e.Dur > 0) { int d = Math.Abs(e.Dur - dur); sc += d <= 3 ? 12 : d <= 10 ? 8 : d <= 25 ? 3 : d > 90 ? -15 : -4; }
                if (cl.EndsWith(" - topic")) sc += 6;
                if (firstArtist != "" && (cl.Contains(firstArtist) || firstArtist.Contains(cl.Replace(" - topic", "").Trim()))) sc += 5;
                if (tl.Contains("official") || tl.Contains("oficial")) sc += 2;
                foreach (string b in bad) if (tl.Contains(b) && !ql.Contains(b)) sc -= b == "letra" || b == "lyrics" ? 1 : 7;
                if (sc > bestScore) { bestScore = sc; best = e; }
            }
            lock (spLock)
            {
                if (sid != "")
                {
                    spYt[sid] = best.Id;
                    try { Directory.CreateDirectory(Path.GetDirectoryName(SpYtFile)); File.AppendAllText(SpYtFile, sid + "\t" + best.Id + "\r\n"); } catch { }
                }
            }
            return "{\"ok\":true,\"id\":" + J(best.Id) + ",\"yttitle\":" + J(best.Title) + ",\"ytch\":" + J(best.Ch) + ",\"ytdur\":" + best.Dur + "}";
        }
    }
}
