// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.7.2.0")]
[assembly: AssemblyFileVersion("1.7.2.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.7.2";
        public const string Notes = "A cleaner, softer look: rounded cards and buttons, new sliders and checkboxes, dark scrollbars, and shorter, clearer text on every page."; // shown in "What's new" after updating
    }
}
