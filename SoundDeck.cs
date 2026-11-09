// SoundDeck - a lightweight, ad-free audio mixer for Windows.
// Channel mixer, per-app output, EQ + mic cleanup (via Equalizer APO).
// Build with build.bat (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SoundDeck
{
    // ================= Windows Core Audio interop =================

    enum EDataFlow { Render = 0, Capture = 1 }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid fmtid; public int pid;
        public PropertyKey(Guid g, int p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort vt; public ushort r1, r2, r3; public IntPtr p1; public IntPtr p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr p);
        void UnregisterControlChangeNotify(IntPtr p);
        void GetChannelCount(out int count);
        void SetMasterVolumeLevel(float db, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel_(); void SetChannelVolumeLevelScalar_(); void GetChannelVolumeLevel_(); void GetChannelVolumeLevelScalar_();
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
        void GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        void GetState(out int state);
        void GetDisplayName_();
        void SetDisplayName_();
        void GetIconPath_();
        void SetIconPath_();
        void GetGroupingParam_();
        void SetGroupingParam_();
        void RegisterAudioSessionNotification_();
        void UnregisterAudioSessionNotification_();
        void GetSessionIdentifier_();
        void GetSessionInstanceIdentifier_();
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid ctx);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Undocumented but long-stable interface used to change the default device.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat_(); void GetDeviceFormat_(); void ResetDeviceFormat_(); void SetDeviceFormat_();
        void GetProcessingPeriod_(); void SetProcessingPeriod_(); void GetShareMode_(); void SetShareMode_();
        void GetPropertyValue_(); void SetPropertyValue_();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    class AudioDevice
    {
        public string Id; public string Name;
        public AudioDevice(string id, string name) { Id = id; Name = name; }
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    class AppSession
    {
        public uint Pid; public string Exe; public string Pretty; public ISimpleAudioVolume Volume; public IAudioMeterInformation Meter;

        public float Peak()
        {
            float p = 0;
            try { if (Meter != null) Meter.GetPeakValue(out p); } catch { }
            return p;
        }
    }

    static class Audio
    {
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);
        [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int len, out IntPtr hstring);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

        public static Guid Ctx = new Guid("6a1f3c52-8b0e-4d8e-9a31-5d2c7e4b9f10");
        static Guid IID_EndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        static Guid IID_SessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        static PropertyKey PKEY_FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        const int CLSCTX_ALL = 23;

        static IMMDeviceEnumerator en;
        static IMMDeviceEnumerator E
        {
            get { if (en == null) en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); return en; }
        }

        public static List<AudioDevice> GetDevices(EDataFlow flow)
        {
            var list = new List<AudioDevice>();
            try
            {
                IMMDeviceCollection col;
                E.EnumAudioEndpoints((int)flow, 1, out col);
                int n; col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d; col.Item(i, out d);
                    string id; d.GetId(out id);
                    list.Add(new AudioDevice(id, GetName(d)));
                }
            }
            catch { }
            // Testing aid: SOUNDDECK_FAKE_GOXLR=1 pretends a GoXLR is plugged in (outputs only).
            if (flow == EDataFlow.Render && Environment.GetEnvironmentVariable("SOUNDDECK_FAKE_GOXLR") == "1")
                foreach (var ch in new[] { "System", "Game", "Chat", "Music", "Sample" })
                    list.Add(new AudioDevice("{fake-goxlr}." + ch, ch + " (TC-Helicon GoXLR)"));
            return list;
        }

        static string GetName(IMMDevice d)
        {
            try
            {
                IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                PropertyKey k = PKEY_FriendlyName; PropVariant v;
                ps.GetValue(ref k, out v);
                string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p1) : null;
                PropVariantClear(ref v);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            return "Unknown device";
        }

        public static string GetDefaultId(EDataFlow flow)
        {
            try { IMMDevice d; E.GetDefaultAudioEndpoint((int)flow, 0, out d); string id; d.GetId(out id); return id; }
            catch { return null; }
        }

        public static void SetDefault(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigClient();
                for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
            }
            catch { }
        }

        static IAudioEndpointVolume EndpointVolume(string id)
        {
            IMMDevice d; E.GetDevice(id, out d);
            Guid g = IID_EndpointVolume; object o;
            d.Activate(ref g, CLSCTX_ALL, IntPtr.Zero, out o);
            return (IAudioEndpointVolume)o;
        }

        public static float GetVolume(string id)
        {
            try { float v; EndpointVolume(id).GetMasterVolumeLevelScalar(out v); return v; }
            catch { return -1; }
        }

        public static void SetVolume(string id, float v)
        {
            try { EndpointVolume(id).SetMasterVolumeLevelScalar(v, ref Ctx); } catch { }
        }

        public static bool GetMute(string id)
        {
            try { bool m; EndpointVolume(id).GetMute(out m); return m; }
            catch { return false; }
        }

        public static void SetMute(string id, bool m)
        {
            try { EndpointVolume(id).SetMute(m, ref Ctx); } catch { }
        }

        static Dictionary<uint, string> exeCache = new Dictionary<uint, string>();
        static Dictionary<string, string> prettyCache = new Dictionary<string, string>();

        static string ExeName(uint pid, out string pretty)
        {
            pretty = null;
            string exe;
            if (!exeCache.TryGetValue(pid, out exe))
            {
                if (exeCache.Count > 500) exeCache.Clear();
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    exe = p.ProcessName.ToLowerInvariant();
                    if (!prettyCache.ContainsKey(exe))
                    {
                        string desc = null;
                        try { desc = p.MainModule.FileVersionInfo.FileDescription; } catch { }
                        if (string.IsNullOrWhiteSpace(desc) || desc.Length > 40)
                            desc = char.ToUpper(p.ProcessName[0]) + p.ProcessName.Substring(1);
                        prettyCache[exe] = desc.Trim();
                    }
                }
                catch { exe = null; }
                exeCache[pid] = exe;
            }
            if (exe != null) prettyCache.TryGetValue(exe, out pretty);
            return exe;
        }

        public static List<AppSession> GetSessions()
        {
            var result = new List<AppSession>();
            uint self = (uint)Process.GetCurrentProcess().Id;
            foreach (var dev in GetDevices(EDataFlow.Render))
            {
                try
                {
                    IMMDevice d; E.GetDevice(dev.Id, out d);
                    Guid g = IID_SessionManager2; object o;
                    d.Activate(ref g, CLSCTX_ALL, IntPtr.Zero, out o);
                    IAudioSessionEnumerator se;
                    ((IAudioSessionManager2)o).GetSessionEnumerator(out se);
                    int n; se.GetCount(out n);
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            IAudioSessionControl2 c; se.GetSession(i, out c);
                            int state; c.GetState(out state);
                            if (state == 2) continue;                    // expired
                            if (c.IsSystemSoundsSession() == 0) continue;
                            uint pid; c.GetProcessId(out pid);
                            if (pid == 0 || pid == self) continue;
                            string pretty;
                            string exe = ExeName(pid, out pretty);
                            if (exe == null || exe == "audiodg") continue;
                            result.Add(new AppSession { Pid = pid, Exe = exe, Pretty = pretty ?? exe, Volume = (ISimpleAudioVolume)c, Meter = c as IAudioMeterInformation });
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return result;
        }

        // ----- Per-app output device (same mechanism as Windows' "App volume and device preferences") -----

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetPersistedFn(IntPtr self, uint pid, int flow, int role, IntPtr deviceId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ClearAllFn(IntPtr self);

        static IntPtr policyFactory = IntPtr.Zero;
        static bool policyTried;

        static IntPtr PolicyFactory()
        {
            if (policyTried) return policyFactory;
            policyTried = true;
            string cls = "Windows.Media.Internal.AudioPolicyConfig";
            IntPtr h;
            if (WindowsCreateString(cls, cls.Length, out h) != 0) return IntPtr.Zero;
            try
            {
                // Windows 11 / 21H2+ interface first, then the older Windows 10 one.
                foreach (string iid in new[] { "ab3d4648-e242-459f-b02f-541c70306324", "2a59116d-6c4f-45e0-a74f-707e3fef9258" })
                {
                    Guid g = new Guid(iid); IntPtr f;
                    if (RoGetActivationFactory(h, ref g, out f) == 0 && f != IntPtr.Zero) { policyFactory = f; break; }
                }
            }
            catch { }
            finally { WindowsDeleteString(h); }
            return policyFactory;
        }

        static Delegate Slot(IntPtr obj, int index, Type t)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, index * IntPtr.Size), t);
        }

        // vtable: IUnknown(3) + IInspectable(3) + 19 other methods, then SetPersisted (25), GetPersisted (26), ClearAll (27)
        public static bool SetAppOutput(uint pid, string deviceId)
        {
            IntPtr f = PolicyFactory();
            if (f == IntPtr.Zero) return false;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    string full = @"\\?\SWD#MMDEVAPI#" + deviceId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
                    WindowsCreateString(full, full.Length, out h);
                }
                var fn = (SetPersistedFn)Slot(f, 25, typeof(SetPersistedFn));
                int hr = fn(f, pid, 0, 0, h);
                fn(f, pid, 0, 1, h);
                return hr == 0;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
        }

        public static void ClearAppOutputs()
        {
            IntPtr f = PolicyFactory();
            if (f == IntPtr.Zero) return;
            try { ((ClearAllFn)Slot(f, 27, typeof(ClearAllFn)))(f); } catch { }
        }

        public static string GuidOf(string deviceId)
        {
            int i = deviceId.LastIndexOf('{');
            return i >= 0 ? deviceId.Substring(i) : deviceId;
        }
    }

    // ================= Equalizer APO config =================

    static class EqApo
    {
        public static string ConfigDir()
        {
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                {
                    if (k != null)
                    {
                        var p = k.GetValue("ConfigPath") as string;
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                    }
                }
            }
            catch { }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"EqualizerAPO\config");
            return Directory.Exists(def) ? def : null;
        }

        public static bool Installed { get { return ConfigDir() != null; } }

        // Returns null on success, "denied" when the folder isn't writable, or an error message.
        public static string Write(string body)
        {
            string dir = ConfigDir();
            if (dir == null) return "Equalizer APO is not installed.";
            try
            {
                File.WriteAllText(Path.Combine(dir, "SoundDeck.txt"), body);
                string main = Path.Combine(dir, "config.txt");
                string cur = File.Exists(main) ? File.ReadAllText(main) : "";
                if (cur.IndexOf("Include: SoundDeck.txt", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string backup = Path.Combine(dir, "config.before-SoundDeck.txt");
                    if (cur.Length > 0 && !File.Exists(backup)) File.WriteAllText(backup, cur);
                    File.WriteAllText(main, "# Managed by SoundDeck. Your old config was saved as config.before-SoundDeck.txt\r\nInclude: SoundDeck.txt\r\n");
                }
                return null;
            }
            catch (UnauthorizedAccessException) { return "denied"; }
            catch (Exception ex) { return ex.Message; }
        }

        public static void FixPermissions()
        {
            string dir = ConfigDir();
            if (dir == null) return;
            try
            {
                var psi = new ProcessStartInfo("icacls.exe", "\"" + dir + "\" /grant *S-1-5-32-545:(OI)(CI)M /T")
                {
                    Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi).WaitForExit();
            }
            catch { }
        }
    }

    // ================= Settings =================

    static class Settings
    {
        static Dictionary<string, string> d = new Dictionary<string, string>();
        static string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundDeck", "settings.txt");
        static bool dirty;
        public static bool Existed; // false on the very first run (fresh install)

        public static void Load()
        {
            try
            {
                string file = path;
                if (!File.Exists(file)) file = path + ".bak"; // the last save was cut off: use the copy before it
                if (!File.Exists(file)) return;
                Existed = true;
                foreach (var line in File.ReadAllLines(file))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Write a new file, then swap it in, so a crash or power cut mid-save can't wipe your settings.
                string tmp = path + ".new";
                File.WriteAllLines(tmp, d.Select(kv => kv.Key + "=" + kv.Value).ToArray());
                if (!File.Exists(path)) File.Move(tmp, path);
                else
                {
                    try { File.Replace(tmp, path, path + ".bak", true); }
                    catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); } // e.g. a drive that can't swap files
                }
                dirty = false;
            }
            catch { }
        }

        public static void SaveIfDirty() { if (dirty) Save(); }

        public static string Get(string k, string def) { string v; return d.TryGetValue(k, out v) ? v : def; }
        public static int GetInt(string k, int def) { int v; return int.TryParse(Get(k, null), out v) ? v : def; }
        public static bool GetBool(string k, bool def) { string v = Get(k, null); return v == null ? def : v == "1"; }
        public static void Set(string k, string v) { string cur; if (d.TryGetValue(k, out cur) && cur == v) return; d[k] = v; dirty = true; }
        public static void Set(string k, bool v) { Set(k, v ? "1" : "0"); }
        public static void Set(string k, int v) { Set(k, v.ToString(CultureInfo.InvariantCulture)); }
        public static void Remove(string k) { if (d.Remove(k)) dirty = true; }
        public static List<string> KeysStartingWith(string prefix) { return d.Keys.Where(k => k.StartsWith(prefix)).ToList(); }
    }

    class DeviceItem
    {
        public string Id; public string Name;
        public DeviceItem(string id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    // ================= UI =================

    partial class MainForm : Form
    {
        static readonly Color Bg = Color.FromArgb(18, 20, 24);
        static readonly Color Card = Color.FromArgb(28, 31, 37);
        static readonly Color Card2 = Color.FromArgb(42, 46, 54);
        static readonly Color Fg = Color.FromArgb(232, 234, 237);
        static readonly Color Dim = Color.FromArgb(154, 160, 166);
        static readonly Color Accent = Color.FromArgb(255, 106, 61);
        static readonly Color Good = Color.FromArgb(61, 220, 151);
        static readonly string[] Channels = { "Game", "Chat", "Media", "Aux" };
        static readonly Color[] ChannelColors = {
            Color.FromArgb(255, 106, 61), Color.FromArgb(61, 220, 151), Color.FromArgb(77, 163, 255), Color.FromArgb(181, 123, 255) };
        static readonly int[] Freqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        static readonly string[] PresetNames = { "Flat", "Bass Boost", "Footsteps (FPS)", "Chat Clarity", "Music", "Movies", "Treble Boost", "Custom" };
        static readonly int[][] Presets = {
            new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            new[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
            new[] { -4, -3, -2, -1, 0, 1, 3, 5, 4, 1 },
            new[] { -3, -2, -1, 0, 1, 2, 3, 3, 1, 0 },
            new[] { 4, 3, 1, 0, -1, -1, 0, 2, 3, 3 },
            new[] { 5, 4, 2, 0, 0, 1, 2, 1, 1, 0 },
            new[] { 0, 0, 0, 0, 0, 1, 2, 4, 5, 5 } };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        Panel content;
        Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        Dictionary<string, Button> navButtons = new Dictionary<string, Button>();
        Timer tick, apoDebounce;
        int tickCount;
        bool loading, reallyExit, startHidden, askedPermission, toldAboutTray;

        List<AudioDevice> renderDevs = new List<AudioDevice>();
        List<AudioDevice> captureDevs = new List<AudioDevice>();
        string deviceKey = "";

        // Mixer
        ComboBox[] chDevice = new ComboBox[4];
        TrackBar[] chVol = new Slider[4];
        Label[] chPct = new Label[4];
        Button[] chMute = new Button[4];
        string appsKey = null;
        List<AppSession> lastSessions = new List<AppSession>();
        Dictionary<uint, string> routed = new Dictionary<uint, string>();

        // EQ
        ComboBox eqDevice, eqPreset;
        CheckBox eqOn;
        TrackBar[] eqBands = new Slider[10];
        Label[] eqVals = new Label[10];
        Label eqStatus, micStatus;

        // Mic
        ComboBox micDevice;
        TrackBar micLevel, micGain;
        Label micLevelLbl, micGainLbl;
        CheckBox micOn, micHp, micClarity, micHarsh, micNs;
        TextBox micNsPath;

        // Mic Mix
        MicMixEngine mixEngine = new MicMixEngine();
        CheckBox mixOn, mixBeep;
        Button mixToggle;
        TextBox mixHotkeyBox;
        ComboBox mixSource;
        Label mixAppsLbl;
        TrackBar mixMediaVol, mixMicVol;
        Label mixMediaLbl, mixMicLbl, mixCableStatus, mixMicName, mixEngineStatus, mixHotkeyStatus;
        LinkLabel mixCableLink;
        Label steps;
        int mixLastStart = -100;
        bool hotkeyOk;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int HotkeyId = 1;

        public MainForm(bool hidden)
        {
            startHidden = hidden;
            Text = "SoundDeck";
            ClientSize = new Size(1372, 700);
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Fg;
            Font = new Font("Segoe UI", 9.5f);
            Icon = MakeIcon();

            // Older versions turned on Ctrl+Alt+M (music) and Ctrl+Alt+H (hear yourself) by default. People who
            // already had SoundDeck keep them; new installs start with no hotkeys, so nothing clashes with other apps.
            if (Settings.Existed)
            {
                if (Settings.Get("mix.vk", null) == null) { Settings.Set("mix.vk", (int)Keys.M); Settings.Set("mix.mods", 3); }
                if (Settings.Get("side.vk", null) == null) { Settings.Set("side.vk", (int)Keys.H); Settings.Set("side.mods", 3); }
            }

            BuildShell();
            BuildMixer();
            BuildEq();
            BuildMic();
            BuildMicMix();
            BuildStreaming();
            BuildStreamMonitor();
            BuildSoundboard();
            BuildEffects();
            BuildDucking();
            BuildProfiles();
            BuildSettings();
            BuildTray();
            BuildHearYourself();
            BuildPushToTalk();
            BuildDynamicsUi();
            BuildHeadphoneEq();
            BuildUpdates(pages["Settings"]);
            BuildGoXlr(pages["Settings"]);
            BuildGoxLinkUi(pages["Settings"]);
            StartGoxPolling();
            BuildTesting(); // TEMPORARY: see Testing.cs

            RefreshDevices(true);
            LoadEqDevice();
            LoadMicDevice();
            UpdateApoStatus();
            // Testing aid: SOUNDDECK_PAGE=<page name> opens that page instead of the Mixer.
            string startPage = Environment.GetEnvironmentVariable("SOUNDDECK_PAGE");
            ShowPage(startPage != null && pages.ContainsKey(startPage) ? startPage : "Mixer");

            tick = new Timer { Interval = 1000 };
            tick.Tick += (s, e) => OnTick();
            tick.Start();
            apoDebounce = new Timer { Interval = 350 };
            apoDebounce.Tick += (s, e) => { apoDebounce.Stop(); WriteApo(); };
            StartControlServer();
            OnTick();
            fast = new Timer { Interval = 50 };
            fast.Tick += (s, e) => OnFastTick();
            fast.Start();
            Look.Apply(this);
            InitScaling();
            CheckWhatsNew();
            if (StreamerOn)
            {
                // Streamer mode adds a column, so start wider (but never bigger than the screen).
                var wa = Screen.FromControl(this).WorkingArea;
                ClientSize = new Size(Math.Min(BaseW, wa.Width - 40), Math.Min(700, wa.Height - 60));
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing && Settings.GetBool("closeToTray", true))
            {
                e.Cancel = true;
                Hide();
                if (!toldAboutTray)
                {
                    toldAboutTray = true;
                    tray.ShowBalloonTip(3000, "SoundDeck is still running", "Your mixer keeps working. Click the tray icon to open it again, or right-click > Close SoundDeck to quit.", ToolTipIcon.Info);
                }
                return;
            }
            Settings.Save();
            mixEngine.Stop();
            sideEngine.Stop();
            boardLocal.Stop();
            micMeter.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            reallyExit = true;
            Close();
            Application.Exit();
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // ---------- Shell ----------

        void BuildShell()
        {
            var nav = new Panel { Dock = DockStyle.Left, Width = 180, BackColor = Card };
            navPanel = nav;
            Look.NavPanel = nav;
            content = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            Controls.Add(nav);
            Controls.Add(content);
            content.BringToFront();

            var logo = new PictureBox { Left = 18, Top = 22, Width = 28, Height = 28, SizeMode = PictureBoxSizeMode.StretchImage, Image = Icon.ToBitmap() };
            nav.Controls.Add(logo);
            Lbl(nav, "SoundDeck", 52, 23, 13f, true, Fg);

            string[] names = { "Mixer", "Equalizer", "Microphone", "Mic Mix", "Streaming", "Soundboard", "Effects", "Ducking", "Profiles", "Settings", "Testing" };
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                var b = new Button
                {
                    Text = "   " + name, Left = 10, Top = 80 + i * 46, Width = 160, Height = 40,
                    FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Fg, BackColor = Card, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 10.5f)
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Card2;
                b.Click += (s, e) => ShowPage(name);
                nav.Controls.Add(b);
                navButtons[name] = b;
            }
            var ver = Lbl(nav, "No ads. No account.\nNo bloat.", 18, 0, 8.5f, false, Dim);
            ver.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            ver.Top = nav.Height - 50;
        }

        Panel NewPage(string name, string title, string subtitle)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Visible = false };
            content.Controls.Add(p);
            pages[name] = p;
            Lbl(p, title, 22, 16, 18f, true, Fg);
            var sub = Lbl(p, subtitle, 25, 52, 9.5f, false, Dim);
            sub.MaximumSize = new Size(820, 0);
            return p;
        }

        void ShowPage(string name)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == name;
            foreach (var kv in navButtons)
            {
                bool on = kv.Key == name;
                kv.Value.BackColor = on ? Card2 : Card;
                kv.Value.ForeColor = on ? Accent : Fg;
            }
            if (name == "Microphone") LoadMicLevel();
            if (name == "Equalizer" || name == "Microphone") UpdateApoStatus();
        }

        // ---------- Mixer ----------

        void BuildMixer()
        {
            var p = NewPage("Mixer", "Mixer", "Drag apps between channels, or onto Mic to play them in your mic. Right-click an app for more options.");
            for (int i = 0; i < 4; i++)
            {
                int ci = i;
                var card = new Panel { Left = 24 + i * 192, Top = 86, Width = 180, Height = 590, BackColor = Card };
                p.Controls.Add(card);
                card.Controls.Add(new Panel { Left = 0, Top = 0, Width = 180, Height = 4, BackColor = ChannelColors[i] });
                Lbl(card, Channels[i], 12, 14, 14f, true, ChannelColors[i]);
                Lbl(card, "Output", 14, 48, 8.5f, false, Dim);

                var cb = Combo(card, 12, 66, 156);
                cb.SelectedIndexChanged += (s, e) =>
                {
                    if (loading) return;
                    var it = chDevice[ci].SelectedItem as DeviceItem;
                    Settings.Set("ch." + Channels[ci] + ".dev", it == null || it.Id == null ? "" : it.Id);
                    routed.Clear();
                    ApplyMixer();
                    PublishOutput(ci);
                };
                chDevice[i] = cb;

                var tb = new Slider
                {
                    Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
                    Left = 68, Top = 100, Height = 120, BackColor = Card, SmallChange = 2, LargeChange = 10,
                    Value = Math.Max(0, Math.Min(100, Settings.GetInt("ch." + Channels[i] + ".vol", 100)))
                };
                card.Controls.Add(tb);
                chVol[i] = tb;

                chMeter[i] = new LevelMeter(ChannelColors[i]) { Left = 122, Top = 106, Width = 10, Height = 108 };
                card.Controls.Add(chMeter[i]);

                var pct = Lbl(card, tb.Value + "%", 0, 222, 12f, true, Fg);
                pct.AutoSize = false; pct.Width = 180; pct.TextAlign = ContentAlignment.MiddleCenter; pct.Height = 24;
                chPct[i] = pct;
                tb.ValueChanged += (s, e) =>
                {
                    chPct[ci].Text = chVol[ci].Value + "%";
                    Settings.Set("ch." + Channels[ci] + ".vol", chVol[ci].Value);
                    GoxOnChannelVolume(ci);
                    ApplyMixer();
                    PublishChannel(ci);
                };

                var mute = Btn(card, "Mute", 12, 250, 156, 28);
                mute.Click += (s, e) => ToggleMute(ci);
                chMute[i] = mute;
                UpdateMuteButton(i);
                BuildStreamFader(card, ci);

                Lbl(card, "Apps", 14, 292, 8.5f, false, Dim);
                var zone = NewZone(card, 312, 270);
                zones[Channels[i]] = zone;
                MakeDropTarget(card, Channels[i], zone);
            }

            // Fifth column: apps SoundDeck should never touch.
            BuildMicColumn(p, 24 + 4 * 192);
            BuildStreamColumn(p, 24 + 5 * 192);
            var leave = new Panel { Left = 24 + 5 * 192, Top = 86, Width = 180, Height = 590, BackColor = Card };
            leaveCard = leave;
            p.Controls.Add(leave);
            leave.Controls.Add(new Panel { Left = 0, Top = 0, Width = 180, Height = 4, BackColor = Dim });
            Lbl(leave, "Leave alone", 12, 14, 14f, true, Dim);
            var leaveNote = Lbl(leave, "Apps here keep their own volume and output.", 14, 48, 8.5f, false, Dim);
            leaveNote.MaximumSize = new Size(156, 0);
            var leaveZone = NewZone(leave, 112, 470);
            zones["None"] = leaveZone;
            MakeDropTarget(leave, "None", leaveZone);
        }

        const string DragPrefix = "sounddeck-app:";
        static readonly Color ZoneBg = Color.FromArgb(22, 24, 29);
        static readonly Color ZoneHot = Color.FromArgb(52, 58, 70);
        Dictionary<string, FlowLayoutPanel> zones = new Dictionary<string, FlowLayoutPanel>();
        ToolTip tips = new ToolTip();

        FlowLayoutPanel NewZone(Control card, int top, int height)
        {
            var z = new FlowLayoutPanel
            {
                Left = 8, Top = top, Width = 164, Height = height, BackColor = ZoneBg, AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 2, 0, 4)
            };
            card.Controls.Add(z);
            return z;
        }

        void MakeDropTarget(Control c, string channel, FlowLayoutPanel zone)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                var data = e.Data.GetData(typeof(string)) as string;
                if (data == null || !data.StartsWith(DragPrefix)) return;
                e.Effect = DragDropEffects.Move;
                zone.BackColor = ZoneHot;
            };
            c.DragLeave += (s, e) => zone.BackColor = ZoneBg;
            c.DragDrop += (s, e) =>
            {
                zone.BackColor = ZoneBg;
                var data = e.Data.GetData(typeof(string)) as string;
                // Deferred: MoveApp rebuilds the chips, and the dragged chip is still inside DoDragDrop here.
                if (data != null && data.StartsWith(DragPrefix))
                    BeginInvoke(new Action(() => MoveApp(data.Substring(DragPrefix.Length), channel)));
            };
            foreach (Control child in c.Controls) MakeDropTarget(child, channel, zone);
        }

        void MoveApp(string exe, string channel)
        {
            Settings.Set("app." + exe, channel);
            routed.Clear();
            appsKey = null;
            UpdateAppsList();
            ApplyMixer();
        }

        Label MakeChip(AppSession a, string channel)
        {
            string exe = a.Exe;
            int ci = Array.IndexOf(Channels, channel);
            Color stripe = ci >= 0 ? ChannelColors[ci] : Dim;
            var chip = new Label
            {
                Text = a.Pretty, Width = 140, Height = 32, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Card2, ForeColor = Fg, Padding = new Padding(10, 0, 4, 0), Margin = new Padding(4, 4, 0, 0), Cursor = Cursors.SizeAll
            };
            bool inMic = InMic(exe), live = StreamerOn && StreamGainFor(exe) > 0;
            if (inMic || live) chip.Padding = new Padding(10, 0, (inMic ? 30 : 0) + (live ? 32 : 0) + 4, 0);
            chip.Paint += (s, e) =>
            {
                // Drawn as a rounded pill with a coloured dot instead of a square block with a side bar.
                var g = e.Graphics;
                g.Clear(Look.BackOf(chip.Parent));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Look.FillRound(g, chip.BackColor, new RectangleF(0, 0, chip.Width, chip.Height), Look.R(7));
                float d = Math.Max(6, chip.Height / 4.5f);
                using (var b = new SolidBrush(stripe)) g.FillEllipse(b, chip.Height * 0.32f - d / 2, (chip.Height - d) / 2f, d, d);
                var tr = new Rectangle(chip.Padding.Left + (int)(chip.Height * 0.25f), 0, chip.Width - chip.Padding.Left - (int)(chip.Height * 0.25f) - chip.Padding.Right, chip.Height);
                TextRenderer.DrawText(g, chip.Text, chip.Font, tr, chip.ForeColor, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                int x = chip.Width - 4;
                if (live) { x -= 30; ChipTag(e.Graphics, "LIVE", StreamColor, x, chip.Height); x -= 2; }
                if (inMic) { x -= 28; ChipTag(e.Graphics, "MIC", MicColor, x, chip.Height); }
            };
            chip.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) chip.DoDragDrop(DragPrefix + exe, DragDropEffects.Move); };
            tips.SetToolTip(chip, a.Pretty + " (" + exe + ".exe). Drag to another channel, onto Mic or Stream, or right-click.");
            chip.ContextMenuStrip = AppMenu(exe, channel);
            return chip;
        }

        void UpdateMuteButton(int ci)
        {
            bool m = Settings.GetBool("ch." + Channels[ci] + ".mute", false);
            chMute[ci].Text = m ? "Muted" : "Mute";
            chMute[ci].BackColor = m ? Color.FromArgb(150, 45, 45) : Card2;
            PublishChannel(ci);
        }

        void ToggleMute(int ci)
        {
            string k = "ch." + Channels[ci] + ".mute";
            Settings.Set(k, !Settings.GetBool(k, false));
            UpdateMuteButton(ci);
            GoxOnChannelMute(ci);
            ApplyMixer();
        }

        // ---------- Stream Deck control ----------

        ControlServer control;

        void StartControlServer()
        {
            control = new ControlServer(cmd =>
            {
                try { if (IsHandleCreated) BeginInvoke(new Action(() => HandleCommand(cmd))); } catch { }
            });
            control.Start();
            for (int i = 0; i < 4; i++) { PublishChannel(i); PublishOutput(i); }
            PublishDevices();
            PublishSounds();
            PublishProfiles();
            PublishHearYourself();
            PublishStreamMonitor();
            control.Publish("bleep", "bleep 0");
            ApplyFx();
            UpdateChatMuteButton();
            UpdateMixToggle();
        }

        void PublishChannel(int ci)
        {
            if (control == null) return;
            bool m = Settings.GetBool("ch." + Channels[ci] + ".mute", false);
            control.Publish("ch." + Channels[ci], "state " + Channels[ci] + " " + chVol[ci].Value + " " + (m ? 1 : 0));
        }

        void HandleCommand(string cmd)
        {
            var p = cmd.Split(' ');
            int ci = p.Length > 1 ? Array.IndexOf(Channels, p[1]) : -1;
            if (p[0] == "vol" && p.Length == 3 && ci >= 0)
            {
                int v;
                if (!int.TryParse(p[2], NumberStyles.AllowLeadingSign, Inv, out v)) return;
                int nv = p[2][0] == '+' || p[2][0] == '-' ? chVol[ci].Value + v : v;
                chVol[ci].Value = Math.Max(0, Math.Min(100, nv));
            }
            else if (p[0] == "mute" && ci >= 0) ToggleMute(ci);
            else if (p[0] == "mixtoggle") ToggleMediaMix();
            else if (p[0] == "out" && p.Length == 3 && ci >= 0) CycleOutput(ci, p[2] == "prev" ? -1 : 1);
            else if (p[0] == "setout" && p.Length == 3 && ci >= 0) SetChannelOutput(ci, p[2] == "-" ? "" : p[2]);
            else if (p[0] == "play" && p.Length == 2) { int n; if (int.TryParse(p[1], out n)) PlaySound(n); }
            else if (p[0] == "stopsounds") StopSounds();
            else if (p[0] == "profile" && cmd.Length > 8) ApplyProfile(cmd.Substring(8), false);
            else if (p[0] == "sidetoggle") ToggleHearYourself();
            else if (p[0] == "chatmute") ToggleChatMute();
            else if (p[0] == "voicefx") ToggleFx();
            else if (p[0] == "bleep" && p.Length == 2) bleepDeckHeld = p[1] == "down";
            else if (p[0] == "streammon") ToggleStreamMonitor();
            else if (p[0] == "ptt" && p.Length == 2) pttDeckHeld = p[1] == "down";
            else if (p[0] == "update" && !Updater.IsDevCopy) { autoInstallUpdate = true; CheckForUpdates(false); }
        }

        // Virtual devices (Sonar, Steam, VB-CABLE) are skipped when cycling outputs from the Stream Deck.
        static bool IsVirtualOutput(string name)
        {
            return name.IndexOf("Sonar", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void CycleOutput(int ci, int dir)
        {
            var ids = new List<string> { "" };
            ids.AddRange(renderDevs.Where(d => !IsHiddenOutput(d)).Select(d => d.Id));
            string cur = Settings.Get("ch." + Channels[ci] + ".dev", "");
            int i = ids.IndexOf(cur);
            int next = i < 0 ? (dir > 0 ? 0 : ids.Count - 1) : ((i + dir) % ids.Count + ids.Count) % ids.Count;
            SetChannelOutput(ci, ids[next]);
        }

        void SetChannelOutput(int ci, string id)
        {
            if (id != "" && !renderDevs.Any(d => d.Id == id)) { PublishOutput(ci); return; } // unplugged
            FillDeviceCombo(chDevice[ci], ShownOutputs(id), id, true);
            Settings.Set("ch." + Channels[ci] + ".dev", id);
            routed.Clear();
            ApplyMixer();
            PublishOutput(ci);
        }

        // "Speakers (Arctis Pro Wireless Game)" -> "Arctis Pro Wireless Game"; "LG ULTRAWIDE (NVIDIA ...)" -> "LG ULTRAWIDE"
        static string ShortDeviceName(string name)
        {
            int open = name.IndexOf(" (");
            if (open < 0 || !name.EndsWith(")")) return name;
            string outer = name.Substring(0, open), inner = name.Substring(open + 2, name.Length - open - 3);
            string[] generic = { "speakers", "headphones", "headset", "headset earphone", "earphones", "line out", "digital output", "realtek digital output", "output" };
            return generic.Contains(outer.Trim().ToLowerInvariant()) ? inner : outer;
        }

        void PublishOutput(int ci)
        {
            if (control == null) return;
            string id = Settings.Get("ch." + Channels[ci] + ".dev", "");
            var d = renderDevs.FirstOrDefault(x => x.Id == id);
            string name = id == "" ? "System default" : d == null ? "Unplugged" : ShortDeviceName(d.Name);
            control.Publish("out." + Channels[ci], "out " + Channels[ci] + " " + (id == "" ? "-" : id) + " " + name);
        }

        // "devices <id>\t<name>\t<id>\t<name>..." with "-" meaning System default. Hidden outputs are left out.
        void PublishDevices()
        {
            if (control == null) return;
            var parts = new List<string> { "-", "System default" };
            foreach (var d in renderDevs.Where(x => !IsHiddenOutput(x))) { parts.Add(d.Id); parts.Add(ShortDeviceName(d.Name)); }
            control.Publish("devices", "devices " + string.Join("\t", parts));
        }

        static string GuessChannel(string exe)
        {
            string[] chat = { "harmony", "discord","discordptb", "discordcanary", "teamspeak", "ts3client_win64", "ts3client_win32", "slack", "ms-teams", "teams",
                              "zoom", "skype", "mumble", "element", "signal", "whatsapp", "guilded", "telegram" };
            string[] media = { "spotify", "chrome", "firefox", "msedge", "brave", "opera", "opera_gx", "vivaldi", "vlc", "mpc-hc64", "mpc-be64",
                               "foobar2000", "itunes", "applemusic", "wmplayer", "music.ui", "tidal", "deezer", "plex", "potplayermini64", "aimp", "musicbee" };
            // Sonar's own process carries every app's audio while it's installed; turning it down would turn everything down.
            if (exe.StartsWith("steelseries")) return "None";
            if (chat.Contains(exe)) return "Chat";
            if (media.Contains(exe)) return "Media";
            return "Game";
        }

        string ChannelFor(string exe)
        {
            return Settings.Get("app." + exe, null) ?? GuessChannel(exe);
        }

        void UpdateAppsList()
        {
            var apps = lastSessions.GroupBy(s => s.Exe).Select(g => g.First()).OrderBy(s => s.Pretty).ToList();
            // Rebuild when the apps change, or their MIC / LIVE tags do.
            string key = (StreamerOn ? "S|" : "") + string.Join("|", apps.Select(a => a.Exe + (InMic(a.Exe) ? "+m" : "") + (StreamerOn && StreamGainFor(a.Exe) > 0 ? "+s" : "")));
            if (key == appsKey) return;
            appsKey = key;
            FillStreamZone(apps);

            foreach (var kv in zones)
            {
                var zone = kv.Value;
                zone.SuspendLayout();
                foreach (Control old in zone.Controls.Cast<Control>().ToList())
                {
                    if (old.ContextMenuStrip != null) old.ContextMenuStrip.Dispose();
                    old.Dispose();
                }
                zone.Controls.Clear();
                foreach (var a in apps.Where(x => (Array.IndexOf(Channels, ChannelFor(x.Exe)) >= 0 ? ChannelFor(x.Exe) : "None") == kv.Key))
                {
                    var chip = MakeChip(a, kv.Key);
                    MakeDropTarget(chip, kv.Key, zone);
                    zone.Controls.Add(chip);
                }
                if (zone.Controls.Count == 0)
                {
                    var hint = new Label { Text = "Drag apps here", Width = 140, Height = 32, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(95, 100, 108), Margin = new Padding(4, 4, 0, 0) };
                    MakeDropTarget(hint, kv.Key, zone);
                    zone.Controls.Add(hint);
                }
                zone.ResumeLayout();
                if (uiScale != 1f) ScaleTree(zone);
            }
        }

        void ApplyMixer()
        {
            foreach (var s in lastSessions)
            {
                string ch = ChannelFor(s.Exe);
                int ci = Array.IndexOf(Channels, ch);
                if (ci < 0) continue;
                // Mute = a tiny volume, not a real mute (a real mute or exactly 0 makes Windows send silence,
                // which the stream mix would record too). Channels controlled by a GoXLR stay at full here.
                float vol = PersonalVolume(ci);
                try
                {
                    float cur; s.Volume.GetMasterVolume(out cur);
                    if (Math.Abs(cur - vol) > 0.004f) s.Volume.SetMasterVolume(vol, ref Audio.Ctx);
                    bool curMute; s.Volume.GetMute(out curMute);
                    if (curMute) s.Volume.SetMute(false, ref Audio.Ctx);
                }
                catch { }

                string dev = Settings.Get("ch." + ch + ".dev", "");
                string done;
                if (routed.TryGetValue(s.Pid, out done) && done == dev) continue;
                string prev = Settings.Get("routed." + s.Exe, "");
                if (dev != "" || prev != "")
                {
                    Audio.SetAppOutput(s.Pid, dev);
                    if (dev == "") Settings.Remove("routed." + s.Exe);
                    else Settings.Set("routed." + s.Exe, dev);
                }
                routed[s.Pid] = dev;
            }
            if (routed.Count > 400) routed.Clear();
            if (mixEngine.Running) UpdateEngineSources(); // keep mic/stream levels independent of your volume
        }

        // ---------- Equalizer ----------

        void BuildEq()
        {
            var p = NewPage("Equalizer", "Equalizer", "Each output device has its own EQ, so game and chat audio can sound different.");
            eqStatus = ApoStatus(p, 84);

            Lbl(p, "Device", 25, 134, 9.5f, false, Dim);
            eqDevice = Combo(p, 100, 130, 330);
            eqDevice.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = eqDevice.SelectedItem as DeviceItem;
                if (it != null) Settings.Set("eq.dev", it.Id);
                LoadEqDevice();
            };
            eqOn = Chk(p, "EQ on for this device", 450, 132);
            eqOn.CheckedChanged += (s, e) => { if (loading) return; string k = EqKey("on"); if (k != null) { Settings.Set(k, eqOn.Checked); ScheduleApo(); } };

            Lbl(p, "Preset", 25, 174, 9.5f, false, Dim);
            eqPreset = Combo(p, 100, 170, 200);
            foreach (var n in PresetNames) eqPreset.Items.Add(n);
            eqPreset.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                int pi = eqPreset.SelectedIndex;
                string k = EqKey("preset");
                if (k == null) return;
                Settings.Set(k, PresetNames[pi]);
                if (pi >= Presets.Length) return;
                loading = true;
                for (int i = 0; i < 10; i++) { eqBands[i].Value = Presets[pi][i]; Settings.Set(EqKey("b" + i), Presets[pi][i]); }
                loading = false;
                if (!eqOn.Checked) eqOn.Checked = true;
                ScheduleApo();
            };
            var reset = Btn(p, "Reset to flat", 316, 169, 120, 28);
            reset.Click += (s, e) => eqPreset.SelectedIndex = 0;

            for (int i = 0; i < 10; i++)
            {
                int bi = i;
                var card = new Panel { Left = 24 + i * 80, Top = 216, Width = 72, Height = 300, BackColor = Card };
                p.Controls.Add(card);
                var val = Lbl(card, "0 dB", 0, 10, 9f, true, Fg);
                val.AutoSize = false; val.Width = 72; val.TextAlign = ContentAlignment.MiddleCenter;
                eqVals[i] = val;
                var tb = new Slider
                {
                    Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, TickStyle = TickStyle.Both, TickFrequency = 3,
                    Left = 12, Top = 36, Height = 220, BackColor = Card, SmallChange = 1, LargeChange = 3
                };
                card.Controls.Add(tb);
                eqBands[i] = tb;
                var f = Lbl(card, Freqs[i] >= 1000 ? (Freqs[i] / 1000) + "k" : Freqs[i].ToString(), 0, 266, 9f, false, Dim);
                f.AutoSize = false; f.Width = 72; f.TextAlign = ContentAlignment.MiddleCenter;
                tb.ValueChanged += (s, e) =>
                {
                    int v = eqBands[bi].Value;
                    eqVals[bi].Text = (v > 0 ? "+" : "") + v + " dB";
                    if (loading) return;
                    string k = EqKey("b" + bi);
                    if (k == null) return;
                    Settings.Set(k, v);
                    loading = true;
                    eqPreset.SelectedIndex = PresetNames.Length - 1;
                    Settings.Set(EqKey("preset"), "Custom");
                    if (!eqOn.Checked) { eqOn.Checked = true; Settings.Set(EqKey("on"), true); }
                    loading = false;
                    ScheduleApo();
                };
            }
            Lbl(p, "Hz", 830, 482, 9f, false, Dim);
        }

        string EqKey(string suffix)
        {
            var it = eqDevice.SelectedItem as DeviceItem;
            if (it == null || it.Id == null) return null;
            return "eq." + Audio.GuidOf(it.Id) + "." + suffix;
        }

        void LoadEqDevice()
        {
            bool prev = loading;
            loading = true;
            if (EqKey("on") != null)
            {
                eqOn.Checked = Settings.GetBool(EqKey("on"), false);
                int pi = Array.IndexOf(PresetNames, Settings.Get(EqKey("preset"), "Flat"));
                eqPreset.SelectedIndex = pi < 0 ? 0 : pi;
                for (int i = 0; i < 10; i++)
                    eqBands[i].Value = Math.Max(-12, Math.Min(12, Settings.GetInt(EqKey("b" + i), 0)));
            }
            LoadHeadphoneLabel();
            loading = prev;
        }

        // ---------- Microphone ----------

        void BuildMic()
        {
            var p = NewPage("Microphone", "Microphone", "Set your mic level and clean up how you sound to others.");
            micStatus = ApoStatus(p, 84);

            Lbl(p, "Mic", 25, 134, 9.5f, false, Dim);
            micDevice = Combo(p, 100, 130, 330);
            micDevice.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                var it = micDevice.SelectedItem as DeviceItem;
                if (it != null) Settings.Set("mic.dev", it.Id);
                LoadMicLevel();
                ScheduleApo();
                RestartMicMix();
            };

            Lbl(p, "Mic level", 25, 178, 9.5f, false, Dim);
            micLevel = new Slider { Left = 96, Top = 172, Width = 340, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, BackColor = Bg };
            p.Controls.Add(micLevel);
            micLevelLbl = Lbl(p, "", 444, 176, 10f, true, Fg);
            micLevel.ValueChanged += (s, e) =>
            {
                micLevelLbl.Text = micLevel.Value + "%";
                if (loading) return;
                var it = micDevice.SelectedItem as DeviceItem;
                if (it != null) Audio.SetVolume(it.Id, micLevel.Value / 100f);
            };

            Lbl(p, "Voice processing", 22, 226, 12f, true, Fg);
            micOn = Chk(p, "Turn on voice processing for this mic", 25, 258);
            micHp = Chk(p, "Remove low rumble (desk bumps, fans, AC hum)", 45, 288);
            micClarity = Chk(p, "Clarity boost (helps you cut through in game)", 45, 318);
            micHarsh = Chk(p, "Soften harsh \"s\" sounds", 45, 348);
            Lbl(p, "Extra boost", 45, 386, 9.5f, false, Dim);
            micGain = new Slider { Left = 130, Top = 380, Width = 240, Minimum = 0, Maximum = 12, TickStyle = TickStyle.None, BackColor = Bg };
            p.Controls.Add(micGain);
            micGainLbl = Lbl(p, "", 378, 384, 10f, true, Fg);

            micNs = Chk(p, "Noise suppression plugin (older option)", 45, 426);
            var nsNote = Lbl(p, "Not needed: Remove noise in the Mic column on the Mixer page does this with nothing to install.", 65, 454, 9f, false, Dim);
            nsNote.MaximumSize = new Size(700, 0);
            micNsPath = new TextBox { Left = 65, Top = 480, Width = 430, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true };
            p.Controls.Add(micNsPath);
            var browse = Btn(p, "Browse...", 503, 478, 90, 26);
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "VST plugin (*.dll)|*.dll", Title = "Pick rnnoise_mono.dll" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        micNsPath.Text = dlg.FileName;
                        Settings.Set("mic.nspath", dlg.FileName);
                        if (!micNs.Checked) micNs.Checked = true;
                        ScheduleApo();
                    }
                }
            };
            Link(p, "Get RNNoise (free)", 603, 482, "https://github.com/werman/noise-suppression-for-voice/releases");

            micOn.Checked = Settings.GetBool("mic.on", false);
            micHp.Checked = Settings.GetBool("mic.hp", true);
            micClarity.Checked = Settings.GetBool("mic.clarity", false);
            micHarsh.Checked = Settings.GetBool("mic.harsh", false);
            micNs.Checked = Settings.GetBool("mic.ns", false);
            micGain.Value = Math.Max(0, Math.Min(12, Settings.GetInt("mic.gain", 0)));
            micGainLbl.Text = "+" + micGain.Value + " dB";
            micNsPath.Text = Settings.Get("mic.nspath", "");

            micOn.CheckedChanged += (s, e) => { Settings.Set("mic.on", micOn.Checked); ScheduleApo(); };
            micHp.CheckedChanged += (s, e) => { Settings.Set("mic.hp", micHp.Checked); ScheduleApo(); };
            micClarity.CheckedChanged += (s, e) => { Settings.Set("mic.clarity", micClarity.Checked); ScheduleApo(); };
            micHarsh.CheckedChanged += (s, e) => { Settings.Set("mic.harsh", micHarsh.Checked); ScheduleApo(); };
            micNs.CheckedChanged += (s, e) => { Settings.Set("mic.ns", micNs.Checked); ScheduleApo(); };
            micGain.ValueChanged += (s, e) => { micGainLbl.Text = "+" + micGain.Value + " dB"; Settings.Set("mic.gain", micGain.Value); ScheduleApo(); };
        }

        void LoadMicDevice() { }

        void LoadMicLevel()
        {
            var it = micDevice.SelectedItem as DeviceItem;
            if (it == null) return;
            float v = Audio.GetVolume(it.Id);
            bool prev = loading;
            loading = true;
            if (v >= 0) micLevel.Value = (int)Math.Round(v * 100);
            micLevelLbl.Text = micLevel.Value + "%";
            loading = prev;
        }

        // ---------- Equalizer APO ----------

        Label ApoStatus(Panel p, int top)
        {
            var l = Lbl(p, "", 25, top, 9.5f, false, Fg);
            l.MaximumSize = new Size(560, 0);
            var btn = Btn(p, "Install Equalizer APO (free)", 600, top - 4, 230, 30);
            btn.BackColor = Accent; btn.ForeColor = Color.White;
            btn.Click += (s, e) => InstallApo();
            l.Tag = btn;
            return l;
        }

        // Downloads Equalizer APO 1.4.2 from SourceForge, checks it, installs it silently (Windows asks permission),
        // then its device window opens so the user can tick their headset and mic.
        bool apoInstalling;
        void InstallApo()
        {
            if (apoInstalling) return;
            const string url = "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe";
            const string sha = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA"; // matches Chocolatey's published hash
            apoInstalling = true;
            foreach (var l in new[] { eqStatus, micStatus }) { l.Text = "Downloading Equalizer APO (11 MB)..."; l.ForeColor = Dim; ((Control)l.Tag).Enabled = false; }
            var t = new System.Threading.Thread(() =>
            {
                string err = null;
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                    byte[] data;
                    using (var wc = new System.Net.WebClient()) { wc.Headers.Add("User-Agent", "SoundDeck/" + AppVersion.Value); data = wc.DownloadData(url); }
                    string got;
                    using (var h = System.Security.Cryptography.SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "");
                    if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new Exception("the download didn't match the expected file, so it wasn't used");
                    string tmp = Path.Combine(Path.GetTempPath(), "EqualizerAPO-x64-1.4.2.exe");
                    File.WriteAllBytes(tmp, data);
                    BeginInvoke(new Action(() => { foreach (var l in new[] { eqStatus, micStatus }) l.Text = "Installing. Click Yes when Windows asks, then tick your headset and mic in the window that opens."; }));
                    Process.Start(new ProcessStartInfo(tmp, "/S") { Verb = "runas", UseShellExecute = true }).WaitForExit();
                    try { File.Delete(tmp); } catch { }
                    if (!EqApo.Installed) throw new Exception("its setup didn't finish");
                    System.Threading.Thread.Sleep(2000);
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
                    if (Process.GetProcessesByName("DeviceSelector").Length == 0 && Process.GetProcessesByName("Configurator").Length == 0)
                    {
                        string sel = Path.Combine(dir, File.Exists(Path.Combine(dir, "DeviceSelector.exe")) ? "DeviceSelector.exe" : "Configurator.exe");
                        if (File.Exists(sel)) try { Process.Start(new ProcessStartInfo(sel) { Verb = "runas", UseShellExecute = true }); } catch { }
                    }
                }
                catch (System.ComponentModel.Win32Exception) { err = "Windows permission was declined, so Equalizer APO wasn't installed."; }
                catch (Exception ex) { err = "Couldn't install Equalizer APO: " + ex.Message; }
                BeginInvoke(new Action(() =>
                {
                    apoInstalling = false;
                    UpdateApoStatus();
                    if (err != null) foreach (var l in new[] { eqStatus, micStatus }) { l.Text = err; l.ForeColor = Accent; ((Control)l.Tag).Enabled = true; }
                    else MessageBox.Show(this, "Equalizer APO is installed.\n\n1. In its window, tick your headphones/headset and your mic, then click OK.\n2. Restart your PC.\n\nAfter that, the Equalizer and voice processing work.", "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            }) { IsBackground = true };
            t.Start();
        }

        void UpdateApoStatus()
        {
            bool ok = EqApo.Installed;
            foreach (var l in new[] { eqStatus, micStatus })
            {
                l.ForeColor = ok ? Good : Accent;
                l.Text = ok
                    ? "Equalizer APO is ready. No effect on a device? Tick it in the Equalizer APO Configurator, then restart."
                    : "This needs Equalizer APO (free, open source, no ads). Click the button to install it; Windows asks permission once, then tick your headset and mic and restart.";
                ((Control)l.Tag).Visible = !ok;
                ((Control)l.Tag).Enabled = !apoInstalling;
            }
        }

        void ScheduleApo()
        {
            if (loading) return;
            apoDebounce.Stop();
            apoDebounce.Start();
        }

        void WriteApo()
        {
            if (!EqApo.Installed) return;
            var sb = new StringBuilder();
            sb.AppendLine("# Written by SoundDeck. Changes made here will be overwritten.");
            foreach (var d in renderDevs)
            {
                string g = Audio.GuidOf(d.Id);
                if (!Settings.GetBool("eq." + g + ".on", false)) continue;
                var bands = Enumerable.Range(0, 10).Select(i => Settings.GetInt("eq." + g + ".b" + i, 0)).ToArray();
                sb.AppendLine("# " + d.Name);
                sb.AppendLine("Device: " + g);
                try { foreach (var l in HeadphoneLines(g)) sb.AppendLine(l); } catch { } // headphone tuning goes first
                int max = bands.Max();
                if (max > 0) sb.AppendLine("Preamp: -" + max + " dB");
                for (int i = 0; i < 10; i++)
                    if (bands[i] != 0)
                        sb.AppendLine(string.Format(Inv, "Filter: ON PK Fc {0} Hz Gain {1} dB Q 1.41", Freqs[i], bands[i]));
            }
            var mic = micDevice.SelectedItem as DeviceItem;
            if (micOn.Checked && mic != null)
            {
                sb.AppendLine("# " + mic.Name);
                sb.AppendLine("Device: " + Audio.GuidOf(mic.Id));
                string ns = Settings.Get("mic.nspath", "");
                if (micNs.Checked && ns != "" && File.Exists(ns)) sb.AppendLine("VSTPlugin: Library \"" + ns + "\"");
                if (micHp.Checked) sb.AppendLine("Filter: ON HP Fc 80 Hz");
                if (micClarity.Checked) sb.AppendLine("Filter: ON PK Fc 3500 Hz Gain 4 dB Q 1");
                if (micHarsh.Checked) sb.AppendLine("Filter: ON PK Fc 7000 Hz Gain -4 dB Q 2");
                if (micGain.Value > 0) sb.AppendLine("Preamp: " + micGain.Value + " dB");
            }
            sb.AppendLine("Device: all");

            string err = EqApo.Write(sb.ToString());
            if (err == "denied" && !askedPermission)
            {
                askedPermission = true;
                var r = MessageBox.Show(this, "SoundDeck needs permission to save settings into Equalizer APO's folder. Windows will ask you to allow it once.\n\nAllow now?",
                    "SoundDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) { EqApo.FixPermissions(); err = EqApo.Write(sb.ToString()); }
            }
            if (err != null && err != "denied")
                MessageBox.Show(this, "Couldn't save EQ settings: " + err, "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------- Mic Mix ----------

        void BuildMicMix()
        {
            var p = NewPage("Mic Mix", "Mic Mix", "Play music or videos into your mic so friends in Harmony hear them. The button below (or a hotkey) turns the music on and off; your voice always goes through.");

            mixCableStatus = Lbl(p, "", 25, 92, 9.5f, false, Fg);
            mixCableStatus.MaximumSize = new Size(560, 0);
            mixCableLink = Link(p, "Get VB-CABLE (free)", 600, 92, "https://vb-audio.com/Cable/");

            mixOn = Chk(p, "Turn on Mic Mix", 25, 140);
            mixOn.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            mixOn.Checked = Settings.GetBool("mix.on", false);
            mixOn.CheckedChanged += (s, e) => { Settings.Set("mix.on", mixOn.Checked); RestartMicMix(); };
            mixMicName = Lbl(p, "", 45, 170, 9f, false, Dim);

            mixToggle = new Button
            {
                Left = 25, Top = 204, Width = 300, Height = 56, FlatStyle = FlatStyle.Flat, ForeColor = Fg, Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold)
            };
            mixToggle.FlatAppearance.BorderSize = 0;
            mixToggle.Click += (s, e) => ToggleMediaMix();
            p.Controls.Add(mixToggle);

            Lbl(p, "Hotkey", 350, 206, 9.5f, false, Dim);
            mixHotkeyBox = new TextBox { Left = 350, Top = 228, Width = 220, BackColor = Card2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true, Cursor = Cursors.Hand };
            mixHotkeyBox.Text = HotkeyText(Settings.GetInt("mix.mods", 0), Settings.GetInt("mix.vk", 0));
            mixHotkeyBox.Enter += (s, e) => { if (IsHandleCreated) UnregisterHotKey(Handle, HotkeyId); mixHotkeyBox.BackColor = Color.FromArgb(70, 50, 40); };
            mixHotkeyBox.Leave += (s, e) => { mixHotkeyBox.BackColor = Card2; RegisterMixHotkey(); };
            mixHotkeyBox.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true; e.Handled = true;
                Keys k = e.KeyCode;
                if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return;
                if (k == Keys.Tab || k == Keys.Escape) { mixToggle.Focus(); return; }
                int mods = 0, vk = (int)k;
                if (k == Keys.Back || k == Keys.Delete) vk = 0;
                else mods = (e.Control ? 2 : 0) | (e.Alt ? 1 : 0) | (e.Shift ? 4 : 0);
                Settings.Set("mix.mods", mods);
                Settings.Set("mix.vk", vk);
                mixHotkeyBox.Text = HotkeyText(mods, vk);
                mixToggle.Focus();
            };
            p.Controls.Add(mixHotkeyBox);
            mixHotkeyStatus = Lbl(p, "Click the box, then press your keys. Backspace clears it.", 350, 254, 8.5f, false, Dim);

            // Apps you haven't picked yet default to this channel (old setting, kept hidden for profiles).
            mixSource = Combo(p, 150, 296, 150);
            mixSource.Items.AddRange(new object[] { "Media", "Aux", "Game" });
            int si = mixSource.Items.IndexOf(Settings.Get("mix.source", "Media"));
            mixSource.SelectedIndex = si < 0 ? 0 : si;
            mixSource.SelectedIndexChanged += (s, e) => Settings.Set("mix.source", (string)mixSource.SelectedItem);
            mixSource.Visible = false;
            mixAppsLbl = Lbl(p, "", 25, 300, 9.5f, false, Fg);
            mixAppsLbl.MaximumSize = new Size(800, 0);

            Lbl(p, "Music volume", 25, 344, 9.5f, false, Dim);
            mixMediaVol = new Slider { Left = 150, Top = 338, Width = 300, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, BackColor = Bg, Value = Math.Max(0, Math.Min(100, Settings.GetInt("mix.mediaVol", 35))) };
            p.Controls.Add(mixMediaVol);
            mixMediaLbl = Lbl(p, "", 456, 342, 10f, true, Fg);
            mixMediaVol.ValueChanged += (s, e) => { Settings.Set("mix.mediaVol", mixMediaVol.Value); UpdateMixGains(); };

            Lbl(p, "Your voice", 25, 388, 9.5f, false, Dim);
            mixMicVol = new Slider { Left = 150, Top = 382, Width = 300, Minimum = 0, Maximum = 200, TickStyle = TickStyle.None, BackColor = Bg, Value = Math.Max(0, Math.Min(200, Settings.GetInt("mix.micVol", 100))) };
            p.Controls.Add(mixMicVol);
            mixMicLbl = Lbl(p, "", 456, 386, 10f, true, Fg);
            mixMicVol.ValueChanged += (s, e) => { Settings.Set("mix.micVol", mixMicVol.Value); UpdateMixGains(); };
            Lbl(p, "These only change what your friends hear.", 155, 428, 8.5f, false, Dim);

            mixBeep = Chk(p, "Beep when the hotkey turns music on or off", 25, 446);
            mixBeep.Checked = Settings.GetBool("mix.beep", true);
            mixBeep.CheckedChanged += (s, e) => Settings.Set("mix.beep", mixBeep.Checked);

            mixEngineStatus = Lbl(p, "", 25, 478, 9.5f, true, Dim);

            Lbl(p, "One-time setup", 22, 516, 12f, true, Fg);
            steps = Lbl(p, "", 25, 546, 9.5f, false, Dim);
            steps.MaximumSize = new Size(800, 0);

            UpdateMixGains();
            UpdateMixToggle();
        }

        static string HotkeyText(int mods, int vk)
        {
            if (vk == 0) return "(none)";
            var parts = new List<string>();
            if ((mods & 2) != 0) parts.Add("Ctrl");
            if ((mods & 1) != 0) parts.Add("Alt");
            if ((mods & 4) != 0) parts.Add("Shift");
            string key = ((Keys)vk).ToString();
            if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key.Substring(1);
            parts.Add(key);
            return string.Join(" + ", parts);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTitleBar();
            RegisterMixHotkey();
            RegisterSideHotkey();
            RegisterFxHotkey();
            BeginInvoke(new Action(GoXlrWindowReady));
        }

        // Dark title bar, colored to match the side panel (Windows 11; Windows 10 gets plain dark mode).
        void DarkTitleBar()
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);                // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = Card.R | (Card.G << 8) | (Card.B << 16);      // COLORREF is 0x00BBGGRR
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);           // DWMWA_CAPTION_COLOR
                int text = Fg.R | (Fg.G << 8) | (Fg.B << 16);
                DwmSetWindowAttribute(Handle, 36, ref text, 4);              // DWMWA_TEXT_COLOR
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) ToggleMediaMix();
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == SideHotkeyId) ToggleHearYourself();
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == FxHotkeyId) ToggleFx();
            base.WndProc(ref m);
        }

        void RegisterMixHotkey()
        {
            if (!IsHandleCreated) return;
            UnregisterHotKey(Handle, HotkeyId);
            int vk = Settings.GetInt("mix.vk", 0);
            int mods = Settings.GetInt("mix.mods", 0);
            hotkeyOk = vk != 0 && RegisterHotKey(Handle, HotkeyId, (uint)(mods | 0x4000), (uint)vk);
            if (mixHotkeyStatus != null)
            {
                bool clash = vk != 0 && !hotkeyOk;
                mixHotkeyStatus.Text = clash ? "Another program already uses that hotkey. Pick a different one." : "Click the box, then press your keys. Backspace clears it.";
                mixHotkeyStatus.ForeColor = clash ? Accent : Dim;
            }
        }

        void ToggleMediaMix()
        {
            mixEngine.MediaOn = !mixEngine.MediaOn;
            UpdateMixToggle();
            if (mixBeep.Checked)
            {
                Beep(mixEngine.MediaOn);
            }
            if (!mixEngine.Running)
                tray.ShowBalloonTip(2500, "Mic Mix isn't running", "Open SoundDeck > Mic Mix to set it up.", ToolTipIcon.Warning);
        }

        void UpdateMixToggle()
        {
            bool on = mixEngine.MediaOn;
            mixToggle.Text = on ? "Music in mic: ON" : "Music in mic: OFF";
            mixToggle.BackColor = on ? Color.FromArgb(30, 140, 95) : Card2;
            if (tray != null) tray.Text = on ? "SoundDeck (music in mic: ON)" : "SoundDeck";
            if (control != null) control.Publish("mix", "mix " + (on ? 1 : 0));
        }

        void UpdateMixGains()
        {
            mixMediaLbl.Text = mixMediaVol.Value + "%";
            mixMicLbl.Text = mixMicVol.Value + "%";
            mixEngine.MediaGain = mixMediaVol.Value / 100f;
            mixEngine.MicGain = Settings.GetBool("chat.muted", false) ? 0f : mixMicVol.Value / 100f; // streamer "mute me in chat"
        }

        // VB-CABLE if installed, otherwise Steam's virtual mic (installed with Steam Remote Play), which loops
        // whatever is played into "Speakers (Steam Streaming Microphone)" back out as "Microphone (Steam Streaming Microphone)".
        AudioDevice FindCable()
        {
            return renderDevs.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? renderDevs.FirstOrDefault(d => d.Name.IndexOf("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsSteamCable(AudioDevice cable)
        {
            return cable != null && cable.Name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string DiscordInputName(AudioDevice cable)
        {
            return IsSteamCable(cable) ? "Microphone (Steam Streaming Microphone)" : "CABLE Output (VB-Audio Virtual Cable)";
        }

        void RestartMicMix()
        {
            if (mixOn == null) return;
            mixEngine.Stop();
            mixLastStart = -100;
            TickMicMix();
        }

        void TickMicMix()
        {
            if (mixOn == null) return;
            var cable = FindCable();
            mixCableStatus.Text = cable == null
                ? "Mic Mix needs a virtual mic. Install Steam (it includes one) or VB-CABLE (free, no ads), restart your PC, then come back here."
                : "Ready. In Harmony, set your microphone to \"" + DiscordInputName(cable) + "\".";
            steps.Text =
                "1.  In Harmony's voice settings, set your microphone (input device) to \"" + DiscordInputName(cable) + "\".\n" +
                "2.  Tick \"Turn on Mic Mix\" above. Keep SoundDeck running, because your Harmony mic now goes through it.\n" +
                "3.  Put Spotify, your browser, etc. in the Media channel on the Mixer page.\n" +
                (IsSteamCable(cable) ? "Steam's virtual mic is already on your PC, so there's nothing else to install." : "");
            mixCableStatus.ForeColor = cable != null ? Good : Accent;
            mixCableLink.Visible = cable == null;

            var mic = micDevice.SelectedItem as DeviceItem;
            bool badMic = mic == null || mic.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || mic.Name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0;
            mixMicName.Text = badMic
                ? "Pick your real mic (like Headset Microphone) on the Microphone page."
                : "Using mic: " + mic.Name + "  (change it on the Microphone page)";
            mixMicName.ForeColor = badMic ? Accent : Dim;

            UpdateEngineSources();
            var micApps = lastSessions.Where(s => InMic(s.Exe)).Select(s => s.Pretty).Distinct().ToList();
            mixAppsLbl.Text = micApps.Count == 0
                ? "No apps in your mic yet. On the Mixer page, drag an app onto Mic, or right-click it > Play in my mic."
                : "In your mic: " + string.Join(", ", micApps) + ". Change this on the Mixer page.";

            if (mixOn.Checked && cable != null && !badMic)
            {
                // Retry every 5 seconds if the engine stopped (e.g. a device was unplugged),
                // and restart if streamer mode's second mic changed.
                string stream = StreamTargetId();
                bool streamChanged = mixEngine.Running && mixEngine.StreamDeviceId != stream;
                if (streamChanged || (!mixEngine.Running && tickCount - mixLastStart >= 5))
                {
                    mixLastStart = tickCount;
                    mixEngine.Start(mic.Id, cable.Id, stream);
                }
            }
            else if (mixEngine.Running) mixEngine.Stop();

            string st = mixOn.Checked ? mixEngine.Status : "Off";
            int sources = micApps.Count;
            mixEngineStatus.Text = st == "Running"
                ? "Running. Sending your mic" + (sources > 0 ? " and " + sources + " app(s)" : "") + " to " + DiscordInputName(cable) + "."
                : "Status: " + st;
            mixEngineStatus.ForeColor = st == "Running" ? Good : (st.StartsWith("Stopped") ? Accent : Dim);
        }

        // Short two-note beep as a WAV file in memory.
        static byte[] Tone(int f1, int f2)
        {
            const int rate = 44100;
            int each = rate * 7 / 100;
            var samples = new short[each * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                int f = i < each ? f1 : f2;
                int j = i % each;
                double env = Math.Min(1.0, Math.Min(j, each - j) / 300.0);
                samples[i] = (short)(Math.Sin(2 * Math.PI * f * i / rate) * 6000 * env);
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples.Length * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples.Length * 2);
            foreach (var s in samples) w.Write(s);
            return ms.ToArray();
        }

        // ---------- Settings ----------

        void BuildSettings()
        {
            var p = NewPage("Settings", "Settings", "How SoundDeck starts, updates, and which devices it shows.");
            var startup = Chk(p, "Start SoundDeck when Windows starts", 25, 96);
            startup.Checked = IsStartupOn();
            startup.CheckedChanged += (s, e) => SetStartup(startup.Checked);

            var startMin = Chk(p, "Start hidden in the tray", 25, 126);
            startMin.Checked = Settings.GetBool("startHidden", true);
            startMin.CheckedChanged += (s, e) => Settings.Set("startHidden", startMin.Checked);

            var closeTray = Chk(p, "Keep running in the tray when I close the window", 25, 156);
            closeTray.Checked = Settings.GetBool("closeToTray", true);
            closeTray.CheckedChanged += (s, e) => Settings.Set("closeToTray", closeTray.Checked);

            Lbl(p, "Reset", 22, 204, 12f, true, Fg);
            var reset = Btn(p, "Reset every app's output to default", 25, 236, 280, 32);
            reset.Click += (s, e) =>
            {
                Audio.ClearAppOutputs();
                foreach (var k in Settings.KeysStartingWith("routed.")) Settings.Remove(k);
                foreach (var ch in Channels) Settings.Set("ch." + ch + ".dev", "");
                RefreshDevices(true);
                routed.Clear();
                MessageBox.Show(this, "Done. Every app goes back to the Windows default device. Restart apps if they don't switch right away.", "SoundDeck");
            };

            Lbl(p, "Replacing Sonar", 22, 294, 12f, true, Fg);
            var sonar = Lbl(p, "Happy with SoundDeck? Uninstall SteelSeries GG in Windows Settings > Apps. That also removes Sonar's virtual devices, " +
                "so pick your headset again in each Mixer channel afterwards.", 25, 324, 9.5f, false, Dim);
            sonar.MaximumSize = new Size(540, 0);

            Lbl(p, "Outputs", 22, 384, 12f, true, Fg);
            var outNote = Lbl(p, "Untick devices you don't use to hide them from the Mixer and Stream Deck. " +
                "Windows keeps them, and nothing already using one is moved.", 25, 414, 9.5f, false, Dim);
            outNote.MaximumSize = new Size(540, 0);
            outputsPanel = new FlowLayoutPanel { Left = 25, Top = 460, Width = 780, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 20) };
            p.Controls.Add(outputsPanel);
        }

        FlowLayoutPanel outputsPanel;

        bool IsHiddenOutput(AudioDevice d)
        {
            return Settings.GetBool("out.hide." + d.Id, IsVirtualOutput(d.Name));
        }

        // Shown devices for the Mixer/tray/Stream Deck; the one already in use stays visible.
        List<AudioDevice> ShownOutputs(string keepId)
        {
            return renderDevs.Where(d => !IsHiddenOutput(d) || d.Id == keepId).ToList();
        }

        void FillOutputsPanel()
        {
            if (outputsPanel == null) return;
            outputsPanel.SuspendLayout();
            foreach (Control old in outputsPanel.Controls.Cast<Control>().ToList()) old.Dispose();
            outputsPanel.Controls.Clear();
            foreach (var d in renderDevs)
            {
                string id = d.Id;
                var c = new CheckBox { Text = d.Name, AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Cursor = Cursors.Hand, Checked = !IsHiddenOutput(d), Margin = new Padding(0, 2, 0, 2) };
                c.CheckedChanged += (s, e) =>
                {
                    Settings.Set("out.hide." + id, !((CheckBox)s).Checked);
                    BeginInvoke(new Action(() => RefreshDevices(true)));
                };
                outputsPanel.Controls.Add(c);
            }
            outputsPanel.ResumeLayout();
            if (uiScale != 1f) ScaleTree(outputsPanel);
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool IsStartupOn()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("SoundDeck") != null; }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue("SoundDeck", "\"" + Application.ExecutablePath + "\" --autostart");
                    else k.DeleteValue("SoundDeck", false);
                }
            }
            catch { }
        }

        // ---------- Tray ----------

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip
            {
                ShowCheckMargin = false, ShowImageMargin = false, Renderer = new MenuRenderer(),
                Padding = new Padding(4, 4, 4, 4), Font = new Font("Segoe UI", 10f), DropShadowEnabled = false
            };
            // Windows 11 rounds the menu's corners and gives it a soft shadow.
            trayMenu.HandleCreated += (s, e) =>
            {
                try { int round = 2; DwmSetWindowAttribute(trayMenu.Handle, 33, ref round, 4); } catch { }
            };
            trayMenu.Items.Add("...");
            trayMenu.Opening += (s, e) => { BuildTrayMenu(); e.Cancel = false; };
            tray = new NotifyIcon { Icon = Icon, Text = "SoundDeck", Visible = true, ContextMenuStrip = trayMenu };
            // Left-click opens the window; right-click shows Open / Close.
            tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        }

        void BuildTrayMenu()
        {
            trayMenu.Items.Clear();
            var open = new ToolStripMenuItem("Open SoundDeck") { Padding = new Padding(10, 7, 24, 7) };
            open.Click += (s, e) => ShowWindow();
            trayMenu.Items.Add(open);
            var exit = new ToolStripMenuItem("Close SoundDeck") { Padding = new Padding(10, 7, 24, 7) };
            exit.Click += (s, e) => ExitApp();
            trayMenu.Items.Add(exit);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark menu with rounded hover highlight, matching the app.
        class MenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { } // DWM draws the rounded border

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                using (var path = new GraphicsPath())
                using (var b = new SolidBrush(Card2))
                {
                    int d = 10;
                    path.AddArc(r.X, r.Y, d, d, 180, 90);
                    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();
                    g.FillPath(b, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Fg;
                base.OnRenderItemText(e);
            }
        }

        // ---------- Devices + timer ----------

        void OnTick()
        {
            tickCount++;
            if (tickCount % 3 == 1) RefreshDevices(false);
            GuardDefaultOutput();
            lastSessions = Audio.GetSessions();
            UpdateAppsList();
            ApplyMixer();
            TickMicMix();
            TickHearYourself();
            TickStreaming();
            TickStreamMonitor();
            TickEffectsStatus();
            TickGoxAutostart();
            TickProfiles();
            LoadMicColumn();
            Settings.SaveIfDirty();
        }

        void RefreshDevices(bool force)
        {
            var r = Audio.GetDevices(EDataFlow.Render);
            var c = Audio.GetDevices(EDataFlow.Capture);
            string key = string.Join("|", r.Select(d => d.Id + d.Name)) + "#" + string.Join("|", c.Select(d => d.Id + d.Name));
            if (!force && key == deviceKey) return;
            deviceKey = key;
            renderDevs = r; captureDevs = c;

            for (int i = 0; i < 4; i++)
            {
                string cur = Settings.Get("ch." + Channels[i] + ".dev", "");
                FillDeviceCombo(chDevice[i], ShownOutputs(cur), cur, true);
                PublishOutput(i);
            }
            PublishDevices();
            FillOutputsPanel();
            if (sideDev != null) FillOutCombo(sideDev, "side.dev");
            if (sbDev != null) FillOutCombo(sbDev, "sb.dev");
            if (monDev != null) FillOutCombo(monDev, "mon.dev");
            FillDeviceCombo(eqDevice, renderDevs, Settings.Get("eq.dev", Audio.GetDefaultId(EDataFlow.Render) ?? ""), false);
            FillDeviceCombo(micDevice, captureDevs, Settings.Get("mic.dev", null) ?? RealMicId(), false);
            LoadEqDevice();
            LoadMicLevel();
            FillMicColumn();
            UpdateGoXlr();
            FillStreamDevices();
        }

        static bool IsVirtualMic(string name)
        {
            return name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0
                // GoXLR inputs that aren't a mic: the full stream mix (would echo everything) and the sampler.
                || (IsGoXlr(name) && (name.StartsWith("Broadcast Stream Mix", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Sample", StringComparison.OrdinalIgnoreCase)));
        }

        // The Windows default mic may be the virtual mic (so games hear Mic Mix), so pick and remember a real one.
        string RealMicId()
        {
            string def = Audio.GetDefaultId(EDataFlow.Capture);
            var real = captureDevs.Where(d => !IsVirtualMic(d.Name)).ToList();
            var pick = real.FirstOrDefault(d => d.Id == def)
                ?? real.FirstOrDefault(d => IsGoXlr(d.Name) && d.Name.StartsWith("Chat Mic", StringComparison.OrdinalIgnoreCase))
                ?? real.FirstOrDefault(d => d.Name.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? real.FirstOrDefault();
            if (pick == null) return "";
            Settings.Set("mic.dev", pick.Id);
            return pick.Id;
        }

        void FillDeviceCombo(ComboBox cb, List<AudioDevice> devs, string selectedId, bool includeDefault)
        {
            bool prev = loading;
            loading = true;
            cb.Items.Clear();
            if (includeDefault) cb.Items.Add(new DeviceItem(null, "System default"));
            int sel = -1;
            foreach (var d in devs)
            {
                cb.Items.Add(new DeviceItem(d.Id, d.Name));
                if (d.Id == selectedId) sel = cb.Items.Count - 1;
            }
            if (sel < 0 && cb.Items.Count > 0) sel = 0;
            cb.SelectedIndex = sel;
            loading = prev;
        }

        // ---------- Helpers ----------

        static Label Lbl(Control parent, string text, int x, int y, float size, bool bold, Color color)
        {
            var l = new Label
            {
                Text = text, Left = x, Top = y, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            parent.Controls.Add(l);
            return l;
        }

        static Button Btn(Control parent, string text, int x, int y, int w, int h)
        {
            var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = h, FlatStyle = FlatStyle.Flat, BackColor = Card2, ForeColor = Fg, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 63, 74);
            parent.Controls.Add(b);
            return b;
        }

        static ComboBox Combo(Control parent, int x, int y, int w)
        {
            var c = new ComboBox { Left = x, Top = y, Width = w, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Card2, ForeColor = Fg, DropDownWidth = 360 };
            parent.Controls.Add(c);
            return c;
        }

        static CheckBox Chk(Control parent, string text, int x, int y)
        {
            var c = new CheckBox { Text = text, Left = x, Top = y, AutoSize = true, ForeColor = Fg, BackColor = Color.Transparent, Cursor = Cursors.Hand };
            parent.Controls.Add(c);
            return c;
        }

        static LinkLabel Link(Control parent, string text, int x, int y, string url)
        {
            var l = new LinkLabel { Text = text, Left = x, Top = y, AutoSize = true, LinkColor = Accent, ActiveLinkColor = Fg, BackColor = Color.Transparent };
            l.LinkClicked += (s, e) => { try { Process.Start(url); } catch { } };
            parent.Controls.Add(l);
            return l;
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Accent)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, 10, 20, 10, 13);
                    g.DrawLine(pen, 16, 24, 16, 8);
                    g.DrawLine(pen, 22, 19, 22, 14);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    static class Program
    {
        static bool crashShown;

        // Writes %APPDATA%\SoundDeck\crash.log and tells the user, so a problem can be sent to whoever shared SoundDeck.
        static void Crash(Exception ex, bool fatal)
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundDeck", "crash.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                File.AppendAllText(log, "==== " + DateTime.Now + "  SoundDeck " + AppVersion.Value + "  Windows " + Environment.OSVersion.Version + (fatal ? "  (fatal)" : "") +
                    "\r\n" + (ex == null ? "unknown error" : ex.ToString()) + "\r\n\r\n");
            }
            catch { }
            if (crashShown) return;
            crashShown = true;
            try
            {
                MessageBox.Show("SoundDeck ran into a problem" + (fatal ? " and has to close" : "") + ":\n\n" + (ex == null ? "unknown error" : ex.Message) +
                    "\n\nDetails were saved to:\n" + log + "\n\nPlease send that file to whoever gave you SoundDeck.", "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            crashShown = fatal; // after a non-fatal error, show the next one too
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var m = new System.Threading.Mutex(true, "SoundDeck_single_instance", out created))
            {
                // Right after an update the old copy is still closing, so wait for it instead of complaining.
                if (!created && args.Contains("--after-update"))
                {
                    try { created = m.WaitOne(15000); }
                    catch (System.Threading.AbandonedMutexException) { created = true; }
                }
                Updater.CleanupOld();
                if (!created)
                {
                    MessageBox.Show("SoundDeck is already running. Look for its orange icon in the tray, near the clock.", "SoundDeck");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Never vanish silently: log any unexpected error and say where the log is.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Crash(e.Exception, false);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception, true);
                Settings.Load();
                bool hidden = args.Contains("--autostart") && Settings.GetBool("startHidden", true);
                MainForm form;
                try { form = new MainForm(hidden); }
                catch (Exception ex) { Crash(ex, true); return; }
                Application.Run(form);
            }
        }
    }
}
