// The Effects page (voice effects + bleep) and "Listen to your stream" on the Streaming page.

using System;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundDeck
{
    partial class MainForm
    {
        CheckBox fxOn;
        Button[] fxTiles;
        TrackBar fxAmount;
        Label fxAmountLbl, fxStatus, fxHotkeyStatus, bleepStatus;
        TextBox fxHotkeyBox, bleepKeyBox;
        const int FxHotkeyId = 4;
        Timer bleepTimer;
        bool bleepDeckHeld;

        static readonly Color FxColor = Color.FromArgb(181, 123, 255);

        void BuildEffects()
        {
            var p = NewPage("Effects", "Voice effects", "Change how your voice sounds to friends and your stream. Needs Mic Mix on.");

            fxOn = Chk(p, "Voice effect on", 25, 100);
            fxOn.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            fxOn.Checked = Settings.GetBool("fx.on", false);
            fxOn.CheckedChanged += (s, e) => { Settings.Set("fx.on", fxOn.Checked); ApplyFx(); };
            fxStatus = Lbl(p, "", 200, 103, 9f, false, Dim);

            var names = VoiceFxSettings.Names;
            fxTiles = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int fi = i;
                var b = Btn(p, names[i], 25 + i * 132, 140, 124, 48);
                b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
                b.Click += (s, e) => { Settings.Set("fx.effect", fi); if (!fxOn.Checked) fxOn.Checked = true; ApplyFx(); };
                fxTiles[i] = b;
            }

            Lbl(p, "Strength", 25, 214, 9.5f, false, Dim);
            fxAmount = new Slider { Left = 150, Top = 208, Width = 300, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt("fx.amount", 60), 0, 100) };
            p.Controls.Add(fxAmount);
            fxAmountLbl = Lbl(p, "", 456, 212, 10f, true, Fg);
            fxAmount.ValueChanged += (s, e) => { Settings.Set("fx.amount", fxAmount.Value); ApplyFx(); };

            Lbl(p, "Hotkey", 25, 256, 9.5f, false, Dim);
            // No hotkey until you pick one: Ctrl+Alt+V is "paste special" in Office and other apps.
            fxHotkeyBox = HotkeyBox(p, 150, 252, "fx", 0, 0, FxHotkeyId, RegisterFxHotkey);
            fxHotkeyStatus = Lbl(p, "", 380, 256, 8.5f, false, Dim);
            Lbl(p, "Turns the effect on and off. To hear it yourself, turn on Hear yourself on the Microphone page.", 25, 294, 8.5f, false, Dim);

            Lbl(p, "Bleep", 22, 346, 12f, true, Fg);
            Lbl(p, "Hold the key to replace your voice with a beep, for friends and your stream. Needs Mic Mix on.", 25, 374, 9f, false, Dim);
            Lbl(p, "Key", 25, 410, 9.5f, false, Dim);
            bleepKeyBox = new TextBox { Left = 150, Top = 406, Width = 220, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true, Cursor = Cursors.Hand };
            bleepKeyBox.Text = PttKeyText(Settings.GetInt("bleep.vk", 0));
            bleepKeyBox.Enter += (s, e) => bleepKeyBox.BackColor = Color.FromArgb(70, 50, 40);
            bleepKeyBox.Leave += (s, e) => bleepKeyBox.BackColor = Card2;
            bleepKeyBox.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true; e.Handled = true;
                Keys k = e.KeyCode;
                if (k == Keys.Tab || k == Keys.Escape) { p.Focus(); return; }
                SetBleepKey(k == Keys.Back || k == Keys.Delete ? 0 : (int)k);
                p.Focus();
            };
            bleepKeyBox.MouseDown += (s, e) =>
            {
                if (!bleepKeyBox.Focused) return;
                int vk = e.Button == MouseButtons.XButton1 ? 0x05 : e.Button == MouseButtons.XButton2 ? 0x06 : e.Button == MouseButtons.Middle ? 0x04 : 0;
                if (vk == 0) return;
                SetBleepKey(vk);
                p.Focus();
            };
            p.Controls.Add(bleepKeyBox);
            Lbl(p, "Click the box, then press a key or a mouse side button. Backspace clears it.", 380, 410, 8.5f, false, Dim);
            bleepStatus = Lbl(p, "", 25, 448, 10f, true, Dim);
            Lbl(p, "The Stream Deck has Voice effect and Bleep keys too.", 25, 476, 8.5f, false, Dim);

            bleepTimer = new Timer { Interval = 15 };
            bleepTimer.Tick += (s, e) => TickBleep();
            bleepTimer.Start();
            ApplyFx();
        }

        void ApplyFx()
        {
            if (fxOn == null) return;
            int fx = Clamp(Settings.GetInt("fx.effect", 0), 0, VoiceFxSettings.Names.Length - 1);
            VoiceFxSettings.Effect = fx;
            VoiceFxSettings.Amount = fxAmount.Value / 100f;
            VoiceFxSettings.On = fxOn.Checked;
            fxAmountLbl.Text = fxAmount.Value + "%";
            for (int i = 0; i < fxTiles.Length; i++)
            {
                bool sel = i == fx;
                fxTiles[i].BackColor = sel ? (fxOn.Checked ? FxColor : Color.FromArgb(78, 64, 104)) : Card2;
                fxTiles[i].ForeColor = sel ? Color.White : Fg;
            }
            if (control != null) control.Publish("voicefx", "voicefx " + (fxOn.Checked ? 1 : 0) + " " + VoiceFxSettings.Names[fx]);
            TickEffectsStatus();
        }

        void ToggleFx()
        {
            fxOn.Checked = !fxOn.Checked;
            Beep(fxOn.Checked);
        }

        void RegisterFxHotkey()
        {
            if (!IsHandleCreated || fxHotkeyStatus == null) return;
            UnregisterHotKey(Handle, FxHotkeyId);
            int vk = Settings.GetInt("fx.vk", 0);
            int mods = Settings.GetInt("fx.mods", 0);
            bool ok = vk != 0 && RegisterHotKey(Handle, FxHotkeyId, (uint)(mods | 0x4000), (uint)vk);
            bool clash = vk != 0 && !ok;
            fxHotkeyStatus.Text = clash ? "Another program already uses that hotkey. Pick a different one." : "Click the box, then press your keys. Backspace clears it.";
            fxHotkeyStatus.ForeColor = clash ? Accent : Dim;
        }

        void SetBleepKey(int vk)
        {
            Settings.Set("bleep.vk", vk);
            bleepKeyBox.Text = PttKeyText(vk);
        }

        // Every 15 ms: is the bleep key (or Stream Deck key) held?
        void TickBleep()
        {
            int vk = Settings.GetInt("bleep.vk", 0);
            bool held = bleepDeckHeld || (vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0);
            if (held != VoiceFxSettings.Bleep)
            {
                VoiceFxSettings.Bleep = held;
                if (control != null) control.Publish("bleep", "bleep " + (held ? 1 : 0));
                TickEffectsStatus();
            }
        }

        // Status lines on the Effects page (also run every second).
        void TickEffectsStatus()
        {
            if (fxStatus == null) return;
            bool mix = mixEngine.Running;
            string fxText; Color fxColor;
            if (!fxOn.Checked) { fxText = "Off"; fxColor = Dim; }
            else if (!mix) { fxText = "Turn on Mic Mix so friends hear it."; fxColor = Warn; }
            else { fxText = "On: " + VoiceFxSettings.Names[VoiceFxSettings.Effect]; fxColor = Good; }
            if (fxStatus.Text != fxText) { fxStatus.Text = fxText; fxStatus.ForeColor = fxColor; }

            int vk = Settings.GetInt("bleep.vk", 0);
            string bt; Color bc;
            if (VoiceFxSettings.Bleep) { bt = "Bleeping"; bc = Accent; }
            else if (!mix) { bt = "Turn on Mic Mix to use the bleep."; bc = Warn; }
            else if (vk == 0) { bt = "Pick a key above, or use the Stream Deck key."; bc = Dim; }
            else { bt = "Ready. Hold " + PttKeyText(vk) + " to bleep."; bc = Good; }
            if (bleepStatus.Text != bt) { bleepStatus.Text = bt; bleepStatus.ForeColor = bc; }
        }

        // ---------- Listen to your stream (Streaming page) ----------

        ComboBox monWhat, monDev;
        CheckBox monOn, monVoice;
        Label monStatus;

        void BuildStreamMonitor()
        {
            var p = pages["Streaming"];
            Lbl(p, "Listen to your stream", 22, 566, 12f, true, Fg);
            var note = Lbl(p, "Plays your stream's mix in your headset so you can check levels. You'll hear apps twice while it's on.", 25, 594, 9f, false, Dim);
            note.MaximumSize = new Size(760, 0);

            monOn = Chk(p, "Listen", 25, 626);
            monOn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            monOn.Checked = false; // never starts on by itself: hearing everything twice would be confusing
            monOn.CheckedChanged += (s, e) => { TickStreamMonitor(); PublishStreamMonitor(); };
            monStatus = Lbl(p, "", 150, 628, 9f, false, Dim);

            Lbl(p, "Listen to", 25, 666, 9.5f, false, Dim);
            monWhat = Combo(p, 150, 662, 300);
            monWhat.Items.Add("What your stream hears");
            monWhat.Items.Add("What Discord hears");
            monWhat.SelectedIndex = Clamp(Settings.GetInt("mon.what", 0), 0, 1);
            monWhat.SelectedIndexChanged += (s, e) => { Settings.Set("mon.what", monWhat.SelectedIndex); TickStreamMonitor(); };

            Lbl(p, "Hear it on", 25, 706, 9.5f, false, Dim);
            monDev = Combo(p, 150, 702, 300);
            monDev.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = monDev.SelectedItem as DeviceItem;
                Settings.Set("mon.dev", it == null || it.Id == null ? "" : it.Id);
                TickStreamMonitor();
            };
            FillOutCombo(monDev, "mon.dev");

            monVoice = Chk(p, "Include my voice (it comes back slightly late)", 150, 742);
            monVoice.Checked = Settings.GetBool("mon.voice", false);
            monVoice.CheckedChanged += (s, e) => { Settings.Set("mon.voice", monVoice.Checked); TickStreamMonitor(); };
            Lbl(p, "There's a Listen to stream key for the Stream Deck too.", 25, 780, 8.5f, false, Dim);
            new Panel { Parent = p, Left = 0, Top = 800, Width = 10, Height = 24, BackColor = Bg }; // bottom padding when scrolling
        }

        void ToggleStreamMonitor()
        {
            monOn.Checked = !monOn.Checked;
        }

        void PublishStreamMonitor()
        {
            if (control != null) control.Publish("streammon", "streammon " + (monOn.Checked ? 1 : 0));
        }

        void TickStreamMonitor()
        {
            if (monOn == null) return;
            mixEngine.MonitorChat = monWhat.SelectedIndex == 1;
            mixEngine.MonitorVoice = monVoice.Checked;
            mixEngine.MonitorId = monOn.Checked ? OutputFor("mon.dev") : null;

            string st; Color c = Dim;
            if (!monOn.Checked) st = "";
            else if (!mixEngine.Running) { st = "Turn on Mic Mix to use this."; c = Warn; }
            else if (monWhat.SelectedIndex == 0 && (stOn == null || !stOn.Checked)) { st = "Streamer mode is off. Pick What Discord hears instead."; c = Warn; }
            else if (mixEngine.MonitorStatus == "Running") { st = "Listening"; c = Good; }
            else { st = mixEngine.MonitorStatus; c = mixEngine.MonitorStatus.StartsWith("Stopped") ? Accent : Dim; }
            if (monStatus.Text != st) monStatus.Text = st;
            monStatus.ForeColor = c;
        }
    }
}
