// "Gate & compressor" section on the Equalizer page, below the EQ bands.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        CheckBox gateOn, compOn;
        TrackBar gateThr, gateRel, compThr, compRatio, compMakeup;
        Label gateState, compState;

        void BuildDynamicsUi()
        {
            var p = pages["Equalizer"];
            Lbl(p, "Gate & compressor", 22, 540, 12f, true, Fg);
            var note = Lbl(p, "For your mic. Built into SoundDeck, so they work without Equalizer APO. They run right after Remove noise, on the mic your friends and your stream hear, and on Hear yourself. They need Mic Mix turned on.", 25, 568, 9f, false, Dim);
            note.MaximumSize = new Size(760, 0);

            gateOn = Chk(p, "Noise gate", 25, 606);
            gateOn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            gateState = Lbl(p, "", 160, 608, 9.5f, true, Dim);
            gateThr = SliderRow(p, "Opens at", 640, -80, -20, "gate.thr", -50, v => v + " dB");
            gateRel = SliderRow(p, "Closes after", 680, 50, 1000, "gate.rel", 200, v => v + " ms");
            var gTip = Lbl(p, "Talk normally and watch Open / Closed. Lower \"Opens at\" until your quietest words open it; raise it if keyboard or fan noise does.", 150, 718, 8.5f, false, Dim);
            gTip.MaximumSize = new Size(640, 0);

            compOn = Chk(p, "Compressor", 25, 760);
            compOn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            compState = Lbl(p, "", 160, 762, 9.5f, true, Dim);
            compThr = SliderRow(p, "Starts at", 794, -50, 0, "comp.thr", -20, v => v + " dB");
            compRatio = SliderRow(p, "Strength", 834, 1, 20, "comp.ratio", 4, v => v + ":1");
            compMakeup = SliderRow(p, "Boost after", 874, 0, 24, "comp.makeup", 6, v => "+" + v + " dB");
            var cTip = Lbl(p, "Turns loud moments (laughing, shouting) down so you sound even. Strength 3:1 to 6:1 suits most voices; \"Boost after\" brings your overall level back up.", 150, 912, 8.5f, false, Dim);
            cTip.MaximumSize = new Size(640, 0);
            new Panel { Parent = p, Left = 0, Top = 950, Width = 10, Height = 30, BackColor = Bg }; // bottom padding when scrolling

            gateOn.Checked = Settings.GetBool("gate.on", false);
            compOn.Checked = Settings.GetBool("comp.on", false);
            gateOn.CheckedChanged += (s, e) => { Settings.Set("gate.on", gateOn.Checked); ApplyDynamics(); };
            compOn.CheckedChanged += (s, e) => { Settings.Set("comp.on", compOn.Checked); ApplyDynamics(); };
            ApplyDynamics();
        }

        TrackBar SliderRow(Panel p, string label, int y, int min, int max, string key, int def, Func<int, string> fmt)
        {
            Lbl(p, label, 45, y + 4, 9.5f, false, Dim);
            var tb = new TrackBar { Left = 150, Top = y - 2, Width = 300, Minimum = min, Maximum = max, TickStyle = TickStyle.None, BackColor = Bg, Value = Clamp(Settings.GetInt(key, def), min, max) };
            p.Controls.Add(tb);
            var val = Lbl(p, fmt(tb.Value), 456, y + 2, 10f, true, Fg);
            tb.ValueChanged += (s, e) => { val.Text = fmt(tb.Value); Settings.Set(key, tb.Value); ApplyDynamics(); };
            return tb;
        }

        void ApplyDynamics()
        {
            if (compMakeup == null) return;
            VoiceDynamics.GateOn = gateOn.Checked;
            VoiceDynamics.GateThresholdDb = gateThr.Value;
            VoiceDynamics.GateReleaseMs = gateRel.Value;
            VoiceDynamics.CompOn = compOn.Checked;
            VoiceDynamics.CompThresholdDb = compThr.Value;
            VoiceDynamics.CompRatio = compRatio.Value;
            VoiceDynamics.CompMakeupDb = compMakeup.Value;
            foreach (var c in new Control[] { gateThr, gateRel }) c.Enabled = gateOn.Checked;
            foreach (var c in new Control[] { compThr, compRatio, compMakeup }) c.Enabled = compOn.Checked;
        }

        // Live "Open / Closed" and "turning down X dB" while the Equalizer page is showing.
        void TickDynamicsUi(bool onScreen)
        {
            if (gateState == null || !onScreen || !pages["Equalizer"].Visible) return;
            bool running = mixEngine.Running;
            if (!gateOn.Checked) { gateState.Text = ""; }
            else if (!running) { gateState.Text = "Turn on Mic Mix to use it"; gateState.ForeColor = Warn; }
            else { bool open = VoiceDynamics.GateOpen; gateState.Text = open ? "Open" : "Closed"; gateState.ForeColor = open ? Good : Dim; }
            if (!compOn.Checked) { compState.Text = ""; }
            else if (!running) { compState.Text = "Turn on Mic Mix to use it"; compState.ForeColor = Warn; }
            else
            {
                float gr = VoiceDynamics.GainReductionDb;
                compState.Text = gr >= 0.5f ? "Turning down " + Math.Round(gr) + " dB" : "Not turning down";
                compState.ForeColor = gr >= 0.5f ? Good : Dim;
            }
        }

        void ReloadDynamicsFromSettings()
        {
            if (compMakeup == null) return;
            gateOn.Checked = Settings.GetBool("gate.on", false);
            compOn.Checked = Settings.GetBool("comp.on", false);
            gateThr.Value = Clamp(Settings.GetInt("gate.thr", -50), gateThr.Minimum, gateThr.Maximum);
            gateRel.Value = Clamp(Settings.GetInt("gate.rel", 200), gateRel.Minimum, gateRel.Maximum);
            compThr.Value = Clamp(Settings.GetInt("comp.thr", -20), compThr.Minimum, compThr.Maximum);
            compRatio.Value = Clamp(Settings.GetInt("comp.ratio", 4), compRatio.Minimum, compRatio.Maximum);
            compMakeup.Value = Clamp(Settings.GetInt("comp.makeup", 6), compMakeup.Minimum, compMakeup.Maximum);
            ApplyDynamics();
        }
    }
}
