// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.0.0")]
[assembly: AssemblyFileVersion("1.8.0.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.0";
        public const string Notes = "New Effects page: voice effects (reverb, echo, deep, high, robot, megaphone) and a bleep key.\nListen to your stream: hear your stream or Discord mix in your headset (Streaming page).\nNew Stream Deck keys: Voice effect, Bleep and Listen to stream (reinstall the plugin from the installer to get them)."; // shown in "What's new" after updating
    }
}
