// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.3.0")]
[assembly: AssemblyFileVersion("1.9.3.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.3";
        public const string Notes = "A tidier window: the side menu is grouped into Listen, Talk and Stream, and Setup leaves it once everything's done (Settings > Open Setup brings it back).|The Microphone page folds into sections you open with a click. Noise removal moved there too, with a live bar showing your voice against the background noise; the Mixer's Mic column just says whether it's on.|Settings only shows the GoXLR part while a GoXLR is plugged in."; // shown in "What's new" after updating
    }
}
