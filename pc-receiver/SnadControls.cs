// 主题化控件。
//
// WinForms 原生控件在深色主题下有若干死穴，必须自绘才能统一：
//   - Button 会忽略 BackColor（除非关掉 UseVisualStyleBackColor），且不接受圆角
//   - ComboBox 的下拉箭头与边框是系统绘制，无法改色
//   - CheckBox 的方框是系统位图
//
// 这里全部自绘。代价是要自己处理状态与键盘，换来的是整套界面真正一致。

using System;
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
            Font = Theme.Ui(9f);
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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);

            using (var bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.BgPanel))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            bool on = Enabled;

            Color fill, border, text;
            if (!on)
            {
                fill = Theme.Mix(Theme.BgRaised, Theme.BgPanel, 0.5f);
                border = Color.FromArgb(90, Theme.Line);
                text = Theme.TextTertiary;
            }
            else if (_primary)
            {
                fill = _down ? Theme.Scale(Theme.Accent, 0.75f)
                     : _hover ? Theme.Scale(Theme.Accent, 0.9f)
                     : Theme.Accent;
                border = Color.FromArgb(160, Theme.Scale(Theme.Accent, 1.15f));
                text = Color.FromArgb(255, 10, 14, 18);       // 强调底上用深色文字，对比最高
            }
            else
            {
                fill = _down ? Theme.BgActive
                     : _hover ? Theme.BgHover
                     : Theme.BgRaised;
                // 悬停时描边亮起，是"可点"的明确提示
                border = _hover ? Color.FromArgb(150, Theme.Accent) : Color.FromArgb(120, Theme.Line);
                text = Theme.TextPrimary;
            }

            using (var br = new SolidBrush(fill))
                Theme.FillRound(g, br, r, 5f);
            using (var pen = new Pen(border, 1f))
                Theme.DrawRound(g, pen, r, 5f);

            // 主按钮加一层极淡外发光，突出主操作
            if (_primary && on)
                using (var pen = new Pen(Color.FromArgb(45, Theme.Accent), 3f))
                    Theme.DrawRound(g, pen, new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f), 5f);

            var f = Font;
            var sz = g.MeasureString(Text, f);
            using (var br = new SolidBrush(text))
                g.DrawString(Text, f, br, (Width - sz.Width) / 2f, (Height - sz.Height) / 2f);
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
        /// WinForms 没有内建下拉层，这里让父容器负责重绘列表区域。
        /// </summary>
        public void DrawDropdown(Graphics g)
        {
            if (!_open || _items.Length == 0) return;
            int itemH = ItemHeight;
            var boxRect = new RectangleF(Left + 0.5f, Top + Height, Width - 1f, _items.Length * itemH);

            using (var br = new SolidBrush(Color.FromArgb(250, Theme.BgRaised)))
                Theme.FillRound(g, br, boxRect, 5f);
            using (var pen = new Pen(Color.FromArgb(170, Theme.Accent), 1f))
                Theme.DrawRound(g, pen, boxRect, 5f);

            for (int i = 0; i < _items.Length; i++)
            {
                var itemRect = new RectangleF(boxRect.Left + 2, boxRect.Top + 2 + i * itemH,
                                              boxRect.Width - 4, itemH - 1);
                if (i == _hoverItem)
                {
                    using (var br = new SolidBrush(Theme.BgActive))
                        Theme.FillRound(g, br, itemRect, 4f);
                }
                else if (i == _selected)
                {
                    using (var br = new SolidBrush(Color.FromArgb(60, Theme.Accent)))
                        Theme.FillRound(g, br, itemRect, 4f);
                }
                using (var br = new SolidBrush(i == _selected ? Theme.Accent : Theme.TextPrimary))
                    g.DrawString(_items[i], Font, br, itemRect.Left + 8,
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
}
