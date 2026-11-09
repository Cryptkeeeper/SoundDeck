// Controls a GoXLR through GoXLR Utility (free, open source, https://github.com/GoXLR-on-Linux/goxlr-utility),
// which runs a local web API on port 14564: POST /api/command with "GetStatus" or {"Command":[serial,{...}]}.
//  - GoXLR faders/mutes <-> SoundDeck channel sliders/mutes (and so the Stream Deck dials), both ways
//  - Mic column level/mute <-> GoXLR mic gain and cough (mute) button
//  - which channel each fader controls, and fader/button colours matching SoundDeck's
// A channel is only linked when its apps actually play through the matching GoXLR channel ("Set up GoXLR").

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SoundDeck
{
    // One snapshot of the GoXLR's state, read from GoXLR Utility.
    class GoxState
    {
        public string Serial, DeviceType, MicType;
        public Dictionary<string, int> Volumes = new Dictionary<string, int>();      // GoXLR channel -> 0..255
        public Dictionary<string, string> FaderChannel = new Dictionary<string, string>(); // "A".."D" -> channel
        public Dictionary<string, string> FaderMute = new Dictionary<string, string>();    // "A".."D" -> MuteState
        public int MicGain;          // dB, for the current mic type
        public string CoughState = "Unmuted";
        public int Version;          // goes up each time a new snapshot arrives
    }

    static class GoxClient
    {
        public static int Port = 14564;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        static int version;

        static string Post(object request)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + "/api/command");
            req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 1500; req.ReadWriteTimeout = 1500;
            req.Proxy = null;
            byte[] body = Encoding.UTF8.GetBytes(json.Serialize(request));
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        // Null when GoXLR Utility isn't running or no GoXLR is connected.
        public static GoxState GetStatus()
        {
            var root = json.DeserializeObject(Post("GetStatus")) as Dictionary<string, object>;
            var status = root != null && root.ContainsKey("Status") ? root["Status"] as Dictionary<string, object> : null;
            var mixers = status != null && status.ContainsKey("mixers") ? status["mixers"] as Dictionary<string, object> : null;
            if (mixers == null || mixers.Count == 0) return null;
            var kv = mixers.First();
            var m = kv.Value as Dictionary<string, object>;
            var st = new GoxState { Serial = kv.Key, Version = Interlocked.Increment(ref version) };
            try { st.DeviceType = (string)((Dictionary<string, object>)((Dictionary<string, object>)m["hardware"]))["device_type"]; } catch { st.DeviceType = "Full"; }
            var levels = (Dictionary<string, object>)m["levels"];
            foreach (var v in (Dictionary<string, object>)levels["volumes"]) st.Volumes[v.Key] = Convert.ToInt32(v.Value);
            foreach (var f in (Dictionary<string, object>)m["fader_status"])
            {
                var fs = (Dictionary<string, object>)f.Value;
                st.FaderChannel[f.Key] = (string)fs["channel"];
                st.FaderMute[f.Key] = (string)fs["mute_state"];
            }
            var mic = (Dictionary<string, object>)m["mic_status"];
            st.MicType = (string)mic["mic_type"];
            var gains = (Dictionary<string, object>)mic["mic_gains"];
            if (gains.ContainsKey(st.MicType)) st.MicGain = Convert.ToInt32(gains[st.MicType]);
            try { st.CoughState = (string)((Dictionary<string, object>)m["cough_button"])["state"]; } catch { }
            return st;
        }

        // Sends one GoXLR command, e.g. Command(serial, "SetVolume", "Game", 200). Returns null if OK, else the error.
        public static string Command(string serial, string name, params object[] args)
        {
            object payload = args.Length == 1 ? args[0] : (object)args;
            var cmd = new Dictionary<string, object> { { "Command", new object[] { serial, new Dictionary<string, object> { { name, payload } } } } };
            try
            {
                string resp = Post(cmd);
                return resp.Trim().Trim('"') == "Ok" ? null : resp;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // Installing and starting GoXLR Utility so it feels like part of SoundDeck: a silent install (one Windows
    // permission prompt, none of its setup screens), then SoundDeck starts its background part hidden.
    static class GoxUtil
    {
        public const string Url = "https://github.com/GoXLR-on-Linux/goxlr-utility/releases/download/v1.2.4/goxlr-utility-1.2.4.exe";
        public const string Sha = "0A8743D27EA5D1741956AECFC3AE26C0F7399945CF29D2BB6B3DAB459E86AC2F"; // GitHub's published SHA-256

        // Where its installer put it (it records the folder in the registry), or null.
        public static string Dir()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\GoXLR Utility"))
                {
                    string p = k == null ? null : k.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "goxlr-daemon.exe"))) return p;
                }
            }
            catch { }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GoXLR Utility");
            return File.Exists(Path.Combine(def, "goxlr-daemon.exe")) ? def : null;
        }

        public static bool Running { get { return Process.GetProcessesByName("goxlr-daemon").Length > 0; } }

        // TC-Helicon's own GoXLR App ("GoXLR App.exe"). Only one of the two can talk to the GoXLR at a time.
        // GoXLR Utility's own programs are all named "goxlr-...", so they don't count.
        public static bool OfficialAppRunning()
        {
            foreach (var p in Process.GetProcesses())
            {
                string n = p.ProcessName;
                if (n.StartsWith("goxlr", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("goxlr-", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Starts its background part (tray icon only, no window). SoundDeck isn't elevated, so neither is it.
        public static bool Start()
        {
            string dir = Dir();
            if (dir == null || Running || OfficialAppRunning()) return false;
            try { Process.Start(new ProcessStartInfo(Path.Combine(dir, "goxlr-daemon.exe")) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true }); return true; }
            catch { return false; }
        }

        // Runs on a background thread. Throws with a plain-English reason if it didn't install.
        public static void Install(Action<string> say)
        {
            if (OfficialAppRunning()) throw new Exception("close TC-Helicon's GoXLR App first (only one can control the GoXLR), then try again");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            byte[] data;
            using (var wc = new WebClient()) { wc.Headers.Add("User-Agent", "SoundDeck/" + AppVersion.Value); data = wc.DownloadData(Url); }
            string got;
            using (var h = System.Security.Cryptography.SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "");
            if (!got.Equals(Sha, StringComparison.OrdinalIgnoreCase)) throw new Exception("the download didn't match the expected file, so it wasn't used");
            string tmp = Path.Combine(Path.GetTempPath(), "goxlr-utility-1.2.4.exe");
            File.WriteAllBytes(tmp, data);
            say("Installing GoXLR Utility. Click Yes when Windows asks...");
            // /S = silent: no setup screens. It also adds itself to Windows startup.
            var p = Process.Start(new ProcessStartInfo(tmp, "/S") { Verb = "runas", UseShellExecute = true });
            p.WaitForExit();
            try { File.Delete(tmp); } catch { }
            if (Dir() == null) throw new Exception("its setup didn't finish");
            say("Starting GoXLR Utility...");
            Start();
        }
    }

    partial class MainForm
    {
        // SoundDeck channel -> GoXLR channel (same mapping as "Set up GoXLR").
        static string GoxChannelFor(int ci) { return GoXlrMap[ci, 1]; }

        static readonly string[] GoxChannels = { "Mic", "LineIn", "Console", "System", "Game", "Chat", "Sample", "Music", "Headphones", "MicMonitor", "LineOut" };
        static readonly string[] GoxFaders = { "A", "B", "C", "D" };
        const int GoxMaxMicGain = 72;

        volatile GoxState gox;           // latest snapshot (null = not connected)
        int goxSeen;                     // last snapshot version applied to the UI
        bool goxApplying;                // true while copying GoXLR values into SoundDeck controls (don't echo back)
        DateTime[] goxSentVol = new DateTime[4], goxSentMute = new DateTime[4];
        DateTime goxSentMic, goxSentCough;
        Thread goxThread;
        CheckBox goxLink;
        ComboBox[] goxFaderCombo = new ComboBox[4];
        Label goxUtilStatus;
        Button goxColors, goxInstall;
        bool goxInstalling;

        bool GoxLinkOn { get { return gox != null && goxLink != null && goxLink.Checked; } }

        // Linked = SoundDeck controls this channel through the GoXLR (its apps play through the matching GoXLR channel).
        bool ChannelLinked(int ci)
        {
            if (!GoxLinkOn) return false;
            string dev = GoXlrDevice(renderDevs.Select(d => d.Name), GoxChannelFor(ci));
            var d2 = dev == null ? null : renderDevs.FirstOrDefault(r => r.Name == dev);
            return d2 != null && Settings.Get("ch." + Channels[ci] + ".dev", "") == d2.Id;
        }

        string FaderFor(GoxState st, string goxChannel)
        {
            foreach (var f in GoxFaders) { string c; if (st.FaderChannel.TryGetValue(f, out c) && c == goxChannel) return f; }
            return null;
        }

        bool MicIsGoxlr()
        {
            var mic = micDevice.SelectedItem as DeviceItem;
            return mic != null && IsGoXlr(mic.Name);
        }

        // The volume SoundDeck puts on an app for you. Linked channels leave it at full and let the GoXLR do it.
        float PersonalVolume(int ci)
        {
            bool muted = Settings.GetBool("ch." + Channels[ci] + ".mute", false);
            if (ChannelLinked(ci))
            {
                bool goxMutes = gox != null && FaderFor(gox, GoxChannelFor(ci)) != null; // the fader's mute button handles it
                return Math.Max(SilentVolume, (muted && !goxMutes ? 0f : 1f) * duck[ci]);
            }
            return Math.Max(SilentVolume, muted ? 0f : chVol[ci].Value / 100f * duck[ci]);
        }

        // ---------- Talking to GoXLR Utility (background thread) ----------

        void StartGoxPolling()
        {
            goxThread = new Thread(() =>
            {
                while (true)
                {
                    GoxState st = null;
                    try { st = GoxClient.GetStatus(); } catch { }
                    gox = st;
                    Thread.Sleep(st == null ? 3000 : 250);
                }
            }) { IsBackground = true, Name = "GoXLR" };
            goxThread.Start();
        }

        void GoxSend(string name, params object[] args)
        {
            var st = gox;
            if (st == null) return;
            ThreadPool.QueueUserWorkItem(_ => GoxClient.Command(st.Serial, name, args));
        }

        // ---------- SoundDeck -> GoXLR ----------

        void GoxOnChannelVolume(int ci)
        {
            if (goxApplying || !ChannelLinked(ci)) return;
            goxSentVol[ci] = DateTime.Now;
            GoxSend("SetVolume", GoxChannelFor(ci), (int)Math.Round(chVol[ci].Value * 255 / 100.0));
        }

        // Returns true if the GoXLR handled the mute (so SoundDeck shouldn't also mute the apps).
        void GoxOnChannelMute(int ci)
        {
            if (goxApplying || !ChannelLinked(ci)) return;
            string f = FaderFor(gox, GoxChannelFor(ci));
            if (f == null) return;
            goxSentMute[ci] = DateTime.Now;
            bool muted = Settings.GetBool("ch." + Channels[ci] + ".mute", false);
            GoxSend("SetFaderMuteState", f, muted ? "MutedToAll" : "Unmuted");
        }

        bool GoxOnMicLevel(int pct)
        {
            if (goxApplying || !GoxLinkOn || !MicIsGoxlr()) return false;
            goxSentMic = DateTime.Now;
            GoxSend("SetMicrophoneGain", gox.MicType, (int)Math.Round(pct * GoxMaxMicGain / 100.0));
            return true;
        }

        bool GoxOnMicMute()
        {
            if (goxApplying || !GoxLinkOn || !MicIsGoxlr()) return false;
            goxSentCough = DateTime.Now;
            bool mutedNow = gox.CoughState != "Unmuted";
            GoxSend("SetCoughMuteState", mutedNow ? "Unmuted" : "MutedToAll");
            gox.CoughState = mutedNow ? "Unmuted" : "MutedToAll"; // show it right away
            return true;
        }

        // ---------- GoXLR -> SoundDeck (UI thread, from the fast timer) ----------

        void TickGox()
        {
            var st = gox;
            UpdateGoxUi(st);
            if (st == null || st.Version == goxSeen || !goxLink.Checked) return;
            goxSeen = st.Version;
            var now = DateTime.Now;
            goxApplying = true;
            try
            {
                for (int ci = 0; ci < 4; ci++)
                {
                    if (!ChannelLinked(ci)) continue;
                    int v;
                    if ((now - goxSentVol[ci]).TotalMilliseconds > 800 && st.Volumes.TryGetValue(GoxChannelFor(ci), out v))
                    {
                        int pct = (int)Math.Round(v * 100 / 255.0);
                        if (Math.Abs(pct - chVol[ci].Value) >= 1) chVol[ci].Value = Clamp(pct, 0, 100);
                    }
                    string f = FaderFor(st, GoxChannelFor(ci));
                    if (f != null && (now - goxSentMute[ci]).TotalMilliseconds > 800)
                    {
                        bool m = st.FaderMute[f] != "Unmuted";
                        if (m != Settings.GetBool("ch." + Channels[ci] + ".mute", false))
                        {
                            Settings.Set("ch." + Channels[ci] + ".mute", m);
                            UpdateMuteButton(ci);
                        }
                    }
                }
                if (MicIsGoxlr())
                {
                    if ((now - goxSentMic).TotalMilliseconds > 800)
                    {
                        int pct = Clamp((int)Math.Round(st.MicGain * 100.0 / GoxMaxMicGain), 0, 100);
                        if (Math.Abs(pct - mcVol.Value) >= 1) mcVol.Value = pct;
                    }
                    if ((now - goxSentCough).TotalMilliseconds > 800) ShowMicMuteLook(st.CoughState != "Unmuted");
                }
                // Fader assignment combos follow the device (unless one is being changed right now).
                for (int i = 0; i < 4; i++)
                {
                    string ch;
                    if (!st.FaderChannel.TryGetValue(GoxFaders[i], out ch)) continue;
                    var cb = goxFaderCombo[i];
                    if (!cb.DroppedDown && !cb.Focused && (string)cb.SelectedItem != ch) cb.SelectedItem = ch;
                }
            }
            finally { goxApplying = false; }
            ApplyVolumes();
        }

        void ShowMicMuteLook(bool m)
        {
            mcMute.Text = m ? "Muted" : "Mute";
            mcMute.BackColor = m ? Color.FromArgb(150, 45, 45) : Card2;
        }

        // ---------- UI (Settings > GoXLR) ----------

        void BuildGoxLinkUi(Panel p)
        {
            int x = 600;
            goxUtilStatus = Lbl(p, "", x, 486, 9.5f, false, Dim);
            goxUtilStatus.MaximumSize = new Size(380, 0);
            goxLink = Chk(p, "Let SoundDeck control the GoXLR", x, 536);
            goxLink.Checked = Settings.GetBool("goxlr.link", true);
            goxLink.CheckedChanged += (s, e) => { Settings.Set("goxlr.link", goxLink.Checked); ApplyMixer(); };

            Lbl(p, "Faders (left to right)", x, 568, 8.5f, false, Dim);
            for (int i = 0; i < 4; i++)
            {
                int fi = i;
                var cb = Combo(p, x + i * 95, 588, 88);
                cb.DropDownWidth = 140;
                cb.Items.AddRange(GoxChannels);
                cb.SelectionChangeCommitted += (s, e) =>
                {
                    var ch = cb.SelectedItem as string;
                    if (ch != null) GoxSend("SetFader", GoxFaders[fi], ch);
                };
                goxFaderCombo[i] = cb;
            }
            goxColors = Btn(p, "Match GoXLR colours to SoundDeck", x, 628, 270, 32);
            goxColors.Click += (s, e) => MatchGoxColours();
            goxInstall = Btn(p, "Add GoXLR control (free)", x, 628, 270, 32);
            goxInstall.BackColor = Accent; goxInstall.ForeColor = Color.White;
            goxInstall.Click += (s, e) => InstallGoxUtility();
            goxStart = Btn(p, "Start GoXLR Utility", x, 628, 270, 32);
            goxStart.Click += (s, e) => { goxAutoTries = 0; goxAutoNext = DateTime.MinValue; if (!GoxUtil.Start()) lastGoxUi = ""; };
            goxAuto = Chk(p, "Start GoXLR Utility with SoundDeck (hidden)", x, 672);
            goxAuto.Checked = Settings.GetBool("goxlr.autostart", true);
            goxAuto.CheckedChanged += (s, e) => Settings.Set("goxlr.autostart", goxAuto.Checked);
            UpdateGoxUi(null);
        }

        Button goxStart;
        CheckBox goxAuto;
        int goxAutoTries;
        DateTime goxAutoNext;

        // Every second: if a GoXLR is plugged in and GoXLR Utility is installed but not running, start it hidden.
        // Gives up after 3 tries per session (so a crashing Utility isn't restarted forever), and never starts it
        // while TC-Helicon's GoXLR App is open.
        void TickGoxAutostart()
        {
            if (goxAuto == null || !goxAuto.Checked || gox != null || goxInstalling || goxAutoTries >= 3 || DateTime.Now < goxAutoNext) return;
            if (!HasGoXlr || GoxUtil.Running || GoxUtil.Dir() == null || GoxUtil.OfficialAppRunning()) return;
            goxAutoTries++;
            goxAutoNext = DateTime.Now.AddSeconds(30);
            GoxUtil.Start();
        }

        static string GoxColourFor(string goxChannel)
        {
            switch (goxChannel)
            {
                case "Game": return "FF6A3D";
                case "Chat": return "3DDC97";
                case "Music": return "4DA3FF";
                case "System": return "B57BFF";
                case "Mic": return "F05A78";
                default: return "9AA0A6";
            }
        }

        void MatchGoxColours()
        {
            var st = gox;
            if (st == null) return;
            for (int i = 0; i < 4; i++)
            {
                string ch;
                if (!st.FaderChannel.TryGetValue(GoxFaders[i], out ch)) continue;
                string c = GoxColourFor(ch);
                GoxSend("SetFaderColours", GoxFaders[i], c, c);
                GoxSend("SetButtonColours", "Fader" + (i + 1) + "Mute", c, null);
            }
        }

        string lastGoxUi = "";
        DateTime goxProbeNext;
        bool[] goxProbe = new bool[3]; // installed, running, TC-Helicon app open
        void UpdateGoxUi(GoxState st)
        {
            if (goxUtilStatus == null) return;
            bool hasDevice = HasGoXlr;
            // This runs every 50 ms: look at installed programs and running processes at most every 2 seconds.
            if (DateTime.Now >= goxProbeNext)
            {
                goxProbeNext = DateTime.Now.AddSeconds(2);
                goxProbe[0] = hasDevice && GoxUtilityInstalled();
                goxProbe[1] = goxProbe[0] && GoxUtil.Running;
                goxProbe[2] = st == null && hasDevice && GoxUtil.OfficialAppRunning();
            }
            bool installed = goxProbe[0], running = goxProbe[1], official = goxProbe[2];
            string key = (st == null ? "none" : st.Serial + st.DeviceType) + hasDevice + goxInstalling + installed + running + official;
            bool isConnected = st != null;
            foreach (Control c in new Control[] { goxLink, goxColors }) c.Visible = isConnected;
            foreach (var cb in goxFaderCombo) cb.Visible = isConnected;
            goxInstall.Visible = !isConnected && !goxInstalling && hasDevice && !installed;
            goxStart.Visible = !isConnected && !goxInstalling && hasDevice && installed && !running && !official;
            goxAuto.Visible = installed && hasDevice;
            if (key == lastGoxUi) return;
            lastGoxUi = key;
            if (isConnected)
            {
                goxUtilStatus.Text = "Connected to your GoXLR" + (st.DeviceType == "Mini" ? " Mini" : "") + ". Channels you set up above are controlled by its faders and mute buttons, both ways.";
                goxUtilStatus.ForeColor = Good;
            }
            else if (goxInstalling) { goxUtilStatus.Text = goxInstallSay; goxUtilStatus.ForeColor = Dim; }
            else if (!hasDevice) goxUtilStatus.Text = "";
            else if (official) { goxUtilStatus.Text = "TC-Helicon's GoXLR App is open. Only one program can control the GoXLR: close it (and turn off its start with Windows) to let SoundDeck control it."; goxUtilStatus.ForeColor = Warn; }
            else if (installed && running) { goxUtilStatus.Text = "Connecting to your GoXLR..."; goxUtilStatus.ForeColor = Dim; }
            else if (installed) { goxUtilStatus.Text = "GoXLR control isn't running."; goxUtilStatus.ForeColor = Warn; }
            else { goxUtilStatus.Text = "To control the GoXLR's faders, mutes, mic and colours from SoundDeck, add GoXLR control (free, open-source GoXLR Utility, 44 MB). Windows asks permission once; after that it runs hidden with SoundDeck."; goxUtilStatus.ForeColor = Dim; }
        }

        static bool GoxUtilityInstalled()
        {
            return GoxUtil.Dir() != null || GoxUtil.Running;
        }

        string goxInstallSay = "";

        // Downloads GoXLR Utility from its GitHub release (checked against GitHub's published SHA-256), installs it silently and starts it.
        void InstallGoxUtility()
        {
            if (goxInstalling) return;
            goxInstalling = true; lastGoxUi = ""; goxInstallSay = "Downloading GoXLR Utility (44 MB)...";
            new Thread(() =>
            {
                string err = null;
                try { GoxUtil.Install(t => { goxInstallSay = t; try { BeginInvoke(new Action(() => lastGoxUi = "")); } catch { } }); }
                catch (System.ComponentModel.Win32Exception) { err = "Windows permission was declined."; }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        goxInstalling = false; lastGoxUi = ""; goxProbeNext = DateTime.MinValue;
                        goxAutoTries = 0;
                        if (err != null) MessageBox.Show(this, "GoXLR control wasn't installed: " + err, "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }
    }
}
