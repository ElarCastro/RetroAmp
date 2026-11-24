// RetroAmp - ventana propia de Windows (WinForms + WebView2)
// Ahora va compilado dentro de RetroAmp.exe (antes se compilaba al vuelo desde servidor.ps1)
using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RetroAmp
{
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class TaskbarListCom { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    public class Host : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;

        WebView2 web;
        CoreWebView2Environment env0;
        System.Collections.Generic.Dictionary<string, ModWin> mods = new System.Collections.Generic.Dictionary<string, ModWin>();
        bool modsHidden;
        readonly System.Collections.Generic.List<ModWin> shadeHid = new System.Collections.Generic.List<ModWin>();
        void ShowMods(bool show)
        {
            if (!show)
            {
                modsHidden = true;
                foreach (ModWin w in mods.Values)
                    if (!w.IsDisposed && w.Visible) { if (!shadeHid.Contains(w)) shadeHid.Add(w); try { w.Hide(); } catch { } }
            }
            else
            {
                modsHidden = false;
                foreach (ModWin w in shadeHid) if (!w.IsDisposed) { try { w.Show(); } catch { } }
                shadeHid.Clear();
                try { Activate(); } catch { }
            }
        }
        System.Collections.Generic.Dictionary<string, Rectangle> modBounds = new System.Collections.Generic.Dictionary<string, Rectangle>();
        Hashtable ctx;
        string url, dataDir, root;
        public static string ConfDir;   // media\perfil (posición de las ventanas)
        bool ready, ghost, clickThrough, playing, docked, thumbsAdded;
        bool snapOn = true;                       // imanes entre ventanas
        System.Collections.Generic.List<ModWin> grp; Point grpLast;   // ventanas que se mueven con la principal
        byte ghostAlpha = 190;
        Point undockPos;
        bool hasUndockPos;
        Timer timer;
        NotifyIcon tray;
        ToolStripMenuItem miGhost, miTop;
        ITaskbarList3 tb;
        uint msgTaskbarCreated;
        IntPtr icoPrev, icoPlay, icoPause, icoNext;
        public bool InitFailed;
        public string InitError;

        public Host(string url, string dataDir, string root, Hashtable ctx)
        {
            this.url = url; this.dataDir = dataDir; this.root = root; this.ctx = ctx;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(17, 17, 24);
            Text = "RetroAmp";
            ShowInTaskbar = true;
            ClientSize = new Size(470, 150);
            Icon = Icon.FromHandle(MakeAppIcon());
            icoPrev = MakeIcon(0); icoPlay = MakeIcon(1); icoPause = MakeIcon(2); icoNext = MakeIcon(3);
            wantPos = LoadPos();
            if (savedSize.Width > 0) ClientSize = savedSize;
            // Al abrir, la ventana espera fuera de la pantalla hasta que la página dice su tamaño (sin "brincar" de tamaño)
            placing = true; try { Location = new Point(-32000, -32000); } finally { placing = false; }
            Timer showT = new Timer(); showT.Interval = 8000;
            showT.Tick += delegate { showT.Stop(); showT.Dispose(); ShowPlaced(); };
            showT.Start();
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            try { web.DefaultBackgroundColor = Color.FromArgb(17, 17, 24); } catch { }
            Controls.Add(web);
            msgTaskbarCreated = RegisterWindowMessage("TaskbarButtonCreated");
            BuildTray();
            timer = new Timer();
            timer.Interval = 40;
            timer.Tick += OnTick;
            timer.Start();
            Load += OnLoad;
            FormClosing += OnClosing;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x20000; // WS_MINIMIZEBOX: permite minimizar desde la barra de tareas
                return cp;
            }
        }

        public static string Run(string url, string dataDir, string root, Hashtable ctx, string loaderDir)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDPIAware(); } catch { } }
            try
            {
                MethodInfo mi = typeof(CoreWebView2Environment).GetMethod("SetLoaderDllFolderPath", BindingFlags.Public | BindingFlags.Static);
                if (mi != null) mi.Invoke(null, new object[] { loaderDir });
            }
            catch { }
            try { Application.EnableVisualStyles(); } catch { }
            Host f = new Host(url, dataDir, root, ctx);
            if (Program.StartMinimized) f.WindowState = FormWindowState.Minimized;
            Application.Run(f);
            if (f.InitFailed) return f.InitError ?? "error desconocido";
            return "";
        }

        async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                CoreWebView2EnvironmentOptions o = new CoreWebView2EnvironmentOptions();
                o.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling";
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir, o);
                await web.EnsureCoreWebView2Async(env);
                CoreWebView2Settings st = web.CoreWebView2.Settings;
                st.AreDefaultContextMenusEnabled = false;
                st.AreDevToolsEnabled = false;
                st.IsStatusBarEnabled = false;
                st.IsZoomControlEnabled = false;
                TrySetBool(st, "AreBrowserAcceleratorKeysEnabled", false);
                TrySetBool(st, "IsNonClientRegionSupportEnabled", true);
                TrySetBool(st, "IsSwipeNavigationEnabled", false);
                web.CoreWebView2.WebMessageReceived += OnWebMessage;
                env0 = env;
                LoadModBounds();
                web.CoreWebView2.NewWindowRequested += OnNewWindow;
                web.CoreWebView2.ContainsFullScreenElementChanged += delegate { SetFull(web.CoreWebView2.ContainsFullScreenElement); };
                web.CoreWebView2.Navigate(url);
                ready = true;
                RegisterHotKey(Handle, 1, 0x4000, 0xB3); // play/pausa
                RegisterHotKey(Handle, 2, 0x4000, 0xB0); // siguiente
                RegisterHotKey(Handle, 3, 0x4000, 0xB1); // anterior
                RegisterHotKey(Handle, 4, 0x4000, 0xB2); // stop
                RegisterHotKey(Handle, 5, 0x4000 | 0x0002 | 0x0001, 0x46); // Ctrl+Alt+F: modo fantasma
            }
            catch (Exception ex)
            {
                InitFailed = true;
                InitError = ex.ToString();
                Close();
            }
        }

        static void TrySetBool(object target, string prop, bool value)
        {
            try
            {
                PropertyInfo pi = target.GetType().GetProperty(prop);
                if (pi != null && pi.CanWrite) pi.SetValue(target, value, null);
            }
            catch { }
        }

        void Post(string s)
        {
            if (!ready || web.CoreWebView2 == null) return;
            try { web.CoreWebView2.PostWebMessageAsString(s); } catch { }
        }

        static float ParseF(string s)
        {
            float f;
            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) return f;
            return 0f;
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string m;
            try { m = e.TryGetWebMessageAsString(); } catch { return; }
            if (string.IsNullOrEmpty(m)) return;
            string[] p = m.Split('|');
            switch (p[0])
            {
                case "drag":
                    BeginGroup();
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // regresa al soltar el mouse
                    grp = null;
                    break;
                case "wreset":
                    modBounds.Clear(); SaveModBounds();
                    {   // ORDEN ORIGINAL: si el reproductor quedó fuera de las pantallas (monitor desconectado) regresa a la principal
                        bool vis = false;
                        foreach (Screen sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(new Rectangle(Left + 20, Top + 5, Math.Max(60, Width - 40), 20))) vis = true;
                        if (!vis && !docked && !full) { Rectangle wa = Screen.PrimaryScreen.WorkingArea; wantPos = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 80); KeepOnScreen(); }
                    }
                    break;
                case "snap":
                    if (p.Length >= 2) snapOn = p[1] == "1";
                    break;
                case "wdrag":
                    if (p.Length >= 2)
                    {
                        ModWin w;
                        if (mods.TryGetValue(p[1], out w) && !w.IsDisposed && !w.Full)
                        {
                            ReleaseCapture();
                            SendMessage(w.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                        }
                    }
                    break;
                case "wmove0":
                    if (p.Length >= 2)
                    {
                        ModWin w;
                        if (mods.TryGetValue(p[1], out w) && !w.IsDisposed) w.MoveStart = w.Location;
                    }
                    break;
                case "wmove":
                    if (p.Length >= 5)
                    {
                        ModWin w;
                        if (mods.TryGetValue(p[1], out w) && !w.IsDisposed && !w.Full)
                        {
                            float dpr = ParseF(p[4]); if (dpr <= 0) dpr = 1f;
                            Rectangle r = new Rectangle(new Point(w.MoveStart.X + (int)Math.Round(ParseF(p[2]) * dpr), w.MoveStart.Y + (int)Math.Round(ParseF(p[3]) * dpr)), w.Size);
                            if (snapOn) { Point o = Snap.Offset(r, Targets(w, null), Areas(), SnapDist()); r.Offset(o); }
                            w.Location = r.Location;
                        }
                    }
                    break;
                case "wsize":
                    if (p.Length >= 5)
                    {
                        ModWin w;
                        if (mods.TryGetValue(p[1], out w) && !w.IsDisposed && !w.Full)
                        {
                            float dpr = ParseF(p[4]); if (dpr <= 0) dpr = 1f;
                            int ww = (int)Math.Round(ParseF(p[2]) * dpr), hh = (int)Math.Round(ParseF(p[3]) * dpr);
                            Size sz = new Size(Math.Max((int)(260 * dpr), ww), Math.Max((int)(60 * dpr), hh));
                            if (snapOn && placed) { Size g = Snap.Grow(new Rectangle(w.Location, sz), Targets(w, null), Areas(), SnapDist()); sz = new Size(Math.Max((int)(260 * dpr), sz.Width + g.Width), Math.Max((int)(60 * dpr), sz.Height + g.Height)); }
                            w.ClientSize = sz;
                        }
                    }
                    break;
                case "ready":
                    pageReady = true; ShowSoon();
                    break;
                case "size":
                    if (p.Length >= 4) SetCssSize(ParseF(p[1]), ParseF(p[2]), ParseF(p[3]));
                    break;
                case "ghost":
                    if (p.Length >= 2) SetGhost(p[1] == "1");
                    break;
                case "alpha":
                    if (p.Length >= 2)
                    {
                        int a = (int)(ParseF(p[1]) * 2.55f);
                        ghostAlpha = (byte)Math.Max(40, Math.Min(255, a));
                        if (ghost) ApplyGhost(true);
                    }
                    break;
                case "top":
                    if (p.Length >= 2) SetTop(p[1] == "1");
                    break;
                case "min":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "close":
                    Close();
                    break;
                case "dock":
                    if (p.Length >= 2) DockToTaskbar(p[1] == "1");
                    break;
                case "notify":   // aviso de Windows junto al reloj (p. ej. empieza un programa de la guía)
                    if (p.Length >= 3) { try { tray.ShowBalloonTip(15000, p[1], m.Substring(p[0].Length + p[1].Length + 2), ToolTipIcon.Info); } catch { } }
                    break;
                case "mods":   // mini reproductor: esconde las ventanas sueltas y al regresar las muestra como estaban
                    if (p.Length >= 2) ShowMods(p[1] == "1");
                    break;
                case "title":
                    {
                        string t = m.Length > 6 ? m.Substring(6) : "";
                        Text = t.Length > 0 ? t : "RetroAmp";
                        try { tray.Text = t.Length > 60 ? t.Substring(0, 60) + "..." : (t.Length > 0 ? t : "RetroAmp"); } catch { }
                        if (tb != null) { try { tb.SetThumbnailTooltip(Handle, Text); } catch { } }
                    }
                    break;
                case "state":
                    if (p.Length >= 2)
                    {
                        bool now = p[1] == "play";
                        if (now != playing) { playing = now; UpdateThumbButtons(); }
                    }
                    break;
                case "files":
                    // Archivos o carpetas soltados sobre el reproductor: WebView2 da sus rutas reales
                    try
                    {
                        System.Collections.Generic.List<string> paths = new System.Collections.Generic.List<string>();
                        System.Collections.Generic.IReadOnlyList<object> objs = e.AdditionalObjects;
                        if (objs != null)
                            for (int i = 0; i < objs.Count; i++)
                            {
                                CoreWebView2File f = objs[i] as CoreWebView2File;
                                if (f != null && !string.IsNullOrEmpty(f.Path)) paths.Add(f.Path);
                            }
                        if (paths.Count > 0) Post("files|" + string.Join("\n", paths.ToArray()));
                    }
                    catch { }
                    break;
                case "prog":
                    if (p.Length >= 4) SetProgress(ParseF(p[1]), ParseF(p[2]), p[3]);
                    break;
                case "webtv":   // canal con página oficial (TV Azteca): se ve encima de la pantalla de Visualización
                    if (p.Length >= 9 && p[1] == "show") WebTvShow(p[8], ParseF(p[2]), ParseF(p[3]), ParseF(p[4]), ParseF(p[5]), ParseF(p[6]), p[7]);
                    else if (p.Length >= 3 && p[1] == "fill") fillOn = p[2] == "1";
                    else WebTvOff(p.Length >= 2 && p[1] == "hide");
                    break;
            }
        }

        // Pantalla completa (video o efectos): la ventana ocupa todo el monitor y luego vuelve a su lugar
        bool full, fullPrevTop;
        Rectangle fullPrev;
        void SetFull(bool on)
        {
            if (on == full) return;
            if (on)
            {
                fullPrev = Bounds; fullPrevTop = TopMost;
                full = true;
                Bounds = Screen.FromControl(this).Bounds;
                TopMost = true;
            }
            else
            {
                full = false;
                TopMost = fullPrevTop;
                Bounds = fullPrev;
            }
        }

        bool placed, pageReady; Timer settleT;
        // Primer tamaño de la página: se espera un momento por si llega otro (ya con todo acomodado) y luego se muestra
        void ShowSoon()
        {
            if (placed || !pageReady) return;   // se muestra cuando la página ya acomodó todo (mensaje "ready")
            if (settleT == null) { settleT = new Timer(); settleT.Interval = 350; settleT.Tick += delegate { settleT.Stop(); ShowPlaced(); }; }
            settleT.Stop(); settleT.Start();
        }
        void ShowPlaced()
        {
            if (placed || IsDisposed) return;
            placed = true;
            Point before = Location;
            if (docked) PlaceDocked(); else KeepOnScreen();
            int dx = Left - before.X, dy = Top - before.Y;
            foreach (ModWin w in waitMods) { try { if (!w.IsDisposed) w.Location = new Point(w.Left + dx, w.Top + dy); } catch { } }
            waitMods.Clear();
        }
        readonly System.Collections.Generic.List<ModWin> waitMods = new System.Collections.Generic.List<ModWin>();

        void SetCssSize(float w, float h, float dpr)
        {
            if (full) { ShowSoon(); return; }
            if (dpr <= 0) dpr = 1f;
            int pw = (int)Math.Ceiling(w * dpr), ph = (int)Math.Ceiling(h * dpr);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            pw = Math.Max(120, Math.Min(pw, wa.Width));
            ph = Math.Max(20, Math.Min(ph, wa.Height));
            if (ClientSize.Width != pw || ClientSize.Height != ph)
            {
                ClientSize = new Size(pw, ph);
                if (!placed) ShowSoon(); else if (docked) PlaceDocked(); else KeepOnScreen();
            }
            else ShowSoon();
        }

        // ---------- Modo fantasma ----------
        void SetGhost(bool on)
        {
            ghost = on;
            if (on) SetTop(true);
            ApplyGhost(true);
            miGhost.Checked = on;
            Post("ghost|" + (on ? "1" : "0"));
        }

        void ApplyGhost(bool force)
        {
            bool held = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool want = ghost && !held;
            if (!force && want == clickThrough) return;
            clickThrough = want;
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            if (ghost)
            {
                ex |= WS_EX_LAYERED;
                if (want) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
                SetWindowLong(Handle, GWL_EXSTYLE, ex);
                SetLayeredWindowAttributes(Handle, 0, want ? ghostAlpha : (byte)255, 2);
            }
            else
            {
                ex &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
                SetWindowLong(Handle, GWL_EXSTYLE, ex);
            }
            Post("ghostlive|" + (want ? "1" : "0"));
        }

        void SetTop(bool on)
        {
            TopMost = on;
            miTop.Checked = on;
            Post("top|" + (on ? "1" : "0"));
        }

        // ---------- Mini reproductor acoplado junto a la barra de tareas ----------
        void DockToTaskbar(bool on)
        {
            // al abrir el programa todavía espera fuera de la pantalla: solo se anota y ShowPlaced lo acomoda
            if (!placed) { docked = on; if (on) { undockPos = wantPos; hasUndockPos = true; } return; }
            if (on)
            {
                if (!docked) { undockPos = Location; hasUndockPos = true; }
                docked = true;
                PlaceDocked();
            }
            else
            {
                docked = false;
                if (hasUndockPos) Location = undockPos;
                KeepOnScreen();
            }
        }

        void PlaceDocked()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Location = new Point(wa.Right - Width - 8, wa.Bottom - Height - 4);
        }

        // Donde el usuario dejó el reproductor. Si un momento la ventana mide más (al abrir, antes de separar
        // las demás ventanas) se sube para caber, pero al encogerse regresa a su lugar.
        Point wantPos; bool placing; Size savedSize;
        void KeepOnScreen()
        {
            Rectangle wa = Screen.FromPoint(new Point(wantPos.X + 20, wantPos.Y + 10)).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(wantPos.X, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(wantPos.Y, wa.Bottom - Height));
            if (x != Left || y != Top) { placing = true; try { Location = new Point(x, y); } finally { placing = false; } }
        }

        Point LoadPos()
        {
            try
            {
                string f = Path.Combine(ConfDir ?? root, "ventana.txt");
                if (File.Exists(f))
                {
                    string[] a = File.ReadAllText(f).Trim().Split(',');
                    Point pt = new Point(int.Parse(a[0]), int.Parse(a[1]));
                    int sw, sh;
                    if (a.Length >= 4 && int.TryParse(a[2], out sw) && int.TryParse(a[3], out sh) && sw >= 120 && sh >= 20 && sw <= 10000 && sh <= 10000) savedSize = new Size(sw, sh);
                    foreach (Screen s in Screen.AllScreens)
                        if (s.WorkingArea.Contains(new Point(pt.X + 40, pt.Y + 10))) return pt;
                }
            }
            catch { }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Left + (wa.Width - 470) / 2, wa.Top + 80);
        }

        void SavePos()
        {
            try
            {
                Point pt = docked && hasUndockPos ? undockPos : wantPos;
                Size cs = full || !placed ? savedSize : ClientSize;
                File.WriteAllText(Path.Combine(ConfDir ?? root, "ventana.txt"), pt.X + "," + pt.Y + (cs.Width > 0 ? "," + cs.Width + "," + cs.Height : ""));
            }
            catch { }
        }

        // ---------- Ventanas separadas (lista, ecualizador, video, biblioteca...) ----------
        async void OnNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs a)
        {
            string name = "";
            try { name = a.Name ?? ""; } catch { }
            if (!name.StartsWith("wyt_"))
            {
                a.Handled = true;
                try { System.Diagnostics.Process.Start(a.Uri); } catch { }
                return;
            }
            CoreWebView2Deferral d = a.GetDeferral();
            try
            {
                ModWin old;
                if (mods.TryGetValue(name, out old) && !old.IsDisposed) { try { old.Close(); } catch { } }
                ModWin w = new ModWin(name);
                w.Icon = Icon;
                w.Snapper = delegate (Rectangle rr) { if (!snapOn) return rr; Point o = Snap.Offset(rr, Targets(w, null), Areas(), SnapDist()); rr.Offset(o); return rr; };
                Rectangle sb = ModStartBounds(name, a.WindowFeatures);
                // al abrir el programa, las ventanas sueltas esperan fuera de la pantalla junto con la principal
                if (!placed) { sb.Offset(Left - wantPos.X, Top - wantPos.Y); waitMods.Add(w); }
                w.Bounds = sb;
                w.Show(this);
                await w.Web.EnsureCoreWebView2Async(env0);
                CoreWebView2Settings st = w.Web.CoreWebView2.Settings;
                st.AreDefaultContextMenusEnabled = false;
                st.AreDevToolsEnabled = false;
                st.IsStatusBarEnabled = false;
                st.IsZoomControlEnabled = false;
                TrySetBool(st, "AreBrowserAcceleratorKeysEnabled", false);
                TrySetBool(st, "IsSwipeNavigationEnabled", false);
                w.Web.CoreWebView2.ContainsFullScreenElementChanged += delegate { w.SetFull(w.Web.CoreWebView2.ContainsFullScreenElement); };
                w.Web.CoreWebView2.WindowCloseRequested += delegate { try { w.Close(); } catch { } };
                w.Web.CoreWebView2.NewWindowRequested += delegate (object s3, CoreWebView2NewWindowRequestedEventArgs a3) { a3.Handled = true; };
                w.FormClosing += delegate { if (tvWeb != null && tvWeb.Parent == w) WebTvOff(true); };
                w.FormClosed += delegate
                {
                    try { if (!w.Full) modBounds[name] = w.Bounds; } catch { }
                    ModWin cur;
                    if (mods.TryGetValue(name, out cur) && cur == w) mods.Remove(name);
                    SaveModBounds();
                };
                mods[name] = w;
                if (modsHidden)
                {   // se abrió estando en mini reproductor: se esconde cuando termine de cargar
                    if (!shadeHid.Contains(w)) shadeHid.Add(w);
                    Timer th = new Timer(); th.Interval = 1500;
                    th.Tick += delegate { th.Stop(); th.Dispose(); if (modsHidden && !w.IsDisposed) { try { w.Hide(); } catch { } } };
                    th.Start();
                }
                a.NewWindow = w.Web.CoreWebView2;
                a.Handled = true;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(root, "registro.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Ventana separada: " + ex.Message + Environment.NewLine); } catch { }
                a.Handled = true;
            }
            finally { d.Complete(); }
        }

        // ---------- Página oficial de un canal (TV Azteca) dentro de Visualización ----------
        // Usa su propio perfil (datos\perfil-web), sin puente de mensajes con el programa: la página no puede tocar nada nuestro.
        WebView2 tvWeb; CoreWebView2Environment tvEnv; bool tvBusy, escDown; string tvUrl = "", tvLoaded = "";
        Rectangle tvRect; Control tvHost;
        async void WebTvShow(string u, float x, float y, float w, float h, float dpr, string target)
        {
            if (!(u.StartsWith("https://") || u.StartsWith("http://"))) return;
            if (dpr <= 0) dpr = 1f;
            Control par = this;
            if (target.Length > 0) { ModWin mw; if (mods.TryGetValue(target, out mw) && !mw.IsDisposed) par = mw; else return; }
            tvUrl = u; tvHost = par;
            tvRect = new Rectangle((int)Math.Round(x * dpr), (int)Math.Round(y * dpr), Math.Max(1, (int)Math.Round(w * dpr)), Math.Max(1, (int)Math.Round(h * dpr)));
            if (tvWeb == null)
            {
                if (tvBusy) return;
                tvBusy = true;
                try
                {
                    WebView2 wv = new WebView2();
                    try { wv.DefaultBackgroundColor = Color.Black; } catch { }
                    wv.Visible = false;
                    Controls.Add(wv);
                    if (tvEnv == null)
                    {
                        CoreWebView2EnvironmentOptions o = new CoreWebView2EnvironmentOptions();
                        o.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                        tvEnv = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ConfDir ?? root, "web-envivo"), o);   // media\perfil\web-envivo
                    }
                    await wv.EnsureCoreWebView2Async(tvEnv);
                    CoreWebView2Settings st = wv.CoreWebView2.Settings;
                    st.AreDevToolsEnabled = false;
                    st.IsStatusBarEnabled = false;
                    st.IsWebMessageEnabled = false;
                    TrySetBool(st, "IsSwipeNavigationEnabled", false);
                    TrySetBool(st, "IsPasswordAutosaveEnabled", false);
                    TrySetBool(st, "IsGeneralAutofillEnabled", false);
                    wv.CoreWebView2.NewWindowRequested += delegate (object s3, CoreWebView2NewWindowRequestedEventArgs a3)
                    {   // enlaces que abren ventana: los de TV Azteca se quedan aquí, los demás van al navegador
                        a3.Handled = true;
                        try
                        {
                            Uri nu = new Uri(a3.Uri);
                            string cur = ""; try { cur = new Uri(wv.CoreWebView2.Source).Host; } catch { }
                            if (SameSite(nu.Host, cur)) wv.CoreWebView2.Navigate(a3.Uri);
                            else if (nu.Scheme == "https" || nu.Scheme == "http") System.Diagnostics.Process.Start(a3.Uri);
                        }
                        catch { }
                    };
                    // su botón de pantalla completa llena solo el recuadro de Visualización (no el monitor)
                    wv.CoreWebView2.NavigationCompleted += delegate { if (tvLoaded.Length > 0) WebTvFillStart(); };
                    tvWeb = wv;
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(Path.Combine(root, "registro.txt"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Página del canal: " + ex.Message + Environment.NewLine); } catch { }
                    Post("webtv|err|" + ex.Message.Replace("|", " "));
                    tvBusy = false; return;
                }
                tvBusy = false;
            }
            if (tvUrl.Length == 0) return;   // se apagó mientras arrancaba
            WebTvPlace();
            if (tvLoaded != tvUrl) { tvLoaded = tvUrl; try { tvWeb.CoreWebView2.Navigate(tvUrl); } catch { } }
        }
        void WebTvPlace()
        {
            if (tvWeb == null || tvHost == null || tvHost.IsDisposed) return;
            if (tvWeb.Parent != tvHost) tvWeb.Parent = tvHost;
            tvWeb.Dock = DockStyle.None; tvWeb.Bounds = tvRect;
            tvWeb.Visible = true; tvWeb.BringToFront();
        }
        // Al abrir un canal, su reproductor se pone en "pantalla completa" dentro del recuadro: el video llena Visualización.
        // Se busca el video más grande y el bloque que lo contiene con sus botones (pausa, volumen...).
        const string FillJs = "(()=>{if(document.fullscreenElement)return 'ok';const vs=[...document.querySelectorAll('video')].filter(v=>v.offsetWidth>80&&v.offsetHeight>45);if(!vs.length)return 'no';" +
            "vs.sort((a,b)=>b.offsetWidth*b.offsetHeight-a.offsetWidth*a.offsetHeight);const v=vs[0];let c=v.parentElement,best=c;" +
            "for(let i=0;i<8&&c&&c!==document.body;i++){if(c.offsetWidth>v.offsetWidth*1.6||c.offsetHeight>v.offsetHeight*1.6)break;best=c;if(c.querySelector('button'))break;c=c.parentElement;}" +
            "try{best.requestFullscreen().catch(()=>{});}catch(e){}return 'try';})()";
        Timer fillT; int fillLeft;
        void WebTvFillStart()
        {
            if (!fillOn) return;
            fillLeft = 40;
            if (fillT == null) { fillT = new Timer(); fillT.Interval = 750; fillT.Tick += delegate { WebTvFillTry(); }; }
            fillT.Stop(); fillT.Start();
        }
        async void WebTvFillTry()
        {
            if (tvWeb == null || !tvWeb.Visible || tvLoaded.Length == 0 || --fillLeft < 0) { fillT.Stop(); return; }
            try
            {
                string r = await tvWeb.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    "{\"expression\":" + JsStr(FillJs) + ",\"userGesture\":true,\"returnByValue\":true}");
                if (r != null && r.Contains("\"ok\"")) fillT.Stop();
            }
            catch { }
        }
        static bool SameSite(string a, string b)
        {   // mismo sitio: coinciden las dos últimas partes del nombre (envivo.tvazteca.com ~ www.tvazteca.com)
            string[] x = a.ToLowerInvariant().Split('.'), y = b.ToLowerInvariant().Split('.');
            if (x.Length < 2 || y.Length < 2) return false;
            return x[x.Length - 1] == y[y.Length - 1] && x[x.Length - 2] == y[y.Length - 2];
        }
        static string JsStr(string t)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder("\"");
            foreach (char ch in t) { if (ch == '"' || ch == '\\') b.Append('\\').Append(ch); else if (ch < ' ') b.AppendFormat("\\u{0:x4}", (int)ch); else b.Append(ch); }
            return b.Append('"').ToString();
        }
        bool fillOn = true;
        // hide: se esconde y deja de sonar, pero recuerda el canal; off: se olvida del canal
        void WebTvOff(bool hide)
        {
            tvUrl = "";
            if (tvWeb == null) return;
            if (fillT != null) fillT.Stop();
            tvWeb.Visible = false;
            if (tvWeb.Parent != this) tvWeb.Parent = this;   // si la ventana suelta se cierra, la página no se pierde con ella
            tvLoaded = "";
            try { tvWeb.CoreWebView2.Navigate("about:blank"); } catch { }
        }

        Rectangle ModStartBounds(string name, CoreWebView2WindowFeatures f)
        {
            Rectangle r;
            if (modBounds.TryGetValue(name, out r))
                foreach (Screen s in Screen.AllScreens)
                    if (s.WorkingArea.IntersectsWith(new Rectangle(r.X + 20, r.Y + 5, 60, 20))) return r;
            float dpr = 1f; try { using (Graphics g = CreateGraphics()) dpr = g.DpiX / 96f; } catch { }
            int w = 470, h = 300, x = Right + 8, y = Top;
            try
            {
                if (f != null && f.HasSize) { w = (int)(f.Width * dpr); h = (int)(f.Height * dpr); }
                if (f != null && f.HasPosition) { x = (int)(f.Left * dpr); y = (int)(f.Top * dpr); }
            }
            catch { }
            if (!placed) { x += wantPos.X - Left; y += wantPos.Y - Top; }   // la ventana principal todavía espera fuera de la pantalla
            Rectangle wa = placed ? Screen.FromControl(this).WorkingArea : Screen.FromPoint(new Point(wantPos.X + 20, wantPos.Y + 10)).WorkingArea;
            w = Math.Min(Math.Max(200, w), wa.Width); h = Math.Min(Math.Max(80, h), wa.Height);
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w)); y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        void LoadModBounds()
        {
            try
            {
                string f = Path.Combine(ConfDir ?? root, "ventanas.txt");
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f))
                {
                    string[] a = line.Split(',');
                    if (a.Length != 5) continue;
                    modBounds[a[0]] = new Rectangle(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
                }
            }
            catch { }
        }

        void SaveModBounds()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (System.Collections.Generic.KeyValuePair<string, Rectangle> kv in modBounds)
                    sb.Append(kv.Key).Append(',').Append(kv.Value.X).Append(',').Append(kv.Value.Y).Append(',').Append(kv.Value.Width).Append(',').Append(kv.Value.Height).Append("\r\n");
                File.WriteAllText(Path.Combine(ConfDir ?? root, "ventanas.txt"), sb.ToString());
            }
            catch { }
        }

        // ---------- Barra de tareas: botones en la miniatura y progreso ----------
        void InitTaskbar()
        {
            try
            {
                if (tb == null) { tb = (ITaskbarList3)new TaskbarListCom(); tb.HrInit(); }
                THUMBBUTTON[] b = MakeButtons();
                int hr = tb.ThumbBarAddButtons(Handle, (uint)b.Length, b);
                if (hr != 0) tb.ThumbBarUpdateButtons(Handle, (uint)b.Length, b);
                thumbsAdded = true;
            }
            catch { }
        }

        THUMBBUTTON Btn(uint id, IntPtr ico, string tip)
        {
            THUMBBUTTON t = new THUMBBUTTON();
            t.dwMask = 2 | 4 | 8; // icono, texto de ayuda, flags
            t.iId = id;
            t.hIcon = ico;
            t.szTip = tip;
            t.dwFlags = 0;
            return t;
        }

        THUMBBUTTON[] MakeButtons()
        {
            THUMBBUTTON[] b = new THUMBBUTTON[3];
            b[0] = Btn(1, icoPrev, "Anterior");
            b[1] = Btn(2, playing ? icoPause : icoPlay, playing ? "Pausa" : "Reproducir");
            b[2] = Btn(3, icoNext, "Siguiente");
            return b;
        }

        void UpdateThumbButtons()
        {
            if (tb == null || !thumbsAdded) return;
            try { THUMBBUTTON[] b = MakeButtons(); tb.ThumbBarUpdateButtons(Handle, (uint)b.Length, b); } catch { }
        }

        void SetProgress(float cur, float dur, string st)
        {
            if (tb == null) return;
            try
            {
                if (st == "stop" || dur <= 0) { tb.SetProgressState(Handle, 0); return; }
                tb.SetProgressState(Handle, st == "pause" ? 8 : 2);
                tb.SetProgressValue(Handle, (ulong)Math.Max(0, cur * 10), (ulong)Math.Max(1, dur * 10));
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (msgTaskbarCreated != 0 && m.Msg == (int)msgTaskbarCreated)
            {
                InitTaskbar();
            }
            else if (m.Msg == 0x0111) // WM_COMMAND (botones de la miniatura)
            {
                long wp = m.WParam.ToInt64();
                int hi = (int)((wp >> 16) & 0xFFFF), lo = (int)(wp & 0xFFFF);
                if (hi == 0x1800)
                {
                    if (lo == 1) Post("cmd|prev");
                    else if (lo == 2) Post("cmd|toggle");
                    else if (lo == 3) Post("cmd|next");
                    return;
                }
            }
            else if (m.Msg == 0x0312) // WM_HOTKEY (teclas multimedia)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) Post("cmd|toggle");
                else if (id == 2) Post("cmd|next");
                else if (id == 3) Post("cmd|prev");
                else if (id == 4) Post("cmd|stop");
                else if (id == 5) SetGhost(!ghost);
            }
            else if (m.Msg == 0xA3) // doble clic en la barra de titulo: no maximizar
            {
                return;
            }
            else if (m.Msg == 0x0216 && snapOn && WindowState == FormWindowState.Normal) // WM_MOVING: imanes al mover la ventana principal
            {
                RECT rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Rectangle r = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                Point o = Snap.Offset(r, Targets(this, grp), Areas(), SnapDist());
                rc.Left += o.X; rc.Right += o.X; rc.Top += o.Y; rc.Bottom += o.Y;
                Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- Imanes entre ventanas ----------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        int SnapDist() { float dpr = 1f; try { using (Graphics g = CreateGraphics()) dpr = g.DpiX / 96f; } catch { } return (int)Math.Round(12 * dpr); }
        System.Collections.Generic.List<Rectangle> Areas()
        {
            System.Collections.Generic.List<Rectangle> l = new System.Collections.Generic.List<Rectangle>();
            foreach (Screen s in Screen.AllScreens) l.Add(s.WorkingArea);
            return l;
        }
        // Las demás ventanas visibles a las que se puede pegar (sin la que se mueve ni las que van con ella)
        System.Collections.Generic.List<Rectangle> Targets(Form self, System.Collections.Generic.List<ModWin> skip)
        {
            System.Collections.Generic.List<Rectangle> l = new System.Collections.Generic.List<Rectangle>();
            if (self != this && Visible && WindowState == FormWindowState.Normal && !docked) l.Add(Bounds);
            foreach (ModWin w in mods.Values)
                if (w != self && !w.IsDisposed && w.Visible && !w.Full && w.WindowState == FormWindowState.Normal && (skip == null || !skip.Contains(w))) l.Add(w.Bounds);
            return l;
        }
        // Al empezar a mover la ventana principal: las que están pegadas a ella se mueven juntas
        void BeginGroup()
        {
            grp = new System.Collections.Generic.List<ModWin>(); grpLast = Location;
            if (!snapOn || docked) return;
            System.Collections.Generic.List<ModWin> all = new System.Collections.Generic.List<ModWin>();
            System.Collections.Generic.List<Rectangle> rs = new System.Collections.Generic.List<Rectangle>();
            foreach (ModWin w in mods.Values) if (!w.IsDisposed && w.Visible && !w.Full && w.WindowState == FormWindowState.Normal) { all.Add(w); rs.Add(w.Bounds); }
            foreach (int i in Snap.Group(Bounds, rs)) grp.Add(all[i]);
        }
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!placing && !docked && WindowState == FormWindowState.Normal && IsHandleCreated) wantPos = Location;
            if (grp == null || grp.Count == 0) { grpLast = Location; return; }
            int dx = Location.X - grpLast.X, dy = Location.Y - grpLast.Y; grpLast = Location;
            if (dx == 0 && dy == 0) return;
            foreach (ModWin w in grp) { try { if (!w.IsDisposed) w.Location = new Point(w.Left + dx, w.Top + dy); } catch { } }
        }

        // ---------- Icono junto al reloj ----------
        void BuildTray()
        {
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add(new ToolStripMenuItem("Reproducir / Pausa", null, delegate { Post("cmd|toggle"); }));
            cm.Items.Add(new ToolStripMenuItem("Siguiente", null, delegate { Post("cmd|next"); }));
            cm.Items.Add(new ToolStripMenuItem("Anterior", null, delegate { Post("cmd|prev"); }));
            cm.Items.Add(new ToolStripSeparator());
            miGhost = new ToolStripMenuItem("Modo fantasma   (Ctrl+Alt+F)", null, delegate { SetGhost(!ghost); });
            miTop = new ToolStripMenuItem("Siempre visible", null, delegate { SetTop(!TopMost); });
            cm.Items.Add(miGhost);
            cm.Items.Add(miTop);
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(new ToolStripMenuItem("Mostrar reproductor", null, delegate { ShowMe(); }));
            cm.Items.Add(new ToolStripMenuItem("Salir", null, delegate { Close(); }));
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "RetroAmp";
            tray.ContextMenuStrip = cm;
            tray.Visible = true;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMe(); };
        }

        void ShowMe()
        {
            if (ghost) SetGhost(false);
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            KeepOnScreen();
            Activate();
            BringToFront();
        }

        void OnTick(object sender, EventArgs e)
        {
            if (ghost) ApplyGhost(false);
            if (tvWeb != null && tvWeb.Visible && tvHost != null)
            {   // con COMPLETA (todo el monitor) el teclado lo tiene la página del canal: Esc se avisa al programa
                ModWin mw = tvHost as ModWin;
                bool isFull = mw != null ? mw.Full : full;
                bool esc = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
                if (esc && !escDown && isFull && Form.ActiveForm == (Form)tvHost.FindForm()) Post("webtv|esc");
                escDown = esc;
            }
            try
            {
                object sv = ctx["Show"];
                if (sv is bool && (bool)sv) { ctx["Show"] = false; ShowMe(); }
                object st = ctx["Stop"];
                if (st is bool && (bool)st) Close();
            }
            catch { }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            SavePos();
            foreach (ModWin w in mods.Values) { try { if (!w.IsDisposed && !w.Full) modBounds[w.ModName] = w.Bounds; } catch { } }
            SaveModBounds();
            try { timer.Stop(); } catch { }
            for (int i = 1; i <= 5; i++) { try { UnregisterHotKey(Handle, i); } catch { } }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            try { ctx["Stop"] = true; } catch { }
        }

        // ---------- Iconos dibujados ----------
        static IntPtr MakeIcon(int kind)
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Brush br = Brushes.White;
                    if (kind == 0)
                    {
                        g.FillRectangle(br, 2, 3, 2, 10);
                        g.FillPolygon(br, new Point[] { new Point(13, 3), new Point(13, 13), new Point(5, 8) });
                    }
                    else if (kind == 1)
                    {
                        g.FillPolygon(br, new Point[] { new Point(4, 2), new Point(4, 14), new Point(14, 8) });
                    }
                    else if (kind == 2)
                    {
                        g.FillRectangle(br, 3, 3, 4, 10);
                        g.FillRectangle(br, 9, 3, 4, 10);
                    }
                    else
                    {
                        g.FillPolygon(br, new Point[] { new Point(3, 3), new Point(3, 13), new Point(11, 8) });
                        g.FillRectangle(br, 12, 3, 2, 10);
                    }
                }
                return bmp.GetHicon();
            }
        }

        static IntPtr MakeAppIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (Brush bg = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(74, 74, 99), Color.FromArgb(25, 25, 36), 90f))
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    using (Pen pen = new Pen(Color.FromArgb(216, 184, 80), 2f))
                        g.DrawEllipse(pen, 2, 2, 28, 28);
                    int[] hs = new int[] { 8, 14, 20, 12, 17 };
                    using (Brush green = new SolidBrush(Color.FromArgb(0, 230, 0)))
                        for (int i = 0; i < hs.Length; i++)
                            g.FillRectangle(green, 7 + i * 4, 24 - hs[i], 3, hs[i]);
                }
                return bmp.GetHicon();
            }
        }
    }

    // Ventana sin bordes para un módulo separado; la página la mueve y cambia su tamaño
    public class ModWin : Form
    {
        public WebView2 Web;
        public string ModName;
        public bool Full;
        public Point MoveStart;
        public Func<Rectangle, Rectangle> Snapper;
        Rectangle prev; bool prevTop;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0216 && Snapper != null && !Full) // WM_MOVING: imanes
            {
                Host.RECT rc = (Host.RECT)Marshal.PtrToStructure(m.LParam, typeof(Host.RECT));
                Rectangle r = Snapper(Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom));
                rc.Left = r.Left; rc.Top = r.Top; rc.Right = r.Right; rc.Bottom = r.Bottom;
                Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        public ModWin(string name)
        {
            ModName = name;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(17, 17, 24);
            Text = "RetroAmp";
            MinimumSize = new Size(200, 60);
            Web = new WebView2();
            Web.Dock = DockStyle.Fill;
            try { Web.DefaultBackgroundColor = Color.FromArgb(17, 17, 24); } catch { }
            Controls.Add(Web);
        }

        public void SetFull(bool on)
        {
            if (on == Full) return;
            if (on) { prev = Bounds; prevTop = TopMost; Full = true; Bounds = Screen.FromControl(this).Bounds; TopMost = true; }
            else { Full = false; TopMost = prevTop; Bounds = prev; }
        }
    }
}
