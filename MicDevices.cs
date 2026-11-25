// Small helpers about audio devices that don't need the rest of SoundDeck (so they can be tested on their own):
// recognising the same headset (mic or output) after Windows gives it a new ID (even after Windows deletes the old
// one), telling whether Equalizer APO is set up on a device, and taking SoundDeck's old mic section out of
// Equalizer APO's config (1.9.0 moved the mic's voice processing into SoundDeck).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SoundDeck
{
    static class MicDevices
    {
        const string CaptureKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\";

        // "{0.0.1.00000000}.{guid}" -> "{guid}", the part Windows' registry and Equalizer APO use.
        static string Guid(string deviceId)
        {
            int i = deviceId.LastIndexOf('{');
            return i >= 0 ? deviceId.Substring(i) : deviceId;
        }

        // Windows lists every mic it has seen (plugged in or not) in the registry.
        public static bool IsCapture(string deviceIdOrGuid)
        {
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(CaptureKey + Guid(deviceIdOrGuid)))
                    return k != null;
            }
            catch { return false; }
        }

        // The name Windows had for a mic, even one that's unplugged now: "Headset Microphone (Arctis Pro Wireless Chat)".
        public static string KnownName(string deviceId)
        {
            return string.IsNullOrEmpty(deviceId) ? null : NameAt(CaptureKey + Guid(deviceId));
        }

        // The same for an output: "Headphones (Arctis Pro Wireless Game)".
        public static string KnownOutputName(string deviceIdOrGuid)
        {
            return string.IsNullOrEmpty(deviceIdOrGuid) ? null : NameAt(RenderKey + Guid(deviceIdOrGuid));
        }

        const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";

        static string NameAt(string deviceKey)
        {
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(deviceKey + @"\Properties"))
                {
                    if (k == null) return null;
                    var desc = k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                    var iface = k.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                    if (string.IsNullOrEmpty(desc)) return null;
                    return string.IsNullOrEmpty(iface) ? desc : desc + " (" + iface + ")";
                }
            }
            catch { return null; }
        }

        // When Windows sees a device again as a new one, it numbers it: "Headset Microphone (3- Arctis Pro Wireless Chat)".
        // This drops the number, so the same headset matches whichever copy is plugged in.
        public static string PlainName(string name)
        {
            return name == null ? null : Regex.Replace(name, @"\(\d+- ", "(");
        }

        // The saved mic under the ID Windows gives it now. mics = (ID, name) of the real mics Windows has now.
        // It's matched by Windows' old name for the saved ID or, when Windows deleted that entry, by the name SoundDeck
        // saved with it. Null when it isn't plugged in (or there's no name to go by).
        public static string SameMic(string savedId, string savedName, IEnumerable<KeyValuePair<string, string>> mics, Func<string, string> knownName)
        {
            string name = PlainName(knownName(savedId) ?? savedName);
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var m in mics)
                if (string.Equals(PlainName(m.Value), name, StringComparison.OrdinalIgnoreCase)) return m.Key;
            return null;
        }

        // Settings saved per device (EQ is "eq.{guid}.on", "eq.{guid}.b0"...) for a device that has none yet: the guid of
        // an old copy of the same device to take them from, or null. owned: guid -> how many settings it has (the copy
        // with the most wins); present: guids Windows has now (never taken from); nameOf: a guid's device name.
        public static string SettingsDonor(string newName, IDictionary<string, int> owned, ICollection<string> present, Func<string, string> nameOf)
        {
            string want = PlainName(newName);
            if (string.IsNullOrEmpty(want)) return null;
            string best = null;
            int most = 0;
            foreach (var kv in owned)
            {
                if (present.Contains(kv.Key) || kv.Value <= most) continue;
                if (string.Equals(PlainName(nameOf(kv.Key)), want, StringComparison.OrdinalIgnoreCase)) { best = kv.Key; most = kv.Value; }
            }
            return best;
        }

        // What has to change so saved outputs follow a headset to its new ID, worked out from a copy of the settings:
        // setting -> new value. outputKeys hold an output's ID (profiles' "pf|name|...dev" keys are found too);
        // now = (ID, name) of the outputs Windows has; windowsName = Windows' name for an ID it may no longer use.
        //  - every output's name is remembered ("devname.{guid}"), for when Windows deletes an old entry
        //  - a saved output that's gone moves to the device with the same name; one that's only unplugged stays
        //  - EQ ("eq.{guid}.*") and "hide this output" copy from an old copy of the device to the new one
        public static Dictionary<string, string> OutputMoves(IDictionary<string, string> settings, IList<KeyValuePair<string, string>> now,
            Func<string, string> windowsName, IEnumerable<string> outputKeys)
        {
            var set = new Dictionary<string, string>();
            Func<string, string> get = k => { string v; return set.TryGetValue(k, out v) ? v : settings.TryGetValue(k, out v) ? v : null; };
            var present = new HashSet<string>(now.Select(d => Guid(d.Key)), StringComparer.OrdinalIgnoreCase);
            Func<string, string> nameOf = id => windowsName(id) ?? get("devname." + Guid(id));

            foreach (var d in now)
                if (get("devname." + Guid(d.Key)) != d.Value) set["devname." + Guid(d.Key)] = d.Value;

            foreach (var key in outputKeys.Concat(settings.Keys.Where(k => k.StartsWith("pf|") && k.EndsWith(".dev"))).Distinct().ToList())
            {
                string id = get(key) ?? "";
                if (!id.StartsWith("{0.0.0.") || present.Contains(Guid(id))) continue; // empty, a mic, or plugged in
                string same = SameMic(id, get("devname." + Guid(id)), now, windowsName);
                if (same != null) set[key] = same;
            }

            var eqOwned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in settings.Keys.Where(k => k.StartsWith("eq.{")))
            {
                int dot = k.IndexOf('.', 3);
                if (dot < 0) continue;
                string g = k.Substring(3, dot - 3);
                int c;
                eqOwned.TryGetValue(g, out c);
                eqOwned[g] = c + 1;
            }
            var hideKeyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in settings.Keys.Where(k => k.StartsWith("out.hide."))) hideKeyOf[Guid(k.Substring(9))] = k;
            var hideOwned = hideKeyOf.Keys.ToDictionary(g => g, g => 1, StringComparer.OrdinalIgnoreCase);

            foreach (var d in now)
            {
                string g = Guid(d.Key);
                if (!eqOwned.ContainsKey(g))
                {
                    string from = SettingsDonor(d.Value, eqOwned, present, nameOf);
                    if (from != null)
                        foreach (var k in settings.Keys.Where(k => k.StartsWith("eq." + from + ".")).ToList())
                            set["eq." + g + k.Substring(3 + from.Length)] = settings[k];
                }
                if (!hideOwned.ContainsKey(g))
                {
                    string from = SettingsDonor(d.Value, hideOwned, present, nameOf);
                    if (from != null) set["out.hide." + d.Key] = settings[hideKeyOf[from]];
                }
            }
            return set;
        }

        // Equalizer APO only works on devices ticked in its Device Selector, and lists them in the registry.
        // Null when that can't be told (Equalizer APO missing, or a version that keeps no list).
        public static bool? ApoOnDevice(string deviceIdOrGuid)
        {
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var list = hk.OpenSubKey(@"SOFTWARE\EqualizerAPO\Child APOs"))
                {
                    if (list == null) return null;
                    using (var k = list.OpenSubKey(Guid(deviceIdOrGuid))) return k != null;
                }
            }
            catch { return null; }
        }

        // True when the saved mic can never come back: Windows deleted its entry, and there's no saved name to find it
        // by (SoundDeck before 1.9.2 saved only the ID). Then the mic in use should become the saved one.
        public static bool Forgotten(string savedId, string savedName, Func<string, string> knownName, Func<string, bool> isCapture)
        {
            return string.IsNullOrEmpty(savedName) && knownName(savedId) == null && !isCapture(savedId);
        }

        // Equalizer APO config text without the "Device: {guid}" sections for mics (and the "# name" line SoundDeck
        // put above each). Everything else stays exactly as it was.
        public static string WithoutMicSections(string text, Func<string, bool> isCapture)
        {
            var keep = new List<string>();
            bool skip = false;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string t = line.Trim();
                // SoundDeck's mic section never had comments, so a "# name" line starts the next device's section.
                if (skip && t.StartsWith("#")) skip = false;
                if (t.StartsWith("Device:", StringComparison.OrdinalIgnoreCase))
                {
                    string dev = t.Substring(7).Trim();
                    skip = dev.StartsWith("{") && isCapture(dev);
                    // keep[0] is the "# Written by SoundDeck" line, which stays.
                    if (skip && keep.Count > 1 && keep[keep.Count - 1].TrimStart().StartsWith("#")) keep.RemoveAt(keep.Count - 1);
                }
                if (!skip) keep.Add(line);
            }
            return string.Join("\r\n", keep);
        }
    }
}
