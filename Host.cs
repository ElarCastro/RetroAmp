// RetroAmp - ventana propia de Windows (WinForms + WebView2)
// Compilado dentro de RetroAmp.exe (C# 5, .NET Framework 4.x)
using System;
using System.Collections.Generic;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
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
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;

        WebView2 web;
        string url, dataDir, root;
        bool ready, ghost, clickThrough, playing, docked, thumbsAdded;
        byte ghostAlpha = 190;
        Point undockPos;
        bool hasUndockPos;
        Timer timer;
        NotifyIcon tray;
        ToolStripMenuItem miGhost, miTop, miAuto;
        bool startMin;
        ITaskbarList3 tb;
        uint msgTaskbarCreated;
        IntPtr icoPrev, icoPlay, icoPause, icoNext;
        public bool InitFailed;
        public string InitError;

        public Host(string url, string dataDir, string root, bool startMinimized)
        {
            this.url = url; this.dataDir = dataDir; this.root = root;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(17, 17, 24);
            Text = "RetroAmp";
            ShowInTaskbar = true;
            ClientSize = new Size(470, 150);
            Icon = LoadAppIcon(root, SystemInformation.IconSize) ?? Icon.FromHandle(MakeAppIcon());
            icoPrev = MakeIcon(0); icoPlay = MakeIcon(1); icoPause = MakeIcon(2); icoNext = MakeIcon(3);
            Location = LoadPos();
            startMin = startMinimized;
            if (startMin) WindowState = FormWindowState.Minimized;
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
            HandleCreated += delegate
            {
                // Windows 11: esquinas redondeadas en la ventana sin bordes
                try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            };
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

        public static string Run(string url, string dataDir, string root, string loaderDir, bool startMinimized)
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
            Host f = new Host(url, dataDir, root, startMinimized);
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
                web.CoreWebView2.NewWindowRequested += delegate (object s2, CoreWebView2NewWindowRequestedEventArgs a)
                {
                    a.Handled = true;
                    try { System.Diagnostics.Process.Start(a.Uri); } catch { }
                };
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
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
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
                case "hello":
                    Post("autostart|" + (IsAutoStart() ? "1" : "0"));
                    Post("ghost|" + (ghost ? "1" : "0"));
                    break;
                case "autostart":
                    if (p.Length >= 2) SetAutoStart(p[1] == "1");
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
                case "prog":
                    if (p.Length >= 4) SetProgress(ParseF(p[1]), ParseF(p[2]), p[3]);
                    break;
            }
        }

        void SetCssSize(float w, float h, float dpr)
        {
            if (dpr <= 0) dpr = 1f;
            int pw = (int)Math.Ceiling(w * dpr), ph = (int)Math.Ceiling(h * dpr);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            pw = Math.Max(120, Math.Min(pw, wa.Width));
            ph = Math.Max(20, Math.Min(ph, wa.Height));
            if (ClientSize.Width != pw || ClientSize.Height != ph)
            {
                ClientSize = new Size(pw, ph);
                if (docked) PlaceDocked(); else KeepOnScreen();
            }
        }

        // ---------- Modo fantasma ----------
        void SetGhost(bool on)
        {
            ghost = on;
            // Ctrl+W desactiva el modo fantasma; solo se reserva mientras esta activo
            try
            {
                if (on) RegisterHotKey(Handle, 6, 0x4000 | 0x0002, 0x57);
                else UnregisterHotKey(Handle, 6);
            }
            catch { }
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

        // ---------- Iniciar con Windows ----------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        string LaunchCommand()
        {
            return "\"" + Path.Combine(root, "RetroAmp.exe") + "\" --inicio";
        }

        bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue("RetroAmp") != null;
            }
            catch { return false; }
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("RetroAmp", LaunchCommand());
                    else if (k.GetValue("RetroAmp") != null) k.DeleteValue("RetroAmp", false);
                }
            }
            catch { }
            bool now = IsAutoStart();
            try { miAuto.Checked = now; } catch { }
            Post("autostart|" + (now ? "1" : "0"));
        }

        // ---------- Mini reproductor acoplado junto a la barra de tareas ----------
        void DockToTaskbar(bool on)
        {
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

        void KeepOnScreen()
        {
            Rectangle wa = Screen.FromPoint(new Point(Left + 20, Top + 10)).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        Point LoadPos()
        {
            try
            {
                string f = Path.Combine(root, "ventana.txt");
                if (File.Exists(f))
                {
                    string[] a = File.ReadAllText(f).Trim().Split(',');
                    Point pt = new Point(int.Parse(a[0]), int.Parse(a[1]));
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
                Point pt = docked && hasUndockPos ? undockPos : Location;
                File.WriteAllText(Path.Combine(root, "ventana.txt"), pt.X + "," + pt.Y);
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
                else if (id == 6) { if (ghost) SetGhost(false); }
            }
            else if (m.Msg == 0xA3) // doble clic en la barra de titulo: no maximizar
            {
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- Icono junto al reloj ----------
        void BuildTray()
        {
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add(new ToolStripMenuItem("Reproducir / Pausa", null, delegate { Post("cmd|toggle"); }));
            cm.Items.Add(new ToolStripMenuItem("Siguiente", null, delegate { Post("cmd|next"); }));
            cm.Items.Add(new ToolStripMenuItem("Anterior", null, delegate { Post("cmd|prev"); }));
            cm.Items.Add(new ToolStripSeparator());
            miGhost = new ToolStripMenuItem("Modo fantasma   (Ctrl+Alt+F · salir: Ctrl+W)", null, delegate { SetGhost(!ghost); });
            miTop = new ToolStripMenuItem("Siempre visible", null, delegate { SetTop(!TopMost); });
            miAuto = new ToolStripMenuItem("Iniciar con Windows", null, delegate { SetAutoStart(!IsAutoStart()); });
            cm.Items.Add(miGhost);
            cm.Items.Add(miTop);
            cm.Items.Add(miAuto);
            cm.Opening += delegate { try { miAuto.Checked = IsAutoStart(); } catch { } };
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(new ToolStripMenuItem("Actualizar el motor de YouTube (yt-dlp)", null, delegate { UpdateYtdlp(); }));
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(new ToolStripMenuItem("Mostrar reproductor", null, delegate { ShowMe(); }));
            cm.Items.Add(new ToolStripMenuItem("Salir", null, delegate { Close(); }));
            tray = new NotifyIcon();
            tray.Icon = LoadAppIcon(root, SystemInformation.SmallIconSize) ?? Icon;
            tray.Text = "RetroAmp";
            tray.ContextMenuStrip = cm;
            tray.Visible = true;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMe(); };
        }

        // yt-dlp ya no se actualiza solo en segundo plano: el usuario lo pide desde el menu
        bool updating;
        void UpdateYtdlp()
        {
            if (updating) return;
            updating = true;
            Balloon("Buscando actualizaciones de yt-dlp...", ToolTipIcon.Info);
            Thread t = new Thread(delegate ()
            {
                string msg; ToolTipIcon icon = ToolTipIcon.Info;
                try
                {
                    Server.RunResult r = Server.RunTool(App.Ytdlp, new[] { "-U" }, 180000);
                    string all = (r.Out + "\n" + r.Err).Trim();
                    if (r.Code == 0)
                        msg = all.IndexOf("up to date", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "yt-dlp ya esta en la ultima version."
                            : "yt-dlp se actualizo correctamente.";
                    else { msg = "No se pudo actualizar yt-dlp: " + Server.ShortErr(all); icon = ToolTipIcon.Warning; }
                }
                catch (Exception ex) { msg = "No se pudo actualizar yt-dlp: " + ex.Message; icon = ToolTipIcon.Warning; }
                try { BeginInvoke((MethodInvoker)delegate { updating = false; Balloon(msg, icon); }); } catch { updating = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Balloon(string text, ToolTipIcon icon)
        {
            try { tray.ShowBalloonTip(4000, "RetroAmp", text, icon); } catch { }
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
            try
            {
                if (App.Show) { App.Show = false; ShowMe(); }
                if (App.Stop) Close();
            }
            catch { }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            SavePos();
            try { timer.Stop(); } catch { }
            for (int i = 1; i <= 6; i++) { try { UnregisterHotKey(Handle, i); } catch { } }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            App.Stop = true;
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

        static Icon LoadAppIcon(string root, Size size)
        {
            try
            {
                string f = Path.Combine(root, "RetroAmp.ico");
                if (File.Exists(f)) return new Icon(f, size);
            }
            catch { }
            return null;
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
}
