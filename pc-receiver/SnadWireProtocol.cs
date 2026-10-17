// SNAD 线路协议定义（PC 侧）。
//
// 单独成文件的理由：接收器、发包器、以后的自检工具都要用同一套包头定义。
// 放在接收器的 Program 类里时，发包器为了拿几个常量就得把整个接收器编进来——
// 那会让"协议"实际上只有一份实现、却被两处各自臆测。
//
// 与手机端 SnadProtocol.kt 必须一致。
//
// v1 包头（22 字节，小端）：
//   0..3   magic 'S','N','A','D'
//   4      version = 1
//   5      channels
//   6..9   sampleRate
//   10..13 sequence
//   14..17 timestampMs（发送端单调时钟低 32 位）
//   18..21 payloadBytes
//   22..   PCM16LE 交错载荷
//
// v2 包头（26 字节）：前 22 字节与 v1 **完全相同**，随后追加
//   22..23 format（u16）：0=pcm16 1=pcm24 2=pcm32 3=float32
//   24..25 flags（u16）：保留，当前为 0
//   26..   载荷
//
// 之所以这样追加而不是改动前 22 字节：v1 的解析器只读前 22 字节，
// 因此"只追加 + 升版本号"能让旧端继续读出核心字段，
// 而新端能按版本决定包头长度与载荷位深。

using System;

namespace Snad
{
    internal static class WireProtocol
    {
        /// <summary>v1 包头长度。</summary>
        public const int HeaderSizeV1 = 22;

        /// <summary>v2 包头长度。</summary>
        public const int HeaderSizeV2 = 26;

        /// <summary>文本日志包头长度（type = 'T'）。</summary>
        public const int TextHeaderSize = 14;

        public const byte Version1 = 1;
        public const byte Version2 = 2;

        /// <summary>按版本返回包头长度；未知版本返回 0，由调用方报错，不猜。</summary>
        public static int HeaderSizeFor(byte version)
        {
            if (version == Version1) return HeaderSizeV1;
            if (version == Version2) return HeaderSizeV2;
            return 0;
        }

        /// <summary>包头格式码 → 采样格式。</summary>
        public static SampleFormat FormatFromCode(int code)
        {
            switch (code)
            {
                case 1: return SampleFormat.Pcm24;
                case 2: return SampleFormat.Pcm32;
                case 3: return SampleFormat.Float32;
                default: return SampleFormat.Pcm16;
            }
        }

        /// <summary>采样格式 → 包头格式码。</summary>
        public static int CodeFromFormat(SampleFormat f)
        {
            switch (f)
            {
                case SampleFormat.Pcm24: return 1;
                case SampleFormat.Pcm32: return 2;
                case SampleFormat.Float32: return 3;
                default: return 0;
            }
        }

        /// <summary>写小端 32 位。</summary>
        public static void PutInt32LE(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        public static void PutInt16LE(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }
    }
}
