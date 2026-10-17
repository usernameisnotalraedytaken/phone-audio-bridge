// 只读日志视图。
//
// 与 LogStrip 的分工：
//   LogStrip —— 追加式，主界面接收日志用
//   LogView  —— 只读查看器，日志弹窗用，文本由外部整体设置
//
// 刻意做成"无逻辑"控件：不含追加判断、不含行数统计之外的状态，
// 这样弹窗那边负责"取哪些文本"，控件只负责"画出来"。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class LogView : Control
    {
        private string _text = "";
        private int _scroll;
        private FlatScrollBar _bar;

        public LogView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BgBase;
            Font = Theme.Mono(8.5f);

            _bar = new FlatScrollBar { Width = 8, Visible = false };
            _bar.ValueChanged += (s, e) => { _scroll = _bar.Value; Invalidate(); };
            Controls.Add(_bar);
            Resize += (s, e) => LayoutBar();
        }

        public string Text2
        {
            get { return _text; }
            set
            {
                _text = value ?? "";
                _lines = null;
                ClampScroll();
                Invalidate();
            }
        }

        public int LineCount
        {
            get { return Lines.Length; }
        }

        /// <summary>滚到底部（看最新日志）。</summary>
        public void ScrollToEnd()
        {
            _scroll = Math.Max(0, Lines.Length - VisibleLines);
            SyncBar();
            Invalidate();
        }

        /// <summary>当前是否停在末尾（用于刷新后保持贴底）。</summary>
        public bool IsAtEnd
        {
            get { return _scroll >= Math.Max(0, Lines.Length - VisibleLines); }
        }

        /// <summary>滚到顶部。</summary>
        public void ScrollToTop()
        {
            _scroll = 0;
            SyncBar();
            Invalidate();
        }

        private string[] _lines;
        private string[] Lines
        {
            get
            {
                if (_lines == null)
                    _lines = _text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                return _lines;
            }
        }

        private int LineH { get { return Font.Height; } }
        private int VisibleLines { get { return Math.Max(1, (ClientSize.Height - 8) / Math.Max(1, LineH)); } }

        private void ClampScroll()
        {
            int max = Math.Max(0, Lines.Length - VisibleLines);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
            SyncBar();
        }

        private void SyncBar()
        {
            if (_bar == null) return;
            // FlatScrollBar 的接口是 Value/Maximum/ViewSize/Needed，
            // 不是 WinForms 标准那套（Minimum/LargeChange）。
            int total = Lines.Length;
            _bar.ViewSize = VisibleLines;
            _bar.Maximum = Math.Max(0, total - 1);
            _bar.Value = _scroll;
            _bar.Visible = _bar.Needed;
            LayoutBar();
        }

        private void LayoutBar()
        {
            if (_bar == null) return;
            _bar.Bounds = new Rectangle(ClientSize.Width - _bar.Width - 2, 4,
                                        _bar.Width, Math.Max(10, ClientSize.Height - 8));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll -= (e.Delta > 0 ? 3 : -3);
            ClampScroll();
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Up || (keyData & Keys.KeyCode) == Keys.Down ||
                (keyData & Keys.KeyCode) == Keys.PageUp || (keyData & Keys.KeyCode) == Keys.PageDown ||
                (keyData & Keys.KeyCode) == Keys.Home || (keyData & Keys.KeyCode) == Keys.End)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Up: _scroll--; break;
                case Keys.Down: _scroll++; break;
                case Keys.PageUp: _scroll -= VisibleLines; break;
                case Keys.PageDown: _scroll += VisibleLines; break;
                case Keys.Home: ScrollToTop(); return;
                case Keys.End: ScrollToEnd(); return;
                default: return;
            }
            ClampScroll();
            Invalidate();
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();   // 让方向键/PageUp 生效
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.SetupQuality(g);
            g.Clear(BackColor);

            var lines = Lines;
            int lh = LineH;
            int y = 4 - (_scroll * lh) % Math.Max(1, lh);
            int first = _scroll;

            int right = ClientSize.Width - (_bar != null && _bar.Visible ? _bar.Width + 6 : 6);

            // 长行不折行，超出部分裁掉——折行会让"第几行"失去意义，
            // 而日志是按行排查的。需要看全的行可以复制出去。
            for (int i = first; i < lines.Length; i++)
            {
                if (y > ClientSize.Height) break;
                if (y + lh >= 0)
                {
                    string s = lines[i];
                    Color c = ColorFor(s);
                    using (var br = new SolidBrush(c))
                        g.DrawString(s, Font, br, 4, y);
                }
                y += lh;
            }

            // 底部渐隐：提示下面还有内容
            if (Lines.Length > VisibleLines && _scroll + VisibleLines < Lines.Length)
            {
                var r = new Rectangle(0, ClientSize.Height - 14, ClientSize.Width, 14);
                using (var lg = new LinearGradientBrush(r,
                    Color.FromArgb(0, BackColor), Color.FromArgb(200, BackColor),
                    LinearGradientMode.Vertical))
                    g.FillRectangle(lg, r);
            }
            _ = right;
        }

        /// <summary>
        /// 按内容着色：错误显红、警告显橙，其余按明度分级。
        /// 日志里最需要一眼看到的就是失败，因此颜色只服务于这件事。
        /// </summary>
        private static Color ColorFor(string line)
        {
            if (string.IsNullOrEmpty(line)) return Theme.TextTertiary;
            string l = line.ToLowerInvariant();
            if (l.Contains("fail") || l.Contains("error") || l.Contains("exception") ||
                l.Contains("失败") || l.Contains("错误"))
                return Theme.Danger;
            if (l.Contains("warn") || l.Contains("drop") || l.Contains("underrun") ||
                l.Contains("警告"))
                return Theme.Warning;
            // 时间戳行（以 [ 开头）压暗，让正文更突出
            if (line.Length > 0 && line[0] == '[') return Theme.TextSecondary;
            return Theme.TextTertiary;
        }
    }
}
