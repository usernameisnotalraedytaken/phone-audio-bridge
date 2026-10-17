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

        /// <summary>
        /// 量程铺满自身高度的模式。
        /// 与 SetRange 的区别只是语义更明确：把整段量程映射到控件全高，
        /// 0dB 落在几何中心。对齐外部刻度时用它会比手算参数清楚。
        /// </summary>
        public void SetFullRange(double minDb, double maxDb)
        {
            _posZeroY = float.NaN;     // 关掉位置映射，回到铺满模式
            SetRange(minDb, maxDb);
            Invalidate();
        }

        /// <summary>
        /// 按"控件内的绝对位置"设置刻度映射，而不是按数值范围铺满。
        ///
        /// 用途：前置增益推子要和曲线图的 0dB 虚线严格对齐。
        /// 曲线图的纵轴是 ±18dB 且 0dB 未必在正中（上下留白不同），
        /// 若让推子把自己的量程铺满整高，两者就永远对不齐。
        /// 这里直接指定"0dB 对应控件内哪个 y、整高对应多少 dB"，位置因此精确。
        /// </summary>
        public void SetPositionalScale(float zeroAtY, float height, float dbPerHeight)
        {
            _posZeroY = zeroAtY;
            _posHeight = height;
            _posDbPerHeight = dbPerHeight;
            Invalidate();
        }

        private float _posZeroY = float.NaN;
        private float _posHeight;
        private float _posDbPerHeight;

        private const int TrackW = 4;
        private const int ThumbH = 9;

        /// <summary>滑槽的可用区间（像素）。上下各留出半个滑块，滑块才不会越界。</summary>
        private void TrackBounds(out float top, out float bottom)
        {
            top = ThumbH / 2f + 1;
            bottom = ClientSize.Height - ThumbH / 2f - 1;
        }

        /// <summary>是否使用"按控件内位置"的映射（见 SetPositionalScale）。</summary>
        private bool Positional { get { return !float.IsNaN(_posZeroY) && _posDbPerHeight > 0; } }

        private float ValueToY(int v)
        {
            if (Positional)
                return (float)(_posZeroY - (v / 10.0) * _posDbPerHeight);

            float top, bottom;
            TrackBounds(out top, out bottom);
            double t = (double)(v - _min) / Math.Max(1, _max - _min);
            return (float)(bottom - t * (bottom - top));
        }

        private int YToValue(int y)
        {
            if (Positional)
            {
                double db = (_posZeroY - y) / _posDbPerHeight;
                return (int)Math.Round(Math.Max(_min / 10.0, Math.Min(_max / 10.0, db)) * 10);
            }

            float top, bottom;
            TrackBounds(out top, out bottom);
            double t = (bottom - y) / Math.Max(1f, bottom - top);
            if (t < 0) t = 0; if (t > 1) t = 1;
            return _min + (int)Math.Round(t * (_max - _min));
        }

        /// <summary>按映射算出 0dB 在控件内的 y。绘制中线与拖动范围都以此为准。</summary>
        private float ZeroY { get { return ValueToY(0); } }

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

        /// <summary>
        /// 拖动改用"相对位移"，而不是把值直接映射到鼠标的绝对位置。
        ///
        /// 两处手感问题都出在原来的写法：
        ///   1) 按下即 GainDb = YToValue(e.Y) —— 点哪儿值就跳到哪儿，
        ///      想微调反而先来一次大跳。
        ///   2) 每像素直接换算 dB —— 鼠标抖 1px 就改 0.24dB，过于灵敏。
        ///
        /// 现在：按下只记录起点，值等鼠标离开起点超过阈值才跟手，
        /// 移动量按"起点值 + 像素位移 × 每像素 dB"计算，手感线性可控。
        /// </summary>
        private const int DragThresholdPx = 3;
        private int _dragStartY;
        private int _dragStartValue;
        private bool _dragArmed;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _dragging = true;
            _dragArmed = false;
            _dragStartY = e.Y;
            _dragStartValue = _value;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;

            _fineMode = (ModifierKeys & Keys.Shift) == Keys.Shift;

            int dy = e.Y - _dragStartY;
            if (!_dragArmed)
            {
                // 起拖阈值：不设的话单击也会改值，等于"点哪跳哪"
                if (Math.Abs(dy) < DragThresholdPx) return;
                _dragArmed = true;
            }

            // 向上拖动 = 增大，与推子的物理直觉一致
            int delta = ValueDeltaForPixels(-dy);
            _value = ClampValue(_dragStartValue + delta);
            Invalidate();
            RaiseValueChanged();
        }

        /// <summary>滚轮也走同一套灵敏度：一格 0.5dB，Shift 时 0.1dB。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            bool fine = (ModifierKeys & Keys.Shift) == Keys.Shift;
            GainDb = GainDb + (e.Delta > 0 ? (fine ? 0.1 : 0.5) : (fine ? -0.1 : -0.5));
        }

        /// <summary>
        /// 像素位移对应的值增量（以 0.1dB 为单位）。
        ///
        /// 手感基准：**拖满控件高度 = 走完整个量程**。
        /// 这样"鼠标移动多少像素、推子就移动多少像素"，符合直觉。
        ///
        /// 早先是按刻度映射反推（pixels × dbPerHeight），那样灵敏度取决于
        /// 量程与高度的比值：推子 286px 覆盖 ±18dB 时每像素仅 0.13dB，
        /// 鼠标移动 7px 就改 1dB，微调时几乎无法停住（用户反馈"太灵敏"）。
        /// </summary>
        private int ValueDeltaForPixels(int pixels)
        {
            float top, bottom;
            TrackBounds(out top, out bottom);
            double perPx = (double)(_max - _min) / Math.Max(1f, bottom - top);
            double d = pixels * perPx;
            if (_fineMode) d *= 0.2;      // 按住 Shift 精调
            return (int)Math.Round(d);
        }

        /// <summary>按住 Shift 进入精调（灵敏度降到 1/5）。</summary>
        private bool _fineMode;

        private int ClampValue(int v)
        {
            if (v < _min) return _min;
            if (v > _max) return _max;
            return v;
        }

        private void RaiseValueChanged()
        {
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            _dragArmed = false;
            Capture = false;
            Invalidate();
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

        /// <summary>
        /// 按内容宽度撑开。复选块是 "[x] " + 文本，两部分都要算进去。
        ///
        /// 之前由调用方写死宽度（如 Width = 90），而 "EQ ON" 在等宽 9pt 下
        /// 连同 "[x] " 需要更宽，于是显示成 "EQ O"——末字符被切掉。
        /// 让控件自己量，调用方就不必猜。凡是"文字 + 固定前缀"的控件都该这样。
        /// </summary>
        public void FitToContent()
        {
            using (var g = CreateGraphics())
            {
                string box = "[x] ";
                float w = g.MeasureString(box, Font).Width + g.MeasureString(Text, Font).Width;
                Width = (int)Math.Ceiling(w) + 8;
            }
        }

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

        // 只读访问器：供界面自渲染检查按语义挑控件并验证取值，
        // 也便于外部把控件当前状态回报到日志。
        public double Min { get { return _min; } }
        public double Max { get { return _max; } }
        public double Step { get { return _step; } }
        public int Decimals { get { return _decimals; } }
        public string Unit { get { return _unit; } }

        private Rectangle UpRect { get { return new Rectangle(Width - 18, 0, 18, Height / 2); } }
        private Rectangle DownRect { get { return new Rectangle(Width - 18, Height / 2, 18, Height - Height / 2); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            // 禁用态整体压暗：这类控件是自绘的，不压暗的话
            // 用户看不出"这个值当前不生效"，会以为调了有用
            bool on = Enabled;
            Theme.FillRoundGradient(g, r, 3f,
                Theme.Scale(Theme.BgRaised, on ? 1.12f : 0.80f),
                Theme.Scale(Theme.BgRaised, on ? 0.95f : 0.70f));
            using (var pen = new Pen(Color.FromArgb(on ? 130 : 60, Theme.Line), 1f))
                Theme.DrawRound(g, pen, r, 3f);

            // 数值右对齐到按钮区左侧，等宽字体保证位数变化时不左右跳
            string text = _value.ToString("0." + new string('0', _decimals)) + " " + _unit;
            var sz = g.MeasureString(text, Font);
            using (var br = new SolidBrush(on ? Theme.TextPrimary : Theme.TextTertiary))
                g.DrawString(text, Font, br, Width - 18 - sz.Width - 6, (Height - sz.Height) / 2f);

            // 上下按钮区：细分隔线 + 三角
            using (var pen = new Pen(Color.FromArgb(on ? 110 : 55, Theme.Line), 1f))
            {
                g.DrawLine(pen, Width - 18, 0, Width - 18, Height);
                g.DrawLine(pen, Width - 18, Height / 2, Width, Height / 2);
            }
            DrawArrow(g, UpRect, true, on && _hotZone == 1);
            DrawArrow(g, DownRect, false, on && _hotZone == 2);
        }

        private void DrawArrow(Graphics g, Rectangle r, bool up, bool hot)
        {
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            Color c = !Enabled ? Theme.TextTertiary : (hot ? Theme.Accent : Theme.TextSecondary);
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

    /// <summary>
    /// 下拉选择器：显示当前值，点击弹出一个列表窗口。
    ///
    /// 为什么用独立小窗口而不是就地展开：
    ///   WheelSelector 那种"就地改变 Height 展开"的做法在密集表单里会压住
    ///   相邻行，且展开高度受父容器裁剪。独立弹窗不受父容器裁剪，
    ///   也不会打乱布局——标准下拉本来就是这么做的。
    ///
    /// 选项由外部给出，因此可以承载"设备能力决定的可选项"：
    /// 只把设备真正支持的组合放进 Items，用户就选不出跑不起来的配置。
    /// </summary>
    internal sealed class FlatDropdown : Control
    {
        private string[] _items = new string[0];
        private int _index;

        public event EventHandler SelectionChanged;

        public FlatDropdown()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgRaised;
            Font = Theme.Metric(8.5f);
            Cursor = Cursors.Hand;
            Width = 180;
            Height = 24;
        }

        public string[] Items
        {
            get { return _items; }
            set
            {
                _items = value ?? new string[0];
                if (_index >= _items.Length) _index = Math.Max(0, _items.Length - 1);
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v >= _items.Length) v = Math.Max(0, _items.Length - 1);
                if (v == _index) return;
                _index = v;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem
        {
            get { return _index >= 0 && _index < _items.Length ? _items[_index] : ""; }
        }

        /// <summary>按内容选中。找不到时保持原样并返回 false。</summary>
        public bool SelectText(string text)
        {
            for (int i = 0; i < _items.Length; i++)
                if (string.Equals(_items[i], text, StringComparison.Ordinal)) { SelectedIndex = i; return true; }
            return false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            bool on = Enabled;
            bool active = on && (_hover || _open);
            using (var bg = new SolidBrush(active ? Theme.BgActive
                                        : on ? BackColor : Theme.Scale(BackColor, 0.82f)))
                g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(active ? 150 : on ? 90 : 55, Theme.Line), 1f))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            // 右侧箭头
            int aw = 10;
            float cx = Width - aw - 6, cy = Height / 2f;
            using (var pen = new Pen(!on ? Theme.TextTertiary
                                    : active ? Theme.Accent : Theme.TextTertiary, 1.6f))
            {
                g.DrawLine(pen, cx - 3, cy - 1.5f, cx, cy + 1.5f);
                g.DrawLine(pen, cx, cy + 1.5f, cx + 3, cy - 1.5f);
            }

            // 文本：超出时截断并加省略号——宁可显示不全也不越界压住箭头
            string text = SelectedItem;
            float maxW = Width - aw - 14;
            var sz = g.MeasureString(text, Font);
            if (sz.Width > maxW)
            {
                string ell = "…";
                float ellW = g.MeasureString(ell, Font).Width;
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (g.MeasureString(text.Substring(0, mid), Font).Width + ellW <= maxW) lo = mid;
                    else hi = mid - 1;
                }
                text = lo > 0 ? text.Substring(0, lo) + ell : ell;
                sz = g.MeasureString(text, Font);
            }
            using (var br = new SolidBrush(!on || _items.Length == 0 ? Theme.TextTertiary : Theme.TextPrimary))
                g.DrawString(text, Font, br, 6, (Height - sz.Height) / 2f);
        }

        private bool _hover, _open;
        private Form _popup;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_items.Length == 0) return;
            ShowPopup();
        }

        private void ShowPopup()
        {
            if (_popup != null) { ClosePopup(); return; }

            _open = true;
            Invalidate();

            var popup = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = Theme.BgPanel,
                MinimumSize = Size.Empty
            };

            var f = Font;
            int rowH = Math.Max(22, (int)Math.Ceiling(f.Height * 1.6f));
            int rows = Math.Min(_items.Length, 12);
            int w = Math.Max(Width, 160);
            // 宽度按最长项撑开，但不超过 520——设备名可能很长
            using (var g = CreateGraphics())
            {
                foreach (var it in _items)
                {
                    int need = (int)Math.Ceiling(g.MeasureString(it, f).Width) + 20;
                    if (need > w) w = Math.Min(520, need);
                }
            }
            popup.ClientSize = new Size(w, rows * rowH + 2);

            var list = new DropList(this, rowH) { Dock = DockStyle.Fill };
            popup.Controls.Add(list);

            // 定位到本控件下方；下方空间不足时改到上方
            var screen = PointToScreen(new Point(0, Height));
            var wa = Screen.FromControl(this).WorkingArea;
            int y = screen.Y;
            if (y + popup.Height > wa.Bottom) y = PointToScreen(Point.Empty).Y - popup.Height;
            popup.Location = new Point(screen.X, y);

            popup.Deactivate += (s, e) => ClosePopup();
            popup.FormClosed += (s, e) => { _open = false; _popup = null; Invalidate(); };
            _popup = popup;
            popup.Show(this);
            popup.BringToFront();
        }

        private void ClosePopup()
        {
            if (_popup == null) return;
            var p = _popup;
            _popup = null;
            _open = false;
            try { p.Close(); } catch { }
            Invalidate();
        }

        /// <summary>弹窗内的列表：点选即生效并关闭。键盘上下键也能选。</summary>
        private sealed class DropList : Control
        {
            private readonly FlatDropdown _owner;
            private readonly int _rowH;
            private int _hot = -1;

            public DropList(FlatDropdown owner, int rowH)
            {
                _owner = owner;
                _rowH = rowH;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.BgPanel;
                TabStop = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Theme.SetupQuality(g);
                using (var bg = new SolidBrush(Theme.BgPanel)) g.FillRectangle(bg, ClientRectangle);
                using (var pen = new Pen(Theme.Line, 1f))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

                var f = _owner.Font;
                for (int i = 0; i < _owner._items.Length; i++)
                {
                    int y = 1 + i * _rowH;
                    if (y > Height) break;
                    bool sel = i == _owner._index;
                    bool hot = i == _hot;
                    if (sel || hot)
                    {
                        using (var br = new SolidBrush(sel ? Theme.BgActive : Theme.BgHover))
                            g.FillRectangle(br, 1, y, Width - 2, _rowH);
                    }
                    if (sel)
                    {
                        using (var br = new SolidBrush(Theme.Accent))
                            g.FillRectangle(br, 1, y, 2, _rowH);
                    }
                    using (var br = new SolidBrush(sel ? Theme.TextPrimary : Theme.TextSecondary))
                        g.DrawString(_owner._items[i], f, br, 10, y + (_rowH - f.Height) / 2f);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int idx = (e.Y - 1) / Math.Max(1, _rowH);
                if (idx < 0 || idx >= _owner._items.Length) idx = -1;
                if (idx != _hot) { _hot = idx; Invalidate(); }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                int idx = (e.Y - 1) / Math.Max(1, _rowH);
                if (idx >= 0 && idx < _owner._items.Length)
                {
                    _owner.SelectedIndex = idx;
                    _owner.ClosePopup();
                }
                else _owner.ClosePopup();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                if ((keyData & Keys.KeyCode) == Keys.Up || (keyData & Keys.KeyCode) == Keys.Down ||
                    (keyData & Keys.KeyCode) == Keys.Enter || (keyData & Keys.KeyCode) == Keys.Escape)
                    return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Escape) { _owner.ClosePopup(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Enter)
                {
                    if (_hot >= 0) _owner.SelectedIndex = _hot;
                    _owner.ClosePopup();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.Up) { _hot = Math.Max(0, (_hot < 0 ? _owner._index : _hot) - 1); Invalidate(); e.Handled = true; }
                if (e.KeyCode == Keys.Down)
                {
                    _hot = Math.Min(_owner._items.Length - 1, (_hot < 0 ? _owner._index : _hot) + 1);
                    Invalidate(); e.Handled = true;
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                _hot = Math.Max(0, Math.Min(_owner._items.Length - 1,
                       (_hot < 0 ? _owner._index : _hot) + (e.Delta > 0 ? -1 : 1)));
                Invalidate();
            }

            protected override void OnParentChanged(EventArgs e)
            {
                base.OnParentChanged(e);
                // 弹出后立刻拿焦点，方向键才有效。
                // Control.Focus() 返回 bool，不能直接当 Action，这里包一层。
                System.Threading.Tasks.Task.Run(new Action(() =>
                {
                    try { BeginInvoke(new Action(() => { Focus(); })); } catch { }
                }));
            }
        }
    }
}
