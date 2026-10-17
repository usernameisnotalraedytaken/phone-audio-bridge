// 端到端延迟的第三段时间：音频什么时候真正播出。
//
// 前两段已经有了：手机采集时刻（包里的时间戳）、到达 PC 的时刻。
// 这一段要回答"这一帧是几点几分播出来的"，只有三者齐了才能算延迟。
//
// 做法：每个提交给声卡的缓冲覆盖一段连续的帧号；缓冲播完（WOM_DONE）时，
// 这段音频的最后一帧就在那一刻离开队列，于是缓冲内第 k 帧的播出时刻
// 等于"完成时刻 − 剩余帧数 ÷ 采样率"。包按首帧帧号落进对应缓冲即可。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Snad
{
    internal sealed class SnadLatencyMeter
    {
        /// <summary>
        /// 时钟校准器。播放线程是静态方法，拿不到接收器实例，
        /// 因此由 meter 持有这个引用——它是两条线程之间唯一的共享点。
        /// </summary>
        public SnadClockSync Sync;

        /// <summary>
        /// 逐段测量器。挂在 meter 上带进静态播放线程——
        /// 播放线程拿不到接收器实例，只能经由这个共享点访问。
        /// </summary>
        public SnadSpanMeter Spans;

        /// <summary>本机单调毫秒。播放线程用它算缓冲完成时刻。</summary>
        public static double NowMs()
        {
            return Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
        }

        private struct Pending
        {
            public long Frame;
            public uint PhoneTsMs;
        }

        private readonly object _gate = new object();
        private readonly Queue<Pending> _pending = new Queue<Pending>();
        private readonly int _rate;

        // 统计量：最近一个窗口
        private double _sumMs, _minMs = double.MaxValue, _maxMs;
        private long _count, _orphans;

        /// <summary>已读出的总帧数，用于给每个缓冲分配帧区间。</summary>
        private long _framesRead;

        // ---- 段：提交 → 播完（含声卡设备）----
        //
        // WOM_DONE 触发时，这块缓冲里的音频已经真正播出，所以这一段
        // **已经包含设备缓冲**，不需要另外假设一个"设备 12 ms"。
        // 这是目前唯一插好桩的 PC 内部段，先把它报出来。
        private double _bufDurSum, _bufDurMin = double.MaxValue, _bufDurMax;
        private long _bufDurCount;

        public SnadLatencyMeter(int sampleRate) { _rate = Math.Max(1, sampleRate); }

        /// <summary>缓冲已提交时登记它的帧区间，返回该区间的首帧号。</summary>
        public long OnBufferSubmitted(int frames)
        {
            long start;
            lock (_gate) { start = _framesRead; _framesRead += frames; }
            return start;
        }

        /// <summary>
        /// 结算"提交 → 播完"这一段，并返回该缓冲的时长（ms）。
        /// 调用方传入提交时刻；这里只做统计，不参与包结算。
        /// </summary>
        public double OnBufferFinished(double submitMs, double doneMs)
        {
            double dur = doneMs - submitMs;
            if (dur < 0) dur = 0;
            lock (_gate)
            {
                _bufDurSum += dur;
                if (dur < _bufDurMin) _bufDurMin = dur;
                if (dur > _bufDurMax) _bufDurMax = dur;
                _bufDurCount++;
            }
            return dur;
        }

        /// <summary>取出并清空"提交→播完"窗口统计。</summary>
        public string TakeBufferWindow()
        {
            lock (_gate)
            {
                if (_bufDurCount == 0) return null;
                string s = string.Format(CultureInfo.InvariantCulture,
                    "提交→播完（含设备）样本 {0}，均 {1:F1} ms，最小 {2:F1}，最大 {3:F1}",
                    _bufDurCount, _bufDurSum / _bufDurCount, _bufDurMin, _bufDurMax);
                _bufDurCount = 0; _bufDurSum = 0; _bufDurMin = double.MaxValue; _bufDurMax = 0;
                return s;
            }
        }

        /// <summary>收到音频包时登记它的首帧号与手机采集时刻。</summary>
        public void OnPacket(uint phoneTsMs, long startFrame)
        {
            lock (_gate)
            {
                _pending.Enqueue(new Pending { Frame = startFrame, PhoneTsMs = phoneTsMs });

                // 队列无限增长说明映射断了（比如 playback 线程没在跑），
                // 留一个上界并计数，避免把内存拖垮还把问题藏起来。
                if (_pending.Count > 4096)
                {
                    _pending.Dequeue();
                    _orphans++;
                }
            }
        }

        /// <summary>
        /// 缓冲播完。doneMs 与手机时间戳同尺度（本机单调毫秒）。
        /// 返回本次结算出延迟的包数。
        /// </summary>
        public int OnBufferDone(long startFrame, int frames, double doneMs, bool calibrated, double offsetMs)
        {
            int settled = 0;
            double bufEndMs = doneMs;
            lock (_gate)
            {
                while (_pending.Count > 0)
                {
                    var p = _pending.Peek();
                    if (p.Frame >= startFrame + frames) break;      // 属于更后面的缓冲
                    _pending.Dequeue();

                    // 已完成缓冲覆盖 [startFrame, startFrame+frames)。
                    // 该帧的播出时刻 = 完成时刻 − 它之后还剩多少帧的时长。
                    long remain = (startFrame + frames) - p.Frame;
                    double playMs = bufEndMs - remain * 1000.0 / _rate;
                    double raw = playMs - p.PhoneTsMs;              // 含时钟差
                    double latency = calibrated ? raw - offsetMs : raw;

                    _sumMs += latency;
                    if (latency < _minMs) _minMs = latency;
                    if (latency > _maxMs) _maxMs = latency;
                    _count++;
                    settled++;
                }
            }
            return settled;
        }

        /// <summary>
        /// 逐段剖分。返回值里每一项都有独立来源，**合计等于各段之和**——
        /// 这才是有用的东西：能看出延迟长在链路的哪一段，而不是只有一个总数。
        /// </summary>
        /// <param name="arrivalMinusTs">到达 − 手机采集时刻（含两端时钟差）</param>
        /// <param name="offsetMs">时钟差 Δ = PC − 手机</param>
        /// <param name="rttMs">往返时延，用于估计单程</param>
        /// <param name="ringMs">环形缓冲当前水位</param>
        /// <param name="poolMs">waveOut 池在飞（块×个数）</param>
        /// <param name="deviceMs">声卡设备缓冲（估计值，见下方注释）</param>
        public static string Breakdown(double arrivalMinusTs, double offsetMs, double rttMs,
                                       double ringMs, double poolMs, double deviceMs,
                                       out double total)
        {
            // 手机采集 → 包离开手机：从"到达−采集"里先扣掉时钟差，再扣掉网络单程。
            // 剩下的是手机内部的处理与采集缓冲。这是**间接得到**的，
            // 因为手机目前不上报采集缓冲（v2 协议没有能力上报）。
            double oneWay = rttMs / 2.0;
            double phoneInternal = arrivalMinusTs - offsetMs - oneWay;
            if (phoneInternal < 0) phoneInternal = 0;   // 网络估计偏大时会出现负值

            total = phoneInternal + oneWay + ringMs + poolMs + deviceMs;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("  手机采集→发包   " + F(phoneInternal) + "   （到达−采集 " + F(arrivalMinusTs) +
                          " − 时钟差 " + F(offsetMs) + " − 单程 " + F(oneWay) + "）");
            sb.AppendLine("  网络(去程)      " + F(oneWay) + "   （RTT " + F(rttMs) + " ÷ 2）");
            sb.AppendLine("  PC 环形缓冲     " + F(ringMs) + "   （水位）");
            sb.AppendLine("  waveOut 池      " + F(poolMs) + "   （块 × 个数）");
            sb.AppendLine("  声卡设备        " + F(deviceMs) + "   （估计值，未逐机实测）");
            sb.Append("  ──────────────────────────────\n");
            sb.Append("  合计            " + F(total));
            return sb.ToString();
        }

        private static string F(double ms)
        {
            return ms.ToString("F1", CultureInfo.InvariantCulture).PadLeft(8) + " ms";
        }

        /// <summary>取出并清空本窗口统计，写日志用。</summary>
        public string TakeWindow(out bool calibratedWindow)
        {
            lock (_gate)
            {
                calibratedWindow = _count > 0;
                if (_count == 0)
                {
                    return "暂无样本（未收到可结算的包）";
                }
                string s = string.Format(CultureInfo.InvariantCulture,
                    "样本 {0}，均 {1:F1} ms，最小 {2:F1} ms，最大 {3:F1} ms",
                    _count, _sumMs / _count, _minMs, _maxMs);
                _count = 0; _sumMs = 0; _minMs = double.MaxValue; _maxMs = 0;
                return s;
            }
        }

        public long Orphans { get { lock (_gate) return _orphans; } }
        public long FramesRead { get { lock (_gate) return _framesRead; } }
    }
}
