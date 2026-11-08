// Mic Mix engine: mixes your mic with the audio of chosen apps and plays the result into a
// virtual cable (VB-CABLE), so chat apps that use "CABLE Output" as their mic hear both.
// App audio is captured per process (Windows 10 2004+), so game audio and Discord voices stay out.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoundDeck
{
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        void IsFormatSupported_();
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long def, out long min);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEvent Done = new ManualResetEvent(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Done.Set(); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort tag; public ushort channels; public uint rate; public uint avgBytes;
        public ushort blockAlign; public ushort bits; public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessLoopbackParams
    {
        public int activationType; public uint targetProcessId; public int loopbackMode;
    }

    // Stereo sample queue. Counts are always even so left/right never swap.
    class Fifo
    {
        float[] buf = new float[48000 * 2];
        int head, count;
        float[] tmp = new float[48000 * 2];
        short[] stmp = new short[48000 * 2];

        public int Count { get { return count; } }

        public void WriteFrom(IntPtr data, int samples, bool isFloat, bool silent)
        {
            if (samples > tmp.Length) samples = tmp.Length;
            if (silent) Array.Clear(tmp, 0, samples);
            else if (isFloat) Marshal.Copy(data, tmp, 0, samples);
            else
            {
                Marshal.Copy(data, stmp, 0, samples);
                for (int i = 0; i < samples; i++) tmp[i] = stmp[i] / 32768f;
            }
            for (int i = 0; i < samples; i++)
            {
                if (count == buf.Length) { head = (head + 2) % buf.Length; count -= 2; }
                buf[(head + count) % buf.Length] = tmp[i];
                count++;
            }
        }

        public void ReadAdd(float[] dst, int n, float gain)
        {
            int m = Math.Min(n, count);
            for (int i = 0; i < m; i++) dst[i] += buf[(head + i) % buf.Length] * gain;
            head = (head + m) % buf.Length; count -= m;
        }

        public void Discard(int n)
        {
            int m = Math.Min(n, count);
            head = (head + m) % buf.Length; count -= m;
        }

        public void TrimTo(int max)
        {
            if (count > max) Discard(count - max);
        }
    }

    static class MixFormat
    {
        public const int Rate = 48000;
        public const int AUTOCONVERT = unchecked((int)0x80000000) | 0x08000000; // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
        public const int LOOPBACK = 0x00020000;
        public const int EVENTCALLBACK = 0x00040000;
        public static Guid IID_AudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static Guid IID_CaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static Guid IID_RenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

        public static IntPtr Make(bool isFloat)
        {
            var f = new WaveFormatEx
            {
                tag = (ushort)(isFloat ? 3 : 1), channels = 2, rate = Rate,
                bits = (ushort)(isFloat ? 32 : 16), blockAlign = (ushort)(isFloat ? 8 : 4), avgBytes = (uint)(Rate * (isFloat ? 8 : 4))
            };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormatEx)));
            Marshal.StructureToPtr(f, p, false);
            return p;
        }

        // Tries 32-bit float first, then 16-bit PCM. Returns whether float was accepted.
        public static bool Init(IAudioClient client, int flags, long duration)
        {
            IntPtr fmt = Make(true);
            try { client.Initialize(0, flags, duration, 0, fmt, IntPtr.Zero); return true; }
            catch { }
            finally { Marshal.FreeHGlobal(fmt); }
            fmt = Make(false);
            try { client.Initialize(0, flags, duration, 0, fmt, IntPtr.Zero); return false; }
            finally { Marshal.FreeHGlobal(fmt); }
        }

        public static IAudioClient ActivateDevice(IMMDeviceEnumerator en, string id)
        {
            IMMDevice d; en.GetDevice(id, out d);
            Guid g = IID_AudioClient; object o;
            d.Activate(ref g, 23, IntPtr.Zero, out o);
            return (IAudioClient)o;
        }
    }

    class CaptureStream : IDisposable
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        IAudioClient client;
        IAudioCaptureClient cap;
        bool isFloat;
        AutoResetEvent evt;
        public Fifo Fifo = new Fifo();

        public static CaptureStream OpenDevice(IMMDeviceEnumerator en, string id)
        {
            var s = new CaptureStream();
            s.client = MixFormat.ActivateDevice(en, id);
            s.Init(MixFormat.AUTOCONVERT, false);
            return s;
        }

        public static CaptureStream OpenProcess(uint pid)
        {
            var prm = new ProcessLoopbackParams { activationType = 1, targetProcessId = pid, loopbackMode = 0 };
            int size = Marshal.SizeOf(typeof(ProcessLoopbackParams));
            IntPtr blob = Marshal.AllocHGlobal(size);
            IntPtr pv = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PropVariant)));
            try
            {
                Marshal.StructureToPtr(prm, blob, false);
                Marshal.StructureToPtr(new PropVariant { vt = 65, p1 = new IntPtr(size), p2 = blob }, pv, false); // VT_BLOB
                var handler = new ActivationHandler();
                IActivateAudioInterfaceAsyncOperation op;
                ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", MixFormat.IID_AudioClient, pv, handler, out op);
                if (!handler.Done.WaitOne(3000)) throw new TimeoutException("App capture timed out");
                int hr; object o;
                op.GetActivateResult(out hr, out o);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                var s = new CaptureStream();
                s.client = (IAudioClient)o;
                s.Init(MixFormat.LOOPBACK | MixFormat.EVENTCALLBACK | MixFormat.AUTOCONVERT, true);
                return s;
            }
            finally { Marshal.FreeHGlobal(pv); Marshal.FreeHGlobal(blob); }
        }

        void Init(int flags, bool useEvent)
        {
            isFloat = MixFormat.Init(client, flags, 2000000);
            if (useEvent)
            {
                evt = new AutoResetEvent(false);
                client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
            }
            Guid g = MixFormat.IID_CaptureClient; object o;
            client.GetService(ref g, out o);
            cap = (IAudioCaptureClient)o;
            client.Start();
        }

        public void Pump()
        {
            for (int guard = 0; guard < 50; guard++)
            {
                uint next; cap.GetNextPacketSize(out next);
                if (next == 0) return;
                IntPtr data; uint frames, flags; ulong a, b;
                int hr = cap.GetBuffer(out data, out frames, out flags, out a, out b);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (hr != 0 || frames == 0) return;
                Fifo.WriteFrom(data, (int)frames * 2, isFloat, (flags & 2) != 0);
                cap.ReleaseBuffer(frames);
            }
        }

        public void Dispose()
        {
            try { if (client != null) client.Stop(); } catch { }
            if (evt != null) evt.Dispose();
        }
    }

    class RenderStream : IDisposable
    {
        IAudioClient client;
        IAudioRenderClient rc;
        bool isFloat;
        uint bufFrames;
        short[] stmp = new short[48000 * 2];

        public static RenderStream Open(IMMDeviceEnumerator en, string id)
        {
            var s = new RenderStream();
            s.client = MixFormat.ActivateDevice(en, id);
            s.isFloat = MixFormat.Init(s.client, MixFormat.AUTOCONVERT, 1000000);
            s.client.GetBufferSize(out s.bufFrames);
            Guid g = MixFormat.IID_RenderClient; object o;
            s.client.GetService(ref g, out o);
            s.rc = (IAudioRenderClient)o;
            s.client.Start();
            return s;
        }

        public int FramesToWrite(int target)
        {
            uint pad; client.GetCurrentPadding(out pad);
            return Math.Min(target - (int)pad, (int)bufFrames - (int)pad);
        }

        public void Write(float[] mix, int frames)
        {
            IntPtr p; rc.GetBuffer((uint)frames, out p);
            int n = frames * 2;
            if (isFloat) Marshal.Copy(mix, 0, p, n);
            else
            {
                for (int i = 0; i < n; i++) stmp[i] = (short)(mix[i] * 32767f);
                Marshal.Copy(stmp, 0, p, n);
            }
            rc.ReleaseBuffer((uint)frames, 0);
        }

        public void Dispose()
        {
            try { if (client != null) client.Stop(); } catch { }
        }
    }

    class MicMixEngine
    {
        const int TargetFrames = MixFormat.Rate / 25; // 40 ms of latency

        public volatile bool MediaOn;
        public volatile float MediaGain = 0.35f;
        public volatile float MicGain = 1f;
        public volatile string Status = "Off";
        public readonly VoiceBank Board = new VoiceBank(); // soundboard clips going to friends

        // Which apps are captured, and how loud each goes into the chat mic (when music is on) and the stream mic.
        class SourceSet { public uint[] Pids = new uint[0]; public float[] Mic = new float[0], Stream = new float[0]; }
        volatile SourceSet wanted = new SourceSet();
        volatile bool running;
        Thread thread;
        string micId, cableId, streamId;

        // Streamer mode: a second virtual mic for OBS etc., with its own mix.
        public volatile bool StreamBoard = true;
        public volatile float StreamVoiceGain = 1f;
        public volatile string StreamStatus = "Off";
        float streamPeak;
        public float TakeStreamPeak() { float p = streamPeak; streamPeak = 0; return p; }

        public bool Running { get { return thread != null && thread.IsAlive; } }
        public string StreamDeviceId { get { return streamId; } }

        // Listen to your stream: plays the stream mix (or the chat mix) into your headset so you can check it.
        public volatile string MonitorId;          // output device, null = off
        public volatile bool MonitorChat;          // false = what the stream hears, true = what Discord hears
        public volatile bool MonitorVoice;         // include your own voice
        public volatile float MonitorGain = 1f;
        public volatile string MonitorStatus = "Off";

        public void Start(string mic, string cable, string stream)
        {
            Stop();
            micId = mic; cableId = cable; streamId = stream;
            running = true;
            Status = "Starting...";
            thread = new Thread(Run) { IsBackground = true, Name = "MicMix", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) { thread.Join(2000); thread = null; }
            Status = "Off";
        }

        // gains: pid -> {chat mic gain (0/1, music hotkey applies on top), stream gain}
        public void SetSources(Dictionary<uint, float[]> gains)
        {
            var pids = gains.Keys.OrderBy(x => x).ToArray();
            var cur = wanted;
            var next = new SourceSet { Pids = pids, Mic = pids.Select(p => gains[p][0]).ToArray(), Stream = pids.Select(p => gains[p][1]).ToArray() };
            if (pids.SequenceEqual(cur.Pids) && next.Mic.SequenceEqual(cur.Mic) && next.Stream.SequenceEqual(cur.Stream)) return;
            wanted = next;
        }

        void Run()
        {
            CaptureStream mic = null;
            RenderStream outp = null, streamOut = null;
            var sources = new Dictionary<uint, CaptureStream>();
            var denoise = new NoiseRemoval.Stream();
            var dyn = new Dynamics(true); // gate + compressor; this stream drives the live readouts
            var fx = new VoiceFx();
            RenderStream monOut = null;
            string monOpen = null;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                mic = CaptureStream.OpenDevice(en, micId);
                outp = RenderStream.Open(en, cableId);
                if (streamId != null)
                {
                    try { streamOut = RenderStream.Open(en, streamId); StreamStatus = "Running"; }
                    catch (Exception ex) { StreamStatus = "Stopped: " + ex.Message; }
                }
                else StreamStatus = "Off";
                Status = "Running";
                var mix = new float[MixFormat.Rate * 2];
                var micBuf = new float[MixFormat.Rate * 2];
                var boardBuf = new float[MixFormat.Rate * 2];
                var mediaBuf = new float[MixFormat.Rate * 2];
                var streamApps = new float[MixFormat.Rate * 2];
                var srcBuf = new float[MixFormat.Rate * 2];
                var streamMix = new float[MixFormat.Rate * 2];
                var monBuf = new float[MixFormat.Rate * 2];
                SourceSet applied = null;
                var micGain = new Dictionary<uint, float>();
                var streamGain = new Dictionary<uint, float>();
                while (running)
                {
                    Thread.Sleep(5);

                    var want = wanted;
                    if (!ReferenceEquals(want, applied))
                    {
                        applied = want;
                        foreach (var pid in sources.Keys.Where(k => !want.Pids.Contains(k)).ToList())
                        {
                            sources[pid].Dispose();
                            sources.Remove(pid);
                        }
                        micGain.Clear(); streamGain.Clear();
                        for (int i = 0; i < want.Pids.Length; i++)
                        {
                            uint pid = want.Pids[i];
                            micGain[pid] = want.Mic[i];
                            streamGain[pid] = streamId != null ? want.Stream[i] : 0f;
                            if (sources.ContainsKey(pid)) continue;
                            try { sources[pid] = CaptureStream.OpenProcess(pid); } catch { }
                        }
                    }

                    mic.Pump();
                    foreach (var pid in sources.Keys.ToList())
                    {
                        try { sources[pid].Pump(); }
                        catch { sources[pid].Dispose(); sources.Remove(pid); }
                    }

                    int frames = outp.FramesToWrite(TargetFrames);
                    if (frames <= 0) continue;
                    int n = frames * 2;
                    // Each source is read once, then mixed into the chat mic and (in streamer mode) the stream mic.
                    Array.Clear(micBuf, 0, n);
                    Array.Clear(boardBuf, 0, n);
                    Array.Clear(mediaBuf, 0, n);
                    Array.Clear(streamApps, 0, n);
                    mic.Fifo.ReadAdd(micBuf, n, 1f);
                    denoise.Process(micBuf, frames);
                    dyn.Process(micBuf, frames);
                    fx.Process(micBuf, frames);
                    Board.ReadAdd(boardBuf, n);
                    float mg = MediaOn ? MediaGain : 0f;
                    foreach (var kv in sources)
                    {
                        float gm, gs;
                        micGain.TryGetValue(kv.Key, out gm); streamGain.TryGetValue(kv.Key, out gs);
                        gm *= mg;
                        if (gm == 0f && gs == 0f) { kv.Value.Fifo.Discard(n); continue; }
                        Array.Clear(srcBuf, 0, n);
                        kv.Value.Fifo.ReadAdd(srcBuf, n, 1f);
                        for (int i = 0; i < n; i++) { mediaBuf[i] += srcBuf[i] * gm; streamApps[i] += srcBuf[i] * gs; }
                    }

                    float voice = MicGain;
                    for (int i = 0; i < n; i++) mix[i] = Clip(micBuf[i] * voice + boardBuf[i] + mediaBuf[i]);
                    outp.Write(mix, frames);

                    if (streamOut != null)
                    {
                        float sv = StreamVoiceGain, sb = StreamBoard ? 1f : 0f, pk = 0f;
                        for (int i = 0; i < n; i++)
                        {
                            float v = Clip(micBuf[i] * sv + boardBuf[i] * sb + streamApps[i]);
                            streamMix[i] = v;
                            if (v > pk) pk = v; else if (-v > pk) pk = -v;
                        }
                        if (pk > streamPeak) streamPeak = pk;
                        // Both cables run at the same rate; if the stream one is briefly full, skip rather than block.
                        int room = streamOut.FramesToWrite(TargetFrames * 2);
                        if (room > 0) streamOut.Write(streamMix, Math.Min(room, frames));
                    }

                    // Listen to your stream / chat mix in your headset.
                    string monWant = MonitorId;
                    if (monWant != monOpen)
                    {
                        if (monOut != null) { monOut.Dispose(); monOut = null; }
                        monOpen = monWant;
                        MonitorStatus = "Off";
                        if (monWant != null)
                        {
                            try { monOut = RenderStream.Open(en, monWant); MonitorStatus = "Running"; }
                            catch (Exception ex) { MonitorStatus = "Stopped: " + ex.Message; }
                        }
                    }
                    if (monOut != null)
                    {
                        bool chatMix = MonitorChat, withVoice = MonitorVoice;
                        float mgain = MonitorGain, sv2 = StreamVoiceGain, sb2 = StreamBoard ? 1f : 0f;
                        for (int i = 0; i < n; i++)
                        {
                            float v = chatMix ? boardBuf[i] + mediaBuf[i] + (withVoice ? micBuf[i] * voice : 0f)
                                              : streamApps[i] + boardBuf[i] * sb2 + (withVoice ? micBuf[i] * sv2 : 0f);
                            monBuf[i] = Clip(v * mgain);
                        }
                        try
                        {
                            int room = monOut.FramesToWrite(TargetFrames * 2);
                            if (room > 0) monOut.Write(monBuf, Math.Min(room, frames));
                        }
                        catch (Exception ex) { monOut.Dispose(); monOut = null; MonitorStatus = "Stopped: " + ex.Message; }
                    }

                    // Keep latency from creeping up if a device's clock runs slightly fast.
                    mic.Fifo.TrimTo(TargetFrames * 2 * 3);
                    foreach (var s in sources.Values) s.Fifo.TrimTo(TargetFrames * 2 * 3);
                }
                Status = "Off";
            }
            catch (Exception ex)
            {
                Status = "Stopped: " + ex.Message;
            }
            finally
            {
                foreach (var s in sources.Values) s.Dispose();
                if (mic != null) mic.Dispose();
                if (outp != null) outp.Dispose();
                if (streamOut != null) streamOut.Dispose();
                if (monOut != null) monOut.Dispose();
                StreamStatus = "Off";
                MonitorStatus = "Off";
                denoise.Dispose();
            }
        }

        static float Clip(float v) { return v > 1f ? 1f : v < -1f ? -1f : v; }
    }

    // Clips being played (48 kHz stereo float). Each reader (Mic Mix, your headset) has its own bank.
    class VoiceBank
    {
        class Voice { public float[] Data; public int Pos; public float Gain; }
        readonly List<Voice> voices = new List<Voice>();

        public bool Busy { get { lock (voices) return voices.Count > 0; } }

        public void Play(float[] data, float gain)
        {
            lock (voices) voices.Add(new Voice { Data = data, Gain = gain });
        }

        public void StopAll() { lock (voices) voices.Clear(); }

        public void ReadAdd(float[] dst, int n)
        {
            lock (voices)
            {
                for (int v = voices.Count - 1; v >= 0; v--)
                {
                    var x = voices[v];
                    int m = Math.Min(n, x.Data.Length - x.Pos);
                    for (int i = 0; i < m; i++) dst[i] += x.Data[x.Pos + i] * x.Gain;
                    x.Pos += m;
                    if (x.Pos >= x.Data.Length) voices.RemoveAt(v);
                }
            }
        }
    }

    // Plays a capture device (your mic, for "hear yourself") and/or a VoiceBank into an output device.
    class MonitorEngine
    {
        const int TargetFrames = MixFormat.Rate / 50; // 20 ms, low so your own voice doesn't echo
        public volatile float Gain = 1f;
        public volatile string Status = "Off";
        public readonly VoiceBank Bank = new VoiceBank();
        readonly bool stopWhenIdle;
        volatile bool running;
        Thread thread;
        string capId, outId;

        public MonitorEngine(bool stopWhenIdle) { this.stopWhenIdle = stopWhenIdle; }

        public bool Running { get { return thread != null && thread.IsAlive; } }
        public string OutputId { get { return outId; } }

        public void Start(string capture, string output)
        {
            Stop();
            capId = capture; outId = output;
            running = true;
            Status = "Starting...";
            thread = new Thread(Run) { IsBackground = true, Name = "Monitor", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null && thread != Thread.CurrentThread) { thread.Join(2000); thread = null; }
            Status = "Off";
        }

        void Run()
        {
            CaptureStream cap = null;
            RenderStream outp = null;
            var denoise = new NoiseRemoval.Stream();
            var dyn = new Dynamics(false);
            var fx = new VoiceFx();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (capId != null) cap = CaptureStream.OpenDevice(en, capId);
                outp = RenderStream.Open(en, outId);
                Status = "Running";
                var mix = new float[MixFormat.Rate * 2];
                var micBuf = new float[MixFormat.Rate * 2];
                var idleSince = DateTime.Now;
                while (running)
                {
                    Thread.Sleep(3);
                    if (cap != null) cap.Pump();
                    int frames = outp.FramesToWrite(TargetFrames);
                    if (frames <= 0) continue;
                    int n = frames * 2;
                    Array.Clear(mix, 0, n);
                    if (cap != null)
                    {
                        // Hear yourself: same noise removal as friends get, so you hear what they hear.
                        Array.Clear(micBuf, 0, n);
                        cap.Fifo.ReadAdd(micBuf, n, 1f);
                        denoise.Process(micBuf, frames);
                        dyn.Process(micBuf, frames);
                        fx.Process(micBuf, frames);
                        float g = Gain;
                        for (int i = 0; i < n; i++) mix[i] += micBuf[i] * g;
                    }
                    if (Bank.Busy) idleSince = DateTime.Now;
                    else if (stopWhenIdle && (DateTime.Now - idleSince).TotalSeconds > 3) break;
                    Bank.ReadAdd(mix, n);
                    for (int i = 0; i < n; i++)
                    {
                        if (mix[i] > 1f) mix[i] = 1f;
                        else if (mix[i] < -1f) mix[i] = -1f;
                    }
                    outp.Write(mix, frames);
                    if (cap != null) cap.Fifo.TrimTo(TargetFrames * 2 * 2);
                }
                Status = "Off";
            }
            catch (Exception ex) { Status = "Stopped: " + ex.Message; }
            finally
            {
                if (cap != null) cap.Dispose();
                if (outp != null) outp.Dispose();
                denoise.Dispose();
            }
        }
    }

    // Listens to a mic and reports its level and background-noise floor (only while the Mixer is on screen).
    class MicMeter
    {
        const int Block = MixFormat.Rate / 50;          // 20 ms
        const int History = 200;                         // 4 s of blocks for the noise floor
        public volatile float Noise, Level;              // RMS (0..1)
        float peak;
        volatile bool running;
        Thread thread;
        public string DeviceId { get; private set; }
        public bool Running { get { return thread != null && thread.IsAlive; } }

        public float TakePeak() { float p = peak; peak = 0; return p; }

        public void Start(string id)
        {
            Stop();
            DeviceId = id;
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "MicMeter" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) { thread.Join(1000); thread = null; }
            DeviceId = null;
            Level = 0; Noise = 0; peak = 0;
        }

        void Run()
        {
            CaptureStream cap = null;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                cap = CaptureStream.OpenDevice(en, DeviceId);
                var buf = new float[Block * 2];
                var hist = new Queue<float>();
                while (running)
                {
                    Thread.Sleep(10);
                    cap.Pump();
                    while (cap.Fifo.Count >= buf.Length)
                    {
                        Array.Clear(buf, 0, buf.Length);
                        cap.Fifo.ReadAdd(buf, buf.Length, 1f);
                        float pk = 0; double sum = 0;
                        for (int i = 0; i < buf.Length; i++)
                        {
                            float a = Math.Abs(buf[i]);
                            if (a > pk) pk = a;
                            sum += buf[i] * buf[i];
                        }
                        float rms = (float)Math.Sqrt(sum / buf.Length);
                        if (pk > peak) peak = pk;
                        Level = rms;
                        hist.Enqueue(rms);
                        if (hist.Count > History) hist.Dequeue();
                        // Background noise = the quieter moments of the last few seconds (10th percentile).
                        // Some headsets send pure digital silence between sounds, so those blocks are skipped.
                        if (hist.Count >= 25)
                        {
                            var heard = hist.Where(v => v > 1e-6f).OrderBy(v => v).ToList();
                            Noise = heard.Count < 10 ? 1e-6f : heard[heard.Count / 10];
                        }
                    }
                }
            }
            catch { }
            finally { if (cap != null) cap.Dispose(); }
        }
    }

    // Decodes audio files (MP3, WAV, M4A, WMA...) with Windows Media Foundation into 48 kHz stereo float.
    static class AudioFile
    {
        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSourceReader
        {
            void GetStreamSelection_();
            void SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
            void GetNativeMediaType_();
            void GetCurrentMediaType(uint stream, out IMFMediaType type);
            void SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
            void SetCurrentPosition_();
            void ReadSample(uint stream, uint flags, out uint actual, out uint streamFlags, out long time, out IMFSample sample);
        }

        // IMFAttributes methods in vtable order; only the used ones have real signatures.
        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            void GetItem_(); void GetItemType_(); void CompareItem_(); void Compare_();
            [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
            void GetUINT64_(); void GetDouble_(); void GetGUID_(); void GetStringLength_(); void GetString_();
            void GetAllocatedString_(); void GetBlobSize_(); void GetBlob_(); void GetAllocatedBlob_(); void GetUnknown_();
            void SetItem_(); void DeleteItem_(); void DeleteAllItems_();
            void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
            void SetUINT64_(); void SetDouble_();
            void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            // 30 IMFAttributes methods
            void A0(); void A1(); void A2(); void A3(); void A4(); void A5(); void A6(); void A7(); void A8(); void A9();
            void A10(); void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19();
            void A20(); void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29();
            void GetSampleFlags_(); void SetSampleFlags_(); void GetSampleTime_(); void SetSampleTime_();
            void GetSampleDuration_(); void SetSampleDuration_(); void GetBufferCount_(); void GetBufferByIndex_();
            void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            void Lock(out IntPtr data, out int max, out int current);
            void Unlock();
        }

        [DllImport("mfplat.dll", PreserveSig = false)] static extern void MFStartup(int version, int flags);
        [DllImport("mfplat.dll", PreserveSig = false)] static extern void MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", PreserveSig = false)]
        static extern void MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attrs, out IMFSourceReader reader);

        static readonly Guid MT_MAJOR = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MT_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        static readonly Guid MT_RATE = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        static readonly Guid MajorAudio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        static readonly Guid SubFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        const uint FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE;
        const int MaxSeconds = 180;
        static bool started;

        public static float[] Load(string path)
        {
            if (!started) { MFStartup(0x20070, 0); started = true; }
            IMFSourceReader r;
            MFCreateSourceReaderFromURL(path, IntPtr.Zero, out r);
            try
            {
                r.SetStreamSelection(AllStreams, false);
                r.SetStreamSelection(FirstAudio, true);
                IMFMediaType want; MFCreateMediaType(out want);
                want.SetGUID(MT_MAJOR, MajorAudio);
                want.SetGUID(MT_SUBTYPE, SubFloat);
                r.SetCurrentMediaType(FirstAudio, IntPtr.Zero, want);
                IMFMediaType got; r.GetCurrentMediaType(FirstAudio, out got);
                uint ch, rate;
                if (got.GetUINT32(MT_CHANNELS, out ch) != 0 || ch == 0) ch = 2;
                if (got.GetUINT32(MT_RATE, out rate) != 0 || rate == 0) rate = 48000;

                var raw = new List<float>();
                long limit = (long)rate * ch * MaxSeconds;
                while (raw.Count < limit)
                {
                    uint actual, flags; long t; IMFSample s;
                    r.ReadSample(FirstAudio, 0, out actual, out flags, out t, out s);
                    if (s != null)
                    {
                        IMFMediaBuffer b; s.ConvertToContiguousBuffer(out b);
                        IntPtr p; int max, cur;
                        b.Lock(out p, out max, out cur);
                        var tmp = new float[cur / 4];
                        Marshal.Copy(p, tmp, 0, tmp.Length);
                        b.Unlock();
                        raw.AddRange(tmp);
                        Marshal.ReleaseComObject(b);
                        Marshal.ReleaseComObject(s);
                    }
                    if ((flags & 2) != 0 || (flags & 1) != 0) break; // end of stream / error
                }
                return ToStereo48k(raw, (int)ch, (int)rate);
            }
            finally { Marshal.ReleaseComObject(r); }
        }

        static float[] ToStereo48k(List<float> src, int ch, int rate)
        {
            int inFrames = src.Count / ch;
            double step = rate / (double)MixFormat.Rate;
            int outFrames = (int)(inFrames / step);
            var dst = new float[outFrames * 2];
            for (int i = 0; i < outFrames; i++)
            {
                double pos = i * step;
                int a = (int)pos; int b = Math.Min(a + 1, inFrames - 1);
                float f = (float)(pos - a);
                for (int c = 0; c < 2; c++)
                {
                    int sc = ch == 1 ? 0 : c;
                    float va = src[a * ch + sc], vb = src[b * ch + sc];
                    dst[i * 2 + c] = va + (vb - va) * f;
                }
            }
            return dst;
        }
    }
}
