// 实测 waveOut 设备能同时排入多少个缓冲，以及每个缓冲多久完成一次。
//
// 目的：确定 Play 循环该分配多少缓冲、每块多少帧。
//
// 背景：接收器实测"读 18986 帧/s"远低于"到 47976 帧/s"，
// 而同一设备用 400 ms 音频测得的消费率是 99.5% 实时。两者矛盾说明
// 不是设备慢，而是 waveOut 经常无数据可放（饿死）——即缓冲数或块时长不足。
// 这个探测直接回答"能排多少"。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

class Probe
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
        int framesPerBuf = args.Length > 1 ? int.Parse(args[1]) : 768;   // 16 ms
        int maxBufs = args.Length > 2 ? int.Parse(args[2]) : 24;

        var f = new WF { tag = 1, ch = 2, sr = 48000, bps = 16, ba = 4, abps = 192000 };
        IntPtr h;
        int rc = waveOutOpen(out h, dev, ref f, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) { Console.WriteLine("open fail rc=" + rc); return; }

        int hs = Marshal.SizeOf(typeof(WH));
        int bytes = framesPerBuf * 4;
        var data = new IntPtr[maxBufs];
        var hdr = new IntPtr[maxBufs];
        for (int i = 0; i < maxBufs; i++)
        {
            data[i] = Marshal.AllocHGlobal(bytes);
            hdr[i] = Marshal.AllocHGlobal(hs);
            var s = new WH { lp = data[i], len = (uint)bytes, flags = DONE };
            Marshal.StructureToPtr(s, hdr[i], false);
            waveOutPrepareHeader(h, hdr[i], hs);
        }

        Console.WriteLine(string.Format("每块 {0} 帧 = {1:F1} ms，尝试连排 {2} 个缓冲",
            framesPerBuf, framesPerBuf / 48.0, maxBufs));

        // 尽可能快地连续提交，记录每个缓冲的提交时刻
        var submitTime = new double[maxBufs];
        var sw = Stopwatch.StartNew();
        int submitted = 0;
        for (int i = 0; i < maxBufs; i++)
        {
            var s = (WH)Marshal.PtrToStructure(hdr[i], typeof(WH));
            s.flags &= ~DONE;
            Marshal.StructureToPtr(s, hdr[i], false);
            int w = waveOutWrite(h, hdr[i], hs);
            if (w != 0) { Console.WriteLine("第 " + i + " 个提交失败 rc=" + w); break; }
            submitTime[i] = sw.Elapsed.TotalMilliseconds;
            submitted++;
        }
        Console.WriteLine("成功连排 " + submitted + " 个缓冲（总时长 " +
            (submitted * framesPerBuf / 48.0) + " ms）");

        // 观察前若干个何时完成
        Console.WriteLine("\n完成时刻（相对首次提交）：");
        int doneCount = 0;
        var sw2 = Stopwatch.StartNew();
        while (doneCount < submitted && sw2.Elapsed.TotalSeconds < 3)
        {
            for (int i = 0; i < submitted; i++)
            {
                var s = (WH)Marshal.PtrToStructure(hdr[i], typeof(WH));
                if ((s.flags & DONE) != 0 && submitTime[i] >= 0)
                {
                    Console.WriteLine(string.Format("  缓冲[{0}] 于 {1:F0} ms 完成（提交于 {2:F0} ms，历时 {3:F0} ms）",
                        i, sw2.Elapsed.TotalMilliseconds, submitTime[i], sw2.Elapsed.TotalMilliseconds - submitTime[i]));
                    submitTime[i] = -1;
                    doneCount++;
                }
            }
            Thread.Sleep(1);
        }
        Console.WriteLine("\n3 秒内完成 " + doneCount + "/" + submitted + " 个");

        waveOutReset(h);
        waveOutClose(h);
        for (int i = 0; i < maxBufs; i++) { Marshal.FreeHGlobal(data[i]); Marshal.FreeHGlobal(hdr[i]); }
    }
}
