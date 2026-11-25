// Outputs and EQ follow your headset by name, like the mic does. Windows sometimes registers a headset again under
// a new ID (after a Windows update, another USB port, a driver update...), and everything saved under the old ID
// (a channel's output, Hear yourself, the EQ) would quietly stop working. The rules are in MicDevices.OutputMoves.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundDeck
{
    partial class MainForm
    {
        // Settings that hold an output's ID (profiles' copies are found by MicDevices.OutputMoves).
        static readonly string[] OutputKeys = { "ch.Game.dev", "ch.Chat.dev", "ch.Media.dev", "ch.Aux.dev", "side.dev", "sb.dev", "mon.dev", "stream.dev", "eq.dev", "out.lastReal" };
        bool apoWriteWaiting;   // EQ moved before the Equalizer APO writer existed (the first device scan at startup)

        // Runs whenever the device list changes (RefreshDevices), before the lists on screen are filled.
        void FollowOutputs()
        {
            var all = Settings.KeysStartingWith("").ToDictionary(k => k, k => Settings.Get(k, ""));
            var now = renderDevs.Select(d => new KeyValuePair<string, string>(d.Id, d.Name)).ToList();
            var changes = MicDevices.OutputMoves(all, now, MicDevices.KnownOutputName, OutputKeys);
            foreach (var kv in changes) Settings.Set(kv.Key, kv.Value);

            if (changes.Keys.Any(k => !k.StartsWith("devname."))) routed.Clear(); // re-route apps to the new IDs
            if (changes.Keys.Any(k => k.StartsWith("eq.{")))
            {
                if (apoDebounce != null) { apoDebounce.Stop(); apoDebounce.Start(); }
                else apoWriteWaiting = true;
            }
        }
    }
}
