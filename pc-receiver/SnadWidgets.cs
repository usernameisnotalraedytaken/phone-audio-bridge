// 插件风格的专用控件。
//
// WinForms 原生控件在这套界面里都不合用：
//   TrackBar  —— 系统画的滑槽与滑块，配色改不动，且带系统刻度点
//   CheckBox  —— 方框是系统位图，与自绘控件不是一套观感
//   NumericUpDown —— 系统微调按钮，浅色边框，在这套深色界面里非常突兀
//
// 这里全部自绘，形态沿用同一套语言：直角、等宽字、语义色、硬边高亮。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snad
{
    /// <summary>
    /// 竖向推子。用于均衡器逐段增益。
    ///
    /// 与系统 TrackBar 的区别：滑槽是一条细的直角凹槽，滑块是一条横向粗杆
    /// （而不是圆钮），顶部有一道中线刻度便于判断"是否动过"。
    /// 这是硬件调音台推子的形态，也是插件里最常见的做法。
    /// </summary>
    internal sealed class EqFader : Control
    {
        private int _value;              // 以 0.1dB 为单位，避免浮点
        private int _min = -120, _max = 120;
        private bool _hover, _dragging;

        /// <summary>值变化（单位 0.1dB）。</summary>
        public event EventHandler ValueChanged;

        public EqFader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Font = Theme.Metric(8.5f);
            Cursor = Cursors.Hand;
            Height = 110;
        }

        /// <summary>增益，单位 dB。</summary>
        public double GainDb
        {
            get { return _value / 10.0; }
            set
            {
                int v = (int)Math.Round(value * 10);
                if (v < _min) v = _min;
                if (v > _max) v = _max;
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void SetRange(double minDb, double maxDb)
        {
            _min = (int)Math.Round(minDb * 10);
            _max = (int)Math.Round(maxDb * 10);
            GainDb = GainDb;   // 重新钳位
        }

        private const int TrackW = 4;
        private const int ThumbH = 9;

        /// <summary>滑槽的可用区间（像素）。上下各留出半个滑块，滑块才不会越界。</summary>
        private void TrackBounds(out float top, out float bottom)
        {
            top = ThumbH / 2f + 1;
            bottom = ClientSize.Height - ThumbH / 2f - 1;
        }

        private float ValueToY(int v)
        {
            float top, bottom;
            TrackBounds(out top, out bottom);
            double t = (double)(v - _min) / Math.Max(1, _max - _min);
            return (float)(bottom - t * (bottom - top));
        }

        private int YToValue(int y)
        {
            float top, bottom;
            TrackBounds(out top, out bottom);
            double t = (bottom - y) / Math.Max(1f, bottom - top);
            if (t < 0) t = 0; if (t > 1) t = 1;
            return _min + (int)Math.Round(t * (_max - _min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            float cx = ClientSize.Width / 2f;
            float top, bottom;
            TrackBounds(out top, out bottom);

            // 滑槽：直角凹槽，两端略窄使其不像一根柱子
            using (var br = new SolidBrush(Theme.BgBase))
                g.FillRectangle(br, cx - TrackW / 2f, top, TrackW, bottom - top);
            using (var pen = new Pen(Color.FromArgb(110, Theme.Line), 1f))
                g.DrawRectangle(pen, cx - TrackW / 2f, top, TrackW, bottom - top);

            // 0dB 中线：判断"这一段是否动过"的基准
            float zeroY = ValueToY(0);
            using (var pen = new Pen(Color.FromArgb(120, Theme.GridMajor), 1f))
                g.DrawLine(pen, cx - 9, zeroY, cx + 9, zeroY);

            // 已提升部分用强调色实心填充（0dB 到当前值），一眼看出增减方向
            float vy = ValueToY(_value);
            if (Math.Abs(vy - zeroY) > 1f)
            {
                float y0 = Math.Min(vy, zeroY), y1 = Math.Max(vy, zeroY);
                Color fill = _value >= 0 ? Theme.Accent : Theme.Warning;
                using (var br = new SolidBrush(Color.FromArgb(120, fill)))
                    g.FillRectangle(br, cx - TrackW / 2f, y0, TrackW, y1 - y0);
            }

            // 滑块：一条横向粗杆（调音台推子的形态），比圆钮更利落
            var thumb = new RectangleF(cx - 11, vy - ThumbH / 2f, 22, ThumbH);
            bool active = _hover || _dragging;
            Color thumbFill = _dragging ? Theme.Accent
                            : active ? Theme.Scale(Theme.BgRaised, 1.35f)
                            : Theme.BgRaised;
            Theme.FillRoundGradient(g, thumb, 2f,
                Theme.Scale(thumbFill, 1.18f), Theme.Scale(thumbFill, 0.88f));
            using (var pen = new Pen(active ? Theme.Accent : Color.FromArgb(150, Theme.Line), 1f))
                Theme.DrawRound(g, pen, thumb, 2f);
            // 推子中央一道细线，是硬件推子的防滑纹，也强化"可拖"的暗示
            using (var pen = new Pen(Color.FromArgb(active ? 200 : 110,
                        active ? Theme.BgBase : Theme.TextTertiary), 1f))
                g.DrawLine(pen, cx - 7, vy, cx + 7, vy);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _dragging = true;
            Capture = true;
            GainDb = YToValue(e.Y) / 10.0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) GainDb = YToValue(e.Y) / 10.0;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            GainDb = GainDb + (e.Delta > 0 ? 0.5 : -0.5);
        }

        /// <summary>双击归零。</summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            GainDb = 0;
        }
    }

    /// <summary>
    /// 终端风格的复选块：显示为 [x] LABEL，方括号是字符而非绘制框。
    /// 与滚轮选择器的括号同源，整块语言因此连贯。
    /// </summary>
    internal sealed class FlatToggle : Control
    {
        private bool _checked;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public FlatToggle(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Cursor = Cursors.Hand;
            Font = Theme.Mono(9f);
            Height = 22;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Checked = !Checked; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            // 勾选态用强调色，未勾选用次级色：颜色承担状态语义，不靠方框样式区分
            Color fg = _checked ? Theme.Accent
                     : _hover ? Theme.TextPrimary
                     : Theme.TextTertiary;

            string box = _checked ? "[x]" : "[ ]";
            var f = Font;
            float y = (ClientSize.Height - f.Height) / 2f;
            using (var br = new SolidBrush(fg))
            {
                g.DrawString(box, f, br, 0, y);
                var sz = g.MeasureString(box, f);
                g.DrawString(Text, f, br, sz.Width + 6, y);
            }
        }
    }

    /// <summary>
    /// 数值步进器。替代 NumericUpDown——后者的系统微调按钮在这套界面里非常突兀。
    /// 形态为 [ -4.0 dB ▲▼ ]，整体是一个可点上下半区的框。
    /// </summary>
    internal sealed class FlatStepper : Control
    {
        private double _value;
        private double _min = -24, _max = 12, _step = 0.5;
        private int _decimals = 1;
        private string _unit = "dB";
        private int _hotZone;          // 0=无 1=上 2=下

        public event EventHandler ValueChanged;

        public FlatStepper()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Font = Theme.Metric(9f);
            Cursor = Cursors.Hand;
            Width = 92;
            Height = 24;
        }

        public double Value
        {
            get { return _value; }
            set
            {
                double v = Math.Max(_min, Math.Min(_max, value));
                v = Math.Round(v / _step) * _step;
                if (Math.Abs(v - _value) < 1e-9) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void Configure(double min, double max, double step, int decimals, string unit)
        {
            _min = min; _max = max; _step = step; _decimals = decimals; _unit = unit;
            Value = _value;
        }

        private Rectangle UpRect { get { return new Rectangle(Width - 18, 0, 18, Height / 2); } }
        private Rectangle DownRect { get { return new Rectangle(Width - 18, Height / 2, 18, Height - Height / 2); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRoundGradient(g, r, 3f, Theme.Scale(Theme.BgRaised, 1.12f), Theme.Scale(Theme.BgRaised, 0.95f));
            using (var pen = new Pen(Color.FromArgb(130, Theme.Line), 1f))
                Theme.DrawRound(g, pen, r, 3f);

            // 数值右对齐到按钮区左侧，等宽字体保证位数变化时不左右跳
            string text = _value.ToString("0." + new string('0', _decimals)) + " " + _unit;
            var sz = g.MeasureString(text, Font);
            using (var br = new SolidBrush(Theme.TextPrimary))
                g.DrawString(text, Font, br, Width - 18 - sz.Width - 6, (Height - sz.Height) / 2f);

            // 上下按钮区：细分隔线 + 三角
            using (var pen = new Pen(Color.FromArgb(110, Theme.Line), 1f))
            {
                g.DrawLine(pen, Width - 18, 0, Width - 18, Height);
                g.DrawLine(pen, Width - 18, Height / 2, Width, Height / 2);
            }
            DrawArrow(g, UpRect, true, _hotZone == 1);
            DrawArrow(g, DownRect, false, _hotZone == 2);
        }

        private void DrawArrow(Graphics g, Rectangle r, bool up, bool hot)
        {
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            Color c = hot ? Theme.Accent : Theme.TextSecondary;
            using (var br = new SolidBrush(c))
            {
                PointF[] pts = up
                    ? new[] { new PointF(cx, cy - 3), new PointF(cx + 3.5f, cy + 2), new PointF(cx - 3.5f, cy + 2) }
                    : new[] { new PointF(cx, cy + 3), new PointF(cx + 3.5f, cy - 2), new PointF(cx - 3.5f, cy - 2) };
                g.FillPolygon(br, pts);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int z = UpRect.Contains(e.Location) ? 1 : (DownRect.Contains(e.Location) ? 2 : 0);
            if (z != _hotZone) { _hotZone = z; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotZone != 0) { _hotZone = 0; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (UpRect.Contains(e.Location)) Value = _value + _step;
            else if (DownRect.Contains(e.Location)) Value = _value - _step;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = _value + (e.Delta > 0 ? _step : -_step);
        }
    }
}
