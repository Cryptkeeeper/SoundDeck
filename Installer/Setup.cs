// SoundDeck installer. One exe that:
//  - installs SoundDeck for the current user (no admin needed) into %LOCALAPPDATA%\Programs\SoundDeck
//  - downloads RNNoise (noise removal) from its official GitHub release
//  - downloads VB-CABLE (virtual mic) from vb-audio.com and opens its installer, if no virtual mic is present
//  - installs the Stream Deck plugin if the Stream Deck app is installed
//  - adds shortcuts, start-with-Windows, and an entry in Settings > Apps for uninstalling
// Downloads are pinned to exact versions and checked by SHA-256, so a changed file is refused.
//
// Command line: /uninstall   |   /dir:<folder> /quiet /no-vbcable /no-streamdeck /no-startup /no-shortcuts /no-launch (for testing)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SoundDeck Setup")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace SoundDeckSetup
{
    static class Program
    {
        // Quiet (test) runs have no window, so they write to %TEMP%\SoundDeckSetup.log.
        public static void Log(string s)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "SoundDeckSetup.log"), DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            var opts = new Options(args);
            if (opts.Uninstall) { Uninstaller.Run(opts); return; }
            if (opts.Quiet)
            {
                var inst = new Installer(opts, (p, s) => Program.Log(s));
                try { inst.Install(); Program.Log("OK"); }
                catch (Exception ex) { Program.Log("FAILED: " + ex.Message); Environment.ExitCode = 1; }
                return;
            }
            Application.Run(new SetupForm(opts));
        }
    }

    class Options
    {
        public string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SoundDeck");
        public bool Uninstall, Quiet, NoLaunch;
        public bool Noise = true, Apo = true, GoxUtil = true, VbCable = true, StreamDeck = true, Desktop = true, Startup = true, StartMenu = true;

        public Options(string[] args)
        {
            foreach (var a in args)
            {
                string l = a.ToLowerInvariant();
                if (l == "/uninstall") Uninstall = true;
                else if (l == "/quiet") Quiet = true;
                else if (l == "/no-vbcable") VbCable = false;
                else if (l == "/no-streamdeck") StreamDeck = false;
                else if (l == "/no-startup") Startup = false;
                else if (l == "/no-shortcuts") { Desktop = false; StartMenu = false; }
                else if (l == "/no-noise") Noise = false;
                else if (l == "/no-apo") Apo = false;
                else if (l == "/no-goxlr") GoxUtil = false;
                else if (l == "/no-launch") NoLaunch = true;
                else if (l.StartsWith("/dir:")) Dir = a.Substring(5).Trim('"');
            }
        }
    }

    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(18, 20, 24), Card = Color.FromArgb(28, 31, 37), Card2 = Color.FromArgb(42, 46, 54);
        public static readonly Color Fg = Color.FromArgb(232, 234, 237), Dim = Color.FromArgb(154, 160, 166), Accent = Color.FromArgb(255, 106, 61), Good = Color.FromArgb(61, 220, 151);

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Dark(Form f)
        {
            f.BackColor = Bg; f.ForeColor = Fg; f.Font = new Font("Segoe UI", 9.5f);
            f.HandleCreated += (s, e) =>
            {
                try
                {
                    int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4);
                    int cap = Card.R | (Card.G << 8) | (Card.B << 16); DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);
                }
                catch { }
            };
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }

        public static Label Lbl(Control p, string text, int x, int y, float size, bool bold, Color color)
        {
            var l = new Label { Text = text, Left = x, Top = y, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            p.Controls.Add(l);
            return l;
        }

        public static Button Btn(Control p, string text, int x, int y, int w, int h, bool primary)
        {
            var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = h, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Card2, ForeColor = primary ? Color.White : Fg, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular) };
            b.FlatAppearance.BorderSize = 0;
            p.Controls.Add(b);
            return b;
        }

        public static CheckBox Chk(Control p, string text, string note, int x, int y)
        {
            var c = new CheckBox { Text = text, Left = x, Top = y, AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            p.Controls.Add(c);
            if (note != null) { var l = Lbl(p, note, x + 20, y + 24, 8.5f, false, Dim); l.MaximumSize = new Size(470, 0); c.Tag = l; }
            return c;
        }
    }

    // ---------------- What's already on this PC ----------------

    static class Detect
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator { void EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices); }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection { void GetCount(out int n); void Item(int i, out IMMDevice d); }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice { void Activate_(); void OpenPropertyStore(int access, out IPropertyStore store); }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore { void GetCount(out int n); void GetAt_(); void GetValue(ref PropertyKey key, out PropVariant v); }

        [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2; }

        // Names of active input devices (mics), or an empty list if Windows audio can't be read.
        public static List<string> Mics()
        {
            var list = new List<string>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDeviceCollection col; en.EnumAudioEndpoints(1, 1, out col);
                int n; col.GetCount(out n);
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d; col.Item(i, out d);
                    IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                    PropVariant v; ps.GetValue(ref key, out v);
                    if (v.vt == 31) list.Add(Marshal.PtrToStringUni(v.p1));
                }
            }
            catch { }
            return list;
        }

        public static string VirtualMic()
        {
            return Mics().FirstOrDefault(m => m.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? Mics().FirstOrDefault(m => m.IndexOf("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string ApoDir()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
            return File.Exists(Path.Combine(p, "EqualizerAPO.dll")) ? p : null;
        }

        public static bool GoxUtilityInstalled()
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                try { if (Directory.Exists(root) && Directory.GetDirectories(root, "GoXLR Utility*").Length > 0) return true; } catch { }
            }
            return Process.GetProcessesByName("goxlr-daemon").Length > 0;
        }

        public static string StreamDeckExe()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Elgato\StreamDeck\StreamDeck.exe");
            return File.Exists(p) ? p : null;
        }
    }

    // ---------------- Install ----------------

    class Installer
    {
        const string RnNoiseUrl = "https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip";
        const string RnNoiseSha = "E4DCC186102850F8DCEBEB766AD1BF34C24F6280BD614EF84B72DAE4783FC517";
        // Equalizer APO is unsigned; this hash matches the one Chocolatey publishes for the same file.
        const string ApoUrl = "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe";
        const string ApoSha = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA";
        // GoXLR Utility (open source); hash is the SHA-256 GitHub publishes for this release file.
        const string GoxUrl = "https://github.com/GoXLR-on-Linux/goxlr-utility/releases/download/v1.2.4/goxlr-utility-1.2.4.exe";
        const string GoxSha = "0A8743D27EA5D1741956AECFC3AE26C0F7399945CF29D2BB6B3DAB459E86AC2F";
        const string VbCableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
        const string VbCableSha = "BC44E8974191A5A2427D6C979E2DAB08DBB09925F3F435DFA2F0BA3E45B6EB90";
        public const string PluginId = "com.sounddeck.controls.sdPlugin";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundDeck";

        readonly Options o;
        readonly Action<int, string> report;
        public bool VbCableStarted, NeedsRestart, ApoInstalled;
        public List<string> Warnings = new List<string>();

        // Every step also goes to %TEMP%\SoundDeckSetup.log, so a friend can send it if setup goes wrong.
        public Installer(Options o, Action<int, string> report)
        {
            this.o = o;
            this.report = (p, s) => { if (!o.Quiet) Program.Log(s); report(p, s); };
            Program.Log("---- SoundDeck setup " + DateTime.Now + " into " + o.Dir);
        }

        public string Exe { get { return Path.Combine(o.Dir, "SoundDeck.exe"); } }

        public void Install()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            report(2, "Closing SoundDeck if it's running...");
            foreach (var p in Process.GetProcessesByName("SoundDeck")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

            report(5, "Copying SoundDeck...");
            Directory.CreateDirectory(o.Dir);
            Extract("SoundDeck.exe", Exe);
            Extract("SoundDeck.ico", Path.Combine(o.Dir, "SoundDeck.ico"));
            string self = Application.ExecutablePath;
            string setupCopy = Path.Combine(o.Dir, "SoundDeckSetup.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase)) File.Copy(self, setupCopy, true);

            if (o.Noise)
            {
                string dll = Path.Combine(o.Dir, "rnnoise_mono.dll");
                try
                {
                    var zip = Download(RnNoiseUrl, RnNoiseSha, "noise removal (RNNoise)", 10, 70);
                    report(70, "Unpacking noise removal...");
                    using (var z = new ZipArchive(new MemoryStream(zip)))
                    {
                        var entry = z.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("vst/rnnoise_mono.dll", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new Exception("rnnoise_mono.dll wasn't in the download");
                        entry.ExtractToFile(dll, true);
                        var lic = z.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase));
                        if (lic != null) lic.ExtractToFile(Path.Combine(o.Dir, "RNNoise-LICENSE.txt"), true);
                    }
                    File.WriteAllText(Path.Combine(o.Dir, "RNNoise-README.txt"),
                        "Noise removal uses rnnoise_mono.dll from the open-source project\r\nhttps://github.com/werman/noise-suppression-for-voice (release v1.10).\r\n" +
                        "It was downloaded from that project's GitHub release during setup. Source code is available there.\r\n");
                }
                catch (Exception ex) { Warnings.Add("Noise removal wasn't installed: " + ex.Message); }
            }

            if (o.Apo && Detect.ApoDir() == null)
            {
                try
                {
                    var exe = Download(ApoUrl, ApoSha, "equalizer (Equalizer APO)", 62, 70);
                    string tmp = Path.Combine(Path.GetTempPath(), "EqualizerAPO-x64-1.4.2.exe");
                    File.WriteAllBytes(tmp, exe);
                    report(71, "Installing Equalizer APO. Click Yes when Windows asks...");
                    var p = Process.Start(new ProcessStartInfo(tmp, "/S") { Verb = "runas", UseShellExecute = true });
                    p.WaitForExit();
                    try { File.Delete(tmp); } catch { }
                    if (Detect.ApoDir() == null) throw new Exception("its setup didn't finish");
                    ApoInstalled = true; NeedsRestart = true;
                    // Next, pick which devices get the EQ. Setup usually opens that window itself; open it if not.
                    Thread.Sleep(2000);
                    if (Process.GetProcessesByName("DeviceSelector").Length == 0 && Process.GetProcessesByName("Configurator").Length == 0)
                    {
                        string sel = Path.Combine(Detect.ApoDir(), "DeviceSelector.exe");
                        if (!File.Exists(sel)) sel = Path.Combine(Detect.ApoDir(), "Configurator.exe");
                        if (File.Exists(sel)) try { Process.Start(new ProcessStartInfo(sel) { Verb = "runas", UseShellExecute = true }); } catch { }
                    }
                }
                catch (System.ComponentModel.Win32Exception) { Warnings.Add("Equalizer APO wasn't installed because Windows permission was declined. Run SoundDeckSetup.exe again anytime."); }
                catch (Exception ex) { Warnings.Add("Equalizer APO wasn't installed: " + ex.Message); }
            }

            bool goxPresent = Detect.Mics().Any(m => m.IndexOf("GoXLR", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("GO XLR", StringComparison.OrdinalIgnoreCase) >= 0);
            if (o.GoxUtil && goxPresent && !Detect.GoxUtilityInstalled())
            {
                try
                {
                    var exe = Download(GoxUrl, GoxSha, "GoXLR Utility", 72, 80);
                    string tmp = Path.Combine(Path.GetTempPath(), "goxlr-utility-1.2.4.exe");
                    File.WriteAllBytes(tmp, exe);
                    report(80, "Opening the GoXLR Utility installer. Follow its steps...");
                    Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true }).WaitForExit();
                    try { File.Delete(tmp); } catch { }
                }
                catch (System.ComponentModel.Win32Exception) { Warnings.Add("GoXLR Utility wasn't installed because Windows permission was declined."); }
                catch (Exception ex) { Warnings.Add("GoXLR Utility wasn't installed: " + ex.Message); }
            }

            if (o.VbCable && Detect.VirtualMic() == null)
            {
                try
                {
                    var zip = Download(VbCableUrl, VbCableSha, "virtual mic (VB-CABLE)", 72, 82);
                    string dir = Path.Combine(o.Dir, "VBCABLE");
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    using (var z = new ZipArchive(new MemoryStream(zip))) z.ExtractToDirectory(dir);
                    report(83, "Opening the VB-CABLE installer. Click Yes, then \"Install Driver\"...");
                    var psi = new ProcessStartInfo(Path.Combine(dir, "VBCABLE_Setup_x64.exe")) { Verb = "runas", UseShellExecute = true, WorkingDirectory = dir };
                    var p = Process.Start(psi);
                    VbCableStarted = true;
                    p.WaitForExit();
                    NeedsRestart = true;
                }
                catch (System.ComponentModel.Win32Exception) { Warnings.Add("VB-CABLE wasn't installed because Windows permission was declined. You can run SoundDeckSetup.exe again anytime."); }
                catch (Exception ex) { Warnings.Add("VB-CABLE wasn't installed: " + ex.Message); }
            }

            string sd = Detect.StreamDeckExe();
            if (o.StreamDeck && sd != null)
            {
                report(88, "Adding the Stream Deck plugin...");
                try { InstallPlugin(sd); }
                catch (Exception ex) { Warnings.Add("Stream Deck plugin wasn't installed: " + ex.Message); }
            }

            // The last steps are extras: if one fails, note it and keep going instead of stopping setup.
            report(93, "Adding shortcuts...");
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SoundDeck.lnk");
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SoundDeck.lnk");
            try { if (o.StartMenu) Shortcut(startMenu, Exe, "Sound mixer, mic mix and soundboard"); }
            catch (Exception ex) { Warnings.Add("Start menu shortcut wasn't added: " + ex.Message); }
            try { if (o.Desktop) Shortcut(desktop, Exe, "Sound mixer, mic mix and soundboard"); }
            catch (Exception ex) { Warnings.Add("Desktop shortcut wasn't added: " + ex.Message); }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (o.Startup) k.SetValue("SoundDeck", "\"" + Exe + "\" --autostart");
                    else if ((k.GetValue("SoundDeck") as string ?? "").IndexOf(o.Dir, StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue("SoundDeck", false);
                }
            }
            catch (Exception ex) { Warnings.Add("Start with Windows wasn't set: " + ex.Message); }

            report(97, "Adding SoundDeck to Settings > Apps...");
            try { WriteUninstallEntry(setupCopy); }
            catch (Exception ex) { Warnings.Add("SoundDeck wasn't added to Settings > Apps: " + ex.Message); }
            report(100, "Done.");
        }

        void WriteUninstallEntry(string setupCopy)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "SoundDeck");
                k.SetValue("DisplayVersion", "1.0");
                k.SetValue("Publisher", "SoundDeck");
                k.SetValue("DisplayIcon", Path.Combine(o.Dir, "SoundDeck.ico"));
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("UninstallString", "\"" + setupCopy + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("VBCableByUs", VbCableStarted ? 1 : 0, RegistryValueKind.DWord);
                if (ApoInstalled) k.SetValue("ApoByUs", 1, RegistryValueKind.DWord);
                long kb = new DirectoryInfo(o.Dir).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
            }
        }

        void InstallPlugin(string streamDeckExe)
        {
            string plugins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Elgato\StreamDeck\Plugins");
            string dest = Path.Combine(plugins, PluginId);
            foreach (var p in Process.GetProcessesByName("StreamDeck")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            Thread.Sleep(1500);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(plugins);
            using (var z = new ZipArchive(Resource("plugin.zip"))) z.ExtractToDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "sounddeck_path.txt"), Exe);
            // Start it in the background (like at Windows startup) so its window doesn't cover setup.
            Process.Start(new ProcessStartInfo(streamDeckExe, "--runinbk") { UseShellExecute = true });
        }

        byte[] Download(string url, string sha, string what, int from, int to)
        {
            report(from, "Downloading " + what + "...");
            byte[] data = null;
            Exception err = null;
            using (var done = new ManualResetEvent(false))
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "SoundDeck-Setup");
                wc.DownloadProgressChanged += (s, e) => report(from + (to - from) * e.ProgressPercentage / 100, "Downloading " + what + "... " + (e.BytesReceived / 1048576) + " MB");
                wc.DownloadDataCompleted += (s, e) => { if (e.Error != null) err = e.Error; else data = e.Result; done.Set(); };
                wc.DownloadDataAsync(new Uri(url));
                done.WaitOne();
            }
            if (err != null) throw new Exception("couldn't download it (" + err.Message + ")");
            string got;
            using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "");
            if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new Exception("the downloaded file didn't match the expected one, so it wasn't used");
            return data;
        }

        static Stream Resource(string name)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) throw new Exception(name + " is missing from the installer");
            return s;
        }

        static void Extract(string name, string path)
        {
            using (var s = Resource(name))
            using (var f = File.Create(path)) s.CopyTo(f);
        }

        public static void Shortcut(string lnk, string target, string description)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            Marshal.ReleaseComObject(sc); Marshal.ReleaseComObject(shell);
        }
    }

    // ---------------- Window ----------------

    class SetupForm : Form
    {
        readonly Options o;
        CheckBox noise, apo, gx, vb, sd, desk, start;
        Button go, cancel;
        ProgressBar bar;
        Label status, title, sub;
        Panel choices;

        public SetupForm(Options o)
        {
            this.o = o;
            Text = "SoundDeck Setup";
            ClientSize = new Size(580, 678);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Ui.Dark(this);

            title = Ui.Lbl(this, "Install SoundDeck", 28, 22, 18f, true, Ui.Fg);
            sub = Ui.Lbl(this, "A lightweight sound mixer: per-app volume and outputs, music in your mic, soundboard, noise removal. No ads, no account.", 30, 62, 9.5f, false, Ui.Dim);
            sub.MaximumSize = new Size(520, 0);

            choices = new Panel { Left = 28, Top = 112, Width = 524, Height = 458, BackColor = Ui.Card };
            Controls.Add(choices);

            noise = Ui.Chk(choices, "Noise removal", "Downloads RNNoise (36 MB) from its official GitHub page. Removes fans, keyboard and background noise from your mic.", 16, 14);
            noise.Checked = true;

            bool hasApo = Detect.ApoDir() != null;
            apo = Ui.Chk(choices, "Equalizer (Equalizer APO)", hasApo
                ? "Not needed: Equalizer APO is already installed."
                : "Needed for the Equalizer and mic voice processing. Downloads 11 MB from SourceForge. Windows asks permission, then a window lists your devices: tick your headphones/headset and your mic, then OK. Restart after.", 16, 76);
            apo.Checked = !hasApo; apo.Enabled = !hasApo;

            string vm = Detect.VirtualMic();
            vb = Ui.Chk(choices, "Virtual mic (VB-CABLE)", vm != null
                ? "Not needed: you already have \"" + vm + "\"."
                : "Needed to send music, sounds and the cleaned-up mic to Discord and games. Downloads 1 MB from vb-audio.com. Windows asks permission, then click \"Install Driver\". Restart after.", 16, 172);
            vb.Checked = vm == null; vb.Enabled = vm == null;

            bool hasGox = Detect.Mics().Any(m => m.IndexOf("GoXLR", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("GO XLR", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasUtil = Detect.GoxUtilityInstalled();
            gx = Ui.Chk(choices, "GoXLR control (GoXLR Utility)", !hasGox ? "No GoXLR found, so this is skipped."
                : hasUtil ? "Not needed: GoXLR Utility is already installed."
                : "Lets SoundDeck control the GoXLR's faders, mutes, mic and colours. Downloads the free, open-source GoXLR Utility (44 MB). Close TC-Helicon's GoXLR App first; only one can run.", 16, 256);
            gx.Checked = hasGox && !hasUtil; gx.Enabled = hasGox && !hasUtil;

            bool hasSd = Detect.StreamDeckExe() != null;
            sd = Ui.Chk(choices, "Stream Deck plugin", hasSd ? "Dials for each channel plus output, sound and profile keys. The Stream Deck app restarts once." : "Stream Deck app not found, so this is skipped.", 16, 318);
            sd.Checked = hasSd; sd.Enabled = hasSd;

            desk = Ui.Chk(choices, "Desktop shortcut", null, 16, 380); desk.Checked = true;
            start = Ui.Chk(choices, "Start SoundDeck with Windows (hidden in the tray)", null, 16, 414); start.Checked = true;

            bar = new ProgressBar { Left = 28, Top = 584, Width = 524, Height = 8, Visible = false };
            Controls.Add(bar);
            status = Ui.Lbl(this, "Installs to " + o.Dir, 28, 600, 8.5f, false, Ui.Dim);
            status.MaximumSize = new Size(524, 0);

            go = Ui.Btn(this, "Install", 412, 628, 140, 36, true);
            cancel = Ui.Btn(this, "Cancel", 262, 628, 140, 36, false);
            go.Click += (s, e) => Start();
            cancel.Click += (s, e) => Close();
        }

        void Start()
        {
            o.Noise = noise.Checked; o.Apo = apo.Checked; o.GoxUtil = gx.Checked; o.VbCable = vb.Checked; o.StreamDeck = sd.Checked; o.Desktop = desk.Checked; o.Startup = start.Checked;
            choices.Enabled = false; go.Enabled = false; cancel.Enabled = false;
            bar.Visible = true;
            title.Text = "Installing...";
            var inst = new Installer(o, (p, s) => BeginInvoke(new Action(() => { bar.Value = Math.Max(0, Math.Min(100, p)); status.Text = s; status.ForeColor = Ui.Dim; })));
            var t = new Thread(() =>
            {
                Exception err = null;
                try { inst.Install(); } catch (Exception ex) { err = ex; }
                BeginInvoke(new Action(() => Finish(inst, err)));
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // shortcuts use a COM object that needs STA
            t.Start();
        }

        void Finish(Installer inst, Exception err)
        {
            cancel.Enabled = true; cancel.Text = "Close";
            // Come to the front (Stream Deck or an installer window may be on top), and show the result
            // in the big area where the choices were, so nothing is hidden behind the buttons.
            choices.Visible = false; bar.Visible = false;
            status.Top = 120; status.Font = new Font("Segoe UI", 10.5f);
            WindowState = FormWindowState.Normal; TopMost = true; Activate(); TopMost = false;
            if (err != null)
            {
                Program.Log("FAILED: " + err);
                title.Text = "Setup didn't finish";
                status.Text = err.Message + "\n\nA log was saved to " + Path.Combine(Path.GetTempPath(), "SoundDeckSetup.log") + ". Send it to whoever gave you SoundDeck.";
                status.ForeColor = Ui.Accent;
                return;
            }
            foreach (var w in inst.Warnings) Program.Log("warning: " + w);
            // Open SoundDeck right away; the button below opens it again if it was closed.
            try { if (!o.NoLaunch) Process.Start(inst.Exe); } catch { }
            title.Text = "SoundDeck is installed";
            var lines = new List<string>();
            if (inst.ApoInstalled) lines.Add("In the Equalizer APO window, tick your headphones/headset and your mic, then click OK.");
            if (inst.NeedsRestart) lines.Add("Restart your PC to finish setting up " +
                (inst.ApoInstalled && inst.VbCableStarted ? "the equalizer and virtual mic." : inst.ApoInstalled ? "the equalizer." : "the virtual mic."));
            lines.Add("In Discord and your games, set the mic to " + (Detect.VirtualMic() ?? (inst.NeedsRestart ? "\"CABLE Output (VB-Audio Virtual Cable)\" after restarting" : "your SoundDeck virtual mic")) + ", and turn on Mic Mix in SoundDeck.");
            lines.Insert(0, "SoundDeck is open now. Look for its orange icon near the clock.");
            lines.AddRange(inst.Warnings);
            status.Text = string.Join("\n\n", lines);
            status.ForeColor = inst.Warnings.Count > 0 ? Ui.Accent : Ui.Good;

            // Swap the Install button for an Open button.
            var old = go;
            go = Ui.Btn(this, "Open SoundDeck", old.Left, old.Top, old.Width, old.Height, true);
            go.Click += (s, e) => { try { if (Process.GetProcessesByName("SoundDeck").Length == 0) Process.Start(inst.Exe); } catch { } Close(); };
            Controls.Remove(old); old.Dispose();
        }
    }

    // ---------------- Uninstall ----------------

    static class Uninstaller
    {
        static bool Inside(string pathOrCommand, string dir)
        {
            if (string.IsNullOrEmpty(pathOrCommand)) return false;
            string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return pathOrCommand.Replace("\"", "").Trim().StartsWith(full, StringComparison.OrdinalIgnoreCase);
        }

        static string ShortcutTarget(string lnk)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
        }

        public static void Run(Options o)
        {
            string dir = null; bool vbByUs = false, apoByUs = false;
            using (var k = Registry.CurrentUser.OpenSubKey(Installer.UninstallKey))
            {
                if (k != null) { dir = k.GetValue("InstallLocation") as string; vbByUs = Convert.ToInt32(k.GetValue("VBCableByUs", 0)) == 1; apoByUs = Convert.ToInt32(k.GetValue("ApoByUs", 0)) == 1; }
            }
            if (string.IsNullOrEmpty(dir)) dir = o.Dir;
            if (!o.Quiet && MessageBox.Show("Remove SoundDeck from this PC?\n\nYour SoundDeck settings are kept in case you reinstall.", "SoundDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            foreach (var p in Process.GetProcessesByName("SoundDeck")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

            string vbSetup = Path.Combine(dir, @"VBCABLE\VBCABLE_Setup_x64.exe");
            if (!o.Quiet && vbByUs && File.Exists(vbSetup) &&
                MessageBox.Show("Also remove the VB-CABLE virtual mic that SoundDeck setup installed?\n\nIf yes: click Yes when Windows asks, then click \"Remove Driver\".", "SoundDeck", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(vbSetup) { Verb = "runas", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(vbSetup) }).WaitForExit(); } catch { }
            }

            string apoUninstall = Detect.ApoDir() == null ? null : Path.Combine(Detect.ApoDir(), "Uninstall.exe");
            if (!o.Quiet && apoByUs && apoUninstall != null && File.Exists(apoUninstall) &&
                MessageBox.Show("Also remove Equalizer APO, which SoundDeck setup installed?\n\nIf yes: click Yes when Windows asks, then follow its uninstaller. A restart finishes it.", "SoundDeck", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(apoUninstall) { Verb = "runas", UseShellExecute = true }).WaitForExit(); } catch { }
            }

            // Only remove things that point at this copy of SoundDeck.
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    if (k != null && Inside(k.GetValue("SoundDeck") as string, dir)) k.DeleteValue("SoundDeck", false);
            }
            catch { }
            foreach (var lnk in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SoundDeck.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SoundDeck.lnk") })
                try { if (File.Exists(lnk) && Inside(ShortcutTarget(lnk), dir)) File.Delete(lnk); } catch { }

            string plugin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Elgato\StreamDeck\Plugins\" + Installer.PluginId);
            string pathFile = Path.Combine(plugin, "sounddeck_path.txt");
            if (Directory.Exists(plugin) && File.Exists(pathFile) && Inside(File.ReadAllText(pathFile), dir))
            {
                string sd = Detect.StreamDeckExe();
                bool wasRunning = Process.GetProcessesByName("StreamDeck").Length > 0;
                foreach (var p in Process.GetProcessesByName("StreamDeck")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                Thread.Sleep(1000);
                try { Directory.Delete(plugin, true); } catch { }
                if (wasRunning && sd != null) try { Process.Start(new ProcessStartInfo(sd, "--runinbk") { UseShellExecute = true }); } catch { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(Installer.UninstallKey, false); } catch { }

            if (!o.Quiet) MessageBox.Show("SoundDeck was removed.", "SoundDeck");
            if (o.Quiet) Program.Log("Removed " + dir);
            // This exe lives in the folder being removed, so a short-lived cmd deletes it after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
    }
}
