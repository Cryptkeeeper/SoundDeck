// Missing extras: after each update check, SoundDeck looks for the free add-ons it uses
// (noise removal, Equalizer APO, VB-CABLE, GoXLR Utility) and offers to install whatever is missing.
// Every download comes from the project's official site and is checked against a fixed SHA-256.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SoundDeck
{
    class Extra
    {
        public string Key, Name, Why;
        public bool NeedsRestart;
        public Func<bool> Missing;
        public Action<Action<string>> Install; // runs on a background thread; the callback shows progress
    }

    partial class MainForm
    {
        bool extrasOpen, extrasHooked;
        List<Extra> extrasPending;

        List<Extra> AllExtras()
        {
            return new List<Extra>
            {
                new Extra {
                    Key = "noise", Name = "Noise removal (RNNoise)",
                    Why = "Removes fans, keyboard and background noise from your mic. 36 MB from its official GitHub page.",
                    Missing = () => !RnNoise.Available, Install = InstallRnNoise },
                new Extra {
                    Key = "apo", Name = "Equalizer (Equalizer APO)", NeedsRestart = true,
                    Why = "Needed for the Equalizer and voice processing. 11 MB from SourceForge. Windows asks permission, then tick your headset and mic.",
                    Missing = () => !EqApo.Installed, Install = InstallApoNow },
                new Extra {
                    Key = "vbcable", Name = "Virtual mic (VB-CABLE)", NeedsRestart = true,
                    Why = "Needed for Mic Mix, the soundboard in Discord, voice effects and the bleep. 1 MB from vb-audio.com. Windows asks permission, then click Install Driver.",
                    Missing = () => FindCable() == null, Install = InstallVbCable },
                new Extra {
                    Key = "goxutil", Name = "GoXLR control (GoXLR Utility)",
                    Why = "Lets SoundDeck control your GoXLR's faders, mutes and mic (free GoXLR Utility, 44 MB). Windows asks permission once, then it runs hidden. Close TC-Helicon's GoXLR App first.",
                    Missing = () => HasGoXlr && !GoxUtilityInstalled(), Install = InstallGoxNow },
            };
        }

        // Called after every update check. userAsked: the "Check for updates" button, which always shows what's missing.
        void CheckExtras(bool userAsked)
        {
            if (extrasOpen) return;
            bool test = Environment.GetEnvironmentVariable("SOUNDDECK_EXTRAS") != null; // testing aid: list everything
            var missing = AllExtras().Where(x => test || x.Missing()).ToList();
            if (!userAsked && !test)
            {
                missing = missing.Where(x => !Settings.GetBool("extras.skip." + x.Key, false)).ToList();
                DateTime later;
                if (DateTime.TryParse(Settings.Get("extras.later", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out later) && DateTime.Now < later) return;
            }
            if (missing.Count == 0) return;

            if (Visible && WindowState != FormWindowState.Minimized) { ShowExtras(missing); return; }
            extrasPending = missing;
            if (!extrasHooked)
            {
                extrasHooked = true;
                tray.BalloonTipClicked += (s, e) =>
                {
                    var list = extrasPending;
                    if (list == null || pendingUpdate != null) return;
                    ShowWindow();
                    ShowExtras(list);
                };
            }
            tray.ShowBalloonTip(8000, "SoundDeck is missing " + (missing.Count == 1 ? "1 part" : missing.Count + " parts"),
                "Click here to install " + string.Join(", ", missing.Select(x => x.Name.Split(' ')[0].ToLowerInvariant())) + ".", ToolTipIcon.Info);
        }

        void ShowExtras(List<Extra> missing)
        {
            extrasPending = null;
            extrasOpen = true;
            var f = new Form
            {
                Text = "Set up missing parts", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, BackColor = Bg, ForeColor = Fg,
                Font = new Font("Segoe UI", 9.5f), ClientSize = new Size(560, 140 + missing.Count * 74)
            };
            f.HandleCreated += (s, e) => { try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } };
            Lbl(f, "SoundDeck is missing some free parts", 20, 16, 13f, true, Fg);
            var intro = Lbl(f, "Untick anything you don't want; SoundDeck won't ask about it again.", 22, 48, 9f, false, Dim);
            intro.MaximumSize = new Size(520, 0);

            var boxes = new List<CheckBox>();
            int y = 84;
            foreach (var x in missing)
            {
                var c = Chk(f, x.Name, 22, y);
                c.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
                c.Checked = true;
                c.Tag = x;
                boxes.Add(c);
                var why = Lbl(f, x.Why, 46, y + 26, 8.5f, false, Dim);
                why.MaximumSize = new Size(490, 0);
                y += 74;
            }

            var status = Lbl(f, "", 22, y + 6, 9f, false, Dim);
            status.MaximumSize = new Size(330, 0);
            var later = Btn(f, "Not now", 456, y + 2, 84, 32);
            var go = Btn(f, "Install", 350, y + 2, 96, 32);
            go.BackColor = Accent; go.ForeColor = Color.White;
            f.AcceptButton = go;

            bool done = false, running = false;
            later.Click += (s, e) =>
            {
                if (!done) Settings.Set("extras.later", DateTime.Now.AddDays(7).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                f.Close();
            };
            f.FormClosing += (s, e) => { if (running) e.Cancel = true; };
            go.Click += (s, e) =>
            {
                if (done) { f.Close(); return; }
                var pick = boxes.Where(b => b.Checked).Select(b => (Extra)b.Tag).ToList();
                foreach (var b in boxes) if (!b.Checked) Settings.Set("extras.skip." + ((Extra)b.Tag).Key, true);
                if (pick.Count == 0) { f.Close(); return; }
                running = true;
                go.Enabled = false; later.Enabled = false;
                foreach (var b in boxes) b.Enabled = false;
                var problems = new List<string>();
                bool restart = false;
                new Thread(() =>
                {
                    foreach (var x in pick)
                    {
                        var item = x;
                        Action<string> say = t => { try { f.BeginInvoke(new Action(() => { status.Text = t; status.ForeColor = Dim; })); } catch { } };
                        try
                        {
                            say("Downloading " + item.Name + "...");
                            item.Install(say);
                            if (item.NeedsRestart) restart = true;
                        }
                        catch (System.ComponentModel.Win32Exception) { problems.Add(item.Name + ": Windows permission was declined."); }
                        catch (Exception ex) { problems.Add(item.Name + ": " + ex.Message); }
                    }
                    try
                    {
                        f.BeginInvoke(new Action(() =>
                        {
                            running = false; done = true;
                            RefreshDevices(true);
                            ApplyNoiseRemoval();
                            UpdateApoStatus();
                            status.ForeColor = problems.Count == 0 ? Good : Accent;
                            status.Text = problems.Count > 0 ? "Some parts weren't installed:\n" + string.Join("\n", problems)
                                : restart ? "Done. Restart your PC to finish setting up." : "Done.";
                            go.Text = "Close"; go.Enabled = true;
                            later.Visible = false;
                        }));
                    }
                    catch { }
                }) { IsBackground = true }.Start();
            };

            Look.Apply(f);
            try { f.ShowDialog(this); }
            finally { extrasOpen = false; f.Dispose(); }
        }

        // ---------- Installers (run on a background thread) ----------

        static byte[] DownloadChecked(string url, string sha)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            byte[] data;
            using (var wc = new WebClient()) { wc.Headers.Add("User-Agent", "SoundDeck/" + AppVersion.Value); data = wc.DownloadData(url); }
            string got;
            using (var h = System.Security.Cryptography.SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "");
            if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new Exception("the download didn't match the expected file, so it wasn't used");
            return data;
        }

        static void InstallRnNoise(Action<string> say)
        {
            var zip = DownloadChecked("https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip",
                "E4DCC186102850F8DCEBEB766AD1BF34C24F6280BD614EF84B72DAE4783FC517");
            say("Unpacking noise removal...");
            string dir = Path.GetDirectoryName(RnNoise.DllPath);
            using (var z = new ZipArchive(new MemoryStream(zip)))
            {
                var entry = z.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("vst/rnnoise_mono.dll", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new Exception("rnnoise_mono.dll wasn't in the download");
                entry.ExtractToFile(RnNoise.DllPath, true);
                var lic = z.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase));
                if (lic != null) lic.ExtractToFile(Path.Combine(dir, "RNNoise-LICENSE.txt"), true);
            }
            File.WriteAllText(Path.Combine(dir, "RNNoise-README.txt"),
                "Noise removal uses rnnoise_mono.dll from the open-source project\r\nhttps://github.com/werman/noise-suppression-for-voice (release v1.10).\r\n" +
                "It was downloaded from that project's GitHub release by SoundDeck. Source code is available there.\r\n");
        }

        static void InstallApoNow(Action<string> say)
        {
            var exe = DownloadChecked("https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe",
                "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA");
            string tmp = Path.Combine(Path.GetTempPath(), "EqualizerAPO-x64-1.4.2.exe");
            File.WriteAllBytes(tmp, exe);
            say("Installing Equalizer APO. Click Yes when Windows asks...");
            Process.Start(new ProcessStartInfo(tmp, "/S") { Verb = "runas", UseShellExecute = true }).WaitForExit();
            try { File.Delete(tmp); } catch { }
            if (!EqApo.Installed) throw new Exception("its setup didn't finish");
            MarkInstalledByUs("ApoByUs");
            // Then pick which devices get the EQ. Its setup usually opens that window; open it if not.
            Thread.Sleep(2000);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
            if (Process.GetProcessesByName("DeviceSelector").Length == 0 && Process.GetProcessesByName("Configurator").Length == 0)
            {
                string sel = Path.Combine(dir, File.Exists(Path.Combine(dir, "DeviceSelector.exe")) ? "DeviceSelector.exe" : "Configurator.exe");
                if (File.Exists(sel)) try { Process.Start(new ProcessStartInfo(sel) { Verb = "runas", UseShellExecute = true }); } catch { }
            }
            say("In the Equalizer APO window, tick your headset and mic, then click OK.");
        }

        static void InstallVbCable(Action<string> say)
        {
            var zip = DownloadChecked("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
                "BC44E8974191A5A2427D6C979E2DAB08DBB09925F3F435DFA2F0BA3E45B6EB90");
            string dir = Path.Combine(Updater.Dir, "VBCABLE");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            using (var z = new ZipArchive(new MemoryStream(zip))) z.ExtractToDirectory(dir);
            say("Opening the VB-CABLE installer. Click Yes, then \"Install Driver\"...");
            var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "VBCABLE_Setup_x64.exe")) { Verb = "runas", UseShellExecute = true, WorkingDirectory = dir });
            MarkInstalledByUs("VBCableByUs"); // so uninstalling SoundDeck offers to remove it again
            p.WaitForExit();
        }

        static void InstallGoxNow(Action<string> say)
        {
            GoxUtil.Install(say);
        }

        // The installer's Settings > Apps entry remembers what SoundDeck installed, for a clean uninstall.
        static void MarkInstalledByUs(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundDeck", true))
                    if (k != null) k.SetValue(name, 1, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
