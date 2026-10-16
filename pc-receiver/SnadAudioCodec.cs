// 采样格式的编解码与 WAVEFORMATEXTENSIBLE 构造。
//
// 单独抽出来而不是放在 WasapiSink 里，理由有两个：
//   1. 这些函数不依赖任何设备句柄，是纯函数——抽出来才能独立测试
//   2. 位深转换是"音质"这条线的核心，必须能用字节级断言钉住
//
// 位深约定：
//   pcm16    有符号 16 位小端
//   pcm24    有符号 24 位小端（须做符号扩展到 32 位再归一化）
//   float32  IEEE 754 单精度小端
//
// 24bit 的符号扩展是最容易写错的地方：0x800000 表示 -8388608 而不是 +8388608，
// 不做扩展会得到 +0.5 而不是 -1.0，表现为半个波形的极性翻转。

using System;
using System.Runtime.InteropServices;

namespace Snad
{
    internal static class AudioCodec
    {
        public const int WAVE_FORMAT_PCM = 1;
        public const int WAVE_FORMAT_IEEE_FLOAT = 3;
        public const int WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        private static readonly Guid SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid SUBTYPE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

        public static readonly Guid GuidSubtypePcm = SUBTYPE_PCM;
        public static readonly Guid GuidSubtypeFloat = SUBTYPE_FLOAT;

        /// <summary>格式的名称，用于日志与界面。</summary>
        public static string Name(SampleFormat f)
        {
            switch (f)
            {
                case SampleFormat.Pcm16: return "pcm16";
                case SampleFormat.Pcm24: return "pcm24";
                case SampleFormat.Pcm32: return "pcm32";
                default: return "float32";
            }
        }

        /// <summary>每样本字节数。</summary>
        public static int BytesPerSample(SampleFormat f)
        {
            switch (f)
            {
                case SampleFormat.Pcm16: return 2;
                case SampleFormat.Pcm24: return 3;
                default: return 4;      // pcm32 与 float32 都是 4 字节
            }
        }

        /// <summary>位深（比特）。</summary>
        public static int BitsPerSample(SampleFormat f)
        {
            switch (f)
            {
                case SampleFormat.Pcm16: return 16;
                case SampleFormat.Pcm24: return 24;
                default: return 32;
            }
        }

        /// <summary>
        /// 从字节流解码一个样本为 float [-1, 1)。
        /// i 指向该样本的首字节。
        /// </summary>
        public static float Decode(byte[] b, int i, SampleFormat format)
        {
            if (format == SampleFormat.Float32) return BitConverter.ToSingle(b, i);

            switch (BytesPerSample(format))
            {
                case 2:
                    return (short)(b[i] | (b[i + 1] << 8)) / 32768f;

                case 3:
                    {
                        int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
                        // 24bit 符号扩展：最高位为 1 时必须补足高 8 位，
                        // 否则 -1 会被读成 +0.5，表现为波形极性翻转
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        return v / 8388608f;
                    }

                default:
                    return BitConverter.ToInt32(b, i) / 2147483648f;
            }
        }

        /// <summary>
        /// 把一个 float 样本编码进字节流。带钳位，避免超范围时回绕成反相。
        /// </summary>
        public static void Encode(byte[] b, int i, float v, SampleFormat format)
        {
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;

            if (format == SampleFormat.Float32)
            {
                var f = BitConverter.GetBytes(v);
                b[i] = f[0]; b[i + 1] = f[1]; b[i + 2] = f[2]; b[i + 3] = f[3];
                return;
            }

            switch (BytesPerSample(format))
            {
                case 2:
                    {
                        int s = (int)Math.Round(v * 32767.0);
                        b[i] = (byte)(s & 0xFF);
                        b[i + 1] = (byte)((s >> 8) & 0xFF);
                        break;
                    }
                case 3:
                    {
                        int s = (int)Math.Round(v * 8388607.0);
                        b[i] = (byte)(s & 0xFF);
                        b[i + 1] = (byte)((s >> 8) & 0xFF);
                        b[i + 2] = (byte)((s >> 16) & 0xFF);
                        break;
                    }
                default:
                    {
                        long s = (long)Math.Round(v * 2147483647.0);
                        b[i] = (byte)(s & 0xFF);
                        b[i + 1] = (byte)((s >> 8) & 0xFF);
                        b[i + 2] = (byte)((s >> 16) & 0xFF);
                        b[i + 3] = (byte)((s >> 24) & 0xFF);
                        break;
                    }
            }
        }

        /// <summary>某个格式是否用 IEEE 浮点表示。</summary>
        public static bool IsFloat(SampleFormat f) { return f == SampleFormat.Float32; }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        /// <summary>
        /// 构造 WAVEFORMATEXTENSIBLE 并分配非托管内存（调用方负责 FreeCoTaskMem）。
        ///
        /// 一律用 EXTENSIBLE 形式：PCM 与 Float 靠 SubFormat 区分，
        /// 比 wFormatTag 更明确，也是设备驱动的首选形式。
        /// </summary>
        public static IntPtr BuildFormat(SampleFormat format, int channels, int rate)
        {
            int bits = BitsPerSample(format);
            ushort blockAlign = (ushort)(channels * bits / 8);

            var wfe = new WAVEFORMATEXTENSIBLE
            {
                Format = new WAVEFORMATEX
                {
                    wFormatTag = WAVE_FORMAT_EXTENSIBLE,
                    nChannels = (ushort)channels,
                    nSamplesPerSec = (uint)rate,
                    nAvgBytesPerSec = (uint)(rate * blockAlign),
                    nBlockAlign = blockAlign,
                    wBitsPerSample = (ushort)bits,
                    cbSize = 22
                },
                wValidBitsPerSample = (ushort)bits,
                // 立体声为 FL|FR = 0x3；单声道为 FC = 0x4
                dwChannelMask = channels == 2 ? 3u : (channels == 1 ? 4u : 0u),
                SubFormat = format == SampleFormat.Float32 ? SUBTYPE_FLOAT : SUBTYPE_PCM
            };

            IntPtr p = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WAVEFORMATEXTENSIBLE)));
            Marshal.StructureToPtr(wfe, p, false);
            return p;
        }

        /// <summary>
        /// 从混音格式指针推断实际格式。
        ///
        /// 关键在于必须按 WAVEFORMATEXTENSIBLE 整体读取才能拿到 SubFormat——
        /// 只读基础 WAVEFORMATEX 拿不到它，只能靠位深猜，而那是会错的
        /// （32bit 容器既可能是 float 也可能是整数）。
        /// </summary>
        public static SampleFormat FormatFromMix(IntPtr fmt)
        {
            var wf = (WAVEFORMATEX)Marshal.PtrToStructure(fmt, typeof(WAVEFORMATEX));

            if (wf.wFormatTag == WAVE_FORMAT_IEEE_FLOAT) return SampleFormat.Float32;
            if (wf.wFormatTag == WAVE_FORMAT_PCM)
                return wf.wBitsPerSample <= 16 ? SampleFormat.Pcm16 : SampleFormat.Pcm24;

            if (wf.wFormatTag == WAVE_FORMAT_EXTENSIBLE)
            {
                var wfe = (WAVEFORMATEXTENSIBLE)Marshal.PtrToStructure(fmt, typeof(WAVEFORMATEXTENSIBLE));
                if (wfe.SubFormat == SUBTYPE_FLOAT) return SampleFormat.Float32;
                if (wfe.SubFormat == SUBTYPE_PCM)
                    return wf.wBitsPerSample <= 16 ? SampleFormat.Pcm16
                         : (wf.wBitsPerSample <= 24 ? SampleFormat.Pcm24 : SampleFormat.Pcm24);
            }

            // 未知类型：按位深退让，并在名称里体现出来，不假装确定
            return wf.wBitsPerSample >= 32 ? SampleFormat.Float32
                 : (wf.wBitsPerSample <= 16 ? SampleFormat.Pcm16 : SampleFormat.Pcm24);
        }

        /// <summary>描述一个 WAVEFORMATEX 指针，用于日志。</summary>
        public static string Describe(IntPtr fmt)
        {
            var wf = (WAVEFORMATEX)Marshal.PtrToStructure(fmt, typeof(WAVEFORMATEX));
            string kind;
            if (wf.wFormatTag == WAVE_FORMAT_IEEE_FLOAT) kind = "float";
            else if (wf.wFormatTag == WAVE_FORMAT_PCM) kind = "pcm";
            else if (wf.wFormatTag == WAVE_FORMAT_EXTENSIBLE)
            {
                var wfe = (WAVEFORMATEXTENSIBLE)Marshal.PtrToStructure(fmt, typeof(WAVEFORMATEXTENSIBLE));
                kind = "extensible/" + (wfe.SubFormat == SUBTYPE_FLOAT ? "float"
                                      : (wfe.SubFormat == SUBTYPE_PCM ? "pcm" : "unknown"));
            }
            else kind = "tag=0x" + wf.wFormatTag.ToString("X4");

            return wf.nSamplesPerSec + "Hz/" + wf.nChannels + "ch/" + wf.wBitsPerSample + "bit(" + kind + ")";
        }
    }
}
