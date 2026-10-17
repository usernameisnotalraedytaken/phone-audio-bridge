// 独占模式可用组合的穷举探测。
//
// 背景：独占模式对缓冲时长有两条硬约束（必须大于周期、必须与设备内部周期对齐），
// 且各设备不同，官方文档给的"用 GetBufferSize 反算"在本机设备上不成立。
// 与其继续猜，不如把组合穷举一遍，把**设备真正接受什么**变成数据。
//
// 探测维度：
//   周期（periodicity）：从设备最小周期起，若干档
//   缓冲倍数：2x / 3x / 4x / 6x / 8x 周期
//   格式：pcm16 / pcm24
// 对每个组合实际调用一次 Initialize，记录 HRESULT。
//
// 每次失败后重新 Activate 客户端——失败的客户端处于无效状态，
// 对它再调 Initialize 只会重复同样的错误。
//
// 用法：SnadExclusiveProbe.exe <设备序号>

using System;
using System.Runtime.InteropServices;

namespace Snad
{
    internal static class ExclusiveProbe
    {
        private const int CLSCTX_ALL = 23;
        private const int EXCLUSIVE = 1;
        private const int FLAG_EVENTCALLBACK = 0x00040000;
        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        private static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "simple")
            {
                return SimpleProbe(args.Length >= 2 ? int.Parse(args[1]) : 5);
            }

            if (args.Length < 1)
            {
                Console.WriteLine("用法：SnadExclusiveProbe.exe <设备序号>");
                Console.WriteLine();
                foreach (var d in DeviceEnumerator.Probe())
                    Console.WriteLine("  [" + d.Index + "] " + d.Name +
                                      "  最小周期 " + d.MinPeriodMs.ToString("F2") + "ms" +
                                      "  独占 " + d.ExclusiveFormatsText());
                return 0;
            }

            int index = int.Parse(args[0]);
            DeviceCapability dev = null;
            foreach (var d in DeviceEnumerator.Probe()) if (d.Index == index) { dev = d; break; }
            if (dev == null) { Console.WriteLine("找不到设备 " + index); return 2; }

            Console.WriteLine("设备 [" + dev.Index + "] " + dev.Name);
            Console.WriteLine("混音格式 " + dev.MixDescription);
            Console.WriteLine("周期     默认 " + dev.DefaultPeriodMs.ToString("F2") +
                              "ms  最小 " + dev.MinPeriodMs.ToString("F2") + "ms");
            Console.WriteLine();

            int rate = dev.MixRate > 0 ? dev.MixRate : 48000;
            int channels = dev.MixChannels > 0 ? dev.MixChannels : 2;

            // 周期候选：从最小周期起，含最小周期的整数倍
            double[] periodCandidates = BuildPeriods(dev.MinPeriodMs, dev.DefaultPeriodMs);
            int[] mults = { 2, 3, 4, 6, 8, 12 };
            var formats = new[] { SampleFormat.Pcm16, SampleFormat.Pcm24, SampleFormat.Float32 };

            var results = new System.Collections.Generic.List<string>();
            int okCount = 0;

            foreach (double pms in periodCandidates)
            {
                long periodHns = (long)Math.Round(pms * 10000.0);

                foreach (var fmt in formats)
                {
                    IntPtr fmtPtr = AudioCodec.BuildFormat(fmt, channels, rate);

                    // 先确认该格式在独占下是否被支持，不支持就不必试缓冲
                    bool fmtOk = TryIsFormatSupported(dev, fmtPtr);

                    if (!fmtOk)
                    {
                        Marshal.FreeCoTaskMem(fmtPtr);
                        continue;
                    }

                    foreach (int m in mults)
                    {
                        long bufHns = periodHns * m;
                        int hr = TryInitialize(dev, fmtPtr, periodHns, bufHns);

                        string mark = hr == 0 ? "OK  " : "FAIL";
                        string line = "  " + mark + "  周期 " + pms.ToString("F1").PadLeft(5) + "ms" +
                                      "  缓冲 " + (bufHns / 10000.0).ToString("F1").PadLeft(6) + "ms" +
                                      "  " + AudioCodec.Name(fmt).PadRight(8) +
                                      (hr == 0 ? "" : "  hr=0x" + hr.ToString("X8") + " " + HrName(hr));
                        Console.WriteLine(line);
                        if (hr == 0)
                        {
                            okCount++;
                            results.Add("周期 " + pms.ToString("F1") + "ms / 缓冲 " +
                                        (bufHns / 10000.0).ToString("F1") + "ms / " + AudioCodec.Name(fmt));
                        }
                    }

                    Marshal.FreeCoTaskMem(fmtPtr);
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 可用的独占组合（共 " + okCount + " 个）===");
            if (okCount == 0)
            {
                Console.WriteLine("  无。该设备在本机不接受任何独占组合——");
                Console.WriteLine("  这是一个有效结论，应如实告诉用户，而不是假装可用。");
            }
            else
            {
                foreach (var r in results) Console.WriteLine("  " + r);
            }
            return okCount > 0 ? 0 : 1;
        }

        /// <summary>
        /// 简化对照：只试"周期传 0（由缓冲大小决定）"与"周期传实际值"两组，
        /// 用来判断 BUFFER_SIZE_NOT_ALIGNED 到底由哪个参数引起。
        /// 之前的穷举里所有组合都失败，说明问题不在缓冲大小而在用法。
        /// </summary>
        private static int SimpleProbe(int index)
        {
            DeviceCapability dev = null;
            foreach (var d in DeviceEnumerator.Probe()) if (d.Index == index) { dev = d; break; }
            if (dev == null) { Console.WriteLine("找不到设备 " + index); return 2; }

            int rate = dev.MixRate > 0 ? dev.MixRate : 48000;
            int channels = dev.MixChannels > 0 ? dev.MixChannels : 2;

            Console.WriteLine("设备 [" + dev.Index + "] " + dev.Name);
            Console.WriteLine("  最小周期 " + dev.MinPeriodMs.ToString("F2") + "ms  默认 " +
                              dev.DefaultPeriodMs.ToString("F2") + "ms");
            Console.WriteLine();

            IntPtr fmt = AudioCodec.BuildFormat(SampleFormat.Pcm24, channels, rate);

            Console.WriteLine("--- A) periodicity = 0（由缓冲大小决定周期）---");
            foreach (double bufMs in new[] { 10.0, 20.0, 50.0, 100.0, 200.0 })
            {
                long bufHns = (long)(bufMs * 10000);
                int hr = TryInitialize(dev, fmt, 0, bufHns);
                Console.WriteLine("  缓冲 " + bufMs.ToString("F0").PadLeft(4) + "ms  ->  " +
                                  (hr == 0 ? "OK" : "hr=0x" + hr.ToString("X8") + " " + HrName(hr)));
            }

            Console.WriteLine();
            Console.WriteLine("--- B) periodicity = 设备最小周期 ---");
            long minHns = (long)(dev.MinPeriodMs * 10000);
            foreach (int m in new[] { 2, 4, 8 })
            {
                long bufHns = minHns * m;
                int hr = TryInitialize(dev, fmt, minHns, bufHns);
                Console.WriteLine("  周期 " + dev.MinPeriodMs.ToString("F0") + "ms 缓冲 " +
                                  (bufHns / 10000.0).ToString("F0").PadLeft(4) + "ms  ->  " +
                                  (hr == 0 ? "OK" : "hr=0x" + hr.ToString("X8") + " " + HrName(hr)));
            }

            Console.WriteLine();
            Console.WriteLine("--- D) 独占 + 不带 EVENTCALLBACK 标志 ---");
            foreach (int m in new[] { 2, 4, 8 })
            {
                long bufHns = minHns * m;
                int hr = TryInitializeFlags(dev, fmt, minHns, bufHns, 0);
                Console.WriteLine("  周期 " + dev.MinPeriodMs.ToString("F0") + "ms 缓冲 " +
                                  (bufHns / 10000.0).ToString("F0").PadLeft(4) + "ms  ->  " +
                                  (hr == 0 ? "OK" : "hr=0x" + hr.ToString("X8") + " " + HrName(hr)));
            }

            Console.WriteLine();
            Console.WriteLine("--- E) 独占 + 不带标志 + periodicity=0 ---");
            foreach (double bufMs in new[] { 20.0, 50.0, 100.0 })
            {
                long bufHns = (long)(bufMs * 10000);
                int hr = TryInitializeFlags(dev, fmt, 0, bufHns, 0);
                Console.WriteLine("  缓冲 " + bufMs.ToString("F0").PadLeft(4) + "ms  ->  " +
                                  (hr == 0 ? "OK" : "hr=0x" + hr.ToString("X8") + " " + HrName(hr)));
            }

            Console.WriteLine();
            Console.WriteLine("--- C) 共享模式对照（periodicity = 0）---");
            foreach (double bufMs in new[] { 20.0, 50.0 })
            {
                long bufHns = (long)(bufMs * 10000);
                int hr = TryInitializeShared(dev, fmt, bufHns);
                Console.WriteLine("  缓冲 " + bufMs.ToString("F0").PadLeft(4) + "ms  ->  " +
                                  (hr == 0 ? "OK" : "hr=0x" + hr.ToString("X8") + " " + HrName(hr)));
            }

            Marshal.FreeCoTaskMem(fmt);
            return 0;
        }

        private static int TryInitializeFlags(DeviceCapability dev, IntPtr fmt,
                                              long periodHns, long bufHns, int flags)
        {
            var d = OpenDevice(dev);
            if (d == null) return unchecked((int)0x80004005);
            object o; Guid iid = IID_IAudioClient;
            if (d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return unchecked((int)0x80004005);
            var ac = (IAudioClientProbe)o;
            int hr = ac.Initialize(EXCLUSIVE, flags, bufHns, periodHns, fmt, IntPtr.Zero);

            // 必须显式释放客户端，否则设备会被一直占着，
            // 后续探测全部返回 DEVICE_IN_USE（实测踩到）。
            Marshal.ReleaseComObject(ac);
            Marshal.ReleaseComObject(d);
            return hr;
        }

        private static int TryInitializeShared(DeviceCapability dev, IntPtr fmt, long bufHns)
        {
            var d = OpenDevice(dev);
            if (d == null) return unchecked((int)0x80004005);
            object o; Guid iid = IID_IAudioClient;
            if (d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return unchecked((int)0x80004005);
            var ac = (IAudioClientProbe)o;
            int hr = ac.Initialize(0, FLAG_EVENTCALLBACK, bufHns, 0, fmt, IntPtr.Zero);
            Marshal.ReleaseComObject(ac);
            Marshal.ReleaseComObject(d);
            return hr;
        }

        private static double[] BuildPeriods(double minMs, double defMs)
        {
            var list = new System.Collections.Generic.List<double>();
            foreach (double p in new[] { minMs, minMs * 2, minMs * 4, minMs * 8, defMs, defMs * 2 })
            {
                if (p <= 0) continue;
                bool dup = false;
                foreach (double q in list) if (Math.Abs(q - p) < 0.01) { dup = true; break; }
                if (!dup) list.Add(p);
            }
            list.Sort();
            return list.ToArray();
        }

        private static IMMDevice OpenDevice(DeviceCapability dev)
        {
            var en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            IMMDevice d;
            int hr = string.IsNullOrEmpty(dev.Id)
                ? en.GetDefaultAudioEndpoint(0, 0, out d)
                : en.GetDevice(dev.Id, out d);
            return hr == 0 ? d : null;
        }

        private static bool TryIsFormatSupported(DeviceCapability dev, IntPtr fmt)
        {
            var d = OpenDevice(dev);
            if (d == null) return false;
            object o; Guid iid = IID_IAudioClient;
            if (d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return false;
            var ac = (IAudioClientProbe)o;
            IntPtr closest;
            int hr = ac.IsFormatSupported(EXCLUSIVE, fmt, out closest);
            if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
            Marshal.ReleaseComObject(ac);
            Marshal.ReleaseComObject(d);
            return hr == 0;
        }

        private static int TryInitialize(DeviceCapability dev, IntPtr fmt, long periodHns, long bufHns)
        {
            var d = OpenDevice(dev);
            if (d == null) return unchecked((int)0x80004005);
            object o; Guid iid = IID_IAudioClient;
            if (d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return unchecked((int)0x80004005);
            var ac = (IAudioClientProbe)o;
            int hr = ac.Initialize(EXCLUSIVE, FLAG_EVENTCALLBACK, bufHns, periodHns, fmt, IntPtr.Zero);
            Marshal.ReleaseComObject(ac);
            Marshal.ReleaseComObject(d);
            return hr;
        }

        private static string HrName(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x88890008: return "UNSUPPORTED_FORMAT";
                case 0x88890013: return "BUFFER_SIZE_NOT_ALIGNED";
                case 0x88890019: return "BUFFER_SIZE_ERROR";
                case 0x88890015: return "INVALID_DEVICE_PERIOD";
                case 0x8889000A: return "DEVICE_IN_USE";
                case 0x8889000E: return "EXCLUSIVE_MODE_NOT_ALLOWED";
                case 0x80070057: return "E_INVALIDARG";
                default: return "";
            }
        }
    }
}
