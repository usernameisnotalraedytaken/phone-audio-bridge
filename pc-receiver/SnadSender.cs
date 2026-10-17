// 发包器：按与手机完全相同的协议向接收器送音频。
//
// 为什么需要它：
//   接收器的行为（抖动缓冲、漂移补偿、后端表现）此前只能拿真手机试，
//   而手机在别的设备上、要人手操作，无法自动回归。发包器把"手机"变成
//   本地一个进程，于是这些行为可以本地复现、自动跑、可注入故障。
//
// 同时它也是**网络损伤模拟器**：丢包、乱序、抖动、时间戳偏移都能注入，
// 用来回答"这个缓冲深度能扛多大抖动"这类问题——这比拿真手机在房间里
// 走来走去试可靠得多。
//
// 协议（与 SnadProtocol.kt 一致，20/22 字节头，小端）：
//   0..3   magic 'S','N','A','D'
//   4      version = 1
//   5      channels
//   6..9   sampleRate
//   10..13 sequence
//   14..17 timestampMs（手机侧单调时钟低 32 位）—— 接收器目前还没用它
//   18..21 payloadBytes
//   22..   PCM16LE 交错
//
// 用法：
//   SnadSender.exe <目标IP> [选项]
//     端口 <port>        默认 6981
//     时长 <秒>          默认 10
//     块 <ms>            每个包携带的音频时长，默认 21
//     丢包 <百分比>      默认 0
//     抖动 <ms>          发送时刻的随机偏移，默认 0
//     乱序 <百分比>      默认 0
//     音调 <Hz>          默认 440
//     时间戳偏移 <ms>    故意让时间戳与真实时刻错开，用于验证延迟测量
//     静默               不发音频，只发包头（用于测极端情况）
//
// 例：SnadSender.exe 127.0.0.1 时长 20 块 21 丢包 1 抖动 3

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Snad
{
    internal static class Sender
    {
        private const int HeaderSize = 22;
        private const int DefaultPort = 6981;

        private static int _sent, _dropped, _reordered;

        /// <summary>一个待发出去的包，连同它的释放时刻。</summary>
        private sealed class PendingPkt
        {
            public double ReleaseAt;      // 相对发送器启动的毫秒数
            public byte[] Payload;
        }

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Usage();
                return 0;
            }

            string host = args[0];
            int port = DefaultPort;
            double seconds = 10;
            int blockMs = 21;
            double lossPct = 0, jitterMs = 0, reorderPct = 0;
            double baseDelayMs = 80;      // 抖动摆动的基准，需 ≥ 最大抖动
            double toneHz = 440;
            int timestampOffsetMs = 0;
            bool silent = false;

            for (int i = 1; i < args.Length - 1; i += 2)
            {
                string k = args[i].ToLowerInvariant();
                string v = args[i + 1];
                switch (k)
                {
                    case "端口": case "port": port = int.Parse(v); break;
                    case "时长": case "seconds": seconds = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "块": case "block": blockMs = int.Parse(v); break;
                    case "丢包": case "loss": lossPct = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "抖动": case "jitter": jitterMs = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "基准延迟": case "base": baseDelayMs = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "乱序": case "reorder": reorderPct = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "音调": case "tone": toneHz = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "时间戳偏移": case "tsoffset": timestampOffsetMs = int.Parse(v); break;
                }
                if (k == "静默" || k == "silent") silent = true;
            }
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "静默" || args[i] == "silent") silent = true;

            const int rate = 48000, channels = 2;
            int framesPerPacket = rate * blockMs / 1000;
            int payloadBytes = framesPerPacket * channels * 2;

            Console.WriteLine("SNAD 发包器");
            Console.WriteLine("  目标      " + host + ":" + port);
            Console.WriteLine("  时长      " + seconds.ToString("F1") + " 秒");
            Console.WriteLine("  块        " + blockMs + " ms（" + framesPerPacket + " 帧 / " +
                              payloadBytes + " 字节）");
            Console.WriteLine("  音调      " + toneHz.ToString("F0") + " Hz" + (silent ? "（静默）" : ""));
            Console.WriteLine("  丢包      " + lossPct.ToString("F1") + " %");
            Console.WriteLine("  抖动      0.." + jitterMs.ToString("F1") + " ms 随机（叠加在基准延迟上）");
            Console.WriteLine("  基准延迟  " + baseDelayMs.ToString("F1") + " ms（抖动摆动空间，需 ≥ 最大抖动）");
            Console.WriteLine("  乱序      " + reorderPct.ToString("F1") + " %");
            if (timestampOffsetMs != 0)
                Console.WriteLine("  时间戳偏移 " + timestampOffsetMs + " ms（故意错开，用于验证测量）");
            Console.WriteLine();

            IPEndPoint target;
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0) { Console.WriteLine("无法解析主机 " + host); return 2; }
                target = new IPEndPoint(addrs[0], port);
            }
            catch (Exception ex)
            {
                Console.WriteLine("解析目标地址失败：" + ex.Message);
                return 2;
            }

            using (var sock = new UdpClient())
            {
                sock.Client.SendBufferSize = 1 << 20;

                var pcm = new byte[payloadBytes];
                double phase = 0;
                int seq = 0;
                var rnd = new Random(20261017);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var startWall = DateTime.UtcNow;

                // 发送队列：采集与发送必须分开两个线程。
                //
                // 抖动是"包已按固定节拍产生、路上各自被耽搁"，因此
                // 采集时刻必须严格每 blockMs 一次，而释放时刻 = 采集 + 基础延迟 + 抖动。
                // 单线程做不到：等完基础延迟就赶不上下一次采集，
                // 于是采集节拍被拖慢、平均吞吐塌掉（实测吞吐掉到 73%，
                // 接收端被饿死——测到的已不是网络，而是源跟不上实时）。
                var queue = new System.Collections.Concurrent.BlockingCollection<PendingPkt>();
                var sender = new Thread(() =>
                {
                    try
                    {
                        foreach (var item in queue.GetConsumingEnumerable())
                        {
                            while (sw.Elapsed.TotalMilliseconds < item.ReleaseAt) Thread.Sleep(1);
                            TrySend(sock, target, item.Payload);
                            _sent++;
                        }
                    }
                    catch { }
                }) { IsBackground = true, Name = "snad-send" };
                sender.Start();

                int packets = (int)(seconds * 1000 / blockMs);

                long nextSendMs = 0;
                for (int p = 0; p < packets; p++)
                {
                    // 采集时刻严格每 blockMs 一次：真实手机的采集缓冲是固定节拍，
                    // 抖动发生在**采集之后**的网络传输上。
                    while (sw.ElapsedMilliseconds < nextSendMs) Thread.Sleep(1);
                    nextSendMs += blockMs;

                    // 采集时刻要在施加传输延迟**之前**取。
                    // 若先延迟再打时间戳，这个包看起来只是"采集得更晚"，
                    // 接收端算出的 arrival − timestamp 完全不变，注入等于没做。
                    double captureAt = sw.Elapsed.TotalMilliseconds;

                    bool drop = rnd.NextDouble() * 100 < lossPct;
                    if (!drop)
                    {
                        FillTone(pcm, framesPerPacket, channels, toneHz, ref phase, silent);

                        long tsRaw = (long)captureAt + timestampOffsetMs;
                        int ts = (int)(tsRaw & 0xFFFFFFFFL);
                        byte[] pkt = BuildPacket(channels, rate, seq, ts, payloadBytes, pcm);

                        // 释放时刻 = 采集 + 基础延迟 + 独立抖动。
                        // 基础延迟给抖动留出摆动空间，使平均到达间隔仍等于采集间隔；
                        // 没有它时抖动会单向地把发送往后推，吞吐随之塌掉。
                        double releaseAt = captureAt + baseDelayMs +
                                           (jitterMs > 0 ? rnd.NextDouble() * jitterMs : 0);

                        if (reorderPct > 0 && rnd.NextDouble() * 100 < reorderPct)
                        {
                            // 额外压后一点再发，制造乱序
                            releaseAt += blockMs * 1.5;
                            _reordered++;
                        }

                        queue.Add(new PendingPkt { ReleaseAt = releaseAt, Payload = pkt });

                        // 正常情况下队列只有基础延迟/块长 那么几个包；
                        // 超过这个量说明消费端落后，提示而不是无限堆积
                        while (queue.Count > 64) Thread.Sleep(1);
                    }
                    else _dropped++;

                    seq++;
                }

                // 收尾：队列里还没到点的包也要发出去，否则末尾会凭空丢一段
                queue.CompleteAdding();
                sender.Join(2000);

                sw.Stop();
                Console.WriteLine();
                Console.WriteLine("=== 结果 ===");
                Console.WriteLine("  实际时长    " + sw.Elapsed.TotalSeconds.ToString("F2") + " 秒");
                Console.WriteLine("  已发送      " + _sent + " 包（" +
                                  (_sent * blockMs) + " ms 音频）");
                Console.WriteLine("  故意丢弃    " + _dropped + " 包（" + lossPct.ToString("F1") + "%）");
                Console.WriteLine("  故意乱序    " + _reordered + " 包");
            }
            return 0;
        }

        private static void TrySend(UdpClient sock, IPEndPoint target, byte[] pkt)
        {
            try { sock.Send(pkt, pkt.Length, target); }
            catch (Exception ex) { Console.WriteLine("发送失败：" + ex.Message); }
        }

        /// <summary>按手机端 SnadProtocol.buildHeader 的布局构造包头。</summary>
        private static byte[] BuildPacket(int channels, int rate, int seq, int timestampMs,
                                          int payloadBytes, byte[] pcm)
        {
            var pkt = new byte[HeaderSize + payloadBytes];
            pkt[0] = (byte)'S'; pkt[1] = (byte)'N'; pkt[2] = (byte)'A'; pkt[3] = (byte)'D';
            pkt[4] = 1;                          // version
            pkt[5] = (byte)channels;
            PutIntLE(pkt, 6, rate);
            PutIntLE(pkt, 10, seq);
            PutIntLE(pkt, 14, timestampMs);
            PutIntLE(pkt, 18, payloadBytes);
            Buffer.BlockCopy(pcm, 0, pkt, HeaderSize, payloadBytes);
            return pkt;
        }

        private static void PutIntLE(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        /// <summary>
        /// 生成测试音频：基频 + 三次谐波 + 每 500ms 一个脉冲。
        /// 脉冲便于人耳核对通路是否连续（能听到规律哒哒声）。
        /// </summary>
        private static void FillTone(byte[] pcm, int frames, int channels, double toneHz,
                                     ref double phase, bool silent)
        {
            for (int i = 0; i < frames; i++)
            {
                double v;
                if (silent) v = 0;
                else
                {
                    double t = phase / 48000.0;
                    v = 0.22 * Math.Sin(2 * Math.PI * toneHz * t)
                      + 0.07 * Math.Sin(2 * Math.PI * toneHz * 3 * t);
                    if (t % 0.5 < 0.02) v += 0.4 * Math.Sin(2 * Math.PI * 1200 * t);
                }
                phase += 1;

                short s = (short)Math.Round(Math.Max(-1, Math.Min(1, v)) * 32767);
                int off = i * channels * 2;
                for (int c = 0; c < channels; c++)
                {
                    pcm[off + c * 2] = (byte)(s & 0xFF);
                    pcm[off + c * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
        }

        private static void Usage()
        {
            Console.WriteLine("SNAD 发包器 —— 按与手机相同的协议向接收器送音频");
            Console.WriteLine();
            Console.WriteLine("  SnadSender.exe <目标IP> [选项]");
            Console.WriteLine();
            Console.WriteLine("  端口 <port>         默认 6981");
            Console.WriteLine("  时长 <秒>           默认 10");
            Console.WriteLine("  块 <ms>             每包音频时长，默认 21");
            Console.WriteLine("  丢包 <百分比>       默认 0");
            Console.WriteLine("  抖动 <ms>           发送时刻随机偏移，默认 0");
            Console.WriteLine("  乱序 <百分比>       默认 0");
            Console.WriteLine("  音调 <Hz>           默认 440");
            Console.WriteLine("  时间戳偏移 <ms>     让时间戳与真实时刻错开");
            Console.WriteLine("  静默                只发零样本");
            Console.WriteLine();
            Console.WriteLine("  例：SnadSender.exe 127.0.0.1 时长 20 丢包 1 抖动 3");
            Console.WriteLine();
            Console.WriteLine("  配合接收器使用：先起 SnadReceiver.exe，再起本工具。");
            Console.WriteLine("  接收器默认监听 6981，本机测试直接给 127.0.0.1。");
        }
    }
}
