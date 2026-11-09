// TEMPORARY: the "Testing" page. A checklist of things that need trying on real setups
// (GoXLR, Stream Deck, real voices), with Works / Broken / Skip per item and a "Copy results" button.
// To remove it later: delete this file, take it out of build.bat, and drop "Testing" from the nav and BuildTesting().

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        class TestItem
        {
            public string Area, Id, Since, Title, How;
            public Button Ok, Bad, Skip;
            public TextBox Note;
            public TestItem(string area, string id, string since, string title, string how) { Area = area; Id = id; Since = since; Title = title; How = how; }
        }

        // Since = the version the feature was added or last changed. A result from an older version doesn't count.
        static readonly TestItem[] TestItems =
        {
            new TestItem("Voice effects", "fx.each", "1.8.0", "Try each voice effect", "Turn on Mic Mix and Hear yourself, then pick each effect on the Effects page and talk. Each should sound clean, with no crackles or sudden loud bits."),
            new TestItem("Voice effects", "fx.friends", "1.8.0", "Friends hear the effect", "Ask someone in Harmony: they should hear the effect while it's on, and your normal voice when it's off."),
            new TestItem("Voice effects", "fx.strength", "1.8.0", "Strength slider", "Move Strength while talking. The effect should get stronger or weaker smoothly."),
            new TestItem("Voice effects", "fx.hotkey", "1.8.0", "Effect hotkey", "Pick a hotkey on the Effects page and press it while a game is focused. The effect should switch on and off."),

            new TestItem("Bleep", "bleep.key", "1.8.0", "Bleep key", "Pick a bleep key and hold it while talking. Friends should hear a beep instead of your voice, and your voice again as soon as you let go."),
            new TestItem("Bleep", "bleep.game", "1.8.0", "Bleep in a game", "Hold the bleep key while a full-screen game is focused. It should still bleep."),
            new TestItem("Bleep", "bleep.stream", "1.8.0", "Bleep on stream", "Record or stream a few seconds while bleeping. The beep should be on the stream too."),

            new TestItem("Microphone", "mic.gate", "1.8.4", "Gate", "On the Microphone page, turn on the gate. When you're quiet, background noise should cut out. When you talk, the start of your words shouldn't be cut off."),
            new TestItem("Microphone", "mic.comp", "1.8.4", "Compressor", "Turn on the compressor, then talk quietly and then loudly. Friends should hear a more even volume."),
            new TestItem("Microphone", "mic.hear", "1.7.0", "Hear yourself", "Turn on Hear yourself. You should hear your mic in your headset with very little delay."),
            new TestItem("Microphone", "mic.ptt", "1.7.0", "Push-to-talk", "Set a push-to-talk key. Friends should only hear you while it's held, including while a game is focused."),

            new TestItem("Streaming", "mon.stream", "1.8.0", "Listen to your stream", "On the Streaming page, turn on Listen with \"What your stream hears\". You should hear your stream's mix in your headset."),
            new TestItem("Streaming", "mon.discord", "1.8.0", "Listen to what Harmony hears", "Switch Listen to \"What Harmony hears\". You should hear what your friends hear from you."),
            new TestItem("Streaming", "mon.voice", "1.8.0", "Include my voice", "Tick \"Include my voice\" while listening. You should hear yourself, slightly late."),

            new TestItem("Stream Deck", "sd.update", "1.8.0", "New keys showed up", "In the Stream Deck app, the SoundDeck list should have Voice effect, Bleep and Listen to stream, without reinstalling anything."),
            new TestItem("Stream Deck", "sd.fx", "1.8.0", "Voice effect key", "Press it: the effect turns on and the key lights up. Press again to turn it off."),
            new TestItem("Stream Deck", "sd.bleep", "1.8.0", "Bleep key", "Hold it while talking: you should be bleeped until you let go."),
            new TestItem("Stream Deck", "sd.mon", "1.8.0", "Listen to stream key", "Press it: Listen turns on (the key changes) and you hear your stream. Press again to stop."),
            new TestItem("Stream Deck", "sd.old", "1.7.0", "Older keys still work", "Volume dials, mute, output and profile keys still do what they did before."),

            new TestItem("GoXLR", "gox.install", "1.8.6", "Add GoXLR control", "Close TC-Helicon's GoXLR App, then on the Settings page click Add GoXLR control. Windows should ask permission once, with no other setup windows, and it should say Connected within a minute."),
            new TestItem("GoXLR", "gox.auto", "1.8.6", "Starts by itself", "Restart the PC (or quit GoXLR Utility from its tray icon and reopen SoundDeck). SoundDeck should connect to the GoXLR again on its own."),
            new TestItem("GoXLR", "gox.setup", "1.7.0", "Set up GoXLR", "On the Settings page, click Set up GoXLR. SoundDeck's channels should go to the GoXLR's channels."),
            new TestItem("GoXLR", "gox.faders", "1.7.0", "Faders and mute buttons", "With GoXLR Utility running, moving a GoXLR fader or mute button should change the SoundDeck channel, and the other way around."),

            new TestItem("Setup and updates", "setup.fresh", "1.8.2", "Fresh setup", "Run SoundDeck-Setup.exe on a PC that doesn't have SoundDeck. It should finish and start SoundDeck, even if it asks you to restart part way."),
            new TestItem("Setup and updates", "setup.extras", "1.8.1", "Missing extras offered", "Settings > Check for updates. If noise removal, Equalizer APO, VB-CABLE or GoXLR Utility is missing, it should offer to install it."),
            new TestItem("Setup and updates", "upd.auto", "1.8.4", "Updated by itself", "This version arrived through the update check, and the What's new window showed afterwards."),
            new TestItem("Setup and updates", "upd.hotkeys", "1.8.4", "Hotkeys kept", "Hotkeys you had set before updating still work."),

            new TestItem("Everything else", "gen.look", "1.8.4", "Everything looks right", "Click through every page at your normal window size. Nothing should be cut off, overlapping or hard to read."),
            new TestItem("Everything else", "gen.crash", "1.7.0", "No crashes or freezes", "Use SoundDeck normally for a day. It shouldn't crash, freeze, or make your audio stutter."),
        };

        Label testProgress, testCopied;
        static readonly Color TestBad = Color.FromArgb(232, 72, 72);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendCue(IntPtr h, int msg, IntPtr w, string l);

        void BuildTesting()
        {
            var p = NewPage("Testing", "Help test", "Things that need trying on real setups. Mark each one, add a note if something's off, then copy the results and send them over. Skip anything you don't have (like a GoXLR).");

            testProgress = Lbl(p, "", 25, 96, 10f, true, Fg);
            var copy = Btn(p, "Copy results", 700, 88, 150, 34);
            copy.BackColor = Accent; copy.ForeColor = Color.White;
            copy.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(TestReport()); testCopied.Text = "Copied. Paste it in a message."; testCopied.ForeColor = Good; }
                catch { testCopied.Text = "Couldn't copy. Try again."; testCopied.ForeColor = Warn; }
            };
            var clear = Btn(p, "Start over", 860, 88, 110, 34);
            clear.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Clear all your test results and notes?", "SoundDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var t in TestItems) { Settings.Remove("test." + t.Id); Settings.Remove("test." + t.Id + ".note"); t.Note.Text = ""; ShowTest(t); }
                TestChanged();
            };
            testCopied = Lbl(p, "", 980, 96, 9f, false, Dim);

            int y = 140;
            string area = null;
            foreach (var t in TestItems)
            {
                var it = t;
                if (t.Area != area)
                {
                    area = t.Area;
                    y += 14;
                    Lbl(p, area, 22, y, 12f, true, Fg);
                    y += 34;
                }
                Lbl(p, t.Title, 25, y, 10f, true, Fg);
                var how = Lbl(p, t.How, 25, y + 22, 9f, false, Dim);
                how.MaximumSize = new Size(640, 0);
                int textH = 22 + TextRenderer.MeasureText(t.How, how.Font, new Size(640, 0), TextFormatFlags.WordBreak).Height;

                t.Ok = Btn(p, "Works", 700, y, 76, 30);
                t.Bad = Btn(p, "Broken", 782, y, 76, 30);
                t.Skip = Btn(p, "Skip", 864, y, 60, 30);
                t.Ok.Click += (s, e) => SetTest(it, "ok");
                t.Bad.Click += (s, e) => SetTest(it, "bad");
                t.Skip.Click += (s, e) => SetTest(it, "skip");

                t.Note = new TextBox { Left = 940, Top = y + 2, Width = 220, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
                t.Note.Text = TestResult(t) != null ? Settings.Get("test." + t.Id + ".note", "") : "";
                t.Note.HandleCreated += (s, e) => SendCue(it.Note.Handle, 0x1501, (IntPtr)1, "Note (what went wrong?)");
                t.Note.TextChanged += (s, e) =>
                {
                    string n = it.Note.Text.Trim();
                    if (n.Length == 0) Settings.Remove("test." + it.Id + ".note"); else Settings.Set("test." + it.Id + ".note", n);
                    // Writing a note about a problem counts as trying it.
                    if (n.Length > 0 && TestResult(it) == null) SetTest(it, "bad"); else TestChanged();
                };
                p.Controls.Add(t.Note);
                ShowTest(t);

                y += Math.Max(textH, 36) + 18;
            }
            new Panel { Parent = p, Left = 0, Top = y, Width = 10, Height = 24, BackColor = Bg }; // bottom padding when scrolling
            TestChanged();
        }

        // "ok", "bad", "skip", or null when not tried in a version that has the current feature.
        string TestResult(TestItem t)
        {
            string v = Settings.Get("test." + t.Id, null);
            if (v == null) return null;
            int bar = v.IndexOf('|');
            if (bar < 0) return null;
            Version at, since;
            if (!Version.TryParse(v.Substring(bar + 1), out at) || !Version.TryParse(t.Since, out since) || at < since) return null;
            return v.Substring(0, bar);
        }

        void SetTest(TestItem t, string r)
        {
            // Clicking the selected button again un-marks it.
            if (TestResult(t) == r) Settings.Remove("test." + t.Id);
            else Settings.Set("test." + t.Id, r + "|" + AppVersion.Value);
            ShowTest(t);
            TestChanged();
        }

        void ShowTest(TestItem t)
        {
            string r = TestResult(t);
            MarkTestButton(t.Ok, r == "ok", Good, Bg);
            MarkTestButton(t.Bad, r == "bad", TestBad, Color.White);
            MarkTestButton(t.Skip, r == "skip", Color.FromArgb(90, 96, 108), Color.White);
        }

        static void MarkTestButton(Button b, bool on, Color back, Color fore)
        {
            b.BackColor = on ? back : Card2;
            b.ForeColor = on ? fore : Dim;
            b.Font = new Font("Segoe UI", 9.5f, on ? FontStyle.Bold : FontStyle.Regular);
        }

        void TestChanged()
        {
            int done = 0, bad = 0;
            foreach (var t in TestItems)
            {
                string r = TestResult(t);
                if (r != null) done++;
                if (r == "bad") bad++;
            }
            testProgress.Text = done + " of " + TestItems.Length + " checked" + (bad > 0 ? ",  " + bad + " broken" : "");
            testProgress.ForeColor = bad > 0 ? TestBad : done == TestItems.Length ? Good : Fg;
            if (testCopied != null) testCopied.Text = "";
            // Also kept as a file next to the settings, so results on this PC can be read without copying.
            try { File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundDeck", "test-results.txt"), TestReport()); }
            catch { }
        }

        // Environment.OSVersion says 6.2 without an app manifest, so read the real build from the registry.
        static string WindowsName()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build; int.TryParse(Convert.ToString(k.GetValue("CurrentBuild")), out build);
                    string disp = Convert.ToString(k.GetValue("DisplayVersion"));
                    return "Windows " + (build >= 22000 ? "11" : "10") + (disp.Length > 0 ? " " + disp : "") + " (build " + build + ")";
                }
            }
            catch { return "Windows " + Environment.OSVersion.Version; }
        }

        string TestReport()
        {
            bool sd = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Elgato\StreamDeck\StreamDeck.exe"));
            var sb = new StringBuilder();
            sb.AppendLine("SoundDeck test results");
            sb.AppendLine("Version " + AppVersion.Value + ", " + WindowsName() + ", GoXLR " + (HasGoXlr ? "yes" : "no") + ", Stream Deck " + (sd ? "yes" : "no"));
            var groups = new[] { "bad", "ok", "skip", null };
            var heads = new[] { "BROKEN", "Works", "Skipped", "Not tried yet" };
            for (int g = 0; g < groups.Length; g++)
            {
                var lines = new List<string>();
                foreach (var t in TestItems)
                {
                    if (TestResult(t) != groups[g]) continue;
                    string note = groups[g] == null ? "" : Settings.Get("test." + t.Id + ".note", "");
                    lines.Add("- " + t.Area + ": " + t.Title + (note.Length > 0 ? "  (" + note + ")" : ""));
                }
                if (lines.Count == 0) continue;
                sb.AppendLine();
                sb.AppendLine(heads[g] + " (" + lines.Count + ")");
                foreach (var l in lines) sb.AppendLine(l);
            }
            return sb.ToString();
        }
    }
}
