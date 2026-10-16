// 局域网信标：广播自身地址 + 监听对端信标。
//
// 用途（对应"握手受限且无感"）：
//   电脑侧每次启动即开始广播自己的监听地址，手机端收到合法信标后直接接入。
//   无需配对步骤、无需服务器、无需知道对方 IP。
//
// 安全性由三件东西共同保证，缺一不可：
//   1. HMAC-SHA256 —— 没密钥算不出合法报文，局域网内其他设备无法伪装
//   2. seq 单调递增 —— 录下旧报文重放会被丢弃
//   3. 源地址取自 UDP 头 —— 报文体里的任何地址声明都不被信任

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Snad
{
    /// <summary>广播自身信标。</summary>
    internal sealed class BeaconBroadcaster : IDisposable
    {
        private readonly string _deviceId;
        private readonly int _port;
        private readonly byte[] _key;
        private readonly int _intervalMs;
        private readonly Action<string> _log;

        private Thread _thread;
        private volatile bool _running;
        private long _seq;
        private long _sent;

        public BeaconBroadcaster(string deviceId, int port, byte[] derivedKey,
                                 int intervalMs, Action<string> log)
        {
            _deviceId = string.IsNullOrEmpty(deviceId) ? "snad-pc" : deviceId;
            _port = port;
            _key = derivedKey;
            _intervalMs = Math.Max(200, intervalMs);
            _log = log;
        }

        public long SentCount { get { return Interlocked.Read(ref _sent); } }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "snad-beacon-tx" };
            _thread.Start();
        }

        private void Loop()
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.EnableBroadcast = true;
                var target = new IPEndPoint(IPAddress.Broadcast, SnadBeacon.DefaultPort);

                while (_running)
                {
                    try
                    {
                        long s = Interlocked.Increment(ref _seq);
                        byte[] pkt = SnadBeacon.Build(_deviceId, _port, s, _key);
                        udp.Send(pkt, pkt.Length, target);
                        Interlocked.Increment(ref _sent);
                    }
                    catch (SocketException ex)
                    {
                        // 广播失败常见于没有可用网络接口（例如网线断开）。
                        // 不静默：每 20 次提示一次，避免刷日志但也不让人蒙在鼓里。
                        if (SentCount % 20 == 0)
                            _log("信标广播失败（第 " + SentCount + " 次后）：" + ex.Message);
                    }
                    Thread.Sleep(_intervalMs);
                }
            }
            catch (Exception ex)
            {
                _log("信标广播线程异常退出：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (udp != null) { try { udp.Close(); } catch { } }
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive)
            {
                try { _thread.Join(1000); } catch { }
            }
        }
    }

    /// <summary>监听对端信标。</summary>
    internal sealed class BeaconListener : IDisposable
    {
        private readonly byte[] _key;
        private readonly Action<SnadBeacon.Beacon, string> _onBeacon;
        private readonly Action<string> _log;
        private readonly SnadBeacon.ReplayGuard _guard = new SnadBeacon.ReplayGuard();

        private Thread _thread;
        private volatile bool _running;
        private long _accepted, _rejected;

        public BeaconListener(byte[] derivedKey, Action<SnadBeacon.Beacon, string> onBeacon, Action<string> log)
        {
            _key = derivedKey;
            _onBeacon = onBeacon;
            _log = log;
        }

        public long AcceptedCount { get { return Interlocked.Read(ref _accepted); } }
        public long RejectedCount { get { return Interlocked.Read(ref _rejected); } }

        public void Start(int port)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(() => Loop(port)) { IsBackground = true, Name = "snad-beacon-rx" };
            _thread.Start();
        }

        private void Loop(int port)
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                _log("信标监听已启动，端口 " + port);

                var remote = new IPEndPoint(IPAddress.Any, 0);
                while (_running)
                {
                    byte[] data;
                    try { data = udp.Receive(ref remote); }
                    catch (SocketException ex)
                    {
                        if (_running) _log("信标接收异常，继续：" + ex.Message);
                        continue;
                    }
                    catch (ObjectDisposedException) { break; }

                    string reason;
                    var b = SnadBeacon.TryParse(data, data.Length, _key,
                                                remote.Address.ToString(), out reason);
                    if (b == null)
                    {
                        Interlocked.Increment(ref _rejected);
                        // 前 3 次以及每 50 次记录一次原因，便于区分"被攻击"与"密钥不一致"
                        long rj = RejectedCount;
                        if (rj <= 3 || rj % 50 == 0)
                            _log("丢弃信标（来自 " + remote + "）：" + reason);
                        continue;
                    }
                    if (!_guard.Accept(b.DeviceId, b.Seq))
                    {
                        Interlocked.Increment(ref _rejected);
                        // 重放是明确的攻击特征，但也可能只是网络乱序，都会到这里
                        _log("丢弃重放/乱序信标：device=" + b.DeviceId +
                             " seq=" + b.Seq + "（已见 " + _guard.LastSeq(b.DeviceId) + "）");
                        continue;
                    }

                    Interlocked.Increment(ref _accepted);
                    _onBeacon(b, reason);
                }
            }
            catch (Exception ex)
            {
                _log("信标监听线程异常退出：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (udp != null) { try { udp.Close(); } catch { } }
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive)
            {
                try { _thread.Join(1000); } catch { }
            }
        }
    }
}
