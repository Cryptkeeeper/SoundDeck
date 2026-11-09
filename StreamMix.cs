// Stream mixer on the Mixer page (streamer mode only) and per-app MIC / LIVE picks.
//  - Each channel gets a second fader + mute for what the stream hears, independent of what you hear.
//  - The Stream column sets your voice level on stream and lists every app on stream; drop an app on it to force it on.
//  - Drop an app on the Mic column (or right-click > Play in my mic) to put it in your chat mic with Ctrl+Alt+N.
// App audio is recorded before its volume, so your own volume, mute and ducking never change the stream.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        static readonly Color StreamColor = Color.FromArgb(235, 70, 70);
        // Lowest app volume SoundDeck sets. At exactly 0 Windows stops the app's audio, which would cut it from
        // the stream mix too; 0.0001 is -80 dB, so it's silent for you but still recorded at full level.
        const float SilentVolume = 0.0001f;

        TrackBar[] chSVol = new Slider[4];
        Label[] chSPct = new Label[4], chYouCap = new Label[4], chStreamCap = new Label[4];
        Button[] chSMute = new Button[4];
        Panel streamCard, leaveCard;
        TrackBar stColVoice;
        LevelMeter stColMeter;
        Label stColPct;
        Button stColChatMute;
        FlowLayoutPanel streamZone;
        float stColLevel;

        bool StreamerOn { get { return stOn != null && stOn.Checked; } }

        // ---------- Feeding the mic/stream engine ----------

        // Windows records an app *after* its volume, so an app you turned down (or muted) would be quiet on
        // stream too. Each app's mic/stream level is divided by the volume it has for you, which cancels that
        // out: your volume, mute and ducking never change what friends or your stream hear.
        void UpdateEngineSources()
        {
            var gains = new Dictionary<uint, float[]>();
            foreach (var s in lastSessions)
            {
                if (gains.ContainsKey(s.Pid)) continue;
                float gm = InMic(s.Exe) ? 1f : 0f, gs = StreamGainFor(s.Exe);
                if (gm <= 0 && gs <= 0) continue;
                float vol = 1f;
                try { bool muted; s.Volume.GetMasterVolume(out vol); s.Volume.GetMute(out muted); if (muted) vol = 0f; } catch { }
                float undo = 1f / Math.Max(vol, SilentVolume);
                gains[s.Pid] = new[] { gm * undo, gs * undo };
            }
            mixEngine.SetSources(gains);
        }

        // ---------- What goes where ----------

        // In the chat mic (with the music hotkey)? Apps never picked follow the old "whole channel" setting.
        bool InMic(string exe)
        {
            string v = Settings.Get("app." + exe + ".mic", null);
            if (v != null) return v == "1";
            return ChannelFor(exe) == Settings.Get("mix.source", "Media");
        }

        // How loud an app is on stream (0 = not on stream). LIVE tag: "1" always, "0" never, none = follow its channel.
        float StreamGainFor(string exe)
        {
            if (!StreamerOn) return 0f;
            string tag = Settings.Get("app." + exe + ".live", "");
            int ci = Array.IndexOf(Channels, ChannelFor(exe));
            if (tag == "0") return 0f;
            float chVolume = ci >= 0 ? Settings.GetInt("ch." + Channels[ci] + ".svol", 100) / 100f : 1f;
            if (tag == "1") return ci >= 0 ? chVolume : 1f;
            if (ci < 0 || Settings.GetBool("ch." + Channels[ci] + ".smute", false)) return 0f;
            return chVolume;
        }

        void SetMic(string exe, bool on)
        {
            Settings.Set("app." + exe + ".mic", on);
            AppTagsChanged();
        }

        void SetLive(string exe, string tag)
        {
            if (tag == "") Settings.Remove("app." + exe + ".live"); else Settings.Set("app." + exe + ".live", tag);
            AppTagsChanged();
        }

        void AppTagsChanged()
        {
            appsKey = null;
            UpdateAppsList();
            TickMicMix();
        }

        ContextMenuStrip AppMenu(string exe, string channel)
        {
            var menu = new ContextMenuStrip();
            foreach (var ch in Channels.Concat(new[] { "None" }))
            {
                string target = ch;
                var item = new ToolStripMenuItem(ch == "None" ? "Leave alone" : "Move to " + ch) { Checked = ch == channel };
                item.Click += (s, e) => MoveApp(exe, target);
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            bool inMic = InMic(exe);
            bool hasKey = mixHotkeyBox != null && Settings.GetInt("mix.vk", 0) != 0;
            var mic = new ToolStripMenuItem("Play in my mic (" + (hasKey ? "with " + mixHotkeyBox.Text : "when music in mic is on") + ")") { Checked = inMic };
            mic.Click += (s, e) => SetMic(exe, !inMic);
            menu.Items.Add(mic);
            if (StreamerOn)
            {
                string tag = Settings.Get("app." + exe + ".live", "");
                var stream = new ToolStripMenuItem("On stream");
                foreach (var opt in new[] { new[] { "", "Follow its channel's stream fader" }, new[] { "1", "Always on stream" }, new[] { "0", "Never on stream" } })
                {
                    string t = opt[0];
                    var it = new ToolStripMenuItem(opt[1]) { Checked = tag == t };
                    it.Click += (s, e) => SetLive(exe, t);
                    stream.DropDownItems.Add(it);
                }
                menu.Items.Add(stream);
            }
            return menu;
        }

        static void ChipTag(Graphics g, string text, Color color, int x, int h)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = text.Length > 3 ? 28 : 26, y = h / 2 - 8;
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(color))
            using (var f = new Font("Segoe UI", 6.5f, FontStyle.Bold))
            {
                path.AddArc(x, y, 6, 6, 180, 90); path.AddArc(x + w - 6, y, 6, 6, 270, 90);
                path.AddArc(x + w - 6, y + 10, 6, 6, 0, 90); path.AddArc(x, y + 10, 6, 6, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, f, Brushes.White, new RectangleF(x, y, w, 16), sf);
            }
        }

        // Dropping an app chip onto a control runs act(exe) instead of moving it to a channel.
        void AddAppDrop(Control c, Action<string> act, Control glow)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                var data = e.Data.GetData(typeof(string)) as string;
                if (data == null || !data.StartsWith(DragPrefix)) return;
                e.Effect = DragDropEffects.Link;
                if (glow.Tag == null) glow.Tag = glow.BackColor;
                glow.BackColor = ZoneHot;
            };
            c.DragLeave += (s, e) => { if (glow.Tag is Color) glow.BackColor = (Color)glow.Tag; };
            c.DragDrop += (s, e) =>
            {
                if (glow.Tag is Color) glow.BackColor = (Color)glow.Tag;
                var data = e.Data.GetData(typeof(string)) as string;
                if (data != null && data.StartsWith(DragPrefix)) BeginInvoke(new Action(() => act(data.Substring(DragPrefix.Length))));
            };
            foreach (Control child in c.Controls) AddAppDrop(child, act, glow);
        }

        // ---------- Stream fader on each channel card ----------

        void BuildStreamFader(Panel card, int ci)
        {
            string ch = Channels[ci];
            chYouCap[ci] = Lbl(card, "YOU", 24, 92, 7.5f, true, Dim);
            chStreamCap[ci] = Lbl(card, "STREAM", 98, 92, 7.5f, true, StreamColor);
            var tb = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
                Left = 100, Top = 108, Height = 112, BackColor = Card, SmallChange = 2, LargeChange = 10,
                Value = Clamp(Settings.GetInt("ch." + ch + ".svol", 100), 0, 100)
            };
            card.Controls.Add(tb);
            chSVol[ci] = tb;
            var pct = Lbl(card, tb.Value + "%", 90, 222, 12f, true, StreamColor);
            pct.AutoSize = false; pct.Width = 90; pct.Height = 24; pct.TextAlign = ContentAlignment.MiddleCenter;
            chSPct[ci] = pct;
            tb.ValueChanged += (s, e) =>
            {
                chSPct[ci].Text = chSVol[ci].Value + "%";
                Settings.Set("ch." + ch + ".svol", chSVol[ci].Value);
                appsKey = null;
            };
            var m = Btn(card, "", 92, 250, 76, 28);
            m.Click += (s, e) =>
            {
                Settings.Set("ch." + ch + ".smute", !Settings.GetBool("ch." + ch + ".smute", false));
                UpdateStreamMute(ci);
                appsKey = null; TickMicMix();
            };
            chSMute[ci] = m;
            UpdateStreamMute(ci);
        }

        void UpdateStreamMute(int ci)
        {
            bool m = Settings.GetBool("ch." + Channels[ci] + ".smute", false);
            chSMute[ci].Text = m ? "Off stream" : "Stream";
            chSMute[ci].BackColor = m ? Color.FromArgb(150, 45, 45) : Card2;
        }

        // ---------- Stream column ----------

        void BuildStreamColumn(Panel p, int x)
        {
            var card = new Panel { Left = x, Top = 86, Width = 180, Height = 590, BackColor = Card };
            p.Controls.Add(card);
            streamCard = card;
            card.Controls.Add(new Panel { Left = 0, Top = 0, Width = 180, Height = 4, BackColor = StreamColor });
            Lbl(card, "Stream", 12, 14, 14f, true, StreamColor);
            var note = Lbl(card, "What your stream hears. Your voice:", 14, 48, 8.5f, false, Dim);
            note.MaximumSize = new Size(156, 0);

            stColVoice = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = 0, Maximum = 200, TickStyle = TickStyle.None,
                Left = 68, Top = 100, Height = 120, BackColor = Card, SmallChange = 2, LargeChange = 10,
                Value = Clamp(Settings.GetInt("stream.voice", 100), 0, 200)
            };
            card.Controls.Add(stColVoice);
            stColMeter = new LevelMeter(StreamColor) { Left = 122, Top = 106, Width = 10, Height = 108 };
            card.Controls.Add(stColMeter);
            tips.SetToolTip(stColMeter, "Everything your stream mic is sending: your voice, stream apps and sounds.");
            stColPct = Lbl(card, "", 0, 222, 12f, true, Fg);
            stColPct.AutoSize = false; stColPct.Width = 180; stColPct.Height = 24; stColPct.TextAlign = ContentAlignment.MiddleCenter;
            stColVoice.ValueChanged += (s, e) =>
            {
                stColPct.Text = "Voice " + stColVoice.Value + "%";
                if (stVoice != null && stVoice.Value != stColVoice.Value) stVoice.Value = stColVoice.Value; // Streaming page slider saves it
            };
            stColPct.Text = "Voice " + stColVoice.Value + "%";

            stColChatMute = Btn(card, "", 12, 250, 156, 28);
            stColChatMute.Click += (s, e) => ToggleChatMute();
            tips.SetToolTip(stColChatMute, "Mutes you in Harmony only. Your stream still hears you.");

            Lbl(card, "On stream", 14, 292, 8.5f, false, Dim);
            streamZone = NewZone(card, 312, 270);
            AddAppDrop(card, exe => SetLive(exe, "1"), streamZone);
        }

        void FillStreamZone(List<AppSession> apps)
        {
            if (streamZone == null) return;
            streamZone.SuspendLayout();
            foreach (Control old in streamZone.Controls.Cast<Control>().ToList())
            {
                if (old.ContextMenuStrip != null) old.ContextMenuStrip.Dispose();
                old.Dispose();
            }
            streamZone.Controls.Clear();
            if (StreamerOn)
            {
                foreach (var a in apps.Where(x => StreamGainFor(x.Exe) > 0))
                {
                    string ch = Array.IndexOf(Channels, ChannelFor(a.Exe)) >= 0 ? ChannelFor(a.Exe) : "None";
                    var chip = MakeChip(a, ch);
                    AddAppDrop(chip, exe => SetLive(exe, "1"), streamZone);
                    streamZone.Controls.Add(chip);
                }
            }
            if (streamZone.Controls.Count == 0)
            {
                var hint = new Label { Text = "Drag apps here to put them on stream", Width = 140, Height = 40, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(95, 100, 108), Margin = new Padding(4, 4, 0, 0) };
                AddAppDrop(hint, exe => SetLive(exe, "1"), streamZone);
                streamZone.Controls.Add(hint);
            }
            streamZone.ResumeLayout();
            if (uiScale != 1f) ScaleTree(streamZone);
        }

        // ---------- Layout: normal vs streamer mode ----------

        void LayoutMixer()
        {
            bool on = StreamerOn;
            for (int i = 0; i < 4; i++)
            {
                if (chSVol[i] == null) continue;
                foreach (Control c in new Control[] { chSVol[i], chSPct[i], chSMute[i], chYouCap[i], chStreamCap[i] }) c.Visible = on;
                if (on)
                {
                    Place(chVol[i], 16, 108, chVol[i].Width, 112);
                    Place(chMeter[i], 62, 114, 8, 100);
                    Place(chPct[i], 0, 222, 90, 24);
                    Place(chMute[i], 12, 250, 76, 28);
                }
                else
                {
                    Place(chVol[i], 68, 100, chVol[i].Width, 120);
                    Place(chMeter[i], 122, 106, 10, 108);
                    Place(chPct[i], 0, 222, 180, 24);
                    Place(chMute[i], 12, 250, 156, 28);
                }
            }
            if (streamCard != null) streamCard.Visible = on;
            if (leaveCard != null) Place(leaveCard, 24 + (on ? 6 : 5) * 192, 86, 180, 590);
            appsKey = null;
            UpdateChatMuteButton();
        }

        void TickStreamMeter(bool onScreen)
        {
            if (!StreamerOn || stColMeter == null || !onScreen || !pages["Mixer"].Visible) return;
            stColLevel = Math.Max(MeterPos(mixEngine.TakeStreamPeak()), stColLevel - 0.04f);
            stColMeter.Value = stColLevel;
        }
    }
}
