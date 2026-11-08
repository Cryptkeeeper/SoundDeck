// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.7.1.0")]
[assembly: AssemblyFileVersion("1.7.1.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.7.1";
        public const string Notes = "Headphone tuning now detects your headphones: on the Equalizer page, click Use it next to the detected model."; // shown in "What's new" after updating
    }
}
