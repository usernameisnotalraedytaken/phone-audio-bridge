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
        /// <summary>
        /// FFT 点数。
        ///
        /// 这个值直接决定低频能不能看。频率分辨率 = 采样率 / FftSize：
        ///   1024 点 @48kHz -> 每 bin 46.9Hz，而频谱从 30Hz 起，
        ///                     最低两个八度只剩 2 个数据点，画出来是一条平线
        ///                     （用户实测发现）。
        ///   4096 点 @48kHz -> 每 bin 11.7Hz，30~100Hz 之间有 6 根柱可用。
        ///
        /// 再加大会更细，但单次 FFT 成本按 N·log N 增长，且时间窗变长
        /// （4096 点约 85ms），瞬态响应变钝。4096 是这两者的折中。
        /// </summary>
        public const int FftSize = 4096;           // 2 的幂
        private const int Bins = FftSize / 2;      // 有效频点
        public const int Bars = 64;                // 显示柱数

        /// <summary>
        /// 每次分析前移的样本数。小于 FftSize 即形成重叠分析：
        /// 刷新率 = 采样率 / HopSize，4096/1024 在 48kHz 下约 47 次/秒，
        /// 视觉上足够顺滑，且不必为每个音频块都做一次 FFT。
        /// </summary>
        private const int HopSize = FftSize / 4;

        private readonly double[] _re = new double[FftSize];
        private readonly double[] _im = new double[FftSize];
        private readonly double[] _window = new double[FftSize];
        private readonly double[] _bars = new double[Bars];
        private readonly double[] _peak = new double[Bars];
        private readonly double[] _wave = new double[512];

        /// <summary>
        /// 环形缓冲：累积足够的样本再做 FFT。
        /// 直接用"本块样本"会因块长不定而让有效分辨率随块长漂移，
        /// 环缓冲保证每次分析的都是固定的 FftSize 个样本。
        /// </summary>
        private readonly double[] _ring = new double[FftSize];
        private int _ringFill;
        private int _ringPos;
        private int _sinceAnalysis;

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
            bool rateChanged = sampleRate > 0 && sampleRate != _sampleRate;
            if (sampleRate > 0) _sampleRate = sampleRate;
            if (channels > 0) _channels = channels;

            // bin 映射依赖采样率，变了必须重算
            if (rateChanged) BuildLogBins();

            // 环缓冲也要清空：换了采样率或重启流之后，
            // 里面还留着上一段的样本，混进来会让最初几次分析是错的
            _ringFill = 0;
            _ringPos = 0;
            _sinceAnalysis = 0;
        }

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

            // ---- 频谱：把样本填入环缓冲，攒够一跳就分析一次 ----
            //
            // 每个低频柱至少覆盖 1 个 bin 已由 BuildLogBins 保证，
            // 但 30~100Hz 能否分辨取决于 FftSize（见其注释）。
            for (int i = 0; i < frames; i++)
            {
                int b = i * bytesPerFrame;
                short l = (short)(pcm[b] | (pcm[b + 1] << 8));
                short r = channels >= 2 ? (short)(pcm[b + 2] | (pcm[b + 3] << 8)) : l;
                _ring[_ringPos] = (l + r) * 0.5 / 32768.0;
                _ringPos++;
                if (_ringPos >= FftSize) _ringPos = 0;
                if (_ringFill < FftSize) _ringFill++;
                _sinceAnalysis++;
            }

            if (_ringFill < FftSize || _sinceAnalysis < HopSize) return;
            _sinceAnalysis = 0;
            AnalyzeFft();
        }

        /// <summary>
        /// 对环缓冲里最近的 FftSize 个样本做一次加窗 FFT，并归并到显示柱。
        /// 与 Feed 分开是为了让"取样本"和"算频谱"各自清楚。
        /// </summary>
        private void AnalyzeFft()
        {
            // 从最旧的样本开始取，保证时间顺序正确
            int start = _ringFill < FftSize ? 0 : _ringPos;
            for (int i = 0; i < FftSize; i++)
            {
                int idx = start + i;
                if (idx >= FftSize) idx -= FftSize;
                _re[i] = _ring[idx] * _window[i];
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
                // 归一化到满量程：FFT 结果与窗增益、点数有关。
                // 系数里的 16 来自 Hann 窗的相干增益（0.5）平方后的倒数修正，
                // 除以 FftSize^2 把点数的影响消掉，换 FftSize 不必重调标定。
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
