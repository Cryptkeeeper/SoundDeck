// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.4.0")]
[assembly: AssemblyFileVersion("1.8.4.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.4";
        public const string Notes = "Gate & compressor moved to the Microphone page, next to your other mic settings.\nNew installs start without hotkeys, so nothing clashes with other apps. Your existing hotkeys stay as they are.\nRunning the setup again no longer re-downloads noise removal."; // shown in "What's new" after updating
    }
}
