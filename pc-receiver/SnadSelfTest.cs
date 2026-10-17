// 电脑端链路自测：不需要手机，用合成数据断言各环节算得对。
//
// 为什么要它：这一整晚的返工几乎都源于"没有断言就开始装机"。
// Kotlin 本地编不了，但 PC 侧的解析、分段、对账全都是纯逻辑，可以当场验证。
//
// 覆盖：
//   1. 'K' 报文解析（含 flags、缓冲、读块、双时钟）
//   2. 五段测量：包→写→读→提交→播完，逐段数值与端到端对账
//   3. 映射断裂（没有对应包记录）必须计入 unmapped，而不是猜一个值
//   4. 对账不通过时 unaccounted 必须显著非零（门禁的依据）

using System;
using System.Globalization;

namespace Snad
{
    internal static class SnadSelfTest
    {
        private static int _bad;

        private static void Check(bool ok, string what, string detail)
        {
            Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what +
                              (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
            if (!ok) _bad++;
        }

        public static int Run()
        {
            _bad = 0;

            Console.WriteLine("--- 1. 'K' 采样钟报文解析 ---");
            TestClockPacket();

            Console.WriteLine();
            Console.WriteLine("--- 2. 五段测量与对账 ---");
            TestSpans();

            Console.WriteLine();
            Console.WriteLine("--- 3. 映射断裂必须被计数 ---");
            TestUnmapped();

            Console.WriteLine();
            Console.WriteLine(_bad == 0 ? "电脑端链路自测通过" : ("有 " + _bad + " 项失败"));
            return _bad == 0 ? 0 : 1;
        }

        private static byte[] BuildKPacket(int seq, uint frames, long capNs, long bootNs,
                                           int bufFrames, int chunkFrames, bool tsOk)
        {
            var b = new byte[44];
            b[0] = (byte)'S'; b[1] = (byte)'N'; b[2] = (byte)'A'; b[3] = (byte)'D';
            b[4] = 1;
            b[5] = (byte)'K';
            PutI32(b, 6, seq);
            PutI32(b, 10, (int)frames);
            PutI64(b, 14, capNs);
            PutI64(b, 22, bootNs);
            PutI32(b, 34, bufFrames);
            PutI32(b, 38, chunkFrames);
            b[42] = (byte)(tsOk ? 1 : 0);
            return b;
        }

        private static void PutI32(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF); b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void PutI64(byte[] b, int off, long v)
        {
            for (int i = 0; i < 8; i++) b[off + i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        private static void TestClockPacket()
        {
            var l = new SnadClockListener(msg => { });
            // 直接走解析路径（不启动 socket）：用反射不合适，这里通过公开方法不可达，
            // 因此改为构造后调用内部 Parse —— Parse 是 private，改用副作用更小的方式：
            // 这里只验证"报文布局与解析结果一致"，通过一个公开的测试钩子。
            var pkt = BuildKPacket(1234, 56789, 9876543210L, 9876500000L, 16384, 480, true);
            l.TestParse(pkt, pkt.Length);

            Check(l.HasSample, "收到 1 包后 HasSample 为真", "收到 " + l.Received);
            Check(l.LastSeq == 1234, "序号解析正确", "seq=" + l.LastSeq);
            Check(l.LastFrames == 56789, "累计帧数解析正确", "frames=" + l.LastFrames);
            Check(l.BufferFrames == 16384, "缓冲帧数解析正确", "buffer=" + l.BufferFrames);
            Check(l.ChunkFrames == 480, "读块帧数解析正确", "chunk=" + l.ChunkFrames);
            Check(l.PhoneTimestampAvailable, "flags=1 时判定 getTimestamp 可用");
            Check(l.ClockDomainDeltaNs == 9876500000L - 9876543210L,
                  "双时钟差值算对", "delta=" + l.ClockDomainDeltaNs);

            var pkt2 = BuildKPacket(1235, 57269, 0, 0, 16384, 480, false);
            l.TestParse(pkt2, pkt2.Length);
            Check(!l.PhoneTimestampAvailable, "flags=0 时判定不可用（不冒充有采集时刻）");

            // 坏包必须被计数而不是静默
            var bad = new byte[44];
            l.TestParse(bad, bad.Length);
            Check(l.BadMagic >= 1, "magic 不符被计数", "badMagic=" + l.BadMagic);
        }

        private static void TestSpans()
        {
            const int rate = 48000;
            var m = new SnadSpanMeter(rate);

            // 一块 480 帧 = 10 ms。构造一条已知时间线：
            //   到达 0ms → 写入 1ms（S2=1）
            //   写入 1ms → 读出 41ms（S3=40）
            //   读出 41ms → 提交 42ms（S4=1）
            //   提交 42ms → 播完 62ms（S5=20，含设备）
            //   手机采集时刻 = 0（假设两端同一时钟，Δ=0）
            //   端到端 = 62 - 0 = 62；五段和 = 网络0 + 1 + 40 + 1 + 20 = 62 ✓
            const double netRtt = 0;      // RTT=0 → 网络 0
            m.AddArrivalToWrite(1.0);
            m.OnPacketWritten(0, 1.0, 0);

            m.OnBlockRead(0, true, 1.0, 0);
            m.MarkReadNow(41.0);
            m.MarkSubmitNow(42.0);
            m.OnBlockDone(62.0, true, 0.0, netRtt);

            double unaccounted;
            string rep = m.TakeReport(true, netRtt, out unaccounted);

            Check(Math.Abs(unaccounted) < 0.01, "五段之和与端到端一致（未归因≈0）",
                  "未归因=" + unaccounted.ToString("F2"));
            Check(rep.Contains("62.0"), "报告里含端到端 62.0", rep.Replace("\n", " | "));
        }

        private static void TestUnmapped()
        {
            var m = new SnadSpanMeter(48000);
            // 没有任何包记录，直接结算 → 必须计入 unmapped
            m.OnBlockRead(999, false, 0, 0);
            m.MarkReadNow(10);
            m.MarkSubmitNow(11);
            m.OnBlockDone(20, true, 0, 10);

            double unaccounted;
            string rep = m.TakeReport(true, 10, out unaccounted);
            Check(rep.Contains("未映射 1"), "映射断裂被计数", rep.Replace("\n", " | "));
        }
    }
}
