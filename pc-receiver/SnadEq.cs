// 均衡器：双二阶（biquad）滤波器组。
//
// 为什么用 biquad：每个频段只需 5 个系数和 2 个状态变量，单样本 O(1)，
// 在播放线程里逐帧处理也不影响实时性（对比 FFT 卷积需要分块且引入延迟）。
//
// 频段布局为 10 段，覆盖人耳主要范围：
//   低架 105Hz + 8 个峰值段 + 高架 10kHz
// 低频与高频用搁架式（shelf）而非峰值式，因为这两端更接近"整体抬升/衰减"，
// 峰值式在端点会造成不自然的峰。
//
// 系数来自 Audio EQ Cookbook（Robert Bristow-Johnson）的标准公式。
// 每次改变增益都重算系数，因此拖动滑块能立即听到变化。

using System;
using System.Collections.Generic;

namespace Snad
{
    /// <summary>单个双二阶节的直接I型实现。</summary>
    internal sealed class Biquad
    {
        // 归一化系数（已除以 a0）
        private double _b0 = 1, _b1, _b2, _a1, _a2;
        // 状态变量（每个声道独立，因此这里是两个）
        private double _x1L, _x2L, _y1L, _y2L;
        private double _x1R, _x2R, _y1R, _y2R;

        public void Reset()
        {
            _x1L = _x2L = _y1L = _y2L = 0;
            _x1R = _x2R = _y1R = _y2R = 0;
        }

        public void SetPeaking(double sampleRate, double freq, double gainDb, double q)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sampleRate;
            double cs = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / (2.0 * q);
            double a0 = 1 + alpha / A;
            _b0 = (1 + alpha * A) / a0;
            _b1 = (-2 * cs) / a0;
            _b2 = (1 - alpha * A) / a0;
            _a1 = (-2 * cs) / a0;
            _a2 = (1 - alpha / A) / a0;
        }

        public void SetLowShelf(double sampleRate, double freq, double gainDb, double slope)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sampleRate;
            double cs = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / 2.0 * Math.Sqrt((A + 1.0 / A) * (1.0 / slope - 1.0) + 2.0);
            double twoSqrtAalpha = 2.0 * Math.Sqrt(A) * alpha;
            double a0 = (A + 1) + (A - 1) * cs + twoSqrtAalpha;
            _b0 = A * ((A + 1) - (A - 1) * cs + twoSqrtAalpha) / a0;
            _b1 = 2 * A * ((A - 1) - (A + 1) * cs) / a0;
            _b2 = A * ((A + 1) - (A - 1) * cs - twoSqrtAalpha) / a0;
            _a1 = -2 * ((A - 1) + (A + 1) * cs) / a0;
            _a2 = ((A + 1) + (A - 1) * cs - twoSqrtAalpha) / a0;
        }

        public void SetHighShelf(double sampleRate, double freq, double gainDb, double slope)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sampleRate;
            double cs = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / 2.0 * Math.Sqrt((A + 1.0 / A) * (1.0 / slope - 1.0) + 2.0);
            double twoSqrtAalpha = 2.0 * Math.Sqrt(A) * alpha;
            double a0 = (A + 1) - (A - 1) * cs + twoSqrtAalpha;
            _b0 = A * ((A + 1) + (A - 1) * cs + twoSqrtAalpha) / a0;
            _b1 = -2 * A * ((A - 1) + (A + 1) * cs) / a0;
            _b2 = A * ((A + 1) + (A - 1) * cs - twoSqrtAalpha) / a0;
            _a1 = 2 * ((A - 1) - (A + 1) * cs) / a0;
            _a2 = ((A + 1) - (A - 1) * cs - twoSqrtAalpha) / a0;
        }

        /// <summary>处理一个立体声样本对。</summary>
        public void Process(ref double l, ref double r)
        {
            double yl = _b0 * l + _b1 * _x1L + _b2 * _x2L - _a1 * _y1L - _a2 * _y2L;
            _x2L = _x1L; _x1L = l; _y2L = _y1L; _y1L = yl;

            double yr = _b0 * r + _b1 * _x1R + _b2 * _x2R - _a1 * _y1R - _a2 * _y2R;
            _x2R = _x1R; _x1R = r; _y2R = _y1R; _y1R = yr;

            l = yl; r = yr;
        }

        /// <summary>在给定频率上的复增益幅度。用于频响验证，不参与实时处理。</summary>
        public double MagnitudeAt(double sampleRate, double freq)
        {
            double w = 2.0 * Math.PI * freq / sampleRate;
            // H(e^jw) = (b0 + b1 z^-1 + b2 z^-2) / (1 + a1 z^-1 + a2 z^-2)
            double cw = Math.Cos(w), sw = Math.Sin(w);
            double c2w = Math.Cos(2 * w), s2w = Math.Sin(2 * w);

            double nRe = _b0 + _b1 * cw + _b2 * c2w;
            double nIm = -(_b1 * sw + _b2 * s2w);
            double dRe = 1.0 + _a1 * cw + _a2 * c2w;
            double dIm = -(_a1 * sw + _a2 * s2w);

            double num = Math.Sqrt(nRe * nRe + nIm * nIm);
            double den = Math.Sqrt(dRe * dRe + dIm * dIm);
            return den < 1e-20 ? 0 : num / den;
        }
    }

    /// <summary>一个频段的设置。</summary>
    internal sealed class EqBand
    {
        public string Label;      // 显示用，如 "105"
        public double Freq;       // 中心/转折频率
        public double GainDb;     // 增益，负为衰减
        public bool IsLowShelf;
        public bool IsHighShelf;
        public double Q = 1.0;

        public EqBand(string label, double freq, bool lowShelf = false, bool highShelf = false, double q = 1.0)
        {
            Label = label; Freq = freq; IsLowShelf = lowShelf; IsHighShelf = highShelf; Q = q;
        }
    }

    /// <summary>均衡器：一串频段 + 前置增益，逐样本处理。</summary>
    internal sealed class Equalizer
    {
        public const double MinGainDb = -12.0;
        public const double MaxGainDb = 12.0;

        private readonly List<EqBand> _bands = new List<EqBand>();
        private readonly List<Biquad> _filters = new List<Biquad>();
        /// <summary>
        /// 当前采样率。
        ///
        /// 默认 48000，不能是 0。此前初值为 0，而 ResponseDb 与 Rebuild 都在
        /// _sampleRate &lt;= 0 时直接返回——于是：
        ///   ResponseDb 恒返回 0  →  曲线永远是一条直线
        ///   Rebuild 不执行       →  滤波器系数根本没算
        /// 而 Configure 只在 WaveOut.Play 里被调用，也就是说"未开始播放时
        /// 打开均衡器窗口"必然看到一条直线（实测正是用户报的现象）。
        /// 给一个合理的默认值，未配置时也能正确显示与试听。
        /// </summary>
        private int _sampleRate = 48000;
        private int _channels;
        private double _preampDb;
        private double _preampLinear = 1.0;

        /// <summary>
        /// 是否启用。关闭时 Process 直接返回，不做任何计算。
        /// 默认关闭：均衡器是可选效果，不应在用户没要求时改变声音。
        /// </summary>
        public volatile bool Enabled = false;

        public Equalizer()
        {
            // 10 段：低频与高频用搁架，中间 8 段峰值。
            // 频率取近似等比的常用值，接近硬件均衡器的刻度。
            _bands.Add(new EqBand("105", 105, lowShelf: true));
            _bands.Add(new EqBand("240", 240));
            _bands.Add(new EqBand("470", 470));
            _bands.Add(new EqBand("900", 900));
            _bands.Add(new EqBand("1.8k", 1800));
            _bands.Add(new EqBand("3.5k", 3500));
            _bands.Add(new EqBand("6k", 6000));
            _bands.Add(new EqBand("8k", 8000));
            _bands.Add(new EqBand("12k", 12000, highShelf: true));
            _bands.Add(new EqBand("16k", 16000));

            foreach (var _ in _bands) _filters.Add(new Biquad());
        }

        public IList<EqBand> Bands { get { return _bands; } }

        /// <summary>前置增益，用于补偿整体提升导致的削波。</summary>
        public double PreampDb
        {
            get { return _preampDb; }
            set
            {
                _preampDb = Math.Max(-24.0, Math.Min(12.0, value));
                _preampLinear = Math.Pow(10.0, _preampDb / 20.0);
            }
        }

        public int SampleRate { get { return _sampleRate; } }

        /// <summary>设置格式并重算全部系数。采样率变化时必须重算。</summary>
        public void Configure(int sampleRate, int channels)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            _sampleRate = sampleRate;
            _channels = channels;
            RebuildAll();
        }

        public void SetGain(int bandIndex, double gainDb)
        {
            if (bandIndex < 0 || bandIndex >= _bands.Count) return;
            _bands[bandIndex].GainDb = Math.Max(MinGainDb, Math.Min(MaxGainDb, gainDb));
            Rebuild(bandIndex);
        }

        /// <summary>
        /// 只设置增益值，不重算系数。
        ///
        /// 用于联动拖动：一次拖动会改动多个频段，若每个都重算系数，
        /// 10 个 biquad 的三角函数会被反复调用上百次。批量设完之后
        /// 再调一次 RebuildAll 即可。
        /// </summary>
        public void SetGainNoRebuild(int bandIndex, double gainDb)
        {
            if (bandIndex < 0 || bandIndex >= _bands.Count) return;
            _bands[bandIndex].GainDb = Math.Max(MinGainDb, Math.Min(MaxGainDb, gainDb));
        }

        /// <summary>重算所有频段系数。与 SetGainNoRebuild 配合使用。</summary>
        public void RebuildAllFilters()
        {
            RebuildAll();
        }

        /// <summary>把某个频段增益按给定增量调整，返回新值。</summary>
        public double NudgeGain(int bandIndex, double deltaDb)
        {
            if (bandIndex < 0 || bandIndex >= _bands.Count) return 0;
            SetGain(bandIndex, _bands[bandIndex].GainDb + deltaDb);
            return _bands[bandIndex].GainDb;
        }

        public void SetAllGains(double[] gains)
        {
            if (gains == null) return;
            for (int i = 0; i < _bands.Count && i < gains.Length; i++)
                _bands[i].GainDb = Math.Max(MinGainDb, Math.Min(MaxGainDb, gains[i]));
            RebuildAll();
        }

        public double[] GetGains()
        {
            var g = new double[_bands.Count];
            for (int i = 0; i < _bands.Count; i++) g[i] = _bands[i].GainDb;
            return g;
        }

        public void ResetAll()
        {
            foreach (var b in _bands) b.GainDb = 0;
            _preampDb = 0;
            _preampLinear = 1.0;
            RebuildAll();
        }

        public void ResetState()
        {
            foreach (var f in _filters) f.Reset();
        }

        private void Rebuild(int i)
        {
            if (_sampleRate <= 0) return;
            var b = _bands[i];
            var f = _filters[i];
            if (b.IsLowShelf) f.SetLowShelf(_sampleRate, b.Freq, b.GainDb, 0.9);
            else if (b.IsHighShelf) f.SetHighShelf(_sampleRate, b.Freq, b.GainDb, 0.9);
            else f.SetPeaking(_sampleRate, b.Freq, b.GainDb, b.Q);
        }

        private void RebuildAll()
        {
            for (int i = 0; i < _bands.Count; i++) Rebuild(i);
        }

        /// <summary>当前设置的总频响（dB），用于界面画曲线与验证。</summary>
        public double ResponseDb(double freq)
        {
            if (_sampleRate <= 0) return 0;
            double mag = 1.0;
            foreach (var f in _filters) mag *= f.MagnitudeAt(_sampleRate, freq);
            return 20.0 * Math.Log10(Math.Max(1e-12, mag)) + _preampDb;
        }

        /// <summary>
        /// 就地处理一段交错的 16 位 PCM。
        /// 在播放线程上调用，因此内部不做分配、不加锁。
        ///
        /// 不用 unsafe：Main 程序集不启用 /unsafe，用 BitConverter 读写样本
        /// 在 JIT 后与指针版本差异可忽略（都是两条指令），却省掉一个构建约束。
        /// </summary>
        public void Process(byte[] buffer, int byteCount)
        {
            if (!Enabled || _sampleRate <= 0 || byteCount < 4) return;

            int sampleCount = byteCount / 2;              // 16 位样本数
            int frames = _channels > 1 ? sampleCount / 2 : sampleCount;
            bool stereo = _channels > 1;

            for (int i = 0; i < frames; i++)
            {
                int off = stereo ? i * 4 : i * 2;

                double l, r;
                if (stereo)
                {
                    l = (short)(buffer[off] | (buffer[off + 1] << 8));
                    r = (short)(buffer[off + 2] | (buffer[off + 3] << 8));
                }
                else
                {
                    l = r = (short)(buffer[off] | (buffer[off + 1] << 8));
                }

                // 各频段串联：每个 biquad 依次处理
                for (int b = 0; b < _filters.Count; b++)
                    _filters[b].Process(ref l, ref r);

                if (_preampLinear != 1.0) { l *= _preampLinear; r *= _preampLinear; }

                // 硬限幅：宁可削顶也不要把溢出后的反相噪声送出去
                if (l > 32767.0) l = 32767.0; else if (l < -32768.0) l = -32768.0;
                if (r > 32767.0) r = 32767.0; else if (r < -32768.0) r = -32768.0;

                short sl = (short)l, sr = (short)r;
                if (stereo)
                {
                    buffer[off] = (byte)(sl & 0xFF);
                    buffer[off + 1] = (byte)((sl >> 8) & 0xFF);
                    buffer[off + 2] = (byte)(sr & 0xFF);
                    buffer[off + 3] = (byte)((sr >> 8) & 0xFF);
                }
                else
                {
                    buffer[off] = (byte)(sl & 0xFF);
                    buffer[off + 1] = (byte)((sl >> 8) & 0xFF);
                }
            }
        }
    }
}
