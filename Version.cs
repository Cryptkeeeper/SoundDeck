// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.1.0")]
[assembly: AssemblyFileVersion("1.9.1.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.1";
        public const string Notes = "The MIC tag on the Mixer page is now grey while Music in mic is off and turns pink when it's on, so you can tell when friends can hear an app like Chrome.|Hover over an app on the Mixer page to see what its MIC tag means."; // shown in "What's new" after updating
    }
}
