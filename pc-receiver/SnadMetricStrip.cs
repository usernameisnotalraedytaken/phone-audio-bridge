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
            /// <summary>0..1 的电平条比例。负值表示不画条。</summary>
            public double Bar = -1;
            /// <summary>是否处于告警状态（整条转橙）。</summary>
            public bool Alert = false;
            /// <summary>悬浮说明，鼠标停留时显示。</summary>
            public string Tip = "";

            /// <summary>本项的电平条是"越高越糟"还是"越高越好"，影响条的颜色倾向。</summary>
            public bool HigherIsWorse = true;
        }

        private readonly Metric[] _items;
        private int _hover = -1;
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 400, ReshowDelay = 200 };

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

                // ---- 标签 ----
                float padX = 12;
                using (var br = new SolidBrush(Theme.TextTertiary))
                    g.DrawString(it.Label, fLabel, br, x + padX, 8);

                // ---- 数值（大号等宽） ----
                var szVal = g.MeasureString(it.Value, fValue);
                float valY = 20;
                using (var br = new SolidBrush(main))
                    g.DrawString(it.Value, fValue, br, x + padX, valY);

                // ---- 单位，紧贴数值右侧 ----
                if (!string.IsNullOrEmpty(it.Unit))
                {
                    float unitX = x + padX + szVal.Width - 2;
                    float unitY = valY + szVal.Height - fUnit.GetHeight(g) - 2;
                    using (var br = new SolidBrush(sub))
                        g.DrawString(it.Unit, fUnit, br, unitX, unitY);
                }

                // ---- 电平条 ----
                float barY = ClientSize.Height - 20;
                float barW = colW - padX * 2;
                if (it.Bar >= 0 && barW > 8)
                    DrawBar(g, x + padX, barY, barW, 6, it);
                else
                    using (var pen = new Pen(Color.FromArgb(90, Theme.Line), 1f))
                        g.DrawLine(pen, x + padX, barY + 3, x + padX + barW, barY + 3);
            }
        }

        /// <summary>
        /// 迷你电平条：分段而非连续条。
        /// 分段能让"差一点满"和"满了"一眼区分，连续条做不到。
        /// </summary>
        private void DrawBar(Graphics g, float x, float y, float w, float h, Metric it)
        {
            // 分段数可按宽度退化（窄到放不下 14 段时改为 1 段整条），因此不能用 const
            int segments = 14;
            float gap = 2f;
            float segW = (w - gap * (segments - 1)) / segments;
            if (segW < 1) { segments = 1; segW = w; gap = 0; }

            double v = it.Bar;
            if (v < 0) v = 0; if (v > 1) v = 1;
            int lit = (int)Math.Round(v * segments);

            Color onColor = it.Alert ? Theme.Warning : Theme.Accent;
            // 后半段渐变向暖色，制造"接近上限"的预警感
            for (int s = 0; s < segments; s++)
            {
                float sx = x + s * (segW + gap);
                bool on = s < lit;
                Color c;
                if (on)
                {
                    double t = (double)s / Math.Max(1, segments - 1);
                    c = it.HigherIsWorse
                        ? Theme.Mix(onColor, Theme.Warning, (float)(t * t * 0.85))
                        : Theme.Mix(onColor, Theme.Success, (float)(t * 0.5));
                }
                else
                {
                    c = Color.FromArgb(46, Theme.Line);
                }
                using (var br = new SolidBrush(c))
                    Theme.FillRound(g, br, new RectangleF(sx, y, segW, h), 1.5f);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = _items.Length;
            if (n == 0) return;
            int idx = (int)(e.X / ((float)ClientSize.Width / n));
            if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
            if (idx != _hover) { _hover = idx; Invalidate(); }

            string tip = _items[idx].Tip;
            _tip.SetToolTip(this, string.IsNullOrEmpty(tip) ? null : tip);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0) { _hover = -1; Invalidate(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
