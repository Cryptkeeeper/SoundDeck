// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.2.0")]
[assembly: AssemblyFileVersion("1.8.2.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.2";
        public const string Notes = "Fixed setup: restarting your PC during setup (for Equalizer APO or VB-CABLE) no longer leaves SoundDeck half installed."; // shown in "What's new" after updating
    }
}
