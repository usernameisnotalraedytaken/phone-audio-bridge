// 日志弹窗。
//
// 主界面底部只留一行状态（LOG  [+ N LINES]），点击弹出本窗口看全文。
//
// 为什么这么改：日志是排查问题用的，正常使用时它占掉整块屏幕面积，
// 而那块面积本该显示左右声道电平——那是音频工具的核心信息。
// 需要看日志时再打开，与均衡器窗口是同一个模式。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class LogForm : Form
    {
        private readonly Func<string> _source;
        private LogView _view;
        private Label _count;
        private string _lastText = "";
        private bool _atEnd = true;

        public LogForm(Func<string> source)
        {
            _source = source;

            Text = "SNAD  LOG";
            Icon = AppIcon.Load();
            BackColor = Theme.BgBase;
            StartPosition = FormStartPosition.CenterParent;
            // 固定边框 + MinimumSize：FixedToolWindow 会自动收缩并绕过 ClientSize，
            // 导致右侧内容被裁（踩过一次）。
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(560, 320);
            ClientSize = new Size(760, 460);
            ShowInTaskbar = false;
            KeyPreview = true;

            // ---- 顶部：说明 + 行数 + 操作 ----
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Theme.BgPanel,
                Padding = new Padding(14, 12, 14, 12),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            var hint = new Label
            {
                Text = "READ-ONLY",
                ForeColor = Theme.TextTertiary,
                Font = Theme.Label(8f),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            bar.Controls.Add(hint);

            _count = new Label
            {
                Text = "0 LINES",
                ForeColor = Theme.TextSecondary,
                Font = Theme.Label(8f),
                AutoSize = true,
                Margin = new Padding(24, 6, 0, 0)
            };
            bar.Controls.Add(_count);

            var scrollBtn = new FlatButton("BOTTOM")
            {
                Height = 26,
                Margin = new Padding(24, 0, 0, 0)
            };
            scrollBtn.Click += (s, e) => { if (_view != null) _view.ScrollToEnd(); };
            bar.Controls.Add(scrollBtn);
            scrollBtn.Width = 88;

            var copyBtn = new FlatButton("COPY ALL")
            {
                Height = 26,
                Width = 88,
                Margin = new Padding(8, 0, 0, 0)
            };
            copyBtn.Click += (s, e) => CopyAll();
            bar.Controls.Add(copyBtn);

            // ---- 主体 ----
            var host = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgBase,
                Padding = new Padding(10)
            };
            _view = new LogView { BackColor = Theme.BgBase };
            host.Controls.Add(_view);
            // 显式摆放，不依赖 Dock=Fill（嵌套容器里 Fill 实测拿不到尺寸）
            host.Resize += (s, e) =>
            {
                _view.Bounds = new Rectangle(
                    host.Padding.Left, host.Padding.Top,
                    Math.Max(10, host.ClientSize.Width - host.Padding.Horizontal),
                    Math.Max(10, host.ClientSize.Height - host.Padding.Vertical));
            };

            Controls.Add(host);
            Controls.Add(bar);

            // Esc 关闭；Ctrl+C 复制全部
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.C) { CopyAll(); e.Handled = true; }
            };

            Load += (s, e) => Refresh_();
            // 定时刷新：日志由接收线程追加，弹窗打开期间也要持续可见
            var timer = new Timer { Interval = 500 };
            timer.Tick += (s, e) => { _atEnd = _view != null && _view.IsAtEnd; Refresh_(); };
            timer.Start();
            FormClosed += (s, e) => { timer.Stop(); timer.Dispose(); };
        }

        private void Refresh_()
        {
            if (_view == null || _source == null) return;
            try
            {
                string all = _source() ?? "";
                // 只在内容真的变化时重设，避免每次刷新都重置滚动位置
                if (all != _lastText)
                {
                    bool wasAtEnd = _atEnd;
                    _lastText = all;
                    _view.Text2 = all;
                    if (wasAtEnd) _view.ScrollToEnd();
                }
                if (_count != null) _count.Text = _view.LineCount + " LINES";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("刷新日志失败：" + ex.Message);
            }
        }

        private void CopyAll()
        {
            try
            {
                string t = _source != null ? _source() : "";
                if (!string.IsNullOrEmpty(t)) Clipboard.SetText(t);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("复制日志失败：" + ex.Message);
            }
        }
    }

    /// <summary>应用图标加载。主窗口与各弹窗共用，避免每个窗口各写一份。</summary>
    internal static class AppIcon
    {
        private static Icon _cached;

        public static Icon Load()
        {
            if (_cached != null) return _cached;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("AppIcon.ico"))
                    if (s != null) { _cached = new Icon(s); return _cached; }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("加载嵌入图标失败：" + ex.Message);
            }
            try
            {
                var ico = Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (ico != null) { _cached = ico; return _cached; }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("提取关联图标失败：" + ex.Message);
            }
            _cached = SystemIcons.Application;
            return _cached;
        }
    }
}
