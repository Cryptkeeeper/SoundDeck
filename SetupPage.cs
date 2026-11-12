// The "Setup" page: everything SoundDeck needs, as a live checklist.
// Each step checks itself every second while the page is open and has a button that does it (or goes where it's done).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        // Kind: what happens when the step isn't done.
        enum StepKind { Needed, Recommended, Optional }

        class SetupStep
        {
            public string Title, How, ButtonText;
            public StepKind Kind;
            public Func<bool> Show;          // null = always shown
            public Func<bool> Done;
            public Func<bool> CanAct;        // null = the button always works; false greys it out (an earlier step comes first)
            public Func<string> Note;        // optional extra status line (e.g. why it isn't done yet)
            public Action Act;
            public Panel Row;
            public Label Mark, Status;
            public Button Button;
        }

        List<SetupStep> setupSteps;
        Label setupProgress;
        string setupLayoutKey;
        CheckBox settingsStartup;

        const string SetupUrl = "https://github.com/Cryptkeeeper/SoundDeck/releases/latest/download/SoundDeckSetup.exe";

        void BuildSetup()
        {
            var p = NewPage("Setup", "Setup", "Everything SoundDeck needs, in order. Steps tick themselves off when they're done; the button next to each one does it for you or takes you to the right place.");
            setupProgress = Lbl(p, "", 25, 96, 11f, true, Fg);

            setupSteps = new List<SetupStep>
            {
                new SetupStep {
                    Title = "1.  Install the virtual mic (VB-CABLE)", Kind = StepKind.Needed, ButtonText = "Install",
                    How = "SoundDeck sends your voice, music, sounds and effects to Harmony and games through it. Free, 1 MB from vb-audio.com. Windows asks permission, then click Install Driver. Restart your PC afterwards.",
                    Done = () => FindCable() != null,
                    Act = () => InstallExtra("vbcable") },
                new SetupStep {
                    Title = "2.  Pick the mic you talk into", Kind = StepKind.Needed, ButtonText = "Open Microphone",
                    How = "On the Microphone page, choose your real mic, like Headset Microphone, or Chat Mic if you have a GoXLR. Not CABLE Output.",
                    Done = () => { var m = micDevice.SelectedItem as DeviceItem; return m != null && m.Id != null && !IsVirtualMic(m.Name); },
                    Act = () => ShowPage("Microphone") },
                new SetupStep {
                    Title = "3.  Turn on Mic Mix", Kind = StepKind.Needed, ButtonText = "Turn on",
                    How = "Your mic goes through SoundDeck, which adds noise removal, voice effects, the bleep, the soundboard and music. Keep SoundDeck running (it sits in the tray).",
                    Done = () => mixOn.Checked && mixEngine.Running,
                    Note = () => mixOn.Checked && !mixEngine.Running ? "It's turned on but not running yet: " + mixEngine.Status : null,
                    Act = () => { if (!mixOn.Checked) mixOn.Checked = true; else ShowPage("Mic Mix"); } },
                new SetupStep {
                    Title = "4.  Make the virtual mic your Windows mic", Kind = StepKind.Needed, ButtonText = "Do it for me",
                    How = "Then Harmony and games that use the \"Default\" mic get SoundDeck without changing their settings. Windows goes back to your real mic whenever Mic Mix is off or you close SoundDeck, so you aren't left silent.",
                    Done = () => { var v = VirtualCaptureDevice(); return MicAsDefault && v != null && Audio.GetDefaultId(EDataFlow.Capture) == v.Id; },
                    // The virtual mic is silent until Mic Mix runs, so this waits for step 3.
                    CanAct = () => VirtualCaptureDevice() != null && mixEngine.Running,
                    Note = () => VirtualCaptureDevice() == null ? "Do step 1 first (and restart your PC if you just installed it)."
                        : !mixEngine.Running ? "Do step 3 first. The virtual mic is silent until Mic Mix is running." : null,
                    Act = () => { if (!mixAsDefault.Checked) mixAsDefault.Checked = true; else TickDefaultMic(); } },
                new SetupStep {
                    Title = "5.  Set Harmony's mic to Default", Kind = StepKind.Needed, ButtonText = "I did this",
                    How = "In Harmony's voice settings, set the microphone (input device) to Default. Do the same in any game where you picked a mic by name, and restart games that were already open.",
                    Done = () => Settings.GetBool("setup.harmony", false),
                    Act = () => Settings.Set("setup.harmony", !Settings.GetBool("setup.harmony", false)) },
                new SetupStep {
                    Title = "6.  Sound plays in your headset", Kind = StepKind.Needed, ButtonText = "Fix it",
                    How = "Windows' default output should be your headset or speakers, never CABLE Input (that would play everything into your mic). SoundDeck fixes this by itself, so this is normally already done.",
                    Done = () => { string id = Audio.GetDefaultId(EDataFlow.Render); var d = renderDevs.FirstOrDefault(x => x.Id == id); return d != null && !IsVirtualCableOut(d.Name); },
                    Act = GuardDefaultOutput },
                new SetupStep {
                    Title = "Noise removal", Kind = StepKind.Recommended, ButtonText = "Install",
                    How = "Removes fans, keyboard and background noise from your mic. Free, from its official GitHub page.",
                    Done = () => RnNoise.Available,
                    Act = () => InstallExtra("noise") },
                new SetupStep {
                    Title = "Start SoundDeck with Windows", Kind = StepKind.Recommended, ButtonText = "Turn on",
                    How = "Mic Mix only works while SoundDeck runs, so it's easiest to have it start by itself (hidden in the tray).",
                    Done = IsStartupOn,
                    Act = () => { SetStartup(true); if (settingsStartup != null) settingsStartup.Checked = true; } },
                new SetupStep {
                    Title = "GoXLR control", Kind = StepKind.Recommended, ButtonText = "Install",
                    How = "Lets SoundDeck move your GoXLR's faders and mutes. Close TC-Helicon's GoXLR App first; SoundDeck installs the free GoXLR Utility and runs it hidden.",
                    Show = () => HasGoXlr,
                    Done = GoxUtilityInstalled,
                    Act = () => InstallExtra("goxutil") },
                new SetupStep {
                    Title = "Stream Deck plugin", Kind = StepKind.Optional, ButtonText = "Install",
                    How = "Adds SoundDeck's keys and dials to your Stream Deck. The Stream Deck app closes for a few seconds and opens again; after that the plugin updates by itself.",
                    Show = () => File.Exists(StreamDeckExe),
                    Done = () => File.Exists(Path.Combine(PluginDir, "manifest.json")),
                    CanAct = () => !pluginInstalling,
                    Act = InstallPluginNow },
                new SetupStep {
                    Title = "Equalizer", Kind = StepKind.Optional, ButtonText = "Install",
                    How = "Only for the Equalizer page, headphone tuning and the mic filters. Free Equalizer APO; Windows asks permission, then tick your headset and mic and restart.",
                    Done = () => EqApo.Installed,
                    Act = () => InstallExtra("apo") },
                new SetupStep {
                    Title = "Streamer mode", Kind = StepKind.Optional, ButtonText = "Open Streaming",
                    How = "Only if you stream: gives OBS or Streamlabs its own mix, so you can mute yourself in Harmony while your stream still hears you.",
                    Done = () => StreamerOn,
                    Act = () => ShowPage("Streaming") },
            };

            foreach (var st in setupSteps)
            {
                var s = st;
                s.Row = new Panel { Left = 25, Top = 0, Width = 1000, Height = 74, BackColor = Card };
                p.Controls.Add(s.Row);
                s.Mark = Lbl(s.Row, "", 12, 12, 13f, true, Dim);
                Lbl(s.Row, s.Title + (s.Kind == StepKind.Optional ? "  (optional)" : s.Kind == StepKind.Recommended ? "  (recommended)" : ""), 46, 10, 10.5f, true, Fg);
                var how = Lbl(s.Row, s.How, 48, 34, 9f, false, Dim);
                how.MaximumSize = new Size(760, 0);
                s.Status = Lbl(s.Row, "", 48, 0, 8.5f, false, Warn);
                s.Button = Btn(s.Row, s.ButtonText, 840, 18, 140, 34);
                s.Button.Click += (o, e) => { try { s.Act(); } catch { } UpdateSetup(); };
            }
            LayoutSetup(true);
        }

        void InstallExtra(string key)
        {
            if (extrasOpen) return;
            var x = AllExtras().FirstOrDefault(e => e.Key == key);
            if (x != null) ShowExtras(new List<Extra> { x });
        }

        // The recording side of the virtual mic: what Harmony and games should listen to.
        AudioDevice VirtualCaptureDevice()
        {
            var cable = FindCable();
            if (cable == null) return null;
            string want = IsSteamCable(cable) ? "Steam Streaming Microphone" : "CABLE Output";
            return captureDevs.FirstOrDefault(d => d.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---------- The Windows default mic follows Mic Mix ----------
        // While Mic Mix runs, the virtual mic is the Windows default mic, so apps set to "Default" get SoundDeck's mix.
        // The virtual mic is silent whenever Mic Mix isn't feeding it, so when Mic Mix stops or SoundDeck closes,
        // the default goes back to the real mic. (If SoundDeck is killed, it can't; the next start puts it right.)

        internal static MainForm Current;   // for the crash handler, which has no window to ask
        CheckBox mixAsDefault;
        bool asDefaultSyncing;
        int micDefaultIdle;

        bool MicAsDefault { get { return Settings.GetBool("mic.asDefault", false); } }

        // The mic picked on the Microphone page, if it's a real one that's plugged in.
        AudioDevice RealMicDevice()
        {
            // Testing aid: SOUNDDECK_TEST_REALMIC=<part of a recording device's name> stands in for the real mic,
            // so the hand-back can be tried on a PC that has no real mic plugged in.
            string test = Environment.GetEnvironmentVariable("SOUNDDECK_TEST_REALMIC");
            if (!string.IsNullOrEmpty(test)) return captureDevs.FirstOrDefault(d => d.Name.IndexOf(test, StringComparison.OrdinalIgnoreCase) >= 0);
            var m = micDevice.SelectedItem as DeviceItem;
            if (m == null || m.Id == null || IsVirtualMic(m.Name)) return null;
            return captureDevs.FirstOrDefault(d => d.Id == m.Id);
        }

        // Every second.
        void TickDefaultMic()
        {
            var v = VirtualCaptureDevice();
            if (v == null || mixOn == null) return;
            string def = Audio.GetDefaultId(EDataFlow.Capture);
            // Never asked, but Mic Mix is on and the virtual mic already is the Windows mic (set by hand, or by
            // VB-CABLE's installer): look after it from now on, so closing SoundDeck doesn't leave a silent mic.
            if (Settings.Get("mic.asDefault", null) == null)
            {
                if (def != v.Id || !mixOn.Checked) return;
                Settings.Set("mic.asDefault", true);
                if (mixAsDefault != null) { asDefaultSyncing = true; mixAsDefault.Checked = true; asDefaultSyncing = false; }
            }
            if (!MicAsDefault) return;
            if (mixEngine.Running)
            {
                micDefaultIdle = 0;
                if (def != v.Id) Audio.SetDefault(v.Id);
            }
            // Three seconds' grace, so a quick Mic Mix restart doesn't flip the Windows mic back and forth.
            else if (++micDefaultIdle >= 3) RestoreRealMic();
        }

        // Puts the Windows default mic back on the real mic, if it's on the virtual one because of the setting.
        internal void RestoreRealMic()
        {
            try
            {
                if (!MicAsDefault) return;
                var v = VirtualCaptureDevice();
                var real = RealMicDevice();
                if (v == null || real == null) return; // no real mic plugged in: nothing to go back to
                if (Audio.GetDefaultId(EDataFlow.Capture) == v.Id) Audio.SetDefault(real.Id);
            }
            catch { }
        }

        // The tick box on the Mic Mix page (step 4 on the Setup page ticks the same box).
        void BuildMicAsDefault(Panel p, int y)
        {
            mixAsDefault = Chk(p, "Make the virtual mic my Windows mic while Mic Mix is on", 25, y);
            mixAsDefault.Checked = MicAsDefault;
            mixAsDefault.CheckedChanged += (s, e) =>
            {
                if (asDefaultSyncing) return;
                if (!mixAsDefault.Checked) RestoreRealMic(); // while the setting still says yes
                Settings.Set("mic.asDefault", mixAsDefault.Checked);
                micDefaultIdle = 0;
                TickDefaultMic();
            };
            Lbl(p, "Harmony and games set to \"Default\" then use SoundDeck. Windows goes back to your real mic when Mic Mix or SoundDeck is off.", 45, y + 24, 8.5f, false, Dim);
        }

        // ---------- Stream Deck plugin ----------
        // The plugin is packed inside SoundDeck.exe (see build.bat), so adding it needs no download.

        static string StreamDeckExe { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Elgato\StreamDeck\StreamDeck.exe"); } }
        static string PluginDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Elgato\StreamDeck\Plugins\com.sounddeck.controls.sdPlugin"); } }
        bool pluginInstalling;

        void InstallPluginNow()
        {
            if (pluginInstalling) return;
            bool packed;
            using (var probe = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.zip")) packed = probe != null;
            // A copy built without the plugin inside: fall back to the installer, which has it.
            if (!packed) { try { Process.Start(SetupUrl); } catch { } return; }
            if (MessageBox.Show(this, "SoundDeck will add its keys and dials to your Stream Deck.\n\nThe Stream Deck app closes for a few seconds and opens again. Go ahead?",
                "SoundDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            pluginInstalling = true;
            string sdExe = StreamDeckExe, dest = PluginDir, self = Application.ExecutablePath;
            new System.Threading.Thread(() =>
            {
                string err = null;
                try
                {
                    // Its files can't be replaced while the Stream Deck app (and the plugin it runs) is open.
                    foreach (string name in new[] { "StreamDeck", "SoundDeckPlugin" })
                        foreach (var pr in Process.GetProcessesByName(name)) { try { pr.Kill(); pr.WaitForExit(5000); } catch { } pr.Dispose(); }
                    System.Threading.Thread.Sleep(1500);
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var zip = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.zip"))
                    using (var z = new System.IO.Compression.ZipArchive(zip)) System.IO.Compression.ZipFileExtensions.ExtractToDirectory(z, dest);
                    // The plugin starts this copy of SoundDeck when needed, and updates only touch a plugin that points here.
                    File.WriteAllText(Path.Combine(dest, "sounddeck_path.txt"), self);
                }
                catch (Exception ex) { err = ex.Message; }
                try { if (File.Exists(sdExe)) Process.Start(new ProcessStartInfo(sdExe) { UseShellExecute = true }); } catch { }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        pluginInstalling = false;
                        UpdateSetup();
                        if (err != null) MessageBox.Show(this, "The Stream Deck plugin wasn't added: " + err, "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        // Stacks the visible rows; only re-places them when which rows are shown (or their height) changes.
        void LayoutSetup(bool force)
        {
            var shown = setupSteps.Where(s => s.Show == null || s.Show()).ToList();
            string key = string.Join(",", shown.Select(s => s.Title + ":" + (s.Status.Text == "" ? 0 : 1)));
            if (!force && key == setupLayoutKey) return;
            setupLayoutKey = key;
            int y = 132;
            foreach (var s in setupSteps)
            {
                bool on = shown.Contains(s);
                s.Row.Visible = on;
                if (!on) continue;
                bool note = s.Status.Text != "";
                int h = note ? 96 : 74;
                if (s.Kind != StepKind.Needed && s == shown.First(x => x.Kind != StepKind.Needed)) y += 18; // gap before the extras
                Place(s.Status, 48, 72, 760, 18);
                Place(s.Row, 25, y, 1000, h);
                y += h + 8;
            }
        }

        void UpdateSetup()
        {
            if (setupSteps == null) return;
            int needed = 0, neededDone = 0;
            foreach (var s in setupSteps)
            {
                if (s.Show != null && !s.Show()) continue;
                bool done = false;
                try { done = s.Done(); } catch { }
                string note = null;
                if (!done && s.Note != null) { try { note = s.Note(); } catch { } }
                s.Status.Text = note ?? "";
                s.Mark.Text = done ? "✓" : s.Kind == StepKind.Needed ? "!" : "•";
                s.Mark.ForeColor = done ? Good : s.Kind == StepKind.Needed ? Accent : Dim;
                // Step 5 can't be checked automatically, so its button stays as a tick/untick.
                bool manual = s.ButtonText == "I did this";
                s.Button.Text = manual ? (done ? "Undo" : "I did this") : s.ButtonText;
                s.Button.Visible = manual || !done;
                bool can = true;
                if (s.CanAct != null) { try { can = s.CanAct(); } catch { } }
                s.Button.Enabled = can;
                if (s.Kind == StepKind.Needed) { needed++; if (done) neededDone++; }
            }
            LayoutSetup(false);
            setupProgress.Text = neededDone == needed
                ? "You're all set. Friends in Harmony hear you through SoundDeck."
                : neededDone + " of " + needed + " steps done";
            setupProgress.ForeColor = neededDone == needed ? Good : Fg;
        }

    }
}
