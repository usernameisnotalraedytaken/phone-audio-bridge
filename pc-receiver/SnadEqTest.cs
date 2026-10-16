// 均衡器 DSP 验证工具。
//
// 为什么需要它：滤波器系数写错时往往"听感上只是有点怪"，不容易定位。
// 这里用正弦波实测增益，并与解析式频响交叉验证——
// 两者一致才说明系数与实时处理路径都是对的。
//
// 用法：SnadEqTest.exe [采样率]

using System;

namespace Snad
{
    internal static class EqTest
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + what);
            if (!ok) _failures++;
        }

        /// <summary>用给定频率的正弦波实测增益（dB）。</summary>
        private static double MeasureGainDb(Equalizer eq, int sampleRate, double freq, int channels)
        {
            // 生成立体声 16 位 PCM，长度取足够让滤波器进入稳态
            int frames = sampleRate / 2;                 // 0.5 秒
            var buf = new byte[frames * channels * 2];
            for (int i = 0; i < frames; i++)
            {
                short v = (short)(12000.0 * Math.Sin(2.0 * Math.PI * freq * i / sampleRate));
                for (int c = 0; c < channels; c++)
                {
                    int off = (i * channels + c) * 2;
                    buf[off] = (byte)(v & 0xFF);
                    buf[off + 1] = (byte)((v >> 8) & 0xFF);
                }
            }

            eq.ResetState();
            eq.Process(buf, buf.Length);

            // 跳过前 1/4 让瞬态过去，再测后段峰值
            int startFrame = frames / 4;
            int peak = 0;
            for (int i = startFrame; i < frames; i++)
            {
                int off = i * channels * 2;
                short s = (short)(buf[off] | (buf[off + 1] << 8));
                int a = Math.Abs((int)s);
                if (a > peak) peak = a;
            }
            return 20.0 * Math.Log10(Math.Max(1e-9, peak / 12000.0));
        }

        private static int Main(string[] args)
        {
            int sampleRate = 48000;
            if (args.Length > 0) int.TryParse(args[0], out sampleRate);
            if (sampleRate <= 0) sampleRate = 48000;

            Console.WriteLine("=== SNAD 均衡器 DSP 验证 ===");
            Console.WriteLine("采样率 = " + sampleRate + " Hz");
            Console.WriteLine();

            var eq = new Equalizer();
            eq.Configure(sampleRate, 2);

            // ---- 1. 全平：不应改变信号 ----
            Console.WriteLine("--- 全平（所有增益 0）---");
            eq.ResetAll();
            eq.Enabled = true;
            double flat = MeasureGainDb(eq, sampleRate, 1000, 2);
            Check(Math.Abs(flat) < 0.5, "1kHz 增益 " + flat.ToString("F3") + " dB（应约为 0）");
            double flatHigh = MeasureGainDb(eq, sampleRate, 10000, 2);
            Check(Math.Abs(flatHigh) < 0.5, "10kHz 增益 " + flatHigh.ToString("F3") + " dB（应约为 0）");

            // ---- 2. 单段提升：中心频率应接近设定增益 ----
            Console.WriteLine();
            Console.WriteLine("--- 各频段 +6dB，实测中心频率增益 ---");
            for (int i = 0; i < eq.Bands.Count; i++)
            {
                eq.ResetAll();
                eq.SetGain(i, 6.0);
                var band = eq.Bands[i];
                double measured = MeasureGainDb(eq, sampleRate, band.Freq, 2);
                // 搁架式在转折频率处约为一半增益，峰值式应接近全量
                double expected = (band.IsLowShelf || band.IsHighShelf) ? 3.0 : 6.0;
                bool ok = Math.Abs(measured - expected) < 1.5;
                Check(ok, string.Format("{0,-5} (+6dB) 实测 {1,6:F2} dB，期望约 {2:F1} dB",
                      band.Label, measured, expected));
            }

            // ---- 3. 衰减应反向 ----
            Console.WriteLine();
            Console.WriteLine("--- 1kHz 段 -6dB，实测应为负增益 ---");
            eq.ResetAll();
            int midBand = 3;   // 900Hz 附近
            eq.SetGain(midBand, -6.0);
            double cut = MeasureGainDb(eq, sampleRate, eq.Bands[midBand].Freq, 2);
            Check(cut < -3.0, string.Format("{0} (-6dB) 实测 {1:F2} dB（应明显为负）",
                  eq.Bands[midBand].Label, cut));

            // ---- 4. 解析式频响与实测应一致 ----
            Console.WriteLine();
            Console.WriteLine("--- 解析式频响 vs 正弦实测 ---");
            eq.ResetAll();
            eq.SetGain(0, 8.0);      // 低频架
            eq.SetGain(4, -5.0);     // 中频
            double[] freqs = { 100, 500, 1800, 5000, 12000 };
            foreach (double f in freqs)
            {
                double analytic = eq.ResponseDb(f);
                double actual = MeasureGainDb(eq, sampleRate, f, 2);
                bool ok = Math.Abs(analytic - actual) < 1.0;
                Check(ok, string.Format("{0,6:F0} Hz: 解析 {1,6:F2} dB / 实测 {2,6:F2} dB  差 {3:F2}",
                      f, analytic, actual, Math.Abs(analytic - actual)));
            }

            // ---- 5. 关闭时不应改动数据 ----
            Console.WriteLine();
            Console.WriteLine("--- 关闭均衡器时数据应原样通过 ---");
            eq.Enabled = false;
            eq.SetGain(0, 12.0);
            var test = new byte[400];
            var rnd = new Random(1234);
            rnd.NextBytes(test);
            var before = (byte[])test.Clone();
            eq.Process(test, test.Length);
            bool same = true;
            for (int i = 0; i < test.Length; i++) if (test[i] != before[i]) { same = false; break; }
            Check(same, "关闭时输出与输入逐字节相同");

            // ---- 6. 大增益不应溢出成反相噪声 ----
            Console.WriteLine();
            Console.WriteLine("--- 全部 +12dB 且满幅输入，检查限幅 ---");
            eq.Enabled = true;
            eq.ResetAll();
            for (int i = 0; i < eq.Bands.Count; i++) eq.SetGain(i, 12.0);
            eq.PreampDb = 0;
            int n = sampleRate / 4;
            var loud = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                short v = (short)(32000.0 * Math.Sin(2.0 * Math.PI * 300 * i / sampleRate));
                loud[i * 4] = (byte)(v & 0xFF); loud[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
                loud[i * 4 + 2] = (byte)(v & 0xFF); loud[i * 4 + 3] = (byte)((v >> 8) & 0xFF);
            }
            eq.Process(loud, loud.Length);
            // 限幅后不应出现跳变到相反极性的样本（那是整数溢出的典型特征）
            int signFlips = 0, prev = 0;
            for (int i = 0; i < n; i++)
            {
                int off = i * 4;
                int s = (short)(loud[off] | (loud[off + 1] << 8));
                if (prev != 0 && Math.Abs(s) > 30000 && Math.Sign(s) != Math.Sign(prev) && Math.Abs(prev) > 30000)
                    signFlips++;
                prev = s;
            }
            Check(signFlips < 5, "限幅后相邻满幅样本的极性跳变数 = " + signFlips + "（应很少）");

            // ---- 7. 采样率变化后系数应重算 ----
            Console.WriteLine();
            Console.WriteLine("--- 改变采样率后频响应随之变化 ---");
            eq.ResetAll();
            eq.SetGain(5, 6.0);
            eq.Configure(48000, 2);
            double r48 = eq.ResponseDb(eq.Bands[5].Freq);
            eq.Configure(44100, 2);
            double r44 = eq.ResponseDb(eq.Bands[5].Freq);
            Check(Math.Abs(r48 - 6.0) < 0.5, "48kHz 下中心增益 " + r48.ToString("F2") + " dB（应约 6）");
            Check(Math.Abs(r44 - 6.0) < 1.0, "44.1kHz 下中心增益 " + r44.ToString("F2") + " dB（仍应接近 6）");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "全部断言通过" : ("有 " + _failures + " 项断言失败"));
            return _failures == 0 ? 0 : 1;
        }
    }
}
