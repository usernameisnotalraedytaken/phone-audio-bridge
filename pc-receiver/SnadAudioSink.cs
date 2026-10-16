// 音频输出后端抽象。
//
// 为什么要有这一层：
//   waveOut 的时间粒度粗（约 10ms 周期），必须靠深缓冲池换稳定，
//   于是"在飞"的音频量很大——这是当前延迟的最大单项（256ms）。
//   WASAPI 事件驱动可以把周期压到毫秒级，在飞量降到十几毫秒。
//
//   但换后端有风险（独占会占设备、某些设备不支持独占），
//   因此必须**可切换**而不是替换：出问题时能退回 waveOut。
//
// 两种工作模式：
//   推模式  调用方主动 Write，后端自己排队。waveOut 用这种。
//   事件模式 后端在需要数据时回调，调用方在回调里填。WASAPI 用这种。
// 统一成"后端声明自己需要多少数据，调用方按需提供"。

using System;

namespace Snad
{
    /// <summary>采样格式。</summary>
    internal enum SampleFormat
    {
        Pcm16 = 0,
        Pcm24 = 1,
        Pcm32 = 2,
        Float32 = 3
    }

    /// <summary>输出后端类型。</summary>
    internal enum OutputBackend
    {
        WaveOut = 0,
        WasapiShared = 1,
        WasapiExclusive = 2
    }

    /// <summary>
    /// 一个输出设备的能力。由探测得出，编辑器据此限制可选项——
    /// 不能让用户选出跑不起来的组合（例如 Voicemeeter + 独占）。
    /// </summary>
    internal sealed class DeviceCapability
    {
        public string Id = "";
        public string Name = "";
        public int Index = -1;

        /// <summary>设备混音格式的采样率（共享模式必须用它）。</summary>
        public int MixRate;
        public int MixChannels;

        /// <summary>设备混音格式的位深（仅作参考，共享模式实际格式由系统决定）。</summary>
        public int MixBits;

        /// <summary>混音格式解析出的采样格式（由 SubFormat 判定，不靠位深猜）。</summary>
        public SampleFormat MixSampleFormat = SampleFormat.Float32;

        /// <summary>混音格式的可读描述，用于日志与编辑器。</summary>
        public string MixDescription = "";

        /// <summary>默认周期（毫秒）。</summary>
        public double DefaultPeriodMs;

        /// <summary>最小周期（毫秒）。独占模式可压到此值。</summary>
        public double MinPeriodMs;

        /// <summary>独占模式下被设备接受的格式。</summary>
        public bool ExclusivePcm16, ExclusivePcm24, ExclusivePcm32, ExclusiveFloat32;

        /// <summary>是否支持独占模式（任一格式被接受即为真）。</summary>
        public bool SupportsExclusive
        {
            get { return ExclusivePcm16 || ExclusivePcm24 || ExclusivePcm32 || ExclusiveFloat32; }
        }

        /// <summary>共享模式是否可用（混音格式取得即为可用）。</summary>
        public bool SupportsShared { get { return MixRate > 0; } }

        public bool ExclusiveSupports(SampleFormat f)
        {
            switch (f)
            {
                case SampleFormat.Pcm16: return ExclusivePcm16;
                case SampleFormat.Pcm24: return ExclusivePcm24;
                case SampleFormat.Float32: return ExclusiveFloat32;
                default: return false;
            }
        }

        /// <summary>可用格式的中文说明，用于编辑器与日志。</summary>
        public string ExclusiveFormatsText()
        {
            if (!SupportsExclusive) return "无";
            var parts = new System.Collections.Generic.List<string>();
            if (ExclusivePcm16) parts.Add("pcm16");
            if (ExclusivePcm24) parts.Add("pcm24");
            if (ExclusivePcm32) parts.Add("pcm32");
            if (ExclusiveFloat32) parts.Add("float32");
            return string.Join(" / ", parts.ToArray());
        }

        public override string ToString()
        {
            return Name + "（周期 " + DefaultPeriodMs.ToString("F1") + "/" + MinPeriodMs.ToString("F1") +
                   " ms，独占: " + ExclusiveFormatsText() + "）";
        }
    }

    /// <summary>
    /// 输出后端。实现要保证：Write 之后音频最终会被播放，
    /// 且 <see cref="QueuedMs"/> 能反映当前"在飞"的量——
    /// 这个值就是延迟估计里 soundcard 那一项的来源。
    /// </summary>
    internal interface IAudioSink : IDisposable
    {
        /// <summary>后端名称，用于日志与界面。</summary>
        string Name { get; }

        /// <summary>
        /// 打开设备。失败必须抛出并带明确原因，**不得静默回退**——
        /// "该设备是否支持所选模式与格式"本身就是要告诉用户的信息。
        /// </summary>
        /// <param name="exclusive">是否独占模式。共享模式下采样率与声道必须跟随设备混音格式。</param>
        void Open(DeviceCapability device, SampleFormat format, int sampleRate, int channels,
                  int periodMs, bool exclusive);

        /// <summary>
        /// 写入一段交错 PCM（字节序为小端，位深由 Open 时的 format 决定）。
        /// 返回实际接受的字节数；返回 0 表示后端暂时满，调用方应稍后再试。
        /// </summary>
        int Write(byte[] buffer, int offset, int count);

        /// <summary>当前在飞的音频毫秒数。延迟估计的 soundcard 项取自这里。</summary>
        double QueuedMs { get; }

        /// <summary>后端内部缓冲可容纳的毫秒数（上限）。</summary>
        double CapacityMs { get; }

        /// <summary>启动播放。</summary>
        void Start();

        /// <summary>停止播放并清空队列。</summary>
        void Stop();
    }
}
