// 端到端延迟的时钟基准（见 TIME-SYNC.md）
//
// 这一层只做三件事：拼探测包、解回复包、算时钟差。
// 它不碰网络、不碰音频——因此可以脱离手机单独验证，
// 而"公式对不对"恰恰是整条测量链最容易写错、又最难在真机上察觉的部分。

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Snad
{
    internal static class SnadTimeSync
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("SNADBEAC");

        public const byte Version = 1;
        public const byte TypeProbe = (byte)'T';
        public const byte TypeReply = (byte)'t';

        public const int ProbeSize = 18 + 32;   // 头 + HMAC
        public const int ReplySize = 26 + 32;

        private static readonly byte[] Info = Encoding.ASCII.GetBytes("snad-timesync-v1");

        /// <summary>从预共享密钥派生时间同步专用密钥（与信标密钥相互独立）。</summary>
        public static byte[] DeriveKey(string psk)
        {
            if (string.IsNullOrEmpty(psk))
                throw new ArgumentException("PSK 为空：构建时未注入密钥");
            using (var h = new HMACSHA256(Encoding.ASCII.GetBytes(psk)))
                return h.ComputeHash(Info);
        }

        private static void PutU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static uint GetU32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        private static byte[] Hmac(byte[] key, byte[] data, int len)
        {
            using (var h = new HMACSHA256(key))
                return h.ComputeHash(data, 0, len);
        }

        /// <summary>组探测包。t1Ms 是 PC 发出时刻（毫秒低 32 位）。</summary>
        public static byte[] BuildProbe(byte[] key, uint nonce, uint t1Ms)
        {
            var b = new byte[ProbeSize];
            Buffer.BlockCopy(Magic, 0, b, 0, Magic.Length);
            b[8] = Version;
            b[9] = TypeProbe;
            PutU32(b, 10, nonce);
            PutU32(b, 14, t1Ms);
            var mac = Hmac(key, b, 18);
            Buffer.BlockCopy(mac, 0, b, 18, 32);
            return b;
        }

        /// <summary>组回复包（手机端同构实现；PC 侧测试也用它生成合法样本）。</summary>
        public static byte[] BuildReply(byte[] key, uint nonce, uint t1Ms, uint recvMs, uint sendMs)
        {
            var b = new byte[ReplySize];
            Buffer.BlockCopy(Magic, 0, b, 0, Magic.Length);
            b[8] = Version;
            b[9] = TypeReply;
            PutU32(b, 10, nonce);
            PutU32(b, 14, t1Ms);
            PutU32(b, 18, recvMs);
            PutU32(b, 22, sendMs);
            var mac = Hmac(key, b, 26);
            Buffer.BlockCopy(mac, 0, b, 26, 32);
            return b;
        }

        /// <summary>校验并解析回复包。HMAC 不符、类型不符、长度不符一律拒绝并说明原因。</summary>
        public static bool TryParseReply(byte[] pkt, int len, byte[] key,
                                         out uint nonce, out uint t1, out uint recv, out uint send,
                                         out string error)
        {
            nonce = t1 = recv = send = 0;
            error = null;

            if (pkt == null || len < ReplySize) { error = "长度不足"; return false; }
            for (int i = 0; i < Magic.Length; i++)
                if (pkt[i] != Magic[i]) { error = "magic 不符"; return false; }
            if (pkt[8] != Version) { error = "版本不符：" + pkt[8]; return false; }
            if (pkt[9] != TypeReply) { error = "类型不是回复包：" + (char)pkt[9]; return false; }

            var mac = Hmac(key, pkt, 26);
            for (int i = 0; i < 32; i++)
                if (mac[i] != pkt[26 + i]) { error = "HMAC 不符（密钥不一致或被伪造）"; return false; }

            nonce = GetU32(pkt, 10);
            t1 = GetU32(pkt, 14);
            recv = GetU32(pkt, 18);
            send = GetU32(pkt, 22);
            return true;
        }

        /// <summary>
        /// 由一次往返算出 RTT 与时钟差。
        ///
        ///   RTT = (T4 − T1) − (S − R)      扣掉手机内部处理时间
        ///   Δ   ≈ T1 + RTT/2 − R
        ///
        /// 全部用 uint 差值再转 double：毫秒低 32 位在 49.7 天回绕，
        /// 直接用有符号减法会在回绕点给出一个巨大的负数。
        /// 这里用无符号减法，只要一次往返小于 24 天就天然正确。
        /// </summary>
        public static bool TryComputeOffset(uint t1, uint t4, uint recv, uint send,
                                            out double rttMs, out double offsetMs, out string error)
        {
            rttMs = offsetMs = 0;
            error = null;

            uint roundTrip = t4 - t1;        // PC 侧：发出 → 收到
            uint phoneHold = send - recv;    // 手机侧：收到 → 发出

            if (phoneHold > roundTrip)
            {
                error = "手机处理时间(" + phoneHold + "ms) 大于往返(" + roundTrip +
                        "ms)，时钟读数不可比（可能来自不同设备或时钟已被改写）";
                return false;
            }

            double rtt = (double)(roundTrip - phoneHold);
            if (rtt < 0 || rtt > 5000)
            {
                error = "RTT 异常：" + rtt.ToString("F1", CultureInfo.InvariantCulture) + " ms";
                return false;
            }

            rttMs = rtt;
            // T1 + RTT/2 − R：R 是手机在收到探测那一刻的读数
            offsetMs = (double)t1 + rtt / 2.0 - (double)recv;
            return true;
        }

        // ---- 自检：不依赖网络与手机 ----
        //
        // 判据是"已知答案能不能算回来"，包括两个容易被忽略的边界：
        //   a) 手机处理耗时（收→发）必须从 RTT 里扣掉，否则 Δ 会偏
        //   b) 毫秒计数回绕点不能算出巨大负数
        public static int SelfTest()
        {
            int bad = 0;
            var key = DeriveKey("TESTKEY");

            Action<bool, string, string> check = (ok, what, detail) =>
            {
                Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what +
                                  (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
                if (!ok) bad++;
            };

            // 1) 完全对称、手机零处理。构造自洽样本：
            //    真实时钟差 Δ=1000，单程 3ms，
            //    T1=5000 → 手机 R = T1 + 3 − Δ = 4003 → 回复 S=4003 → PC 收到 T4=5006
            {
                uint t1 = 5000; uint recv = 4003; uint send = 4003; uint t4 = 5006;
                double rtt, off; string err;
                bool ok = TryComputeOffset(t1, t4, recv, send, out rtt, out off, out err);
                check(ok && Math.Abs(rtt - 6) < 0.01, "往返 6ms 被算对",
                      ok ? "RTT=" + rtt.ToString("F1") : err);
                check(ok && Math.Abs(off - 1000) < 0.01, "时钟差 1000ms 被算对",
                      ok ? "Δ=" + off.ToString("F1") : err);
            }

            // 2) 手机内部处理 4ms：RTT 扣掉它，Δ 不受影响
            //    S = 4003+4 = 4007，T4 = 5000+3+4+3 = 5010
            {
                uint t1 = 5000; uint recv = 4003; uint send = 4007; uint t4 = 5010;
                double rtt, off; string err;
                bool ok = TryComputeOffset(t1, t4, recv, send, out rtt, out off, out err);
                check(ok && Math.Abs(rtt - 6) < 0.01, "手机处理 4ms 从 RTT 中扣除",
                      ok ? "RTT=" + rtt.ToString("F1") : err);
                check(ok && Math.Abs(off - 1000) < 0.01, "处理耗时不影响时钟差",
                      ok ? "Δ=" + off.ToString("F1") : err);
            }

            // 3) 回绕：T1 在 2^32 附近回绕
            {
                uint t1 = 0xFFFFFFFC;          // 4294967292
                uint t4 = 4;                   // 回绕后
                uint recv = 100; uint send = 100;
                double rtt, off; string err;
                bool ok = TryComputeOffset(t1, t4, recv, send, out rtt, out off, out err);
                check(ok && Math.Abs(rtt - 8) < 0.01, "毫秒计数回绕仍算得出往返",
                      ok ? "RTT=" + rtt.ToString("F1") : err);
            }

            // 4) 手机处理时间大于往返 → 必须报错，不能给出一个数
            {
                uint t1 = 100; uint t4 = 106; uint recv = 0; uint send = 50;
                double rtt, off; string err;
                bool ok = TryComputeOffset(t1, t4, recv, send, out rtt, out off, out err);
                check(!ok, "手机处理时间大于往返时明确报错", err ?? "");
            }

            // 5) 报文往返：组包→解包→校验
            {
                var probe = BuildProbe(key, 12345, 999);
                check(probe.Length == ProbeSize && probe[9] == TypeProbe, "探测包布局正确",
                      probe.Length + " 字节");

                var reply = BuildReply(key, 12345, 999, 7, 8);
                uint n, tt, r, s; string err;
                bool ok = TryParseReply(reply, reply.Length, key, out n, out tt, out r, out s, out err);
                check(ok && n == 12345 && tt == 999 && r == 7 && s == 8, "回复包解析正确",
                      ok ? "nonce=" + n + " t1=" + tt : err);
            }

            // 6) 篡改与错密钥必须被拒
            {
                var reply = BuildReply(key, 1, 2, 3, 4);
                reply[20] ^= 0xFF;
                uint n, tt, r, s; string err;
                bool ok = TryParseReply(reply, reply.Length, key, out n, out tt, out r, out s, out err);
                check(!ok, "被篡改的回复包被拒绝", err ?? "");

                var good = BuildReply(key, 1, 2, 3, 4);
                var other = DeriveKey("OTHER");
                ok = TryParseReply(good, good.Length, other, out n, out tt, out r, out s, out err);
                check(!ok, "密钥不符的回复包被拒绝", err ?? "");
            }

            Console.WriteLine();
            Console.WriteLine(bad == 0 ? "时钟基准自检通过" : ("有 " + bad + " 项失败"));
            return bad == 0 ? 0 : 1;
        }
    }
}
