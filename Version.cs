// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.7.0")]
[assembly: AssemblyFileVersion("1.8.7.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.7";
        public const string Notes = "Harmony is now treated as your chat app: it goes in the Chat channel automatically, so auto-ducking and the Chat slider work with it.|SoundDeck's instructions now say Harmony instead of Discord."; // shown in "What's new" after updating
    }
}
