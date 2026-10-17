// 位深编解码的字节级验证。
//
// 为什么单独做：
//   24bit 的符号扩展是最容易写错的地方——0x800000 表示 -8388608 而不是
//   +8388608，不做扩展会把 -1.0 读成 +0.5，表现为波形极性翻转。
//   这种错误在听感上不明显（一半波形反相），但会毁掉低频，
//   必须用字节级断言钉死，不能靠"听起来还行"。
//
// 同时验证：
//   - WAVEFORMATEXTENSIBLE 的结构长度与字段布局被设备接受
//   - 混音格式的 SubFormat 确实能读出（不靠位深猜）

using System;
using System.Runtime.InteropServices;

namespace Snad
{
    internal static class AudioCodecTest
    {
        private static int _bad;

        private static void Check(bool ok, string what, string detail)
        {
            Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what + (detail == null ? "" : "  " + detail));
            if (!ok) _bad++;
        }

        private static int Main()
        {
            Console.WriteLine("=== 位深编解码字节级验证 ===");
            Console.WriteLine();

            TestRoundTrip(SampleFormat.Pcm16);
            TestRoundTrip(SampleFormat.Pcm24);
            TestRoundTrip(SampleFormat.Float32);

            TestSignExtension();
            TestClamping();
            TestFormatStructure();
            TestMixFormatSubtype();

            Console.WriteLine();
            Console.WriteLine(_bad == 0 ? "字节级验证通过" : ("有 " + _bad + " 项失败"));
            return _bad == 0 ? 0 : 1;
        }

        /// <summary>往返：编码后再解码应还原原值（在量化精度内）。</summary>
        private static void TestRoundTrip(SampleFormat f)
        {
            int bps = AudioCodec.BytesPerSample(f);
            Console.WriteLine("--- " + AudioCodec.Name(f) + " 往返（" + bps + " 字节/样本）---");

            double[] values = { 0.0, 0.5, -0.5, 0.25, -0.25, 0.999, -0.999 };
            double worst = 0;
            var buf = new byte[bps];

            foreach (double v in values)
            {
                AudioCodec.Encode(buf, 0, (float)v, f);
                float back = AudioCodec.Decode(buf, 0, f);
                double err = Math.Abs(back - v);
                if (err > worst) worst = err;
            }

            // 容差按比特精度给，且**允许略超 1 LSB**：
            // 整数 PCM 用 MSB 对齐约定——编码乘以 (2^(n-1) - 1) 让 +1.0 不溢出，
            // 解码除以 2^(n-1) 保持满幅定义。两者相差一个 LSB 的定标因子，
            // 这是行业惯例（WAV 的 PCM 就是如此），不是缺陷。
            // 容差取 1.5 LSB：能抓住"多错一位"这类真错误，又不误报约定本身。
            double lsb = f == SampleFormat.Pcm16 ? 1.0 / 32768
                       : (f == SampleFormat.Pcm24 ? 1.0 / 8388608 : 1e-6);
            double tol = lsb * 1.5;

            Check(worst <= tol, "样本往返误差在 " +
                  (f == SampleFormat.Float32 ? "浮点精度" : "1 LSB") + " 内",
                  "最大误差 " + worst.ToString("E2") + "（" + (worst / lsb).ToString("F2") +
                  " LSB），容差 " + tol.ToString("E2"));
            Console.WriteLine();
        }

        /// <summary>符号扩展：-1.0 与 -0.5 必须解出负值。</summary>
        private static void TestSignExtension()
        {
            Console.WriteLine("--- 24bit 符号扩展（易错点）---");

            // -1.0 的 24bit 补码是 0x800000
            var buf = new byte[3];
            buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x80;
            float v = AudioCodec.Decode(buf, 0, SampleFormat.Pcm24);
            Check(v < -0.99f, "-1.0 (0x800000) 解出负值",
                  "得到 " + v.ToString("F4") + "（若为 +0.5 则符号扩展有误）");

            // -0.5 应为 0xC00000
            buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0xC0;
            v = AudioCodec.Decode(buf, 0, SampleFormat.Pcm24);
            Check(v < -0.49f && v > -0.51f, "-0.5 (0xC00000) 解出 -0.5 附近", "得到 " + v.ToString("F4"));

            // 最大值 0x7FFFFF 应为正
            buf[0] = 0xFF; buf[1] = 0xFF; buf[2] = 0x7F;
            v = AudioCodec.Decode(buf, 0, SampleFormat.Pcm24);
            Check(v > 0.99f, "+最大 (0x7FFFFF) 解出正值", "得到 " + v.ToString("F4"));

            // 往返一个负值，确认编码端也正确
            var enc = new byte[3];
            AudioCodec.Encode(enc, 0, -1.0f, SampleFormat.Pcm24);
            var dec = AudioCodec.Decode(enc, 0, SampleFormat.Pcm24);
            Check(dec < -0.99f, "编码 -1.0 后再解码仍为负值",
                  "字节 " + enc[0].ToString("X2") + " " + enc[1].ToString("X2") + " " + enc[2].ToString("X2") +
                  " -> " + dec.ToString("F4"));
            Console.WriteLine();
        }

        /// <summary>超范围必须钳位，不能回绕成反相。</summary>
        private static void TestClamping()
        {
            Console.WriteLine("--- 超范围钳位 ---");
            var buf = new byte[3];

            AudioCodec.Encode(buf, 0, 4.0f, SampleFormat.Pcm24);
            float hi = AudioCodec.Decode(buf, 0, SampleFormat.Pcm24);
            Check(hi > 0.99f, "输入 4.0 钳到正向满幅", "得到 " + hi.ToString("F4"));

            AudioCodec.Encode(buf, 0, -4.0f, SampleFormat.Pcm24);
            float lo = AudioCodec.Decode(buf, 0, SampleFormat.Pcm24);
            Check(lo < -0.99f, "输入 -4.0 钳到负向满幅",
                  "得到 " + lo.ToString("F4") + "（若为正则发生回绕，会表现为爆音）");

            var b16 = new byte[2];
            AudioCodec.Encode(b16, 0, 9.0f, SampleFormat.Pcm16);
            Check(AudioCodec.Decode(b16, 0, SampleFormat.Pcm16) > 0.99f, "pcm16 同样钳位", null);
            Console.WriteLine();
        }

        /// <summary>结构布局：EXTENSIBLE 应为 40 字节，字段位置正确。</summary>
        private static void TestFormatStructure()
        {
            Console.WriteLine("--- WAVEFORMATEXTENSIBLE 结构布局 ---");

            int baseSize = Marshal.SizeOf(typeof(AudioCodec.WAVEFORMATEX));
            int extSize = Marshal.SizeOf(typeof(AudioCodec.WAVEFORMATEXTENSIBLE));

            Check(baseSize == 18, "WAVEFORMATEX 为 18 字节", "实测 " + baseSize);
            Check(extSize == 40, "WAVEFORMATEXTENSIBLE 为 40 字节", "实测 " + extSize);

            foreach (var f in new[] { SampleFormat.Pcm16, SampleFormat.Pcm24, SampleFormat.Float32 })
            {
                foreach (int ch in new[] { 1, 2 })
                {
                    int rate = 48000;
                    IntPtr p = AudioCodec.BuildFormat(f, ch, rate);
                    var wfe = (AudioCodec.WAVEFORMATEXTENSIBLE)Marshal.PtrToStructure(
                        p, typeof(AudioCodec.WAVEFORMATEXTENSIBLE));
                    Marshal.FreeCoTaskMem(p);

                    int bits = AudioCodec.BitsPerSample(f);
                    bool ok = wfe.Format.wFormatTag == AudioCodec.WAVE_FORMAT_EXTENSIBLE
                           && wfe.Format.nChannels == ch
                           && wfe.Format.nSamplesPerSec == rate
                           && wfe.Format.wBitsPerSample == bits
                           && wfe.Format.nBlockAlign == ch * bits / 8
                           && wfe.Format.nAvgBytesPerSec == rate * (uint)(ch * bits / 8)
                           && wfe.Format.cbSize == 22
                           && wfe.wValidBitsPerSample == bits
                           && wfe.SubFormat == (f == SampleFormat.Float32
                                                ? AudioCodec.GuidSubtypeFloat : AudioCodec.GuidSubtypePcm);

                    Check(ok, AudioCodec.Name(f) + " / " + ch + "ch 结构字段正确",
                          "blockAlign=" + wfe.Format.nBlockAlign + " avgBytes=" + wfe.Format.nAvgBytesPerSec);
                }
            }
            Console.WriteLine();
        }

        /// <summary>真实设备的混音格式必须能读出 SubFormat，而不是靠位深猜。</summary>
        private static void TestMixFormatSubtype()
        {
            Console.WriteLine("--- 真实设备混音格式的 SubFormat 读取 ---");
            try
            {
                var devices = DeviceEnumerator.Probe();
                int checkedCount = 0;

                foreach (var d in devices)
                {
                    if (!d.SupportsShared) continue;
                    checkedCount++;
                    var f = d.MixSampleFormat;
                    Console.WriteLine("  " + d.Name);
                    Console.WriteLine("    " + d.MixDescription);
                    Console.WriteLine("    -> 判定为 " + AudioCodec.Name(f));

                    // 本机混音格式是 32bit extensible；它必须是 float32 才算读对了
                    if (d.MixBits >= 32)
                        Check(f == SampleFormat.Float32, "32bit 混音格式判定为 float32",
                              "若判成 pcm24 说明只读了基础结构、没读 SubFormat");
                }

                Check(checkedCount > 0, "至少探测到一个共享模式可用设备",
                      "共 " + checkedCount + " 个");
            }
            catch (Exception ex)
            {
                Check(false, "设备探测未抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            Console.WriteLine();
        }
    }
}
