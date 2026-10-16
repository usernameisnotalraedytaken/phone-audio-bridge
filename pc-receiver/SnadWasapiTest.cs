// WASAPI 播放验证与延迟测量。
//
// 目的：在接进接收器之前，先证明共享/独占都能出声，并测出真实的
// "写入 → 实际播出" 延迟。这是 v3 计划里"阶段一"的验收依据。
//
// 测量方法：
//   向指定后端连续写入一段已知音频（低频正弦 + 每 500ms 一个脉冲标记），
//   同时按固定间隔记录 QueuedMs（当前排队的毫秒数）。
//   稳定后 QueuedMs 的稳态值就是该后端在我们这一侧的在飞量，
//   它对应延迟估计里的 soundcard 项。
//
//   同时统计欠载：如果后端消费快于我们写入，QueuedMs 会被拉到 0，
//   那说明这个配置不可用——这比"听起来有没有断"可靠得多。
//
// 用法：
//   SnadWasapiTest.exe                        列出设备能力
//   SnadWasapiTest.exe <设备序号> <模式> <格式> <周期ms> <秒数>
//     模式  shared | exclusive
//     格式  pcm16 | pcm24 | pcm32 | float32
//   例：SnadWasapiTest.exe 5 exclusive pcm24 3 10

using System;
using System.Collections.Generic;
using System.Threading;

namespace Snad
{
    internal static class WasapiTest
    {
        private static int Main(string[] args)
        {
            if (args.Length < 5) { Usage(); return 0; }

            int index = int.Parse(args[0]);
            string mode = args[1].ToLowerInvariant();
            string fmtName = args[2].ToLowerInvariant();
            int periodMs = int.Parse(args[3]);
            int seconds = int.Parse(args[4]);

            var devices = DeviceEnumerator.Probe();
            DeviceCapability dev = null;
            foreach (var d in devices) if (d.Index == index) { dev = d; break; }
            if (dev == null) { Console.WriteLine("找不到设备序号 " + index); return 2; }

            bool exclusive = mode == "exclusive";
            SampleFormat fmt = ParseFormat(fmtName);

            Console.WriteLine("设备   [" + dev.Index + "] " + dev.Name);
            Console.WriteLine("模式   " + (exclusive ? "独占" : "共享"));
            Console.WriteLine("格式   " + AudioCodec.Name(fmt));
            Console.WriteLine("周期   " + periodMs + " ms（设备最小 " + dev.MinPeriodMs.ToString("F2") + " ms）");
            Console.WriteLine("时长   " + seconds + " 秒");
            Console.WriteLine();

            // 共享模式必须跟随设备混音格式
            int rate = dev.MixRate > 0 ? dev.MixRate : 48000;
            int channels = dev.MixChannels > 0 ? dev.MixChannels : 2;
            if (!exclusive)
            {
                fmt = dev.MixSampleFormat;
                Console.WriteLine("共享模式：格式与采样率跟随设备混音格式 -> " +
                                  AudioCodec.Name(fmt) + " @" + rate + "Hz/" + channels + "ch");
                Console.WriteLine();
            }

            var sink = new WasapiSink();
            try
            {
                sink.Open(dev, fmt, rate, channels, periodMs, exclusive);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[打开失败] " + ex.Message);
                Console.WriteLine();
                Console.WriteLine("这是一个有效结果——说明该组合在本机不可用。");
                return 3;
            }

            Console.WriteLine("已打开：" + sink.Name);
            Console.WriteLine("  内部环形缓冲容量 " + sink.CapacityMs.ToString("F1") + " ms");
            Console.WriteLine("  设备报告的流延迟 " + sink.StreamLatencyMs.ToString("F1") + " ms" +
                              (sink.StreamLatencyMs <= 0 ? "（该模式未提供此值）" : ""));
            Console.WriteLine();

            sink.Start();

            // 写入循环：按 5ms 一批喂数据，模拟接收器的节奏
            int frameBytes = AudioCodec.BytesPerSample(fmt) * channels;
            int framesPerWrite = rate * 5 / 1000;           // 5ms
            var block = new byte[framesPerWrite * frameBytes];
            double phase = 0;

            var queued = new List<double>();
            long written = 0, zeroHits = 0, totalWrites = 0;
            double sumQueued = 0, maxQueued = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextWriteMs = 0;

            while (sw.Elapsed.TotalSeconds < seconds)
            {
                // 按 5ms 节奏喂，模拟真实数据到达速率
                long now = sw.ElapsedMilliseconds;
                if (now < nextWriteMs) { Thread.Sleep(1); continue; }
                nextWriteMs += 5;

                FillBlock(block, framesPerWrite, channels, fmt, ref phase, rate);

                int want = block.Length;
                int off = 0;
                while (off < want)
                {
                    int n = sink.Write(block, off, want - off);
                    if (n <= 0) break;
                    off += n;
                }
                written += off;
                totalWrites++;

                double q = sink.QueuedMs;
                sumQueued += q;
                if (q > maxQueued) maxQueued = q;
                if (q <= 0.01) zeroHits++;
                queued.Add(q);
            }

            double elapsed = sw.Elapsed.TotalSeconds;
            sink.Stop();

            // 统计稳态队列：丢掉前 30% 的爬升阶段
            int skip = queued.Count * 3 / 10;
            double steadySum = 0; int steadyN = 0;
            for (int i = skip; i < queued.Count; i++) { steadySum += queued[i]; steadyN++; }
            double steady = steadyN > 0 ? steadySum / steadyN : 0;

            Console.WriteLine("=== 结果 ===");
            Console.WriteLine("  实际时长        " + elapsed.ToString("F2") + " 秒");
            Console.WriteLine("  写入总量        " + (written / frameBytes) + " 帧（" +
                              (written / frameBytes * 1000.0 / rate).ToString("F0") + " ms 音频）");
            Console.WriteLine("  队列稳态        " + steady.ToString("F2") + " ms   <- 对应延迟里的 soundcard 项");
            Console.WriteLine("  队列峰值        " + maxQueued.ToString("F2") + " ms");
            Console.WriteLine("  队列见底次数    " + zeroHits + " / " + totalWrites +
                              (zeroHits > 0 ? "   <- 出现见底说明该配置供不上，延迟数据不可用" : ""));

            // 音频时长的收支：写入的毫秒数应与实际时长相符，否则说明有丢样
            double audioMs = written / (double)frameBytes * 1000.0 / rate;
            double ratio = audioMs / (elapsed * 1000.0);
            Console.WriteLine("  写入/实际       " + ratio.ToString("F4") +
                              (Math.Abs(ratio - 1.0) > 0.02 ? "   <- 偏离 2% 以上，可能有丢样" : ""));

            return zeroHits > 0 ? 1 : 0;
        }

        /// <summary>
        /// 填一块测试音频：220Hz 正弦 + 每 500ms 一个 20ms 的脉冲。
        /// 脉冲用于人工核对（能听到规律的哒哒声说明通路正常），
        /// 正弦用于判断是否真的有声音而不是静音。
        /// </summary>
        private static void FillBlock(byte[] block, int frames, int channels,
                                      SampleFormat fmt, ref double phase, int rate)
        {
            int bps = AudioCodec.BytesPerSample(fmt);
            int frameBytes = bps * channels;

            for (int i = 0; i < frames; i++)
            {
                double t = phase / rate;
                double v = 0.25 * Math.Sin(2 * Math.PI * 220 * phase / rate);

                // 每 500ms 一个 20ms 脉冲
                double cyc = t % 0.5;
                if (cyc < 0.02) v += 0.5 * Math.Sin(2 * Math.PI * 880 * phase / rate);

                phase += 1;

                float f = (float)v;
                int off = i * frameBytes;
                for (int c = 0; c < channels; c++)
                    AudioCodec.Encode(block, off + c * bps, f, fmt);
            }
        }

        private static SampleFormat ParseFormat(string s)
        {
            switch (s)
            {
                case "pcm16": return SampleFormat.Pcm16;
                case "pcm24": return SampleFormat.Pcm24;
                case "pcm32": return SampleFormat.Pcm32;
                case "float32": return SampleFormat.Float32;
                default: throw new ArgumentException("未知格式 " + s);
            }
        }

        private static void Usage()
        {
            Console.WriteLine("WASAPI 播放验证与延迟测量");
            Console.WriteLine();
            Console.WriteLine("  SnadWasapiTest.exe                        列出设备与能力");
            Console.WriteLine("  SnadWasapiTest.exe <序号> <模式> <格式> <周期ms> <秒数>");
            Console.WriteLine("      模式  shared | exclusive");
            Console.WriteLine("      格式  pcm16 | pcm24 | pcm32 | float32");
            Console.WriteLine();
            Console.WriteLine("  例：SnadWasapiTest.exe 5 exclusive pcm24 3 10");
            Console.WriteLine();

            var devices = DeviceEnumerator.Probe();
            Console.WriteLine("=== 可用设备与组合 ===");
            foreach (var d in devices)
            {
                Console.WriteLine("[" + d.Index + "] " + d.Name);
                Console.WriteLine("      共享 " + (d.SupportsShared ? "可用" : "不可用") +
                                  "   独占 " + d.ExclusiveFormatsText() +
                                  "   最小周期 " + d.MinPeriodMs.ToString("F2") + " ms");
            }
        }
    }
}
