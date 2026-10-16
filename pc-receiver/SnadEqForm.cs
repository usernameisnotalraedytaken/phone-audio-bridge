// 均衡器窗口。
//
// 形态参照 Cubase / FabFilter 那一类专业 DSP 插件：
//   上半部是一张大的频响曲线图（节点可直接拖动），
//   下半部是一排控制条（开关、前置、预设、逐段推子、读数）。
// 曲线是视觉主体——插件里"图"就是界面，"控件"只是它的注脚。
//
// 文字一律英文。原因有二：
//   1. Consolas 不含汉字，中英混排时中文会回退到别的字体，字重与行高都不一致；
//   2. 专业插件的界面语言本来就是英文缩写（PREAMP / FLAT / BASS），更利落。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class EqForm : Form
    {
        private readonly Equalizer _eq;
        private readonly Action<string> _log;

        private SpectrumGraph _graph;
        private FlatToggle _enable;
        private FlatToggle _couple;
        private Label _hint;
        private EqFader[] _faders;
        private Label[] _valueLabels;
        private EqFader _preampFader;
        private Label _preampLabel;

        /// <summary>
        /// 把 PREAMP 推子与图区的**绘图矩形**对齐——不是与图区控件对齐。
        ///
        /// 图区控件里包含上下留白：顶部是 dB 刻度文字区，底部是频率刻度文字区，
        /// 这两段**没有图线**。若把推子的全高对到控件全高，推子顶部与底部
        /// 就落在空白区里，滑到那里无法与虚线对应（用户实测发现的）。
        ///
        /// 因此改为：推子的矩形与 PlotRectangle 完全重合——
        /// 相同的高度、相同的 0dB 位置。这样推子行程的每一处都对应图上有线的位置。
        /// </summary>
        private void AlignPreampFader()
        {
            if (_graph == null || _preampFader == null || _graph.Height <= 0) return;

            var plot = _graph.PlotRectangle;
            if (plot.Height <= 1) return;

            // 推子在容器坐标下的矩形 = 图区绘图矩形的位置与高度。
            // 横向仍按 Resize 里算好的位置（faderX），这里只改纵向。
            var want = new Rectangle(
                _preampFader.Left,
                _graph.Top + (int)Math.Round(plot.Top),
                _preampFader.Width,
                Math.Max(20, (int)Math.Round(plot.Height)));

            if (_preampFader.Bounds != want) _preampFader.Bounds = want;

            // 量程铺满自身高度，因此几何中心即 0dB，与绘图区 0 线重合。
            // 范围直接引用 Equalizer 的常量，两边不可能再不一致
            // （曾经推子 ±18 而 EQ 内部钳到 [-24,+12]，推到底曲线却不动）。
            _preampFader.SetFullRange(Equalizer.PreampMinDb, Equalizer.PreampMaxDb);
        }

        /// <summary>曲线图。检查工具用它模拟拖动节点，验证与推子的联动。</summary>
        internal SpectrumGraph Graph { get { return _graph; } }

        /// <summary>推子数组。检查工具用它模拟拖动推子，验证与曲线的联动。</summary>
        internal EqFader[] Faders { get { return _faders; } }

        /// <summary>数值标签。检查工具用它确认读数已刷新。</summary>
        internal Label[] ValueLabels { get { return _valueLabels; } }

        /// <summary>
        /// 按 Equalizer 当前值刷新推子与读数。
        /// 曲线拖动后由事件回调走这条路径；检查工具也直接调它来验证联动。
        /// </summary>
        internal void SyncFromEqForTest()
        {
            SyncFadersFromEq();
            UpdateReadouts();
            _graph.Invalidate();
        }

        /// <summary>防止联动拖动时事件递归。</summary>
        private bool _adjusting;

        /// <summary>联动强度：相邻频段跟随比例按高斯衰减。sigma=1.1 时紧邻约跟随 66%。</summary>
        private const double CoupleSigma = 1.1;

        public EqForm(Equalizer eq, Action<string> log)
        {
            _eq = eq;
            _log = log;

            // 确保滤波器已按某个采样率算过系数。
            // Equalizer 有 48000 的默认值，因此这里通常已是可用的；
            // 但接收器若用 44100，MainForm 会在打开本窗口前先 Configure 成实际值，
            // 那时这里不能再用默认值覆盖回去——所以只在未配置时才设。
            if (_eq.SampleRate <= 0) _eq.Configure(48000, 2);

            Text = "SNAD  EQUALIZER";
            // 复用主窗口的内嵌图标。不设的话标题栏是系统默认图标，
            // 与主窗口看起来像两个不相干的程序。
            Icon = LoadAppIcon();
            BackColor = Theme.BgBase;
            ForeColor = Theme.TextPrimary;
            Font = Theme.Mono(9f);
            // FixedToolWindow 会按内容自动收缩并绕过 ClientSize，导致最右控件被裁
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            BuildUi();
            SyncFromEq();
        }

        /// <summary>
        /// 从 exe 自身内嵌资源取出应用图标（与主窗口同一套逻辑）。
       /// 优先取嵌入的多尺寸资源，取不到再退回 ExtractAssociatedIcon。
        /// </summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("AppIcon.ico"))
                    if (s != null) return new System.Drawing.Icon(s);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("EQ：加载嵌入图标失败：" + ex.Message);
            }
            try
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (ico != null) return ico;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("EQ：提取关联图标失败：" + ex.Message);
            }
            return SystemIcons.Application;
        }

        private void BuildUi()
        {
            SuspendLayout();

            int bandCount = _eq.Bands.Count;
            const int colW = 62;
            const int faderH = 100;
            // 标签高度按字体实测，不能写死。
            // 写死 16/18 时，中文与带下伸部的字形（如 "1.8k" 的 k）
            // 下沿会被 Label 边界切掉——实测多行读数下缘都被截断。
            int readoutH = Theme.Metric(9f).Height + 4;
            int freqH = Theme.Label(8f).Height + 4;
            const int graphH = 300;

            // ---- 曲线图（视觉主体）----
            _graph = new SpectrumGraph(new SpectrumAnalyzer())
            {
                Eq = _eq,
                ShowSpectrum = false,      // 插件里只画 EQ 曲线与栅格，不叠频谱
                AllowBandDrag = true
            };
            // dB 轴改为 ±18：EQ 增益只有 ±12dB，沿用频谱的 0~-96 会把曲线
            // 挤在顶部一条窄带里，形状完全看不出来
            _graph.UseEqAxis(18.0);
            _graph.BandChanged += (b, g) =>
            {
                // 曲线是数据源，推子必须跟着走——否则曲线在变、推子不动，
                // 两者看起来是解耦的（实测正是如此：只刷新了读数，漏了推子）。
                SyncFadersFromEq();
                UpdateReadouts();
                UpdateHint();
            };
            _graph.BandReset += b =>
            {
                _log("Band " + _eq.Bands[b].Label + " reset to 0");
                SyncFadersFromEq();
                UpdateReadouts();
                UpdateHint();
            };

            var graphHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = graphH,
                BackColor = Theme.BgBase,
                Padding = new Padding(10, 10, 10, 4)
            };
            graphHost.Controls.Add(_graph);

            // ---- 图区右侧的 PREAMP 推子 ----
            // 它表达的是"整条曲线上下平移"，因此必须与图区的 0dB 虚线严格对齐：
            // 推子的 0dB 位置 = 图区 0 线的像素位置。
            // 这靠 SetPositionalScale 指定绝对位置实现——
            // 若让推子把自己的量程铺满整高，两者永远对不齐。
            _preampFader = new EqFader
            {
                Width = 56,
                BackColor = Theme.BgBase
            };
            _preampFader.SetRange(-18, 18);
            _preampFader.GainDb = _eq.PreampDb;
            _preampFader.ValueChanged += (s, e) =>
            {
                _eq.PreampDb = _preampFader.GainDb;
                _preampLabel.Text = "PREAMP  " + _preampFader.GainDb.ToString("+0.0;-0.0;0.0") + " dB";
                _graph.Invalidate();
                UpdateHint();
            };
            graphHost.Controls.Add(_preampFader);

            // 不依赖 Dock=Fill：嵌套容器里的 Fill 子控件实测拿不到尺寸（0x0），
            // 因此显式按宿主客户区摆放。这与主界面日志区用的是同一套办法。
            graphHost.Resize += (s, e) =>
            {
                int padL = graphHost.Padding.Left;
                int padT = graphHost.Padding.Top;
                int padR = graphHost.Padding.Right;
                int padB = graphHost.Padding.Bottom;
                int innerH = Math.Max(10, graphHost.ClientSize.Height - padT - padB);

                // 推子占右侧固定宽度，图区吃掉其余
                const int faderW = 56, gap = 10;
                int faderX = graphHost.ClientSize.Width - padR - faderW;
                _preampFader.Bounds = new Rectangle(faderX, padT, faderW, innerH);

                _graph.Bounds = new Rectangle(padL, padT,
                    Math.Max(60, faderX - gap - padL), innerH);

                AlignPreampFader();
            };

            // ---- 控制条 1：开关 + 前置 ----
            //
            // 用 FlowLayoutPanel 排，不手工累加 x 坐标。
            // 手工累加时间距是我拍脑袋的常数（+16 / +22），
            // 结果几个控件贴在一起很局促；交给布局引擎后
            // 只需给一个统一的间距值，也不会因为某个控件改宽度而错位。
            var bar1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = Theme.BgPanel,
                Padding = new Padding(14, 12, 14, 12),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false
            };

            _enable = new FlatToggle("EQ ON") { Height = 26, Margin = new Padding(0, 0, 0, 0) };
            _enable.FitToContent();
            _enable.CheckedChanged += (s, e) =>
            {
                _eq.Enabled = _enable.Checked;
                _graph.Invalidate();
                _log("Equalizer " + (_enable.Checked ? "enabled" : "disabled"));
            };
            bar1.Controls.Add(_enable);

            _couple = new FlatToggle("LINK") { Height = 26, Margin = new Padding(26, 0, 0, 0) };
            _couple.FitToContent();
            _couple.CheckedChanged += (s, e) =>
                _log("Linked drag " + (_couple.Checked ? "on" : "off"));
            bar1.Controls.Add(_couple);

            // PREAMP 改为图区右侧的推子（见下方 "控制条 1b"）。
            // 语义上它就是"整条曲线上下平移"，用推子直接表达位移，
            // 而且可以和 0dB 虚线严格对齐——比填数字直观。
            // 这里只留一个读数标签，数值随推子更新。
            _preampLabel = new Label
            {
                Text = "PREAMP  0.0 dB",
                ForeColor = Theme.TextTertiary,
                Font = Theme.Label(8f),
                AutoSize = true,
                Margin = new Padding(30, 6, 0, 0)
            };
            bar1.Controls.Add(_preampLabel);

            // ---- 控制条 2：预设（等宽平铺，不留缺口）----
            var presets = new (string Name, double[] Gains)[]
            {
                ("FLAT",    new double[10]),
                ("BASS",    new double[] {  7,  5,  3,  1,  0,  0,  0,  0,  0,  0 }),
                ("TREBLE",  new double[] {  0,  0,  0,  0,  0,  1,  3,  4,  5,  5 }),
                ("VOCAL",   new double[] { -2, -1,  1,  3,  4,  3,  1,  0, -1, -1 }),
                ("NIGHT",   new double[] { -3, -2,  0,  2,  3,  2,  0, -2, -4, -5 }),
                ("CUTLOW",  new double[] { -8, -6, -3,  0,  0,  0,  0,  0,  0,  0 }),
            };

            int contentW = 14 + bandCount * colW + 14;
            int presetAreaW = contentW - 28;      // 两侧各 14
            int btnGap = 8;
            int btnW = (presetAreaW - btnGap * (presets.Length - 1)) / presets.Length;

            // 预设行同样交给布局引擎，间距统一由 Margin 决定
            var bar2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = Theme.BgBase,
                Padding = new Padding(14, 10, 14, 10),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            foreach (var p in presets)
            {
                var btn = new FlatButton(p.Name)
                {
                    Width = btnW,
                    Height = 28,
                    Margin = new Padding(0, 0, btnGap, 0)
                };
                btn.Tag = p.Gains;
                btn.Click += OnPresetClicked;
                bar2.Controls.Add(btn);
            }

            // ---- 控制条 3：逐段推子 + 读数 ----
            // 刻度由上方曲线图承担，推子上不再重复画刻度线——那是噪音。
            var faderRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = faderH + readoutH + freqH + 18,
                BackColor = Theme.BgPanel,
                Padding = new Padding(12, 6, 12, 6)
            };
            _faders = new EqFader[bandCount];
            _valueLabels = new Label[bandCount];

            for (int i = 0; i < bandCount; i++)
            {
                int x = 12 + i * colW;
                var band = _eq.Bands[i];

                var fd = new EqFader
                {
                    Width = colW - 8,
                    Height = faderH,
                    Location = new Point(x + 4, 6),
                    Tag = i
                };
                fd.SetRange(-12, 12);
                fd.GainDb = band.GainDb;
                fd.ValueChanged += OnFaderChanged;
                faderRow.Controls.Add(fd);
                _faders[i] = fd;

                var vl = new Label
                {
                    Text = "0.0",
                    ForeColor = Theme.TextTertiary,
                    Font = Theme.Metric(9f),
                    Width = colW,
                    Height = readoutH,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(x, 6 + faderH + 2)
                };
                faderRow.Controls.Add(vl);
                _valueLabels[i] = vl;

                var fl = new Label
                {
                    Text = band.Label,
                    ForeColor = Theme.TextTertiary,
                    Font = Theme.Label(8f),
                    Width = colW,
                    Height = freqH,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(x, 6 + faderH + readoutH + 1)
                };
                faderRow.Controls.Add(fl);
            }

            // ---- 底部提示 ----
            var hintHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBase, Padding = new Padding(12, 6, 12, 8) };
            _hint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextTertiary,
                Font = Theme.Metric(8.5f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            hintHost.Controls.Add(_hint);

            // 添加顺序必须"从内到外"：Fill 最先加，之后才是各条 Top。
            // WinForms 里后添加的控件先占边缘，若把 Fill 留到最后，
            // 它会先占满整块，随后加入的 Top 条反而把它的空间挤掉——
            // 实测把图区放在最后加，图区尺寸直接是 0x0。
            Controls.Add(graphHost);
            Controls.Add(hintHost);
            Controls.Add(faderRow);
            Controls.Add(bar2);
            Controls.Add(bar1);

            ClientSize = new Size(contentW, graphH + 52 + 50 + (faderH + readoutH + freqH + 18) + 32);
            MinimumSize = new Size(ClientSize.Width + 16, ClientSize.Height + 40);

            ResumeLayout(true);
        }

        private void OnPresetClicked(object sender, EventArgs e)
        {
            var btn = sender as FlatButton;
            if (btn == null) return;
            var gains = btn.Tag as double[];
            if (gains == null) return;

            _eq.SetAllGains(gains);
            SyncFromEq();
            _log("Preset: " + btn.Text);
        }

        /// <summary>
        /// 推子变动。
        ///
        /// 联动模式下以被拖频段为中心，相邻频段按高斯权重跟随，形成平滑的调整曲线。
        /// 拖动会连续触发本事件，因此批量设值后统一重算一次系数——
        /// 若每段各自重算，10 个 biquad 的三角函数会被反复调用上百次。
        /// </summary>
        private void OnFaderChanged(object sender, EventArgs e)
        {
            if (_adjusting) return;      // 由同步方法设置推子时，忽略回调
            var fd = sender as EqFader;
            if (fd == null) return;

            int idx = (int)fd.Tag;
            double delta = fd.GainDb - _eq.Bands[idx].GainDb;
            if (Math.Abs(delta) < 1e-9) return;

            int n = _eq.Bands.Count;
            for (int j = 0; j < n; j++)
            {
                double w;
                if (j == idx) w = 1.0;
                else if (_couple.Checked)
                {
                    double d = j - idx;
                    w = Math.Exp(-(d * d) / (2.0 * CoupleSigma * CoupleSigma));
                }
                else w = 0.0;

                if (w <= 0.01) continue;
                _eq.SetGainNoRebuild(j, _eq.Bands[j].GainDb + delta * w);
            }
            _eq.RebuildAllFilters();

            // 联动会改到相邻频段，那些推子的位置与曲线都要跟上。
            // 早先在这里来回翻 _adjusting 来绕过守卫、逐个补设推子，
            // 是补丁摞补丁；统一走一次同步即可。
            SyncFadersFromEq();
            UpdateReadouts();
            _graph.Invalidate();
            UpdateHint();
        }

        /// <summary>把所有推子按 Equalizer 的当前值摆好。不再触发回调。</summary>
        private void SyncFadersFromEq()
        {
            _adjusting = true;
            try
            {
                for (int i = 0; i < _faders.Length && i < _eq.Bands.Count; i++)
                    _faders[i].GainDb = _eq.Bands[i].GainDb;
            }
            finally
            {
                _adjusting = false;
            }
        }

        /// <summary>把 Equalizer 的当前状态同步到所有控件与图上。</summary>
        private void SyncFromEq()
        {
            SyncFadersFromEq();
            if (_preampFader != null && Math.Abs(_preampFader.GainDb - _eq.PreampDb) > 1e-9)
                _preampFader.GainDb = _eq.PreampDb;
            if (_preampLabel != null)
                _preampLabel.Text = "PREAMP  " + _eq.PreampDb.ToString("+0.0;-0.0;0.0") + " dB";
            _enable.Checked = _eq.Enabled;
            UpdateReadouts();
            _graph.Invalidate();
            UpdateHint();
        }

        /// <summary>刷新数值标签。有增益的段用强调色，全平的段压暗——一眼看出哪些段动过。</summary>
        private void UpdateReadouts()
        {
            for (int i = 0; i < _valueLabels.Length; i++)
            {
                double g = _eq.Bands[i].GainDb;
                _valueLabels[i].Text = g.ToString("+0.0;-0.0;0.0");
                _valueLabels[i].ForeColor = Math.Abs(g) < 0.05 ? Theme.TextTertiary : Theme.Accent;
            }
        }

        /// <summary>
        /// 提示总增益是否过大。多段同时提升会让峰值超出满量程而被限幅，
        /// 此时应降低前置增益——把这个判断直接说出来，而不是让人自己试。
        /// </summary>
        private void UpdateHint()
        {
            if (_hint == null || _eq == null) return;

            double maxBoost = 0;
            for (int i = 0; i < _eq.Bands.Count; i++)
                if (_eq.Bands[i].GainDb > maxBoost) maxBoost = _eq.Bands[i].GainDb;
            double headroom = -_eq.PreampDb;

            if (maxBoost > 3 && headroom < maxBoost - 3)
            {
                _hint.Text = "MAX BOOST " + maxBoost.ToString("F1") + " dB  ->  " +
                             "SET PREAMP TO -" + Math.Max(0, maxBoost - 3).ToString("F1") +
                             " dB TO AVOID CLIPPING";
                _hint.ForeColor = Theme.Warning;
            }
            else
            {
                _hint.Text = _couple.Checked
                    ? "DRAG NODES ON THE CURVE OR THE FADERS   |   LINK ON: NEIGHBOURING BANDS FOLLOW"
                    : "DRAG NODES ON THE CURVE OR THE FADERS   |   RIGHT-CLICK OR DOUBLE-CLICK A NODE TO RESET";
                _hint.ForeColor = Theme.TextTertiary;
            }
        }
    }
}
