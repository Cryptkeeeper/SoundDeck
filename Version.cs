// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.8.0")]
[assembly: AssemblyFileVersion("1.8.8.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.8";
        public const string Notes = "New Setup page (top of the menu): a checklist of everything SoundDeck needs. Each step ticks itself off when it's done, and its button does it for you.|One click makes the virtual mic your Windows mic, so Harmony and games set to Default just work."; // shown in "What's new" after updating
    }
}
