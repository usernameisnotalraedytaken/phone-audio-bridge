// 时钟校准的运行时部分：定期向手机发探测、收回复、算出 Δ。
//
// 原则与整个项目一致：任何失败都要说清楚是哪一步、为什么，
// 不允许"悄悄用 0 顶上"。拿不到样本时 Δ 就是未知，端到端延迟也就不可用——
// 这一点必须让上层能看见（HasCalibration），而不是给一个看起来合理的数。

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Snad
{
    internal sealed class SnadClockSync : IDisposable
    {
        private readonly byte[] _key;
        private readonly IPEndPoint _target;
        private readonly Action<string> _log;
        private readonly Thread _thread;
        private volatile bool _running = true;

        private uint _nonce;
        private double _offsetMs;
        private double _bestRttMs = double.MaxValue;
        private int _samples;
        private long _failures;
        private string _lastError;

        public SnadClockSync(string psk, IPAddress phone, int port, Action<string> log)
        {
            if (phone == null) throw new ArgumentNullException("phone");
            _key = SnadTimeSync.DeriveKey(psk);
            _target = new IPEndPoint(phone, port);
            _log = log;
            _thread = new Thread(Loop) { IsBackground = true, Name = "snad-clock-sync" };
        }

        public void Start() { _thread.Start(); }

        /// <summary>是否已拿到至少一个可用样本。为 false 时端到端延迟不可用。</summary>
        public bool HasCalibration { get { return _samples > 0; } }

        /// <summary>PC 时钟 − 手机时钟（ms）。未校准时无意义，先查 HasCalibration。</summary>
        public double OffsetMs { get { return _offsetMs; } }

        public int Samples { get { return _samples; } }
        public double BestRttMs { get { return _bestRttMs == double.MaxValue ? -1 : _bestRttMs; } }
        public long Failures { get { return _failures; } }
        public string LastError { get { return _lastError; } }

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static double NowMs() { return Stopwatch.GetTimestamp() * MsPerTick; }

        private void Loop()
        {
            // 前 8 次快速连打，拿到第一批样本；之后每 30 秒补一次。
            for (int i = 0; i < 8 && _running; i++)
            {
                Probe(1500);
                Thread.Sleep(200);
            }

            while (_running)
            {
                for (int s = 0; s < 30 && _running; s++) Thread.Sleep(1000);
                if (_running) Probe(1500);
            }
        }

        /// <summary>
        /// 发一次探测并等回复。超时、报文错误、时间不可比都各自留痕，
        /// 并计入 Failures——上层据此判断"校准到底有没有在工作"。
        /// </summary>
        private void Probe(int timeoutMs)
        {
            uint nonce = ++_nonce;
            try
            {
                using (var sock = new UdpClient(0))
                {
                    sock.Client.ReceiveTimeout = timeoutMs;

                    // T1 取在**发送之前这一瞬间**：发送调用本身的耗时应计入网络，
                    // 而不是算成 PC 侧的处理时间。
                    uint t1 = unchecked((uint)(long)NowMs());
                    var probe = SnadTimeSync.BuildProbe(_key, nonce, t1);

                    double t1Exact = NowMs();
                    sock.Send(probe, probe.Length, _target);

                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] reply = sock.Receive(ref from);

                    uint rNonce, rt1, recv, send;
                    string err;
                    if (!SnadTimeSync.TryParseReply(reply, reply.Length, _key,
                                                    out rNonce, out rt1, out recv, out send, out err))
                    {
                        Fail("回复包不合法：" + err); return;
                    }
                    if (rNonce != nonce)
                    {
                        Fail("nonce 不匹配：发出 " + nonce + "，回来 " + rNonce + "（可能是重放的旧包）");
                        return;
                    }

                    double rtt, offset;
                    if (!SnadTimeSync.TryComputeOffset(t1, unchecked((uint)(long)NowMs()), recv, send,
                                                       out rtt, out offset, out err))
                    {
                        Fail("时间不可比：" + err); return;
                    }

                    _samples++;
                    if (rtt < _bestRttMs) _bestRttMs = rtt;

                    // 只接受接近最优 RTT 的样本：RTT 大的往往意味着排队严重，
                    // 而那些样本的 RTT/2 假设最不成立。
                    if (_samples == 1 || rtt <= _bestRttMs * 1.5)
                        _offsetMs = offset;

                    _log(string.Format(CultureInfo.InvariantCulture,
                        "[时钟] 样本#{0} RTT={1:F1}ms Δ={2:F2}ms（最优 RTT {3:F1}ms，采用 Δ={4:F2}ms，" +
                        "失败 {5} 次）",
                        _samples, rtt, offset, _bestRttMs, _offsetMs, _failures));
                }
            }
            catch (SocketException ex)
            {
                Fail("等待回复超时或网络错误：" + ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Fail(string why)
        {
            _failures++;
            _lastError = why;
            // 每次都报：校准失败意味着端到端延迟不可用，不能只在第一次说一声。
            _log("[时钟] 校准失败（累计 " + _failures + " 次）：" + why);
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
