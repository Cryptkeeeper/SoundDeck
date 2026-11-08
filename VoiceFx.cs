// Voice effects (reverb, echo, deep/high voice, robot, megaphone) and the bleep, applied to your mic
// after noise removal and the gate/compressor. Settings are shared (VoiceFxSettings); each audio stream
// (friends + stream, and Hear yourself) has its own VoiceFx state.

using System;

namespace SoundDeck
{
    static class VoiceFxSettings
    {
        public static readonly string[] Names = { "Reverb", "Echo", "Deep voice", "High voice", "Robot", "Megaphone" };
        public static volatile bool On;
        public static volatile int Effect;          // index into Names
        public static volatile float Amount = 0.6f; // 0..1, "Strength"
        public static volatile bool Bleep;          // held: your voice is replaced with a beep
    }

    class VoiceFx
    {
        const int Rate = MixFormat.Rate;
        const float Pi2 = (float)(Math.PI * 2);

        float wet;              // 0..1, fades the effect in/out so switching doesn't click
        int effect = -1;
        bool idle = true;
        float bleepGain, bleepPhase;

        // Shared delay line (echo, pitch, robot).
        readonly float[] line = new float[Rate];
        int w;

        // Reverb: 4 damped combs into 2 allpasses (Schroeder / Freeverb style), lengths for 48 kHz.
        static readonly int[] CombLen = { 1695, 1760, 1623, 1548 };
        static readonly int[] ApLen = { 605, 480 };
        readonly float[][] comb = new float[4][];
        readonly float[] combLp = new float[4];
        readonly int[] combPos = new int[4];
        readonly float[][] ap = new float[2][];
        readonly int[] apPos = new int[2];

        float pitchPhase, ringPhase;
        Biquad hp, lp;

        public VoiceFx()
        {
            for (int i = 0; i < 4; i++) comb[i] = new float[CombLen[i]];
            for (int i = 0; i < 2; i++) ap[i] = new float[ApLen[i]];
        }

        void Reset()
        {
            Array.Clear(line, 0, line.Length);
            for (int i = 0; i < 4; i++) { Array.Clear(comb[i], 0, comb[i].Length); combLp[i] = 0; }
            for (int i = 0; i < 2; i++) Array.Clear(ap[i], 0, ap[i].Length);
            pitchPhase = 0; ringPhase = 0;
            hp = Biquad.HighPass(500, 0.7f);
            lp = Biquad.LowPass(3000, 0.9f);
        }

        public void Process(float[] buf, int frames)
        {
            bool on = VoiceFxSettings.On, bleep = VoiceFxSettings.Bleep;
            if (!on && wet <= 0f && !bleep && bleepGain <= 0f) { idle = true; return; }
            int fx = VoiceFxSettings.Effect;
            if (idle || fx != effect) { Reset(); effect = fx; idle = false; }
            float a = Math.Max(0f, Math.Min(1f, VoiceFxSettings.Amount));
            float wetStep = 1f / (Rate * 0.03f), bleepStep = 1f / (Rate * 0.005f);
            float bleepInc = Pi2 * 1000f / Rate;

            for (int i = 0; i < frames; i++)
            {
                float x = (buf[i * 2] + buf[i * 2 + 1]) * 0.5f;
                wet = on ? Math.Min(1f, wet + wetStep) : Math.Max(0f, wet - wetStep);
                float y = x;
                if (wet > 0f) { float e = Effect(x, a); y = x + (e - x) * wet; }
                else Feed(x);

                bleepGain = bleep ? Math.Min(1f, bleepGain + bleepStep) : Math.Max(0f, bleepGain - bleepStep);
                if (bleepGain > 0f)
                {
                    bleepPhase += bleepInc;
                    if (bleepPhase > Pi2) bleepPhase -= Pi2;
                    y = y * (1f - bleepGain) + (float)Math.Sin(bleepPhase) * 0.3f * bleepGain;
                }

                y = y > 1f ? 1f : y < -1f ? -1f : y;
                buf[i * 2] = y; buf[i * 2 + 1] = y;
            }
        }

        // Keeps the delay line current while the effect is faded out (only the bleep is running).
        void Feed(float x)
        {
            line[w] = x;
            if (++w >= line.Length) w = 0;
        }

        float Tap(float delay)
        {
            float pos = w - delay;
            while (pos < 0) pos += line.Length;
            int i0 = (int)pos;
            float f = pos - i0;
            int i1 = i0 + 1 >= line.Length ? 0 : i0 + 1;
            return line[i0] + (line[i1] - line[i0]) * f;
        }

        float Effect(float x, float a)
        {
            switch (effect)
            {
                case 0: return Reverb(x, a);
                case 1: return Echo(x, a);
                case 2: return Pitch(x, 1f - 0.12f - 0.22f * a);  // down to ~0.66
                case 3: return Pitch(x, 1f + 0.18f + 0.42f * a);  // up to ~1.6
                case 4: return Robot(x, a);
                case 5: return Megaphone(x, a);
            }
            Feed(x);
            return x;
        }

        float Reverb(float x, float a)
        {
            Feed(x);
            float fb = 0.78f + 0.12f * a, damp = 0.25f, input = x * 0.12f, sum = 0f;
            for (int k = 0; k < 4; k++)
            {
                var c = comb[k];
                float o = c[combPos[k]];
                combLp[k] = o * (1 - damp) + combLp[k] * damp;
                c[combPos[k]] = input + combLp[k] * fb;
                if (++combPos[k] >= c.Length) combPos[k] = 0;
                sum += o;
            }
            for (int k = 0; k < 2; k++)
            {
                var b = ap[k];
                float o = b[apPos[k]];
                float v = sum + o * 0.5f;
                b[apPos[k]] = v;
                sum = o - v * 0.5f;
                if (++apPos[k] >= b.Length) apPos[k] = 0;
            }
            return x + sum * (0.6f + 0.9f * a);
        }

        float Echo(float x, float a)
        {
            float d = Tap(Rate * 0.32f);
            float fb = 0.3f + 0.3f * a;
            line[w] = x + d * fb;
            if (++w >= line.Length) w = 0;
            return x + d * (0.45f + 0.25f * a);
        }

        // Delay-line pitch shifter: two read heads sweep through a 50 ms window, crossfaded so the jumps aren't heard.
        float Pitch(float x, float ratio)
        {
            Feed(x);
            const float Win = Rate * 0.05f;
            pitchPhase += (1f - ratio) / Win;
            if (pitchPhase >= 1f) pitchPhase -= 1f; else if (pitchPhase < 0f) pitchPhase += 1f;
            float p2 = pitchPhase + 0.5f; if (p2 >= 1f) p2 -= 1f;
            // Equal-power crossfade (sin^2 + cos^2 = 1), so the level stays the same as your normal voice.
            float g1 = (float)Math.Sin(Math.PI * pitchPhase), g2 = (float)Math.Sin(Math.PI * p2);
            // The two heads partly cancel each other, so add back about 3 dB.
            return (Tap(2 + pitchPhase * Win) * g1 + Tap(2 + p2 * Win) * g2) * 1.4f;
        }

        // Ring modulation plus a short metallic comb.
        float Robot(float x, float a)
        {
            ringPhase += Pi2 * (45f + 50f * a) / Rate;
            if (ringPhase > Pi2) ringPhase -= Pi2;
            float r = x * (float)Math.Sin(ringPhase) * 1.3f;
            float c = r + Tap(Rate * 0.007f) * 0.45f;
            line[w] = c;
            if (++w >= line.Length) w = 0;
            return c * 0.6f;
        }

        // Thin, distorted "through a speaker" sound.
        float Megaphone(float x, float a)
        {
            Feed(x);
            float v = lp.Run(hp.Run(x));
            float drive = 3f + 9f * a;
            return (float)Math.Tanh(v * drive) * 0.6f;
        }

        // RBJ cookbook biquad.
        struct Biquad
        {
            float b0, b1, b2, a1, a2, z1, z2;

            public float Run(float x)
            {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }

            static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2)
            {
                return new Biquad { b0 = (float)(b0 / a0), b1 = (float)(b1 / a0), b2 = (float)(b2 / a0), a1 = (float)(a1 / a0), a2 = (float)(a2 / a0) };
            }

            public static Biquad HighPass(float f, float q)
            {
                double w0 = 2 * Math.PI * f / Rate, c = Math.Cos(w0), al = Math.Sin(w0) / (2 * q);
                return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
            }

            public static Biquad LowPass(float f, float q)
            {
                double w0 = 2 * Math.PI * f / Rate, c = Math.Cos(w0), al = Math.Sin(w0) / (2 * q);
                return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
            }
        }
    }
}
