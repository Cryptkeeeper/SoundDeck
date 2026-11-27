// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.7.0")]
[assembly: AssemblyFileVersion("1.9.7.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.7";
        public const string Notes = "New: Mute breathing and vape puffs, on the Microphone page under Noise and echo removal. Breathing, sighs and vape puffs between your words are muted so friends don't hear them. It's off unless you turn it on.|Coughs still get through, and the very start of a puff can too."; // shown in "What's new" after updating
    }
}
