// 实时频谱与波形分析，供 GUI 绘制。
//
// 设计要点：
//   1. 就地实现基 2 FFT，不引入任何外部库（本机无法使用 NuGet）。
//   2. 加 Hann 窗抑制频谱泄漏——否则单音会拖出旁瓣，看起来像噪声。
//   3. 频点按对数分布归并到显示柱：低频给更多柱，符合听觉分辨率，
//      而不是把线性频点直接压扁（那样低频只有一两根柱子）。
//   4. 峰值带衰减，视觉上自然下落而不是闪烁。

using System;

namespace Snad
{
    internal sealed class SpectrumAnalyzer
    {
        public const int FftSize = 1024;          // 2 的幂
        private const int Bins = FftSize / 2;      // 有效频点
        public const int Bars = 64;                // 显示柱数

        private readonly double[] _re = new double[FftSize];
        private readonly double[] _im = new double[FftSize];
        private readonly double[] _window = new double[FftSize];
        private readonly double[] _bars = new double[Bars];
        private readonly double[] _peak = new double[Bars];
        private readonly double[] _wave = new double[512];

        private readonly int[] _barStart = new int[Bars];
        private readonly int[] _barEnd = new int[Bars];

        private int _sampleRate = 48000;
        private int _channels = 2;

        public double[] BarsDb { get { return _bars; } }
        public double[] PeaksDb { get { return _peak; } }
        public double[] Waveform { get { return _wave; } }

        public SpectrumAnalyzer()
        {
            // Hann 窗
            for (int i = 0; i < FftSize; i++)
                _window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));

            // 关键：初始值必须是底噪，不能是 0。
            // 0 dB 在显示范围里是满量程，未启动时会把每根柱子画满（实测正是如此）。
            for (int b = 0; b < Bars; b++) { _bars[b] = FloorDb; _peak[b] = FloorDb; }
            for (int i = 0; i < _wave.Length; i++) _wave[i] = 0;

            BuildLogBins();
        }

        /// <summary>
        /// 按对数频率把 FFT 频点映射到显示柱。低频区间窄、高频区间宽，
        /// 因此每根柱覆盖的频点数不同，这正是我们要的听感分布。
        /// </summary>
        private void BuildLogBins()
        {
            double fMin = 30.0, fMax = 16000.0;
            double lnMin = Math.Log(fMin), lnMax = Math.Log(fMax);
            for (int b = 0; b < Bars; b++)
            {
                double f0 = Math.Exp(lnMin + (lnMax - lnMin) * b / Bars);
                double f1 = Math.Exp(lnMin + (lnMax - lnMin) * (b + 1) / Bars);
                int i0 = (int)(f0 * FftSize / _sampleRate);
                int i1 = (int)(f1 * FftSize / _sampleRate);
                if (i1 <= i0) i1 = i0 + 1;
                _barStart[b] = Math.Max(1, Math.Min(Bins - 1, i0));
                _barEnd[b] = Math.Max(_barStart[b] + 1, Math.Min(Bins, i1));
            }
        }

        public void Reset(int sampleRate, int channels)
        {
            if (sampleRate > 0 && sampleRate != _sampleRate)
            {
                _sampleRate = sampleRate;
                BuildLogBins();
            }
            if (channels > 0) _channels = channels;
        }

        private long _framesSeen;

        /// <summary>
        /// 喂入一块立体声交错 PCM16LE。为了降低开销，每两次调用才做一次 FFT
        /// （波形仍然每次都更新）。
        /// </summary>
        public void Feed(byte[] pcm, int bytes, int channels, int sampleRate)
        {
            if (pcm == null || bytes < 8) return;
            if (channels < 1) channels = 1;
            if (sampleRate > 0 && sampleRate != _sampleRate) Reset(sampleRate, channels);

            int bytesPerFrame = channels * 2;
            int frames = bytes / bytesPerFrame;
            if (frames <= 0) return;

            // 波形：取一块居中区域，做峰值抽取到 512 点
            int waveSpan = Math.Min(frames, FftSize);
            int waveOff = Math.Max(0, (frames - waveSpan) / 2);
            for (int i = 0; i < _wave.Length; i++)
            {
                int f = waveOff + (int)((long)i * waveSpan / _wave.Length);
                if (f >= frames) f = frames - 1;
                int b = f * bytesPerFrame;
                short l = (short)(pcm[b] | (pcm[b + 1] << 8));
                _wave[i] = l / 32768.0;
            }

            // 频谱：隔次计算，省一半 CPU
            _framesSeen++;
            if ((_framesSeen & 1) != 0) return;

            int take = Math.Min(FftSize, frames);
            int off = Math.Max(0, (frames - take) / 2);
            for (int i = 0; i < FftSize; i++)
            {
                int f = off + i;
                double v = 0;
                if (f < frames)
                {
                    int b = f * bytesPerFrame;
                    short l = (short)(pcm[b] | (pcm[b + 1] << 8));
                    short r = channels >= 2 ? (short)(pcm[b + 2] | (pcm[b + 3] << 8)) : l;
                    v = (l + r) * 0.5 / 32768.0;
                }
                _re[i] = v * _window[i];
                _im[i] = 0;
            }

            Fft(_re, _im);

            for (int b = 0; b < Bars; b++)
            {
                double sum = 0;
                int n = 0;
                for (int i = _barStart[b]; i < _barEnd[b]; i++)
                {
                    double mag = Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
                    sum += mag * mag;      // 功率求和
                    n++;
                }
                double power = n > 0 ? sum / n : 0;
                // 归一化到满量程：FFT 结果与窗增益、点数有关
                double db = power > 1e-20
                    ? 10.0 * Math.Log10(power / (FftSize * FftSize / 16.0))
                    : FloorDb;

                // 峰值带衰减：新值更高就抬升，否则缓慢下落
                if (db > _peak[b]) _peak[b] = db;
                else _peak[b] -= 0.8;
                if (_peak[b] < FloorDb) _peak[b] = FloorDb;

                _bars[b] = db;
            }
        }

        /// <summary>底噪下限。低于此值一律按 FloorDb 显示，避免把"无声"画成满柱。</summary>
        public const double FloorDb = -96.0;

        /// <summary>本帧是否完全没有信号（用于让面板显示为静默状态而不是满柱）。</summary>
        public bool IsSilent()
        {
            for (int b = 0; b < Bars; b++)
                if (_bars[b] > FloorDb + 3.0) return false;
            return true;
        }

        /// <summary>就地基 2 FFT（Cooley-Tukey，迭代版）。</summary>
        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            // 位反转置换
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
