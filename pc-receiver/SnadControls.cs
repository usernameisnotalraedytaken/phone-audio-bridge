// 主题化控件。
//
// WinForms 原生控件在深色主题下有若干死穴，必须自绘才能统一：
//   - Button 会忽略 BackColor（除非关掉 UseVisualStyleBackColor），且不接受圆角
//   - ComboBox 的下拉箭头与边框是系统绘制，无法改色
//   - CheckBox 的方框是系统位图
//
// 这里全部自绘。代价是要自己处理状态与键盘，换来的是整套界面真正一致。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snad
{
    /// <summary>统一样式的按钮。支持主按钮（实心强调）与次按钮（描边）。</summary>
    internal sealed class FlatButton : Control
    {
        private bool _hover, _down;
        private bool _primary;

        public FlatButton(string text, bool primary = false)
        {
            Text = text;
            _primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Cursor = Cursors.Hand;
            // 与滚轮选择器同族：整块界面走终端语言，中文字形由系统回退补上
            Font = Theme.Mono(9f);
            TabStop = true;
        }

        public bool Primary
        {
            get { return _primary; }
            set { _primary = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        /// <summary>
        /// 按钮外观。
        ///
        /// 取折中：保留等宽字体（与读数系统同族），但恢复圆角与轻微渐变。
        /// 之前做成直角纯色块，"终端感"是足了，却让按钮看着像早年 Win32 控件——
        /// 按钮是动作入口，不是数据展示，不需要承担终端语言的重量。
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            const float radius = 4f;
            bool on = Enabled;

            Color fillTop, fillBottom, border, text;
            if (!on)
            {
                fillTop = fillBottom = Theme.Mix(Theme.BgRaised, Theme.BgPanel, 0.5f);
                border = Color.FromArgb(90, Theme.Line);
                text = Theme.TextTertiary;
            }
            else if (_primary)
            {
                // 主按钮：强调色反白，但用渐变给它厚度，不是一块死色
                Color baseCol = _down ? Theme.Scale(Theme.Accent, 0.78f)
                              : _hover ? Theme.Scale(Theme.Accent, 0.92f)
                              : Theme.Accent;
                fillTop = Theme.Scale(baseCol, 1.10f);
                fillBottom = Theme.Scale(baseCol, 0.90f);
                border = Theme.Scale(baseCol, 1.22f);
                text = Color.FromArgb(255, 10, 14, 18);
            }
            else
            {
                Color baseCol = _down ? Theme.BgActive
                              : _hover ? Theme.BgHover
                              : Theme.BgRaised;
                fillTop = Theme.Scale(baseCol, 1.12f);
                fillBottom = Theme.Scale(baseCol, 0.94f);
                border = _hover ? Color.FromArgb(170, Theme.Accent) : Color.FromArgb(120, Theme.Line);
                text = Theme.TextPrimary;
            }

            Theme.FillRoundGradient(g, r, radius, fillTop, fillBottom);
            using (var pen = new Pen(border, 1f))
                Theme.DrawRound(g, pen, r, radius);

            // 顶部一道极淡高光，强化"上表面受光"的感觉
            if (on)
            {
                using (var pen = new Pen(Color.FromArgb(38, 255, 255, 255), 1f))
                using (var path = Theme.SuperEllipse(
                    new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), radius - 1))
                    g.DrawPath(pen, path);
            }

            var f = Font;
            var sz = g.MeasureString(Text, f);
            using (var br = new SolidBrush(text))
                g.DrawString(Text, f, br,
                             (int)((Width - sz.Width) / 2f), (int)((Height - sz.Height) / 2f));
        }
    }

    /// <summary>统一样式的下拉框。自绘箭头与边框，避免系统浅色边框破坏整体。</summary>
    internal sealed class FlatCombo : Control
    {
        private string[] _items = new string[0];
        private int _selected = -1;
        private bool _hover, _open;
        private int _hoverItem = -1;

        public event EventHandler SelectedIndexChanged;

        public FlatCombo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Cursor = Cursors.Hand;
            Font = Theme.Ui(9f);
        }

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; if (_selected >= _items.Length) _selected = -1; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = (value < 0 || value >= _items.Length) ? -1 : value;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem
        {
            get { return (_selected >= 0 && _selected < _items.Length) ? _items[_selected] : null; }
        }

        /// <summary>按文本选中，用于从配置恢复。</summary>
        public void SelectText(string text)
        {
            int i = Array.IndexOf(_items, text);
            if (i >= 0) SelectedIndex = i;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _hoverItem = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_items.Length == 0) return;
            _open = !_open;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_open) return;
            int idx = ItemIndexAt(e.Y);
            if (idx != _hoverItem) { _hoverItem = idx; Invalidate(); }
        }

        private int ItemIndexAt(int y)
        {
            int itemH = ItemHeight;
            // 下拉列表从控件下方展开
            int ly = y - Height;
            if (ly < 0) return -1;
            int idx = ly / itemH;
            return (idx >= 0 && idx < _items.Length) ? idx : -1;
        }

        private int ItemHeight { get { return Math.Max(22, Font.Height + 8); } }

        /// <summary>下拉展开时需要的额外高度，由父容器在绘制时预留（这里主动请求重绘）。</summary>
        public int DropdownHeight { get { return _items.Length * ItemHeight; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            // 展开时不能只画自己这一块，否则下拉列表会被裁掉。
            // 因此把列表画在父容器的坐标系里（见 DrawDropdown）。
            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var br = new SolidBrush(_hover || _open ? Theme.BgHover : Theme.BgRaised))
                Theme.FillRound(g, br, r, 5f);
            using (var pen = new Pen(_hover || _open ? Color.FromArgb(150, Theme.Accent) : Color.FromArgb(120, Theme.Line), 1f))
                Theme.DrawRound(g, pen, r, 5f);

            string text = SelectedItem ?? "—";
            using (var br = new SolidBrush(Theme.TextPrimary))
                g.DrawString(text, Font, br, 10, (Height - Font.Height) / 2f);

            // 自绘箭头
            float ax = Width - 15, ay = Height / 2f - 1;
            using (var pen = new Pen(Theme.TextSecondary, 1.6f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                if (_open)
                {
                    g.DrawLine(pen, ax - 4, ay + 2, ax, ay - 2);
                    g.DrawLine(pen, ax, ay - 2, ax + 4, ay + 2);
                }
                else
                {
                    g.DrawLine(pen, ax - 4, ay - 2, ax, ay + 2);
                    g.DrawLine(pen, ax, ay + 2, ax + 4, ay - 2);
                }
            }
        }

        /// <summary>
        /// 在父容器上绘制展开的列表。
        ///
        /// 形态：每一项各自带一圈边框，当前项用强调色边框并加一个左侧标记条，
        /// 一眼能看出"列表里哪一项是当前的"，而不是只靠底色深浅区分。
        /// 外圈再套一层容器边框把整个列表框住。
        /// </summary>
        public void DrawDropdown(Graphics g)
        {
            if (!_open || _items.Length == 0) return;

            int itemH = ItemHeight;
            const int margin = 3;          // 项与容器边框之间的留白
            const int gap = 2;             // 项与项之间的间隙
            var boxRect = new RectangleF(Left + 0.5f, Top + Height + 2, Width - 1f,
                                         _items.Length * itemH + margin * 2);

            // 投影：极淡的偏移层，制造"浮起"的感觉
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                Theme.FillRound(g, sh, new RectangleF(boxRect.X + 2, boxRect.Y + 3, boxRect.Width, boxRect.Height), 6f);

            using (var br = new SolidBrush(Color.FromArgb(252, Theme.BgRaised)))
                Theme.FillRound(g, br, boxRect, 6f);
            using (var pen = new Pen(Color.FromArgb(190, Theme.Accent), 1f))
                Theme.DrawRound(g, pen, boxRect, 6f);

            for (int i = 0; i < _items.Length; i++)
            {
                bool sel = (i == _selected);
                bool hov = (i == _hoverItem);
                var itemRect = new RectangleF(boxRect.Left + margin,
                                              boxRect.Top + margin + i * itemH,
                                              boxRect.Width - margin * 2,
                                              itemH - gap);

                // 每项自己的底与边框
                Color fill = sel ? Color.FromArgb(48, Theme.Accent)
                           : hov ? Theme.BgActive
                           : Theme.Mix(Theme.BgRaised, Theme.BgPanel, 0.35f);
                using (var br = new SolidBrush(fill))
                    Theme.FillRound(g, br, itemRect, 4f);

                Color border = sel ? Theme.Accent
                             : hov ? Color.FromArgb(150, Theme.Accent)
                             : Color.FromArgb(110, Theme.Line);
                using (var pen = new Pen(border, sel ? 1.4f : 1f))
                    Theme.DrawRound(g, pen, itemRect, 4f);

                // 当前项左侧的强调标记条
                if (sel)
                {
                    using (var br = new SolidBrush(Theme.Accent))
                        Theme.FillRound(g, br,
                            new RectangleF(itemRect.Left + 3, itemRect.Top + 5, 2.5f, itemRect.Height - 10), 1.2f);
                }

                // 文本：当前项用强调色，其余用主文字色
                Color fg = sel ? Theme.Accent : Theme.TextPrimary;
                using (var br = new SolidBrush(fg))
                    g.DrawString(_items[i], Font, br,
                                 itemRect.Left + (sel ? 12 : 9),
                                 itemRect.Top + (itemRect.Height - Font.Height) / 2f);
            }
        }

        /// <summary>处理展开状态的鼠标点击。父容器在 MouseDown 时转发。</summary>
        public bool HandleDropdownClick(Point parentPoint)
        {
            if (!_open) return false;
            int idx = ItemIndexAt(parentPoint.Y - Top);
            if (idx >= 0 && idx < _items.Length)
            {
                SelectedIndex = idx;
                _open = false;
                Invalidate();
                return true;
            }
            // 点在列表外：收起
            _open = false;
            Invalidate();
            return false;
        }

        public bool IsOpen { get { return _open; } }

        public void CloseDropdown()
        {
            if (!_open) return;
            _open = false;
            Invalidate();
        }
    }

    /// <summary>统一样式的复选框。自绘方框与对勾。</summary>
    internal sealed class FlatCheck : Control
    {
        private bool _checked;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public FlatCheck(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Cursor = Cursors.Hand;
            Font = Theme.Ui(9f);
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

            float bs = 15f;
            float by = (Height - bs) / 2f;
            var box = new RectangleF(0.5f, by + 0.5f, bs, bs);

            using (var br = new SolidBrush(_checked ? Theme.Accent : (_hover ? Theme.BgHover : Theme.BgRaised)))
                Theme.FillRound(g, br, box, 3.5f);
            using (var pen = new Pen(_checked ? Color.FromArgb(180, Theme.Accent) : Color.FromArgb(130, Theme.Line), 1f))
                Theme.DrawRound(g, pen, box, 3.5f);

            if (_checked)
            {
                using (var pen = new Pen(Color.FromArgb(255, 10, 14, 18), 2f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, box.Left + 3.5f, box.Top + 7.5f, box.Left + 6f, box.Top + 10.5f);
                    g.DrawLine(pen, box.Left + 6f, box.Top + 10.5f, box.Left + 11.5f, box.Top + 4.5f);
                }
            }

            using (var br = new SolidBrush(_hover ? Theme.TextPrimary : Theme.TextSecondary))
                g.DrawString(Text, Font, br, bs + 8, (Height - Font.Height) / 2f);
        }
    }

    /// <summary>
    /// 自绘细滚动条。
    ///
    /// 为什么不用系统滚动条：深色主题下原生滚动条的浅灰轨道和立体箭头非常突兀，
    /// 而且无法通过任何属性改色。WinForms 的 ScrollBar 是系统绘制的，只能自绘。
    ///
    /// 形态参照现代应用：细轨、圆角滑块、悬停加宽并提亮、无需滚动时整条淡出。
    /// </summary>
    internal sealed class FlatScrollBar : Control
    {
        private int _value;
        private int _maximum = 1;      // 可滚动的内容单位总数
        private int _viewSize = 1;     // 可视区域占用的单位数
        private bool _hover, _dragging;
        private int _dragOffset;

        /// <summary>值变化时通知宿主重绘内容。</summary>
        public event EventHandler ValueChanged;

        private const int TrackWidth = 10;
        private const int ThumbWidth = 6;
        private const int ThumbWidthHover = 8;
        private const int MinThumbHeight = 28;

        public FlatScrollBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;
            Width = TrackWidth;
            Cursor = Cursors.Default;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int max = Math.Max(0, _maximum - _viewSize);
                int v = value < 0 ? 0 : (value > max ? max : value);
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = Math.Max(1, value); ClampValue(); Invalidate(); }
        }

        public int ViewSize
        {
            get { return _viewSize; }
            set { _viewSize = Math.Max(1, value); ClampValue(); Invalidate(); }
        }

        /// <summary>内容是否超出可视区。未超出时整条淡出，不占用视觉注意力。</summary>
        public bool Needed { get { return _maximum > _viewSize; } }

        private void ClampValue()
        {
            int max = Math.Max(0, _maximum - _viewSize);
            if (_value > max) _value = max;
            if (_value < 0) _value = 0;
        }

        /// <summary>滑块的位置与高度（像素）。</summary>
        private void ThumbRect(out float top, out float height)
        {
            float track = ClientSize.Height;
            double frac = Math.Min(1.0, (double)_viewSize / _maximum);
            height = (float)Math.Max(MinThumbHeight, track * frac);
            if (height > track) height = track;
            double scrollFrac = _maximum > _viewSize
                ? (double)_value / (_maximum - _viewSize) : 0;
            top = (float)(scrollFrac * (track - height));
            if (top < 0) top = 0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            if (!Needed) return;   // 不需要滚动时不画任何东西

            float top, height;
            ThumbRect(out top, out height);

            int w = (_hover || _dragging) ? ThumbWidthHover : ThumbWidth;
            float x = (ClientSize.Width - w) / 2f;

            Color c = _dragging ? Theme.Accent
                    : _hover ? Theme.Mix(Theme.TextTertiary, Theme.Accent, 0.4f)
                    : Color.FromArgb(150, Theme.TextTertiary);

            using (var br = new SolidBrush(c))
                Theme.FillRound(g, br, new RectangleF(x, top, w, height), w / 2f);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            float top, height;
            ThumbRect(out top, out height);

            if (e.Y >= top && e.Y <= top + height)
            {
                _dragOffset = (int)(e.Y - top);
            }
            else
            {
                // 点击空白处：把滑块中心移到点击位置（一屏一屏跳太慢）
                _dragOffset = (int)(height / 2);
                MoveThumbTo(e.Y - _dragOffset, height);
            }
            _dragging = true;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            float top, height;
            ThumbRect(out top, out height);
            MoveThumbTo(e.Y - _dragOffset, height);
        }

        private void MoveThumbTo(float thumbTop, float thumbHeight)
        {
            float track = ClientSize.Height;
            float range = track - thumbHeight;
            if (range <= 0) return;
            double frac = thumbTop / range;
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            Value = (int)Math.Round(frac * Math.Max(0, _maximum - _viewSize));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        /// <summary>鼠标滚轮：向上为负 delta。</summary>
        public void WheelBy(int delta)
        {
            int step = Math.Max(1, _viewSize / 6);
            Value = _value + (delta < 0 ? -step : step);
        }
    }

    /// <summary>
    /// 滚轮选择器（iOS 时间选择器形态），默认收起，点击才展开。
    ///
    /// 收起态只占一行，显示当前值——参数不常改，常驻三行会白占地方且显得凌乱。
    /// 点击后用高度动画展开成滚轮：中间是选中项（高亮框），上下项按距离渐隐。
    /// 选中、点空白处或失焦都会收起。
    ///
    /// 用高度动画而不是直接切换高度：高度突变会让整行控件跳一下，
    /// 展开过程本身也是"这里可以滚"的提示。
    /// </summary>
    internal sealed class WheelSelector : Control
    {
        private string[] _items = new string[0];
        private int _index;
        private double _offset;          // 滚动位置（以项为单位），向 _index 缓动
        private double _expand;          // 0=收起 1=展开，动画量
        private bool _open;
        private bool _dragging;
        private bool _pressed;
        private int _dragStartY;
        private double _dragStartOffset;
        private bool _hover;
        private readonly Timer _anim;

        /// <summary>按下瞬间的鼠标位置与展开状态，用于区分"点开"与"点选"。</summary>
        private int _downX, _downY;
        private bool _wasOpenAtDown;

        /// <summary>最近一次鼠标事件的位置。OnClick 里用它而不是 Cursor.Position。</summary>
        private int _lastX, _lastY;

        public event EventHandler SelectedIndexChanged;

        /// <summary>展开后可见行数（奇数，保证中间一行完整）。</summary>
        public int VisibleRows = 3;

        public WheelSelector()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgRaised;
            Font = Theme.Mono(9.5f);
            Cursor = Cursors.Hand;
            Height = RowHeight;

            _anim = new Timer { Interval = 16 };
            _anim.Tick += (s, e) => Step();
        }

        private int RowHeight { get { return Math.Max(20, Font.Height + 8); } }

        /// <summary>展开后的目标高度。</summary>
        private int ExpandedHeight { get { return RowHeight * VisibleRows; } }

        /// <summary>
        /// 高度变化通知宿主。宿主需要据此重新垂直居中，
        /// 否则展开后控件会偏出中心或被容器裁掉。
        /// </summary>
        public event EventHandler HeightChanged;

        private void NotifyHeightChanged()
        {
            if (HeightChanged != null) HeightChanged(this, EventArgs.Empty);
        }

        public string[] Items
        {
            get { return _items; }
            set
            {
                _items = value ?? new string[0];
                if (_index >= _items.Length) _index = Math.Max(0, _items.Length - 1);
                _offset = _index;
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (_items.Length == 0) return;
                int v = value < 0 ? 0 : (value >= _items.Length ? _items.Length - 1 : value);
                if (v == _index) return;
                _index = v;
                _anim.Start();
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedItem
        {
            get { return (_index >= 0 && _index < _items.Length) ? _items[_index] : null; }
        }

        public void SelectText(string text)
        {
            int i = Array.IndexOf(_items, text);
            if (i >= 0) SelectedIndex = i;
        }

        public bool IsOpen { get { return _open; } }

        /// <summary>
        /// 滚动位置的合法区间。
        ///
        /// 高亮固定在正中，因此 _offset 直接就是"正中间那一项的序号"，
        /// 区间就是 [0, 总数-1]。不需要为"底部留白"做补偿——
        /// 两端本来就会自然露出空白（iOS 滚轮同理）。
        /// </summary>
        private void OffsetRange(out double lo, out double hi)
        {
            int n = _items.Length;
            if (n <= 0) { lo = 0; hi = 0; return; }
            lo = 0;
            hi = n - 1;
        }

        private double ClampOffset(double v)
        {
            double lo, hi;
            OffsetRange(out lo, out hi);
            if (v < lo) v = lo;
            if (v > hi) v = hi;
            return v;
        }

        public void Open()
        {
            if (_open || _items.Length == 0) return;
            _open = true;

            // 高亮在正中，_offset 就是中间那一项的序号，因此直接取当前选中项，
            // 它就会出现在正中间。展开过程因此是"高亮处始终是当前值"，
            // 上下两侧把相邻候选露出来。
            _offset = _index;
            Height = ExpandedHeight + 2;
            NotifyHeightChanged();

            _anim.Start();
            Invalidate();
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            _dragging = false;
            // 收起后回到"只显示当前值"的单行高度
            Height = RowHeight;
            _offset = _index;
            NotifyHeightChanged();

            _anim.Start();
            Invalidate();
        }

        /// <summary>每帧推进：展开动画与滚动缓动各自收敛。</summary>
        private void Step()
        {
            bool busy = false;

            double targetExpand = _open ? 1.0 : 0.0;
            double de = targetExpand - _expand;
            if (Math.Abs(de) > 0.02) { _expand += de * 0.32; busy = true; }
            else _expand = targetExpand;

            double dd = _index - _offset;
            if (Math.Abs(dd) > 0.01) { _offset += dd * 0.35; busy = true; }
            else _offset = _index;

            Invalidate();
            if (!busy) _anim.Stop();
        }

        /// <summary>滚轮模式下的横条一半高度。</summary>
        private float BarHeight { get { return (float)(_expand * RowHeight / 2.0); } }

        /// <summary>
        /// 高亮条在控件内的 y。
        ///
        /// 展开后必须落在正中间——这是 iOS 时间滚轮的核心特征：
        /// 高亮位置固定不动，列表从它下面滚过。之前把高亮放在第一行，
        /// 结果选中项永远贴顶、下方空一截，观感是"错位"而不是"滚轮"。
        /// 收起态本来就是单行居中，因此用 _expand 过渡即可，无需跳变。
        /// </summary>
        private float BarTop
        {
            get
            {
                float collapsed = (ClientSize.Height - RowHeight) / 2f;
                float expanded = (ClientSize.Height - RowHeight) / 2f;
                return (float)(collapsed + (expanded - collapsed) * _expand);
            }
        }

        /// <summary>
        /// 中括号向内收进的距离。展开时收敛到 0，即"线条展开成滚轮"。
        ///
        /// 用 Width 而不是 ClientSize.Width：控件尚未挂到父容器时 ClientSize 为 0，
        /// 会把括号算到文字上（实测悬停态括号压在 "balanced" 上）。
        /// </summary>
        private float BracketInset
        {
            get
            {
                float w = Width > 0 ? Width : 200f;
                return (float)((1.0 - _expand) * (w * 0.26f));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            if (_items.Length == 0) return;

            if (_expand < 0.02) DrawCollapsed(g);
            else DrawExpanded(g);
        }

        /// <summary>
        /// 收起态。三种子状态：
        ///   静止 —— 只有文字，颜色压到次级，不画任何装饰，尽量不引人注意
        ///   悬停 —— 文字提亮，两侧出现方块中括号，底部一条细线
        ///   按下 —— 括号与细线提亮，为展开做视觉准备
        ///
        /// 括号用"竖线 + 上下向内短横"的方块形态，全部平直无圆弧：
        /// 圆弧属于拟物/科技风，方块括号才是终端审美。
        /// </summary>
        private void DrawCollapsed(Graphics g)
        {
            float h = ClientSize.Height;
            float midY = h / 2f;

            var font = _hover ? Theme.MonoEmphasis(10f) : Theme.Mono(9.5f);
            Color fg = _hover ? Theme.TextPrimary : Theme.TextSecondary;
            if (_hover) fg = Theme.Mix(fg, Theme.Accent, 0.25f);

            // 水平居中于"左右留白之间"的中点，而不是控件中点——
            // 右侧要给箭头留位，按控件中点居中的话文字会偏右，看着不齐。
            const float chevW = 11f;
            const float leftPad = 10f;
            float rightPad = chevW + 10f;
            float textCenter = (leftPad + (ClientSize.Width - rightPad)) / 2f;
            var sz = g.MeasureString(_items[_index], font);
            using (var br = new SolidBrush(fg))
                g.DrawString(_items[_index], font, br,
                             textCenter - sz.Width / 2f, midY - sz.Height / 2f);

            // 静止态不画任何装饰——它应该安静地待在那里，只在被注意到时才亮起来。
            if (!_hover) return;

            // 括号构成按钮边缘的左右两段，因此贴着控件两侧，而不是紧贴文字。
            // 箭头放在右括号内侧，文字在括号内居中 —— 视觉上是 [    balanced    v ]
            float arm = Math.Max(4f, h * 0.18f);     // 上下向内的小横臂，让"["有形状
            float half = Math.Max(5f, h * 0.32f);
            float left = 1f;
            float right = ClientSize.Width - 1f;

            Color line = Color.FromArgb(_pressed ? 255 : 210, Theme.Accent);
            using (var pen = new Pen(line, _pressed ? 1.8f : 1.3f))
            {
                pen.StartCap = LineCap.Flat;
                pen.EndCap = LineCap.Flat;
                // 左括号
                g.DrawLine(pen, left, midY - half, left, midY + half);
                g.DrawLine(pen, left, midY - half, left + arm, midY - half);
                g.DrawLine(pen, left, midY + half, left + arm, midY + half);
                // 右括号（镜像）
                g.DrawLine(pen, right, midY - half, right, midY + half);
                g.DrawLine(pen, right, midY - half, right - arm, midY - half);
                g.DrawLine(pen, right, midY + half, right - arm, midY + half);
            }

            // 箭头放在右括号内侧
            float ax = right - arm - chevW / 2f - 2f;
            using (var pen = new Pen(Color.FromArgb(_pressed ? 240 : 180, fg), 1.4f))
            {
                pen.StartCap = LineCap.Flat; pen.EndCap = LineCap.Flat;
                g.DrawLine(pen, ax - 4, midY - 2, ax, midY + 2);
                g.DrawLine(pen, ax, midY + 2, ax + 4, midY - 2);
            }
        }

        /// <summary>
        /// 展开态：终端列表的形态——硬边高亮条贯穿整行，左侧一个块状光标，
        /// 上下项按距离衰减亮度，边缘用渐变淡出。
        /// 不使用圆角、渐变填充与发光：那些属于拟物风，与 TUI 审美冲突。
        /// </summary>
        private void DrawExpanded(Graphics g)
        {
            int rowH = RowHeight;
            float barH = BarHeight;
            float barY = BarTop;
            float center = barY + barH;

            // 展开态：外框 + 高亮条都贴到第一行（BarTop≈0），与收起态"条在整行居中"
            // 之间用 _expand 过渡；文字因此始终停在原处，视觉上是滚轮从下方长出来。
            // 高度按已展开的部分增长，未展开时控件高度就等于收起高度，不会被裁。
            float grow = (float)(ClientSize.Height * _expand);
            if (grow > 1f)
            {
                using (var pen = new Pen(Color.FromArgb((int)(200 * _expand), Theme.Accent), 1f))
                    g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, grow - 1f);
            }

            // 选中行：整行硬边高亮
            if (barH > 0.5f)
            {
                var selRect = new RectangleF(0, barY, ClientSize.Width - 1, barH * 2);
                using (var br = new SolidBrush(Color.FromArgb(_hover ? 70 : 56, Theme.Accent)))
                    g.FillRectangle(br, selRect);
            }

            int half = VisibleRows / 2;
            int baseIdx = (int)Math.Floor(_offset);
            float selCenterY = float.MinValue;

            for (int row = -half - 1; row <= half + 1; row++)
            {
                int idx = baseIdx + row;
                if (idx < 0 || idx >= _items.Length) continue;

                float cy = center + (float)((idx - _offset) * rowH);
                if (cy < -rowH || cy > ClientSize.Height + rowH) continue;

                float dist = Math.Abs(cy - center) / rowH;
                float alpha = 1f - dist * 0.45f;
                if (alpha < 0.15f) alpha = 0.15f;
                bool isSel = Math.Abs(cy - center) < rowH * 0.5f;

                // 选中项用强调色，其余按距离衰减——模拟终端里"非焦点行更暗"
                Color fg = isSel ? Theme.Accent
                                 : Theme.Mix(Theme.TextTertiary, Theme.TextSecondary,
                                             Math.Max(0f, 1f - dist * 0.6f));

                var f = isSel ? Theme.MonoEmphasis(10f) : Theme.Mono(9f);
                var sz = g.MeasureString(_items[idx], f);
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * alpha), fg)))
                    g.DrawString(_items[idx], f, br,
                                 (ClientSize.Width - sz.Width) / 2f, cy - sz.Height / 2f);

                if (isSel)
                {
                    selCenterY = cy;

                    // 确认态：选中项左侧一个实心方块 + 右侧一个勾。
                    // 只靠高亮条不够——高亮表示"光标停在这"，方块与勾才表示
                    // "这一项就是当前值"，两者在有滚动偏移时是不同含义。
                    using (var br = new SolidBrush(Color.FromArgb(230, Theme.Accent)))
                    {
                        g.FillRectangle(br, 4, cy - 3, 6, 6);
                        // 右侧勾：两段短线
                        float cx = ClientSize.Width - 14, cyy = cy;
                        using (var pen = new Pen(Color.FromArgb(230, Theme.Accent), 2f))
                        {
                            pen.StartCap = LineCap.Flat; pen.EndCap = LineCap.Flat;
                            g.DrawLine(pen, cx - 4, cyy, cx - 1, cyy + 3);
                            g.DrawLine(pen, cx - 1, cyy + 3, cx + 4, cyy - 3);
                        }
                    }
                }
            }

            // 块状光标：贴在选中行左端，是终端里最常见的"当前位置"标记
            if (selCenterY > float.MinValue)
            {
                float ch = rowH * 0.5f;
                using (var br = new SolidBrush(Color.FromArgb(230, Theme.Accent)))
                    g.FillRectangle(br, 4, selCenterY - ch / 2f, 3, ch);
            }

            // 上下渐隐遮罩：让边缘项自然消失在背景里，而不是被硬裁
            int maskH = Math.Max(1, (int)(rowH * _expand));
            Color bgc = Theme.BgBase;
            using (var top = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, ClientSize.Width), maskH),
                bgc, Color.Transparent, LinearGradientMode.Vertical))
                g.FillRectangle(top, 0, 0, ClientSize.Width, maskH);
            using (var bot = new LinearGradientBrush(
                new Rectangle(0, Math.Max(0, ClientSize.Height - maskH), Math.Max(1, ClientSize.Width), maskH),
                Color.Transparent, bgc, LinearGradientMode.Vertical))
                g.FillRectangle(bot, 0, ClientSize.Height - maskH, ClientSize.Width, maskH);
        }

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _pressed = true;
            _downX = e.X;
            _downY = e.Y;
            // 必须在展开之前记录：OnMouseDown 里就会把 _open 置为 true，
            // 事后再读就分不清"本来就是展开的"和"这次点开的"了。
            _wasOpenAtDown = _open;
            Invalidate();

            if (!_open)
            {
                // 收起态：按下即展开，并在展开后开始拖动。
                // 这样"点一下就能直接拖"，不用先点开再拖第二次。
                Open();
                _dragging = true;
                _dragStartY = e.Y;
                _dragStartOffset = _offset;
                Capture = true;
                return;
            }

            _dragging = true;
            _dragStartY = e.Y;
            _dragStartOffset = _offset;
            Capture = true;
            _anim.Stop();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _lastX = e.X;
            _lastY = e.Y;
            if (!_dragging || !_open) return;
            // 向上拖动 = 内容上移 = 索引变大，与触屏直觉一致
            double delta = (double)(_dragStartY - e.Y) / RowHeight;
            _offset = ClampOffset(_dragStartOffset + delta);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            _lastX = e.X;
            _lastY = e.Y;
            Invalidate();
            if (!_dragging) return;
            _dragging = false;
            Capture = false;

            // 松手吸附到最近一项。
            // 高亮固定在正中，因此 _offset 就是中间那一项的序号，
            // 两者必须收敛到同一个值，否则会出现"高亮的和选中的不是同一个"。
            int target = (int)Math.Round(ClampOffset(_offset));
            if (target < 0) target = 0;
            if (target >= _items.Length) target = _items.Length - 1;
            _offset = target;
            SelectedIndex = target;
            _anim.Start();

            // 只有"本来展开、或这次拖动了"才算确认，才收起。
            // 收起态按下只为展开：此时若也收起，表现就是点开一松手又关掉
            // （实测正是如此——提前收起的其实是这里，不是 OnClick）。
            bool moved = Math.Abs(e.Y - _downY) > 3 || Math.Abs(e.X - _downX) > 3;
            if (DebugGestures)
                Console.WriteLine("    [手势] OnMouseUp: wasOpenAtDown=" + _wasOpenAtDown +
                                  " moved=" + moved + " 位置 " + e.X + "," + e.Y +
                                  " 按下于 " + _downX + "," + _downY);
            if (_wasOpenAtDown || moved) Close();
        }

        /// <summary>
        /// 点击。
        ///
        /// 这里必须区分两种手势，否则"点开"会被自己立刻收起：
        ///   点开     —— 按下与松开位置几乎相同，且按下时处于收起态
        ///   点选确认 —— 按下时已展开，或期间发生了拖动
        ///
        /// 位置一律用事件里记下的 _lastX/_lastY，不读 Cursor.Position：
        /// 控件未挂到窗口、或光标已移开时，反推出来的坐标是错的
        /// （实测 OnMouseUp 判为未移动、OnClick 却判为移动了）。
        /// </summary>
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_items.Length == 0) return;

            bool moved = Math.Abs(_lastY - _downY) > 3 || Math.Abs(_lastX - _downX) > 3;
            if (DebugGestures)
                Console.WriteLine("    [手势] OnClick: wasOpenAtDown=" + _wasOpenAtDown +
                                  " moved=" + moved + " open=" + _open +
                                  " 松开于 " + _lastX + "," + _lastY);
            if (!_open) return;

            // 在收起态按下、且没有拖动 → 这是一次"点开"，保持展开
            if (!_wasOpenAtDown && !moved) return;

            int rowH = RowHeight;
            float center = BarTop + BarHeight;
            int delta = (int)Math.Round((_lastY - center) / rowH);
            if (delta != 0) SelectedIndex = _index + delta;
            // 点到正中间那一项时 delta 为 0，但那也是一次确认，同样要收起
            Close();
        }

        /// <summary>手势诊断开关。排查"点开被自己收起"这类问题时打开。</summary>
        public static bool DebugGestures = false;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_open) return;      // 收起态不响应滚轮，避免误改设置
            SelectedIndex = _index + (e.Delta < 0 ? 1 : -1);
        }

        /// <summary>失焦即收起。点击别处由宿主调用 CloseAll 处理。</summary>
        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 可折叠的日志区。
    ///
    /// 日志是排查用的，正常使用时它只占地方——因此默认折叠，只留一条标题栏，
    /// 点击标题栏展开/收起。标题栏同时显示行数，折叠时也知道有没有新日志。
    /// </summary>
    internal sealed class CollapsibleLog : Panel
    {
        private readonly LogStrip _log;
        private readonly Label _header;
        private bool _expanded;

        /// <summary>展开后的高度。</summary>
        public int ExpandedHeight = 130;

        /// <summary>折叠时只留标题栏的高度。</summary>
        public int CollapsedHeight = 26;

        public CollapsibleLog()
        {
            BackColor = Theme.BgPanel;
            _header = new Label
            {
                Dock = DockStyle.Top,
                Height = CollapsedHeight,
                BackColor = Theme.BgPanel,
                ForeColor = Theme.TextTertiary,
                Font = Theme.Label(8f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                Cursor = Cursors.Hand,
                Text = "LOG  [+]  0 LINES"
            };
            _header.Click += (s, e) => Toggle();
            Controls.Add(_header);

            _log = new LogStrip { Visible = false, BackColor = Theme.BgPanel };
            Controls.Add(_log);

            Height = CollapsedHeight;
            Resize += (s, e) => LayoutChildren();
            LayoutChildren();
        }

        public bool Expanded { get { return _expanded; } }

        public void Toggle()
        {
            _expanded = !_expanded;
            _log.Visible = _expanded;
            Height = _expanded ? ExpandedHeight : CollapsedHeight;
            UpdateHeader();
            LayoutChildren();
            if (_expanded) _log.Focus();
        }

        public void Append(string line)
        {
            _log.Append(line);
            UpdateHeader();
        }

        public void Clear()
        {
            _log.Clear();
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            _header.Text = "LOG  " + (_expanded ? "[−]" : "[+]") + "  " + _log.LineCount + " LINES";
        }

        private void LayoutChildren()
        {
            _log.Bounds = new Rectangle(
                12, CollapsedHeight,
                Math.Max(10, ClientSize.Width - 24),
                Math.Max(10, ClientSize.Height - CollapsedHeight - 6));
        }
    }

    /// <summary>
    /// 事件日志条。
    ///
    /// 用只读 TextBox 承担滚动与选择（自己实现文本排版与命中测试不划算），
    /// 但把它的系统滚动条藏掉，改用自绘的 FlatScrollBar 并由本控件接管滚轮。
    /// 颜色压到底色，使日志退到视觉底层——它是排查用的，不该和新界面的数据抢注意力。
    /// </summary>
    internal sealed class LogStrip : Control
    {
        private const int MaxLines = 400;

        private readonly TextBox _inner;
        private readonly FlatScrollBar _bar;
        private readonly List<string> _lines = new List<string>();

        public LogStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgPanel;

            _inner = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                // 关键：ScrollBars.None 把系统滚动条彻底去掉，只保留滚动能力
                ScrollBars = ScrollBars.None,
                BackColor = Theme.BgBase,
                ForeColor = Theme.TextTertiary,
                Font = Theme.Mono(8.5f),
                TabStop = false,
                ShortcutsEnabled = true      // 允许 Ctrl+C 复制
            };
            _inner.MouseWheel += OnInnerWheel;
            _inner.KeyDown += OnInnerKey;
            Controls.Add(_inner);

            _bar = new FlatScrollBar { Width = 10 };
            _bar.ValueChanged += (s, e) => SyncScroll();
            Controls.Add(_bar);

            Resize += (s, e) => Relayout();
        }

        private void Relayout()
        {
            int barW = 10;
            _bar.Bounds = new Rectangle(ClientSize.Width - barW, 0, barW, ClientSize.Height);
            _inner.Bounds = new Rectangle(0, 0, Math.Max(10, ClientSize.Width - barW - 2), ClientSize.Height);
            UpdateScrollMetrics();
        }

        /// <summary>追加一行，带时间戳。超出上限时裁掉最早的行。</summary>
        public void Append(string line)
        {
            _lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines / 2);

            _inner.Lines = _lines.ToArray();
            // 新内容到达时滚到底部——日志的价值在于最新的那几行
            _bar.Value = int.MaxValue;
            UpdateScrollMetrics();
        }

        /// <summary>已记录的行数。折叠时用来在标题栏显示。</summary>
        public int LineCount { get { return _lines.Count; } }

        private void UpdateScrollMetrics()
        {
            int lineH = Math.Max(1, _inner.Font.Height);
            int visibleLines = Math.Max(1, _inner.ClientSize.Height / lineH);
            _bar.Maximum = _lines.Count;
            _bar.ViewSize = visibleLines;
            _bar.Invalidate();
        }

        private void OnInnerWheel(object sender, MouseEventArgs e)
        {
            _bar.WheelBy(e.Delta);
        }

        private void OnInnerKey(object sender, KeyEventArgs e)
        {
            // 键盘滚动也走同一条路，保证滚动条位置与实际内容一致
            if (e.KeyCode == Keys.Up) { _bar.Value -= 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { _bar.Value += 1; e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { _bar.Value -= _bar.ViewSize; e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { _bar.Value += _bar.ViewSize; e.Handled = true; }
        }

        /// <summary>把自绘滚动条的位置换算成 TextBox 的字符偏移并滚动。</summary>
        private void SyncScroll()
        {
            int lineH = Math.Max(1, _inner.Font.Height);
            // TextBox 只能按字符索引定位，用"每行大约多少字符"估算不可靠，
            // 因此改为按行索引：取第 N 行的首字符位置。
            int targetLine = _bar.Value;
            if (targetLine <= 0)
            {
                _inner.SelectionStart = 0;
                _inner.ScrollToCaret();
                return;
            }
            int idx = 0, line = 0;
            string text = _inner.Text;
            while (line < targetLine && idx < text.Length)
            {
                int nl = text.IndexOf('\n', idx);
                if (nl < 0) break;
                idx = nl + 1;
                line++;
            }
            if (idx <= text.Length)
            {
                _inner.SelectionStart = idx;
                _inner.SelectionLength = 0;
                _inner.ScrollToCaret();
            }
        }

        public void Clear()
        {
            _lines.Clear();
            _inner.Clear();
            UpdateScrollMetrics();
        }
    }
}
