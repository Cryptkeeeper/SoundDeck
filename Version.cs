// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.5.0")]
[assembly: AssemblyFileVersion("1.8.5.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.5";
        public const string Notes = "New Testing page at the bottom of the menu: a checklist of things that need trying on real setups. Mark each one Works, Broken or Skip, add a note if something's off, then press Copy results and send it over. It's temporary and will go away once testing is done."; // shown in "What's new" after updating
    }
}
