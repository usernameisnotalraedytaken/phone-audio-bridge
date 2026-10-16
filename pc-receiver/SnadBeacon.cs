// SNAD 局域网信标协议：接收端广播自身地址，发送端验证后接入。
//
// 设计目标（对应"握手受限且无感"）：
//   无感 —— 密钥在构建时注入两端，运行期没有任何配对步骤；广播一听即连。
//   受限 —— 局域网内其他设备即使收到广播，没有预共享密钥就算不出合法 HMAC。
//   免认证 —— 每次接入是一次 HMAC 挑战，不是登录，不依赖任何服务器下发身份。
//
// 报文（小端序，固定头 40 字节 + 可变 device_id）：
//   offset size  field
//   0      8     magic  'S','N','A','D','B','E','A','C'
//   8      1     version = 1
//   9      1     idLen
//   10     4     seq        单调递增，用于丢弃重放
//   14     8     issuedAt   服务器/本机毫秒时间戳，仅作可观测性，不承担安全职责
//   22     18    reserved   置零，为将来扩展留位且保持对齐
//   40     idLen device_id  UTF-8
//   之后   4     port       监听端口
//   之后   32    hmac       HMAC-SHA256(derivedKey, 此前全部字节)
//
// 密钥派生：derivedKey = HMAC-SHA256(PSK, "snad-beacon-v1")
//   —— 不直接用 PSK 当 HMAC 密钥，避免同一密钥在不同用途间复用。
//
// seq 承担防重放：接收端只接受严格大于已见值的 seq，且同一 device_id 独立计数。

using System;
using System.Security.Cryptography;
using System.Text;

namespace Snad
{
    internal static class SnadBeacon
    {
        public static readonly byte[] Magic =
            Encoding.ASCII.GetBytes("SNADBEAC");

        public const byte Version = 1;
        public const int FixedHeaderSize = 40;
        public const int HmacSize = 32;
        public const int DefaultPort = 6983;

        private const string DeriveInfo = "snad-beacon-v1";

        /// <summary>
        /// 由预共享密钥派生信标专用密钥。两端必须使用完全相同的规则，
        /// 否则 KeyId 相同也无法互通。
        /// </summary>
        public static byte[] DeriveKey(string pskBase32)
        {
            if (string.IsNullOrEmpty(pskBase32))
                throw new ArgumentException("PSK 为空：构建时未注入密钥", "pskBase32");
            using (var h = new HMACSHA256(Encoding.ASCII.GetBytes(pskBase32)))
                return h.ComputeHash(Encoding.ASCII.GetBytes(DeriveInfo));
        }

        /// <summary>KeyId：PSK 的 SHA256 前 8 字节，大写十六进制。用于确认两端密钥是否一致。</summary>
        public static string ComputeKeyId(string pskBase32)
        {
            if (string.IsNullOrEmpty(pskBase32)) return "无密钥";
            using (var sha = SHA256.Create())
            {
                var d = sha.ComputeHash(Encoding.ASCII.GetBytes(pskBase32));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(d[i].ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>组包。返回完整报文字节。</summary>
        public static byte[] Build(string deviceId, int port, long seq, byte[] derivedKey)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(deviceId ?? "snad-pc");
            if (idBytes.Length > 255) throw new ArgumentException("device_id 过长", "deviceId");

            int total = FixedHeaderSize + idBytes.Length + 4 + HmacSize;
            var buf = new byte[total];

            Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
            buf[8] = Version;
            buf[9] = (byte)idBytes.Length;
            PutInt32LE(buf, 10, unchecked((int)seq));
            PutInt64LE(buf, 14, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Buffer.BlockCopy(idBytes, 0, buf, FixedHeaderSize, idBytes.Length);
            int p = FixedHeaderSize + idBytes.Length;
            PutInt32LE(buf, p, port);

            using (var h = new HMACSHA256(derivedKey))
            {
                var mac = h.ComputeHash(buf, 0, total - HmacSize);
                Buffer.BlockCopy(mac, 0, buf, total - HmacSize, HmacSize);
            }
            return buf;
        }

        /// <summary>解析结果。</summary>
        public sealed class Beacon
        {
            public string DeviceId;
            public int Port;
            public long Seq;
            public long IssuedAt;

            /// <summary>
            /// 来源地址，由接收方从 UDP 源地址填入，不来自报文内容。
            /// 这一点很重要：报文体里的地址是攻击者可伪造的，而 UDP 源地址由内核填充。
            /// </summary>
            public string SourceAddress;
        }

        /// <summary>
        /// 校验并解析。任何一步不通过都返回 null，并通过 reason 说明原因。
        /// 不使用静默失败：调用方需要知道是"没密钥""版本不符"还是"HMAC 不对"。
        ///
        /// sourceAddress 由调用方从 UDP 源地址传入，不取自报文内容。
        /// </summary>
        public static Beacon TryParse(byte[] data, int length, byte[] derivedKey,
                                     string sourceAddress, out string reason)
        {
            reason = null;
            if (data == null || length < FixedHeaderSize + 4 + HmacSize)
            {
                reason = "报文过短（" + (data == null ? 0 : length) + " 字节）";
                return null;
            }
            for (int i = 0; i < Magic.Length; i++)
            {
                if (data[i] != Magic[i]) { reason = "magic 不匹配"; return null; }
            }
            if (data[8] != Version) { reason = "版本不符：" + data[8]; return null; }

            int idLen = data[9];
            int need = FixedHeaderSize + idLen + 4 + HmacSize;
            if (length < need) { reason = "长度与 idLen 不符"; return null; }

            // 先验 HMAC，再解释内容——避免对未认证数据做任何解析
            using (var h = new HMACSHA256(derivedKey))
            {
                var mac = h.ComputeHash(data, 0, need - HmacSize);
                int off = need - HmacSize;
                int diff = 0;
                for (int i = 0; i < HmacSize; i++) diff |= mac[i] ^ data[off + i];
                if (diff != 0) { reason = "HMAC 校验失败（密钥不一致或报文被改）"; return null; }
            }

            var b = new Beacon
            {
                DeviceId = Encoding.UTF8.GetString(data, FixedHeaderSize, idLen),
                Port = GetInt32LE(data, FixedHeaderSize + idLen),
                Seq = GetInt32LE(data, 10),
                IssuedAt = GetInt64LE(data, 14),
                SourceAddress = sourceAddress,
            };
            if (b.Port <= 0 || b.Port > 65535) { reason = "端口非法：" + b.Port; return null; }
            return b;
        }

        /// <summary>
        /// 防重放：同一 device_id 的 seq 必须严格递增。
        /// 独立成类便于两端复用同一判定。
        /// </summary>
        internal sealed class ReplayGuard
        {
            private readonly System.Collections.Generic.Dictionary<string, long> _last =
                new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);

            /// <summary>返回 true 表示接受，false 表示是重放或乱序的旧报文。</summary>
            public bool Accept(string deviceId, long seq)
            {
                long prev;
                if (_last.TryGetValue(deviceId, out prev) && seq <= prev) return false;
                _last[deviceId] = seq;
                return true;
            }

            public long LastSeq(string deviceId)
            {
                long v;
                return _last.TryGetValue(deviceId, out v) ? v : -1;
            }
        }

        private static void PutInt32LE(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void PutInt64LE(byte[] b, int off, long v)
        {
            for (int i = 0; i < 8; i++) b[off + i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        private static int GetInt32LE(byte[] b, int off)
        {
            return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
        }

        private static long GetInt64LE(byte[] b, int off)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)b[off + i] << (8 * i);
            return v;
        }
    }
}
