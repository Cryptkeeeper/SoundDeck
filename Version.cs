// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.2.0")]
[assembly: AssemblyFileVersion("1.9.2.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.2";
        public const string Notes = "Your mic is found again even when Windows forgets your headset's old ID (for example after a Windows update or plugging it into another USB port): SoundDeck now remembers the mic's name too."; // shown in "What's new" after updating
    }
}
