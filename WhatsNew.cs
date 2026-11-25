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
        string whatsNewFrom;   // the version that was running before this update ("old" = from before 1.8.4)

        void CheckWhatsNew()
        {
            string last = Settings.Get("app.lastver", Settings.Existed ? "old" : AppVersion.Value);
            // Testing aid: SOUNDDECK_WHATSNEW=1 shows the window; =1.9.0 shows it as if updating from 1.9.0.
            string testFrom = Environment.GetEnvironmentVariable("SOUNDDECK_WHATSNEW");
            bool test = testFrom != null;
            whatsNewFrom = test ? testFrom : last;
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

        // Lists every version since the one that was running before (ReleaseNotes.cs), newest first.
        void ShowWhatsNew()
        {
            whatsNewPending = false;
            var missed = ReleaseNotes.Missed(ReleaseNotes.Load(), whatsNewFrom, AppVersion.Value, AppVersion.Notes);
            Version v;
            string from = Version.TryParse(whatsNewFrom ?? "", out v) ? whatsNewFrom : null;
            using (var f = ReleaseNotes.BuildDialog(missed, AppVersion.Value, from)) f.ShowDialog(this);
        }
    }
}
