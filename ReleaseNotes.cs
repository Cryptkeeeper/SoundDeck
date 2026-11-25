// "What's new" lists every version since the one you had, not just the latest.
// The notes for every version are built into the exe from ReleaseNotes.txt (publish-update.ps1 adds each release).
// Kept apart from MainForm so it can be tested on its own (only needs Theme.cs).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundDeck
{
    static class ReleaseNotes
    {
        public const int MaxShown = 5;   // more than this and the rest is summed up in one line

        public class Entry
        {
            public string Version;
            public string[] Lines;
        }

        // "1.9.3=line|line|..." per line, newest first; "#" lines are comments.
        public static List<Entry> Parse(string text)
        {
            var list = new List<Entry>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string l = raw.Trim();
                int eq = l.IndexOf('=');
                if (l == "" || l.StartsWith("#") || eq <= 0) continue;
                System.Version v;
                if (!System.Version.TryParse(l.Substring(0, eq).Trim(), out v)) continue;
                list.Add(new Entry { Version = l.Substring(0, eq).Trim(), Lines = SplitLines(l.Substring(eq + 1)) });
            }
            return list;
        }

        // Notes are written "first line|second line" (publish-update.ps1); real line breaks work too.
        public static string[] SplitLines(string notes)
        {
            return (notes ?? "").Split('|', '\n')
                .Select(s => s.Trim().TrimStart('-', '•', ' ').Trim())
                .Where(s => s != "").ToArray();
        }

        // What to show after updating from lastSeen to current, newest first. lastSeen "old" means a version from
        // before "What's new" existed (so everything recent); anything else unreadable shows just the current notes.
        public static List<Entry> Missed(List<Entry> all, string lastSeen, string current, string currentNotes)
        {
            System.Version cur, from = new System.Version(0, 0);
            if (!System.Version.TryParse(current ?? "", out cur)) cur = new System.Version(0, 0);
            bool known = lastSeen == "old" || System.Version.TryParse(lastSeen ?? "", out from);
            var list = all
                .Where(e => { var v = System.Version.Parse(e.Version); return v <= cur && (known ? v > from : v == cur); })
                .GroupBy(e => System.Version.Parse(e.Version)).Select(g => g.First())
                .OrderByDescending(e => System.Version.Parse(e.Version)).ToList();
            // A copy built without its own line (or without the list at all) still shows its own notes.
            if (!list.Any(e => System.Version.Parse(e.Version) == cur))
                list.Insert(0, new Entry { Version = current, Lines = SplitLines(currentNotes) });
            return list;
        }

        public static List<Entry> Load()
        {
            try
            {
                using (var s = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("ReleaseNotes.txt"))
                {
                    if (s == null) return new List<Entry>();
                    using (var r = new StreamReader(s)) return Parse(r.ReadToEnd());
                }
            }
            catch { return new List<Entry>(); }
        }

        // ---------- The window ----------

        static readonly Color Bg = Color.FromArgb(18, 20, 24), Fg = Color.FromArgb(232, 234, 237),
            Dim = Color.FromArgb(154, 160, 166), Accent = Color.FromArgb(255, 106, 61);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static Label AddLabel(Control p, string text, int x, int y, float size, bool bold, Color color)
        {
            var l = new Label { Text = text, Left = x, Top = y, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            p.Controls.Add(l);
            return l;
        }

        // shown: newest first (from Missed). from: the version they had, or null if it isn't known.
        public static Form BuildDialog(List<Entry> shown, string current, string from)
        {
            var f = new Form
            {
                Text = "What's new", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
                BackColor = Bg, ForeColor = Fg, Font = new Font("Segoe UI", 9.5f), ClientSize = new Size(480, 200)
            };
            f.HandleCreated += (s, e) => { try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } };

            var list = shown.Take(MaxShown).ToList();
            int more = shown.Count - list.Count;
            bool several = list.Count > 1;
            AddLabel(f, "SoundDeck " + current, 20, 16, 15f, true, Fg);
            AddLabel(f, several ? "You've been updated" + (from != null ? " from " + from : "") + ". Here's what's new since then:"
                            : "You've been updated. Here's what's new:", 22, 50, 9.5f, false, Dim);

            // Scrolls if a lot was missed, so the window never outgrows the screen.
            var body = new Panel { Left = 0, Top = 78, Width = 480, Height = 10, AutoScroll = true, BackColor = Bg };
            f.Controls.Add(body);
            int y = 2;
            foreach (var e in list)
            {
                if (several) { AddLabel(body, e.Version, 22, y, 10f, true, Accent); y += 24; }
                var lines = e.Lines.Length == 0 ? new[] { "Fixes and small improvements." } : e.Lines;
                foreach (var line in lines)
                {
                    AddLabel(body, "•", 24, y, 10f, false, Dim);
                    var t = AddLabel(body, line, 40, y, 10f, false, Fg);
                    t.MaximumSize = new Size(408, 0);
                    y = t.Bottom + 6;
                }
                y += several ? 10 : 0;
            }
            if (more > 0) { AddLabel(body, "Plus " + more + " earlier update" + (more == 1 ? "" : "s") + ".", 22, y, 9.5f, false, Dim); y += 24; }
            body.Height = Math.Min(y, 420);

            var ok = new Button
            {
                Text = "Got it", Left = 360, Top = body.Bottom + 16, Width = 100, Height = 32, FlatStyle = FlatStyle.Flat,
                BackColor = Accent, ForeColor = Color.White, Cursor = Cursors.Hand, DialogResult = DialogResult.OK
            };
            ok.FlatAppearance.BorderSize = 0;
            f.Controls.Add(ok);
            f.AcceptButton = ok; f.CancelButton = ok;
            f.ClientSize = new Size(480, ok.Bottom + 16);
            Look.Apply(f);
            return f;
        }
    }
}
