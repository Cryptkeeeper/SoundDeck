// Voice processing from the Microphone page (remove low rumble, clarity boost, soften harsh "s", extra boost), run
// inside SoundDeck on your mic before noise removal. Up to 1.8.x these were written into Equalizer APO's config,
// so friends had to install Equalizer APO just for their mic; the filters here are the same ones it ran.
// Settings are shared (VoiceFilters); each audio stream has its own MicFilters state.

using System;

namespace SoundDeck
{
    static class VoiceFilters
    {
        public static volatile bool On;                   // "Turn on voice processing for this mic"
        public static volatile bool LowCut, Clarity, Soften;
        public static volatile int BoostDb;               // Extra boost, 0..12 dB
    }

    class MicFilters
    {
        // What SoundDeck used to write for Equalizer APO: "Filter: ON HP Fc 80 Hz" (Q 0.707),
        // "Filter: ON PK Fc 3500 Hz Gain 4 dB Q 1", "Filter: ON PK Fc 7000 Hz Gain -4 dB Q 2" and "Preamp: <boost> dB".
        readonly Biquad lowCut = Biquad.HighPass(80, Math.Sqrt(0.5));
        readonly Biquad clarity = Biquad.Peak(3500, 4, 1);
        readonly Biquad soften = Biquad.Peak(7000, -4, 2);

        // buf: interleaved stereo, 48 kHz.
        public void Process(float[] buf, int frames)
        {
            bool on = VoiceFilters.On;
            bool lc = on && VoiceFilters.LowCut, cl = on && VoiceFilters.Clarity, sf = on && VoiceFilters.Soften;
            int boost = on ? VoiceFilters.BoostDb : 0;
            lowCut.Use(lc); clarity.Use(cl); soften.Use(sf);
            if (!lc && !cl && !sf && boost <= 0) return;

            double g = boost > 0 ? Math.Pow(10, boost / 20.0) : 1.0;
            for (int i = 0; i < frames; i++)
            {
                double l = buf[i * 2], r = buf[i * 2 + 1];
                if (lc) { l = lowCut.Run(l, 0); r = lowCut.Run(r, 1); }
                if (cl) { l = clarity.Run(l, 0); r = clarity.Run(r, 1); }
                if (sf) { l = soften.Run(l, 0); r = soften.Run(r, 1); }
                buf[i * 2] = (float)(l * g);
                buf[i * 2 + 1] = (float)(r * g);
            }
            lowCut.Flush(); clarity.Flush(); soften.Flush();
        }

        // A standard EQ filter (RBJ "Audio EQ Cookbook", the same formulas Equalizer APO uses), one memory per channel.
        class Biquad
        {
            double b0, b1, b2, a1, a2;
            readonly double[] z1 = new double[2], z2 = new double[2];
            bool inUse;

            public static Biquad HighPass(double hz, double q)
            {
                double w = 2 * Math.PI * hz / MixFormat.Rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
                return new Biquad().Set((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al);
            }

            public static Biquad Peak(double hz, double db, double q)
            {
                double a = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / MixFormat.Rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
                return new Biquad().Set(1 + al * a, -2 * cs, 1 - al * a, 1 + al / a, -2 * cs, 1 - al / a);
            }

            Biquad Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
            {
                b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
                return this;
            }

            // A filter that's switched back on starts from silence, not from where it was left (no click).
            public void Use(bool on)
            {
                if (on && !inUse) { z1[0] = z1[1] = z2[0] = z2[1] = 0; }
                inUse = on;
            }

            public double Run(double x, int ch)
            {
                double y = b0 * x + z1[ch];
                z1[ch] = b1 * x - a1 * y + z2[ch];
                z2[ch] = b2 * x - a2 * y;
                return y;
            }

            // After long digital silence the memory decays toward tiny "denormal" numbers, which are very slow for
            // the CPU. Anything that small is inaudible, so it's set to zero once per block.
            public void Flush()
            {
                for (int c = 0; c < 2; c++)
                {
                    if (Math.Abs(z1[c]) < 1e-20) z1[c] = 0;
                    if (Math.Abs(z2[c]) < 1e-20) z2[c] = 0;
                }
            }
        }
    }
}
