// "Mute breathing and vape puffs" on the Microphone page. A voice has a pitch: its sound repeats, which shows up as
// evenly spaced harmonics (a peak in the "cepstrum"). A breath or a vape puff is air noise with no such pattern.
// Sound without a voice in it is let through only briefly (an "s", "f" or "h" at the start of a word, 150 ms) and
// for a while after you've spoken (word endings, 800 ms); a puff that goes on longer is muted. It watches the mic
// before noise removal (so a puff that noise removal lets through halfway is already known to be a puff) and turns
// it down after. Tuned on two recordings of the user's own puffs and voice (Arctis Pro Wireless, raw mic): it cut
// ~75-80% of the puff sound noise removal let through, and no clearly voiced speech. Coughs get through: they're
// voiced, and look too much like a laugh or a loud word to mute safely.
// Settings are shared (BreathFilter); each audio stream has its own Breath state.

using System;

namespace SoundDeck
{
    static class BreathFilter
    {
        public static volatile bool On;
        public static volatile int Mutes;   // how many times it has muted something since SoundDeck started (chat mic only)
        public static volatile bool Muting; // muting right now (chat mic only)
    }

    class Breath
    {
        const int Rate = MixFormat.Rate;
        const int Hop = 480;                                   // decides every 10 ms
        const int N = 2048;                                    // 43 ms spectrum for the harmonics test
        const int CN = 512;                                    // cepstrum from the 0..6 kHz part of it
        const int Dec = 4, Hist = 540, Win = 360, MinLag = 30, MaxLag = 171; // pitch search at 12 kHz: 70..400 Hz

        // Tuning (static so tests can try other values).
        public static double CepMin = 3.5;      // harmonics this clear = a voice
        public static double PitchMin = 0.8;    // or two frames in a row this periodic, with little hiss and some harmonics
        public static int OnsetHops = 15;       // sound without a voice allowed before a voice starts (150 ms)
        public static int HoldHops = 80;        // ...and after a voice (800 ms), for word endings, pauses and breathy laughs
        public static int QuietHops = 15;       // 150 ms of quiet ends a puff
        public static double OverFloorDb = 12;  // "sound" = this much louder than the room's background
        public static double MinSoundDb = -70;

        readonly bool report;
        readonly Bq hpP = Bq.HighPass(80), lp1 = Bq.LowPass(1000), lp2 = Bq.LowPass(1000), hpH = Bq.HighPass(2000);
        readonly double[] ring = new double[Hist], lin = new double[Hist], r = new double[MaxLag + 2];
        readonly double[] wave = new double[N], win = new double[N], re = new double[N], im = new double[N], logp = new double[N / 2 + 1];
        readonly double[] cre = new double[CN], cim = new double[CN];
        int ringPos, decPhase, hopPos, wavePos;
        double hopE, hopH, floorDb;
        int sinceVoice, unvoiced, quiet, pitchRun;
        bool allow, pass, wasOn;
        float gain;
        float[] gains = new float[4800];
        int gainCount;

        // What the last 10 ms looked like (for tests).
        public double LastLevel, LastCep, LastPitch, LastHiss;
        public bool LastSound, LastVoiced, LastPass;

        // report: this stream updates the live readouts (only the chat mic does).
        public Breath(bool report)
        {
            this.report = report;
            for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            Reset();
        }

        void Reset()
        {
            Array.Clear(ring, 0, Hist); Array.Clear(wave, 0, N);
            ringPos = decPhase = hopPos = wavePos = 0; hopE = hopH = 0;
            hpP.Clear(); lp1.Clear(); lp2.Clear(); hpH.Clear();
            floorDb = -60; sinceVoice = 10000; unvoiced = 0; quiet = 10000; pitchRun = 0;
            allow = pass = true; gain = 1f;
        }

        // Call with the mic before noise removal. buf: interleaved stereo, 48 kHz.
        public void Analyze(float[] buf, int frames)
        {
            gainCount = 0;
            if (!BreathFilter.On) { wasOn = false; if (report) BreathFilter.Muting = false; return; }
            if (!wasOn) { Reset(); wasOn = true; }
            if (gains.Length < frames) gains = new float[frames];
            const float atk = 0.0069f;  // ~3 ms to open
            const float rel = 0.0014f;  // ~15 ms to fade out
            for (int i = 0; i < frames; i++)
            {
                double x = (buf[i * 2] + buf[i * 2 + 1]) * 0.5;
                double h = hpH.Run(x);
                hopE += x * x; hopH += h * h;
                wave[wavePos] = x; if (++wavePos == N) wavePos = 0;
                double p = lp2.Run(lp1.Run(hpP.Run(x)));
                if (++decPhase == Dec) { decPhase = 0; ring[ringPos] = p; if (++ringPos == Hist) ringPos = 0; }
                if (++hopPos == Hop) { hopPos = 0; Decide(); }
                float target = pass ? 1f : 0f;
                gain += (target - gain) * (target > gain ? atk : rel);
                gains[i] = gain;
            }
            gainCount = frames;
            hpP.Flush(); lp1.Flush(); lp2.Flush(); hpH.Flush();
        }

        // Call with the same block after noise removal.
        public void Apply(float[] buf, int frames)
        {
            if (gainCount != frames) return;
            for (int i = 0; i < frames; i++) { buf[i * 2] *= gains[i]; buf[i * 2 + 1] *= gains[i]; }
        }

        void Decide()
        {
            double lvl = 10 * Math.Log10(hopE / Hop + 1e-14), hiss = hopH / (hopE + 1e-20);
            hopE = hopH = 0;
            // The room's background: follows quiet moments quickly, loud ones only slowly (0.5 dB a second).
            if (lvl < floorDb) floorDb += (lvl - floorDb) * 0.3; else floorDb += 0.005;
            if (floorDb < -100) floorDb = -100;

            bool sound = lvl > floorDb + OverFloorDb && lvl > MinSoundDb;
            double cep = 0, pitch = 0;
            if (sound) { cep = Cepstrum(); pitch = Pitch(); }
            bool pitched = sound && pitch >= PitchMin && hiss <= 0.3 && cep >= 2.0;
            pitchRun = pitched ? pitchRun + 1 : 0;
            bool voiced = sound && (cep >= CepMin || pitchRun >= 2);
            bool before = pass;
            if (voiced) { sinceVoice = 0; unvoiced = 0; quiet = 0; allow = true; }
            else
            {
                if (sinceVoice < 10000) sinceVoice++;
                if (sound) { quiet = 0; unvoiced++; allow = sinceVoice <= HoldHops || unvoiced <= OnsetHops; }
                else if (++quiet >= QuietHops) { quiet = QuietHops; unvoiced = 0; allow = true; }
                // a short gap inside a puff keeps it muted
            }
            // The first moment of a vowel can come before the harmonics test is sure: a clearly pitched frame gets through.
            pass = allow || (sound && pitch >= PitchMin && hiss <= 0.35);
            if (report)
            {
                if (before && !pass) BreathFilter.Mutes++;
                BreathFilter.Muting = !pass;
            }
            LastLevel = lvl; LastCep = cep; LastPitch = pitch; LastHiss = hiss;
            LastSound = sound; LastVoiced = voiced; LastPass = pass;
        }

        // How clearly the last 43 ms has evenly spaced harmonics (cepstral peak prominence over 0..6 kHz, pitch 70..400 Hz).
        double Cepstrum()
        {
            for (int i = 0; i < N; i++) { re[i] = wave[(wavePos + i) % N] * win[i]; im[i] = 0; }
            Fft(re, im, false);
            for (int k = 0; k <= CN / 2; k++) logp[k] = Math.Log(re[k] * re[k] + im[k] * im[k] + 1e-18);
            for (int k = 0; k <= CN / 2; k++) { cre[k] = logp[k]; cim[k] = 0; }
            for (int k = 1; k < CN / 2; k++) { cre[CN - k] = cre[k]; cim[CN - k] = 0; }
            Fft(cre, cim, true);
            const int fs = Rate / 4; // the 0..6 kHz half-spectrum is a 12 kHz signal's
            int q0 = (int)(0.0025 * fs), q1 = (int)(0.014 * fs);
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int m = 0;
            for (int q = q0; q <= q1; q++) { double y = cre[q]; sx += q; sy += y; sxx += q * (double)q; sxy += q * y; m++; }
            double slope = (m * sxy - sx * sy) / (m * sxx - sx * sx), icp = (sy - slope * sx) / m;
            double best = -1e9;
            for (int q = q0; q <= q1; q++) { double v = cre[q] - (icp + slope * q); if (v > best) best = v; }
            return best * 8.686;
        }

        // How strongly the last 30 ms repeats at a voice pitch (peaks only): 1 = a clean tone, near 0 = hiss.
        double Pitch()
        {
            for (int j = 0; j < Hist; j++) lin[j] = ring[(ringPos + j) % Hist];
            int s0 = Hist - Win;
            double e0 = 0; for (int i = 0; i < Win; i++) e0 += lin[s0 + i] * lin[s0 + i];
            for (int k = MinLag - 1; k <= MaxLag + 1; k++)
            {
                double num = 0, ek = 0; int o = s0 - k;
                for (int i = 0; i < Win; i++) { double b = lin[o + i]; num += lin[s0 + i] * b; ek += b * b; }
                r[k] = num / Math.Sqrt(e0 * ek + 1e-20);
            }
            double best = 0;
            for (int k = MinLag; k <= MaxLag; k++) if (r[k] >= r[k - 1] && r[k] >= r[k + 1] && r[k] > best) best = r[k];
            return best;
        }

        static void Fft(double[] re, double[] im, bool inv)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inv ? 1 : -1), wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, c = a + len / 2;
                        double xr = re[c] * cr - im[c] * ci, xi = re[c] * ci + im[c] * cr;
                        re[c] = re[a] - xr; im[c] = im[a] - xi; re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                    }
                }
            }
            if (inv) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }

        class Bq
        {
            double b0, b1, b2, a1, a2, z1, z2;
            public static Bq LowPass(double hz) { double w = 2 * Math.PI * hz / Rate, cs = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2); return Make((1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + al, -2 * cs, 1 - al); }
            public static Bq HighPass(double hz) { double w = 2 * Math.PI * hz / Rate, cs = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2); return Make((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al); }
            static Bq Make(double nb0, double nb1, double nb2, double na0, double na1, double na2) { var b = new Bq(); b.b0 = nb0 / na0; b.b1 = nb1 / na0; b.b2 = nb2 / na0; b.a1 = na1 / na0; b.a2 = na2 / na0; return b; }
            public double Run(double x) { double y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
            public void Clear() { z1 = z2 = 0; }
            public void Flush() { if (Math.Abs(z1) < 1e-20) z1 = 0; if (Math.Abs(z2) < 1e-20) z2 = 0; }
        }
    }
}
