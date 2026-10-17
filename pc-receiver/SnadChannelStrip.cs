// 声道条：一个声道的波形 + 电平表。
//
// 布局（横向）：
//   [ 声道名 ]  [       波形       ]  [ 电平表 ]
//                ^ 中线为零电平        ^ 有效值填充
//                                       ^ 峰值刻线
//                                       ^ 历史峰值标记
//
// 为什么波形与电平要并排：波形看"形状"（是否削顶、是否单边偏），
// 电平表看"数值"（多响、离满量程还有多少）。专业工具里这两者是
// 分工的，不能互相替代——只看电平不知道是否削波，只看波形不知道具体余量。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class ChannelStrip : Control
    {
        private readonly int _channel;
        private StereoMeter _meter;

        /// <summary>显示用的声道名（L / R）。</summary>
        public string ChannelName = "L";

        /// <summary>电平表刻度下限（dB）。低于它不显示。</summary>
        public double MeterFloorDb = -60.0;

        public ChannelStrip(int channel, StereoMeter meter)
        {
            _channel = channel;
            _meter = meter;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Height = 64;
        }

        public StereoMeter Meter
        {
            get { return _meter; }
            set { _meter = value; Invalidate(); }
        }

        private const int LabelW = 22;
        // 竖表宽度：两根柱(19) + 刻度数字(约 30) + 数值读数要放得下两个 "-18.0"
        // （每条约 40px，并排需约 82）。按最宽的那个留，否则会被右边界裁掉。
        // 竖表宽度：两根柱(约 44) + 刻度数字(约 26) + 余量。
        // 下方两个读数（各约 38px）要居中于各自柱子，太窄会挤在一起，
        // 因此这里要留够——窄表配宽读数必然重叠。
        private const int MeterW = 116;
        private const int Pad = 8;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);

            if (_meter == null) return;

            int waveX = Pad + LabelW + Pad;
            int waveW = Math.Max(20, ClientSize.Width - waveX - MeterW - Pad * 3);
            int waveY = Pad;
            int waveH = Math.Max(10, ClientSize.Height - Pad * 2);

            DrawName(g, Pad, ClientSize.Height);
            DrawWave(g, new Rectangle(waveX, waveY, waveW, waveH));
            DrawMeter(g, new Rectangle(waveX + waveW + Pad, waveY, MeterW, waveH));
        }

        private void DrawName(Graphics g, int x, int h)
        {
            var f = Theme.Metric(11f);
            var sz = g.MeasureString(ChannelName, f);
            using (var br = new SolidBrush(Theme.TextSecondary))
                g.DrawString(ChannelName, f, br, x, (h - sz.Height) / 2f);
        }

        /// <summary>波形：零线居中，填充上下对称的轮廓。</summary>
        private void DrawWave(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(Theme.BgBase))
                g.FillRectangle(bg, r);
            using (var pen = new Pen(Color.FromArgb(60, Theme.Line), 1f))
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);

            float midY = r.Y + r.Height / 2f;

            // 零线
            using (var pen = new Pen(Color.FromArgb(70, Theme.Grid), 1f))
                g.DrawLine(pen, r.X + 1, midY, r.Right - 1, midY);

            var wave = _meter.Wave(_channel);
            int n = wave.Length;
            if (n < 2 || r.Width < 4) return;

            // 波形轮廓：逐像素列取该列覆盖样本的极值，画成上下包络。
            // 直接连线画样本点会在密集时糊成一团，包络更清晰。
            var top = new PointF[r.Width];
            var bot = new PointF[r.Width];
            float halfH = r.Height / 2f - 1;

            for (int px = 0; px < r.Width; px++)
            {
                int i0 = (int)((long)px * n / r.Width);
                int i1 = (int)((long)(px + 1) * n / r.Width);
                if (i1 <= i0) i1 = i0 + 1;
                if (i1 > n) i1 = n;

                double maxV = 0, minV = 0;
                for (int i = i0; i < i1; i++)
                {
                    double v = wave[i];
                    if (v > maxV) maxV = v;
                    if (v < minV) minV = v;
                }
                float x = r.X + px;
                top[px] = new PointF(x, midY - (float)(maxV * halfH));
                bot[px] = new PointF(x, midY - (float)(minV * halfH));
            }

            // 用强调色的半透明填充 + 亮边，与频谱图保持同一套视觉语言
            if (r.Width >= 2)
            {
                var poly = new PointF[r.Width * 2];
                Array.Copy(top, 0, poly, 0, r.Width);
                for (int i = 0; i < r.Width; i++) poly[r.Width + i] = bot[r.Width - 1 - i];

                using (var br = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                    g.FillPolygon(br, poly);
                using (var pen = new Pen(Color.FromArgb(220, Theme.Accent), 1f))
                {
                    g.DrawLines(pen, top);
                    g.DrawLines(pen, bot);
                }
            }
        }

        /// <summary>
        /// 电平表：竖向，两根粗柱。
        ///
        /// 设计取舍（前几版都栽在这里）：
        ///   - 柱是主角，要粗、要亮。早先柱只有 8px 宽，而刻度数字占了大半宽度，
        ///     控件里最有意义的部分反而最不显眼。
        ///   - 刻度不标数字，只留几条参考横线（0 / -6 / -18 / -36 dB）。
        ///     线性 dB 轴上标数字必然上密下疏，表一矮就叠成一团；
        ///     而"离 0dB 还有多少"看柱顶到哪条线就够了，不需要读具体数值——
        ///     具体数值由下方读数给出。
        ///   - 不居中：柱与读数都靠左贴紧，右侧不留大片空白。
        ///     居中在"内容比容器窄"时必然两边留空，而这里内容本来就窄。
        /// </summary>
        private void DrawMeter(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(Theme.BgBase))
                g.FillRectangle(bg, r);

            var fRead = Theme.Metric(8f);
            int readH = fRead.Height + 2;
            int barTop = r.Y + 1;
            int barBot = r.Bottom - readH - 2;
            int barH = Math.Max(8, barBot - barTop);

            // 两根柱贴左；右侧留给刻度数字。
            // 柱宽按"总宽 - 刻度数字 - 两侧余量"分配，因此表越宽柱越粗，
            // 不会出现"控件很大、柱子很细"的情况。
            var fTick = Theme.Metric(7.5f);
            int tickGap = 5;
            int tickW = 0;
            foreach (string t in new[] { "0", "-6", "-12", "-24" })
            {
                int w = (int)Math.Ceiling(g.MeasureString(t, fTick).Width);
                if (w > tickW) tickW = w;
            }
            const int barGap = 3;
            int barW = Math.Max(10, Math.Min(22, (r.Width - tickW - tickGap - 10 - barGap) / 2));
            int barsW = barW * 2 + barGap;
            int barX = r.X + 4;

            float MinToY(double db)
            {
                double t = (db - MeterFloorDb) / (0 - MeterFloorDb);
                if (t < 0) t = 0; if (t > 1) t = 1;
                return barBot - (float)(t * barH);
            }

            // 柱槽：暗底，暗示"可到多高"
            using (var slot = new SolidBrush(Color.FromArgb(30, Theme.Line)))
                g.FillRectangle(slot, barX, barTop, barsW, barH);

            double rmsDb = StereoMeter.ToDb(_meter.Rms(_channel));
            double pkDb = StereoMeter.ToDb(_meter.Peak(_channel));

            // ---- 刻度：参考横线 + 数字 ----
            //
            // 数字要保留——"离 0dB 还有多少"是能据此决策的信息。
            // 但线性 dB 轴位置上密下疏，表矮时 0/-6/-12 会叠在一起，
            // 因此按实际下沉空间自动挑选：自 0dB 向下扫描候选值，
            // 只有当它与上一个已放置标签的间距够一行字高时才放。
            // 这样表高表矮都不会重叠，放得下几个就显示几个。
            float tickH = fTick.Height;
            float lastY = float.MinValue;
            foreach (double t in new double[] { 0, -3, -6, -9, -12, -15, -18, -22, -26, -30, -36, -42, -48, -54, -60 })
            {
                float y = MinToY(t);
                if (y < barTop - 0.5f || y > barBot + 0.5f) continue;

                using (var pen = new Pen(Color.FromArgb(t == 0 ? 150 : 60, Theme.Line), 1f))
                    g.DrawLine(pen, barX, y, barX + barsW - 1, y);

                if (y < lastY + tickH + 2f) continue;      // 会重叠，跳过这个数字
                string s = t == 0 ? "0" : t.ToString("0");
                using (var br = new SolidBrush(Color.FromArgb(175, Theme.TextTertiary)))
                    g.DrawString(s, fTick, br, barX + barsW + tickGap, y - tickH / 2f);
                lastY = y;
            }

            DrawVerticalBar(g, barX, MinToY(rmsDb), barBot, barW, false);
            DrawVerticalBar(g, barX + barW + barGap, MinToY(pkDb), barBot, barW, true);

            // 历史峰值：横跨两根柱的一条亮线
            if (_meter.Hold(_channel) > 1e-4)
            {
                double holdDb = StereoMeter.ToDb(_meter.Hold(_channel));
                float hy = MinToY(holdDb);
                Color hc = _meter.Clipped(_channel) ? Theme.Danger
                         : (holdDb > -3 ? Theme.Warning : Theme.TextSecondary);
                using (var br = new SolidBrush(hc))
                    g.FillRectangle(br, barX - 1, hy - 1, barsW + 2, 2);
            }

            // ---- 读数：各自对齐到所属柱子的中心 ----
            //
            // 早先是左对齐排布（第一个数字从柱区左边缘起、第二个按第一个的宽度
            // 往后推），于是数字与柱子明显错位——用户指出"电平表和下面实际数字
            // 没对齐"。这里改为每个读数居中于它对应的那根柱子，
            // 宽度超出时以柱中心为基准钳位，保证不越界也不互相压。
            string sRms = _meter.Rms(_channel) > 1e-5 ? Fmt(rmsDb) : "--";
            string sPk = _meter.Peak(_channel) > 1e-5 ? Fmt(pkDb) : "--";
            var fReadout = fRead;
            var szA = g.MeasureString(sRms, fReadout);
            var szB = g.MeasureString(sPk, fReadout);

            float ax = barX + barW / 2f - szA.Width / 2f;
            float bx = barX + barW + barGap + barW / 2f - szB.Width / 2f;

            // 两个读数的最小间距：避免在窄控件里叠在一起
            float minGap = 3f;
            if (ax + szA.Width + minGap > bx)
            {
                float shift = (ax + szA.Width + minGap - bx) / 2f;
                ax -= shift; bx += shift;
            }
            // 不越出控件左右边界
            if (ax < r.X) ax = r.X;
            float maxB = r.Right - szB.Width;
            if (bx > maxB) bx = maxB;
            if (ax + szA.Width > bx - minGap) ax = Math.Max(r.X, bx - minGap - szA.Width);

            using (var br = new SolidBrush(Color.FromArgb(210, Theme.TextSecondary)))
            {
                g.DrawString(sRms, fReadout, br, ax, barBot + 2);
                g.DrawString(sPk, fReadout, br, bx, barBot + 2);
            }
        }

        /// <summary>
        /// dB 读数格式。取整——电平表上小数位没有意义（±0.1dB 听不出来），
        /// 却让每条读数宽出约 8px，窄控件里两个读数会挤成一片
        /// （实测 "-14.1-11.1" 连在一起，看不出是两个数）。
        /// </summary>
        private static string Fmt(double db)
        {
            if (db <= MeterFloorDbDefault + 0.01) return "-inf";
            return db.ToString("+0;-0;0");
        }

        private const double MeterFloorDbDefault = -60.0;

        /// <summary>一根竖向柱：从底部填充到给定 y。</summary>
        private static void DrawVerticalBar(Graphics g, int x, float topY, int bottomY, int w, bool isPeak)
        {
            if (bottomY <= topY) return;

            var rc = new RectangleF(x, topY, w, bottomY - topY);

            // 渐变按柱的满量程（barTop..bottomY 的上级）铺，而不是只铺已填充段：
            // 这样颜色位置不随电平高低漂移——低电平时时也是绿、高电平顶端才转橙，
            // 颜色本身就成了"离满量程还有多少"的提示。
            using (var lg = new LinearGradientBrush(
                new RectangleF(x, topY, w, Math.Max(1, bottomY - topY)),
                isPeak ? Theme.Accent : Theme.Success,
                isPeak ? Theme.Warning : Theme.Success,
                LinearGradientMode.Vertical))
            {
                // 位置 0 是矩形顶部（高电平端）：
                //   峰值柱  顶部橙 -> 中部青 -> 底部绿
                //   有效值柱 顶部青 -> 底部绿
                var blend = new ColorBlend(isPeak ? 3 : 2);
                if (isPeak)
                {
                    blend.Positions = new[] { 0f, 0.28f, 1f };
                    blend.Colors = new[] { Theme.Warning, Theme.Accent, Theme.Success };
                }
                else
                {
                    blend.Positions = new[] { 0f, 1f };
                    blend.Colors = new[] { Theme.Accent, Theme.Success };
                }
                lg.InterpolationColors = blend;
                g.FillRectangle(lg, rc);
            }
        }
    }
}
