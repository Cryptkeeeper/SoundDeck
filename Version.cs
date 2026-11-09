// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.6.0")]
[assembly: AssemblyFileVersion("1.8.6.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.6";
        public const string Notes = "GoXLR control is now one click: on the Settings page, Add GoXLR control installs GoXLR Utility quietly (Windows asks permission once) and SoundDeck starts it hidden in the background. Only shows if you have a GoXLR. Two new GoXLR checks on the Testing page."; // shown in "What's new" after updating
    }
}
