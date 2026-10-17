// 到达统计的确定性验证。
//
// 为什么用合成序列而不是端到端注入：
//   端到端注入要经过"发包器调度 → 网络 → 接收 → 统计"，
//   其中发包器自身的调度误差与接收端欠载都会混进被测量里。
//   先前用注入法试了三轮（0/8/20ms）都测到约 0.3ms，无法判断
//   是统计错了还是注入没生效。合成序列把变量降到只有统计本身。
//
// 期望值都是可解析算出的：
//   常数序列                     抖动 0
//   独立均匀抖动 U(0,J)          相邻差均值 ≈ J/3
//   线性漂移 D ms/min            漂移读数应为 D
//   单点尖峰                     极差明显抬高，但抖动只被影响一步

using System;

namespace Snad
{
    internal static class ArrivalStatsTest
    {
        private static int _bad;

        private static void Check(bool ok, string what, string detail)
        {
            Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what + (detail == null ? "" : "  " + detail));
            if (!ok) _bad++;
        }

        private static int Main()
        {
            Console.WriteLine("=== 到达统计验证（合成序列） ===");
            Console.WriteLine();

            TestNoJitter();
            TestUniformJitter();
            TestLinearDrift();
            TestSpike();
            TestMinSamples();

            Console.WriteLine();
            Console.WriteLine(_bad == 0 ? "到达统计验证通过" : ("有 " + _bad + " 项失败"));
            return _bad == 0 ? 0 : 1;
        }

        /// <summary>构造等间隔到达的序列，offset 由外部函数给出。</summary>
        private static void Build(int n, double intervalMs, Func<int, double> offsetAt,
                                  out double[] offsets, out double[] times)
        {
            offsets = new double[n];
            times = new double[n];
            for (int i = 0; i < n; i++)
            {
                offsets[i] = offsetAt(i);
                times[i] = i * intervalMs;
            }
        }

        private static void TestNoJitter()
        {
            Console.WriteLine("--- 无抖动：差值恒定 ---");
            double[] off, t;
            // 含一个 3.5ms 的常数时钟偏移，它不应影响任何统计量
            Build(200, 21, i => 3.5, out off, out t);

            double jit, drift, span;
            bool ok = ArrivalStats.Compute(off, t, out jit, out drift, out span);
            Check(ok, "Compute 返回成功", null);
            Check(Math.Abs(jit) < 1e-9, "抖动为 0", "实测 " + jit.ToString("F4") + " ms");
            Check(Math.Abs(span) < 1e-9, "极差为 0", "实测 " + span.ToString("F4") + " ms");
            Check(Math.Abs(drift) < 1e-9, "漂移为 0", "实测 " + drift.ToString("F4") + " ms/分");
            Console.WriteLine();
        }

        private static void TestUniformJitter()
        {
            Console.WriteLine("--- 均匀抖动 U(0, J)：相邻差均值应约 J/3 ---");
            foreach (double J in new[] { 6.0, 12.0, 30.0 })
            {
                var rnd = new Random(1234);
                double[] off, t;
                Build(4000, 21, i => rnd.NextDouble() * J, out off, out t);

                double jit, drift, span;
                ArrivalStats.Compute(off, t, out jit, out drift, out span);

                double expect = J / 3.0;
                // 4000 个样本下抽样误差约 ±2%，留 10% 余量
                bool ok = Math.Abs(jit - expect) <= expect * 0.10;
                Check(ok, "J=" + J.ToString("F0") + "ms 抖动读数符合 J/3",
                      "实测 " + jit.ToString("F2") + "，期望 " + expect.ToString("F2") + " ms");
            }
            Console.WriteLine();
        }

        private static void TestLinearDrift()
        {
            Console.WriteLine("--- 线性漂移：读数应等于注入的斜率 ---");
            foreach (double msPerMin in new[] { 5.0, -12.0 })
            {
                // 每包 21ms，每分钟 2857.1 包
                const int n = 5715;                       // 约 2 分钟
                double perPacket = msPerMin / (60000.0 / 21.0);
                double[] off, t;
                Build(n, 21, i => 100.0 + i * perPacket, out off, out t);

                double jit, drift, span;
                ArrivalStats.Compute(off, t, out jit, out drift, out span);

                bool ok = Math.Abs(drift - msPerMin) <= Math.Abs(msPerMin) * 0.10 + 0.1;
                Check(ok, "注入 " + msPerMin.ToString("+0;-0") + " ms/分",
                      "实测 " + drift.ToString("F2") + " ms/分");
            }
            Console.WriteLine();
        }

        private static void TestSpike()
        {
            Console.WriteLine("--- 单点尖峰：极差被抬高，抖动只受一步影响 ---");
            var rnd = new Random(7);
            double[] off, t;
            // 基线抖动很小，第 100 个样本突然晚 40ms
            Build(256, 21, i => (i == 100 ? 40.0 : rnd.NextDouble() * 0.5), out off, out t);

            double jit, drift, span;
            ArrivalStats.Compute(off, t, out jit, out drift, out span);

            Check(span > 35.0, "极差反映尖峰", "实测 " + span.ToString("F1") + " ms");
            // 尖峰只影响两个相邻差，其余 254 个差值接近 0
            Check(jit < 1.0, "抖动未被单点尖峰放大",
                  "实测 " + jit.ToString("F3") + " ms（若接近 40 说明用了极差而非相邻差）");
            Console.WriteLine();
        }

        private static void TestMinSamples()
        {
            Console.WriteLine("--- 样本不足时不给结论 ---");
            double[] off, t;
            Build(ArrivalStats.MinSamples - 1, 21, i => 1.0, out off, out t);

            double jit, drift, span;
            bool ok = ArrivalStats.Compute(off, t, out jit, out drift, out span);
            Check(!ok, "样本数 " + (ArrivalStats.MinSamples - 1) + " 时返回 false", null);
            Check(jit == 0 && drift == 0 && span == 0, "返回 false 时输出全为 0", null);

            Build(ArrivalStats.MinSamples, 21, i => 1.0, out off, out t);
            Check(ArrivalStats.Compute(off, t, out jit, out drift, out span),
                  "样本数达到 " + ArrivalStats.MinSamples + " 时返回 true", null);

            // 传入 null 不应抛异常
            bool nullOk = true;
            try { ArrivalStats.Compute(null, null, out jit, out drift, out span); }
            catch { nullOk = false; }
            Check(nullOk, "null 输入不抛异常", null);
            Console.WriteLine();
        }
    }
}
