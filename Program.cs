using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Inkly - minimal, toolbar-free screen annotation tool.
//   Ctrl+Q  : start drawing on the screen under the cursor. Press again to cycle the color:
//             black -> red -> yellow -> green -> blue -> (back to black).
//   while drawing:
//     left-drag   : draw        wheel : thickness        Ctrl+wheel : zoom        middle-drag : pan
//     right-click : menu (Pin / Eraser / Clear / Reset zoom / Exit)
//   Capture is the single monitor under the cursor and excludes the taskbar (working area).
//   Pinned snip windows are normal minimizable windows and stay editable
//   (draw / erase / undo, Ctrl+S save PNG, Ctrl+C copy; Ctrl+2..6 set colors there).

namespace Inkly
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        static readonly IntPtr DPI_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

        static System.Threading.Mutex _mtx;

        [STAThread]
        static void Main()
        {
            // Per-Monitor-V2 DPI awareness, set before any window/Graphics exists. Without it a
            // system-DPI-aware process gets virtualized (wrong) coordinates on any monitor whose
            // scaling differs from the primary - which made screen capture land off-screen (black)
            // on a 100% secondary while the 150% primary worked. Falls back on older Windows.
            try { if (!SetProcessDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2)) SetProcessDPIAware(); }
            catch { try { SetProcessDPIAware(); } catch { } }

            // Single-instance guard: if Inkly is already running, exit immediately. Prevents multiple
            // copies (autostart + manual launches) from fighting over the global draw hotkey.
            bool createdNew;
            _mtx = new System.Threading.Mutex(true, @"Local\Inkly_SingleInstance_v1", out createdNew);
            if (!createdNew) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayAppContext());
            GC.KeepAlive(_mtx);
        }
    }

    static class Util
    {
        public static int DigitIndex(Keys k)
        {
            if (k >= Keys.D1 && k <= Keys.D6) return (int)(k - Keys.D1) + 1;
            if (k >= Keys.NumPad1 && k <= Keys.NumPad6) return (int)(k - Keys.NumPad1) + 1;
            return 0;
        }
        public static float Clamp(float v, float lo, float hi) { return Math.Max(lo, Math.Min(hi, v)); }
    }

    // For the snip windows' Ctrl+1..6 (1=black 2=red 3=green 4=blue 5=yellow 6=white).
    static class Palette
    {
        public static Color Get(int n)
        {
            switch (n)
            {
                case 1: return Color.Black;
                case 2: return Color.Red;
                case 3: return Color.LimeGreen;
                case 4: return Color.DodgerBlue;
                case 5: return Color.Gold;
                case 6: return Color.White;
                default: return Color.Red;
            }
        }
    }

    class Settings
    {
        public Color PenColor = Color.Black;
        public float BrushWidth = 4f;
        public float HiWidth = 18f;
        public bool Highlighter = false;
        public event Action Changed;

        public void NotifyChanged() { if (Changed != null) Changed(); }

        static string IniPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Inkly.ini"); } }

        static float ParseF(string v, float fb)
        {
            float w;
            if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w)) return w;
            return fb;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) return;
                foreach (string line in File.ReadAllLines(IniPath))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = t.Substring(0, eq).Trim();
                    string v = t.Substring(eq + 1).Trim();
                    if (k == "Color") { try { PenColor = ColorTranslator.FromHtml(v); } catch { } }
                    else if (k == "Width") BrushWidth = ParseF(v, BrushWidth);
                    else if (k == "HiWidth") HiWidth = ParseF(v, HiWidth);
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                File.WriteAllText(IniPath,
                    "Color=" + ColorTranslator.ToHtml(PenColor) + Environment.NewLine +
                    "Width=" + BrushWidth.ToString(ci) + Environment.NewLine +
                    "HiWidth=" + HiWidth.ToString(ci) + Environment.NewLine);
            }
            catch { }
        }
    }

    class Stroke
    {
        public Color Color;
        public float Width;
        public bool Highlighter;
        public List<PointF> Points = new List<PointF>();
        public Stroke(Color c, float w, bool hi) { Color = c; Width = w; Highlighter = hi; }
    }

    // Shared drawing surface. Strokes are stored in image coordinates. Supports fit-to-window plus
    // user zoom (Ctrl+wheel, centered on cursor) and pan (middle-drag).
    class DrawingControl : Control
    {
        Settings settings;
        Bitmap bg;
        List<Stroke> strokes;
        Stroke current;
        public bool Eraser;
        public event Action<Point> RightMouse;

        float userZoom = 1f;
        PointF pan = PointF.Empty;
        bool panning;
        Point lastPan;

        public DrawingControl(Settings s, Bitmap background, List<Stroke> initial)
        {
            settings = s;
            bg = background;
            strokes = initial != null ? initial : new List<Stroke>();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            TabStop = true;
        }

        public Bitmap Background { get { return bg; } }
        public List<Stroke> Strokes { get { return strokes; } }
        int ImgW { get { return bg != null ? bg.Width : Width; } }
        int ImgH { get { return bg != null ? bg.Height : Height; } }

        float BaseScale()
        {
            if (bg == null || bg.Width == 0 || bg.Height == 0) return 1f;
            return Math.Min((float)Width / bg.Width, (float)Height / bg.Height);
        }
        float Scale() { return BaseScale() * userZoom; }

        void CenterIfFit()
        {
            if (userZoom == 1f)
            {
                float sc = Scale();
                pan = new PointF((Width - ImgW * sc) / 2f, (Height - ImgH * sc) / 2f);
            }
        }
        PointF ToImage(Point p)
        {
            float sc = Scale(); if (sc <= 0) sc = 1;
            return new PointF((p.X - pan.X) / sc, (p.Y - pan.Y) / sc);
        }

        public void ResetZoom() { userZoom = 1f; CenterIfFit(); Invalidate(); }
        public void Undo() { if (strokes.Count > 0) { strokes.RemoveAt(strokes.Count - 1); Invalidate(); } }
        public void ClearAll() { strokes.Clear(); current = null; Invalidate(); }
        public void ToggleEraser() { Eraser = !Eraser; Cursor = Eraser ? Cursors.Hand : Cursors.Cross; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Right) { if (RightMouse != null) RightMouse(e.Location); base.OnMouseDown(e); return; }
            if (e.Button == MouseButtons.Middle) { panning = true; lastPan = e.Location; Cursor = Cursors.SizeAll; base.OnMouseDown(e); return; }
            if (e.Button == MouseButtons.Left)
            {
                if (Eraser) { EraseAt(ToImage(e.Location)); Invalidate(); }
                else
                {
                    bool hi = settings.Highlighter;
                    float w = hi ? settings.HiWidth : settings.BrushWidth;
                    current = new Stroke(settings.PenColor, w, hi);
                    current.Points.Add(ToImage(e.Location));
                    strokes.Add(current);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (panning)
            {
                pan = new PointF(pan.X + (e.X - lastPan.X), pan.Y + (e.Y - lastPan.Y));
                lastPan = e.Location; Invalidate(); base.OnMouseMove(e); return;
            }
            if ((e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                if (Eraser) { EraseAt(ToImage(e.Location)); Invalidate(); }
                else if (current != null) { current.Points.Add(ToImage(e.Location)); Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle) { panning = false; Cursor = Eraser ? Cursors.Hand : Cursors.Cross; }
            if (e.Button == MouseButtons.Left) current = null;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                PointF before = ToImage(e.Location);
                userZoom = Util.Clamp(userZoom * (e.Delta > 0 ? 1.2f : 1f / 1.2f), 1f, 16f);
                float sc = Scale();
                pan = new PointF(e.Location.X - before.X * sc, e.Location.Y - before.Y * sc);
                CenterIfFit(); Invalidate();
            }
            else
            {
                float d = e.Delta > 0 ? 1 : -1;
                if (settings.Highlighter) settings.HiWidth = Util.Clamp(settings.HiWidth + d * 2f, 2f, 96f);
                else settings.BrushWidth = Util.Clamp(settings.BrushWidth + d, 1f, 64f);
                settings.Save();
            }
            base.OnMouseWheel(e);
        }

        void EraseAt(PointF p)
        {
            for (int i = strokes.Count - 1; i >= 0; i--)
            {
                Stroke s = strokes[i];
                float rr = 12f + s.Width / 2f;
                for (int j = 0; j < s.Points.Count; j++)
                {
                    float dx = s.Points[j].X - p.X, dy = s.Points[j].Y - p.Y;
                    if (dx * dx + dy * dy <= rr * rr) { strokes.RemoveAt(i); break; }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            CenterIfFit();
            float sc = Scale();
            g.TranslateTransform(pan.X, pan.Y);
            g.ScaleTransform(sc, sc);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (bg != null) g.DrawImage(bg, 0, 0, bg.Width, bg.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < strokes.Count; i++) { Stroke s = strokes[i]; if (s.Highlighter) DrawHighlighter(g, s); else DrawBrush(g, s); }
        }

        public Bitmap Flatten()
        {
            Bitmap o = new Bitmap(ImgW, ImgH, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(o))
            {
                if (bg != null) g.DrawImageUnscaled(bg, 0, 0); else g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < strokes.Count; i++) { Stroke s = strokes[i]; if (s.Highlighter) DrawHighlighter(g, s); else DrawBrush(g, s); }
            }
            return o;
        }

        static void DrawBrush(Graphics g, Stroke s)
        {
            using (Pen pen = new Pen(s.Color, s.Width))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                if (s.Points.Count == 1)
                {
                    float r = s.Width / 2f;
                    using (SolidBrush b = new SolidBrush(s.Color)) g.FillEllipse(b, s.Points[0].X - r, s.Points[0].Y - r, s.Width, s.Width);
                }
                else if (s.Points.Count == 2) g.DrawLine(pen, s.Points[0], s.Points[1]);
                else g.DrawCurve(pen, s.Points.ToArray(), 0.5f);
            }
        }

        static void DrawHighlighter(Graphics g, Stroke s)
        {
            Color col = Color.FromArgb(110, s.Color.R, s.Color.G, s.Color.B);
            if (s.Points.Count == 1)
            {
                float r = s.Width / 2f;
                using (SolidBrush b = new SolidBrush(col)) g.FillEllipse(b, s.Points[0].X - r, s.Points[0].Y - r, s.Width, s.Width);
                return;
            }
            using (GraphicsPath gp = new GraphicsPath())
            {
                try
                {
                    if (s.Points.Count == 2) gp.AddLine(s.Points[0], s.Points[1]);
                    else gp.AddCurve(s.Points.ToArray(), 0.5f);
                    using (Pen wp = new Pen(Color.Black, s.Width)) { wp.StartCap = LineCap.Round; wp.EndCap = LineCap.Round; wp.LineJoin = LineJoin.Round; gp.Widen(wp); }
                    gp.FillMode = FillMode.Winding;
                    using (SolidBrush b = new SolidBrush(col)) g.FillPath(b, gp);
                }
                catch
                {
                    using (Pen pen = new Pen(col, s.Width))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                        if (s.Points.Count >= 3) g.DrawCurve(pen, s.Points.ToArray(), 0.5f);
                        else g.DrawLine(pen, s.Points[0], s.Points[s.Points.Count - 1]);
                    }
                }
            }
        }
    }

    class HotkeyWindow : NativeWindow
    {
        public event Action<int> HotkeyPressed;
        const int WM_HOTKEY = 0x0312;
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && HotkeyPressed != null) HotkeyPressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    class TrayAppContext : ApplicationContext
    {
        const int ID_DRAW = 0xA11;
        const uint MOD_CONTROL = 0x0002;
        const uint VK_DRAW = 0x51; // Q  (chosen to avoid Claude Desktop's global Ctrl+1)

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        // Ctrl+1 cycles through these in order.
        static readonly Color[] Cycle = { Color.Black, Color.Red, Color.Gold, Color.LimeGreen, Color.DodgerBlue };
        int colorIndex = 0;

        Settings settings = new Settings();
        NotifyIcon tray;
        OverlayForm overlay;
        HotkeyWindow hotkeyWin;
        System.Windows.Forms.Timer hotkeyWatch;
        bool hotkeyOk;
        List<SnapshotForm> snaps = new List<SnapshotForm>();

        public TrayAppContext()
        {
            settings.Load();
            settings.Changed += delegate { UpdateTrayIcon(); };

            overlay = new OverlayForm(settings);
            overlay.PinRequested += delegate(Bitmap bg, List<Stroke> st, bool minimized)
            {
                SnapshotForm f = new SnapshotForm(settings, bg, st, minimized);
                snaps.Add(f);
                f.FormClosed += delegate { snaps.Remove(f); };
                f.Show();
            };

            // Ctrl+1 is the only keyboard shortcut, and it's a system-wide RegisterHotKey, which is
            // stable across app switches (a low-level hook can get silently dropped by Windows).
            hotkeyWin = new HotkeyWindow();
            hotkeyWin.HotkeyPressed += delegate(int id) { if (id == ID_DRAW) OnDrawHotkey(); };
            TryRegisterHotkey();
            // Self-heal: if Ctrl+1 was momentarily held by something else at startup (or
            // registration otherwise failed), keep retrying until Inkly actually owns the hotkey.
            hotkeyWatch = new System.Windows.Forms.Timer();
            hotkeyWatch.Interval = 3000;
            hotkeyWatch.Tick += delegate { if (!hotkeyOk) TryRegisterHotkey(); };
            hotkeyWatch.Start();

            tray = new NotifyIcon();
            tray.Icon = MakeIcon(settings.PenColor);
            tray.Text = "Inkly  -  Ctrl+Q to draw / cycle color";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Start / stop drawing", null, delegate { overlay.Toggle(); });
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem hiItem = new ToolStripMenuItem("Highlighter");
            hiItem.CheckOnClick = true;
            hiItem.Checked = settings.Highlighter;
            hiItem.Click += delegate { settings.Highlighter = hiItem.Checked; };
            menu.Items.Add(hiItem);
            menu.Items.Add("Custom color...", null, delegate { PickColor(); });
            ToolStripMenuItem widthItem = new ToolStripMenuItem("Thickness");
            foreach (int w in new int[] { 2, 4, 6, 10, 16, 24, 36 })
            {
                int ww = w;
                widthItem.DropDownItems.Add(w + " px", null, delegate { if (settings.Highlighter) settings.HiWidth = ww; else settings.BrushWidth = ww; settings.Save(); });
            }
            menu.Items.Add(widthItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Ctrl+Q = draw / cycle color · right-click while drawing for more", null, delegate { }).Enabled = false;
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            menu.Opening += delegate { hiItem.Checked = settings.Highlighter; };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { overlay.Toggle(); };
        }

        void TryRegisterHotkey()
        {
            if (hotkeyOk) return;
            hotkeyOk = RegisterHotKey(hotkeyWin.Handle, ID_DRAW, MOD_CONTROL, VK_DRAW);
        }

        void OnDrawHotkey()
        {
            if (!overlay.IsActive)
            {
                colorIndex = 0;
                settings.PenColor = Cycle[0];
                settings.Save(); settings.NotifyChanged();
                overlay.EnsureActive();
            }
            else
            {
                colorIndex = (colorIndex + 1) % Cycle.Length;
                settings.PenColor = Cycle[colorIndex];
                settings.Save(); settings.NotifyChanged();
            }
        }

        void PickColor()
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = settings.PenColor; dlg.FullOpen = true;
                if (dlg.ShowDialog() == DialogResult.OK) { settings.PenColor = dlg.Color; settings.Save(); UpdateTrayIcon(); }
            }
        }

        void UpdateTrayIcon()
        {
            if (tray == null) return;
            Icon old = tray.Icon;
            tray.Icon = MakeIcon(settings.PenColor);
            if (old != null) { IntPtr h = old.Handle; old.Dispose(); DestroyIcon(h); }
        }

        static Icon MakeIcon(Color c)
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 11, 11);
                    using (Pen p = new Pen(Color.White, 1)) g.DrawEllipse(p, 2, 2, 11, 11);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void ExitApp()
        {
            try { if (hotkeyWatch != null) hotkeyWatch.Stop(); } catch { }
            try { UnregisterHotKey(hotkeyWin.Handle, ID_DRAW); } catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (overlay != null) overlay.Dispose();
            if (hotkeyWin != null) hotkeyWin.DestroyHandle();
            Application.Exit();
        }
    }

    class OverlayForm : Form
    {
        Settings settings;
        DrawingControl canvas;
        bool active;
        ContextMenuStrip overlayMenu;
        System.Windows.Forms.Timer keyTimer;
        bool escPrev, zPrev, pPrev, oPrev;
        public event Action<Bitmap, List<Stroke>, bool> PinRequested;

        public bool IsActive { get { return active; } }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

        public OverlayForm(Settings s)
        {
            settings = s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Text = "Inkly";
            AutoScaleMode = AutoScaleMode.None; // we place/size in physical pixels under PerMonitorV2

            overlayMenu = new ContextMenuStrip();
            overlayMenu.Items.Add("Pin snip - maximized  (P)", null, delegate { DoPin(false); });
            overlayMenu.Items.Add("Pin snip - minimized  (O)", null, delegate { DoPin(true); });
            overlayMenu.Items.Add("Toggle eraser", null, delegate { if (canvas != null) canvas.ToggleEraser(); });
            overlayMenu.Items.Add("Toggle highlighter", null, delegate { settings.Highlighter = !settings.Highlighter; });
            overlayMenu.Items.Add("Undo", null, delegate { if (canvas != null) canvas.Undo(); });
            overlayMenu.Items.Add("Clear", null, delegate { if (canvas != null) canvas.ClearAll(); });
            overlayMenu.Items.Add("Reset zoom", null, delegate { if (canvas != null) canvas.ResetZoom(); });
            overlayMenu.Items.Add(new ToolStripSeparator());
            overlayMenu.Items.Add("Exit drawing", null, delegate { Stop(); });

            keyTimer = new System.Windows.Forms.Timer();
            keyTimer.Interval = 40;
            keyTimer.Tick += KeyPoll;
        }

        public void Toggle() { if (active) Stop(); else Start(); }
        public void EnsureActive() { if (!active) Start(); }

        void ForceForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgT = GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint myT = GetCurrentThreadId();
                if (fgT != myT) AttachThreadInput(myT, fgT, true);
                SetForegroundWindow(Handle);
                BringToFront();
                Activate();
                if (canvas != null) canvas.Focus();
                if (fgT != myT) AttachThreadInput(myT, fgT, false);
            }
            catch { }
        }

        void Start()
        {
            Screen scr = Screen.FromPoint(Cursor.Position);
            Rectangle b = scr.WorkingArea; // exclude the taskbar
            Bitmap bg = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bg)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            if (canvas != null) { Controls.Remove(canvas); canvas.Dispose(); }
            canvas = new DrawingControl(settings, bg, new List<Stroke>());
            canvas.Dock = DockStyle.Fill;
            canvas.RightMouse += delegate(Point p) { overlayMenu.Show(canvas, p); };
            Controls.Add(canvas);
            Bounds = b;
            active = true;
            escPrev = false; zPrev = false; pPrev = false; oPrev = false;
            keyTimer.Start();
            Show();
            ForceForeground();
        }

        void Stop()
        {
            active = false;
            if (keyTimer != null) keyTimer.Stop();
            Hide();
            if (canvas != null) { Controls.Remove(canvas); canvas.Dispose(); canvas = null; }
        }

        // Esc (exit) and Ctrl+Z (undo) are polled here while drawing: reliable regardless of
        // window focus, with no global hook that Windows can silently drop.
        void KeyPoll(object sender, EventArgs e)
        {
            if (!active) return;
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool esc = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (esc && !escPrev) { escPrev = esc; Stop(); return; }
            escPrev = esc;
            bool p = (GetAsyncKeyState(0x50) & 0x8000) != 0;
            if (p && !pPrev && !ctrl) { pPrev = p; DoPin(false); return; }
            pPrev = p;
            bool o = (GetAsyncKeyState(0x4F) & 0x8000) != 0;
            if (o && !oPrev && !ctrl) { oPrev = o; DoPin(true); return; }
            oPrev = o;
            bool z = (GetAsyncKeyState(0x5A) & 0x8000) != 0;
            if (ctrl && z && !zPrev && canvas != null) canvas.Undo();
            zPrev = z;
        }

        void DoPin(bool minimized)
        {
            if (canvas == null || canvas.Background == null) return;
            Bitmap bg = (Bitmap)canvas.Background.Clone();
            List<Stroke> copy = new List<Stroke>(canvas.Strokes);
            if (PinRequested != null) PinRequested(bg, copy, minimized);
            Stop();
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } // WS_EX_TOOLWINDOW
        }
    }

    class SnapshotForm : Form
    {
        static int counter = 0;
        Settings settings;
        DrawingControl canvas;
        ContextMenuStrip menu;
        bool startMinimized, restoreToMax;

        public SnapshotForm(Settings s, Bitmap bg, List<Stroke> strokes, bool minimized)
        {
            settings = s;
            startMinimized = minimized;
            Text = "Inkly snip " + (++counter);
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            try { this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            MinimumSize = new Size(240, 180);
            AutoScaleMode = AutoScaleMode.None;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            float sc = Math.Min(wa.Width * 0.6f / bg.Width, wa.Height * 0.6f / bg.Height);
            if (sc > 1f) sc = 1f;
            ClientSize = new Size(Math.Max(240, (int)(bg.Width * sc)), Math.Max(180, (int)(bg.Height * sc)));
            WindowState = minimized ? FormWindowState.Minimized : FormWindowState.Maximized;

            canvas = new DrawingControl(settings, bg, strokes);
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);

            menu = new ContextMenuStrip();
            menu.Items.Add("Eraser  (E)", null, delegate { canvas.ToggleEraser(); });
            menu.Items.Add("Undo  (Ctrl+Z)", null, delegate { canvas.Undo(); });
            menu.Items.Add("Reset zoom  (0)", null, delegate { canvas.ResetZoom(); });
            menu.Items.Add("Clear  (Del)", null, delegate { canvas.ClearAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Save as PNG...  (Ctrl+S)", null, delegate { Save(); });
            menu.Items.Add("Copy  (Ctrl+C)", null, delegate { CopyImg(); });
            menu.Items.Add("Close", null, delegate { Close(); });
            canvas.RightMouse += delegate(Point p) { menu.Show(canvas, p); };
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); canvas.Focus(); if (startMinimized) restoreToMax = true; }

        // O pins minimized but should restore to maximized: when it's first restored from the
        // taskbar (state becomes Normal), bump it to Maximized once.
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (restoreToMax && WindowState == FormWindowState.Normal) { restoreToMax = false; WindowState = FormWindowState.Maximized; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            if (key == Keys.Escape) { Close(); return true; }
            if (ctrl && key == Keys.Z) { canvas.Undo(); return true; }
            if (ctrl && key == Keys.S) { Save(); return true; }
            if (ctrl && key == Keys.C) { CopyImg(); return true; }
            if (ctrl)
            {
                int idx = Util.DigitIndex(key);
                if (idx >= 1) { settings.PenColor = Palette.Get(idx); settings.Save(); settings.NotifyChanged(); return true; }
            }
            if (!ctrl && key == Keys.H) { settings.Highlighter = !settings.Highlighter; return true; }
            if (!ctrl && key == Keys.E) { canvas.ToggleEraser(); return true; }
            if (!ctrl && (key == Keys.D0 || key == Keys.NumPad0)) { canvas.ResetZoom(); return true; }
            if (!ctrl && key == Keys.Delete) { canvas.ClearAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Save()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "PNG image|*.png"; d.FileName = "snip.png";
                if (d.ShowDialog() == DialogResult.OK)
                    using (Bitmap b = canvas.Flatten()) b.Save(d.FileName, ImageFormat.Png);
            }
        }

        void CopyImg()
        {
            try { using (Bitmap b = canvas.Flatten()) Clipboard.SetImage(b); } catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (canvas != null) canvas.Dispose();
            base.OnFormClosed(e);
        }
    }
}
