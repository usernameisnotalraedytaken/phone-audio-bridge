// 信标协议验证工具。
//
// 为什么需要它：手机端还没实现，但协议必须先在自己机器上证明能跑通——
// 广播、HMAC 校验、防重放三者都要有可观测的结果，而不是等两端都写完再一起调。
//
// 用法：
//   SnadBeaconTest.exe --listen              只监听，打印收到的合法信标
//   SnadBeaconTest.exe --broadcast           只广播（可与 --listen 同时用）
//   SnadBeaconTest.exe --listen --broadcast  本机自收自发，验证全链路
//   SnadBeaconTest.exe --tamper              发送一个被篡改的报文，验证会被拒
//   SnadBeaconTest.exe --replay              重放同一 seq，验证会被拒
//
// 退出码：0 表示断言全部通过，1 表示有断言失败。

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Snad
{
    internal static class BeaconTest
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + what);
            if (!ok) _failures++;
        }

        private static int Main(string[] args)
        {
            bool listen = false, broadcast = false, tamper = false, replay = false;
            string bindAddr = null;
            List<string> targets = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--listen": listen = true; break;
                    case "--broadcast": broadcast = true; break;
                    case "--tamper": tamper = true; break;
                    case "--replay": replay = true; break;
                    case "--bind":
                        if (i + 1 < args.Length) bindAddr = args[++i];
                        break;
                    case "--to":
                        // 指定广播目标地址，可重复。用于比较受限广播与子网广播
                        // 在本机各网卡上的实际可达性。
                        if (i + 1 < args.Length) targets.Add(args[++i]);
                        break;
                    case "--help": case "-h":
                        Console.WriteLine("用法：--listen | --broadcast | --tamper | --replay");
                        Console.WriteLine("      --bind <本机地址>   监听绑定到指定网卡地址");
                        Console.WriteLine("      --to <目标地址>     发送到指定地址（可重复），默认 255.255.255.255");
                        return 0;
                }
            }
            if (!listen && !broadcast && !tamper && !replay) { listen = true; broadcast = true; }
            if (targets.Count == 0) targets.Add("255.255.255.255");

            Console.WriteLine("=== SNAD 信标验证 ===");
            Console.WriteLine("KeyId = " + SnadSecret.KeyId + "（两端必须一致）");
            Console.WriteLine("端口  = " + SnadBeacon.DefaultPort);
            Console.WriteLine();

            byte[] key;
            try
            {
                key = SnadBeacon.DeriveKey(SnadSecret.Psk);
                Console.WriteLine("密钥派生成功（派生密钥长度 " + key.Length + " 字节）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("密钥派生失败：" + ex.Message);
                return 1;
            }
            Console.WriteLine();

            // ---- 派生规则与 KeyId 必须自洽 ----
            string computedId = SnadBeacon.ComputeKeyId(SnadSecret.Psk);
            Check(computedId == SnadSecret.KeyId,
                  "KeyId 自洽（编译期 " + SnadSecret.KeyId + " == 运行期 " + computedId + "）");

            // ---- 本机往返：组包 -> 解析 ----
            long seq = 4242;
            byte[] pkt = SnadBeacon.Build("snad-pc-test", 6981, seq, key);
            string reason;
            var parsed = SnadBeacon.TryParse(pkt, pkt.Length, key, "127.0.0.1", out reason);
            Check(parsed != null, "本机往返解析成功" + (parsed == null ? "（原因：" + reason + "）" : ""));
            if (parsed != null)
            {
                Check(parsed.DeviceId == "snad-pc-test", "device_id 读写一致：" + parsed.DeviceId);
                Check(parsed.Port == 6981, "端口读写一致：" + parsed.Port);
                Check(parsed.Seq == seq, "seq 读写一致：" + parsed.Seq);
                Check(parsed.SourceAddress == "127.0.0.1", "源地址取自调用方传入值：" + parsed.SourceAddress);
            }

            // ---- 错误密钥必须被拒 ----
            byte[] wrongKey = SnadBeacon.DeriveKey("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            var bad = SnadBeacon.TryParse(pkt, pkt.Length, wrongKey, "127.0.0.1", out reason);
            Check(bad == null, "错误密钥被拒（原因：" + reason + "）");

            // ---- 被篡改的报文必须被拒 ----
            byte[] evil = (byte[])pkt.Clone();
            evil[SnadBeacon.FixedHeaderSize + 1] ^= 0xFF;   // 改 device_id 一个字节
            var tam = SnadBeacon.TryParse(evil, evil.Length, key, "127.0.0.1", out reason);
            Check(tam == null, "篡改报文被拒（原因：" + reason + "）");

            // ---- 防重放 ----
            var guard = new SnadBeacon.ReplayGuard();
            Check(guard.Accept("dev-a", 100), "防重放：首次 seq=100 接受");
            Check(!guard.Accept("dev-a", 100), "防重放：重复 seq=100 拒绝");
            Check(!guard.Accept("dev-a", 99), "防重放：回退 seq=99 拒绝");
            Check(guard.Accept("dev-a", 101), "防重放：递增 seq=101 接受");
            Check(guard.Accept("dev-b", 1), "防重放：不同 device 独立计数");

            // ---- 实际网络收发 ----
            BeaconListener listener = null;
            BeaconBroadcaster tx = null;
            int received = 0;
            SnadBeacon.Beacon lastSeen = null;

            try
            {
                if (listen)
                {
                    listener = new BeaconListener(key,
                        (b, r) =>
                        {
                            Interlocked.Increment(ref received);
                            lastSeen = b;
                            Console.WriteLine("  收到信标：device=" + b.DeviceId + " 地址=" + b.SourceAddress +
                                              ":" + b.Port + " seq=" + b.Seq);
                        },
                        m => Console.WriteLine("  [监听] " + m));
                    listener.Start(SnadBeacon.DefaultPort);
                    Thread.Sleep(300);
                }

                if (broadcast)
                {
                    tx = new BeaconBroadcaster("snad-pc-test", 6981, key, 400,
                        m => Console.WriteLine("  [广播] " + m));
                    tx.Start();
                    Console.WriteLine("  广播已启动，等待 2.5 秒观察收发……");
                    Thread.Sleep(2500);
                }

                if (tamper)
                {
                    Console.WriteLine("  发送被篡改的报文（应被监听侧拒绝）……");
                    byte[] evilPkt = SnadBeacon.Build("snad-evil", 6981, 9999, key);
                    evilPkt[10] ^= 0x01;
                    SendRaw(evilPkt);
                    Thread.Sleep(500);
                }

                if (replay)
                {
                    Console.WriteLine("  重放同一 seq 两次（第二次应被拒）……");
                    byte[] r1 = SnadBeacon.Build("snad-replay", 6981, 777, key);
                    SendRaw(r1); Thread.Sleep(300);
                    SendRaw(r1); Thread.Sleep(500);
                }
            }
            finally
            {
                if (tx != null) tx.Dispose();
                if (listener != null) listener.Dispose();
            }

            Console.WriteLine();
            if (listen)
            {
                Check(received > 0, "实际收到了 " + received + " 个合法信标");
                if (listener != null)
                    Check(listener.RejectedCount > 0 || !(tamper || replay),
                          "被拒报文数 " + listener.RejectedCount + "（tamper/replay 场景应大于 0）");
            }
            if (broadcast && tx != null)
                Check(tx.SentCount > 0, "实际发出 " + tx.SentCount + " 个信标");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "全部断言通过" : ("有 " + _failures + " 项断言失败"));
            return _failures == 0 ? 0 : 1;
        }

        private static void SendRaw(byte[] pkt)
        {
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, SnadBeacon.DefaultPort));
            }
        }
    }
}
