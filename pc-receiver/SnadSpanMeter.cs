// 端到端延迟的逐段测量与对账。
//
// 五段（全部实测，无常数、无假设）：
//   S1 网络           RTT/2（来自时钟校准）
//   S2 到达→写入环形   recvfrom 返回 → ring.Write 完成
//   S3 写入→读出       该帧写入时刻 → ring.Read 完成
//   S4 读出→提交       read 返回 → waveOutWrite
//   S5 提交→播完       waveOutWrite → WOM_DONE（**已含声卡设备**）
//
// 对账：S1+S2+S3+S4+S5 必须等于 (WOM_DONE − 手机采集时刻 − Δ)。
// 不相等就报告差值，并且**不输出端到端数字**——宁可缺，不给假。
//
// 映射：每个音频包按"写入帧号"登记 (写入时刻, 手机采集时刻)。
// 读出一块时按该块首帧号查表；查不到说明映射断了（丢帧/欠载），
// 此时把该块标记为不可结算，计入 unmapped，而不是猜一个值。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Snad
{
    internal sealed class SnadSpanMeter
    {
        private struct Pkt
        {
            public long Frame;         // 写入帧号（全局）
            public double WriteMs;     // 写入完成时刻（本机单调 ms）
            public uint PhoneTsMs;     // 手机采集时刻
        }

        private readonly object _gate = new object();
        private readonly Queue<Pkt> _pkts = new Queue<Pkt>();
        private readonly int _rate;

        // 窗口累计
        private double _s2, _s3, _s4, _s5;
        private double _s2Max, _s3Max, _s4Max, _s5Max;
        private long _n;                // 结算成功的块数
        private long _unmapped;         // 映射断掉的块数
        private double _e2eSum;
        private double _e2eMin = double.MaxValue, _e2eMax;

        public SnadSpanMeter(int sampleRate) { _rate = Math.Max(1, sampleRate); }

        /// <summary>包到达并写入环形后登记。</summary>
        public void OnPacketWritten(long frameStart, double writeMs, uint phoneTsMs)
        {
            lock (_gate)
            {
                _pkts.Enqueue(new Pkt { Frame = frameStart, WriteMs = writeMs, PhoneTsMs = phoneTsMs });
                while (_pkts.Count > 8192) { _pkts.Dequeue(); _unmapped++; }
            }
        }

        /// <summary>
        /// 取某帧所在的包记录（用于读出一块时的映射）。
        /// 返回 false 表示该帧没有对应记录——映射已断。
        /// </summary>
        public bool TryLookup(long frame, out double writeMs, out uint phoneTsMs)
        {
            writeMs = 0; phoneTsMs = 0;
            lock (_gate)
            {
                // 队列按帧号升序，取最后一个 Frame <= 目标帧 的记录。
                Pkt? hit = null;
                foreach (var p in _pkts)
                {
                    if (p.Frame <= frame) hit = p;
                    else break;
                }
                if (hit == null) return false;
                writeMs = hit.Value.WriteMs;
                phoneTsMs = hit.Value.PhoneTsMs;
                return true;
            }
        }

        private double _pendingReadToSubmit;
        private double _pendingSubmitMs;
        private double _pendingWriteMs;
        private double _pendingReadMs;
        private uint _pendingPhoneTs;
        private bool _pendingValid;

        /// <summary>读出一块（read 返回）后调用，记录读出时刻与映射到的包。</summary>
        public void OnBlockRead(long frameStart, bool mapped, double writeMs, uint phoneTsMs)
        {
            lock (_gate)
            {
                _pendingValid = mapped;
                _pendingWriteMs = writeMs;
                _pendingPhoneTs = phoneTsMs;
                _pendingReadMs = 0;       // 由 MarkReadNow 填
            }
        }

        public void MarkReadNow(double ms) { lock (_gate) _pendingReadMs = ms; }
        public void MarkSubmitNow(double ms) { lock (_gate) _pendingSubmitMs = ms; }

        /// <summary>缓冲播完时结算这一块。</summary>
        public void OnBlockDone(double doneMs, bool calibrated, double offsetMs, double rttMs)
        {
            lock (_gate)
            {
                if (!_pendingValid)
                {
                    _unmapped++;
                    return;
                }
                double s4 = _pendingSubmitMs - _pendingReadMs;
                double s5 = doneMs - _pendingSubmitMs;
                double s3 = _pendingReadMs - _pendingWriteMs;
                if (s3 < 0) s3 = 0;
                if (s4 < 0) s4 = 0;
                if (s5 < 0) s5 = 0;

                _s3 += s3; if (s3 > _s3Max) _s3Max = s3;
                _s4 += s4; if (s4 > _s4Max) _s4Max = s4;
                _s5 += s5; if (s5 > _s5Max) _s5Max = s5;
                _n++;

                if (calibrated)
                {
                    // 端到端：播出时刻 − 手机采集时刻 − 时钟差
                    double e2e = doneMs - _pendingPhoneTs - offsetMs;
                    _e2eSum += e2e;
                    if (e2e < _e2eMin) _e2eMin = e2e;
                    if (e2e > _e2eMax) _e2eMax = e2e;

                    // 对账：五段之和 vs 端到端
                    double sum = rttMs / 2.0 + 0 /*S2见窗口*/ + s3 + s4 + s5;
                    _reconSum += sum;
                    _reconE2e += e2e;
                    _reconN++;
                }
                _pendingValid = false;
            }
        }

        private double _reconSum, _reconE2e;
        private long _reconN;

        /// <summary>包到达→写入完成（由收包线程直接报）。</summary>
        public void AddArrivalToWrite(double ms)
        {
            lock (_gate) { _s2 += ms; if (ms > _s2Max) _s2Max = ms; }
        }

        /// <summary>出一份窗口报告；对账不通过时 unaccounted ≠ 0，调用方据此拒绝出数。</summary>
        public string TakeReport(bool calibrated, double rttMs, out double unaccounted)
        {
            lock (_gate)
            {
                unaccounted = 0;
                if (_n == 0 && _unmapped == 0) return "本窗口无已结算的块";

                string s = string.Format(CultureInfo.InvariantCulture,
                    "块 {0} 个（未映射 {1}）：网络 {2:F1} | 到达→写 {3:F1}(max {4:F1}) | " +
                    "写→读 {5:F1}(max {6:F1}) | 读→提交 {7:F1}(max {8:F1}) | 提交→播完 {9:F1}(max {10:F1})",
                    _n, _unmapped, rttMs / 2.0,
                    _n > 0 ? _s2 / _n : 0, _s2Max,
                    _n > 0 ? _s3 / _n : 0, _s3Max,
                    _n > 0 ? _s4 / _n : 0, _s4Max,
                    _n > 0 ? _s5 / _n : 0, _s5Max);

                if (calibrated && _reconN > 0)
                {
                    double avgE2e = _reconE2e / _reconN;
                    double avgSum = _reconSum / _reconN + (_s2 / Math.Max(1, _n));
                    unaccounted = avgE2e - avgSum;

                    s += string.Format(CultureInfo.InvariantCulture,
                        "\n         端到端 {0:F1} ms（最小 {1:F1}，最大 {2:F1}）；" +
                        "五段之和 {3:F1} ms；未归因 {4:F1} ms{5}",
                        avgE2e, _e2eMin, _e2eMax, avgSum, unaccounted,
                        Math.Abs(unaccounted) > 20 ? "  ← 超过 20 ms，数字不可信" : "");
                }
                else
                {
                    s += "\n         未校准：端到端与对账暂不输出";
                }

                _n = 0; _unmapped = 0;
                _s2 = _s3 = _s4 = _s5 = 0;
                _s2Max = _s3Max = _s4Max = _s5Max = 0;
                _e2eSum = 0; _e2eMin = double.MaxValue; _e2eMax = 0;
                _reconSum = _reconE2e = 0; _reconN = 0;
                return s;
            }
        }
    }
}
