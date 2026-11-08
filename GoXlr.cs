// GoXLR / GoXLR Mini support without TC-Helicon's app.
// The GoXLR shows up in Windows as normal audio devices named like "Game (TC-Helicon GoXLR)" or
// "Chat Mic (TC-Helicon GoXLR Mini)". Sending each SoundDeck channel to the matching GoXLR device puts it
// on that GoXLR channel, so the hardware fader and mute button control it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SoundDeck
{
    partial class MainForm
    {
        // SoundDeck channel -> GoXLR channel
        static readonly string[,] GoXlrMap = { { "Game", "Game" }, { "Chat", "Chat" }, { "Media", "Music" }, { "Aux", "System" } };

        Label goxStatus;
        Button goxSetup;
        bool goxAskWhenReady;

        // Called once the window exists (see OnHandleCreated).
        void GoXlrWindowReady()
        {
            if (goxAskWhenReady) { goxAskWhenReady = false; UpdateGoXlr(); }
        }

        static bool IsGoXlr(string name)
        {
            return name.IndexOf("GoXLR", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("GO XLR", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The GoXLR device for a channel name ("Game", "Chat Mic"...), from a list of device names. Null if none.
        static string GoXlrDevice(IEnumerable<string> names, string channel)
        {
            return names.FirstOrDefault(n => IsGoXlr(n) && n.StartsWith(channel + " (", StringComparison.OrdinalIgnoreCase));
        }

        bool HasGoXlr { get { return renderDevs.Any(d => IsGoXlr(d.Name)); } }

        void BuildGoXlr(Panel p)
        {
            int x = 600;
            Lbl(p, "GoXLR", x - 3, 310, 12f, true, Fg);
            goxStatus = Lbl(p, "", x, 340, 9.5f, false, Dim);
            goxStatus.MaximumSize = new Size(380, 0);
            goxSetup = Btn(p, "Set up GoXLR", x, 436, 170, 32);
            goxSetup.Click += (s, e) => SetupGoXlr(true);
            UpdateGoXlr();
        }

        void UpdateGoXlr()
        {
            if (goxStatus == null) return;
            if (!HasGoXlr)
            {
                goxStatus.Text = "No GoXLR connected. Plug it in and this section lets you set it up in one click.";
                goxStatus.ForeColor = Dim;
                goxSetup.Visible = false;
                return;
            }
            goxSetup.Visible = true;
            var names = renderDevs.Select(d => d.Name).ToList();
            var parts = new List<string>();
            for (int i = 0; i < GoXlrMap.GetLength(0); i++)
            {
                string dev = GoXlrDevice(names, GoXlrMap[i, 1]);
                if (dev == null) continue;
                var d = renderDevs.First(r => r.Name == dev);
                bool on = Settings.Get("ch." + GoXlrMap[i, 0] + ".dev", "") == d.Id;
                parts.Add(GoXlrMap[i, 0] + " -> " + GoXlrMap[i, 1] + (on ? " (on)" : ""));
            }
            goxStatus.Text = "GoXLR found. \"Set up GoXLR\" puts SoundDeck's channels on its channels:\n" + string.Join(",  ", parts) +
                "\nand uses its Chat Mic as your mic.";
            goxStatus.ForeColor = Good;

            // The first time a GoXLR shows up, offer to set it up. This can run while the window is still
            // being built, when popups aren't allowed yet, so wait for the window first.
            if (!Settings.GetBool("goxlr.asked", false))
            {
                if (!IsHandleCreated) { goxAskWhenReady = true; return; }
                Settings.Set("goxlr.asked", true);
                BeginInvoke(new Action(() =>
                {
                    var r = MessageBox.Show(this, "SoundDeck found a GoXLR.\n\nSet it up now? Your Game, Chat, Media and Aux channels will play through the GoXLR's Game, Chat, Music and System channels, " +
                        "so its faders and mute buttons control them, and its Chat Mic becomes your mic.\n\nYou can change this anytime in Settings.",
                        "SoundDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r == DialogResult.Yes) SetupGoXlr(false);
                }));
            }
        }

        void SetupGoXlr(bool fromButton)
        {
            var names = renderDevs.Select(d => d.Name).ToList();
            int done = 0;
            for (int i = 0; i < GoXlrMap.GetLength(0); i++)
            {
                string dev = GoXlrDevice(names, GoXlrMap[i, 1]);
                if (dev == null) continue;
                int ci = Array.IndexOf(Channels, GoXlrMap[i, 0]);
                SetChannelOutput(ci, renderDevs.First(r => r.Name == dev).Id);
                done++;
            }
            string mic = GoXlrDevice(captureDevs.Select(d => d.Name), "Chat Mic");
            if (mic != null)
            {
                for (int i = 0; i < micDevice.Items.Count; i++)
                    if (((DeviceItem)micDevice.Items[i]).Name == mic) { micDevice.SelectedIndex = i; break; }
            }
            UpdateGoXlr();
            if (fromButton)
                MessageBox.Show(this, done + " channel(s) now play through the GoXLR" + (mic != null ? ", and its Chat Mic is your mic." : ".") +
                    "\n\nThe GoXLR fader for each channel now controls it. Turn the matching SoundDeck slider up to 100% so the fader has the full range.",
                    "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
