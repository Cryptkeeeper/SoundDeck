// Keeps the window tidy:
//  - the side menu is grouped (Listen / Talk / Stream), and Setup only shows while something it needs isn't done
//  - the Microphone page is a stack of sections that fold open and shut (open ones are remembered)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        // ---------- Side menu ----------

        // First entry is the group's heading; "" draws a thin line instead.
        static readonly string[][] NavGroups = {
            new[] { "Listen", "Mixer", "Equalizer", "Ducking" },
            new[] { "Talk", "Microphone", "Mic Mix", "Effects", "Soundboard" },
            new[] { "Stream", "Streaming" },
            new[] { "", "Profiles", "Settings" },
            new[] { "", "Testing" },   // TEMPORARY: see Testing.cs
        };
        readonly Dictionary<string, Label> navHeads = new Dictionary<string, Label>();
        readonly List<Panel> navLines = new List<Panel>();
        bool setupInMenu;
        int setupMissing;   // checks in a row that found a needed step not done

        void BuildNavGroups()
        {
            foreach (var g in NavGroups)
            {
                if (g[0] != "") navHeads[g[0]] = Lbl(navPanel, g[0], 18, 0, 8.5f, false, Dim);
                else navLines.Add(new Panel { Parent = navPanel, BackColor = Card2, Left = 18, Width = 144, Height = 1 });
            }
            // A fresh install starts on Setup; after that it only comes back if something it needs goes missing.
            setupInMenu = !Settings.GetBool("setup.complete", false);
            LayoutNav();
        }

        void LayoutNav()
        {
            if (navPanel == null || navHeads.Count == 0) return;
            Button setup = navButtons["Setup"];
            bool showSetup = setupInMenu || (pages.ContainsKey("Setup") && pages["Setup"].Visible);
            setup.Visible = showSetup;
            setup.Text = "   " + (setupInMenu ? "Finish setup" : "Setup");
            if (setup.ForeColor != Accent) setup.ForeColor = setupInMenu ? Warn : Fg; // Accent = the page that's open
            int y = 72, line = 0;
            if (showSetup) { PlaceNav(setup, 10, y, 160, 34); y += 42; }
            for (int gi = 0; gi < NavGroups.Length; gi++)
            {
                var g = NavGroups[gi];
                if (gi > 0) y += 8;
                if (g[0] != "") { MoveNav(navHeads[g[0]], 18, y); y += 22; }
                else { PlaceNav(navLines[line++], 18, y, 144, 1); y += 9; }
                for (int i = 1; i < g.Length; i++) { PlaceNav(navButtons[g[i]], 10, y, 160, 34); y += 36; }
            }
        }

        // The side menu keeps even proportions when the window scales (see Scaling.cs).
        void PlaceNav(Control c, int x, int y, int w, int h)
        {
            UiBase b;
            if (uiBase.TryGetValue(c, out b)) b.Bounds = new Rectangle(x, y, w, h);
            c.Bounds = new Rectangle(S(x), S(y), S(w), S(h));
        }

        void MoveNav(Control c, int x, int y)
        {
            UiBase b;
            if (uiBase.TryGetValue(c, out b)) b.Bounds = new Rectangle(x, y, b.Bounds.Width, b.Bounds.Height);
            c.Location = new Point(S(x), S(y));
        }

        // True when every needed Setup step is done.
        bool SetupComplete()
        {
            foreach (var s in setupSteps)
            {
                if (s.Kind != StepKind.Needed) continue;
                try
                {
                    if (s.Show != null && !s.Show()) continue;
                    if (!s.Done()) return false;
                }
                catch { return false; }
            }
            return true;
        }

        // Every 2 seconds. Setup leaves the menu as soon as everything's done, and comes back (as "Finish setup")
        // only after something has been missing for about 10 seconds, so a quick Mic Mix restart doesn't flash it.
        void TickSetupInMenu()
        {
            if (setupSteps == null) return;
            bool want = setupInMenu;
            if (SetupComplete()) { setupMissing = 0; want = false; }
            else if (++setupMissing >= 5) want = true;
            if (want == setupInMenu) return;
            setupInMenu = want;
            Settings.Set("setup.complete", !want);
            LayoutNav();
        }

        // ---------- Folding sections (Microphone page) ----------

        class Fold
        {
            public string Key;
            public int Order, BodyH;
            public Panel Head, Body;
            public Label Title, Sum;
            public Func<string> Summary;
        }

        readonly List<Fold> micFolds = new List<Fold>();
        static readonly string[] MicFoldOrder = { "noise", "voice", "hear", "dyn", "ptt" };
        const int MicFoldTop = 218, MicFoldX = 22, FoldW = 980, FoldHeadH = 40;
        Panel micFoldEnd;

        // Adds a section to the Microphone page. Build its controls into the returned panel (from y = 0, same x as
        // before), then call EndMicFold. Summary is shown on the heading, so you can see what's on with it shut.
        Panel MicFold(string key, string title, Func<string> summary)
        {
            var p = pages["Microphone"];
            var f = new Fold { Key = key, Order = Array.IndexOf(MicFoldOrder, key), Summary = summary };
            f.Head = new Panel { Parent = p, BackColor = Bg, Cursor = Cursors.Hand, Width = FoldW, Height = FoldHeadH };
            f.Title = Lbl(f.Head, title, 24, 9, 11f, true, Fg);
            f.Sum = Lbl(f.Head, "", 280, 12, 9.5f, false, Dim);
            f.Title.Cursor = f.Sum.Cursor = Cursors.Hand;
            EventHandler click = (s, e) => ToggleMicFold(f);
            f.Head.Click += click; f.Title.Click += click; f.Sum.Click += click;
            f.Head.Paint += (s, e) => PaintFoldHead(e.Graphics, f);
            tips.SetToolTip(f.Head, "Click to open or close");
            f.Body = new Panel { Parent = p, BackColor = Bg, Width = FoldW, Height = 10 };
            micFolds.Add(f);
            micFolds.Sort((a, b) => a.Order.CompareTo(b.Order));
            return f.Body;
        }

        void EndMicFold(Panel body)
        {
            var f = micFolds.First(x => x.Body == body);
            int bottom = 0;
            foreach (Control c in body.Controls) bottom = Math.Max(bottom, c.Bottom);
            f.BodyH = bottom + 16;
            LayoutMicFolds();
        }

        bool MicFoldOpen(string key)
        {
            return Settings.GetBool("fold." + key, key == "noise");
        }

        void ToggleMicFold(Fold f)
        {
            Settings.Set("fold." + f.Key, !MicFoldOpen(f.Key));
            LayoutMicFolds();
        }

        // Opens the Microphone page with one section open (the Mixer's Mic column uses this for noise removal).
        void ShowMicFold(string key)
        {
            if (!MicFoldOpen(key)) Settings.Set("fold." + key, true);
            ShowPage("Microphone");
            LayoutMicFolds();
        }

        void LayoutMicFolds()
        {
            var p = pages["Microphone"];
            p.SuspendLayout();
            int y = MicFoldTop;
            foreach (var f in micFolds)
            {
                bool open = MicFoldOpen(f.Key);
                PlaceScrolled(f.Head, MicFoldX, y, FoldW, FoldHeadH);
                y += FoldHeadH;
                f.Body.Visible = open;
                if (open) { PlaceScrolled(f.Body, MicFoldX, y, FoldW, f.BodyH); y += f.BodyH; }
                f.Head.Invalidate();
            }
            if (micFoldEnd == null) micFoldEnd = new Panel { Parent = p, BackColor = Bg, Width = 10, Height = 24 };
            PlaceScrolled(micFoldEnd, 0, y, 10, 24); // room at the bottom when scrolling
            p.ResumeLayout(true);
            UpdateMicFoldSums();
        }

        // Every second while the Microphone page shows.
        void UpdateMicFoldSums()
        {
            foreach (var f in micFolds)
            {
                string s = "";
                try { s = f.Summary() ?? ""; } catch { }
                if (f.Sum.Text != s) f.Sum.Text = s;
                f.Sum.ForeColor = s == "Off" || s == "" ? Dim : s == "Missing" ? Accent : Good;
            }
        }

        void PaintFoldHead(Graphics g, Fold f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Card2)) g.DrawLine(pen, 0, 0, f.Head.Width, 0);
            float cx = f.Title.Left / 2f, cy = f.Head.Height / 2f + 1, w = Math.Max(3f, Look.R(4.5f));
            PointF[] pts = MicFoldOpen(f.Key)
                ? new[] { new PointF(cx - w, cy - w / 2), new PointF(cx, cy + w / 2), new PointF(cx + w, cy - w / 2) }
                : new[] { new PointF(cx - w / 2, cy - w), new PointF(cx + w / 2, cy), new PointF(cx - w / 2, cy + w) };
            using (var pen = new Pen(Dim, Math.Max(1.6f, 1.6f * Look.Scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
        }

        // Like Place (Scaling.cs), for a page that may be scrolled.
        void PlaceScrolled(Control c, int x, int y, int w, int h)
        {
            UiBase b;
            if (uiBase.TryGetValue(c, out b)) b.Bounds = new Rectangle(x, y, w, h);
            var sc = c.Parent as ScrollableControl;
            Point off = sc != null ? sc.AutoScrollPosition : Point.Empty;
            c.Bounds = new Rectangle(SX(x) + off.X, SY(y) + off.Y, SX(w), SY(h));
        }
    }
}
