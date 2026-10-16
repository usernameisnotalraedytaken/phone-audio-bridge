// 频谱图面板：界面视觉主体，同时承载 EQ 编辑。
//
// 参照 FabFilter Pro-Q 的做法——图本身既是可视化也是控件：
//   - 频谱是低饱和渐隐填充（底部实、顶部透），不抢视线
//   - EQ 曲线是亮青描边 + 微发光，叠在频谱之上
//   - 频段节点可直接拖动，拖时显示读数，不需要另开窗口
//   - 频率轴对数分布并带刻度标题，dB 轴带数值，都是"专业工具"的基本要求
//
// 坐标轴映射集中在这里，因此轴标签位置与实际数据位置永远一致。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class SpectrumGraph : Control
    {
        // ---- 坐标轴范围 ----
        private const double FMin = 30.0;
        private const double FMax = 16000.0;
        private const double DbTop = 0.0;
        private const double DbBottom = SpectrumAnalyzer.FloorDb;   // -96

        // ---- 边距：左侧留给 dB 标签，底部留给频率标签 ----
        private const int PadLeft = 44;
        private const int PadRight = 14;
        private const int PadTop = 14;
        private const int PadBottom = 26;

        // ---- 栅格刻度 ----
        private static readonly double[] FreqTicks = { 30, 60, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        /// <summary>
        /// 频率刻度标签。单位直接写进标签（专业音频工具的做法），
        /// 省掉单独一个 "Hz" 单位——它与最右侧标签挤在一起无法两全。
        /// </summary>
        private static readonly string[] FreqLabels =
            { "30", "60", "125", "250", "500", "1k", "2k", "4k", "8k", "16kHz" };

        /// <summary>dB 主刻度（带标签）。密度参照专业音频工具，12dB 一档。</summary>
        private static readonly double[] DbTicks = { 0, -12, -24, -36, -48, -60, -72, -84 };

        /// <summary>dB 次刻度（不带标签，只画线）。</summary>
        private static readonly double[] DbMinorTicks = { -6, -18, -30, -42, -54, -66, -78, -90 };

        private readonly SpectrumAnalyzer _an;
        private Equalizer _eq;

        /// <summary>频谱动画平滑值（每根柱向目标值缓动，避免逐帧闪烁）。</summary>
        private float[] _smooth;
        private float[] _peakHold;
        private int[] _peakHoldFrames;

        private int _dragBand = -1;
        private int _hoverBand = -1;
        private Point _mousePos;
        private bool _mouseInside;

        /// <summary>拖动频段时回调，用于主窗口保存设置。</summary>
        public event Action<int, double> BandChanged;

        /// <summary>双击频段时归零。</summary>
        public event Action<int> BandReset;

        public SpectrumGraph(SpectrumAnalyzer an)
        {
            _an = an;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgBase;
            TabStop = false;
        }

        public Equalizer Eq
        {
            get { return _eq; }
            set { _eq = value; }
        }

        /// <summary>是否允许拖动频段节点。</summary>
        public bool AllowBandDrag = true;

        private RectangleF Plot
        {
            get
            {
                float w = Math.Max(10, Width - PadLeft - PadRight);
                float h = Math.Max(10, Height - PadTop - PadBottom);
                return new RectangleF(PadLeft, PadTop, w, h);
            }
        }

        // ---------------------------------------------------------------- 坐标映射

        private float FreqToX(double f)
        {
            var p = Plot;
            double t = (Math.Log10(f) - Math.Log10(FMin)) / (Math.Log10(FMax) - Math.Log10(FMin));
            return (float)(p.Left + t * p.Width);
        }

        private double XToFreq(float x)
        {
            var p = Plot;
            double t = (x - p.Left) / p.Width;
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Math.Pow(10, Math.Log10(FMin) + t * (Math.Log10(FMax) - Math.Log10(FMin)));
        }

        private float DbToY(double db)
        {
            var p = Plot;
            double t = (db - DbBottom) / (DbTop - DbBottom);
            if (t < 0) t = 0; if (t > 1) t = 1;
            return (float)(p.Bottom - t * p.Height);
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Theme.BgBase))
                g.FillRectangle(bg, ClientRectangle);

            var p = Plot;

            DrawBackgroundGradient(g, p);
            DrawGrid(g, p);
            DrawSpectrum(g, p);
            DrawGridOverlay(g, p);          // 补画横栅格，避免被频谱填充盖住
            DrawAxisLabels(g, p);
            if (_eq != null) DrawEqCurve(g, p);
            DrawInteraction(g, p);
            DrawFrame(g, p);
        }

        /// <summary>图区背景：从上到下极淡的提亮，制造纵深。</summary>
        private void DrawBackgroundGradient(Graphics g, RectangleF p)
        {
            using (var lg = new LinearGradientBrush(
                new RectangleF(p.Left, p.Top, p.Width, p.Height),
                Color.FromArgb(255, 16, 19, 25), Color.FromArgb(255, 12, 14, 18),
                LinearGradientMode.Vertical))
                g.FillRectangle(lg, p);
        }

        /// <summary>
        /// 栅格：频率竖线 + dB 横线。主刻度略亮，避免一片均匀网格。
        ///
        /// 频谱填充会盖住横线，因此必须再画一次（见 DrawGridOverlay）。
        /// </summary>
        private void DrawGrid(Graphics g, RectangleF p)
        {
            foreach (double f in FreqTicks)
            {
                float x = FreqToX(f);
                using (var pen = new Pen(Theme.Grid, 1f))
                    g.DrawLine(pen, x, p.Top, x, p.Bottom);
            }
            foreach (double db in DbTicks)
            {
                float y = DbToY(db);
                using (var pen = new Pen(db == 0 ? Theme.GridMajor : Theme.Grid, 1f))
                    g.DrawLine(pen, p.Left, y, p.Right, y);
            }
            foreach (double db in DbMinorTicks)
            {
                float y = DbToY(db);
                using (var pen = new Pen(Color.FromArgb(70, Theme.Grid), 1f))
                    g.DrawLine(pen, p.Left, y, p.Right, y);
            }
        }

        /// <summary>在频谱填充之上重画横栅格，让刻度始终可见（否则被填充盖住）。</summary>
        private void DrawGridOverlay(Graphics g, RectangleF p)
        {
            foreach (double db in DbTicks)
            {
                float y = DbToY(db);
                using (var pen = new Pen(Color.FromArgb(db == 0 ? 120 : 60, Theme.GridMajor), 1f))
                    g.DrawLine(pen, p.Left, y, p.Right, y);
            }
        }

        /// <summary>
        /// 频谱：暗色渐隐填充。
        /// 底部实、顶部透明——这样它读起来像"能量从底部升起"，
        /// 而不是一块实心色块压住 EQ 曲线。
        /// </summary>
        private void DrawSpectrum(Graphics g, RectangleF p)
        {
            if (_an == null) return;
            var bars = _an.BarsDb;
            int n = bars.Length;
            if (n < 2) return;

            // 平滑：每根柱向目标缓动，避免逐帧跳变
            if (_smooth == null || _smooth.Length != n)
            {
                _smooth = new float[n];
                _peakHold = new float[n];
                _peakHoldFrames = new int[n];
                for (int i = 0; i < n; i++) { _smooth[i] = (float)DbBottom; _peakHold[i] = (float)DbBottom; }
            }

            int cols = Math.Max(24, (int)(p.Width / 4));   // 每 4px 一列，柱宽随窗口自适应
            var pts = new PointF[cols];
            for (int c = 0; c < cols; c++)
            {
                // 列 -> 频率 -> 最近的频谱柱
                double f = XToFreq(p.Left + (c + 0.5f) * (p.Width / cols));
                int bi = FreqToBarIndex(f, n);
                float target = (float)bars[bi];

                // 上升快、下落慢，看起来更自然
                float cur = _smooth[bi];
                float next = target > cur ? cur + (target - cur) * 0.55f : cur + (target - cur) * 0.18f;
                _smooth[bi] = next;

                // 峰值保持
                if (next > _peakHold[bi]) { _peakHold[bi] = next; _peakHoldFrames[bi] = 45; }
                else if (_peakHoldFrames[bi] > 0) _peakHoldFrames[bi]--;
                else _peakHold[bi] += ((float)DbBottom - _peakHold[bi]) * 0.05f;

                float y = DbToY(next);
                pts[c] = new PointF(p.Left + (c + 0.5f) * (p.Width / cols), y);
            }

            // 填充：从曲线向下到基线，用纵向渐变让顶部自然淡出
            var poly = new PointF[cols + 2];
            Array.Copy(pts, poly, cols);
            poly[cols] = new PointF(p.Right, p.Bottom);
            poly[cols + 1] = new PointF(p.Left, p.Bottom);

            using (var lg = new LinearGradientBrush(
                new RectangleF(p.Left, p.Top, p.Width, p.Height),
                Theme.SpectrumLow, Theme.SpectrumHigh, LinearGradientMode.Vertical))
            {
                // 顶淡底实：靠 alpha 渐变实现，不用改颜色本身。
                // alpha 压得比较低——频谱是背景层，不能压过 EQ 曲线。
                var blend = new ColorBlend(3);
                blend.Positions = new[] { 0f, 0.55f, 1f };
                blend.Colors = new[]
                {
                    Color.FromArgb(18, Theme.SpectrumLow),
                    Color.FromArgb(70, Theme.Mix(Theme.SpectrumLow, Theme.SpectrumHigh, 0.5f)),
                    Color.FromArgb(120, Theme.SpectrumHigh),
                };
                lg.InterpolationColors = blend;
                g.FillPolygon(lg, poly);
            }

            // 顶边描一条细亮线：把频谱轮廓勾出来。
            // 只靠填充的话，能量少的地方会糊成一片，看不出谱形。
            using (var pen = new Pen(Color.FromArgb(150, Theme.Accent), 1.1f))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts);
            }

            // 峰值线：极淡，只在有明显信号时画
            for (int c = 0; c < cols; c++)
            {
                double f = XToFreq(p.Left + (c + 0.5f) * (p.Width / cols));
                int bi = FreqToBarIndex(f, n);
                if (_peakHold[bi] <= DbBottom + 6) continue;
                float y = DbToY(_peakHold[bi]);
                float x = p.Left + (c + 0.5f) * (p.Width / cols);
                using (var pen = new Pen(Color.FromArgb(70, Theme.SpectrumPeak), 1f))
                    g.DrawLine(pen, x, y, x, y + 2);
            }
        }

        /// <summary>把频率映射到频谱柱序号。频谱柱本身是对数分布的，这里做端点插值。</summary>
        private static int FreqToBarIndex(double f, int n)
        {
            double t = (Math.Log10(f) - Math.Log10(FMin)) / (Math.Log10(FMax) - Math.Log10(FMin));
            if (t < 0) t = 0; if (t > 1) t = 1;
            int i = (int)(t * (n - 1) + 0.5);
            if (i < 0) i = 0; if (i >= n) i = n - 1;
            return i;
        }

        /// <summary>坐标轴刻度标签：dB 在左，频率在下。</summary>
        private void DrawAxisLabels(Graphics g, RectangleF p)
        {
            // 注意：Theme 返回的是缓存字体，不能放进 using（会被 Dispose 掉，
            // 之后同尺寸字体再绘制就报"参数无效"）。只有 Brush/Pen 这类新建对象才用 using。
            var fDb = Theme.Metric(8.5f);
            var fUnit = Theme.Metric(7.5f);
            var fFreq = Theme.Metric(8.5f);

            using (var br = new SolidBrush(Theme.TextTertiary))
            {
                foreach (double db in DbTicks)
                {
                    string s = db == 0 ? "0" : db.ToString("0");
                    var sz = g.MeasureString(s, fDb);
                    g.DrawString(s, fDb, br, p.Left - 8 - sz.Width, DbToY(db) - sz.Height / 2);
                }

                // 单位放在图区左外侧上方，与顶部刻度留出距离
                var u = g.MeasureString("dB", fUnit);
                g.DrawString("dB", fUnit, br, p.Left - 8 - u.Width, p.Top - 13);

                for (int i = 0; i < FreqTicks.Length; i++)
                {
                    float x = FreqToX(FreqTicks[i]);
                    var sz = g.MeasureString(FreqLabels[i], fFreq);
                    // 边缘钳位：两端标签居中会超出控件被裁（实测 "16k" 只显示一半）
                    float tx = x - sz.Width / 2;
                    if (tx < 2) tx = 2;
                    if (tx + sz.Width > Width - 2) tx = Width - 2 - sz.Width;
                    g.DrawString(FreqLabels[i], fFreq, br, tx, p.Bottom + 7);
                }
            }
        }

        /// <summary>
        /// EQ 曲线：亮青描边 + 微发光，叠在频谱之上。
        /// 曲线用 Equalizer.ResponseDb 逐像素求值，因此与实际滤波器完全一致，
        /// 不是"画个好看的示意线"。
        /// </summary>
        private void DrawEqCurve(Graphics g, RectangleF p)
        {
            int steps = Math.Max(32, (int)p.Width / 3);
            var pts = new PointF[steps];
            for (int i = 0; i < steps; i++)
            {
                float x = p.Left + i * (p.Width / (steps - 1));
                double f = XToFreq(x);
                double db = _eq.ResponseDb(f);
                // 曲线显示范围 ±18dB 映射到绘图区上下留白内，超出部分贴边
                float y = DbToY(db);
                if (y < p.Top) y = p.Top;
                if (y > p.Bottom) y = p.Bottom;
                pts[i] = new PointF(x, y);
            }

            // 关闭时曲线压暗，明确区分"启用中"与"只是显示"
            bool on = _eq.Enabled;
            Color line = on ? Theme.Accent : Color.FromArgb(110, Theme.AccentSoft);

            // 0dB 参考线，让"平直"有基准可看
            float zeroY = DbToY(0);
            using (var pen = new Pen(Color.FromArgb(on ? 70 : 40, Theme.Accent), 1f))
            {
                pen.DashStyle = DashStyle.Dash;
                pen.DashPattern = new[] { 3f, 4f };
                g.DrawLine(pen, p.Left, zeroY, p.Right, zeroY);
            }

            Theme.GlowLine(g, pts, line, on ? 1.8f : 1.2f, on ? 3 : 1);
        }

        /// <summary>频段节点与拖动提示。</summary>
        private void DrawInteraction(Graphics g, RectangleF p)
        {
            if (_eq == null) return;

            for (int i = 0; i < _eq.Bands.Count; i++)
            {
                double f = _eq.Bands[i].Freq;
                float x = FreqToX(f);
                if (x < p.Left - 4 || x > p.Right + 4) continue;

                double db = _eq.Bands[i].GainDb;
                float y = DbToY(_eq.ResponseDb(f));

                bool active = (i == _dragBand) || (i == _hoverBand && _mouseInside);
                float r = active ? 6.5f : 4.5f;
                var col = _eq.Enabled ? Theme.Accent : Theme.AccentSoft;

                // 节点圆点：外圈淡填充 + 亮描边
                using (var br = new SolidBrush(Color.FromArgb(active ? 90 : 55, col)))
                    g.FillEllipse(br, x - r, y - r, r * 2, r * 2);
                using (var pen = new Pen(col, active ? 2f : 1.4f))
                    g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);

                // 拖动或悬停时显示该频段的读数（参数即读数）
                if (active)
                {
                    string label = _eq.Bands[i].Label + "  " + db.ToString("+0.0;-0.0;0.0") + " dB";
                    // 注意别用 f：上面 f 已是该频段的频率
                    var fTip = Theme.Metric(9f, FontStyle.Bold);
                    var sz = g.MeasureString(label, fTip);
                    float bw = sz.Width + 12, bh = sz.Height + 4;
                    float bx = x - bw / 2, by = y - r - bh - 6;
                    if (bx < p.Left) bx = p.Left;
                    if (bx + bw > p.Right) bx = p.Right - bw;
                    if (by < p.Top) by = y + r + 6;

                    using (var bb = new SolidBrush(Color.FromArgb(235, Theme.BgRaised)))
                        Theme.FillRound(g, bb, new RectangleF(bx, by, bw, bh), 4f);
                    using (var pen = new Pen(Color.FromArgb(120, col), 1f))
                        Theme.DrawRound(g, pen, new RectangleF(bx, by, bw, bh), 4f);
                    using (var br = new SolidBrush(Theme.TextPrimary))
                        g.DrawString(label, fTip, br, bx + 6, by + 2);
                }
            }
        }

        /// <summary>图区边框：极淡一圈，界定数据范围。</summary>
        private void DrawFrame(Graphics g, RectangleF p)
        {
            using (var pen = new Pen(Color.FromArgb(90, Theme.Line), 1f))
                g.DrawRectangle(pen, p.Left, p.Top, p.Width, p.Height);
        }

        // ---------------------------------------------------------------- 交互

        /// <summary>把鼠标位置映射到最近的频段（只认水平距离，因为频率轴是对数的）。</summary>
        private int HitTestBand(Point pt)
        {
            if (_eq == null) return -1;
            var p = Plot;
            if (pt.X < p.Left - 10 || pt.X > p.Right + 10) return -1;

            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _eq.Bands.Count; i++)
            {
                float x = FreqToX(_eq.Bands[i].Freq);
                float d = Math.Abs(pt.X - x);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            // 阈值随列宽缩放，避免频段密集处难以命中
            float threshold = Math.Max(14f, p.Width / Math.Max(1, _eq.Bands.Count) * 0.5f);
            return bestDist <= threshold ? best : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!AllowBandDrag || _eq == null) return;
            int b = HitTestBand(e.Location);
            if (b < 0) return;

            if (e.Button == MouseButtons.Right)
            {
                // 右键归零，与多数 EQ 插件一致
                _eq.SetGain(b, 0);
                if (BandReset != null) BandReset(b);
                Invalidate();
                return;
            }
            _dragBand = b;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mousePos = e.Location;
            _mouseInside = true;
            if (_eq == null) return;

            if (_dragBand >= 0)
            {
                // 纵向拖动改增益。映射到 ±18dB 显示范围，手感与图上曲线一致。
                var p = Plot;
                double db = DbBottom + (p.Bottom - e.Y) / p.Height * (DbTop - DbBottom);
                if (db > Equalizer.MaxGainDb) db = Equalizer.MaxGainDb;
                if (db < Equalizer.MinGainDb) db = Equalizer.MinGainDb;
                _eq.SetGain(_dragBand, db);
                if (BandChanged != null) BandChanged(_dragBand, db);
                Invalidate();
            }
            else
            {
                int h = HitTestBand(e.Location);
                if (h != _hoverBand) { _hoverBand = h; Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragBand >= 0)
            {
                int b = _dragBand;
                _dragBand = -1;
                Capture = false;
                if (BandChanged != null) BandChanged(b, _eq.Bands[b].GainDb);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseInside = false;
            if (_hoverBand >= 0) { _hoverBand = -1; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!AllowBandDrag || _eq == null) return;
            int b = HitTestBand(e.Location);
            if (b < 0) return;
            _eq.SetGain(b, 0);
            if (BandReset != null) BandReset(b);
            Invalidate();
        }
    }
}
