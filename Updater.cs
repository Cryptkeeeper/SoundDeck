// Automatic updates from GitHub Releases.
// Each release has update.txt (what's new + where the zip is + its SHA-256) and update.txt.sig, a signature
// made with the publisher's private key. SoundDeck only trusts update.txt if the signature matches the public
// key below, and only installs a zip whose SHA-256 matches the signed update.txt. update.txt can also move
// the feed somewhere else (feed=...), so the hosting can change later without anyone reinstalling.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace SoundDeck
{
    class UpdateInfo
    {
        public Version Version;
        public string ZipUrl, Sha256, PluginVersion, Feed, Notes;
    }

    static class Updater
    {
        public const string DefaultFeed = "https://github.com/Cryptkeeeper/SoundDeck/releases/latest/download/";
        const string PublicKey = "<RSAKeyValue><Modulus>x/sB0buKmzj7hlAn8i+25MDF7jiE9F7W4gpvWaybnpLRg9Ibgixk49DFNxFfvUnJ5ecApB3ZmQQfQ0Jq34WbT3bCeeH2Y54jaDwVAw1tO3nMGdITr1UTQDtMrKvnjDdT2OYqp16ix/hH/hRhzMB9E7tBtN42W2M7ikgki46l8bqixMZj1R9I1WBAstpGIx/JmAmE6hyo/SzkXJotJ0a71TKI9FQlD46kqZti1IVJdMnIJflhmnZhChlxBcJ7kHP1EHE6x04q/rQCzTDUVF1RzuYqypfOaVrvi39F45MdtrIDvbJqyb6sc6scXHR3GUqz5lhoZqRJCg65JDSbcyzi5qx/NIg5Lc+OBPhSuzF/WvwnT5mQ50k5X7eE6BnXKUa++2jZgpEfWaf0iTHRDsF5J0g2grgERBqMPPs5MmNEIc8CXoFzN6hDmxyz5sYLNoTTLY0suxqkpkYxt5XrmkZY7bRq72aC5J0plY5qqMtSo7lCLXR8d+Faxp9SPMXqLGBV</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public static Version Current { get { return new Version(AppVersion.Value); } }
        static string ExePath { get { return Application.ExecutablePath; } }
        public static string Dir { get { return Path.GetDirectoryName(ExePath); } }

        // The copy in the source folder (next to SoundDeck.cs) is the one being developed, so it never updates itself.
        public static bool IsDevCopy { get { return File.Exists(Path.Combine(Dir, "SoundDeck.cs")); } }

        // Where to look: a test override, then a feed moved by a previous update, then the built-in one.
        public static string Feed
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("SOUNDDECK_FEED");
                if (!string.IsNullOrEmpty(env)) return env;
                return Settings.Get("update.feed", DefaultFeed);
            }
        }

        // After an update the old exe is still closing for a moment, so keep trying for up to 30 seconds.
        public static void CleanupOld()
        {
            string old = ExePath + ".old";
            if (!File.Exists(old)) return;
            new Thread(() =>
            {
                for (int i = 0; i < 30 && File.Exists(old); i++)
                {
                    try { File.Delete(old); } catch { Thread.Sleep(1000); }
                }
            }) { IsBackground = true }.Start();
        }

        static byte[] Fetch(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "SoundDeck/" + AppVersion.Value);
                wc.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                return wc.DownloadData(url);
            }
        }

        static string Join(string feed, string file) { return feed.EndsWith("/") || feed.EndsWith("\\") ? feed + file : feed + "/" + file; }

        // Returns the latest published release (newer or not). Throws if the check fails or the signature is wrong.
        public static UpdateInfo Check()
        {
            string feed = Feed;
            byte[] text = Fetch(Join(feed, "update.txt"));
            byte[] sig = Convert.FromBase64String(Encoding.ASCII.GetString(Fetch(Join(feed, "update.txt.sig"))).Trim());
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(PublicKey);
                if (!rsa.VerifyData(text, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new Exception("the update's signature didn't match, so it was ignored");
            }
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Encoding.UTF8.GetString(text).Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            var info = new UpdateInfo
            {
                Version = new Version(d["version"]),
                ZipUrl = d["zip"].StartsWith("http", StringComparison.OrdinalIgnoreCase) || d["zip"].Contains(":\\") ? d["zip"] : Join(feed, d["zip"]),
                Sha256 = d["sha256"],
                PluginVersion = d.ContainsKey("plugin") ? d["plugin"] : null,
                Feed = d.ContainsKey("feed") ? d["feed"] : null,
                Notes = d.ContainsKey("notes") ? d["notes"].Replace("|", "\n") : ""
            };
            return info;
        }

        // Downloads and checks the update, then swaps in the new files. The caller restarts SoundDeck.
        public static void Install(UpdateInfo info)
        {
            byte[] zip = Fetch(info.ZipUrl);
            string got;
            using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(zip)).Replace("-", "");
            if (!got.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new Exception("the download didn't match the signed update, so it wasn't installed");

            string temp = Path.Combine(Path.GetTempPath(), "SoundDeckUpdate-" + info.Version);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            using (var z = new ZipArchive(new MemoryStream(zip))) z.ExtractToDirectory(temp);
            string newExe = Path.Combine(temp, "SoundDeck.exe");
            if (!File.Exists(newExe)) throw new Exception("the update has no SoundDeck.exe");

            // A running exe can be renamed but not overwritten, so move it aside and put the new one in its place.
            string old = ExePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(ExePath, old);
            try { File.Copy(newExe, ExePath); }
            catch { File.Move(old, ExePath); throw; }

            // Any other files in the update (besides the plugin) go next to SoundDeck.exe.
            foreach (var f in Directory.GetFiles(temp))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("SoundDeck.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("plugin.zip", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Copy(f, Path.Combine(Dir, name), true); } catch { }
            }

            string pluginZip = Path.Combine(temp, "plugin.zip");
            if (File.Exists(pluginZip)) UpdatePlugin(pluginZip, info.PluginVersion);
            try { Directory.Delete(temp, true); } catch { }
        }

        // Replaces the Stream Deck plugin only if it's installed for this copy of SoundDeck and its version changed.
        static void UpdatePlugin(string zip, string version)
        {
            string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Elgato\StreamDeck\Plugins\com.sounddeck.controls.sdPlugin");
            string pathFile = Path.Combine(dest, "sounddeck_path.txt");
            if (!File.Exists(pathFile)) return;
            if (!string.Equals(File.ReadAllText(pathFile).Trim(), ExePath, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string manifest = File.ReadAllText(Path.Combine(dest, "manifest.json"));
                if (version != null && manifest.Contains("\"Version\": \"" + version + "\"")) return;
            }
            catch { }

            string sdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Elgato\StreamDeck\StreamDeck.exe");
            bool wasRunning = Process.GetProcessesByName("StreamDeck").Length > 0;
            foreach (var p in Process.GetProcessesByName("StreamDeck")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            Thread.Sleep(1500);
            string keepPath = File.ReadAllText(pathFile);
            Directory.Delete(dest, true);
            using (var z = ZipFile.OpenRead(zip)) z.ExtractToDirectory(dest);
            File.WriteAllText(pathFile, keepPath);
            if (wasRunning && File.Exists(sdExe)) Process.Start(sdExe);
        }
    }

    partial class MainForm
    {
        Label updVersion, updStatus;
        Button updCheck, updNow;
        CheckBox updAuto;
        UpdateInfo pendingUpdate;
        Timer updTimer;
        bool updBusy, autoInstallUpdate;

        void BuildUpdates(Panel p)
        {
            int x = 600;
            Lbl(p, "Updates", x - 3, 96, 12f, true, Fg);
            updVersion = Lbl(p, "You have SoundDeck " + AppVersion.Value, x, 126, 9.5f, false, Dim);
            updStatus = Lbl(p, "", x, 150, 9.5f, false, Dim);
            updStatus.MaximumSize = new Size(380, 0);
            updCheck = Btn(p, "Check for updates", x, 216, 170, 32);
            updCheck.Click += (s, e) => CheckForUpdates(true);
            updNow = Btn(p, "Update now", x + 180, 216, 140, 32);
            updNow.BackColor = Accent; updNow.ForeColor = Color.White;
            updNow.Visible = false;
            updNow.Click += (s, e) => InstallUpdate();
            updAuto = Chk(p, "Check automatically", x, 258);
            updAuto.Checked = Settings.GetBool("update.auto", true);
            updAuto.CheckedChanged += (s, e) => Settings.Set("update.auto", updAuto.Checked);

            if (Updater.IsDevCopy)
            {
                updStatus.Text = "This is your development copy (it sits next to the source code), so it doesn't update itself. Copies installed with SoundDeckSetup do.";
                updCheck.Enabled = false; updAuto.Enabled = false;
                if (Environment.GetEnvironmentVariable("SOUNDDECK_EXTRAS") != null) // testing aid: show the missing-parts window
                {
                    var once = new Timer { Interval = 3000 };
                    once.Tick += (s, e) => { once.Stop(); CheckExtras(false); };
                    once.Start();
                }
                return;
            }

            // First check 30 seconds after starting, then once a day.
            updTimer = new Timer { Interval = 30000 };
            updTimer.Tick += (s, e) =>
            {
                updTimer.Interval = 24 * 3600 * 1000;
                if (updAuto.Checked) CheckForUpdates(false);
            };
            updTimer.Start();
            tray.BalloonTipClicked += (s, e) => { if (pendingUpdate != null) { ShowWindow(); ShowPage("Settings"); } };
        }

        void CheckForUpdates(bool userAsked)
        {
            if (updBusy) return;
            updBusy = true;
            updCheck.Enabled = false;
            if (userAsked) { updStatus.Text = "Checking..."; updStatus.ForeColor = Dim; }
            var t = new Thread(() =>
            {
                UpdateInfo info = null; string err = null;
                try { info = Updater.Check(); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    err = r != null && r.StatusCode == HttpStatusCode.NotFound ? "No updates have been published yet." : "Couldn't reach the update server (" + ex.Message + ").";
                }
                catch (Exception ex) { err = "Update check failed: " + ex.Message; }
                try { BeginInvoke(new Action(() => CheckDone(info, err, userAsked))); } catch { }
            }) { IsBackground = true };
            t.Start();
        }

        void CheckDone(UpdateInfo info, string err, bool userAsked)
        {
            updBusy = false;
            updCheck.Enabled = true;
            if (err != null)
            {
                if (userAsked) { updStatus.Text = err; updStatus.ForeColor = Accent; }
                return;
            }
            // A signed release can move the feed (e.g. to a new host); remember it even if there's nothing new.
            if (!string.IsNullOrEmpty(info.Feed) && Environment.GetEnvironmentVariable("SOUNDDECK_FEED") == null) Settings.Set("update.feed", info.Feed);
            if (info.Version <= Updater.Current) info = null;
            if (info == null)
            {
                pendingUpdate = null; updNow.Visible = false;
                updStatus.Text = "You're up to date (checked " + DateTime.Now.ToString("h:mm tt") + ").";
                updStatus.ForeColor = Good;
                CheckExtras(userAsked); // SoundDeck itself is current; now offer any missing add-ons
                return;
            }
            pendingUpdate = info;
            if (autoInstallUpdate) { autoInstallUpdate = false; InstallUpdate(); return; }
            updNow.Visible = true;
            updStatus.Text = "SoundDeck " + info.Version + " is available." + (info.Notes != "" ? "\n" + info.Notes : "");
            updStatus.ForeColor = Fg;
            if (!userAsked) tray.ShowBalloonTip(6000, "SoundDeck " + info.Version + " is available", "Click here, then \"Update now\". Takes a few seconds.", ToolTipIcon.Info);
        }

        void InstallUpdate()
        {
            var info = pendingUpdate;
            if (info == null || updBusy) return;
            updBusy = true;
            updNow.Enabled = false; updCheck.Enabled = false;
            updStatus.Text = "Downloading SoundDeck " + info.Version + "..."; updStatus.ForeColor = Dim;
            bool wasVisible = Visible;
            var t = new Thread(() =>
            {
                string err = null;
                try { Updater.Install(info); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        updBusy = false;
                        if (err != null)
                        {
                            updStatus.Text = "Update failed: " + err; updStatus.ForeColor = Accent;
                            updNow.Enabled = true; updCheck.Enabled = true;
                            return;
                        }
                        Settings.Save();
                        Process.Start(new ProcessStartInfo(Application.ExecutablePath, wasVisible ? "--after-update" : "--after-update --autostart") { UseShellExecute = true });
                        ExitApp();
                    }));
                }
                catch { }
            }) { IsBackground = true };
            t.Start();
        }
    }
}
