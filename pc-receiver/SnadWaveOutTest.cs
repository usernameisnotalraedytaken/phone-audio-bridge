// waveOut 后端的基线测量。
//
// 为什么单独做：
//   本项目的延迟大头是声卡侧的 waveOut 缓冲池（16 个 16ms = 256ms 在飞）。
//   要证明 WASAPI 换得值，必须先把这个基线量准——不能拿"配置算出来的数"
//   当基线，那是把估计当实测。
//
// 测量方法与 SnadWasapiTest 一致，便于横向比较：
//   按固定间隔检查有多少个缓冲仍在飞，换算成毫秒。
//   池空（无一缓冲在飞）意味着设备已经无数据可放，即欠载。
//
// 用法：SnadWaveOutTest.exe <设备序号> <块ms> <缓冲个数> <秒数>
//   设备序号 -1 表示跟随系统默认

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Snad
{
    internal static class WaveOutTest
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEOUTCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels, wReserved1;
            public uint dwSupport;
        }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, int deviceId, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveOutGetDevCaps(IntPtr id, ref WAVEOUTCAPS caps, int size);

        private const uint WHDR_DONE = 0x00000001;
        private static int _hdrSize = Marshal.SizeOf(typeof(WAVEHDR));

        private static int Main(string[] args)
        {
            if (args.Length < 4)
            {
                Usage();
                return 0;
            }

            int deviceId = int.Parse(args[0]);
            int blockMs = int.Parse(args[1]);
            int slots = int.Parse(args[2]);
            int seconds = int.Parse(args[3]);

            int rate = 48000, channels = 2, bytesPerSample = 2;
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)rate,
                nBlockAlign = (ushort)(channels * bytesPerSample),
                wBitsPerSample = (ushort)(bytesPerSample * 8)
            };
            fmt.nAvgBytesPerSec = (uint)(rate * fmt.nBlockAlign);

            IntPtr h;
            int rc = waveOutOpen(out h, deviceId, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0)
            {
                Console.WriteLine("waveOutOpen 失败 rc=" + rc + "（设备 " + deviceId + "）");
                return 2;
            }

            int bufBytes = rate * channels * bytesPerSample * blockMs / 1000;
            Console.WriteLine("设备   " + (deviceId < 0 ? "(系统默认)" : deviceId.ToString()));
            Console.WriteLine("块     " + blockMs + " ms");
            Console.WriteLine("缓冲池 " + slots + " 个  ->  配置在飞 " + (blockMs * slots) + " ms");
            Console.WriteLine("时长   " + seconds + " 秒");
            Console.WriteLine();

            var hdrs = new IntPtr[slots];
            var bufs = new IntPtr[slots];
            for (int i = 0; i < slots; i++)
            {
                hdrs[i] = Marshal.AllocHGlobal(_hdrSize);
                bufs[i] = Marshal.AllocHGlobal(bufBytes);
                var hdr = new WAVEHDR { lpData = bufs[i], dwBufferLength = (uint)bufBytes };
                Marshal.StructureToPtr(hdr, hdrs[i], false);
                waveOutPrepareHeader(h, hdrs[i], _hdrSize);
                waveOutWrite(h, hdrs[i], _hdrSize);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long checks = 0, zeroHits = 0, resubmits = 0;
            double sumPending = 0, maxPending = 0;
            var pendingHist = new Dictionary<int, long>();

            while (sw.Elapsed.TotalSeconds < seconds)
            {
                // 与接收器一致：把已完成的缓冲立刻重新提交，
                // 池子才会维持在稳态。只写一次的话池子会排空，
                // 量到的是"空池"而不是真实行为。
                for (int i = 0; i < slots; i++)
                {
                    var hdr = (WAVEHDR)Marshal.PtrToStructure(hdrs[i], typeof(WAVEHDR));
                    if ((hdr.dwFlags & WHDR_DONE) != 0)
                    {
                        // 清零标志后重新写入
                        hdr.dwFlags &= ~WHDR_DONE;
                        Marshal.StructureToPtr(hdr, hdrs[i], false);
                        waveOutWrite(h, hdrs[i], _hdrSize);
                        resubmits++;
                    }
                }

                int pending = 0;
                for (int i = 0; i < slots; i++)
                {
                    var hdr = (WAVEHDR)Marshal.PtrToStructure(hdrs[i], typeof(WAVEHDR));
                    if ((hdr.dwFlags & WHDR_DONE) == 0) pending++;
                }

                double pendMs = pending * (double)blockMs;
                sumPending += pendMs;
                if (pendMs > maxPending) maxPending = pendMs;
                if (pending == 0) zeroHits++;
                checks++;
                if (!pendingHist.ContainsKey(pending)) pendingHist[pending] = 0;
                pendingHist[pending]++;

                Thread.Sleep(5);
            }

            double steady = checks > 0 ? sumPending / checks : 0;

            waveOutReset(h);
            for (int i = 0; i < slots; i++) { Marshal.FreeHGlobal(hdrs[i]); Marshal.FreeHGlobal(bufs[i]); }
            waveOutClose(h);

            Console.WriteLine("=== 结果 ===");
            Console.WriteLine("  实际时长        " + sw.Elapsed.TotalSeconds.ToString("F2") + " 秒");
            Console.WriteLine("  重新提交次数    " + resubmits);
            Console.WriteLine("  平均在飞        " + steady.ToString("F2") + " ms   <- 对应延迟里的 soundcard 项");
            Console.WriteLine("  峰值在飞        " + maxPending.ToString("F2") + " ms");
            Console.WriteLine("  池空次数        " + zeroHits + " / " + checks +
                              (zeroHits > 0 ? "   <- 池空即欠载，该配置不可用" : ""));
            Console.WriteLine("  配置上限        " + (blockMs * slots) + " ms（池满时的在飞量）");

            Console.Write("  在飞分布        ");
            var keys = new List<int>(pendingHist.Keys);
            keys.Sort();
            foreach (int k in keys)
                Console.Write(k + "个(" + (k * blockMs) + "ms):" + (pendingHist[k] * 100 / checks) + "%  ");
            Console.WriteLine();

            return zeroHits > 0 ? 1 : 0;
        }

        private static void Usage()
        {
            Console.WriteLine("waveOut 基线测量");
            Console.WriteLine();
            Console.WriteLine("  SnadWaveOutTest.exe <设备序号> <块ms> <缓冲个数> <秒数>");
            Console.WriteLine("    设备序号 -1 表示跟随系统默认");
            Console.WriteLine();
            Console.WriteLine("  例：SnadWaveOutTest.exe -1 16 16 8");
            Console.WriteLine();

            int n = waveOutGetNumDevs();
            Console.WriteLine("waveOut 输出设备：" + n);
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEOUTCAPS();
                int hr = waveOutGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WAVEOUTCAPS)));
                Console.WriteLine("  [" + i + "] " + (hr == 0 ? caps.szPname : "(读取失败 " + hr + ")"));
            }
        }
    }
}
