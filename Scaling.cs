// Scales the whole UI (positions, sizes and fonts) with the window size.
// Every control's size at 100% is remembered the first time it's seen; new controls are assumed to be built at 100%.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundDeck
{
    partial class MainForm
    {
        const int BaseH = 700; // the window's height at 100%
        int BaseW { get { return StreamerOn ? 1564 : 1372; } } // streamer mode adds the Stream column

        // Moves a control after it was built. x/y/w/h are at 100%; the current scale is applied.
        void Place(Control c, int x, int y, int w, int h)
        {
            UiBase b;
            if (uiBase.TryGetValue(c, out b)) b.Bounds = new Rectangle(x, y, w, h);
            c.Bounds = new Rectangle(SX(x), SY(y), SX(w), SY(h));
        }

        // Re-applies scaling even if the window size didn't change (e.g. after streamer mode changes the layout).
        void RescaleNow()
        {
            uiScale = -1;
            ApplyUiScale();
        }

        class UiBase { public Rectangle Bounds; public Font Font; public Padding Margin, Padding; public Size Max; }

        readonly Dictionary<Control, UiBase> uiBase = new Dictionary<Control, UiBase>();
        float uiScale = 1f;
        Font baseFormFont;
        Panel navPanel;
        Timer scaleTimer;

        void InitScaling()
        {
            AutoScaleMode = AutoScaleMode.None;
            baseFormFont = Font;
            scaleTimer = new Timer { Interval = 120 };
            scaleTimer.Tick += (s, e) => { scaleTimer.Stop(); ApplyUiScale(); };
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized) return;
                scaleTimer.Stop();
                scaleTimer.Start();
            };
        }

        // Text and spacing scale evenly (uiScale); layout may stretch a bit more along the longer side
        // (scaleX/scaleY, up to 35% more) so the window is filled without giant gaps.
        float scaleX = 1f, scaleY = 1f;

        void ApplyUiScale()
        {
            if (baseFormFont == null) return; // still building the window
            float sx = ClientSize.Width / (float)BaseW, sy = ClientSize.Height / (float)BaseH;
            float s = Math.Max(0.75f, Math.Min(3f, Math.Min(sx, sy)));
            float ax = Math.Max(s, Math.Min(sx, s * 1.35f)), ay = Math.Max(s, Math.Min(sy, s * 1.35f));
            if (Math.Abs(s - uiScale) < 0.01f && Math.Abs(ax - scaleX) < 0.01f && Math.Abs(ay - scaleY) < 0.01f) return;
            uiScale = s; scaleX = ax; scaleY = ay;
            Look.Scale = s;
            SuspendLayout();
            Font = ScaledFont(baseFormFont);
            ScaleTree(this);
            ResumeLayout(true);
            Invalidate(true);
        }

        Font ScaledFont(Font f)
        {
            return new Font(f.FontFamily, f.Size * uiScale, f.Style, f.Unit);
        }

        int S(int v) { return (int)Math.Round(v * uiScale); }
        int SX(int v) { return (int)Math.Round(v * scaleX); }
        int SY(int v) { return (int)Math.Round(v * scaleY); }

        void ScaleTree(Control parent)
        {
            // Measure from the top-left, not from wherever the page is scrolled to.
            var scroll = parent as ScrollableControl;
            if (scroll != null && scroll.AutoScroll && !(parent is FlowLayoutPanel)) scroll.AutoScrollPosition = Point.Empty;
            parent.SuspendLayout();
            foreach (Control k in parent.Controls)
            {
                UiBase b;
                if (!uiBase.TryGetValue(k, out b)) b = Remember(k);
                Apply(k, b);
                if (k.Controls.Count > 0 && !(k is ComboBox) && !(k is TrackBar)) ScaleTree(k);
            }
            parent.ResumeLayout(true);
        }

        UiBase Remember(Control k)
        {
            // A font equal to the parent's is inherited, so it follows the parent and isn't set directly.
            bool inherited = k.Parent != null && k.Font.Equals(k.Parent.Font);
            var b = new UiBase { Bounds = k.Bounds, Font = inherited ? null : k.Font, Margin = k.Margin, Padding = k.Padding, Max = k.MaximumSize };
            uiBase[k] = b;
            k.Disposed += (s, e) => uiBase.Remove((Control)s);
            return b;
        }

        void Apply(Control k, UiBase b)
        {
            if (b.Font != null) k.Font = ScaledFont(b.Font);
            k.Margin = new Padding(S(b.Margin.Left), S(b.Margin.Top), S(b.Margin.Right), S(b.Margin.Bottom));
            k.Padding = new Padding(S(b.Padding.Left), S(b.Padding.Top), S(b.Padding.Right), S(b.Padding.Bottom));
            if (b.Max != Size.Empty) k.MaximumSize = new Size(SX(b.Max.Width), S(b.Max.Height));

            bool bottomAnchored = (k.Anchor & AnchorStyles.Bottom) != 0 && (k.Anchor & AnchorStyles.Top) == 0;
            // Things inside a flowing list (app chips) and the side menu keep even proportions.
            bool even = k.Parent is FlowLayoutPanel || k.Parent == navPanel;
            int w = even ? S(b.Bounds.Width) : SX(b.Bounds.Width);
            int h = even ? S(b.Bounds.Height) : SY(b.Bounds.Height);
            switch (k.Dock)
            {
                case DockStyle.None:
                    if (bottomAnchored)
                    {
                        k.Size = new Size(w, h);
                        k.Location = new Point(S(b.Bounds.X), k.Parent.ClientSize.Height - k.Height - S(12));
                    }
                    else k.Bounds = new Rectangle(even ? S(b.Bounds.X) : SX(b.Bounds.X), even ? S(b.Bounds.Y) : SY(b.Bounds.Y), w, h);
                    break;
                case DockStyle.Left:
                case DockStyle.Right:
                    k.Width = S(b.Bounds.Width);
                    break;
                case DockStyle.Top:
                case DockStyle.Bottom:
                    k.Height = S(b.Bounds.Height);
                    break;
            }
        }
    }
}
