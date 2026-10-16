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
        private const int MeterW = 92;
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
        /// 电平表：横向分段条。左侧是有效值填充，右侧刻度到 0dB；
        /// 峰值画成一条竖线，历史峰值画成一个小方块，超过 -0.5dB 转警示色。
        /// </summary>
        private void DrawMeter(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(Theme.BgBase))
                g.FillRectangle(bg, r);

            // 底部留出一行给数值读数。不留的话读数会画到控件外被裁掉
            // （与之前多处"写死尺寸导致内容被切"是同一类错误）。
            var fRead = Theme.Metric(8f);
            int readH = fRead.Height + 2;
            int barArea = Math.Max(10, r.Height - readH);

            // 两条柱 + 中间 2px 间隔，必须放得进 barArea。
            // 早先各自 Math.Max(6, ...) 会超出可用高度，柱状条压在读数上。
            int gap = 2;
            int barH = Math.Max(4, (barArea - gap) / 2);
            int rmsY = r.Y + 1;
            int pkY = rmsY + barH + gap;

            float MinToX(double db)
            {
                double t = (db - MeterFloorDb) / (0 - MeterFloorDb);
                if (t < 0) t = 0; if (t > 1) t = 1;
                return r.X + (float)(t * r.Width);
            }

            // 刻度：-60/-40/-20/-12/-6/-3/0，只画细线不写数字——
            // 通道条本身很窄，写数字会挤。精确数值在下方读数区给出。
            double[] ticks = { -40, -20, -12, -6, -3, 0 };
            foreach (double t in ticks)
            {
                float x = MinToX(t);
                using (var pen = new Pen(Color.FromArgb(t == 0 ? 120 : 45, Theme.Line), 1f))
                    g.DrawLine(pen, x, r.Y + 1, x, r.Bottom - 1);
            }

            // ---- 有效值 ----
            double rmsDb = StereoMeter.ToDb(_meter.Rms(_channel));
            float rmsX = MinToX(rmsDb);
            if (rmsX > r.X)
            {
                var rc = new RectangleF(r.X, rmsY, rmsX - r.X, barH);
                using (var lg = new LinearGradientBrush(
                    new RectangleF(r.X, rmsY, Math.Max(1, r.Width), barH),
                    Theme.Success, Theme.Accent, LinearGradientMode.Horizontal))
                    g.FillRectangle(lg, rc);
            }

            // ---- 峰值 ----
            double pkDb = StereoMeter.ToDb(_meter.Peak(_channel));
            float pkX = MinToX(pkDb);
            if (pkX > r.X)
            {
                // 超过 -3dB 转橙、超过 -0.5dB 转红，提示接近满量程
                Color pc = pkDb > -0.5 ? Theme.Danger : (pkDb > -3 ? Theme.Warning : Theme.TextSecondary);
                using (var br = new SolidBrush(pc))
                    g.FillRectangle(br, pkX - 2, pkY, 2, barH);
            }
            using (var pen = new Pen(Color.FromArgb(50, Theme.Line), 1f))
                g.DrawRectangle(pen, r.X, pkY, r.Width - 1, barH);

            // ---- 历史峰值：一次削波要能被看见，否则瞬态削波永远发现不了 ----
            double holdDb = StereoMeter.ToDb(_meter.Hold(_channel));
            float holdX = MinToX(holdDb);
            if (_meter.Hold(_channel) > 1e-4)
            {
                Color hc = _meter.Clipped(_channel) ? Theme.Danger
                         : (holdDb > -3 ? Theme.Warning : Theme.TextTertiary);
                using (var br = new SolidBrush(hc))
                    g.FillRectangle(br, holdX - 1, r.Y + 1, 2, r.Height - 2);
            }

            // ---- 数值读数 ----
            // RMS / 峰值 两值并列。只看条形看不出具体余量，数字才是能据此决策的信息。
            string s = _meter.Rms(_channel) > 1e-5
                ? rmsDb.ToString("0.0") + " / " + pkDb.ToString("0.0")
                : "--";
            using (var br = new SolidBrush(Color.FromArgb(210, Theme.TextTertiary)))
                g.DrawString(s, fRead, br, r.X, r.Bottom - readH + 1);
        }
    }
}
