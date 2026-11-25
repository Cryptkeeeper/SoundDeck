// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.5.0")]
[assembly: AssemblyFileVersion("1.9.5.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.5";
        public const string Notes = "Your outputs, Hear yourself and EQ now follow your headset when Windows gives it a new ID (after a Windows update or plugging it into another USB port), like the mic already did.|The Equalizer page tells you when Equalizer APO isn't set up on the device you picked, with a button to set it up."; // shown in "What's new" after updating
    }
}
