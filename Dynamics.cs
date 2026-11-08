// Noise gate + compressor for the mic, run inside SoundDeck after noise removal.
// Settings are shared (VoiceDynamics); each audio stream has its own Dynamics state.

using System;

namespace SoundDeck
{
    static class VoiceDynamics
    {
        public static volatile bool GateOn, CompOn;
        public static volatile float GateThresholdDb = -50f;  // below this the mic is silenced
        public static volatile float GateReleaseMs = 200f;    // how fast it closes after you stop talking
        public static volatile float CompThresholdDb = -20f;  // above this, loud sounds get turned down
        public static volatile float CompRatio = 4f;          // 4 = every 4 dB over the threshold becomes 1 dB
        public static volatile float CompMakeupDb = 6f;       // boost after compressing, so you're not quieter overall

        // Live readouts from the chat-mic stream, for the Microphone page.
        public static volatile bool GateOpen = true;
        public static volatile float GainReductionDb;
    }

    class Dynamics
    {
        const float Rate = MixFormat.Rate;
        float gateEnv, gateGain = 1f, compEnv, compGainDb;
        int holdLeft;
        readonly bool report;

        // report: this stream updates the live readouts (only the chat mic does, so they don't flicker).
        public Dynamics(bool report) { this.report = report; }

        static float Coef(float ms) { return (float)Math.Exp(-1.0 / (Rate * ms / 1000.0)); }
        static float Db(float lin) { return lin <= 1e-9f ? -180f : (float)(20 * Math.Log10(lin)); }
        static float Lin(float db) { return (float)Math.Pow(10, db / 20.0); }

        public void Process(float[] buf, int frames)
        {
            bool gate = VoiceDynamics.GateOn, comp = VoiceDynamics.CompOn;
            if (!gate && !comp) { gateGain = 1f; compGainDb = 0f; if (report) { VoiceDynamics.GateOpen = true; VoiceDynamics.GainReductionDb = 0f; } return; }

            float openLin = Lin(VoiceDynamics.GateThresholdDb), closeLin = Lin(VoiceDynamics.GateThresholdDb - 4f); // 4 dB hysteresis
            // "Closes after" = time for the gate to fade 40 dB (practically silent), not just its time constant.
            float envRel = Coef(50f), gAtk = Coef(1.5f);
            float gRel = (float)Math.Exp(Math.Log(0.01) / (Rate * Math.Max(20f, VoiceDynamics.GateReleaseMs) / 1000.0));
            int hold = (int)(Rate * 0.08f); // stay open 80 ms after the level drops, so word endings aren't cut
            float cThr = VoiceDynamics.CompThresholdDb, ratio = Math.Max(1f, VoiceDynamics.CompRatio), makeup = VoiceDynamics.CompMakeupDb;
            float cAtk = Coef(8f), cRel = Coef(150f);
            float maxGr = 0f;
            bool open = gateGain > 0.5f;

            for (int i = 0; i < frames; i++)
            {
                float l = buf[i * 2], r = buf[i * 2 + 1];
                float a = Math.Max(Math.Abs(l), Math.Abs(r));
                float g = 1f;

                if (gate)
                {
                    gateEnv = a > gateEnv ? a : gateEnv * envRel;
                    bool wantOpen = gateEnv > openLin || (gateGain > 0.5f && gateEnv > closeLin);
                    if (wantOpen) holdLeft = hold;
                    else if (holdLeft > 0) { holdLeft--; wantOpen = true; }
                    float target = wantOpen ? 1f : 0f;
                    gateGain = target > gateGain ? target + (gateGain - target) * gAtk : target + (gateGain - target) * gRel;
                    g *= gateGain;
                    open = gateGain > 0.5f;
                }
                else gateGain = 1f;

                if (comp)
                {
                    float x = a * g;
                    compEnv = x > compEnv ? x + (compEnv - x) * cAtk : x + (compEnv - x) * cRel;
                    float lvl = Db(compEnv);
                    float want = lvl > cThr ? (cThr + (lvl - cThr) / ratio) - lvl : 0f; // negative = turn down
                    compGainDb = want < compGainDb ? want + (compGainDb - want) * cAtk : want + (compGainDb - want) * cRel;
                    if (-compGainDb > maxGr) maxGr = -compGainDb;
                    g *= Lin(compGainDb + makeup);
                }

                l *= g; r *= g;
                buf[i * 2] = l > 1f ? 1f : l < -1f ? -1f : l;
                buf[i * 2 + 1] = r > 1f ? 1f : r < -1f ? -1f : r;
            }

            if (report) { VoiceDynamics.GateOpen = !gate || open; VoiceDynamics.GainReductionDb = comp ? maxGr : 0f; }
        }
    }
}
