// Runs the RNNoise noise-removal plugin (rnnoise_mono.dll, a VST2 effect by werman) directly inside SoundDeck.
// One instance per audio stream; call it from that stream's thread only.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SoundDeck
{
    // Shared switch for every stream that carries your mic (Mic Mix and Hear yourself).
    static class NoiseRemoval
    {
        public static volatile bool On;
        public static volatile float Strength = 0.6f; // RNNoise "VAD threshold": how sure it must be that you're talking

        // Per-stream helper: creates the plugin on first use and keeps its strength in sync.
        public class Stream : IDisposable
        {
            RnNoise rn;
            float applied = -1;
            bool failed;
            public string Error;

            public void Process(float[] buf, int frames)
            {
                if (!On || failed || frames <= 0) return;
                if (rn == null)
                {
                    try { rn = new RnNoise(); }
                    catch (Exception ex) { failed = true; Error = ex.Message; return; }
                }
                float s = Strength;
                if (s != applied) { rn.SetParam(0, s); applied = s; }
                rn.ProcessStereo(buf, frames);
            }

            public void Dispose() { if (rn != null) rn.Dispose(); rn = null; }
        }
    }

    class RnNoise : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr HostCallback(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PluginMain(HostCallback host);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Dispatcher(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetParameterFn(IntPtr effect, int index, float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float GetParameterFn(IntPtr effect, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ProcessFn(IntPtr effect, IntPtr inputs, IntPtr outputs, int frames);

        const int Magic = 0x56737450; // 'VstP'
        const int effOpen = 0, effClose = 1, effGetParamName = 8, effSetSampleRate = 10, effSetBlockSize = 11, effMainsChanged = 12, effStartProcess = 71, effStopProcess = 72;
        const int MaxBlock = 4800;

        public static string DllPath { get { return Path.Combine(Path.GetDirectoryName(typeof(RnNoise).Assembly.Location), "rnnoise_mono.dll"); } }
        public static bool Available { get { return File.Exists(DllPath); } }

        static IntPtr module;
        static PluginMain main;
        static readonly HostCallback host = Host; // kept alive for the plugin's lifetime
        static readonly object loadLock = new object();

        IntPtr effect, inBuf, outBuf, inPtrs, outPtrs;
        Dispatcher dispatch;
        SetParameterFn setParam;
        GetParameterFn getParam;
        ProcessFn process;
        float[] mono = new float[MaxBlock];

        public int ParamCount { get; private set; }

        static IntPtr Host(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
        {
            switch (opcode)
            {
                case 1: return new IntPtr(2400);   // audioMasterVersion
                case 16: return new IntPtr(48000); // audioMasterGetSampleRate
                case 17: return new IntPtr(MaxBlock);
                default: return IntPtr.Zero;
            }
        }

        public RnNoise()
        {
            lock (loadLock)
            {
                if (module == IntPtr.Zero)
                {
                    module = LoadLibrary(DllPath);
                    if (module == IntPtr.Zero) throw new Exception("Couldn't load rnnoise_mono.dll (error " + Marshal.GetLastWin32Error() + ")");
                    IntPtr f = GetProcAddress(module, "VSTPluginMain");
                    if (f == IntPtr.Zero) throw new Exception("rnnoise_mono.dll isn't the expected plugin");
                    main = (PluginMain)Marshal.GetDelegateForFunctionPointer(f, typeof(PluginMain));
                }
                effect = main(host);
            }
            if (effect == IntPtr.Zero || Marshal.ReadInt32(effect, 0) != Magic) throw new Exception("RNNoise plugin didn't start");

            // AEffect layout (64-bit): dispatcher 8, setParameter 24, getParameter 32, numParams 44, numInputs 48, numOutputs 52, processReplacing 120
            dispatch = (Dispatcher)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(effect, 8), typeof(Dispatcher));
            setParam = (SetParameterFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(effect, 24), typeof(SetParameterFn));
            getParam = (GetParameterFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(effect, 32), typeof(GetParameterFn));
            process = (ProcessFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(effect, 120), typeof(ProcessFn));
            ParamCount = Marshal.ReadInt32(effect, 44);
            int ins = Marshal.ReadInt32(effect, 48), outs = Marshal.ReadInt32(effect, 52);
            if (ins < 1 || outs < 1) throw new Exception("RNNoise plugin has no audio input");

            dispatch(effect, effOpen, 0, IntPtr.Zero, IntPtr.Zero, 0);
            dispatch(effect, effSetSampleRate, 0, IntPtr.Zero, IntPtr.Zero, MixFormat.Rate);
            dispatch(effect, effSetBlockSize, 0, new IntPtr(MaxBlock), IntPtr.Zero, 0);
            dispatch(effect, effMainsChanged, 0, new IntPtr(1), IntPtr.Zero, 0);
            dispatch(effect, effStartProcess, 0, IntPtr.Zero, IntPtr.Zero, 0);

            // One buffer per channel the plugin expects (mono plugin = 1 each).
            inBuf = Marshal.AllocHGlobal(MaxBlock * 4 * ins);
            outBuf = Marshal.AllocHGlobal(MaxBlock * 4 * outs);
            inPtrs = Marshal.AllocHGlobal(IntPtr.Size * ins);
            outPtrs = Marshal.AllocHGlobal(IntPtr.Size * outs);
            for (int i = 0; i < ins; i++) Marshal.WriteIntPtr(inPtrs, i * IntPtr.Size, inBuf + i * MaxBlock * 4);
            for (int i = 0; i < outs; i++) Marshal.WriteIntPtr(outPtrs, i * IntPtr.Size, outBuf + i * MaxBlock * 4);
        }

        public string ParamName(int i)
        {
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                Marshal.WriteByte(buf, 0, 0);
                dispatch(effect, effGetParamName, i, IntPtr.Zero, buf, 0);
                return Marshal.PtrToStringAnsi(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public float GetParam(int i) { return getParam(effect, i); }
        public void SetParam(int i, float v) { setParam(effect, i, v); }

        // Cleans an interleaved stereo buffer in place (mixed to mono, cleaned, copied to both sides).
        public void ProcessStereo(float[] buf, int frames)
        {
            int done = 0;
            while (done < frames)
            {
                int n = Math.Min(MaxBlock, frames - done);
                for (int i = 0; i < n; i++) mono[i] = (buf[(done + i) * 2] + buf[(done + i) * 2 + 1]) * 0.5f;
                Marshal.Copy(mono, 0, inBuf, n);
                process(effect, inPtrs, outPtrs, n);
                Marshal.Copy(outBuf, mono, 0, n);
                for (int i = 0; i < n; i++) { buf[(done + i) * 2] = mono[i]; buf[(done + i) * 2 + 1] = mono[i]; }
                done += n;
            }
        }

        public void Dispose()
        {
            if (effect == IntPtr.Zero) return;
            try
            {
                dispatch(effect, effStopProcess, 0, IntPtr.Zero, IntPtr.Zero, 0);
                dispatch(effect, effMainsChanged, 0, IntPtr.Zero, IntPtr.Zero, 0);
                dispatch(effect, effClose, 0, IntPtr.Zero, IntPtr.Zero, 0);
            }
            catch { }
            effect = IntPtr.Zero;
            Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); Marshal.FreeHGlobal(inPtrs); Marshal.FreeHGlobal(outPtrs);
        }
    }
}
