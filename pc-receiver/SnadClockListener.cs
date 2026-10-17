// 采样钟接收：解析手机每 50 ms 发来的 'K' 报文。
//
// 它解决的是端到端测量的**起点**：手机某帧是什么时候被系统产出的。
// 音频包里那个时间戳是"应用读到样点"的时刻，比真正产出晚一个采集缓冲；
// 'K' 报文带的是 getTimestamp() 给的采集时刻，才是起点。
//
// 全部失败路径都要留痕：解析错、端口占用、超时都各有日志，
// 不允许"没收到"和"收到了但没用上"看起来一样。

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Snad
{
    internal sealed class SnadClockListener : IDisposable
    {
        public const int Port = 6984;
        private const int PacketSize = 44;

        private readonly Action<string> _log;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private UdpClient _sock;

        private long _received, _badMagic, _badVersion, _badType, _badLength;

        // 最近一包的内容
        private int _lastSeq = -1;
        private long _lastFrames;
        private long _captureNs;
        private long _monoNs;
        private long _bootNs;
        private int _bufferFrames;
        private int _chunkFrames;
        private bool _tsOk;
        private double _lastRecvMs;

        /// <summary>是否收到过任何一个 'K' 包。</summary>
        public bool HasSample { get { return Interlocked.Read(ref _received) > 0; } }

        /// <summary>手机侧 getTimestamp 是否可用（最近一包的 flags）。</summary>
        public bool PhoneTimestampAvailable { get { return _tsOk; } }

        public long Received { get { return Interlocked.Read(ref _received); } }
        public long BadMagic { get { return Interlocked.Read(ref _badMagic); } }
        public long BadVersion { get { return Interlocked.Read(ref _badVersion); } }
        public long BadType { get { return Interlocked.Read(ref _badType); } }
        public long BadLength { get { return Interlocked.Read(ref _badLength); } }

        public int LastSeq { get { return _lastSeq; } }
        public long LastFrames { get { return _lastFrames; } }
        public int BufferFrames { get { return _bufferFrames; } }
        public int ChunkFrames { get { return _chunkFrames; } }

        /// <summary>CLOCK_MONOTONIC 与 CLOCK_BOOTTIME 的差（纳秒）。休眠会让这个差值变化。</summary>
        public long ClockDomainDeltaNs { get { return _bootNs - _monoNs; } }

        public SnadClockListener(Action<string> log)
        {
            _log = log;
            _thread = new Thread(Loop) { IsBackground = true, Name = "snad-clock-rx" };
        }

        public void Start()
        {
            try
            {
                _sock = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
                _sock.Client.ReceiveTimeout = 1000;
                _thread.Start();
                _log("[采样钟] 监听 " + Port + " 端口");
            }
            catch (Exception ex)
            {
                // 端口占用必须说清楚：常见原因是上一实例没退干净
                _log("[采样钟] 绑定 " + Port + " 失败（" + ex.GetType().Name + "：" + ex.Message +
                     "）——采样钟不可用，端到端起点只能退化为包时间戳");
                throw;
            }
        }

        private void Loop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] p = _sock.Receive(ref remote);
                    Parse(p, p.Length, remote);
                }
                catch (SocketException ex)
                {
                    if (!_running) break;
                    if (ex.SocketErrorCode != SocketError.TimedOut)
                        _log("[采样钟] 接收异常：" + ex.SocketErrorCode);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log("[采样钟] 解析异常：" + ex.GetType().Name + "：" + ex.Message);
                }
            }
        }

        private static long GetI64(byte[] b, int off)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)b[off + i] << (8 * i);
            return v;
        }

        private static int GetI32(byte[] b, int off)
        {
            return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
        }

        /// <summary>
        /// 自测钩子：不经过 socket，直接把一段字节喂进解析流程。
        /// 存在的理由很实际——没有它，'K' 报文的解析只能靠"装到手机上看看"，
        /// 而这条路今晚已经证明代价很高。
        /// </summary>
        public void TestParse(byte[] p, int len)
        {
            Parse(p, len, new IPEndPoint(IPAddress.Loopback, 0));
        }

        private void Parse(byte[] p, int len, IPEndPoint from)
        {
            if (len < PacketSize) { Interlocked.Increment(ref _badLength); return; }
            if (p[0] != 'S' || p[1] != 'N' || p[2] != 'A' || p[3] != 'D')
            {
                Interlocked.Increment(ref _badMagic); return;
            }
            if (p[4] != 1) { Interlocked.Increment(ref _badVersion); return; }
            if (p[5] != 'K') { Interlocked.Increment(ref _badType); return; }

            _lastSeq = GetI32(p, 6);
            _lastFrames = GetI32(p, 10) & 0xFFFFFFFFL;
            _captureNs = GetI64(p, 14);
            _bootNs = GetI64(p, 22);
            _bufferFrames = GetI32(p, 34);
            _chunkFrames = GetI32(p, 38);
            _tsOk = (p[42] & 1) != 0;
            _lastRecvMs = SnadLatencyMeter.NowMs();

            long n = Interlocked.Increment(ref _received);
            if (n == 1 || n % 200 == 0)
            {
                _log(string.Format(CultureInfo.InvariantCulture,
                    "[采样钟] 第 {0} 包（来自 {1}）：seq={2} 帧累计={3} 采集时刻={4} " +
                    "缓冲={5}帧 读块={6}帧 getTimestamp={7}",
                    n, from.Address, _lastSeq, _lastFrames,
                    _tsOk ? _captureNs.ToString(CultureInfo.InvariantCulture) : "不可用",
                    _bufferFrames, _chunkFrames, _tsOk ? "可用" : "不可用"));
            }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_sock != null) _sock.Close(); } catch { }
        }
    }
}
