// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.4.0")]
[assembly: AssemblyFileVersion("1.9.4.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.4";
        public const string Notes = "If you skip an update, What's new now lists every version you missed, newest first, instead of only the latest.|What's new shows each change as its own bullet point."; // shown in "What's new" after updating
    }
}
