// Push to talk / push to mute, on the Microphone page.
// Mutes the Windows mic itself, so it works in Discord, games and everything else.
// The key is read straight from the keyboard/mouse state, so it works while a game has focus,
// and it doesn't block the key from reaching the game. A Stream Deck "Push to talk" key works too.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundDeck
{
    partial class MainForm
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        ComboBox pttMode;
        TextBox pttKeyBox;
        Label pttStatus;
        Timer pttTimer;
        bool pttDeckHeld;        // set by the Stream Deck key ("ptt down" / "ptt up")
        bool pttApplied;         // we've set a mute state since the mode was turned on
        bool pttMutedNow;        // the state we last set
        string pttMicId;         // the mic we last set it on

        static readonly string[] PttModes = { "Off", "Push to talk (muted until you hold the key)", "Push to mute (hold the key to mute)" };

        void BuildPushToTalk()
        {
            var p = pages["Microphone"];
            Lbl(p, "Push to talk", 22, 770, 12f, true, Fg);
            var note = Lbl(p, "Mutes your mic everywhere (Discord, games, your stream) unless you hold a key or mouse button. Works while you're in a game.", 25, 798, 9f, false, Dim);
            note.MaximumSize = new Size(760, 0);

            Lbl(p, "Mode", 25, 836, 9.5f, false, Dim);
            pttMode = Combo(p, 150, 832, 330);
            foreach (var m in PttModes) pttMode.Items.Add(m);
            pttMode.SelectedIndex = Clamp(Settings.GetInt("ptt.mode", 0), 0, 2);
            pttMode.SelectedIndexChanged += (s, e) =>
            {
                Settings.Set("ptt.mode", pttMode.SelectedIndex);
                if (pttMode.SelectedIndex == 0) PttRelease();
                pttApplied = false;
                UpdatePttTimer();
            };

            Lbl(p, "Key", 25, 878, 9.5f, false, Dim);
            pttKeyBox = new TextBox { Left = 150, Top = 874, Width = 220, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true, Cursor = Cursors.Hand };
            pttKeyBox.Text = PttKeyText(Settings.GetInt("ptt.vk", 0));
            pttKeyBox.Enter += (s, e) => pttKeyBox.BackColor = Color.FromArgb(70, 50, 40);
            pttKeyBox.Leave += (s, e) => pttKeyBox.BackColor = Card2;
            pttKeyBox.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true; e.Handled = true;
                Keys k = e.KeyCode;
                if (k == Keys.Tab || k == Keys.Escape) { p.Focus(); return; }
                SetPttKey(k == Keys.Back || k == Keys.Delete ? 0 : (int)k);
                p.Focus();
            };
            // Mouse side buttons and the middle button can be the key too.
            pttKeyBox.MouseDown += (s, e) =>
            {
                if (!pttKeyBox.Focused) return;
                int vk = e.Button == MouseButtons.XButton1 ? 0x05 : e.Button == MouseButtons.XButton2 ? 0x06 : e.Button == MouseButtons.Middle ? 0x04 : 0;
                if (vk == 0) return;
                SetPttKey(vk);
                p.Focus();
            };
            p.Controls.Add(pttKeyBox);
            Lbl(p, "Click the box, then press a key or a mouse side button. Backspace clears it.", 380, 878, 8.5f, false, Dim);

            pttStatus = Lbl(p, "", 25, 916, 10f, true, Dim);
            Lbl(p, "There's also a Push to talk key for the Stream Deck.", 25, 942, 8.5f, false, Dim);
            new Panel { Parent = p, Left = 0, Top = 966, Width = 10, Height = 30, BackColor = Bg }; // bottom padding when scrolling

            pttTimer = new Timer { Interval = 15 };
            pttTimer.Tick += (s, e) => TickPushToTalk();
            Application.ApplicationExit += (s, e) => PttRelease(); // never leave the mic muted after closing
            UpdatePttTimer();
        }

        void SetPttKey(int vk)
        {
            Settings.Set("ptt.vk", vk);
            pttKeyBox.Text = PttKeyText(vk);
            pttApplied = false;
        }

        static string PttKeyText(int vk)
        {
            if (vk == 0) return "(none)";
            if (vk == 0x04) return "Middle mouse button";
            if (vk == 0x05) return "Mouse side button (back)";
            if (vk == 0x06) return "Mouse side button (forward)";
            switch ((Keys)vk)
            {
                case Keys.ControlKey: return "Ctrl";
                case Keys.ShiftKey: return "Shift";
                case Keys.Menu: return "Alt";
                case Keys.Capital: return "Caps Lock";
                case Keys.Oemtilde: return "` (tilde key)";
            }
            return HotkeyText(0, vk);
        }

        void UpdatePttTimer()
        {
            if (pttTimer == null) return;
            pttTimer.Enabled = pttMode.SelectedIndex != 0;
            TickPushToTalk();
        }

        // Runs every 15 ms while push to talk is on (and from the 50 ms tick, so it can't get stuck).
        void TickPushToTalk()
        {
            if (pttMode == null) return;
            int mode = pttMode.SelectedIndex;
            int vk = Settings.GetInt("ptt.vk", 0);
            bool held = pttDeckHeld || (vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0);

            if (mode == 0) { ShowPttStatus("Off", Dim); return; }
            if (vk == 0 && !pttDeckHeld && mode == 2) { ShowPttStatus("Pick a key below.", Warn); return; }

            bool wantMuted = mode == 1 ? !held : held;
            var mic = micDevice.SelectedItem as DeviceItem;
            string micId = mic == null ? null : mic.Id;
            if (micId == null) { ShowPttStatus("No mic selected.", Warn); return; }

            if (!pttApplied || wantMuted != pttMutedNow || micId != pttMicId)
            {
                if (pttApplied && pttMicId != null && micId != pttMicId && pttMutedNow) Audio.SetMute(pttMicId, false); // mic was switched
                Audio.SetMute(micId, wantMuted);
                pttApplied = true; pttMutedNow = wantMuted; pttMicId = micId;
                LoadMicColumn();
            }

            if (wantMuted) ShowPttStatus(mode == 1 ? (vk == 0 ? "Mic muted. Pick a key below, or use the Stream Deck key." : "Mic muted. Hold " + PttKeyText(vk) + " to talk.") : "Mic muted while you hold the key.", Accent);
            else ShowPttStatus(mode == 1 ? "Talking" : "Mic on. Hold " + PttKeyText(vk) + " to mute.", Good);
        }

        void ShowPttStatus(string text, Color c)
        {
            if (pttStatus.Text != text) pttStatus.Text = text;
            if (pttStatus.ForeColor != c) pttStatus.ForeColor = c;
        }

        // Unmutes the mic if push to talk muted it.
        void PttRelease()
        {
            if (pttApplied && pttMutedNow && pttMicId != null) Audio.SetMute(pttMicId, false);
            pttApplied = false; pttMutedNow = false;
            try { LoadMicColumn(); } catch { }
        }
    }
}
