// Set by Installer\publish-update.ps1. Bump it there, not by hand.
using System.Reflection;

[assembly: AssemblyTitle("SoundDeck")]
[assembly: AssemblyProduct("SoundDeck")]
[assembly: AssemblyVersion("1.8.1.0")]
[assembly: AssemblyFileVersion("1.8.1.0")]

namespace SoundDeck
{
    static class AppVersion
    {
        public const string Value = "1.8.1";
        public const string Notes = "SoundDeck now offers to install missing parts it needs (noise removal, Equalizer APO, VB-CABLE, GoXLR Utility) when it checks for updates.\nYou don't need to reinstall the Stream Deck plugin: updates replace it for you."; // shown in "What's new" after updating
    }
}
