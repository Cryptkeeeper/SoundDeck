// "What's new" after an update: the first time a new version starts, show what changed.
// The notes are built into the exe (AppVersion.Notes, written by publish-update.ps1), so this works
// no matter which older version did the update. A fresh install shows nothing.

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundDeck
{
    partial class MainForm
    {
        bool whatsNewPending;

        void CheckWhatsNew()
        {
            string last = Settings.Get("app.lastver", Settings.Existed ? "old" : AppVersion.Value);
            bool test = Environment.GetEnvironmentVariable("SOUNDDECK_WHATSNEW") != null; // testing aid
            if (!test)
            {
                if (Updater.IsDevCopy) return;
                Settings.Set("app.lastver", AppVersion.Value);
                if (last == AppVersion.Value) return;
                try { if (last != "old" && new Version(last) > new Version(AppVersion.Value)) return; } catch { } // went back a version
            }

            // Wait a moment so the window (if any) is on screen first.
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) =>
            {
                t.Stop(); t.Dispose();
                if (Visible) ShowWhatsNew();
                else
                {
                    whatsNewPending = true;
                    tray.ShowBalloonTip(8000, "SoundDeck updated to " + AppVersion.Value, AppVersion.Notes == "" ? "You're on the latest version." : "Click here to see what's new.", ToolTipIcon.Info);
                }
            };
            t.Start();
            tray.BalloonTipClicked += (s, e) => { if (whatsNewPending) { whatsNewPending = false; ShowWindow(); ShowWhatsNew(); } };
        }

        void ShowWhatsNew()
        {
            whatsNewPending = false;
            using (var f = new Form())
            {
                f.Text = "What's new";
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = Bg; f.ForeColor = Fg; f.Font = new Font("Segoe UI", 9.5f);
                f.ClientSize = new Size(480, 200);
                f.HandleCreated += (s, e) => { try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } };

                var title = Lbl(f, "SoundDeck " + AppVersion.Value, 20, 16, 15f, true, Fg);
                var sub = Lbl(f, "You've been updated. Here's what's new:", 22, 50, 9.5f, false, Dim);
                string notes = AppVersion.Notes.Trim();
                if (notes == "") notes = "Fixes and small improvements.";
                notes = string.Join("\n", notes.Split('\n').Select(l => l.Trim()).Where(l => l != "").Select(l => l.StartsWith("-") || l.StartsWith("•") ? "•  " + l.TrimStart('-', '•', ' ') : "•  " + l));
                var body = Lbl(f, notes, 22, 80, 10f, false, Fg);
                body.MaximumSize = new Size(436, 0);
                int bottom = body.Bottom + 24;
                var ok = Btn(f, "Got it", 360, bottom, 100, 32);
                ok.BackColor = Accent; ok.ForeColor = Color.White;
                ok.DialogResult = DialogResult.OK;
                f.AcceptButton = ok; f.CancelButton = ok;
                f.ClientSize = new Size(480, ok.Bottom + 16);
                f.ShowDialog(this);
            }
        }
    }
}
