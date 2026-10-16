// 测量 waveOut 设备对 48kHz 立体声的真实消费速率。
//
// 目的：判断 Voicemeeter Input 是否以实时（1.0x）消费音频。
// 若设备消费慢于实时，任何缓冲都必然被填满——那是输入输出速率不匹配，
// 不是靠调整缓冲尺寸能解决的问题。
//
// 方法：投递 400 ms 音频后计时，看放完需要多久。消费速率是稳态量，
//       400 ms 已足够，不需要长时段统计。

using System;
using System.Runtime.InteropServices;
using System.Threading;

class Rate
{
    [StructLayout(LayoutKind.Sequential)]
    struct WF { public ushort tag, ch; public uint sr, abps; public ushort ba, bps, cb; }

    [StructLayout(LayoutKind.Sequential)]
    struct WH { public IntPtr lp; public uint len, rec; public IntPtr user; public uint flags, loops; public IntPtr next, resv; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int id, ref WF f, IntPtr cb, IntPtr i, uint fl);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr w, int cb);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr w, int cb);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);

    const uint DONE = 1;

    static void Main(string[] args)
    {
        int dev = args.Length > 0 ? int.Parse(args[0]) : 1;
        string name = args.Length > 1 ? args[1] : ("device " + dev);

        var f = new WF { tag = 1, ch = 2, sr = 48000, bps = 16, ba = 4, abps = 192000 };
        IntPtr h;
        int rc = waveOutOpen(out h, dev, ref f, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) { Console.WriteLine(name + ": waveOutOpen 失败 rc=" + rc); return; }

        int hs = Marshal.SizeOf(typeof(WH));
        int frames = 4800;              // 每块 100 ms
        int bytes = frames * 4;
        const int n = 4;                // 共 400 ms
        var d = new IntPtr[n];
        var w = new IntPtr[n];

        for (int i = 0; i < n; i++)
        {
            d[i] = Marshal.AllocHGlobal(bytes);
            w[i] = Marshal.AllocHGlobal(hs);
            var s = new WH { lp = d[i], len = (uint)bytes, flags = DONE };
            // 三参数重载的第三个参数是 bool fDeleteOld，必须显式传 false，
            // 否则会去匹配四参数重载并报 CS1503。
            Marshal.StructureToPtr(s, w[i], false);
            waveOutPrepareHeader(h, w[i], hs);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            var s = (WH)Marshal.PtrToStructure(w[i], typeof(WH));
            s.flags &= ~DONE;
            Marshal.StructureToPtr(s, w[i], false);
            int wr = waveOutWrite(h, w[i], hs);
            if (wr != 0) { Console.WriteLine(name + ": waveOutWrite 失败 " + wr); return; }
        }

        while (true)
        {
            int done = 0;
            for (int i = 0; i < n; i++)
            {
                var s = (WH)Marshal.PtrToStructure(w[i], typeof(WH));
                if ((s.flags & DONE) != 0) done++;
            }
            if (done == n) break;
            Thread.Sleep(2);
        }
        sw.Stop();

        double audioSec = n * frames / 48000.0;
        Console.WriteLine(string.Format(
            "{0}: 投递 {1:F0} ms 音频，放完耗时 {2:F0} ms，消费率 = {3:F1}% 实时",
            name, audioSec * 1000, sw.Elapsed.TotalMilliseconds,
            audioSec / sw.Elapsed.TotalSeconds * 100));

        waveOutReset(h);
        waveOutClose(h);
        for (int i = 0; i < n; i++) { Marshal.FreeHGlobal(d[i]); Marshal.FreeHGlobal(w[i]); }
    }
}
