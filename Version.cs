// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.3.0")]
[assembly: AssemblyFileVersion("1.8.3.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.3";
        public const string Notes = "Safer: websites can no longer send commands to SoundDeck, and settings survive a crash or power cut.\nThe voice effects hotkey is no longer set by default (it blocked Ctrl+Alt+V in other apps). Pick one on the Effects page if you want it.\nSettings > Apps now shows the right SoundDeck version."; // shown in "What's new" after updating
    }
}
