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
                    How = "Then Harmony and games that use the \"Default\" mic get SoundDeck without changing their settings. SoundDeck still listens to your real mic directly.",
                    Done = () => { var v = VirtualCaptureDevice(); return v != null && Audio.GetDefaultId(EDataFlow.Capture) == v.Id; },
                    Note = () => VirtualCaptureDevice() == null ? "Do step 1 first (and restart your PC if you just installed it)." : null,
                    Act = () => { var v = VirtualCaptureDevice(); if (v != null) Audio.SetDefault(v.Id); } },
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
                    Title = "Stream Deck plugin", Kind = StepKind.Optional, ButtonText = "Get installer",
                    How = "Adds SoundDeck's keys and dials to your Stream Deck. Run SoundDeck's installer again and tick the Stream Deck plugin; after that it updates by itself.",
                    Show = () => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Elgato\StreamDeck\StreamDeck.exe")),
                    Done = () => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Elgato\StreamDeck\Plugins\com.sounddeck.controls.sdPlugin\manifest.json")),
                    Act = () => { try { Process.Start(SetupUrl); } catch { } } },
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
