// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.6.0")]
[assembly: AssemblyFileVersion("1.9.6.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.6";
        public const string Notes = "Your mic now skips Windows' own mic effects (like Voice Clarity), which were cutting in and out and adding delay. SoundDeck's noise removal does the cleanup instead, and far less background sound reaches your friends.|The Microphone page shows what friends actually hear next to your mic, so you can see noise removal working.|New: Remove echo (beta), for when friends hear your speakers or themselves through your mic. It's off unless you turn it on."; // shown in "What's new" after updating
    }
}
