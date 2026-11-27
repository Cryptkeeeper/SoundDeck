// Echo removal: takes the sound playing on your outputs (speakers, headset) back out of your mic, so friends don't
// hear themselves, your game or your videos through it. Noise removal can't do this: that sound is often voices,
// which noise removal is built to keep. Runs in Mic Mix, first thing on the mic.
//
// How it works: each output's sound is captured as Windows plays it ("loopback"), lined up with the mic sample by
// sample (EchoRef), and an adaptive filter per output (EchoPath) learns how that sound reaches the mic (its delay,
// volume and the tone of the room or earcup) and subtracts it. The filter is a partitioned-block frequency-domain
// NLMS filter (overlap-save, 512-sample blocks, 300 ms long). It runs in two copies: the "background" one keeps
// learning, and its result is used (copied to the "foreground") only once it clearly does better. Your own talking
// confuses the learning, so it can make the background worse for a moment but never what friends hear.
// Everything here runs on Mic Mix's audio thread.

using System;
using System.Collections.Generic;

namespace SoundDeck
{
    static class EchoRemoval
    {
        public static volatile bool On;
        public static volatile bool Active;     // something is playing on an output, and it's being taken out
        public static volatile float CutDb;     // how much quieter the mic got while that was happening (rough, live)
        public static volatile int Restarts;    // times the safety check started the filters over (for checking)
        public static volatile bool Talking;    // it thinks you're talking (for checking)
    }

    // One output's sound, lined up with the mic sample for sample. The mic and each output deliver in packets that
    // arrive at their own moments, so echo removal waits for an output's packet rather than guessing: the n-th mic
    // sample after lining up always meets the n-th sample of the output (plus slow clock-drift nudges).
    class EchoRef : IDisposable
    {
        public readonly string DeviceId;
        readonly CaptureStream cap;
        readonly Fifo testFifo;
        const int Nudge = MixFormat.Rate * 5 / 1000;  // how far the line-up may wander before the clocks are evened out
        internal bool Primed;                         // lined up with the mic and delivering
        internal bool Realigned;                      // just lined up afresh: the filter for it starts over
        internal int SinceData;                       // mic samples since this output last delivered anything
        internal int hold;                            // samples to repeat (output's clock running slow)
        internal float last;
        int seen;                                     // samples in the queue after the last read
        int settle, sinceNudge;
        double avg, baseline;
        readonly float[] st = new float[MixFormat.Rate * 2];
        public int Nudges, Primes, Stops;             // for checking
        public int Waiting { get { return Fifo.Count / 2; } }
        public double Baseline { get { return baseline; } }
        internal EchoPath Path;

        public EchoRef(IMMDeviceEnumerator en, string id) { DeviceId = id; cap = CaptureStream.OpenLoopback(en, id); }
        public EchoRef(string id, Fifo feed) { DeviceId = id; testFifo = feed; } // tests feed it directly

        Fifo Fifo { get { return cap != null ? cap.Fifo : testFifo; } }
        internal int Available { get { return Fifo.Count / 2 + hold; } }

        // Collects what the output played since last time (tests write into their queue, then call this too).
        public void Pump()
        {
            if (cap != null) cap.Pump();
            if (Fifo.Count > seen) { SinceData = 0; Received += (Fifo.Count - seen) / 2; }
            seen = Fifo.Count;
        }

        public void Dispose() { if (cap != null) cap.Dispose(); }

        // Since lining up: samples this output delivered, and mic samples that arrived. Their difference only
        // changes with packet timing (back and forth) and clock drift (steadily).
        internal long Received, MicSeen;

        // Lines up afresh: keeps only the newest `keep` samples, which go with the `micWaiting` mic samples now waiting.
        internal void Prime(int keep, int micWaiting)
        {
            int avail = Fifo.Count / 2;
            if (avail > keep) Fifo.Discard((avail - keep) * 2);
            seen = Fifo.Count;
            Received = Fifo.Count / 2; MicSeen = micWaiting;
            Primed = true; Realigned = true; hold = 0; settle = 0; sinceNudge = 0; SinceData = 0; Primes++;
        }

        internal void Stop() { Primed = false; Stops++; Fifo.Discard(Fifo.Count); seen = 0; hold = 0; }

        // n mono samples (n <= Available).
        internal void Read(float[] dst, int off, int n)
        {
            int i = 0;
            while (i < n && hold > 0) { dst[off + i++] = last; hold--; }
            int have = Math.Min(n - i, Fifo.Count / 2);
            if (have > 0)
            {
                Array.Clear(st, 0, have * 2);
                Fifo.ReadAdd(st, have * 2, 1f);
                for (int k = 0; k < have; k++) dst[off + i + k] = (st[k * 2] + st[k * 2 + 1]) * 0.5f;
                i += have;
                last = dst[off + i - 1];
            }
            if (i < n) Array.Clear(dst, off + i, n - i);
            seen = Fifo.Count;
        }

        // After each round: this output's clock and the mic's never run at exactly the same speed. lead = how far
        // the output is ahead of the mic since lining up. It settles in the first second; after that, a lead that
        // grows or shrinks is evened out one sample at a time (at most one nudge every 10 rounds, which still covers
        // 200 ppm). The filter follows a one-sample step without noticing.
        internal void Drift()
        {
            double lead = Received - MicSeen;
            avg += (lead - avg) * (settle < 100 ? 0.05 : 0.01);
            if (settle < 100) { if (++settle == 100) baseline = avg; return; }
            if (++sinceNudge < 10) return;
            if (avg > baseline + Nudge && Fifo.Count >= 2) { Fifo.Discard(2); seen = Fifo.Count; Received--; avg -= 1; Nudges++; sinceNudge = 0; }
            else if (avg < baseline - Nudge) { hold++; Received++; avg += 1; Nudges++; sinceNudge = 0; }
        }
    }

    // The filter for one output: how its sound gets into the mic.
    class EchoPath
    {
        public readonly float[][] Xr, Xi, Br, Bi, Fr, Fi; // input blocks' spectra (newest first, a ring) and both filters
        public readonly float[] Prev = new float[EchoCanceller.N], In = new float[EchoCanceller.N];
        public readonly float[] G = new float[EchoCanceller.Parts];  // how much of the learning each piece gets
        public readonly float[] Yb = new float[EchoCanceller.N], Yf = new float[EchoCanceller.N], Er = new float[EchoCanceller.N];
        public double AvgB, Sb, ErrPow;                             // its background filter's error (smoothed, this block)
        public double LongB, LongWithout;                           // over ~1/3 s: background error; mic without its echo
        public double SecB; public int SecAge;                      // background error over ~1 s, and how long it's been kept
        public int WorseRun;
        public int Head, Silent = int.MaxValue / 2;
        public volatile int PeakMs = -1;  // for checking
        public bool Busy { get { return Silent <= EchoCanceller.Parts; } }

        public EchoPath()
        {
            int p = EchoCanceller.Parts, b = EchoCanceller.Bins;
            Xr = Jag(p, b); Xi = Jag(p, b); Br = Jag(p, b); Bi = Jag(p, b); Fr = Jag(p, b); Fi = Jag(p, b);
        }

        static float[][] Jag(int a, int b) { var r = new float[a][]; for (int i = 0; i < a; i++) r[i] = new float[b]; return r; }

        public void Reset()
        {
            for (int p = 0; p < EchoCanceller.Parts; p++)
            {
                Array.Clear(Xr[p], 0, Xr[p].Length); Array.Clear(Xi[p], 0, Xi[p].Length);
                Array.Clear(Br[p], 0, Br[p].Length); Array.Clear(Bi[p], 0, Bi[p].Length);
                Array.Clear(Fr[p], 0, Fr[p].Length); Array.Clear(Fi[p], 0, Fi[p].Length);
            }
            Array.Clear(Prev, 0, Prev.Length);
            Silent = int.MaxValue / 2;
            AvgB = Sb = ErrPow = LongB = LongWithout = SecB = 0; WorseRun = 0; SecAge = 0;
        }

        // The ring slot p blocks back from the newest.
        public int Slot(int p) { int s = Head - p; return s < 0 ? s + EchoCanceller.Parts : s; }
    }

    class EchoCanceller
    {
        public const int N = 512, M = 2 * N, Bins = N + 1;
        public const int Parts = 28;              // 28 blocks of 10.7 ms: echo up to 300 ms after the sound played
        public static float Mu = 0.8f;            // how fast the background filter learns (normalized, 0..2)
        public static float Floor = 1e-3f;        // learning is slowed in bins this far under the average power
        public static bool Safety = true, TwoFilters = true;
        public static int ConstrainEach = 1;
        public static float ErrFloor = 1f;
        public static double TalkRatio = 4;      // mic this many times the expected echo (6 dB) = you're talking
        public static double Alpha = -0.5;       // -1 = every piece learns the same; towards 1 = mostly where echo is

        readonly float[] mic = new float[N];
        int inCount;
        int blockNo;

        // Foreground / background bookkeeping (smoothed energies of the mic, and of the mic after each filter).
        double avgD, avgF, avgB, slowD, slowF, errPow, erD, erF, hiss;
        bool adapted;          // the filter has learned the echo at least once (talk detection only works after)
        int talkHold, talkRun;
        public bool Talking;   // for tests
        double cutNum, cutDen;
        public int Copies, BackgroundResets, SafetyResets, OutputWipes; // for tests
        double longF, secF;
        public double BgErr, FgErr, MicIn;                 // for tests: summed energies while something played

        readonly float[] re = new float[M], im = new float[M], yb = new float[N], yf = new float[N];
        readonly float[] ebR = new float[Bins], ebI = new float[Bins];
        readonly float[] sumBr = new float[Bins], sumBi = new float[Bins], sumFr = new float[Bins], sumFi = new float[Bins];
        readonly float[] norm = new float[Bins];
        readonly float[] eb = new float[N], ef = new float[N];

        public EchoCanceller() { Restart(); }

        // Mic samples (mono) waiting for the outputs' sound that goes with them.
        float[] pend = new float[MixFormat.Rate];
        int pendCount, pushed;
        const int MaxWait = MixFormat.Rate * 60 / 1000; // an output that sent nothing for 60 ms has stopped playing
        Fifo sink;
        readonly float[] stOut = new float[N * 2];

        public void Restart()
        {
            inCount = 0; pendCount = 0; pushed = 0;
            avgD = avgF = avgB = slowD = slowF = errPow = erD = erF = hiss = longF = secF = 0; cutNum = cutDen = 0;
            adapted = false; talkHold = talkRun = 0;
            testOut = null;
        }

        // New mic sound (interleaved stereo, as it arrived).
        public void Push(float[] stereo, int frames)
        {
            if (pendCount + frames > pend.Length)
            {
                var bigger = new float[Math.Max(pend.Length * 2, pendCount + frames)];
                Array.Copy(pend, bigger, pendCount); pend = bigger;
            }
            for (int i = 0; i < frames; i++) pend[pendCount + i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
            pendCount += frames; pushed += frames;
        }

        // Cleans as much of the waiting mic sound as every playing output has caught up with, and adds it (stereo,
        // in 10.7 ms blocks) to outp. Call after pumping the outputs.
        public void Run(IList<EchoRef> refs, Fifo outp)
        {
            sink = outp;
            foreach (var r in refs)
            {
                if (r.Path == null) r.Path = new EchoPath();
                r.SinceData += pushed;
                if (r.Primed) r.MicSeen += pushed;
                if (r.Primed && r.SinceData > MaxWait) r.Stop(); // nothing playing any more: no waiting for it
                // Lines up when it starts delivering. Its newest sound goes with the mic sound now waiting, plus 5 ms
                // more of it, so even a setup where the mic hears the sound almost at once still works.
                if (!r.Primed && r.Available > 0 && r.SinceData <= pushed) r.Prime(Math.Min(r.Available, pendCount + MixFormat.Rate / 200), pendCount);
                if (r.Realigned) { r.Path.Reset(); r.Realigned = false; adapted = false; }
            }
            pushed = 0;

            int k = pendCount;
            foreach (var r in refs) if (r.Primed) k = Math.Min(k, r.Available);
            // The mic never waits longer than that for an output: one that's behind by more has stalled.
            if (pendCount - k > MaxWait)
            {
                foreach (var r in refs) if (r.Primed && r.Available < pendCount - MaxWait) r.Stop();
                k = pendCount;
                foreach (var r in refs) if (r.Primed) k = Math.Min(k, r.Available);
            }

            int pos = 0;
            while (pos < k)
            {
                int take = Math.Min(N - inCount, k - pos);
                Array.Copy(pend, pos, mic, inCount, take);
                foreach (var r in refs)
                {
                    if (r.Primed) r.Read(r.Path.In, inCount, take);
                    else Array.Clear(r.Path.In, inCount, take);
                }
                inCount += take; pos += take;
                if (inCount == N) { Block(refs); inCount = 0; }
            }
            if (k > 0) { Array.Copy(pend, k, pend, 0, pendCount - k); pendCount -= k; }
            foreach (var r in refs) if (r.Primed) r.Drift();
        }

        // For tests: the old one-call form. Cleans stereo in place, with a fixed delay of TestDelay samples (so the
        // wait for the outputs' packets never shows), and pumps the test outputs itself.
        public const int TestDelay = N + 2048;
        Fifo testOut;
        public void Process(float[] stereo, int frames, int real, IList<EchoRef> refs)
        {
            if (testOut == null) { testOut = new Fifo(); testOut.Write(new float[TestDelay * 2], TestDelay * 2); }
            foreach (var r in refs) r.Pump();
            Push(stereo, frames);
            Run(refs, testOut);
            Array.Clear(stereo, 0, frames * 2);
            testOut.ReadAdd(stereo, frames * 2, 1f);
        }

        void Emit(float[] block)
        {
            for (int i = 0; i < N; i++) { stOut[i * 2] = block[i]; stOut[i * 2 + 1] = block[i]; }
            sink.Write(stOut, N * 2);
        }

        void Block(IList<EchoRef> refs)
        {
            blockNo++;
            bool any = false;
            foreach (var r in refs)
            {
                var p = r.Path;
                double e = 0;
                for (int i = 0; i < N; i++) e += p.In[i] * (double)p.In[i];
                p.Silent = e > 1e-12 ? 0 : Math.Min(p.Silent + 1, 1 << 20);
                if (p.Busy) { any = true; AddInput(p); }
                Array.Copy(p.In, p.Prev, N);
            }
            double sd = 0;
            for (int i = 0; i < N; i++) sd += mic[i] * (double)mic[i];
            if (!any)
            {
                EchoRemoval.Active = false;
                Emit(mic);
                return;
            }

            // Each output on its own: what its background and foreground filters think its echo is. Each output also
            // learns and is judged against the mic minus the other outputs' foreground echo, so an output that never
            // reaches the mic (headset sound that doesn't leak) can't drag the others down or add noise of its own.
            Array.Clear(yf, 0, N);
            foreach (var r in refs)
            {
                var p = r.Path;
                if (!p.Busy) continue;
                Array.Clear(sumBr, 0, Bins); Array.Clear(sumBi, 0, Bins); Array.Clear(sumFr, 0, Bins); Array.Clear(sumFi, 0, Bins);
                for (int k = 0; k < Parts; k++)
                {
                    int s = p.Slot(k);
                    float[] xr = p.Xr[s], xi = p.Xi[s], br = p.Br[k], bi = p.Bi[k], fr = p.Fr[k], fi = p.Fi[k];
                    double we = 0;
                    for (int f = 0; f < Bins; f++)
                    {
                        float a = xr[f], b = xi[f];
                        sumBr[f] += br[f] * a - bi[f] * b; sumBi[f] += br[f] * b + bi[f] * a;
                        sumFr[f] += fr[f] * a - fi[f] * b; sumFi[f] += fr[f] * b + fi[f] * a;
                        we += br[f] * br[f] + bi[f] * bi[f];
                    }
                    p.G[k] = (float)Math.Sqrt(we);
                }
                LastHalf(sumBr, sumBi, p.Yb);
                LastHalf(sumFr, sumFi, p.Yf);
                for (int i = 0; i < N; i++) yf[i] += p.Yf[i];
            }

            double sf = 0, syf = 0;
            for (int i = 0; i < N; i++)
            {
                ef[i] = mic[i] - yf[i];
                sf += ef[i] * (double)ef[i]; syf += yf[i] * (double)yf[i];
            }

            // Are you talking? Once the filter knows the echo, the mic being well above the echo it expects means
            // there's something else in it: your voice. Learning then would teach the filter your voice, so it waits
            // (a little past the end of each word, too). If that lasts very long it's more likely the echo itself
            // changed (headset moved), so it learns anyway; the two-filter check still protects what friends hear.
            // The mic's own background hiss (quickest to follow it down, slow to rise) doesn't count as talking.
            if (sd < hiss || hiss <= 0) hiss = hiss <= 0 ? sd : 0.9 * hiss + 0.1 * sd; else hiss *= 1.002;
            bool talkNow = adapted && sd > TalkRatio * (syf + hiss) + 1e-9 * N;
            if (talkNow) talkHold = 20; else if (talkHold > 0) talkHold--;
            bool talking = talkNow || talkHold > 0;
            talkRun = talking ? talkRun + 1 : 0;
            bool learn = !talking || talkRun > 80;
            Talking = talking;
            EchoRemoval.Talking = talking;

            int c = blockNo % Parts;
            foreach (var r in refs)
            {
                var p = r.Path;
                if (!p.Busy) continue;
                // This output's error: the mic minus the others' foreground echo and its own background echo.
                double sr = 0, sw = 0;
                for (int i = 0; i < N; i++)
                {
                    float v = ef[i] + p.Yf[i] - p.Yb[i]; p.Er[i] = v; sr += v * (double)v;
                    float w = ef[i] + p.Yf[i]; sw += w * (double)w; // the mic without this output's foreground echo
                }
                p.Sb = sr;
                p.AvgB = 0.8 * p.AvgB + 0.2 * sr;
                p.LongB = 0.97 * p.LongB + 0.03 * sr;
                p.LongWithout = 0.97 * p.LongWithout + 0.03 * sw;
                p.SecB = 0.99 * p.SecB + 0.01 * sr; p.SecAge++;
                if (!learn) continue;

                // Pieces of the filter that already hold echo learn faster ("proportionate" learning): an earcup
                // leak sits in one or two of the 28 pieces, so most of the learning goes there instead of spread thin.
                double total = 0;
                for (int k = 0; k < Parts; k++) total += p.G[k];
                for (int k = 0; k < Parts; k++)
                    p.G[k] = total > 1e-20 ? (float)((1 - Alpha) / (2.0 * Parts) + (1 + Alpha) * p.G[k] / (2 * total)) : 1f / Parts;
                Array.Clear(norm, 0, Bins);
                for (int k = 0; k < Parts; k++)
                {
                    int s = p.Slot(k);
                    float[] xr = p.Xr[s], xi = p.Xi[s];
                    float g = p.G[k];
                    // Summed fresh each time: a running total drifts badly when loud sound leaves the window.
                    for (int f = 0; f < Bins; f++) norm[f] += g * (xr[f] * xr[f] + xi[f] * xi[f]);
                }

                // Its error spectrum (overlap-save: the error sits in the second half).
                Array.Clear(re, 0, N); Array.Clear(im, 0, M);
                Array.Copy(p.Er, 0, re, N, N);
                Fft.Forward(re, im);
                Array.Copy(re, ebR, Bins); Array.Copy(im, ebI, Bins);
                double mean = 0, emean = 0;
                for (int f = 0; f < Bins; f++) { mean += norm[f]; emean += ebR[f] * ebR[f] + ebI[f] * ebI[f]; }
                p.ErrPow = 0.9 * p.ErrPow + 0.1 * emean / Bins;
                // Keeps it from learning where the output is (nearly) silent: there's nothing to learn from there,
                // and the mic's own hiss would push the filter around until a sound arrived and set it off.
                float floor = (float)(mean / Bins * Floor + p.ErrPow * ErrFloor + 1e-12);
                // Learning speed: full when what's left is mostly echo, slower when it's mostly the room's steady
                // noise. Learning fast in noise makes the filter jitter by more than the echo it could take out.
                double frac = p.AvgB > 1e-10 * N ? 1 - hiss / p.AvgB : 1;
                float mu = (float)(Mu * Math.Max(0.1, Math.Min(1.0, frac)));
                for (int f = 0; f < Bins; f++) norm[f] = mu / (norm[f] + floor);
                for (int k = 0; k < Parts; k++)
                {
                    int s = p.Slot(k);
                    float[] xr = p.Xr[s], xi = p.Xi[s], br = p.Br[k], bi = p.Bi[k];
                    float gk = p.G[k];
                    for (int f = 0; f < Bins; f++)
                    {
                        // W += mu * gain of this piece * conj(X) * E / power
                        float a = xr[f], b = xi[f], er = ebR[f], ei = ebI[f], g = norm[f] * gk;
                        br[f] += g * (a * er + b * ei);
                        bi[f] += g * (a * ei - b * er);
                    }
                }
                // A few pieces of the filter per round are trimmed back to 512 taps (the rest would wrap around).
                for (int k = 0; k < ConstrainEach; k++) { int q = (c * ConstrainEach + k) % Parts; Constrain(p.Br[q], p.Bi[q]); }
            }

            avgD = 0.8 * avgD + 0.2 * sd; avgF = 0.8 * avgF + 0.2 * sf;
            longF = 0.97 * longF + 0.03 * sf;
            secF = 0.99 * secF + 0.01 * sf;
            double tiny = 1e-10 * N;
            FgErr += sf; MicIn += sd;
            if (!talking)
            {
                erD = 0.95 * erD + 0.05 * sd; erF = 0.95 * erF + 0.05 * sf;
                if (erD > 4 * erF + tiny) adapted = true; // it takes out a quarter of the echo or more
            }
            // Per output: use its background filter once it's clearly better (one switch per round), and undo it when
            // it went wrong. While you talk, only a much better one counts (your voice is in both errors, and a small
            // "improvement" then is the filter learning your voice). One that stays worse for a second (an output
            // whose sound never reaches the mic, so the filter only picks up noise) is wiped back to the foreground.
            // A switch needs the background to be better both just now (50 ms) and over the last third of a second:
            // with a noisy room, a filter for an output that never reaches the mic can look better for a moment by
            // fitting the noise.
            // One switch per round: each output's background learned with the others' foreground as it is, so
            // switching two at once would count the same echo twice.
            bool switched = false;
            foreach (var r in refs)
            {
                var p = r.Path;
                if (!p.Busy) continue;
                BgErr += p.Sb;
                // Clearly better now (a quarter less), or a little better (3% less) steadily for a whole second. In a
                // noisy room the echo is only part of what the mic hears, so even a perfect filter takes out less than
                // a quarter, and each step after the first takes out less again; a filter that's only fitting noise
                // jitters, which makes it worse than the foreground, and can't stay ahead for a second.
                bool better = talking ? p.AvgB < 0.25 * avgF && p.LongB < 0.25 * longF
                                      : (p.AvgB < 0.75 * avgF && p.LongB < 0.8 * longF)
                                        || (p.SecB < 0.97 * secF && p.LongB < 0.99 * longF && p.AvgB < avgF && p.SecAge > 100);
                if (!switched && (!TwoFilters || (better && avgF - p.AvgB > tiny)))
                {
                    CopyFilter(p, true);
                    for (int i = 0; i < N; i++) ef[i] = p.Er[i];
                    double ratio = secF > tiny ? p.SecB / secF : 1;
                    sf = p.Sb; avgF = p.AvgB; longF = p.LongB; secF = p.SecB; p.LongWithout = longF * 1.5; switched = true; p.WorseRun = 0;
                    foreach (var o in refs) if (o != r && o.Path.Busy) o.Path.SecB *= ratio; // they were judged against the old foreground
                    Copies++;
                }
                else if (p.AvgB > 3 * avgF + tiny || (p.WorseRun = p.AvgB > 1.3 * avgF ? p.WorseRun + 1 : 0) > 100)
                {
                    CopyFilter(p, false);
                    p.AvgB = avgF; p.LongB = longF; p.SecB = secF; p.WorseRun = 0;
                    BackgroundResets++;
                }
                // Each output's foreground echo has to make the mic quieter. If the mic would be quieter without it
                // (over a third of a second), that output's filters are wrong: wipe just those.
                if (p.LongWithout < 0.9 * longF && longF > tiny)
                {
                    for (int k = 0; k < Parts; k++) { Array.Clear(p.Br[k], 0, Bins); Array.Clear(p.Bi[k], 0, Bins); Array.Clear(p.Fr[k], 0, Bins); Array.Clear(p.Fi[k], 0, Bins); }
                    for (int i = 0; i < N; i++) ef[i] += p.Yf[i];
                    longF = p.LongWithout; p.LongB = longF; p.AvgB = avgF; p.LongWithout = longF; p.SecB = secF; p.SecAge = 0;
                    OutputWipes++;
                }
            }
            // Safety: if what's left has been louder than the mic itself for about a second, the filters are wrong
            // (say the output's sound jumped out of line with the mic); start them over.
            slowD = 0.98 * slowD + 0.02 * sd; slowF = 0.98 * slowF + 0.02 * sf;
            if (Safety && slowF > 2 * slowD + tiny)
            {
                slowF = slowD;
                SafetyResets++;
                EchoRemoval.Restarts++;
                adapted = false;
                foreach (var r in refs) { var p = r.Path; for (int k = 0; k < Parts; k++) { Array.Clear(p.Br[k], 0, Bins); Array.Clear(p.Bi[k], 0, Bins); Array.Clear(p.Fr[k], 0, Bins); Array.Clear(p.Fi[k], 0, Bins); } }
                for (int i = 0; i < N; i++) ef[i] = mic[i];
                avgF = avgD; sf = sd; longF = secF = slowD;
                foreach (var r in refs) { r.Path.AvgB = avgD; r.Path.LongB = r.Path.SecB = slowD; r.Path.SecAge = 0; }
            }

            // For checking: where the foreground filter thinks the echo is, per output (ms after the sound played).
            foreach (var r in refs)
            {
                var p = r.Path; double bestE = 0; int bestK = -1;
                for (int k = 0; k < Parts; k++) { double e2 = 0; for (int f = 0; f < Bins; f += 4) e2 += p.Fr[k][f] * p.Fr[k][f] + p.Fi[k][f] * p.Fi[k][f]; if (e2 > bestE) { bestE = e2; bestK = k; } }
                p.PeakMs = bestK < 0 ? -1 : (int)(bestK * N * 1000L / MixFormat.Rate);
            }
            cutNum = 0.98 * cutNum + 0.02 * sd; cutDen = 0.98 * cutDen + 0.02 * sf;
            EchoRemoval.CutDb = cutDen > 1e-14 ? (float)Math.Max(0, 10 * Math.Log10((cutNum + 1e-14) / (cutDen + 1e-14))) : 0f;
            EchoRemoval.Active = true;
            Emit(ef);
        }

        // New input block for this output: its spectrum goes in the ring, and the ring's power is updated.
        void AddInput(EchoPath p)
        {
            p.Head = (p.Head + 1) % Parts;
            int s = p.Head;
            Array.Copy(p.Prev, 0, re, 0, N);
            Array.Copy(p.In, 0, re, N, N);
            Array.Clear(im, 0, M);
            Fft.Forward(re, im);
            Array.Copy(re, p.Xr[s], Bins); Array.Copy(im, p.Xi[s], Bins); // replaces the oldest block
        }

        // Time signal (last half) of a half spectrum.
        void LastHalf(float[] hr, float[] hi, float[] dst)
        {
            Full(hr, hi);
            Fft.Inverse(re, im);
            Array.Copy(re, N, dst, 0, N);
        }

        void Full(float[] hr, float[] hi)
        {
            re[0] = hr[0]; im[0] = 0; re[N] = hr[N]; im[N] = 0;
            for (int f = 1; f < N; f++) { re[f] = hr[f]; im[f] = hi[f]; re[M - f] = hr[f]; im[M - f] = -hi[f]; }
        }

        void Constrain(float[] wr, float[] wi)
        {
            Full(wr, wi);
            Fft.Inverse(re, im);
            Array.Clear(re, N, N); Array.Clear(im, 0, M);
            Fft.Forward(re, im);
            Array.Copy(re, wr, Bins); Array.Copy(im, wi, Bins);
        }

        static void CopyFilter(EchoPath p, bool toForeground)
        {
            for (int k = 0; k < Parts; k++)
            {
                if (toForeground) { Array.Copy(p.Br[k], p.Fr[k], Bins); Array.Copy(p.Bi[k], p.Fi[k], Bins); }
                else { Array.Copy(p.Fr[k], p.Br[k], Bins); Array.Copy(p.Fi[k], p.Bi[k], Bins); }
            }
        }
    }

    // Radix-2 FFT of size EchoCanceller.M, in place on separate real and imaginary arrays.
    static class Fft
    {
        const int M = EchoCanceller.M;
        static readonly float[] cos = new float[M / 2], sin = new float[M / 2];
        static readonly int[] rev = new int[M];

        static Fft()
        {
            for (int i = 0; i < M / 2; i++) { cos[i] = (float)Math.Cos(2 * Math.PI * i / M); sin[i] = (float)Math.Sin(2 * Math.PI * i / M); }
            int bits = 0; while ((1 << bits) < M) bits++;
            for (int i = 0; i < M; i++) { int r = 0; for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b); rev[i] = r; }
        }

        public static void Forward(float[] re, float[] im) { Run(re, im, -1); }

        public static void Inverse(float[] re, float[] im)
        {
            Run(re, im, 1);
            float s = 1f / M;
            for (int i = 0; i < M; i++) { re[i] *= s; im[i] *= s; }
        }

        static void Run(float[] re, float[] im, int sign)
        {
            for (int i = 0; i < M; i++)
            {
                int j = rev[i];
                if (j > i) { float t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int size = 2; size <= M; size <<= 1)
            {
                int half = size >> 1, step = M / size;
                for (int start = 0; start < M; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = cos[k * step], wi = sign * sin[k * step];
                        int a = start + k, b = a + half;
                        float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
                }
            }
        }
    }
}
