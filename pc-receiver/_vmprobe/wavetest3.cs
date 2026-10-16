using System;
using System.Runtime.InteropServices;
using System.Threading;
class WaveTest3 {
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int id, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr wh, int cb);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr wh, int cb);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    const uint WHDR_DONE = 1;

    static void Main() {
        int[] devs = { 1, 0, 7, 9 };
        string[] names = { "Voicemeeter Input", "耳机(Realtek)", "扬声器(Realtek)", "Voicemeeter AUX" };
        for (int d = 0; d < devs.Length; d++) {
            var fmt = new WAVEFORMATEX { wFormatTag=1, nChannels=2, nSamplesPerSec=48000, wBitsPerSample=16, nBlockAlign=4, nAvgBytesPerSec=192000, cbSize=0 };
            IntPtr h;
            int hr = waveOutOpen(out h, devs[d], ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (hr != 0) { Console.WriteLine($"{names[d],-22} 打开失败 rc={hr}"); continue; }

            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            int simMs = 3000;                              // 名义上送 3 秒音频
            int totalFrames = 48000 * simMs / 1000;
            int framesPerBuf = 48000 * 16 / 1000;          // 每块 16ms
            int bufBytes = framesPerBuf * 4;
            int nbuf = 4;

            var data = new IntPtr[nbuf]; var hdr = new IntPtr[nbuf]; var done = new bool[nbuf];
            for (int i = 0; i < nbuf; i++) {
                data[i] = Marshal.AllocHGlobal(bufBytes);
                hdr[i] = Marshal.AllocHGlobal(hs);
                var w = new WAVEHDR { lpData = data[i], dwBufferLength = (uint)bufBytes, dwFlags = WHDR_DONE };
                Marshal.StructureToPtr(w, hdr[i], false);
                waveOutPrepareHeader(h, hdr[i], hs);
            }

            int written = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (written < totalFrames) {
                for (int i = 0; i < nbuf && written < totalFrames; i++) {
                    var cur = (WAVEHDR)Marshal.PtrToStructure(hdr[i], typeof(WAVEHDR));
                    if ((cur.dwFlags & WHDR_DONE) == 0) continue;
                    cur.dwFlags &= ~WHDR_DONE;
                    Marshal.StructureToPtr(cur, hdr[i], false);
                    int w = waveOutWrite(h, hdr[i], hs);
                    if (w != 0) { Console.WriteLine($"{names[d],-22} waveOutWrite 失败 {w}"); goto next; }
                    written += framesPerBuf;
                }
                Thread.Sleep(1);
            }
            // 等队列放完
            Thread.Sleep(600);
            sw.Stop();
            double secs = sw.Elapsed.TotalSeconds;
            double audioSecs = written / 48000.0;
            Console.WriteLine($"{names[d],-22} 送入 {audioSecs:F2}s 音频, 实际耗时 {secs:F2}s, 消费率={audioSecs/secs*100:F1}% 实时");
        next:
            waveOutReset(h); waveOutClose(h);
            for (int i = 0; i < nbuf; i++) { Marshal.FreeHGlobal(data[i]); Marshal.FreeHGlobal(hdr[i]); }
        }
    }
}
