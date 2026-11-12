// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.9.0")]
[assembly: AssemblyFileVersion("1.8.9.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.9";
        public const string Notes = "Your mic no longer goes silent when SoundDeck is closed. Windows now uses SoundDeck's mic only while Mic Mix is on, and goes back to your real mic when Mic Mix or SoundDeck is off (new tick box on the Mic Mix page).|The Setup page now adds the Stream Deck plugin itself, with no download.|Safer add-on installs: each installer is locked from the moment it's checked until it finishes.|Lighter in the background."; // shown in "What's new" after updating
    }
}
