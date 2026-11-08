// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.7.0";
        public const string Notes = "Push to talk / push to mute: Microphone page, works in games and Discord. There's a Stream Deck key too.\nHeadphone tuning: on the Equalizer page, pick your headphone model for a correction EQ made for it.\nThis What's new window, so you can see what each update adds."; // shown in "What's new" after updating
    }
}
