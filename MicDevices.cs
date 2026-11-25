// Small helpers about mics that don't need the rest of SoundDeck (so they can be tested on their own):
// recognising the same headset after Windows gives it a new ID (even after Windows deletes the old one), and taking SoundDeck's old mic section out of
// Equalizer APO's config (1.9.0 moved the mic's voice processing into SoundDeck).

using System;
using System.Collections.Generic;
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
            if (string.IsNullOrEmpty(deviceId)) return null;
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hk.OpenSubKey(CaptureKey + Guid(deviceId) + @"\Properties"))
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
