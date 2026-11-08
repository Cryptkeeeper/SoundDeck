// Softer look for the stock WinForms controls: rounded cards, buttons, combo boxes and text boxes,
// custom sliders and checkboxes, and dark scrollbars. Look.Apply styles a window and anything added to it later.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundDeck
{
    static class Look
    {
        static readonly Color Bg = Color.FromArgb(18, 20, 24);
        static readonly Color Fg = Color.FromArgb(232, 234, 237);
        static readonly Color Dim = Color.FromArgb(154, 160, 166);
        static readonly Color Accent = Color.FromArgb(255, 106, 61);
        static readonly Color Line = Color.FromArgb(58, 63, 74);

        public static float Scale = 1f;   // the main window's UI scale, for corner sizes
        public static Control NavPanel;   // buttons in here are drawn as side-menu items

        static readonly HashSet<Control> done = new HashSet<Control>();

        public static int R(float v) { return Math.Max(1, (int)Math.Round(v * Scale)); }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var path = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, path);
        }

        // The colour showing behind a control (skips transparent parents).
        public static Color BackOf(Control c)
        {
            while (c != null && c.BackColor.A < 255) c = c.Parent;
            return c == null ? Bg : c.BackColor;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Paints the corners outside a rounded rectangle in the background colour, so a square control looks rounded.
        public static void Corners(Graphics g, Rectangle r, float rad, Color outside)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath(FillMode.Alternate))
            using (var round = Round(new RectangleF(r.X, r.Y, r.Width, r.Height), rad))
            using (var b = new SolidBrush(outside))
            {
                path.AddRectangle(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2));
                path.AddPath(round, false);
                g.FillPath(b, path);
            }
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string id);
        // Undocumented but stable since Windows 10 1903 (used by Notepad++ and others) to get dark scrollbars.
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool allow);
        static bool darkApp, darkFailed;

        static void AllowDark(IntPtr h)
        {
            if (darkFailed) return;
            try
            {
                if (!darkApp) { SetPreferredAppMode(1); darkApp = true; } // 1 = allow dark
                AllowDarkModeForWindow(h, true);
            }
            catch { darkFailed = true; }
        }

        // Dark scrollbars (Windows 10 1903 and later; older versions just keep the light ones).
        public static void DarkTheme(Control c, string theme)
        {
            EventHandler apply = (s, e) => { try { AllowDark(c.Handle); SetWindowTheme(c.Handle, theme, null); } catch { } };
            if (c.IsHandleCreated) apply(null, null);
            c.HandleCreated += apply;
        }

        // ---------- Applying the look ----------

        public static void Apply(Control root)
        {
            foreach (Control k in root.Controls) ApplyTree(k);
            if (done.Add(root)) root.ControlAdded += (s, e) => ApplyTree(e.Control);
        }

        static void ApplyTree(Control k)
        {
            if (done.Add(k))
            {
                k.Disposed += (s, e) => done.Remove((Control)s);
                Style(k);
                k.ControlAdded += (s, e) => ApplyTree(e.Control);
            }
            foreach (Control c in k.Controls) ApplyTree(c);
        }

        static void Style(Control k)
        {
            if (k is Slider) StyleSlider((Slider)k);
            else if (k is Button && ((Button)k).FlatStyle == FlatStyle.Flat) StyleButton((Button)k);
            else if (k is CheckBox) StyleCheck((CheckBox)k);
            else if (k is ComboBox) new ComboSkin((ComboBox)k);
            else if (k is TextBox && ((TextBox)k).BorderStyle == BorderStyle.FixedSingle) StyleText((TextBox)k);
            else if (k is ListBox) StyleList((ListBox)k);
            else if (k is Panel) StylePanel((Panel)k);
        }

        // ---------- Cards ----------

        static void StylePanel(Panel p)
        {
            if (p.AutoScroll) DarkTheme(p, "DarkMode_Explorer");
            if (p.Dock != DockStyle.None || p.Parent == null) return;
            if (p.BackColor.A < 255 || p.BackColor == BackOf(p.Parent) || p.Width < 16 || p.Height < 16) return;

            // A thin full-width strip along the top edge becomes a coloured top edge that follows the rounded corners.
            Panel stripe = null;
            foreach (Control c in p.Controls)
            {
                var sp = c as Panel;
                if (sp != null && sp.Top == 0 && sp.Left == 0 && sp.Height <= 5 && sp.Width >= p.Width - 1) { stripe = sp; break; }
            }
            if (stripe != null) stripe.Visible = false;

            bool zone = p is FlowLayoutPanel;
            SetDoubleBuffered(p);
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                float rad = R(zone ? 7 : 10);
                if (stripe != null)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = Round(new RectangleF(0, 0, p.Width, p.Height), rad))
                    using (var b = new SolidBrush(stripe.BackColor))
                    {
                        var old = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        g.FillRectangle(b, 0, 0, p.Width, Math.Max(3, stripe.Height));
                        g.Clip = old;
                    }
                }
                Corners(g, p.ClientRectangle, rad, BackOf(p.Parent));
            };
            p.Resize += (s, e) => p.Invalidate();
            // Scrolling moves already-painted corners, so repaint them.
            p.Scroll += (s, e) => p.Invalidate();
            p.MouseWheel += (s, e) => p.Invalidate();
            // Drag-over highlight changes the zone colour; chips inside need their corners redrawn.
            p.BackColorChanged += (s, e) => p.Invalidate(true);
        }

        static void SetDoubleBuffered(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(c, true, null);
            }
            catch { }
        }

        // ---------- Buttons ----------

        static void StyleButton(Button b)
        {
            bool hover = false, down = false;
            b.MouseEnter += (s, e) => { hover = true; b.Invalidate(); };
            b.MouseLeave += (s, e) => { hover = false; down = false; b.Invalidate(); };
            b.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { down = true; b.Invalidate(); } };
            b.MouseUp += (s, e) => { down = false; b.Invalidate(); };
            b.EnabledChanged += (s, e) => b.Invalidate();
            b.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color back = BackOf(b.Parent);
                g.Clear(back);
                bool nav = b.Parent != null && b.Parent == NavPanel;
                Color fill = b.BackColor;
                if (!b.Enabled) fill = Mix(fill, back, 0.45f);
                else if (down) fill = Mix(fill, Color.Black, 0.15f);
                else if (hover) fill = fill == back ? Mix(fill, Color.White, 0.06f) : Mix(fill, Color.White, 0.09f);
                var r = new RectangleF(0, 0, b.Width, b.Height);
                float rad = Math.Min(R(nav ? 8 : 7), b.Height / 2f);
                if (fill != back) FillRound(g, fill, r, rad);
                if (b.FlatAppearance.BorderSize > 0 && b.Enabled)
                    using (var path = Round(new RectangleF(0.5f, 0.5f, b.Width - 1, b.Height - 1), rad))
                    using (var pen = new Pen(b.FlatAppearance.BorderColor.IsEmpty ? Line : b.FlatAppearance.BorderColor))
                        g.DrawPath(pen, path);

                var tr = new Rectangle(0, 0, b.Width, b.Height);
                if (nav)
                {
                    bool selected = b.ForeColor == Accent;
                    if (selected) FillRound(g, Accent, new RectangleF(R(4), b.Height * 0.28f, R(3), b.Height * 0.44f), R(1.5f));
                    tr = new Rectangle(R(16), 0, b.Width - R(20), b.Height);
                }
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                var ta = b.TextAlign;
                if (ta == ContentAlignment.MiddleLeft || ta == ContentAlignment.TopLeft || ta == ContentAlignment.BottomLeft) flags |= TextFormatFlags.Left;
                else flags |= TextFormatFlags.HorizontalCenter;
                if (!nav && b.Text.IndexOf('\n') >= 0) flags = (flags & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.WordBreak;
                if (!nav) tr.Inflate(-R(6), 0);
                string text = nav ? b.Text.Trim() : b.Text;
                TextRenderer.DrawText(g, text, b.Font, tr, b.Enabled ? b.ForeColor : Mix(Dim, back, 0.3f), flags);
            };
        }

        // ---------- Checkboxes ----------

        static int BoxSize(CheckBox c) { return Math.Max(12, (int)Math.Round(c.Font.Height * 0.85f)); }

        static void SizeCheck(CheckBox c)
        {
            int box = BoxSize(c);
            var t = TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix);
            c.AutoSize = false;
            c.Size = new Size(box + box / 2 + t.Width + 2, Math.Max(box + 4, t.Height + 2));
        }

        static void StyleCheck(CheckBox c)
        {
            if (c.Appearance != Appearance.Normal) return;
            bool hover = false;
            SizeCheck(c);
            c.TextChanged += (s, e) => SizeCheck(c);
            c.MouseEnter += (s, e) => { hover = true; c.Invalidate(); };
            c.MouseLeave += (s, e) => { hover = false; c.Invalidate(); };
            c.EnabledChanged += (s, e) => c.Invalidate();
            c.Paint += (s, e) =>
            {
                var g = e.Graphics;
                Color back = BackOf(c);
                g.Clear(back);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int box = BoxSize(c);
                float y = (c.Height - box) / 2f;
                var r = new RectangleF(0.5f, y, box - 1, box - 1);
                bool colored = c.ForeColor != Fg && c.ForeColor != Dim && c.ForeColor.A == 255;
                Color on = colored ? c.ForeColor : Accent;
                if (!c.Enabled) on = Mix(on, back, 0.55f);
                float rad = box * 0.28f;
                if (c.Checked)
                {
                    FillRound(g, on, r, rad);
                    using (var pen = new Pen(c.Enabled ? Color.White : Mix(Color.White, back, 0.5f), Math.Max(1.6f, box / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] {
                            new PointF(r.X + box * 0.24f, y + box * 0.52f),
                            new PointF(r.X + box * 0.43f, y + box * 0.70f),
                            new PointF(r.X + box * 0.76f, y + box * 0.32f) });
                }
                else
                {
                    FillRound(g, Mix(back, Color.White, hover && c.Enabled ? 0.10f : 0.05f), r, rad);
                    using (var path = Round(r, rad))
                    using (var pen = new Pen(c.Enabled ? (hover ? Dim : Mix(Dim, back, 0.45f)) : Mix(Dim, back, 0.7f), 1.2f))
                        g.DrawPath(pen, path);
                }
                var tr = new Rectangle(box + box / 2, 0, c.Width - box - box / 2, c.Height);
                Color fg = c.Enabled ? c.ForeColor : Mix(Dim, back, 0.35f);
                TextRenderer.DrawText(g, c.Text, c.Font, tr, fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
        }

        // ---------- Text boxes ----------

        // A borderless text box on a rounded field drawn by its parent.
        static void StyleText(TextBox t)
        {
            var parent = t.Parent;
            if (parent == null) return;
            int h = t.Height;
            t.BorderStyle = BorderStyle.None;
            int inner = t.Height;
            int padX = 7, padY = Math.Max(2, (h - inner) / 2);
            t.Bounds = new Rectangle(t.Left + padX, t.Top + padY, Math.Max(20, t.Width - padX * 2), inner);
            parent.Paint += (s, e) =>
            {
                if (!t.Visible || t.Parent != parent) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int px = Math.Max(4, t.Height * 7 / 17), py = Math.Max(2, t.Height / 6);
                var r = new RectangleF(t.Left - px, t.Top - py, t.Width + px * 2, t.Height + py * 2);
                float rad = R(6);
                FillRound(g, t.BackColor, r, rad);
                using (var path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), rad))
                using (var pen = new Pen(t.Focused ? Mix(Accent, t.BackColor, 0.3f) : Line))
                    g.DrawPath(pen, path);
            };
            t.GotFocus += (s, e) => parent.Invalidate();
            t.LostFocus += (s, e) => parent.Invalidate();
            t.LocationChanged += (s, e) => parent.Invalidate();
            t.SizeChanged += (s, e) => parent.Invalidate();
            t.VisibleChanged += (s, e) => parent.Invalidate();
        }

        // A list on a rounded panel drawn by its parent.
        static void StyleList(ListBox l)
        {
            DarkTheme(l, "DarkMode_Explorer");
            var parent = l.Parent;
            if (parent == null || l.BorderStyle != BorderStyle.None) return;
            int pad = 6;
            l.Bounds = new Rectangle(l.Left + pad, l.Top + pad, l.Width - pad * 2, l.Height - pad * 2);
            parent.Paint += (s, e) =>
            {
                if (!l.Visible || l.Parent != parent) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int px = Math.Max(4, l.Font.Height / 3);
                FillRound(e.Graphics, l.BackColor, new RectangleF(l.Left - px, l.Top - px, l.Width + px * 2, l.Height + px * 2), R(10));
            };
            l.LocationChanged += (s, e) => parent.Invalidate();
            l.SizeChanged += (s, e) => parent.Invalidate();
        }

        // ---------- Sliders ----------

        static void StyleSlider(Slider s)
        {
            if (s.Minimum < 0 && s.Maximum > 0) s.FromCenter = true;
            // Windows makes a horizontal slider 45px tall, which hides text just below it. The drawn one needs about 26.
            if (s.Orientation == Orientation.Horizontal && s.TickStyle == TickStyle.None) { s.AutoSize = false; s.Height = 26; }
            // On a card with a coloured top edge (the Mixer channels), the slider uses that colour.
            var card = s.Parent;
            if (card != null)
                foreach (Control c in card.Controls)
                    if (c is Panel && c.Top == 0 && c.Left == 0 && c.Height <= 5 && c.Width >= card.Width - 1) { s.Fill = c.BackColor; break; }
        }

        // ---------- Combo boxes ----------

        // Repaints the border and arrow of a flat combo box after Windows draws it.
        class ComboSkin : NativeWindow
        {
            readonly ComboBox c;
            bool hover;

            public ComboSkin(ComboBox c)
            {
                this.c = c;
                if (c.IsHandleCreated) Attach(null, null);
                c.HandleCreated += Attach;
                c.HandleDestroyed += (s, e) => ReleaseHandle();
                c.MouseEnter += (s, e) => { hover = true; c.Invalidate(); };
                c.MouseLeave += (s, e) => { hover = false; c.Invalidate(); };
                c.EnabledChanged += (s, e) => c.Invalidate();
                c.Resize += (s, e) => c.Invalidate();
            }

            void Attach(object s, EventArgs e)
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(c.Handle);
                try { AllowDark(c.Handle); SetWindowTheme(c.Handle, "DarkMode_CFD", null); } catch { }
            }

            [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
            [DllImport("gdi32.dll")] static extern int SetBkColor(IntPtr hdc, int color);
            [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
            static readonly Dictionary<int, IntPtr> brushes = new Dictionary<int, IntPtr>();
            static int Win(Color c) { return c.R | (c.G << 8) | (c.B << 16); }
            [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
            [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
            struct RECT { public int L, T, R, B; }

            protected override void WndProc(ref Message m)
            {
                // A disabled typing box inside the combo asks for its colours here; Windows would make it light grey.
                if (m.Msg == 0x0138 && !c.Enabled) // WM_CTLCOLORSTATIC
                {
                    Color fill = Mix(c.BackColor, BackOf(c.Parent), 0.45f);
                    int key = Win(fill);
                    IntPtr br;
                    if (!brushes.TryGetValue(key, out br)) { br = CreateSolidBrush(key); brushes[key] = br; }
                    SetBkColor(m.WParam, key);
                    SetTextColor(m.WParam, Win(Mix(Dim, fill, 0.35f)));
                    m.Result = br;
                    return;
                }
                base.WndProc(ref m);
                if (m.Msg == 0x000F || m.Msg == 0x0085) // WM_PAINT, WM_NCPAINT
                    try { Draw(); } catch { }
            }

            void Draw()
            {
                if (c.IsDisposed || c.Width < 10) return;
                using (var g = Graphics.FromHwnd(c.Handle))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var r = c.ClientRectangle;
                    Color fill = c.Enabled ? c.BackColor : Mix(c.BackColor, BackOf(c.Parent), 0.45f);
                    Color outside = BackOf(c.Parent);
                    // Cover Windows' light border.
                    using (var pen = new Pen(fill, 2)) g.DrawRectangle(pen, 1, 1, r.Width - 2, r.Height - 2);
                    if (c.DropDownStyle == ComboBoxStyle.DropDown)
                    {
                        // Typing combos: repaint everything around the typing box (Windows leaves light lines there).
                        IntPtr edit = GetWindow(c.Handle, 5); // GW_CHILD
                        RECT er;
                        if (edit != IntPtr.Zero && GetWindowRect(edit, out er))
                        {
                            var tl = c.PointToClient(new Point(er.L, er.T));
                            var old = g.Clip;
                            g.ExcludeClip(new Rectangle(tl.X, tl.Y, er.R - er.L, er.B - er.T));
                            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                            g.Clip = old;
                        }
                    }
                    else if (!c.Enabled)
                    {
                        // Windows draws disabled ones with light lines; draw the whole thing instead.
                        using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                        var tr = new Rectangle(r.X + 3, r.Y, r.Width - SystemInformation.HorizontalScrollBarArrowWidth - 8, r.Height);
                        TextRenderer.DrawText(g, c.Text, c.Font, tr, Mix(Dim, fill, 0.35f), TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    }
                    // Arrow area.
                    int aw = SystemInformation.HorizontalScrollBarArrowWidth + 3;
                    using (var b = new SolidBrush(fill)) g.FillRectangle(b, r.Right - aw - 1, 1, aw, r.Height - 2);
                    float cx = r.Right - aw / 2f - 3, cy = r.Height / 2f + 1, w = Math.Max(3.5f, r.Height / 6.5f);
                    using (var pen = new Pen(c.Enabled ? (hover ? Fg : Dim) : Mix(Dim, fill, 0.5f), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(cx - w, cy - w / 2), new PointF(cx, cy + w / 2), new PointF(cx + w, cy - w / 2) });
                    float rad = Math.Min(R(6), r.Height / 2f);
                    Corners(g, r, rad, outside);
                    using (var path = Round(new RectangleF(0.5f, 0.5f, r.Width - 1, r.Height - 1), rad))
                    using (var pen = new Pen(c.Focused || hover ? Mix(Line, Color.White, 0.12f) : Line))
                        if (c.Enabled) g.DrawPath(pen, path);
                }
            }
        }
    }

    // A flat slider: thin rounded track, coloured fill and a round knob. Drop-in for TrackBar.
    class Slider : TrackBar
    {
        Color fill = Color.FromArgb(255, 106, 61);
        public Color Fill { get { return fill; } set { fill = value; Invalidate(); } }
        public bool FromCenter; // fill from 0 instead of from the start (EQ bands)
        bool dragging, hover;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        bool Vert { get { return Orientation == Orientation.Vertical; } }
        int Knob { get { return Math.Max(5, (int)Math.Round(Font.Height * 0.4f)); } }

        void Ends(out float a, out float b)
        {
            int len = Vert ? Height : Width;
            a = Knob + 3; b = len - Knob - 3;
        }

        float PosOf(int v)
        {
            float a, b; Ends(out a, out b);
            float t = Maximum == Minimum ? 0 : (v - Minimum) / (float)(Maximum - Minimum);
            return Vert ? b - t * (b - a) : a + t * (b - a);
        }

        int ValueAt(Point p)
        {
            float a, b; Ends(out a, out b);
            float x = Vert ? p.Y : p.X;
            float t = (x - a) / Math.Max(1f, b - a);
            if (Vert) t = 1 - t;
            t = Math.Max(0, Math.Min(1, t));
            return Minimum + (int)Math.Round(t * (Maximum - Minimum));
        }

        protected override void OnValueChanged(EventArgs e) { base.OnValueChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // Mouse dragging is handled here so the knob follows the drawn track exactly.
        protected override void WndProc(ref Message m)
        {
            const int Move = 0x200, Down = 0x201, Up = 0x202, Dbl = 0x203, CaptureChanged = 0x215;
            if (m.Msg == CaptureChanged) dragging = false;
            if (m.Msg == Down || m.Msg == Dbl || m.Msg == Up || (m.Msg == Move && dragging))
            {
                long lp = m.LParam.ToInt64();
                var pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                if (m.Msg == Up)
                {
                    if (dragging) { dragging = false; Capture = false; Invalidate(); OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, pt.X, pt.Y, 0)); }
                    return;
                }
                if (m.Msg != Move)
                {
                    if (CanFocus) Focus();
                    dragging = true; Capture = true;
                    OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, pt.X, pt.Y, 0));
                }
                int v = ValueAt(pt);
                if (v != Value) { Value = v; OnScroll(EventArgs.Empty); }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Look.BackOf(Parent);
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int k = Knob;
            float th = Math.Max(3f, k * 0.55f);
            float a, b; Ends(out a, out b);
            float c = (Vert ? Width : Height) / 2f;
            // A horizontal slider is taller than it looks; keep the track where Windows drew it, level with its label.
            if (!Vert && TickStyle == TickStyle.None) c = Math.Min(c, k + 5);
            float pos = PosOf(Value);
            Color track = Look.Mix(back, Color.White, Enabled ? 0.13f : 0.07f);
            Color f = Enabled ? fill : Look.Mix(fill, back, 0.6f);

            Look.FillRound(g, track, Vert ? new RectangleF(c - th / 2, a, th, b - a) : new RectangleF(a, c - th / 2, b - a, th), th / 2);

            if (TickStyle != TickStyle.None && TickFrequency > 0 && Maximum > Minimum)
                using (var pen = new Pen(Look.Mix(back, Color.White, 0.11f), 1f))
                    for (int v = Minimum; v <= Maximum; v += TickFrequency)
                    {
                        float p = PosOf(v), off = k + 4, len = Math.Max(3, k * 0.5f);
                        bool zero = FromCenter && v == 0;
                        if (Vert)
                        {
                            g.DrawLine(pen, c - off - len - (zero ? 2 : 0), p, c - off, p);
                            g.DrawLine(pen, c + off, p, c + off + len + (zero ? 2 : 0), p);
                        }
                        else
                        {
                            g.DrawLine(pen, p, c - off - len, p, c - off);
                            g.DrawLine(pen, p, c + off, p, c + off + len);
                        }
                    }

            float from = FromCenter ? PosOf(Math.Max(Minimum, Math.Min(Maximum, 0))) : (Vert ? b : a);
            float lo = Math.Min(from, pos), hi = Math.Max(from, pos);
            if (hi - lo > 0.5f)
                Look.FillRound(g, f, Vert ? new RectangleF(c - th / 2, lo, th, hi - lo) : new RectangleF(lo, c - th / 2, hi - lo, th), th / 2);

            float r = k + ((hover || dragging) && Enabled ? 1 : 0);
            float kx = Vert ? c : pos, ky = Vert ? pos : c;
            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(shadow, kx - r, ky - r + 1.5f, r * 2, r * 2);
            using (var ring = new SolidBrush(f)) g.FillEllipse(ring, kx - r, ky - r, r * 2, r * 2);
            float ir = r - Math.Max(2.5f, r * 0.32f);
            using (var dot = new SolidBrush(Enabled ? Color.White : Look.Mix(Color.White, back, 0.55f))) g.FillEllipse(dot, kx - ir, ky - ir, ir * 2, ir * 2);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Color.FromArgb(90, f), 2f)) g.DrawEllipse(pen, kx - r - 2.5f, ky - r - 2.5f, r * 2 + 5, r * 2 + 5);
        }
    }
}
