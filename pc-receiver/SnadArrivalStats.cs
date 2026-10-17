// 到达时间统计：从"到达时刻 − 发送时刻"的差值序列里算出抖动与漂移。
//
// 为什么单独抽成纯函数：
//   这段math决定"缓冲该留多深"，必须能用已知输入精确验证。
//   塞在接收循环里只能靠端到端注入去试，而端到端注入本身会引入
//   调度误差与接收端欠载，噪声远大于要测的量——先前就是这样
//   试了三轮都测不出注入的抖动（注入 0/8/20ms 都报 0.3ms）。
//   抽出来之后可以用合成序列断言，结果是确定的。
//
// 能得出什么、不能得出什么：
//   两端时钟不同源，偏移未知且拿不到往返时延，因此**绝对延迟算不出来**。
//   但差值的**变化量**与时钟偏移无关，所以抖动精确可得；
//   差值的长期趋势反映两端时钟速率差，即漂移。
//   绝对延迟另用声卡回环测。

using System;

namespace Snad
{
    internal static class ArrivalStats
    {
        /// <summary>样本少于此数时不给结论——两三个样本算出的"抖动"没有意义。</summary>
        public const int MinSamples = 32;

        /// <summary>
        /// 计算统计量。
        /// </summary>
        /// <param name="offsets">
        /// 按时间顺序排列的"到达时刻 − 发送时刻"差值（毫秒）。
        /// 两端的时钟偏移是其中的常数项，不影响下面三个量。
        /// </param>
        /// <param name="localTimes">
        /// 与 offsets 一一对应的本地到达时刻（毫秒）。用于把漂移按**实际窗口时长**
        /// 归一，而不是假设固定的包间隔——后者在换 block 大小后就错了。
        /// </param>
        /// <param name="jitterMs">
        /// 抖动：相邻差值变化的绝对值均值。这是缓冲需要吸收的量，
        /// 比极差稳健——一次偶发大跳不应让之后永远显示"抖动很大"。
        /// </param>
        /// <param name="driftMsPerMin">漂移：差值的长期趋势，换算成每分钟毫秒。</param>
        /// <param name="spanMs">窗口内的极差，作参考（偶发尖峰会抬高它）。</param>
        public static bool Compute(double[] offsets, double[] localTimes,
                                   out double jitterMs, out double driftMsPerMin, out double spanMs)
        {
            jitterMs = driftMsPerMin = spanMs = 0;

            if (offsets == null || offsets.Length < MinSamples) return false;
            int n = offsets.Length;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (offsets[i] < min) min = offsets[i];
                if (offsets[i] > max) max = offsets[i];
            }
            spanMs = max - min;

            // 抖动：相邻差值的绝对变化均值
            double sum = 0;
            for (int i = 1; i < n; i++) sum += Math.Abs(offsets[i] - offsets[i - 1]);
            jitterMs = sum / (n - 1);

            // 漂移：首尾各四分之一段的均值之差，按**两段时刻中心之间的距离**归一。
            //
            // 这里不能用整个窗口时长当分母：两段的均值分别位于各自段的中心，
            // 相隔约 3/4 窗口而不是整个窗口。先前按整窗归一，实测漂移恒为
            // 真值的 75%（注入 +5 读到 3.75，注入 -12 读到 -9.0）——
            // 0.75 正好是 3/4，说明分母选错了。
            // 取段内平均时刻作分母，就不必假设段长与位置，换任何比例都成立。
            if (localTimes == null || localTimes.Length != n) return true;

            int q = Math.Max(1, n / 4);
            double headSum = 0, headTimeSum = 0;
            double tailSum = 0, tailTimeSum = 0;
            for (int i = 0; i < q; i++)
            {
                headSum += offsets[i];
                headTimeSum += localTimes[i];
            }
            for (int i = n - q; i < n; i++)
            {
                tailSum += offsets[i];
                tailTimeSum += localTimes[i];
            }
            headSum /= q;
            tailSum /= q;
            headTimeSum /= q;
            tailTimeSum /= q;

            double centerSpan = tailTimeSum - headTimeSum;
            if (centerSpan < 1000) return true;     // 两段中心太近，斜率没有意义

            driftMsPerMin = (tailSum - headSum) / centerSpan * 60000.0;
            return true;
        }
    }
}
