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
        private const int MeterW = 96;
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
        /// 电平表：竖向。
        ///
        /// 竖着放是音频工具的行业惯例（调音台、DAW、硬件表头都如此），
        /// 两个原因：
        ///   1. 纵向空间在通道条里本来就是富余的，横表却要占掉一整行高度，
        ///      把波形的纵向空间压扁——竖表把这个矛盾解开了
        ///   2. 竖表的高度直接对应动态范围，一眼能看出"离满量程还有多少"，
        ///      横表要读刻度才知道
        ///
        /// 两根柱并排：左=有效值(RMS)，右=峰值(peak)。
        /// 峰值柱上再叠一条历史峰值刻线，一次削波不会被漏掉。
        /// </summary>
        private void DrawMeter(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(Theme.BgBase))
                g.FillRectangle(bg, r);

            var fRead = Theme.Metric(8f);
            var fTick = Theme.Metric(7f);
            int readH = fRead.Height + 2;
            int tickW = 22;                      // 右侧刻度数字
            int barTop = r.Y + 2;
            int barBot = r.Bottom - readH - 2;   // 底部留给数值
            int barH = Math.Max(8, barBot - barTop);

            // 两根柱：各 8px，间隔 3px，整体居中；刻度数字放右侧
            const int barW = 8, barGap = 3;
            int barsW = barW * 2 + barGap;
            int barX = r.X + Math.Max(0, (r.Width - tickW - barsW) / 2);

            float MinToY(double db)
            {
                double t = (db - MeterFloorDb) / (0 - MeterFloorDb);
                if (t < 0) t = 0; if (t > 1) t = 1;
                return barBot - (float)(t * barH);
            }

            // ---- 刻度：横线 + 关键值数字 ----
            //
            // 标签按"每隔固定像素"放置，而不是每隔固定 dB：
            // 线性 dB 轴上 0/-6/-12/-24/-48/-60 在顶部挤成一团
            // （dB 刻度本来就是上密下疏），固定像素间距才均匀可读。
            // 数值取该位置对应的 dB，取整到常用档位。
            double[] ticks = { -60, -48, -36, -24, -12, -6, -3, 0 };
            foreach (double t in ticks)
            {
                float y = MinToY(t);
                using (var pen = new Pen(Color.FromArgb(40, Theme.Line), 1f))
                    g.DrawLine(pen, barX - 3, y, barX + barsW + 3, y);
            }

            double[] labelDb = { 0, -6, -12.5, -20, -30, -42, -54 };
            foreach (double t in labelDb)
            {
                float y = MinToY(t);
                string s = t == 0 ? "0" : t.ToString("0");
                var sz = g.MeasureString(s, fTick);
                using (var br = new SolidBrush(Color.FromArgb(165, Theme.TextTertiary)))
                    g.DrawString(s, fTick, br, barX + barsW + 5, y - sz.Height / 2f);
                using (var pen = new Pen(Color.FromArgb(70, Theme.Line), 1f))
                    g.DrawLine(pen, barX + barsW + 1, y, barX + barsW + 4, y);
            }

            // ---- 柱底与边框 ----
            using (var pen = new Pen(Color.FromArgb(70, Theme.Line), 1f))
                g.DrawRectangle(pen, barX, barTop, barsW, barH);

            double rmsDb = StereoMeter.ToDb(_meter.Rms(_channel));
            double pkDb = StereoMeter.ToDb(_meter.Peak(_channel));

            // 有效值柱（左）：绿->青渐变，表示"平均响度"
            DrawVerticalBar(g, barX, MinToY(rmsDb), barBot, barW, false);
            // 峰值柱（右）：接近满量程转橙/红
            DrawVerticalBar(g, barX + barW + barGap, MinToY(pkDb), barBot, barW, true);

            // ---- 历史峰值刻线：横跨两根柱，一次削波要能被看见 ----
            if (_meter.Hold(_channel) > 1e-4)
            {
                double holdDb = StereoMeter.ToDb(_meter.Hold(_channel));
                float hy = MinToY(holdDb);
                Color hc = _meter.Clipped(_channel) ? Theme.Danger
                         : (holdDb > -3 ? Theme.Warning : Theme.TextSecondary);
                using (var br = new SolidBrush(hc))
                    g.FillRectangle(br, barX - 2, hy - 1, barsW + 4, 2);
            }

            // ---- 数值读数：两根柱各标一个，避免"两个数字不知道谁是谁" ----
            // 早先是 "RMS / PK" 并排写在柱下方，没有标签，读的人只能猜
            // （用户反馈"看不懂"）。
            string sRms = _meter.Rms(_channel) > 1e-5 ? Fmt(rmsDb) : "--";
            string sPk = _meter.Peak(_channel) > 1e-5 ? Fmt(pkDb) : "--";
            var szRms = g.MeasureString(sRms, fRead);
            var szPk = g.MeasureString(sPk, fRead);
            // 两条读数在柱的区域左右分开摆放；总宽超出可用宽度时整体左移，
            // 保证不越过控件右边界被裁（实测按固定偏移会切掉末位数字）。
            float readW = szRms.Width + 6 + szPk.Width;
            float readX = barX + (barsW - readW) / 2f;
            if (readX < r.X) readX = r.X;
            float maxX = r.Right - readW;
            if (readX > maxX) readX = maxX == r.X ? r.X : maxX;
            using (var br = new SolidBrush(Color.FromArgb(200, Theme.TextTertiary)))
            {
                g.DrawString(sRms, fRead, br, readX, barBot + 2);
                g.DrawString(sPk, fRead, br, readX + szRms.Width + 6, barBot + 2);
            }
        }

        /// <summary>dB 读数统一格式：带一位小数，正数带 + 号，一眼能看出是增是减。</summary>
        private static string Fmt(double db)
        {
            if (db <= MeterFloorDbDefault + 0.01) return "-inf";
            return db.ToString("+0.0;-0.0;0.0");
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
