// Streamer mode: besides the chat mic (Discord), SoundDeck feeds a second virtual mic for OBS / Streamlabs,
// with its own mix (set on the Mixer page: stream faders + Stream column). Plus "mute me in chat only".

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        CheckBox stOn, stBoard;
        ComboBox stDev;
        TrackBar stVoice;
        Label stVoiceLbl, stStatus, stObs, stNeed;
        Button stChatMute;

        // Virtual cables SoundDeck can play into, and the mic name apps see on the other end.
        static bool IsVirtualCableOut(string name)
        {
            if (name.IndexOf("16ch", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0
                || (name.IndexOf("CABLE-", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                || name.IndexOf("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string MicNameFor(string cableOut)
        {
            if (cableOut.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0) return "Microphone (Steam Streaming Microphone)";
            return cableOut.Replace("Input", "Output");
        }

        void BuildStreaming()
        {
            var p = NewPage("Streaming", "Streaming", "SoundDeck makes a second mic just for your stream, so Discord and your stream each get their own mix, and you can mute yourself in Discord while your stream still hears you.");

            stOn = Chk(p, "Turn on streamer mode", 25, 104);
            stOn.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            stOn.Checked = Settings.GetBool("stream.on", false);
            stOn.CheckedChanged += (s, e) =>
            {
                Settings.Set("stream.on", stOn.Checked);
                UpdateStreamingUi(); LayoutMixer(); RescaleNow(); RestartMicMix();
            };
            stNeed = Lbl(p, "", 45, 134, 9f, false, Dim);
            stNeed.MaximumSize = new Size(760, 0);

            Lbl(p, "Stream mic", 25, 184, 9.5f, false, Dim);
            stDev = Combo(p, 150, 180, 320);
            stDev.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = stDev.SelectedItem as DeviceItem;
                Settings.Set("stream.dev", it == null || it.Id == null ? "" : it.Id);
                UpdateStreamingUi(); RestartMicMix();
            };
            stObs = Lbl(p, "", 150, 210, 9f, false, Fg);
            stObs.MaximumSize = new Size(700, 0);

            Lbl(p, "Your voice", 25, 258, 9.5f, false, Dim);
            stVoice = new Slider { Left = 150, Top = 252, Width = 300, Minimum = 0, Maximum = 200, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt("stream.voice", 100), 0, 200) };
            p.Controls.Add(stVoice);
            stVoiceLbl = Lbl(p, "", 456, 256, 10f, true, Fg);
            stVoice.ValueChanged += (s, e) =>
            {
                Settings.Set("stream.voice", stVoice.Value); ApplyStreamMix();
                if (stColVoice != null && stColVoice.Value != stVoice.Value) stColVoice.Value = stVoice.Value; // Mixer's Stream column
            };

            stBoard = Chk(p, "Soundboard on stream", 25, 306);
            stBoard.Checked = Settings.GetBool("stream.board", true);
            stBoard.CheckedChanged += (s, e) => { Settings.Set("stream.board", stBoard.Checked); ApplyStreamMix(); };
            var tip = Lbl(p, "Your stream mic carries your voice plus your stream mix. Set the mix on the Mixer page: each channel gets a Stream fader, " +
                "and you can drag apps onto the Stream column. In OBS, use only this mic: " +
                "remove Desktop Audio and your normal mic so nothing plays twice.", 25, 340, 8.5f, false, Dim);
            tip.MaximumSize = new Size(760, 0);

            Lbl(p, "Chat mic", 22, 420, 12f, true, Fg);
            stChatMute = Btn(p, "", 25, 452, 300, 44);
            stChatMute.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            stChatMute.Click += (s, e) => ToggleChatMute();
            var cm = Lbl(p, "Mutes you in Discord only. Your stream still hears you. Also on the Stream Deck as \"Mute chat\".", 340, 466, 9f, false, Dim);
            cm.MaximumSize = new Size(440, 0);

            stStatus = Lbl(p, "", 25, 520, 9.5f, true, Dim);
            stStatus.MaximumSize = new Size(760, 0);
            ApplyStreamMix();
            UpdateChatMuteButton();
            LayoutMixer();
        }

        void ApplyStreamMix()
        {
            stVoiceLbl.Text = stVoice.Value + "%";
            mixEngine.StreamVoiceGain = stVoice.Value / 100f;
            mixEngine.StreamBoard = stBoard.Checked;
        }

        bool ChatMuted { get { return Settings.GetBool("chat.muted", false); } }

        void ToggleChatMute()
        {
            Settings.Set("chat.muted", !ChatMuted);
            UpdateMixGains();
            UpdateChatMuteButton();
            Beep(!ChatMuted);
        }

        void UpdateChatMuteButton()
        {
            if (stChatMute == null) return;
            bool m = ChatMuted;
            stChatMute.Text = m ? "Muted in chat (click to unmute)" : "Mute me in chat";
            stChatMute.BackColor = m ? Color.FromArgb(150, 45, 45) : Card2;
            if (stColChatMute != null)
            {
                stColChatMute.Text = m ? "Muted in chat" : "Mute me in chat";
                stColChatMute.BackColor = stChatMute.BackColor;
            }
            if (control != null) control.Publish("chatmute", "chatmute " + (m ? 1 : 0));
        }

        // The device the stream mic plays into, or null if streamer mode is off or no second cable exists.
        string StreamTargetId()
        {
            if (stOn == null || !stOn.Checked) return null;
            var chat = FindCable();
            var options = renderDevs.Where(d => IsVirtualCableOut(d.Name) && (chat == null || d.Id != chat.Id)).ToList();
            string pick = Settings.Get("stream.dev", "");
            var dev = options.FirstOrDefault(d => d.Id == pick) ?? options.FirstOrDefault();
            return dev == null ? null : dev.Id;
        }

        void FillStreamDevices()
        {
            if (stDev == null) return;
            var chat = FindCable();
            bool prev = loading;
            loading = true;
            stDev.Items.Clear();
            string cur = StreamTargetId() ?? Settings.Get("stream.dev", "");
            int sel = -1;
            foreach (var d in renderDevs.Where(x => IsVirtualCableOut(x.Name) && (chat == null || x.Id != chat.Id)))
            {
                stDev.Items.Add(new DeviceItem(d.Id, MicNameFor(d.Name)));
                if (d.Id == cur) sel = stDev.Items.Count - 1;
            }
            if (sel < 0 && stDev.Items.Count > 0) sel = 0;
            stDev.SelectedIndex = sel;
            loading = prev;
            UpdateStreamingUi();
        }

        void UpdateStreamingUi()
        {
            if (stOn == null) return;
            var chat = FindCable();
            string target = StreamTargetId();
            bool on = stOn.Checked;
            foreach (Control c in new Control[] { stDev, stVoice, stBoard }) c.Enabled = on;

            stNeed.Text = chat == null
                ? "Set up Mic Mix first (it needs a virtual mic)."
                : "Discord / chat apps use: \"" + DiscordInputName(chat) + "\"";
            stNeed.ForeColor = chat == null ? Accent : Dim;

            if (!on) { stObs.Text = ""; }
            else if (stDev.Items.Count == 0)
            {
                stObs.Text = "Streamer mode needs a second virtual mic, because your chat mic already uses \"" + (chat == null ? "a virtual mic" : DiscordInputName(chat)) + "\".\n" +
                    "Easiest: install Steam (free), which adds \"Steam Streaming Microphone\". Or VB-Audio's \"VB-CABLE A+B\" from vb-audio.com.";
                stObs.ForeColor = Accent;
            }
            else
            {
                var it = stDev.SelectedItem as DeviceItem;
                stObs.Text = "In OBS / Streamlabs: add an Audio Input Capture and pick \"" + (it == null ? "" : it.Name) + "\". Remove your normal mic from OBS so your voice isn't doubled.";
                stObs.ForeColor = Fg;
            }
        }

        void TickStreaming()
        {
            if (stStatus == null) return;
            if (!stOn.Checked) { stStatus.Text = "Streamer mode is off."; stStatus.ForeColor = Dim; return; }
            string st = !mixEngine.Running ? "Turn on Mic Mix (Mic Mix page) to use streamer mode." : mixEngine.StreamStatus;
            stStatus.Text = st == "Running" ? "Running. Your stream mic is live." : st;
            stStatus.ForeColor = st == "Running" ? Good : (st.StartsWith("Stopped") || !mixEngine.Running ? Accent : Dim);
        }
    }
}
