// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.9.0.0")]
[assembly: AssemblyFileVersion("1.9.0.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.9.0";
        public const string Notes = "Your mic's voice processing (remove rumble, clarity boost, soften harsh s, extra boost) now runs inside SoundDeck, so Equalizer APO is only needed for the Equalizer page.|New installs include noise removal, so setup no longer downloads 36 MB.|Your mic now follows your headset if Windows gives it a new ID (for example after plugging it into another USB port), and SoundDeck never picks Stereo Mix as your mic.|The old noise suppression plugin option is gone: Remove noise on the Mixer does the same with nothing to set up."; // shown in "What's new" after updating
    }
}
