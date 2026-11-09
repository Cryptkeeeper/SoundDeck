// Level meters, auto-ducking, hear yourself, soundboard and profiles.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundDeck
{
    // Simple bar meter (0..1). Vertical unless Horizontal is set.
    class LevelMeter : Control
    {
        float value, marker = -1;
        readonly Color color;
        public bool Horizontal;

        // Optional white line (e.g. background-noise level); negative hides it.
        public float Marker
        {
            get { return marker; }
            set { if (Math.Abs(value - marker) < 0.004f) return; marker = value; Invalidate(); }
        }

        public LevelMeter(Color color)
        {
            this.color = color;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(22, 24, 29);
        }

        public float Value
        {
            get { return value; }
            set
            {
                float v = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(v - this.value) < 0.004f) return;
                this.value = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Look.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float rad = Math.Min(Width, Height) / 2f;
            var full = new RectangleF(0, 0, Width, Height);
            Look.FillRound(g, BackColor, full, rad);
            if (value > 0)
            {
                using (var clip = Look.Round(full, rad))
                using (var b = new SolidBrush(color))
                {
                    g.SetClip(clip);
                    if (Horizontal) g.FillRectangle(b, 0, 0, Width * value, Height);
                    else { float h = Height * value; g.FillRectangle(b, 0, Height - h, Width, h); }
                    g.ResetClip();
                }
            }
            if (marker >= 0)
            {
                using (var b = new SolidBrush(Color.White))
                {
                    if (Horizontal) g.FillRectangle(b, (int)(Width * marker) - 1, 0, 2, Height);
                    else g.FillRectangle(b, 0, Height - (int)(Height * marker) - 1, Width, 2);
                }
            }
        }
    }

    partial class MainForm
    {
        Timer fast;
        LevelMeter[] chMeter = new LevelMeter[4];
        float[] meterVal = new float[4];
        float[] duck = { 1f, 1f, 1f, 1f };

        // Converts a peak (0..1) to a meter position on a 54 dB scale.
        static float MeterPos(float peak)
        {
            if (peak <= 0.0001f) return 0;
            return Math.Max(0, Math.Min(1, (float)((20 * Math.Log10(peak) + 54) / 54)));
        }

        void OnFastTick()
        {
            var peak = new float[4];
            foreach (var s in lastSessions)
            {
                int ci = Array.IndexOf(Channels, ChannelFor(s.Exe));
                if (ci < 0) continue;
                float p = s.Peak();
                if (p > peak[ci]) peak[ci] = p;
            }

            bool showMeters = Visible && WindowState != FormWindowState.Minimized;
            for (int i = 0; i < 4; i++)
            {
                meterVal[i] = Math.Max(MeterPos(peak[i]), meterVal[i] - 0.04f);
                if (showMeters && pages["Mixer"].Visible) chMeter[i].Value = meterVal[i];
            }
            if (showMeters && pages["Ducking"].Visible) duckChatMeter.Value = meterVal[1];
            TickMicColumn(showMeters);
            TickStreamMeter(showMeters);
            TickDynamicsUi(showMeters);
            TickGox();
            TickPushToTalk();

            TickDucking(peak[1]);
        }

        // ---------- Mic column on the Mixer ----------

        static readonly Color MicColor = Color.FromArgb(240, 90, 120);
        static readonly Color Warn = Color.FromArgb(240, 200, 80);
        MicMeter micMeter = new MicMeter();
        ComboBox mcDev;
        TrackBar mcVol;
        Label mcPct, mcNoise, mcNoiseDb, mcHint;
        Button mcMute;
        LevelMeter mcMeter;
        float mcLevel;
        DateTime mcStarted;

        void BuildMicColumn(Panel p, int x)
        {
            var card = new Panel { Left = x, Top = 86, Width = 180, Height = 590, BackColor = Card };
            p.Controls.Add(card);
            card.Controls.Add(new Panel { Left = 0, Top = 0, Width = 180, Height = 4, BackColor = MicColor });
            Lbl(card, "Mic", 12, 14, 14f, true, MicColor);
            Lbl(card, "Input", 14, 48, 8.5f, false, Dim);

            mcDev = Combo(card, 12, 66, 156);
            mcDev.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = mcDev.SelectedItem as DeviceItem;
                if (it == null) return;
                for (int i = 0; i < micDevice.Items.Count; i++)
                    if (((DeviceItem)micDevice.Items[i]).Id == it.Id) { micDevice.SelectedIndex = i; break; }
                LoadMicColumn();
            };

            mcVol = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
                Left = 68, Top = 100, Height = 120, BackColor = Card, SmallChange = 2, LargeChange = 10
            };
            card.Controls.Add(mcVol);
            mcMeter = new LevelMeter(MicColor) { Left = 122, Top = 106, Width = 10, Height = 108 };
            card.Controls.Add(mcMeter);
            mcPct = Lbl(card, "", 0, 222, 12f, true, Fg);
            mcPct.AutoSize = false; mcPct.Width = 180; mcPct.TextAlign = ContentAlignment.MiddleCenter; mcPct.Height = 24;
            mcVol.ValueChanged += (s, e) =>
            {
                mcPct.Text = mcVol.Value + "%";
                if (loading || goxApplying) return;
                if (GoxOnMicLevel(mcVol.Value)) return; // GoXLR mic: this slider is its mic gain
                var mic = micDevice.SelectedItem as DeviceItem;
                if (mic == null) return;
                Audio.SetVolume(mic.Id, mcVol.Value / 100f);
                LoadMicLevel();
            };

            mcMute = Btn(card, "Mute", 12, 250, 156, 28);
            mcMute.Click += (s, e) =>
            {
                if (GoxOnMicMute()) { ShowMicMuteLook(gox != null && gox.CoughState != "Unmuted"); return; } // GoXLR mute button
                var mic = micDevice.SelectedItem as DeviceItem;
                if (mic == null) return;
                Audio.SetMute(mic.Id, !Audio.GetMute(mic.Id));
                LoadMicColumn();
            };

            Lbl(card, "Background noise", 14, 292, 8.5f, false, Dim);
            mcNoise = Lbl(card, "", 12, 312, 14f, true, Dim);
            mcNoiseDb = Lbl(card, "", 14, 342, 9f, false, Dim);
            mcHint = Lbl(card, "", 14, 372, 8.5f, false, Dim);
            mcHint.MaximumSize = new Size(156, 0);
            tips.SetToolTip(mcMeter, "Your mic level. The white line is your background noise; your voice should be well above it.");

            mcNs = Chk(card, "Remove noise", 12, 436);
            mcNs.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            mcNs.Checked = Settings.GetBool("ns.on", true);
            mcNs.CheckedChanged += (s, e) => { Settings.Set("ns.on", mcNs.Checked); ApplyNoiseRemoval(); };
            Lbl(card, "Strength", 14, 466, 8.5f, false, Dim);
            mcNsStrength = new Slider { Left = 6, Top = 478, Width = 168, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, BackColor = Card, Value = Clamp(Settings.GetInt("ns.strength", 60), 0, 100) };
            card.Controls.Add(mcNsStrength);
            mcNsStrength.ValueChanged += (s, e) => { Settings.Set("ns.strength", mcNsStrength.Value); ApplyNoiseRemoval(); };
            tips.SetToolTip(mcNsStrength, "Higher cuts more background sound between and around your words. Too high can clip the start of quiet words.");
            mcNsStatus = Lbl(card, "", 14, 524, 8f, false, Dim);
            mcNsStatus.MaximumSize = new Size(156, 0);

            var settings = Btn(card, "Mic settings", 12, 556, 156, 28);
            settings.Click += (s, e) => ShowPage("Microphone");
            ApplyNoiseRemoval();

            // Drop an app here to put it in your chat mic (plays when music-in-mic is on).
            tips.SetToolTip(card, "Drag an app here to play it in your mic when music-in-mic is on. Right-click an app to remove it.");
            AddAppDrop(card, exe => SetMic(exe, true), card);
        }

        CheckBox mcNs;
        TrackBar mcNsStrength;
        Label mcNsStatus;

        void ApplyNoiseRemoval()
        {
            NoiseRemoval.On = mcNs.Checked && RnNoise.Available;
            NoiseRemoval.Strength = mcNsStrength.Value / 100f * 0.95f;
            mcNsStrength.Enabled = mcNs.Checked;
            UpdateNoiseStatus();
        }

        void UpdateNoiseStatus()
        {
            if (mcNsStatus == null) return;
            string text; Color color = Dim;
            if (!RnNoise.Available) { text = "rnnoise_mono.dll is missing from SoundDeck's folder."; color = Accent; }
            else if (!mcNs.Checked) text = "Off. Friends hear your mic as it is.";
            else if (!mixEngine.Running) { text = "Turn on Mic Mix so friends get the clean mic."; color = Warn; }
            else { text = "On. Friends hear the clean mic."; color = Good; }
            mcNsStatus.Text = text;
            mcNsStatus.ForeColor = color;
        }

        void FillMicColumn()
        {
            if (mcDev == null) return;
            bool prev = loading;
            loading = true;
            var mic = micDevice.SelectedItem as DeviceItem;
            mcDev.Items.Clear();
            int sel = -1;
            foreach (DeviceItem it in micDevice.Items)
            {
                if (IsVirtualMic(it.Name) && (mic == null || it.Id != mic.Id)) continue; // keep the list to real mics
                mcDev.Items.Add(new DeviceItem(it.Id, it.Name));
                if (mic != null && it.Id == mic.Id) sel = mcDev.Items.Count - 1;
            }
            mcDev.SelectedIndex = sel;
            loading = prev;
            LoadMicColumn();
        }

        // Volume and mute can change outside SoundDeck, so this re-reads them every second.
        void LoadMicColumn()
        {
            if (mcDev == null) return;
            UpdateNoiseStatus();
            var mic = micDevice.SelectedItem as DeviceItem;
            if (mic == null) return;
            bool prev = loading;
            loading = true;
            for (int i = 0; i < mcDev.Items.Count; i++)
                if (((DeviceItem)mcDev.Items[i]).Id == mic.Id && mcDev.SelectedIndex != i) mcDev.SelectedIndex = i;
            if (!(GoxLinkOn && MicIsGoxlr())) // with a linked GoXLR, its mic gain and mute button drive these instead
            {
                float v = Audio.GetVolume(mic.Id);
                if (v >= 0) mcVol.Value = Clamp((int)Math.Round(v * 100), 0, 100);
                mcPct.Text = mcVol.Value + "%";
                ShowMicMuteLook(Audio.GetMute(mic.Id));
            }
            loading = prev;
        }

        static float Db(float rms) { return rms <= 0.000001f ? -120f : (float)(20 * Math.Log10(rms)); }

        // Runs the mic listener only while the Mixer is on screen, and updates the meter and noise readout.
        void TickMicColumn(bool onScreen)
        {
            if (mcDev == null) return;
            var mic = micDevice.SelectedItem as DeviceItem;
            bool want = onScreen && pages["Mixer"].Visible && mic != null && !IsVirtualMic(mic.Name);
            if (!want) { if (micMeter.Running || micMeter.DeviceId != null) micMeter.Stop(); return; }
            if (micMeter.DeviceId != mic.Id || (!micMeter.Running && (DateTime.Now - mcStarted).TotalSeconds > 3))
            {
                mcStarted = DateTime.Now;
                micMeter.Start(mic.Id);
            }

            mcLevel = Math.Max(MeterPos(micMeter.TakePeak()), mcLevel - 0.04f);
            mcMeter.Value = mcLevel;

            float noise = micMeter.Noise;
            if (noise <= 0) { mcMeter.Marker = -1; mcNoise.Text = "Listening..."; mcNoise.ForeColor = Dim; mcNoiseDb.Text = ""; mcHint.Text = ""; return; }
            float db = Db(noise);
            mcMeter.Marker = MeterPos(noise);
            mcNoiseDb.Text = db <= -100 ? "" : Math.Round(db) + " dB";
            if (mcMute.Text == "Muted") { mcNoise.Text = "Muted"; mcNoise.ForeColor = Dim; mcHint.Text = ""; }
            else if (db < -60) { mcNoise.Text = "Quiet"; mcNoise.ForeColor = Good; mcHint.Text = ""; }
            else if (db < -45) { mcNoise.Text = "Some noise"; mcNoise.ForeColor = Warn; mcHint.Text = mcNs.Checked ? "Remove noise is cleaning this up." : "A little hum or fan noise. Turn on Remove noise below."; }
            else { mcNoise.Text = "Noisy"; mcNoise.ForeColor = Accent; mcHint.Text = mcNs.Checked ? "Remove noise is cleaning this up. Raise Strength if friends still hear it." : "Friends can hear this. Turn on Remove noise below."; }
        }

        // ---------- Auto-ducking ----------

        CheckBox duckOn;
        CheckBox[] duckTargets = new CheckBox[4];
        TrackBar duckAmount, duckSens;
        Label duckAmountLbl, duckSensLbl, duckState;
        LevelMeter duckChatMeter;
        DateTime lastTalk = DateTime.MinValue;

        void BuildDucking()
        {
            var p = NewPage("Ducking", "Auto-ducking", "When someone talks in your Chat channel (Harmony, etc.), SoundDeck turns other audio down so you can hear them, then brings it back.");

            duckOn = Chk(p, "Turn on auto-ducking", 25, 96);
            duckOn.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            duckOn.Checked = Settings.GetBool("duck.on", true);
            duckOn.CheckedChanged += (s, e) => Settings.Set("duck.on", duckOn.Checked);

            Lbl(p, "Turn down", 25, 146, 9.5f, false, Dim);
            int x = 150;
            for (int i = 0; i < 4; i++)
            {
                if (Channels[i] == "Chat") continue;
                string ch = Channels[i];
                var c = Chk(p, ch, x, 144);
                c.ForeColor = ChannelColors[i];
                c.Checked = Settings.GetBool("duck." + ch, ch == "Media");
                c.CheckedChanged += (s, e) => Settings.Set("duck." + ch, ((CheckBox)s).Checked);
                duckTargets[i] = c;
                x += 100;
            }

            Lbl(p, "How much", 25, 192, 9.5f, false, Dim);
            duckAmount = new Slider { Left = 150, Top = 186, Width = 300, Minimum = 10, Maximum = 100, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt("duck.amount", 60), 10, 100) };
            p.Controls.Add(duckAmount);
            duckAmountLbl = Lbl(p, "", 456, 190, 10f, true, Fg);
            duckAmount.ValueChanged += (s, e) => { Settings.Set("duck.amount", duckAmount.Value); UpdateDuckLabels(); };

            Lbl(p, "Sensitivity", 25, 236, 9.5f, false, Dim);
            duckSens = new Slider { Left = 150, Top = 230, Width = 300, Minimum = 1, Maximum = 10, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt("duck.sens", 6), 1, 10) };
            p.Controls.Add(duckSens);
            duckSensLbl = Lbl(p, "", 456, 234, 10f, true, Fg);
            duckSens.ValueChanged += (s, e) => { Settings.Set("duck.sens", duckSens.Value); UpdateDuckLabels(); };
            Lbl(p, "Raise it if quiet friends don't trigger ducking. Lower it if small sounds set it off.", 155, 280, 8.5f, false, Dim);

            Lbl(p, "Chat level", 25, 322, 9.5f, false, Dim);
            duckChatMeter = new LevelMeter(ChannelColors[1]) { Left = 155, Top = 326, Width = 290, Height = 10, Horizontal = true, BackColor = Card2 };
            p.Controls.Add(duckChatMeter);
            duckState = Lbl(p, "", 456, 320, 10f, true, Dim);

            var note = Lbl(p, "Put Harmony (or Discord, etc.) in the Chat channel on the Mixer page. " +
                "Your Stream Deck dials keep showing your normal level while ducking.", 25, 372, 9.5f, false, Dim);
            note.MaximumSize = new Size(760, 0);
            UpdateDuckLabels();
        }

        void UpdateDuckLabels()
        {
            duckAmountLbl.Text = duckAmount.Value + "% quieter";
            duckSensLbl.Text = duckSens.Value.ToString();
        }

        // Peak level that counts as talking: sensitivity 1 = 0.15, 10 = 0.005.
        float DuckThreshold()
        {
            return (float)(0.15 * Math.Pow(0.005 / 0.15, (duckSens.Value - 1) / 9.0));
        }

        void TickDucking(float chatPeak)
        {
            bool on = duckOn.Checked;
            if (on && chatPeak > DuckThreshold()) lastTalk = DateTime.Now;
            bool talking = on && (DateTime.Now - lastTalk).TotalMilliseconds < 700;
            float low = 1f - duckAmount.Value / 100f;
            bool changed = false;
            for (int i = 0; i < 4; i++)
            {
                bool target = talking && duckTargets[i] != null && duckTargets[i].Checked;
                float want = target ? low : 1f;
                float d = duck[i];
                if (want < d) d = Math.Max(want, d - 0.12f);   // down in about 0.3 s
                else if (want > d) d = Math.Min(want, d + 0.025f); // back up in about 1.5 s
                if (d != duck[i]) { duck[i] = d; changed = true; }
            }
            if (changed) { ApplyVolumes(); if (mixEngine.Running) UpdateEngineSources(); }
            if (pages["Ducking"].Visible)
            {
                duckState.Text = !on ? "Off" : talking ? "Ducking" : "Listening";
                duckState.ForeColor = talking ? Good : Dim;
            }
        }

        void ApplyVolumes()
        {
            foreach (var s in lastSessions)
            {
                int ci = Array.IndexOf(Channels, ChannelFor(s.Exe));
                if (ci < 0) continue;
                float vol = PersonalVolume(ci);
                try
                {
                    float cur; s.Volume.GetMasterVolume(out cur);
                    if (Math.Abs(cur - vol) > 0.004f) s.Volume.SetMasterVolume(vol, ref Audio.Ctx);
                }
                catch { }
            }
        }

        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

        // ---------- Hear yourself ----------

        MonitorEngine sideEngine = new MonitorEngine(false);
        CheckBox sideOn;
        TrackBar sideVol;
        Label sideVolLbl, sideStatus, sideHotkeyStatus;
        ComboBox sideDev;
        TextBox sideHotkeyBox;
        const int SideHotkeyId = 2;
        int sideLastStart = -100;
        string sideMic;

        void BuildHearYourself()
        {
            var p = pages["Microphone"];
            Lbl(p, "Hear yourself", 22, 530, 12f, true, Fg);
            Lbl(p, "Plays your mic in your headset so you can hear how loud you are. Use headphones, or you'll get echo.", 25, 558, 9f, false, Dim);

            sideOn = Chk(p, "Hear my mic", 25, 588);
            sideOn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            sideOn.Checked = Settings.GetBool("side.on", false);
            sideOn.CheckedChanged += (s, e) => { Settings.Set("side.on", sideOn.Checked); TickHearYourself(); PublishHearYourself(); };
            sideStatus = Lbl(p, "", 160, 590, 9f, false, Dim);

            Lbl(p, "Level", 25, 628, 9.5f, false, Dim);
            sideVol = new Slider { Left = 150, Top = 622, Width = 300, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt("side.vol", 50), 0, 100) };
            p.Controls.Add(sideVol);
            sideVolLbl = Lbl(p, sideVol.Value + "%", 456, 626, 10f, true, Fg);
            sideVol.ValueChanged += (s, e) =>
            {
                sideVolLbl.Text = sideVol.Value + "%";
                Settings.Set("side.vol", sideVol.Value);
                sideEngine.Gain = sideVol.Value / 100f;
            };
            sideEngine.Gain = sideVol.Value / 100f;

            Lbl(p, "Hear it on", 25, 670, 9.5f, false, Dim);
            sideDev = Combo(p, 150, 666, 300);
            sideDev.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = sideDev.SelectedItem as DeviceItem;
                Settings.Set("side.dev", it == null || it.Id == null ? "" : it.Id);
                TickHearYourself();
            };

            Lbl(p, "Hotkey", 25, 712, 9.5f, false, Dim);
            sideHotkeyBox = HotkeyBox(p, 150, 708, "side", 0, 0, SideHotkeyId, RegisterSideHotkey);
            sideHotkeyStatus = Lbl(p, "Click the box, then press your keys. Backspace clears it.", 380, 712, 8.5f, false, Dim);
            FillOutCombo(sideDev, "side.dev");
            RegisterSideHotkey();
        }

        // Click-to-record hotkey box shared by the newer features.
        TextBox HotkeyBox(Panel p, int x, int y, string prefix, int defMods, int defVk, int id, Action register)
        {
            var box = new TextBox { Left = x, Top = y, Width = 220, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true, Cursor = Cursors.Hand };
            box.Text = HotkeyText(Settings.GetInt(prefix + ".mods", defMods), Settings.GetInt(prefix + ".vk", defVk));
            box.Enter += (s, e) => { if (IsHandleCreated) UnregisterHotKey(Handle, id); box.BackColor = Color.FromArgb(70, 50, 40); };
            box.Leave += (s, e) => { box.BackColor = Card2; register(); };
            box.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true; e.Handled = true;
                Keys k = e.KeyCode;
                if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return;
                if (k == Keys.Tab || k == Keys.Escape) { p.Focus(); return; }
                int mods = 0, vk = (int)k;
                if (k == Keys.Back || k == Keys.Delete) vk = 0;
                else mods = (e.Control ? 2 : 0) | (e.Alt ? 1 : 0) | (e.Shift ? 4 : 0);
                Settings.Set(prefix + ".mods", mods);
                Settings.Set(prefix + ".vk", vk);
                box.Text = HotkeyText(mods, vk);
                p.Focus();
            };
            p.Controls.Add(box);
            return box;
        }

        void RegisterSideHotkey()
        {
            if (!IsHandleCreated || sideHotkeyStatus == null) return;
            UnregisterHotKey(Handle, SideHotkeyId);
            int vk = Settings.GetInt("side.vk", 0);
            int mods = Settings.GetInt("side.mods", 0);
            bool ok = vk != 0 && RegisterHotKey(Handle, SideHotkeyId, (uint)(mods | 0x4000), (uint)vk);
            bool clash = vk != 0 && !ok;
            sideHotkeyStatus.Text = clash ? "Another program already uses that hotkey. Pick a different one." : "Click the box, then press your keys. Backspace clears it.";
            sideHotkeyStatus.ForeColor = clash ? Accent : Dim;
        }

        void ToggleHearYourself()
        {
            sideOn.Checked = !sideOn.Checked;
            Beep(sideOn.Checked);
        }

        void PublishHearYourself()
        {
            if (control != null) control.Publish("side", "side " + (sideOn.Checked ? 1 : 0));
        }

        // Output for a "Hear it on" setting: the picked device, else the Chat channel's, else the Windows default.
        string OutputFor(string key)
        {
            string id = Settings.Get(key, "");
            if (id == "" || !renderDevs.Any(d => d.Id == id)) id = Settings.Get("ch.Chat.dev", "");
            if (id == "" || !renderDevs.Any(d => d.Id == id)) id = Audio.GetDefaultId(EDataFlow.Render);
            // Never "hear" something through a virtual mic: that would send it to friends instead of you.
            var dev = renderDevs.FirstOrDefault(d => d.Id == id);
            if (dev == null || IsVirtualCableOut(dev.Name)) { var real = BestRealOutput(); id = real == null ? null : real.Id; }
            return id;
        }

        // ---------- Beeps and safe default output ----------

        float[] beepUp, beepDown;

        // Two-note beep (48 kHz stereo) played straight to your headset, never into the mic.
        static float[] ToneFloat(int f1, int f2)
        {
            int each = MixFormat.Rate * 7 / 100;
            var d = new float[each * 2 * 2];
            for (int i = 0; i < each * 2; i++)
            {
                int f = i < each ? f1 : f2, j = i % each;
                float env = Math.Min(1f, Math.Min(j, each - j) / 300f);
                float v = (float)(Math.Sin(2 * Math.PI * f * i / MixFormat.Rate) * 0.18 * env);
                d[i * 2] = v; d[i * 2 + 1] = v;
            }
            return d;
        }

        void Beep(bool up)
        {
            if (!mixBeep.Checked) return;
            var data = up ? (beepUp ?? (beepUp = ToneFloat(660, 990))) : (beepDown ?? (beepDown = ToneFloat(990, 660)));
            string outId = OutputFor("sb.dev");
            if (outId == null) return;
            if (!boardLocal.Running || boardLocal.OutputId != outId) boardLocal.Start(null, outId);
            boardLocal.Bank.Play(data, 1f);
        }

        // Your real speakers/headset: the last real Windows default, else headphones, headset, speakers, anything.
        AudioDevice BestRealOutput()
        {
            var real = renderDevs.Where(d => !IsVirtualCableOut(d.Name) && !IsVirtualOutput(d.Name)).ToList();
            string last = Settings.Get("out.lastReal", "");
            return real.FirstOrDefault(d => d.Id == last)
                ?? real.FirstOrDefault(d => d.Name.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase))
                ?? real.FirstOrDefault(d => d.Name.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? real.FirstOrDefault(d => d.Name.StartsWith("Speakers", StringComparison.OrdinalIgnoreCase))
                ?? real.FirstOrDefault();
        }

        // Installing VB-CABLE (and some other virtual mics) makes it the Windows default output, which sends every
        // app on "default" (Windows sounds, Discord, browsers...) into your mic. Put the default back on real speakers.
        void GuardDefaultOutput()
        {
            string def = Audio.GetDefaultId(EDataFlow.Render);
            var cur = renderDevs.FirstOrDefault(d => d.Id == def);
            if (cur == null) return;
            if (!IsVirtualCableOut(cur.Name))
            {
                if (!IsVirtualOutput(cur.Name)) Settings.Set("out.lastReal", cur.Id);
                return;
            }
            var real = BestRealOutput();
            if (real == null) return;
            Audio.SetDefault(real.Id);
            routed.Clear();
            tray.ShowBalloonTip(8000, "Fixed: sound was going into your mic",
                "Windows was playing everything into \"" + cur.Name + "\", so friends could hear it. SoundDeck switched your sound back to " + real.Name + ".", ToolTipIcon.Warning);
        }

        void FillOutCombo(ComboBox cb, string key)
        {
            string cur = Settings.Get(key, "");
            bool prev = loading;
            loading = true;
            cb.Items.Clear();
            cb.Items.Add(new DeviceItem(null, "Same as the Chat channel"));
            int sel = 0;
            foreach (var d in ShownOutputs(cur))
            {
                cb.Items.Add(new DeviceItem(d.Id, d.Name));
                if (d.Id == cur) sel = cb.Items.Count - 1;
            }
            cb.SelectedIndex = sel;
            loading = prev;
        }

        void TickHearYourself()
        {
            if (sideOn == null) return;
            var mic = micDevice.SelectedItem as DeviceItem;
            bool badMic = mic == null || IsVirtualMic(mic.Name);
            string outId = OutputFor("side.dev");
            if (sideOn.Checked && !badMic && outId != null)
            {
                bool changed = sideEngine.OutputId != outId || sideMic != mic.Id;
                if (changed || (!sideEngine.Running && tickCount - sideLastStart >= 5))
                {
                    sideLastStart = tickCount;
                    sideMic = mic.Id;
                    sideEngine.Start(mic.Id, outId);
                }
            }
            else if (sideEngine.Running) sideEngine.Stop();

            string st = !sideOn.Checked ? "" : badMic ? "Pick your real mic at the top of this page." : sideEngine.Status;
            sideStatus.Text = st == "Running" ? (Settings.GetInt("side.vk", 0) == 0 ? "On" : "On. Press " + sideHotkeyBox.Text + " to turn it off.") : st;
            sideStatus.ForeColor = st == "Running" ? Good : st.StartsWith("Stopped") || badMic ? Accent : Dim;
        }

        // ---------- Soundboard ----------

        const int SoundSlots = 12;
        MonitorEngine boardLocal = new MonitorEngine(true);
        Button[] soundTiles = new Button[SoundSlots];
        Dictionary<string, float[]> soundCache = new Dictionary<string, float[]>();
        CheckBox sbFriends, sbMe;
        TrackBar sbVol;
        Label sbVolLbl, sbStatus;
        ComboBox sbDev;

        void BuildSoundboard()
        {
            var p = NewPage("Soundboard", "Soundboard", "Click a sound to play it for you and your friends. Right-click to change it. Sounds work from your Stream Deck too.");

            for (int i = 0; i < SoundSlots; i++)
            {
                int slot = i;
                var b = Btn(p, "", 25 + (i % 4) * 196, 96 + (i / 4) * 86, 186, 76);
                b.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
                b.TextAlign = ContentAlignment.MiddleCenter;
                b.MouseUp += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left) { if (SoundPath(slot) == "") PickSound(slot); else PlaySound(slot + 1); }
                    else if (e.Button == MouseButtons.Right) SoundMenu(slot).Show(soundTiles[slot], e.Location);
                };
                soundTiles[i] = b;
                UpdateTile(i);
            }

            int y = 96 + 3 * 86 + 16;
            var stop = Btn(p, "Stop all sounds", 25, y, 186, 34);
            stop.Click += (s, e) => StopSounds();
            sbStatus = Lbl(p, "", 230, y + 8, 9.5f, false, Dim);

            y += 56;
            sbFriends = Chk(p, "Play to my friends (needs Mic Mix)", 25, y);
            sbFriends.Checked = Settings.GetBool("sb.friends", true);
            sbFriends.CheckedChanged += (s, e) => Settings.Set("sb.friends", sbFriends.Checked);
            sbMe = Chk(p, "Play to me too", 25, y + 30);
            sbMe.Checked = Settings.GetBool("sb.me", true);
            sbMe.CheckedChanged += (s, e) => Settings.Set("sb.me", sbMe.Checked);

            Lbl(p, "Hear it on", 230, y + 31, 9.5f, false, Dim);
            sbDev = Combo(p, 310, y + 27, 300);
            sbDev.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = sbDev.SelectedItem as DeviceItem;
                Settings.Set("sb.dev", it == null || it.Id == null ? "" : it.Id);
            };

            Lbl(p, "Volume", 25, y + 76, 9.5f, false, Dim);
            sbVol = new Slider { Left = 150, Top = y + 70, Width = 300, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt("sb.vol", 70), 0, 100) };
            p.Controls.Add(sbVol);
            sbVolLbl = Lbl(p, sbVol.Value + "%", 456, y + 74, 10f, true, Fg);
            sbVol.ValueChanged += (s, e) => { sbVolLbl.Text = sbVol.Value + "%"; Settings.Set("sb.vol", sbVol.Value); };
            FillOutCombo(sbDev, "sb.dev");
        }

        string SoundPath(int slot) { return Settings.Get("sb." + slot + ".path", ""); }

        string SoundName(int slot)
        {
            string n = Settings.Get("sb." + slot + ".name", "");
            if (n != "") return n;
            string path = SoundPath(slot);
            return path == "" ? "" : Path.GetFileNameWithoutExtension(path);
        }

        void UpdateTile(int slot)
        {
            var b = soundTiles[slot];
            bool empty = SoundPath(slot) == "";
            b.Text = empty ? "+  Add sound" : SoundName(slot);
            b.ForeColor = empty ? Dim : Fg;
            b.BackColor = empty ? Card : Card2;
        }

        ContextMenuStrip SoundMenu(int slot)
        {
            var m = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false };
            m.Items.Add("Choose sound file...", null, (s, e) => PickSound(slot));
            if (SoundPath(slot) != "")
            {
                m.Items.Add("Rename...", null, (s, e) =>
                {
                    string n = Prompt("Rename sound", "Name:", SoundName(slot));
                    if (n == null) return;
                    Settings.Set("sb." + slot + ".name", n.Replace("\t", " ").Trim());
                    UpdateTile(slot); PublishSounds();
                });
                m.Items.Add("Remove", null, (s, e) =>
                {
                    Settings.Remove("sb." + slot + ".path"); Settings.Remove("sb." + slot + ".name");
                    UpdateTile(slot); PublishSounds();
                });
            }
            return m;
        }

        void PickSound(int slot)
        {
            using (var dlg = new OpenFileDialog { Filter = "Sounds (*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac)|*.mp3;*.wav;*.m4a;*.wma;*.aac;*.flac|All files|*.*", Title = "Pick a sound" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Settings.Set("sb." + slot + ".path", dlg.FileName);
                Settings.Remove("sb." + slot + ".name");
                UpdateTile(slot);
                PublishSounds();
            }
        }

        // slot is 1-based (matches the Stream Deck).
        void PlaySound(int slotNumber)
        {
            int slot = slotNumber - 1;
            if (slot < 0 || slot >= SoundSlots) return;
            string path = SoundPath(slot);
            if (path == "") return;
            float[] data;
            lock (soundCache) { if (!soundCache.TryGetValue(path, out data)) data = null; }
            if (data != null) { StartSound(data, slot); return; }
            if (!File.Exists(path)) { sbStatus.Text = "Can't find " + Path.GetFileName(path) + ". Right-click the sound to pick it again."; sbStatus.ForeColor = Accent; return; }

            sbStatus.Text = "Loading " + SoundName(slot) + "..."; sbStatus.ForeColor = Dim;
            var t = new Thread(() =>
            {
                try
                {
                    var d = AudioFile.Load(path);
                    lock (soundCache) soundCache[path] = d;
                    BeginInvoke(new Action(() => StartSound(d, slot)));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => { sbStatus.Text = "Couldn't play that file: " + ex.Message; sbStatus.ForeColor = Accent; }));
                }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void StartSound(float[] data, int slot)
        {
            float gain = sbVol.Value / 100f;
            bool toFriends = sbFriends.Checked && mixEngine.Running;
            if (toFriends) mixEngine.Board.Play(data, gain);
            if (sbMe.Checked)
            {
                string outId = OutputFor("sb.dev");
                if (outId != null)
                {
                    if (!boardLocal.Running || boardLocal.OutputId != outId) boardLocal.Start(null, outId);
                    boardLocal.Bank.Play(data, gain);
                }
            }
            sbStatus.Text = "Playing " + SoundName(slot) + (sbFriends.Checked && !toFriends ? ". Friends can't hear it: turn on Mic Mix." : "");
            sbStatus.ForeColor = sbFriends.Checked && !toFriends ? Accent : Good;
        }

        void StopSounds()
        {
            mixEngine.Board.StopAll();
            boardLocal.Bank.StopAll();
            sbStatus.Text = "Stopped."; sbStatus.ForeColor = Dim;
        }

        void PublishSounds()
        {
            if (control == null) return;
            var names = Enumerable.Range(0, SoundSlots).Select(i => SoundName(i));
            control.Publish("sounds", "sounds " + string.Join("\t", names));
        }

        // ---------- Profiles ----------

        ListBox profList;
        Label profActive, profAutoNote;
        ComboBox profAuto;
        string autoProfile, beforeAuto, autoSuppressed;
        const string BeforeAuto = "~before";

        IEnumerable<string> ProfileKeys()
        {
            foreach (var ch in Channels)
            {
                yield return "ch." + ch + ".vol"; yield return "ch." + ch + ".mute"; yield return "ch." + ch + ".dev";
                yield return "ch." + ch + ".svol"; yield return "ch." + ch + ".smute";
                yield return "duck." + ch;
            }
            foreach (var k in new[] { "duck.on", "duck.amount", "duck.sens", "side.on", "side.vol", "mix.mediaVol", "mix.micVol", "mix.source", "sb.vol", "ns.on", "ns.strength", "stream.on", "stream.voice", "stream.board", "gate.on", "gate.thr", "gate.rel", "comp.on", "comp.thr", "comp.ratio", "comp.makeup" })
                yield return k;
        }

        List<string> ProfileNames()
        {
            return Settings.Get("profiles", "").Split('|').Where(n => n != "").ToList();
        }

        void SaveProfileNames(List<string> names) { Settings.Set("profiles", string.Join("|", names)); PublishProfiles(); }

        void BuildProfiles()
        {
            var p = NewPage("Profiles", "Profiles", "Save your volumes, outputs, ducking and Mic Mix levels as a profile. Switch with one click, from your Stream Deck, or automatically when a game starts.");

            profList = new ListBox { Left = 25, Top = 96, Width = 300, Height = 300, BackColor = Card, ForeColor = Fg, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11f), IntegralHeight = false };
            p.Controls.Add(profList);
            profList.SelectedIndexChanged += (s, e) => LoadAutoField();
            profList.DoubleClick += (s, e) => { var n = SelectedProfile(); if (n != null) ApplyProfile(n, false); };

            int x = 345, y = 96;
            var use = Btn(p, "Switch to this profile", x, y, 260, 34); y += 44;
            use.Click += (s, e) => { var n = SelectedProfile(); if (n != null) ApplyProfile(n, false); };
            var add = Btn(p, "New profile from current settings", x, y, 260, 34); y += 44;
            add.Click += (s, e) => NewProfile();
            var upd = Btn(p, "Save current settings into this profile", x, y, 260, 34); y += 44;
            upd.Click += (s, e) =>
            {
                var n = SelectedProfile(); if (n == null) return;
                SnapshotProfile(n);
                profActive.Text = "Saved current settings into \"" + n + "\".";
            };
            var ren = Btn(p, "Rename...", x, y, 125, 34);
            ren.Click += (s, e) => RenameProfile();
            var del = Btn(p, "Delete", x + 135, y, 125, 34);
            del.Click += (s, e) => DeleteProfile();

            profActive = Lbl(p, "", 25, 408, 10f, true, Good);

            Lbl(p, "Switch automatically", 22, 450, 12f, true, Fg);
            profAutoNote = Lbl(p, "Pick a profile above, then choose the game that turns it on. When the game closes, your old settings come back.", 25, 480, 9.5f, false, Dim);
            profAutoNote.MaximumSize = new Size(760, 0);
            Lbl(p, "When this is running", 25, 518, 9.5f, false, Dim);
            profAuto = new ComboBox { Left = 175, Top = 514, Width = 300, BackColor = Card2, ForeColor = Fg, FlatStyle = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDown };
            p.Controls.Add(profAuto);
            profAuto.DropDown += (s, e) =>
            {
                string cur = profAuto.Text;
                profAuto.Items.Clear();
                profAuto.Items.Add("(nothing)");
                foreach (var exe in lastSessions.Select(a => a.Exe).Distinct().OrderBy(a => a)) profAuto.Items.Add(exe);
                profAuto.Text = cur;
            };
            profAuto.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(SaveAutoField));
            profAuto.Leave += (s, e) => SaveAutoField();
            Lbl(p, "Pick an app that's playing sound, or type its .exe name (like cs2).", 175, 544, 8.5f, false, Dim);

            FillProfileList();
        }

        string SelectedProfile() { return profList.SelectedItem as string; }

        void FillProfileList()
        {
            string sel = SelectedProfile() ?? Settings.Get("profile.active", null);
            profList.Items.Clear();
            foreach (var n in ProfileNames()) profList.Items.Add(n);
            if (sel != null && profList.Items.Contains(sel)) profList.SelectedItem = sel;
            else if (profList.Items.Count > 0) profList.SelectedIndex = 0;
            string active = Settings.Get("profile.active", "");
            profActive.Text = profList.Items.Count == 0 ? "No profiles yet. Set things up how you like, then click \"New profile from current settings\"."
                : active != "" && ProfileNames().Contains(active) ? "Active: " + active + (autoProfile != null ? " (switched automatically)" : "") : "";
            profActive.ForeColor = profList.Items.Count == 0 ? Dim : Good;
            LoadAutoField();
        }

        void LoadAutoField()
        {
            var n = SelectedProfile();
            profAuto.Enabled = n != null;
            string v = n == null ? "" : Settings.Get("pf|" + n + "|@auto", "");
            profAuto.Text = v == "" ? "(nothing)" : v;
        }

        void SaveAutoField()
        {
            var n = SelectedProfile();
            if (n == null) return;
            string v = profAuto.Text.Trim().ToLowerInvariant();
            if (v.EndsWith(".exe")) v = v.Substring(0, v.Length - 4);
            if (v == "(nothing)") v = "";
            if (v == "") Settings.Remove("pf|" + n + "|@auto"); else Settings.Set("pf|" + n + "|@auto", v);
        }

        static string CleanName(string n)
        {
            return n == null ? null : new string(n.Where(c => c != '|' && c != '=' && c != '\t' && c != '\r' && c != '\n').ToArray()).Trim();
        }

        void SnapshotProfile(string name)
        {
            foreach (var k in ProfileKeys())
            {
                string v = Settings.Get(k, null);
                if (v == null) Settings.Remove("pf|" + name + "|" + k); else Settings.Set("pf|" + name + "|" + k, v);
            }
        }

        void NewProfile()
        {
            string n = CleanName(Prompt("New profile", "Name (like Gaming or Music):", ""));
            if (string.IsNullOrEmpty(n) || n.StartsWith("~")) return;
            var names = ProfileNames();
            if (!names.Contains(n)) names.Add(n);
            SnapshotProfile(n);
            SaveProfileNames(names);
            Settings.Set("profile.active", n);
            profList.SelectedItem = null;
            FillProfileList();
            profList.SelectedItem = n;
        }

        void RenameProfile()
        {
            var old = SelectedProfile(); if (old == null) return;
            string n = CleanName(Prompt("Rename profile", "New name:", old));
            if (string.IsNullOrEmpty(n) || n == old || n.StartsWith("~")) return;
            var names = ProfileNames();
            if (names.Contains(n)) { MessageBox.Show(this, "There's already a profile called " + n + ".", "SoundDeck"); return; }
            foreach (var k in Settings.KeysStartingWith("pf|" + old + "|"))
            {
                Settings.Set("pf|" + n + "|" + k.Substring(("pf|" + old + "|").Length), Settings.Get(k, ""));
                Settings.Remove(k);
            }
            names[names.IndexOf(old)] = n;
            if (Settings.Get("profile.active", "") == old) Settings.Set("profile.active", n);
            if (autoProfile == old) autoProfile = n;
            SaveProfileNames(names);
            FillProfileList();
            profList.SelectedItem = n;
        }

        void DeleteProfile()
        {
            var n = SelectedProfile(); if (n == null) return;
            if (MessageBox.Show(this, "Delete the profile \"" + n + "\"?", "SoundDeck", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            foreach (var k in Settings.KeysStartingWith("pf|" + n + "|")) Settings.Remove(k);
            var names = ProfileNames(); names.Remove(n);
            if (Settings.Get("profile.active", "") == n) Settings.Remove("profile.active");
            SaveProfileNames(names);
            profList.SelectedItem = null;
            FillProfileList();
        }

        void ApplyProfile(string name, bool auto)
        {
            if (name != BeforeAuto && !ProfileNames().Contains(name)) return;
            foreach (var k in ProfileKeys())
            {
                string v = Settings.Get("pf|" + name + "|" + k, null);
                if (v != null) Settings.Set(k, v);
            }
            ReloadFromSettings();
            if (name != BeforeAuto) Settings.Set("profile.active", name);
            // A manual pick while a game's profile is on wins until that game closes.
            if (!auto && autoProfile != null) { autoSuppressed = autoProfile; autoProfile = null; beforeAuto = null; }
            PublishProfiles();
            FillProfileList();
            if (auto && name != BeforeAuto) tray.ShowBalloonTip(2000, "Profile: " + name, "Switched automatically.", ToolTipIcon.None);
        }

        // Pushes settings back into the controls after a profile is applied.
        void ReloadFromSettings()
        {
            for (int i = 0; i < 4; i++)
            {
                chVol[i].Value = Clamp(Settings.GetInt("ch." + Channels[i] + ".vol", 100), 0, 100);
                UpdateMuteButton(i);
                if (duckTargets[i] != null) duckTargets[i].Checked = Settings.GetBool("duck." + Channels[i], Channels[i] == "Media");
            }
            routed.Clear();
            RefreshDevices(true);
            duckOn.Checked = Settings.GetBool("duck.on", true);
            duckAmount.Value = Clamp(Settings.GetInt("duck.amount", 60), 10, 100);
            duckSens.Value = Clamp(Settings.GetInt("duck.sens", 6), 1, 10);
            sideOn.Checked = Settings.GetBool("side.on", false);
            sideVol.Value = Clamp(Settings.GetInt("side.vol", 50), 0, 100);
            mixMediaVol.Value = Clamp(Settings.GetInt("mix.mediaVol", 35), 0, 100);
            mixMicVol.Value = Clamp(Settings.GetInt("mix.micVol", 100), 0, 200);
            int si = mixSource.Items.IndexOf(Settings.Get("mix.source", "Media"));
            if (si >= 0) mixSource.SelectedIndex = si;
            sbVol.Value = Clamp(Settings.GetInt("sb.vol", 70), 0, 100);
            mcNs.Checked = Settings.GetBool("ns.on", true);
            mcNsStrength.Value = Clamp(Settings.GetInt("ns.strength", 60), 0, 100);
            stOn.Checked = Settings.GetBool("stream.on", false);
            stVoice.Value = Clamp(Settings.GetInt("stream.voice", 100), 0, 200);
            stBoard.Checked = Settings.GetBool("stream.board", true);
            ReloadDynamicsFromSettings();
            for (int i = 0; i < 4; i++)
            {
                chSVol[i].Value = Clamp(Settings.GetInt("ch." + Channels[i] + ".svol", 100), 0, 100);
                UpdateStreamMute(i);
            }
            appsKey = null;
            ApplyMixer();
        }

        void TickProfiles()
        {
            if (tickCount % 2 != 0) return;
            var names = ProfileNames();
            var autos = names.Select(n => new { n, exe = Settings.Get("pf|" + n + "|@auto", "") }).Where(a => a.exe != "").ToList();
            if (autos.Count == 0 && autoProfile == null) return;

            HashSet<string> running;
            try { running = new HashSet<string>(Process.GetProcesses().Select(pr => pr.ProcessName.ToLowerInvariant())); }
            catch { return; }
            var match = autos.FirstOrDefault(a => running.Contains(a.exe));
            if (match == null) autoSuppressed = null;
            else if (match.n == autoSuppressed) return;

            if (match != null && match.n != autoProfile)
            {
                if (autoProfile == null)
                {
                    SnapshotProfile(BeforeAuto);
                    beforeAuto = Settings.Get("profile.active", "");
                }
                string keepBefore = beforeAuto;
                ApplyProfile(match.n, true);
                autoProfile = match.n; beforeAuto = keepBefore;
                FillProfileList();
            }
            else if (match == null && autoProfile != null)
            {
                string back = beforeAuto;
                autoProfile = null; beforeAuto = null;
                ApplyProfile(BeforeAuto, true);
                if (!string.IsNullOrEmpty(back)) Settings.Set("profile.active", back); else Settings.Remove("profile.active");
                PublishProfiles();
                FillProfileList();
                tray.ShowBalloonTip(2000, "Profile restored", "The game closed, so your previous settings are back.", ToolTipIcon.None);
            }
        }

        void PublishProfiles()
        {
            if (control == null) return;
            string active = Settings.Get("profile.active", "");
            control.Publish("profiles", "profiles " + (active == "" ? "-" : active) + "\t" + string.Join("\t", ProfileNames()));
        }

        // ---------- Small dark text prompt ----------

        string Prompt(string title, string label, string value)
        {
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(380, 130), BackColor = Bg, ForeColor = Fg, Font = Font
            })
            {
                f.HandleCreated += (s, e) => { try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } };
                Lbl(f, label, 16, 14, 9.5f, false, Dim);
                var tb = new TextBox { Left = 18, Top = 40, Width = 344, Text = value, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
                f.Controls.Add(tb);
                var ok = Btn(f, "OK", 196, 84, 80, 30); ok.DialogResult = DialogResult.OK;
                var cancel = Btn(f, "Cancel", 282, 84, 80, 30); cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = ok; f.CancelButton = cancel;
                Look.Apply(f);
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }
    }
}
