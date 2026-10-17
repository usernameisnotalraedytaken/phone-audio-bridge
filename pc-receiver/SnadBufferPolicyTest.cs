// 缓冲策略的确定性验证。
//
// 为什么不在端到端里验：
//   adaptive/calibrated 的行为依赖"掉坑序列"，而端到端里这个序列
//   由网络、调度、注入共同决定，不可复现。用合成序列喂 Observe，
//   期望值可以解析算出来，结果才是确定的。
//   端到端只用来验证"接到接收器上不炸、且欠载不增加"。
//
// 会验的几件事：
//   - static 的目标永不改变（对照基线）
//   - adaptive 在网络好时会把目标压到下限（省延迟）
//   - adaptive 在有掉坑时会把目标抬到 下限+2×掉坑
//   - 两者都严守 min/max
//   - calibrated 到时间后锁定，且锁定后再观测也不变

using System;
using System.Globalization;

namespace Snad
{
    internal static class BufferPolicyTest
    {
        private static int _bad;
        private const int Rate = 48000;

        private static void Check(bool ok, string what, string detail)
        {
            Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what +
                              (detail == null ? "" : "  " + detail));
            if (!ok) _bad++;
        }

        /// <summary>把毫秒换算成帧，喂一次观测。</summary>
        private static void FeedMs(IBufferPolicy p, double availMs, int times)
        {
            int frames = (int)Math.Round(availMs * Rate / 1000.0);
            for (int i = 0; i < times; i++) p.Observe(frames, Rate);
        }

        /// <summary>用"相对当前目标"的方式喂：模拟"每次都在目标附近上下浮动"。</summary>
        private static void FeedRelative(IBufferPolicy p, double offsetMs, int times)
        {
            for (int i = 0; i < times; i++)
            {
                double availMs = p.TargetMs + offsetMs;
                if (availMs < 0) availMs = 0;
                p.Observe((int)Math.Round(availMs * Rate / 1000.0), Rate);
            }
        }

        private static int Main()
        {
            Console.WriteLine("=== 缓冲策略验证（合成序列） ===");
            Console.WriteLine();

            TestStatic();
            TestAdaptiveDown();
            TestAdaptiveUp();
            TestClamp();
            TestCalibrated();
            TestFactory();

            Console.WriteLine();
            Console.WriteLine(_bad == 0 ? "缓冲策略验证通过" : ("有 " + _bad + " 项失败"));
            return _bad == 0 ? 0 : 1;
        }

        private static void TestStatic()
        {
            Console.WriteLine("--- static：目标恒等于配置 ---");
            var p = new StaticPolicy(80);
            FeedRelative(p, -30, 500);          // 无论怎么掉坑都不该变
            Check(p.TargetMs == 80, "目标保持 80", "实测 " + p.TargetMs);
            Check(p.Frozen, "static 恒为已锁定", null);
            Console.WriteLine();
        }

        private static void TestAdaptiveDown()
        {
            Console.WriteLine("--- adaptive：网络好时应压到下限 ---");
            var p = new AdaptivePolicy(80, 40, 200, 0.2);
            // 每次可用量正好等于当前目标 → 掉坑为 0 → 期望值 = 下限
            FeedRelative(p, 0, 3000);
            Check(Math.Abs(p.TargetMs - 40) <= 2, "收敛到下限 40",
                  "实测 " + p.TargetMs + " ms");
            Console.WriteLine();
        }

        private static void TestAdaptiveUp()
        {
            Console.WriteLine("--- adaptive：持续掉坑时应抬到 下限+2×掉坑 ---");
            var p = new AdaptivePolicy(80, 40, 200, 0.2);
            FeedRelative(p, -30, 4000);         // 每次都掉 30 ms
            // 期望 = 40 + 2×30 = 100
            Check(Math.Abs(p.TargetMs - 100) <= 3, "收敛到 100",
                  "实测 " + p.TargetMs + " ms（期望 400+2×30=100）");
            Console.WriteLine();
        }

        private static void TestClamp()
        {
            Console.WriteLine("--- 上限钳位 ---");
            var p = new AdaptivePolicy(80, 40, 120, 0.5);
            FeedMs(p, 0, 4000);                 // 一直空 → 掉坑极大
            Check(p.TargetMs <= 120, "不超过上限 120", "实测 " + p.TargetMs + " ms");
            Check(p.TargetMs >= 110, "确实顶到接近上限", "实测 " + p.TargetMs + " ms");
            Console.WriteLine();
        }

        private static void TestCalibrated()
        {
            Console.WriteLine("--- calibrated：标定后锁定 ---");
            // 5 秒标定；每次观测按 16ms 计 → 需要 313 次
            var p = new CalibratedPolicy(80, 40, 200, 5);

            // 标定期间掉坑 20ms
            FeedRelative(p, -20, 400);

            Check(p.Frozen, "标定完成并锁定", null);
            int locked = p.TargetMs;
            // 锁定值 = 下限 + 1.5×最大掉坑 = 40 + 30 = 70
            Check(Math.Abs(locked - 70) <= 2, "锁定值约 70", "实测 " + locked + " ms");

            // 锁定后继续观测，目标不得再变
            FeedRelative(p, -100, 500);
            Check(p.TargetMs == locked, "锁定后不再变化", "实测 " + p.TargetMs + " ms");

            // 另一个极端：从无掉坑 → 锁到下限
            var q = new CalibratedPolicy(80, 40, 200, 5);
            FeedRelative(q, +10, 400);
            Check(q.Frozen && Math.Abs(q.TargetMs - 40) <= 2, "无掉坑时锁到下限 40",
                  "实测 " + q.TargetMs + " ms");
            Console.WriteLine();
        }

        private static void TestFactory()
        {
            Console.WriteLine("--- 工厂：取值非法时退回 static 而不是抛 ---");
            IBufferPolicy p1 = BufferPolicyFactory.Create(new Profile { BufferMode = "adaptive" });
            Check(p1 is AdaptivePolicy, "adaptive 创建正确", p1.GetType().Name);

            IBufferPolicy p2 = BufferPolicyFactory.Create(new Profile { BufferMode = "calibrated" });
            Check(p2 is CalibratedPolicy, "calibrated 创建正确", p2.GetType().Name);

            IBufferPolicy p3 = BufferPolicyFactory.Create(new Profile { BufferMode = "乱写" });
            Check(p3 is StaticPolicy, "非法取值退回 static", p3.GetType().Name);

            IBufferPolicy p4 = BufferPolicyFactory.Create(new Profile { BufferMode = "static" });
            Check(p4 is StaticPolicy, "static 创建正确", p4.GetType().Name);

            Console.WriteLine();
        }
    }
}
