// 位深数据路径的逐样本对拍（计划 §8④）。
//
// 为什么需要它：此前只验了"比特率 1.5 倍、帧率不变"——
// 那只证明包头与帧数算对了，**不证明样本值没被截断或削波**。
// 计划 §9 风险表也要求"只证数据路径正确（逐位对拍、无削波、无意外重采样）"。
//
// 怎么证：走**真实的 SampleRing**（接收器写包用的就是它），
// 而不是复算一遍公式。输入是精心挑的 24bit 样本，
// 使得"按 24bit 解码再降到 16bit"与"先截断成 16bit"给出不同的结果——
// 两者一旦分得开，就说明低 8 位真的参与了运算。
//
// 用法：SnadBitPathTest.exe
// 退出码 0 = 全部通过。

using System;

namespace Snad
{
    internal static class BitPathTest
    {
        private static int _bad;

        private static void Check(bool ok, string what, string detail)
        {
            Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what +
                              (detail == null ? "" : "  " + detail));
            if (!ok) _bad++;
        }

        private static int Main()
        {
            Console.WriteLine("=== 位深数据路径对拍（经真实 SampleRing） ===");
            Console.WriteLine();

            Test24BitNotTruncated();
            Test24BitSignExtension();
            TestClippingNoWrap();
            Test16BitUnchanged();
            TestDither();

            Console.WriteLine();
            Console.WriteLine(_bad == 0 ? "位深路径对拍通过" : ("有 " + _bad + " 项失败"));
            return _bad == 0 ? 0 : 1;
        }

        /// <summary>把 24bit 有符号样本写成小端三字节。</summary>
        private static void Put24(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
        }

        /// <summary>把一段 24bit 样本送进环形缓冲，再读回来（读出的固定是 16bit）。</summary>
        private static short[] RoundTrip24(int[] samples24, int channels)
        {
            int frames = samples24.Length / channels;
            var payload = new byte[samples24.Length * 3];
            for (int i = 0; i < samples24.Length; i++) Put24(payload, i * 3, samples24[i]);

            var ring = new SampleRing(Math.Max(1024, frames * 2));
            ring.Write(payload, channels, payload.Length, SampleFormat.Pcm24);

            var dst = new byte[frames * 4];
            int got = ring.Read(dst, frames, 1.0);
            if (got != frames)
                throw new InvalidOperationException("读回帧数不符：" + got + " ≠ " + frames);

            var outp = new short[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                outp[f * 2] = (short)(dst[f * 4] | (dst[f * 4 + 1] << 8));
                outp[f * 2 + 1] = (short)(dst[f * 4 + 2] | (dst[f * 4 + 3] << 8));
            }
            return outp;
        }

        /// <summary>
        /// 核心用例：低 8 位必须真的进入解码结果。
        ///
        /// 这一条只能在**解码后的浮点**上验，不能指望从 16bit 输出端看出来：
        /// 环形缓冲读出的永远是 16bit（供 waveOut 用），
        /// 24bit 降到 16bit 必然丢掉低 8 位——那是设计的终点，不是缺陷。
        ///
        /// 取 v = 2000：
        ///   按 24bit 解码 → 2000/8388608 = 2.384e-4 → ×32767 = 7.81
        ///   若先截断成 16bit（2000>>8 = 7）→ 7/32768 = 2.136e-4 → ×32767 = 6.99
        ///   7.81 与 6.99 相差明显，足以分辨低 8 位是否参与运算。
        /// </summary>
        private static void Test24BitNotTruncated()
        {
            Console.WriteLine("--- 24bit 低 8 位参与解码（未被截断成 16bit）---");

            const int v = 2000;
            var payload = new byte[3];
            Put24(payload, 0, v);

            float as24 = AudioCodec.Decode(payload, 0, SampleFormat.Pcm24);

            int v16 = v >> 8;                        // 假设实现先截断成 16bit
            float as16 = v16 / 32768f;

            double scaled24 = as24 * 32767.0;
            double scaled16 = as16 * 32767.0;

            Check(Math.Abs(scaled24 - scaled16) >= 0.5,
                  "24bit 与 16bit 截断的结果可分（用例有鉴别力）",
                  "24bit→" + scaled24.ToString("F2") + "，16bit→" + scaled16.ToString("F2"));

            Check(Math.Abs(scaled24 - 7.81) < 0.05,
                  "解码值等于 24bit 精确换算",
                  "实测 " + scaled24.ToString("F2") + "，期望 7.81");
        }

        /// <summary>负值符号扩展：0x800000 是 -8388608（-1.0），不是 +0.5。</summary>
        private static void Test24BitSignExtension()
        {
            Console.WriteLine("--- 24bit 负值符号扩展 ---");

            var full = new byte[3];
            Put24(full, 0, -8388608);
            float f = AudioCodec.Decode(full, 0, SampleFormat.Pcm24);
            Check(f < -0.999f, "0x800000 解码为 -1.0（极性未翻转）", "实测 " + f.ToString("F4"));

            var half = new byte[3];
            Put24(half, 0, -4194304);
            float h = AudioCodec.Decode(half, 0, SampleFormat.Pcm24);
            Check(Math.Abs(h + 0.5f) < 0.001f, "-4194304 解码为 -0.5", "实测 " + h.ToString("F4"));
        }

        /// <summary>削波：超范围输入必须钳位，不能回绕成反极性。</summary>
        private static void TestClippingNoWrap()
        {
            Console.WriteLine("--- 削波不回绕 ---");

            // 24bit 的取值范围就是 ±8388607，写不进"超范围"，
            // 因此 24bit 这一路不需要钳位——这条用例只对 float32 有意义
            // （float 可以传 1.5，而整数位深不能）。
            var f32 = new byte[8];
            BitConverter.GetBytes(1.5f).CopyTo(f32, 0);
            BitConverter.GetBytes(-1.5f).CopyTo(f32, 4);

            var ring = new SampleRing(1024);
            ring.Write(f32, 2, f32.Length, SampleFormat.Float32);
            var dst = new byte[2 * 4];
            ring.Read(dst, 1, 1.0);
            short l = (short)(dst[0] | (dst[1] << 8));
            short r = (short)(dst[2] | (dst[3] << 8));

            Check(l == 32767, "float32 +1.5 钳到 +32767", "实测 " + l);
            Check(r == -32768 || r == -32767, "float32 -1.5 钳到负满幅", "实测 " + r);
        }

        /// <summary>
        /// 抖动（dither）的可判据性质。
        ///
        /// 用一个"落在 16bit 网格中间"的 24bit 常值输入：
        ///   v = 0x00800080 = 8388736 → /8388608 = 1.0000... 超范围，换小一点
        ///   v = 0x00400040 = 4194368 → 0.5000019…
        /// 该值最接近 16bit 的 16384（0.5），但略高。
        ///
        /// 判据：
        ///   不加抖动 → 每个样本都量化成同一个 16bit 值（恒定）；
        ///   加抖动   → 会在相邻两个码之间跳动，且长期均值更接近真值。
        ///
        /// 这正是"抖动把量化失真打散"的可观测形式。
        /// </summary>
        private static void TestDither()
        {
            Console.WriteLine("--- 抖动（dither）---");

            const int v = 0x00010001;      // 65537 / 8388608 = 0.00781262…
            const int frames = 4096;
            var payload = new byte[frames * 2 * 3];
            for (int i = 0; i < frames * 2; i++) Put24(payload, i * 3, v);

            // 不加抖动
            var ringA = new SampleRing(16384) { Dither = false };
            ringA.Write(payload, 2, payload.Length, SampleFormat.Pcm24);
            var dstA = new byte[frames * 4];
            ringA.Read(dstA, frames, 1.0);
            var setA = new System.Collections.Generic.HashSet<short>();
            double sumA = 0;
            for (int f = 0; f < frames; f++)
            {
                short s = (short)(dstA[f * 4] | (dstA[f * 4 + 1] << 8));
                setA.Add(s);
                sumA += s;
            }

            // 加抖动
            var ringB = new SampleRing(16384) { Dither = true };
            ringB.Write(payload, 2, payload.Length, SampleFormat.Pcm24);
            var dstB = new byte[frames * 4];
            ringB.Read(dstB, frames, 1.0);
            var setB = new System.Collections.Generic.HashSet<short>();
            double sumB = 0;
            for (int f = 0; f < frames; f++)
            {
                short s = (short)(dstB[f * 4] | (dstB[f * 4 + 1] << 8));
                setB.Add(s);
                sumB += s;
            }

            Check(setA.Count == 1, "不加抖动时输出恒定（只有 1 种取值）",
                  "实测 " + setA.Count + " 种");
            Check(setB.Count >= 2, "加抖动后在相邻码之间跳动",
                  "实测 " + setB.Count + " 种");

            double meanA = sumA / frames, meanB = sumB / frames;
            double exact = v / 8388608.0 * 32768.0;
            Console.WriteLine("        真值 " + exact.ToString("F3") +
                              "，不加抖动均值 " + meanA.ToString("F3") +
                              "，加抖动均值 " + meanB.ToString("F3"));

            // 抖动的价值在频谱（把与信号相关的量化失真打散），不在均值精度。
            // 因此这里只验一件必须成立的事：**不引入系统性偏移**。
            // 早先用截断时它偏了 −0.5 LSB（真值 256.004、均值 255.495），
            // 那是因为截断对正数恒定向下；改四舍五入后偏移消失。
            Check(Math.Abs(meanB - exact) <= 0.5,
                  "加抖动后无系统性偏移（≤0.5 LSB）",
                  "偏差 " + (meanB - exact).ToString("F3"));

            // pcm16 输入不应被加抖动（本来就在 16bit 网格上）
            var p16 = new byte[frames * 2 * 2];
            for (int i = 0; i < frames * 2; i++)
            {
                p16[i * 2] = (byte)(100 & 0xFF);
                p16[i * 2 + 1] = (byte)((100 >> 8) & 0xFF);
            }
            var ringC = new SampleRing(16384) { Dither = true };
            ringC.Write(p16, 2, p16.Length, SampleFormat.Pcm16);
            var dstC = new byte[frames * 4];
            ringC.Read(dstC, frames, 1.0);
            var setC = new System.Collections.Generic.HashSet<short>();
            for (int f = 0; f < frames; f++)
                setC.Add((short)(dstC[f * 4] | (dstC[f * 4 + 1] << 8)));
            Check(setC.Count == 1, "pcm16 输入不加抖动（取值仍恒定）", "实测 " + setC.Count + " 种");

            Console.WriteLine();
        }

        /// <summary>pcm16 路径不能被这次改动影响（回归保护）。</summary>
        private static void Test16BitUnchanged()
        {
            Console.WriteLine("--- pcm16 路径回归 ---");

            short[] inPcm = { 1234, -1234, 32767, -32768 };
            var payload = new byte[inPcm.Length * 2];
            for (int i = 0; i < inPcm.Length; i++)
            {
                payload[i * 2] = (byte)(inPcm[i] & 0xFF);
                payload[i * 2 + 1] = (byte)((inPcm[i] >> 8) & 0xFF);
            }

            var ring = new SampleRing(1024);
            ring.Write(payload, 2, payload.Length, SampleFormat.Pcm16);
            var dst = new byte[2 * 4];
            ring.Read(dst, 2, 1.0);

            short l0 = (short)(dst[0] | (dst[1] << 8));
            short l1 = (short)(dst[4] | (dst[5] << 8));
            // 逐位无损：写侧 /32768、读侧 ×32768 互为逆运算，必须完全相等。
            // 早先读侧乘 32767，留下恒定 −1 LSB 偏差（1234→1233）。
            Check(l0 == 1234, "1234 逐位无损往返", "实测 " + l0);
            Check(l1 == 32767, "32767 逐位无损往返", "实测 " + l1);

            Console.WriteLine();
        }
    }
}
