// 左右声道电平与波形分析。
//
// 与 SpectrumAnalyzer 分开：那个做 FFT 出频谱（单路混合），
// 这个只做逐声道统计，开销极小、每块都能更新，适合做实时电平表。
//
// 电平遵循专业工具的习惯：
//   峰值(peak)     —— 瞬时最大值，快起快落，用来发现削波
//   有效值(rms)    —— 响度感受，用指数平均平滑
// 两者分开显示而不是只给一个数：峰值超限但有效值正常，说明是瞬态；
// 有效值也高，才是整段偏响。

using System;

namespace Snad
{
    internal sealed class StereoMeter
    {
        public const int Channels = 2;
        public const int WavePoints = 256;    // 每声道保存的波形点数

        /// <summary>每声道的显示波形，范围 [-1, 1]。</summary>
        private readonly double[][] _wave = new double[Channels][];

        /// <summary>每声道的有效值（线性，0..1）。</summary>
        private readonly double[] _rms = new double[Channels];

        /// <summary>每声道的峰值（线性，0..1），带快起慢落包络。</summary>
        private readonly double[] _peak = new double[Channels];

        /// <summary>每声道的历史最大峰值，用于"是否曾经削波"的指示。</summary>
        private readonly double[] _hold = new double[Channels];

        private int _sampleRate = 48000;
        private int _channelsSeen = 2;

        /// <summary>
        /// 只推进包络衰减，不读新数据。
        ///
        /// 表头的下落速度按"每块衰减一定比例"实现，因此必须按块推进——
        /// 界面定时器（约 30fps）与音频块（约 60/s）频率不同，
        /// 由界面调用会让下落速度随刷新率变化。
        /// 用"每块固定比例"最简单，也与音频块的节奏一致。
        /// </summary>
        public void DecayOnly()
        {
            // 约 20dB/s：峰值保持可见但不至于长时间不动
            const double blockDecay = 0.985;
            for (int c = 0; c < Channels; c++)
            {
                _rms[c] *= RmsDecay;
                _peak[c] *= blockDecay;
                if (++_holdAge[c] > HoldBlocks) _hold[c] *= 0.9;
            }
        }

        /// <summary>RMS 下落比例。比峰值慢，读数才不会闪。</summary>
        private const double RmsDecay = 0.97;

        /// <summary>有效值平滑系数。越小越平滑、跟随越慢。</summary>
        private const double RmsSmoothing = 0.25;

        /// <summary>峰值下落速度（每块衰减比例）。0.88 约在 0.5 秒内落回。</summary>
        private const double PeakRelease = 0.88;

        /// <summary>历史峰值保持时间（块数）。约 3 秒后自动清除，避免一次削波永久亮着。</summary>
        private const int HoldBlocks = 180;

        private readonly int[] _holdAge = new int[Channels];

        public StereoMeter()
        {
            for (int c = 0; c < Channels; c++)
            {
                _wave[c] = new double[WavePoints];
                _rms[c] = 0;
                _peak[c] = 0;
                _hold[c] = 0;
                _holdAge[c] = HoldBlocks;
            }
        }

        public int SampleRate { get { return _sampleRate; } }
        public double[] Wave(int channel) { return _wave[channel]; }
        public double Rms(int channel) { return _rms[channel]; }
        public double Peak(int channel) { return _peak[channel]; }
        public double Hold(int channel) { return _hold[channel]; }

        /// <summary>线性幅度转 dB，下限 FloorDb。</summary>
        public static double ToDb(double linear)
        {
            if (linear <= 1e-6) return FloorDb;
            double db = 20.0 * Math.Log10(linear);
            return db < FloorDb ? FloorDb : db;
        }

        public const double FloorDb = -60.0;

        /// <summary>是否有任一声道曾经触及削波（按历史峰值判断）。</summary>
        public bool Clipped(int channel)
        {
            return _hold[channel] >= 0.999;
        }

        public void Reset(int sampleRate, int channels)
        {
            if (sampleRate > 0) _sampleRate = sampleRate;
            if (channels > 0) _channelsSeen = channels;
        }

        /// <summary>
        /// 喂入一块 PCM16LE。单声道时两路显示同一内容，
        /// 这样界面布局不必因声道数变化而改动。
        /// </summary>
        public void Feed(byte[] pcm, int bytes, int channels, int sampleRate)
        {
            if (pcm == null || bytes < 4)
            {
                // 空块也按静音推进包络：音频到达是间歇的（每 16ms 一块），
                // 块与块之间若不做衰减，表头会在有块时跳起、无块时归零，
                // 看起来像在闪烁。这里统一走一次衰减。
                DecayOnly();
                return;
            }
            if (channels < 1) channels = 1;
            Reset(sampleRate, channels);

            int bytesPerFrame = channels * 2;
            int frames = bytes / bytesPerFrame;
            if (frames <= 0) return;

            // ---- 波形：按峰值抽取到 WavePoints 个点 ----
            // 用区间内绝对值最大的样本，而不是抽样——抽样会漏掉瞬态，
            // 波形看起来比实际"安静"。
            int span = frames;
            for (int i = 0; i < WavePoints; i++)
            {
                int f0 = (int)((long)i * span / WavePoints);
                int f1 = (int)((long)(i + 1) * span / WavePoints);
                if (f1 <= f0) f1 = f0 + 1;
                if (f1 > frames) f1 = frames;

                for (int c = 0; c < Channels; c++)
                {
                    double best = 0;
                    int src = channels == 1 ? 0 : Math.Min(c, channels - 1);
                    for (int f = f0; f < f1; f++)
                    {
                        int b = f * bytesPerFrame + src * 2;
                        if (b + 1 >= bytes) break;
                        short s = (short)(pcm[b] | (pcm[b + 1] << 8));
                        double v = s / 32768.0;
                        if (Math.Abs(v) > Math.Abs(best)) best = v;
                    }
                    _wave[c][i] = best;
                }
            }

            // ---- 电平：逐声道的 RMS 与峰值 ----
            //
            // 必须按声道分别累加平方和。此前把两路的平方和合并后再开方，
            // 得到的是"总能量"而不是每声道电平，左声道因此虚高约 3dB
            // （√2 倍能量），推子/表头读数与实际不符。
            double[] sumSq = new double[Channels];
            int counted = 0;
            double[] blkPeak = new double[Channels];

            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * bytesPerFrame;
                for (int c = 0; c < Channels; c++)
                {
                    int src = channels == 1 ? 0 : Math.Min(c, channels - 1);
                    int b = baseIdx + src * 2;
                    if (b + 1 >= bytes) break;

                    short s = (short)(pcm[b] | (pcm[b + 1] << 8));
                    double v = s / 32768.0;
                    double a = Math.Abs(v);

                    if (a > blkPeak[c]) blkPeak[c] = a;
                    sumSq[c] += v * v;
                }
                counted++;
            }

            for (int c = 0; c < Channels; c++)
            {
                double blockRms = counted > 0 ? Math.Sqrt(sumSq[c] / counted) : 0;

                // RMS：指数平滑，避免数字跳动
                _rms[c] += (blockRms - _rms[c]) * RmsSmoothing;

                // 峰值：立即跟上，缓慢下落（专业表的通用做法）
                if (blkPeak[c] >= _peak[c]) _peak[c] = blkPeak[c];
                else _peak[c] *= PeakRelease;

                // 历史峰值：记录并计时清除
                if (blkPeak[c] >= _hold[c])
                {
                    _hold[c] = blkPeak[c];
                    _holdAge[c] = 0;
                }
                else if (++_holdAge[c] > HoldBlocks)
                {
                    _hold[c] *= 0.9;
                }
            }
        }

        /// <summary>清除历史峰值指示。</summary>
        public void ClearHold()
        {
            for (int c = 0; c < Channels; c++) { _hold[c] = 0; _holdAge[c] = HoldBlocks; }
        }
    }
}
