// 缓冲策略：决定抖动缓冲的目标水位。
//
// 为什么要有这一层：
//   回环实测（LATENCY-MEASURED.md）表明声卡缓冲池不是稳态延迟——
//   池深从 4 改到 32、延迟都在 25-32ms 不动。真正决定"声音晚多久出来"的
//   是环形缓冲停在多深。因此这个深度该由策略来定，而不是写死一个数。
//
//   固定深度有个两难：网络好时它白付延迟，网络差时又不够。
//   三种策略对应三种应对方式：
//     static      写死，行为可预测，v2.0 的行为就是它
//     adaptive    按实测掉坑深度持续调整，受 min/max 约束
//     calibrated  启动时观察一段，锁定一个够用的值，之后不再变
//
// 关键量是什么：
//   "掉坑深度" = 目标水位 − 本次读取时的实际可用帧。
//   它正好等于"如果目标再低这么多就会欠载"的量，因此
//   目标 ≈ 掉坑深度 × 安全系数 就是够用的深度。
//   这个量只取决于到达抖动，不取决于目标本身，所以可迁移。

using System;
using System.Globalization;

namespace Snad
{
    internal interface IBufferPolicy
    {
        /// <summary>策略名，用于日志。</summary>
        string Name { get; }

        /// <summary>当前目标水位（帧）。</summary>
        int TargetMs { get; }

        /// <summary>每次读取后回报一次观测，供策略更新。</summary>
        /// <param name="availableFrames">本次读取前环里的可用帧数。</param>
        /// <param name="sampleRate">当前采样率。</param>
        void Observe(int availableFrames, int sampleRate);

        /// <summary>是否已停止调整（标定完成后为 true）。</summary>
        bool Frozen { get; }

        /// <summary>
        /// 刚刚发生了一次欠载（环形缓冲在读的时候被读空）。
        ///
        /// 这是唯一**直接**的失败信号：观察到的水位只反映读取那一瞬间，
        /// 看不到"块内被读空"。实测差距很大——只看水位时 adaptive 在
        /// 40ms 抖动下欠载 8 次仍在往低处压，因为它根本没看见失败。
        /// </summary>
        void NotifyUnderrun();

        /// <summary>一行状态，写进周期日志。</summary>
        string Status();
    }

    /// <summary>
    /// 固定深度。行为与 v2.0 完全一致，用作对照基线。
    /// </summary>
    internal sealed class StaticPolicy : IBufferPolicy
    {
        private readonly int _targetMs;

        public StaticPolicy(int targetMs) { _targetMs = Math.Max(1, targetMs); }

        public string Name { get { return "static"; } }
        public int TargetMs { get { return _targetMs; } }
        public bool Frozen { get { return true; } }
        public void Observe(int availableFrames, int sampleRate) { }
        public void NotifyUnderrun() { }
        public string Status() { return "固定 " + _targetMs + " ms"; }
    }

    /// <summary>
    /// 自适应：持续按掉坑深度调整目标，受 min/max 约束，调整速度可配。
    ///
    /// 算法刻意做得可解释：
    ///   1. 每次读取记录"掉坑深度"（目标 − 可用），只记正值；
    ///   2. 对它取指数滑动平均，抹掉单次尖峰；
    ///   3. 期望目标 = 下限 + 2 × 平均掉坑（2 倍是安全系数）；
    ///   4. 每次按 adaptRate 向期望值移动一小步，避免突变造成可听见的跳变。
    ///
    /// 网络好时掉坑趋近 0，目标会缓慢降到下限（省延迟）；
    /// 网络差时掉坑变大，目标自动抬高（保连续）。两边都能收敛。
    /// </summary>
    internal sealed class AdaptivePolicy : IBufferPolicy
    {
        private readonly int _minMs, _maxMs;
        private readonly double _rate;
        private double _targetMs;
        private double _emaDipMs;
        private const double SafetyFactor = 2.0;
        private const double DipAlpha = 0.05;      // 掉坑的滑动平均系数

        // 欠载惩罚：每次欠载给期望深度加一截，随后缓慢衰减。
        // 它保证"压得太低"会被立刻纠正，而不是等 EMA 慢慢反映。
        private const double UnderrunBumpMs = 8.0;
        private double _underrunBoostMs;

        public AdaptivePolicy(int startMs, int minMs, int maxMs, double adaptRate)
        {
            _minMs = Math.Max(5, Math.Min(minMs, maxMs));
            _maxMs = Math.Max(_minMs, maxMs);
            _rate = Math.Max(0.01, Math.Min(1.0, adaptRate));
            _targetMs = Clamp(startMs);
        }

        public string Name { get { return "adaptive"; } }

        public int TargetMs { get { return (int)Math.Round(_targetMs); } }

        public bool Frozen { get { return false; } }

        private double Clamp(double ms)
        {
            if (ms < _minMs) return _minMs;
            if (ms > _maxMs) return _maxMs;
            return ms;
        }

        /// <summary>期望目标：下限 + 安全系数 ×（平均掉坑 + 欠载惩罚）。</summary>
        private double DesiredTargetMs()
        {
            return Clamp(_minMs + SafetyFactor * (_emaDipMs + _underrunBoostMs));
        }

        public void NotifyUnderrun()
        {
            _underrunBoostMs += UnderrunBumpMs;
            if (_underrunBoostMs > _maxMs) _underrunBoostMs = _maxMs;
        }

        public void Observe(int availableFrames, int sampleRate)
        {
            if (sampleRate <= 0) return;

            double availMs = availableFrames * 1000.0 / sampleRate;
            double dipMs = _targetMs - availMs;
            if (dipMs < 0) dipMs = 0;               // 只关心"掉下去多少"

            _emaDipMs += (dipMs - _emaDipMs) * DipAlpha;

            // 欠载惩罚缓慢衰减：一段时间不再欠载，才允许重新往低处压
            _underrunBoostMs *= 0.995;

            double want = DesiredTargetMs();
            _targetMs += (want - _targetMs) * _rate;
            _targetMs = Clamp(_targetMs);
        }

        public string Status()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} 目标 {1:F0} ms（范围 {2}-{3}），平均掉坑 {4:F1} ms，欠载惩罚 {5:F1} ms",
                Name, _targetMs, _minMs, _maxMs, _emaDipMs, _underrunBoostMs);
        }
    }

    /// <summary>
    /// 标定：先按配置深度跑一段时间，观察最大掉坑，然后锁定一个够用的深度。
    ///
    /// 与自适应的区别在于"之后不再变"——目标是可预测、可复现，
    /// 代价是网络状况变了它不会跟。适合固定环境（比如同一张桌子、同一个热点）。
    /// </summary>
    internal sealed class CalibratedPolicy : IBufferPolicy
    {
        private readonly int _minMs, _maxMs;
        private readonly int _calibrateSec;
        private readonly double _startMs;

        private double _targetMs;
        private int _observedMs;                   // 已观察的音频时长（毫秒）
        private bool _frozen;

        // 只记录"稳态"掉坑，启动阶段丢掉。
        //
        // 实测教训：把启动瞬间也算进去时，锁出来的目标是 160 ms，
        // 比 static 的 80 ms 还差——起播时环刚从预填充建立、调度也最乱，
        // 那一下的掉坑不代表网络常态。标定的目的是找**稳态够用的深度**。
        private const int WarmupMs = 1500;
        private readonly System.Collections.Generic.List<double> _dips =
            new System.Collections.Generic.List<double>(2048);

        /// <summary>锁定用的分位：忽略孤立尖峰，取覆盖 95% 情况的掉坑。</summary>
        private const double DipPercentile = 0.95;

        public CalibratedPolicy(int startMs, int minMs, int maxMs, int calibrateSec)
        {
            _minMs = Math.Max(5, Math.Min(minMs, maxMs));
            _maxMs = Math.Max(_minMs, maxMs);
            _calibrateSec = Math.Max(1, calibrateSec);
            _startMs = Math.Max(_minMs, Math.Min(_maxMs, startMs));
            _targetMs = _startMs;
        }

        public string Name { get { return "calibrated"; } }
        public int TargetMs { get { return (int)Math.Round(_targetMs); } }
        public bool Frozen { get { return _frozen; } }

        /// <summary>
        /// 标定期间发生欠载：记为一次"整段掉光"的样本。
        ///
        /// 不这么做的话，标定会只看水位样本而漏掉真正的失败——
        /// 实测 40ms 抖动下标定锁到 90ms，但仍欠载 4 次，
        /// 因为水库只在读取瞬间采样，块内被读空它看不见。
        /// </summary>
        public void NotifyUnderrun()
        {
            if (_frozen) return;
            if (_observedMs < WarmupMs) return;
            if (_dips.Count < 2048) _dips.Add(_targetMs);   // 整段掉光
        }

        public void Observe(int availableFrames, int sampleRate)
        {
            if (sampleRate <= 0) return;

            if (_frozen) return;

            _observedMs += 16;

            // 启动阶段不采样：那时环刚由预填充建立、调度也最乱，
            // 把它算进去会把标定值推高（实测曾因此锁到 160ms，
            // 反而比固定 80ms 更差）
            if (_observedMs < WarmupMs) return;

            double availMs = availableFrames * 1000.0 / sampleRate;
            double dipMs = _targetMs - availMs;
            if (dipMs > 0 && _dips.Count < 2048) _dips.Add(dipMs);

            if (_observedMs >= _calibrateSec * 1000)
            {
                _targetMs = ComputeLocked();
                _frozen = true;
            }
        }

        /// <summary>
        /// 由稳态掉坑样本算出锁定值：取 95 分位（忽略孤立尖峰），
        /// 再乘 1.5 安全系数并加上下限，最后钳进 [min,max]。
        ///
        /// 用分位而不是最大值：一次开机、一次系统更新、一次别的程序抢 CPU
        /// 都会造出一个极大的瞬时掉坑，用它定标会让之后永远背着多余的延迟。
        /// </summary>
        private double ComputeLocked()
        {
            double dip;
            if (_dips.Count == 0) dip = 0;
            else
            {
                var sorted = new System.Collections.Generic.List<double>(_dips);
                sorted.Sort();
                int idx = (int)Math.Round((sorted.Count - 1) * DipPercentile);
                if (idx < 0) idx = 0;
                if (idx >= sorted.Count) idx = sorted.Count - 1;
                dip = sorted[idx];
            }

            double locked = _minMs + 1.5 * dip;
            if (locked > _maxMs) locked = _maxMs;
            if (locked < _minMs) locked = _minMs;
            return locked;
        }

        public string Status()
        {
            double dip = 0;
            if (_dips.Count > 0)
            {
                var sorted = new System.Collections.Generic.List<double>(_dips);
                sorted.Sort();
                int idx = (int)Math.Round((sorted.Count - 1) * DipPercentile);
                if (idx >= 0 && idx < sorted.Count) dip = sorted[idx];
            }

            if (_frozen)
                return string.Format(CultureInfo.InvariantCulture,
                    "calibrated 已锁定 {0:F0} ms（标定 {1} s，稳定期 {2} 分位掉坑 {3:F1} ms，样本 {4}）",
                    _targetMs, _calibrateSec, DipPercentile * 100, dip, _dips.Count);

            return string.Format(CultureInfo.InvariantCulture,
                "calibrated 标定中 {0}/{1} s，当前 {2:F0} ms，{3} 分位掉坑 {4:F1} ms，样本 {5}",
                _observedMs / 1000, _calibrateSec, _targetMs, DipPercentile * 100, dip, _dips.Count);
        }
    }

    internal static class BufferPolicyFactory
    {
        /// <summary>按档位创建策略。取值非法时记录原因并退回 static，不静默。</summary>
        public static IBufferPolicy Create(Profile p)
        {
            if (p == null) return new StaticPolicy(80);

            string mode = (p.BufferMode ?? "static").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "adaptive":
                    return new AdaptivePolicy(p.TargetBufferMs, p.MinBufferMs, p.MaxBufferMs, p.AdaptRate);
                case "calibrated":
                    return new CalibratedPolicy(p.TargetBufferMs, p.MinBufferMs, p.MaxBufferMs, p.CalibrateSec);
                case "static":
                    return new StaticPolicy(p.TargetBufferMs);
                default:
                    Logger.Warn("缓冲策略取值无效：" + p.BufferMode + "（应为 static / adaptive / calibrated），" +
                                "已退回 static");
                    return new StaticPolicy(p.TargetBufferMs);
            }
        }
    }
}
