// 音频设备能力探测（命令行工具）。
//
// 枚举逻辑在 DeviceEnumerator 里，与接收器、编辑器共用同一份——
// 探测结果必须一致，否则编辑器会允许用户选出接收器跑不起来的组合。
//
// 用法：
//   SnadAudioProbe.exe          列出设备与关键结论
//   SnadAudioProbe.exe 详细      打印每台设备的完整能力

using System;
using System.Collections.Generic;

namespace Snad
{
    internal static class AudioProbe
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.WriteLine("=== 音频输出设备能力 ===");
                Console.WriteLine();

                List<DeviceCapability> devices = DeviceEnumerator.Probe();
                Console.WriteLine("活动输出设备：" + devices.Count);
                Console.WriteLine();

                foreach (var d in devices)
                {
                    Console.WriteLine("[" + d.Index + "] " + d.Name);
                    Console.WriteLine("    id           " + d.Id);
                    Console.WriteLine("    混音格式      " + d.MixDescription +
                                      "  -> " + AudioCodec.Name(d.MixSampleFormat));
                    Console.WriteLine("    周期         默认 " + d.DefaultPeriodMs.ToString("F2") +
                                      " ms   最小 " + d.MinPeriodMs.ToString("F2") + " ms");
                    Console.WriteLine("    独占模式      " + d.ExclusiveFormatsText());
                    Console.WriteLine("    可用后端      " +
                                      (d.SupportsShared ? "共享" : "共享不可用") +
                                      (d.SupportsExclusive ? " + 独占" : "（独占不可用）"));
                    Console.WriteLine();
                }

                Console.WriteLine("=== 汇总 ===");
                foreach (var d in devices)
                {
                    Console.WriteLine("  " + d.Name);
                    Console.WriteLine("      共享: " + (d.SupportsShared ? "可用" : "不可用") +
                                      "    独占: " + d.ExclusiveFormatsText() +
                                      "    最小周期 " + d.MinPeriodMs.ToString("F2") + "ms");
                }

                var def = DeviceEnumerator.Default();
                Console.WriteLine();
                Console.WriteLine("默认输出设备：" + (def == null ? "(无)" : def.Name));

                int shared = 0, exclusive = 0;
                foreach (var d in devices)
                {
                    if (d.SupportsShared) shared++;
                    if (d.SupportsExclusive) exclusive++;
                }
                Console.WriteLine("可用组合：共享 " + shared + " 台，独占 " + exclusive + " 台");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("探测异常：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }
    }
}
