// 通道条读数面板：六项运行指标，横排。
//
// 参照混音台通道条的做法——每项是"标签 / 大数值 / 单位 / 电平条"的重复单元，
// 重复带来秩序感，也是专业工具的信息密度来源。
//
// 颜色承载语义：正常灰青，超阈值转橙。不做装饰性配色。
// 数值用等宽字体并固定宽度，跳变时不抖动。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class MetricStrip : Control
    {
        /// <summary>一项指标。字段全部给初值——它们由主窗口在运行时填入。</summary>
        internal sealed class Metric
        {
            public string Label = "";      // 如 "LATENCY"
            public string Value = "—";     // 如 "148.0"
            public string Unit = "";       // 如 "ms"
            /// <summary>
            /// 条的当前值，单位与 BarMax 一致。负值表示不画条。
            ///
            /// 语义是"读数本身"，不是"0..1 的比例"：比例由 BarMax 换算。
            /// 之前只传比例，调用方各自 Math.Min(1, x/sr) 把语义压平了——
            /// 于是 RX/TX 常年顶满、DROP 退化成"有没有"的布尔条。
            /// </summary>
            public double BarValue = -1;

            /// <summary>
            /// 条的量程上限。条按 BarValue/BarMax 取长度，但**不截断**：
            /// 超出量程的部分照画（画满并转警示色），这样"超出"本身可见。
            /// 之前一律 Math.Min(1, ...) 把超限信息丢掉了。
            /// </summary>
            public double BarMax = 1.0;

            /// <summary>
            /// 容差带（BarMax 的量纲）。条上会标出这一段区间。
            ///
            /// 用于"有个目标值"的指标：RX/TX 的目标是标称采样率，
            /// 落在带内即为正常。条从 0 起会有大半是常亮的，
            /// 于是改为把目标值画成中线、偏离量画成条——见 DrawBar 的说明。
            /// </summary>
            public double BarTolerance = 0;

            /// <summary>条的起点（默认 0）。设成非零可表达"低于此值不正常"。</summary>
            public double BarOrigin = 0;

            /// <summary>是否处于告警状态（整条转橙）。</summary>
            public bool Alert = false;

            /// <summary>
            /// 是否画成"偏离条"：以 BarOrigin 为中心，正向填充 BarValue-BarOrigin，
            /// 并在中心画一条刻度。用于 RX/TX 这类"目标是某个值"的指标。
            /// </summary>
            public bool DeviationBar = false;

            /// <summary>本项的电平条是"越高越糟"还是"越高越好"，影响条的颜色倾向。</summary>
            public bool HigherIsWorse = true;
        }

        private readonly Metric[] _items;
        private int _hover = -1;

        public MetricStrip(int count)
        {
            _items = new Metric[count];
            for (int i = 0; i < count; i++) _items[i] = new Metric();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            TabStop = false;
        }

        public Metric this[int i] { get { return _items[i]; } }
        public int Count { get { return _items.Length; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            int n = _items.Length;
            if (n == 0) return;

            float colW = (float)ClientSize.Width / n;
            // 标签用等宽（属于读数系统），数值用数显字体（视觉重心，要厚重）
            var fLabel = Theme.Label(7.5f);
            var fValue = Theme.Readout(20f);
            var fUnit = Theme.Metric(8.5f);

            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                float x = i * colW;
                var cell = new RectangleF(x, 0, colW, ClientSize.Height);

                // 分隔线：只在通道之间，两端不画
                if (i > 0)
                {
                    using (var pen = new Pen(Color.FromArgb(120, Theme.Line), 1f))
                        g.DrawLine(pen, x, 8, x, ClientSize.Height - 8);
                }

                // 悬停高亮
                if (i == _hover)
                {
                    using (var br = new SolidBrush(Color.FromArgb(28, Theme.BgHover)))
                        g.FillRectangle(br, cell);
                }

                var main = it.Alert ? Theme.Warning : Theme.TextPrimary;
                var sub = it.Alert ? Theme.Warning : Theme.TextTertiary;

                // ---- 纵向排布：三行各占一行，互不侵入 ----
                //
                //   第一行  标签 + 单位      LATENCY (ms)
                //   第二行  数值             125
                //   第三行  电平条           ▮▮▮▮▯▯▯
                //
                // 单位必须放在标签行，不能跟在数值右侧：数值宽度每帧都在变
                // （"125" 与 "48" 宽度不同），单位会跟着左右跳；而电平条
                // 从列左端铺到列右端，跟着数值走的单位必然被它压住
                // （用户实测：ms 被蓝色电平条盖住并在抖动）。
                // 单位固定在标题里就完全不动了。
                //
                // 行距按字体高度的比例给，不写死像素——144 DPI 下字体变大
                // 而固定边距不变，会把三行挤在一起（这是本控件第二次栽在这上面）。
                float padX = 12;
                // 用 Font.Height（实际行高）而不是 GetHeight(g)：
                // 后者含字体自身的多余行距，会白占几十像素；
                // 而且 PreferredHeight 也必须用同一个量，否则两者对不上。
                float fLabelH = fLabel.Height;
                float fValueH = fValue.Height;
                const float barH = 6;

                // 内容总高固定，剩余空间按比例分摊到各段之间。
                //
                // 之前是"标签贴顶、柱子贴底、空隙全留在中间"：
                // PreferredHeight 加了 14% 余量，那些余量全变成数值与柱子之间的
                // 一大块空白（用户圈出来指出）。三行内容本该是个紧凑的整体，
                // 多余的空间要摊开，不是堆在一处。
                float gapLabel = fLabelH * 0.30f;     // 标签与数值之间
                float gapBar = Math.Max(8f, fValueH * 0.26f);   // 数值与电平条之间
                float padTop = fLabelH * 0.55f;
                float padBottom = Math.Max(5f, fLabelH * 0.30f);

                float contentH = fLabelH + gapLabel + fValueH + gapBar + barH
                               + padTop + padBottom;
                float slack = ClientSize.Height - contentH;

                // 余量按各段比例摊开；空间不足时不摊（slack < 0）
                padTop += slack * 0.35f;
                gapLabel += slack * 0.25f;
                gapBar += slack * 0.25f;
                padBottom += slack * 0.15f;

                float labelY = padTop;
                float valY = labelY + fLabelH + gapLabel;
                float barY = ClientSize.Height - padBottom - barH;

                // 空间不足时压缩数值上方的空隙，而不是让三行重叠
                if (valY + fValueH > barY - gapBar)
                    valY = Math.Max(labelY + fLabelH, barY - gapBar - fValueH);

                // ---- 第一行：标签 + 单位 ----
                using (var br = new SolidBrush(Theme.TextTertiary))
                {
                    g.DrawString(it.Label, fLabel, br, x + padX, labelY);
                    if (!string.IsNullOrEmpty(it.Unit))
                    {
                        var szLab = g.MeasureString(it.Label, fLabel);
                        g.DrawString("(" + it.Unit + ")", fLabel, br,
                                     x + padX + szLab.Width + 3, labelY);
                    }
                }

                // ---- 第二行：数值 ----
                using (var br = new SolidBrush(main))
                    g.DrawString(it.Value, fValue, br, x + padX, valY);

                // ---- 第三行：电平条 ----
                float barW = colW - padX * 2;
                if (it.BarValue >= 0 && barW > 8)
                    DrawBar(g, x + padX, barY, barW, barH, it);
                else
                    using (var pen = new Pen(Color.FromArgb(90, Theme.Line), 1f))
                        g.DrawLine(pen, x + padX, barY + barH / 2, x + padX + barW, barY + barH / 2);
            }
        }

        /// <summary>
        /// 面板绘制一格所需的最小高度。
        /// 主界面据此分配指标条高度，避免外部写死一个"看着差不多"的数
        /// （写死 76 就压上了）。
        /// </summary>
        public int PreferredHeight
        {
            get
            {
                // Theme 的字体在创建时已按 DPI 构建，Height 就是实际行高，
                // 不能再乘 DeviceDpi/96 —— 那是放大两次，
                // 会让"算出来的高度"大于"画出来的高度"（与绘制端对不上）。
                float fLabelH = Theme.Label(7.5f).Height;
                float fValueH = Theme.Readout(20f).Height;

                // 与实际绘制用同一组公式，避免"算出来的高度"与"画出来的高度"不一致。
                //
                // 这里**不再额外乘系数**。之前加了 "×1.14 + 6" 想让它别太挤，
                // 结果高度从 66 涨到 90，多出来的部分全变成数值与柱子之间的
                // 一大块空白（用户圈出来指出）。留白该由各段间距本身给足，
                // 而不是事后放大总高——放大总高只会把空隙堆在某两行之间。
                float need = fLabelH * 0.55f      // 顶部留白
                           + fLabelH
                           + fLabelH * 0.30f      // 标签与数值
                           + fValueH
                           + Math.Max(8f, fValueH * 0.26f)   // 数值与柱子
                           + 6                    // 柱高
                           + Math.Max(5f, fLabelH * 0.30f);  // 底部留白

                return (int)Math.Ceiling(need) + 2;
            }
        }

        /// <summary>
        /// 指标条。分段显示——分段能让"差一点满"和"满了"一眼区分，连续条做不到。
        ///
        /// 两种形态：
        ///   普通条  从 BarOrigin 起按 (BarValue-BarOrigin)/BarMax 填充。
        ///           超出量程不截断，改在最右端加一道警示竖线，
        ///           这样"超限"本身可见（早先一律 Math.Min(1,...) 把它丢掉了）。
        ///   偏离条  以 BarOrigin 为中线，向两侧表示偏离方向与幅度，
        ///           并画出容差带。用于 RX/TX 这类"目标是某个值"的指标——
        ///           它们的比例恒 ≥1，画成普通条会永远顶满、毫无信息。
        /// </summary>
        private void DrawBar(Graphics g, float x, float y, float w, float h, Metric it)
        {
            // 分段数可按宽度退化（窄到放不下 14 段时改为 1 段整条），因此不能用 const
            int segments = 14;
            float gap = 2f;
            float segW = (w - gap * (segments - 1)) / segments;
            if (segW < 1) { segments = 1; segW = w; gap = 0; }

            double max = it.BarMax > 0 ? it.BarMax : 1.0;

            Color onColor = it.Alert ? Theme.Warning : Theme.Accent;

            // 偏离条要表达"偏向哪边"，因此从中线向两侧填（左负右正），
            // 而不是像普通条那样从最左端填过来——后者两边都亮，
            // 看不出是高了还是低了。
            if (it.DeviationBar)
            {
                DrawDeviationBar(g, x, y, w, h, it, max, onColor, segments, segW, gap);
                return;
            }

            // 普通条：从 BarOrigin 起按 (BarValue-BarOrigin)/BarMax 填充。
            // 比例不截断成 1，超出量程单独标记（见下），否则"超限"信息会丢。
            double v = (it.BarValue - it.BarOrigin) / max;
            bool over = v > 1.0;
            if (v < 0) v = 0;
            if (v > 1.0) v = 1.0;
            int lit = (int)Math.Round(v * segments);

            for (int s = 0; s < segments; s++)
            {
                bool on = s < lit;
                Color c;
                if (on)
                {
                    double t = (double)s / Math.Max(1, segments - 1);
                    c = it.HigherIsWorse
                        ? Theme.Mix(onColor, Theme.Warning, (float)(t * t * 0.85))
                        : Theme.Mix(onColor, Theme.Success, (float)(t * 0.5));
                }
                else c = Theme.BgRaised;
                using (var br = new SolidBrush(c))
                    g.FillRectangle(br, x + s * (segW + gap), y, segW, h);
            }

            // 超出量程：在最右端加一道警示竖线，让"超了"这件事看得见
            if (over)
                using (var br = new SolidBrush(Theme.Danger))
                    g.FillRectangle(br, x + w - 2, y - 1, 2, h + 2);
        }

        /// <summary>
        /// 偏离条：以中线的 BarOrigin 为基准，向两侧表示偏离的方向与幅度。
        ///
        /// 用于 RX/TX 这类"目标是某个值"的指标。它们的实际值与目标之比
        /// 恒 ≥1，画成普通条会永远顶满、看不出任何差异（用户实测指出）。
        /// 中线画一条刻度、容差带用淡绿底标出，落在带内即正常。
        /// </summary>
        private void DrawDeviationBar(Graphics g, float x, float y, float w, float h,
                                      Metric it, double max, Color onColor,
                                      int segments, float segW, float gap)
        {
            double d = it.BarValue - it.BarOrigin;
            // 占半幅的比例；超出量程即为 ±1，另用警示色标记方向端点
            double frac = d / max;
            bool over = Math.Abs(frac) > 1.0;
            if (frac > 1.0) frac = 1.0;
            if (frac < -1.0) frac = -1.0;

            // 分段索引：中间两段之间就是中线
            int mid = segments / 2;
            int litCount = (int)Math.Round(Math.Abs(frac) * mid);

            for (int s = 0; s < segments; s++)
            {
                // s 段相对中线的偏移：左半为负、右半为正
                bool on;
                if (frac >= 0) on = s >= mid && s < mid + litCount;
                else on = s < mid && s >= mid - litCount;

                Color c = on ? onColor : Theme.BgRaised;
                if (on && over && (frac > 0 ? s == segments - 1 : s == 0))
                    c = Theme.Danger;      // 超出量程的那一端转警示色

                using (var br = new SolidBrush(c))
                    g.FillRectangle(br, x + s * (segW + gap), y, segW, h);
            }

            // 容差带：淡绿底，横跨中线两侧
            if (it.BarTolerance > 0)
            {
                double tw = 0.5 * (it.BarTolerance / max) * w;
                using (var br = new SolidBrush(Color.FromArgb(55, Theme.Success)))
                    g.FillRectangle(br, x + w / 2f - (float)tw, y - 1, (float)(tw * 2), h + 2);
            }

            // 中线刻度
            using (var pen = new Pen(Color.FromArgb(200, Theme.TextSecondary), 1f))
                g.DrawLine(pen, x + w / 2f, y - 2, x + w / 2f, y + h + 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = _items.Length;
            if (n == 0) return;
            int idx = (int)(e.X / ((float)ClientSize.Width / n));
            if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
            if (idx != _hover) { _hover = idx; Invalidate(); }
            // 不显示悬停提示：弹出的浮层会盖住下方内容，而这里的说明
            // 属于"看一次就够"的信息，放在代码注释里更合适。
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0) { _hover = -1; Invalidate(); }
        }

        protected override void Dispose(bool disposing)
        {
            // 已无 ToolTip 需要释放
            base.Dispose(disposing);
        }
    }
}
