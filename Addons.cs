// Shared by SoundDeck.exe and SoundDeckSetup.exe (build.bat and Installer\build-installer.bat both list this file).
// The one place that says where each free add-on comes from and what its file must be (SHA-256),
// and how an add-on's installer is run with admin rights.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

static class Addons
{
    public const string RnNoiseUrl = "https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip";
    public const string RnNoiseSha = "E4DCC186102850F8DCEBEB766AD1BF34C24F6280BD614EF84B72DAE4783FC517";
    // Equalizer APO is unsigned; this hash matches the one Chocolatey publishes for the same file.
    public const string ApoUrl = "https://downloads.sourceforge.net/project/equalizerapo/1.4.2/EqualizerAPO-x64-1.4.2.exe";
    public const string ApoSha = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA";
    public const string ApoFile = "EqualizerAPO-x64-1.4.2.exe";
    // GoXLR Utility (open source); the hash is the SHA-256 GitHub publishes for this release file.
    public const string GoxUrl = "https://github.com/GoXLR-on-Linux/goxlr-utility/releases/download/v1.2.4/goxlr-utility-1.2.4.exe";
    public const string GoxSha = "0A8743D27EA5D1741956AECFC3AE26C0F7399945CF29D2BB6B3DAB459E86AC2F";
    public const string GoxFile = "goxlr-utility-1.2.4.exe";
    // VB-CABLE must come from vb-audio.com (its license doesn't allow passing it along).
    public const string VbCableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    public const string VbCableSha = "BC44E8974191A5A2427D6C979E2DAB08DBB09925F3F435DFA2F0BA3E45B6EB90";
    public const string VbCableSetup = "VBCABLE_Setup_x64.exe";

    // Throws unless the download is exactly the expected file.
    public static void Check(byte[] data, string sha)
    {
        string got;
        using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(data)).Replace("-", "");
        if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new Exception("the download didn't match the expected file, so it wasn't used");
    }

    // Opens a file so that other programs can still read and run it, but nothing can change, replace, rename or
    // delete it until the returned handle is closed. Then makes sure the file holds exactly the expected bytes.
    // Without this, another program could swap an installer between the moment it's checked and the moment
    // Windows runs it with admin rights.
    public static FileStream Lock(string path, byte[] expected)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            bool same = fs.Length == expected.Length;
            if (same)
            {
                var buf = new byte[81920];
                int at = 0, n;
                while (same && (n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (at + n > expected.Length) { same = false; break; }
                    for (int i = 0; i < n; i++) if (buf[i] != expected[at + i]) { same = false; break; }
                    at += n;
                }
                same = same && at == expected.Length;
            }
            if (!same) throw new Exception("the installer file changed after it was checked, so it wasn't run");
            return fs;
        }
        catch { fs.Dispose(); throw; }
    }

    // Runs a checked installer with admin rights (Windows asks permission) and waits for it to finish.
    // It runs from a new, empty folder and stays locked the whole time. Throws Win32Exception if permission is declined.
    public static void RunElevated(byte[] data, string fileName, string args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "SoundDeck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        try
        {
            File.WriteAllBytes(path, data);
            using (Lock(path, data))
            using (var p = Process.Start(new ProcessStartInfo(path, args) { Verb = "runas", UseShellExecute = true, WorkingDirectory = dir }))
                if (p != null) p.WaitForExit();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // VB-CABLE's setup program needs its driver files beside it, so the zip is unpacked into a folder. The folder
    // is kept: uninstalling SoundDeck uses it to remove the driver. The setup program is locked and compared with
    // the checked zip before it runs with admin rights; the driver files are signed, so Windows checks those.
    // "started" runs once the setup program has opened.
    public static void RunVbCableSetup(byte[] zip, string dir, Action started)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        byte[] setup;
        string path;
        using (var z = new ZipArchive(new MemoryStream(zip)))
        {
            var entry = z.Entries.FirstOrDefault(e => e.Name.Equals(VbCableSetup, StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new Exception("its setup program wasn't in the download");
            z.ExtractToDirectory(dir);
            using (var s = entry.Open())
            using (var ms = new MemoryStream()) { s.CopyTo(ms); setup = ms.ToArray(); }
            path = Path.Combine(dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
        }
        using (Lock(path, setup))
        using (var p = Process.Start(new ProcessStartInfo(path) { Verb = "runas", UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) }))
        {
            if (started != null) started();
            if (p != null) p.WaitForExit();
        }
    }

    // Puts noise removal (one file from the checked RNNoise zip) and its license into a folder.
    // how: who fetched it, for the note left beside it ("by SoundDeck" / "during setup").
    public static void UnpackRnNoise(byte[] zip, string dir, string how)
    {
        using (var z = new ZipArchive(new MemoryStream(zip)))
        {
            var entry = z.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("vst/rnnoise_mono.dll", StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new Exception("rnnoise_mono.dll wasn't in the download");
            entry.ExtractToFile(Path.Combine(dir, "rnnoise_mono.dll"), true);
            var lic = z.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase));
            if (lic != null) lic.ExtractToFile(Path.Combine(dir, "RNNoise-LICENSE.txt"), true);
        }
        File.WriteAllText(Path.Combine(dir, "RNNoise-README.txt"),
            "Noise removal uses rnnoise_mono.dll from the open-source project\r\nhttps://github.com/werman/noise-suppression-for-voice (release v1.10).\r\n" +
            "It was downloaded from that project's GitHub release " + how + ". Source code is available there.\r\n");
    }
}
