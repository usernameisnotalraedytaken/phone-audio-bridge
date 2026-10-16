// 主窗口。
//
// 版面按 K 方向分区：上半屏是频谱图（既是可视化也是 EQ 控件），
// 下半屏是通道条读数（六项指标横排），再下面是控件行与事件日志。
//
// 设计语言统一由 Theme 提供，本文件不出现散落的颜色字面量——
// 否则改一处配色要翻遍全文，而且很容易漏。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class MainForm : Form
    {
        // ---- 控件 ----
        private WheelSelector _profileBox;
        private WheelSelector _deviceBox;
        private FlatButton _startBtn;
        private FlatButton _eqBtn;
        private SpectrumGraph _graph;
        private MetricStrip _metrics;
        private Panel _logBar;                 // 底部一行状态，点击弹出日志窗口
        private Label _logCountLabel;
        private readonly System.Text.StringBuilder _logBuffer = new System.Text.StringBuilder();
        private StereoMeter _stereo;
        private ChannelStrip[] _channelStrips;
        private Panel _channelHost;
        private NotifyIcon _tray;
        private Panel _titleBar;
        private Panel _graphHost;
        private Panel _controlsRow;
        private Wordmark _wordmark;
        private Label _stateLabel, _rateLabel;
        private Panel _dot;

        // ---- 状态 ----
        private Dictionary<string, Profile> _profiles;
        private string _cfgPath;
        private List<DeviceInfo> _devices;
        private int _deviceIndex = -1;

        private SnadReceiver _receiver;
        private Thread _rxThread;
        private CancellationTokenSource _runCts;
        private Profile _active;

        /// <summary>信标广播器。手机端据此自动发现本机，无需手填地址。</summary>
        private BeaconBroadcaster _beacon;

        private readonly SpectrumAnalyzer _analyzer = new SpectrumAnalyzer();
        private readonly Equalizer _idleEq = new Equalizer();
        private readonly object _audioGate = new object();
        private byte[] _pendingAudio;
        private int _pendingBytes, _pendingRate, _pendingCh;

        private System.Windows.Forms.Timer _uiTimer;

        /// <summary>闪烁状态：欠载或丢帧时状态点闪烁，静止的灯容易被忽略。</summary>
        private bool _blinkOn = true;
        private int _blinkTick;

        public MainForm()
        {
            Text = "SNAD  Audio Bridge  -  Receiver";
            Icon = LoadAppIcon();
            BackColor = Theme.BgBase;
            ForeColor = Theme.TextPrimary;
            Font = Theme.Ui(9f);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            // 自绘控件多，窗口缩放时整屏重画会有闪烁，用合成绘制压掉
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            // 尺寸按屏幕工作区自适应。
            // 本机实测工作区 1280x720（PerMonitor DPI 感知），布局需要
            // 标题 40 + 图 300 + 通道条 88 + 控件行 52 + 日志 110 + 边距 ≈ 630。
            var wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1280, 800);
            int wantW = 960, wantH = 640;
            int w = Math.Min(wantW, Math.Max(720, wa.Width - 20));
            int h = Math.Min(wantH, Math.Max(480, wa.Height - 20));
            MinimumSize = new Size(Math.Min(720, wa.Width), Math.Min(480, wa.Height));
            Size = new Size(w, h);

            BuildUi();
            LoadConfigAndDevices();
        }

        /// <summary>
        /// 从 exe 自身内嵌资源取出应用图标。
        ///
        /// 说明：/win32icon 只是把图标写进 exe 资源（供资源管理器显示），
        /// WinForms 窗口并不会自动采用它，必须显式赋给 Form.Icon 与 NotifyIcon.Icon。
        /// </summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted != null) return extracted;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("提取应用图标失败: " + ex.Message);
            }
            return SystemIcons.Application;
        }

        // ------------------------------------------------------------------ 界面构建

        private void BuildUi()
        {
            SuspendLayout();

            // ---- 标题条 ----
            _titleBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.BgPanel };
            // 标题字标。自绘控件，字距、字重对比与声波标记都在里面，
            // 位置也由它自己按实测宽度排布——不再有写死坐标导致的遮挡。
            _wordmark = new Wordmark
            {
                MainText = "SNAD",
                SubText = "AUDIO BRIDGE",
                Location = new Point(14, 3),
                Height = 34
                // 宽度不写死：控件在句柄建立后按内容自动撑开（FitToContent）。
                // 曾经写死 240 而实际需要约 255，副题被截成 "AUDIO BR"。
            };
            // 状态点：正常绿、异常橙，异常时闪烁
            _dot = new Panel { Size = new Size(7, 7), BackColor = Theme.TextTertiary, Location = new Point(0, 17) };
            _stateLabel = new Label
            {
                Text = "IDLE",
                ForeColor = Theme.TextSecondary,
                Font = Theme.Metric(9f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(0, 11)
            };
            _rateLabel = new Label
            {
                Text = "",
                ForeColor = Theme.TextPrimary,
                Font = Theme.Metric(9f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(0, 11)
            };
            _titleBar.Controls.AddRange(new Control[] { _wordmark, _dot, _stateLabel, _rateLabel });
            _titleBar.Resize += (s, e) => LayoutTitleBar();

            // ---- 频谱图 ----
            // Dock=Fill 让图吃掉标题条、通道条、控件行与日志之外的全部高度——
            // 图是视觉主体，应该占最大面积且随窗口缩放。
            _graph = new SpectrumGraph(_analyzer)
            {
                Dock = DockStyle.Fill,
                Eq = _idleEq
            };
            _graph.BandChanged += OnGraphBandChanged;
            _graph.BandReset += OnGraphBandReset;

            var graphHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 6), BackColor = Theme.BgBase };
            graphHost.Controls.Add(_graph);

            // ---- 通道条 ----
            _metrics = new MetricStrip(6) { Dock = DockStyle.Top, Height = 88 };
            BuildMetricLabels();

            // ---- 控件行 ----
            // 高度按滚轮选择器算：可见 3 行 × 行高（字体高 + 8）≈ 78，
            // 上下各留 8 的边距，取 92。给矮了会把滚轮压扁并与日志重叠。
            var controls = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.BgBase, Padding = new Padding(12, 8, 12, 8) };

            // 档位与设备用滚轮选择器：收起时只占一行显示当前值，
            // 点击展开成滚轮（iOS 时间选择器形态），选完自动收起。
            _profileBox = new WheelSelector { Width = 150, Location = new Point(12, 10) };
            _profileBox.SelectedIndexChanged += OnProfileChanged;
            // 展开/收起会改变高度，需要重新垂直居中，否则会偏出中心或被容器裁掉
            _profileBox.Resize += (s, e) => { LayoutControlRow(); EnsureRowTallEnough(); };

            _deviceBox = new WheelSelector { Width = 240, Location = new Point(172, 10) };
            _deviceBox.SelectedIndexChanged += OnDeviceChanged;
            _deviceBox.Resize += (s, e) => { LayoutControlRow(); EnsureRowTallEnough(); };

            _startBtn = new FlatButton("START", true) { Width = 88, Height = 32, Location = new Point(0, 10) };
            _startBtn.Click += OnStartStop;

            _eqBtn = new FlatButton("EQ") { Width = 88, Height = 32, Location = new Point(0, 10) };
            _eqBtn.Click += OnOpenEq;

            controls.Controls.AddRange(new Control[] { _profileBox, _deviceBox, _startBtn, _eqBtn });
            controls.Resize += (s, e) => LayoutControlRow();

            // ---- 底部日志状态行 ----
            // 日志从"占一整块区域的可折叠面板"改为"一行状态 + 弹窗看全文"。
            // 原因：日志是排查用的，正常使用时不该占掉屏幕面积，
            // 而那块面积本该显示左右声道电平——那是音频工具的核心信息。
            _logBar = new Panel { BackColor = Theme.BgPanel, Cursor = Cursors.Hand };
            _logCountLabel = new Label
            {
                Text = "LOG  0 LINES",
                ForeColor = Theme.TextTertiary,
                Font = Theme.Label(8f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(12, 0)
            };
            _logBar.Controls.Add(_logCountLabel);
            _logBar.Click += (s, e) => OpenLogWindow();
            _logCountLabel.Click += (s, e) => OpenLogWindow();

            // ---- 声道条区域：左右声道的波形与电平 ----
            _stereo = new StereoMeter();
            _channelHost = new Panel { BackColor = Theme.BgPanel };
            _channelStrips = new ChannelStrip[StereoMeter.Channels];
            for (int c = 0; c < _channelStrips.Length; c++)
            {
                _channelStrips[c] = new ChannelStrip(c, _stereo)
                {
                    ChannelName = c == 0 ? "L" : "R"
                };
                _channelHost.Controls.Add(_channelStrips[c]);
            }

            // ---- 组装 ----
            // 不用 Dock 排版面：WinForms 的停靠顺序由控件在集合里的次序决定，
            // 实测 BringToFront 无法可靠改变布局顺序，通道条会跑到图上面
            // （与 K 方向相反）。这里改为显式摆放，位置全部由 LayoutAll 算出。
            Controls.Add(graphHost);
            Controls.Add(_channelHost);
            Controls.Add(_logBar);
            Controls.Add(controls);
            Controls.Add(_metrics);
            Controls.Add(_titleBar);

            _graphHost = graphHost;
            _controlsRow = controls;

            // 每一块都不再自行停靠，统一由 LayoutAll 定位
            _titleBar.Dock = DockStyle.None;
            _metrics.Dock = DockStyle.None;
            controls.Dock = DockStyle.None;
            graphHost.Dock = DockStyle.None;
            _channelHost.Dock = DockStyle.None;
            _logBar.Dock = DockStyle.None;

            Resize += (s, e) => LayoutAll();
            LayoutAll();

            // 点击滚轮以外的地方就收起。
            //
            // 注意不能只挂窗体的 MouseDown：客户区被各子面板铺满，
            // 点击落在子控件上，事件不会冒泡到窗体——实测怎么点都收不起来。
            // 因此递归挂到所有子控件。
            HookDismiss(this);

            // ---- 托盘 ----
            _tray = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "SNAD Audio Bridge",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show window", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("Quit", null, (s, e) => { StopReceiver(); _tray.Visible = false; Application.Exit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

            _uiTimer = new System.Windows.Forms.Timer { Interval = 33 };   // ≈30fps
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            ResumeLayout(true);
            LayoutTitleBar();
            LayoutControlRow();
        }

        /// <summary>通道条的六项指标：固定标签与悬浮说明，数值由 OnUiTick 填。</summary>
        private void BuildMetricLabels()
        {
            string[] labels = { "LATENCY", "BUFFER", "RX", "TX", "DROP", "UNDER" };
            string[] units = { "ms", "ms", "fps", "fps", "frames", "underruns" };
            string[] tips =
            {
                "Estimated end-to-end latency: capture + network + jitter buffer + audio block. Excludes sound card latency.",
                "Jitter buffer level. Near capacity starts dropping frames; near zero causes underruns.",
                "Frame rate arriving from the phone. Should match the sample rate.",
                "Frame rate actually written to the sound card. Persistently below RX means the consumer cannot keep up.",
                "Frames dropped from buffer overflow. Non-zero means the level is too high or bursts are too large.",
                "Underruns from an empty buffer. Non-zero means the level is too low or scheduling jitter is high."
            };
            for (int i = 0; i < _metrics.Count; i++)
            {
                _metrics[i].Label = labels[i];
                _metrics[i].Unit = units[i];
                _metrics[i].Tip = tips[i];
                _metrics[i].Value = "—";
                _metrics[i].Bar = -1;
                // RX/TX 是"达到采样率才好"，其余是越低越好
                _metrics[i].HigherIsWorse = (i != 2 && i != 3);
            }
        }

        /// <summary>
        /// 显式排版整个窗口。
        ///
        /// 为什么不用 Dock：WinForms 的停靠结果取决于控件在集合里的次序，
        /// 实测 BringToFront 也无法可靠改变布局顺序，导致通道条跑到图上方。
        /// 显式摆放虽然啰嗦，但结果完全可预测，也便于在检查工具里断言。
        ///
        /// 纵向分配：标题 40 / 图（弹性，最少 160）/ 通道条 88 / 控件行 52 / 日志（弹性，最少 90）。
        /// </summary>
        private void LayoutAll()
        {
            if (_titleBar == null || IsDisposed) return;

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            // 指标条高度由控件自己算（字体随 DPI 变化，写死的数会在高 DPI 下压行）
            int metricsH = _metrics != null ? _metrics.PreferredHeight : 92;
            const int titleH = 40, controlsH = 92;   // controlsH 要容下滚轮的三行
            const int logBarH = 26, graphMinH = 130, channelMinH = 64, channelWantH = 104;

            // 纵向分配（自上而下）：
            //   标题 / 频谱图(弹性) / 指标条 / 声道条 / 控制行 / 日志状态行
            // 图与声道条共享弹性空间。声道条至少 64px——它是主要信息来源，
            // 挤到 30px 时波形与电平表都看不清，等于白放。
            int fixedH = titleH + metricsH + controlsH + logBarH;
            int flexible = h - fixedH;

            // 声道条优先拿够：它是主界面里唯一的实时音频信息，
            // 图形与柱状表都需要纵向空间才看得清。
            int channelH = Math.Min(channelWantH, Math.Max(channelMinH, flexible * 2 / 5));
            if (channelH < channelWantH) channelH = Math.Max(channelMinH, flexible / 2);
            int graphH = flexible - channelH;
            if (graphH < graphMinH)
            {
                graphH = graphMinH;
                channelH = Math.Max(0, flexible - graphH);
            }

            int y = 0;
            _titleBar.Bounds = new Rectangle(0, y, w, titleH);              y += titleH;
            _graphHost.Bounds = new Rectangle(0, y, w, Math.Max(10, graphH)); y += Math.Max(10, graphH);
            _metrics.Bounds = new Rectangle(0, y, w, metricsH);             y += metricsH;
            _channelHost.Bounds = new Rectangle(0, y, w, channelH);         y += channelH;
            _controlsRow.Bounds = new Rectangle(0, y, w, controlsH);        y += controlsH;
            _logBar.Bounds = new Rectangle(0, y, w, Math.Max(10, h - y));

            LayoutTitleBar();
            LayoutControlRow();
            LayoutChannels();
        }

        /// <summary>
        /// 声道条布局：左右两条上下堆叠。
        /// 横向每条都占满宽度，波形因此可以拉长，看形状更清楚。
        /// </summary>
        private void LayoutChannels()
        {
            if (_channelHost == null || _channelStrips == null) return;

            int n = _channelStrips.Length;
            int w = _channelHost.ClientSize.Width;
            int h = _channelHost.ClientSize.Height;
            const int pad = 8;
            int rowH = Math.Max(20, (h - pad * (n + 1)) / Math.Max(1, n));

            int y = pad;
            for (int i = 0; i < n; i++)
            {
                _channelStrips[i].Bounds = new Rectangle(pad, y,
                    Math.Max(40, w - pad * 2), rowH);
                y += rowH + pad;
            }

            if (_logCountLabel != null && _logBar != null)
                _logCountLabel.Top = Math.Max(0, (_logBar.ClientSize.Height - _logCountLabel.Height) / 2);
        }

        /// <summary>
        /// 打开日志窗口。
        /// 内容从内存缓冲取，而不是从控件读——控件只是视图，
        /// 窗口随时可开可关，内容不能跟着控件走。
        /// </summary>
        private void OpenLogWindow()
        {
            try
            {
                using (var f = new LogForm(GetLogText))
                {
                    f.Icon = LoadAppIcon();
                    f.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Failed to open log window: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private string GetLogText()
        {
            lock (_logBuffer) return _logBuffer.ToString();
        }

        /// <summary>标题条右侧的状态点、状态文字与采样率贴右对齐。</summary>
        private void LayoutTitleBar()
        {
            if (_titleBar == null || _dot == null) return;

            int right = _titleBar.ClientSize.Width - 16;
            int rateW = _rateLabel.PreferredWidth > 0 ? _rateLabel.PreferredWidth : 70;
            _rateLabel.Left = right - rateW;
            _stateLabel.Left = _rateLabel.Left - 8 - _stateLabel.PreferredWidth;
            _dot.Left = _stateLabel.Left - 14;
            _dot.Top = (_titleBar.Height - _dot.Height) / 2;
        }

        /// <summary>控件行：开始与均衡器贴右，档位与设备在左。</summary>
        /// <summary>
        /// 控件行排版。
        ///
        /// 每个控件按自身高度垂直居中，而不是统一 Top——滚轮选择器收起时只有一行高，
        /// 展开时会变高，写死 Top 会让它在展开过程中偏出中心。
        /// 因此在滚轮的 Resize 事件里也调一次本方法。
        /// </summary>
        private void LayoutControlRow()
        {
            if (_startBtn == null || _startBtn.Parent == null) return;
            var host = _startBtn.Parent;
            int right = host.ClientSize.Width - host.Padding.Right;
            int midY = host.ClientSize.Height / 2;

            CenterInRow(_profileBox, midY);
            CenterInRow(_deviceBox, midY);
            CenterInRow(_startBtn, midY);
            CenterInRow(_eqBtn, midY);

            // 右侧两个按钮先定位
            _eqBtn.Left = right - _eqBtn.Width;
            _startBtn.Left = _eqBtn.Left - 8 - _startBtn.Width;

            // 设备滚轮吃掉左侧剩余的全部宽度。
            // 之前写死了 280 上限，结果它与 START 之间留出一大片空白，
            // 而设备名（"Voicemeeter VAIO3 Input (VB-Audio ...)"）却被截断——
            // 该用的宽度没用上。这里改为撑满到按钮左侧。
            int deviceLeft = _profileBox.Left + _profileBox.Width + 12;
            int deviceMax = _startBtn.Left - 12 - deviceLeft;
            _deviceBox.Left = deviceLeft;
            _deviceBox.Width = Math.Max(140, deviceMax);
        }

        private static void CenterInRow(Control c, int midY)
        {
            if (c == null) return;
            c.Top = midY - c.Height / 2;
        }

        /// <summary>控件行必须高到能容下展开的滚轮，否则滚轮会被容器裁掉。</summary>
        private void EnsureRowTallEnough()
        {
            if (_controlsRow == null) return;
            int needed = Math.Max(
                _profileBox != null ? _profileBox.Height : 0,
                _deviceBox != null ? _deviceBox.Height : 0) + 16;
            if (needed > _controlsRow.Height) _controlsRow.Height = needed;
        }

        /// <summary>收起所有展开的滚轮选择器。</summary>
        private void CloseAllWheels()
        {
            if (_profileBox != null && _profileBox.IsOpen) _profileBox.Close();
            if (_deviceBox != null && _deviceBox.IsOpen) _deviceBox.Close();
        }

        /// <summary>
        /// 给窗体与所有子控件挂上"点到别处就收起滚轮"的处理。
        ///
        /// 必须递归：客户区被面板铺满，点击落在子控件上不会冒泡到窗体。
        /// 若点击源本身是滚轮（或滚轮内部的子控件），则不收起。
        /// </summary>
        private void HookDismiss(Control root)
        {
            root.MouseDown += OnAnyMouseDown;
            foreach (Control c in root.Controls)
            {
                // 滚轮自己处理点击（展开/拖动/选择），不再挂
                if (c is WheelSelector) continue;
                HookDismiss(c);
            }
        }

        private void OnAnyMouseDown(object sender, MouseEventArgs e)
        {
            var c = sender as Control;
            // 点击落在某个滚轮内部（例如它内部的子控件）时不收起
            if (c != null && IsInsideWheel(c)) return;
            CloseAllWheels();
        }

        private bool IsInsideWheel(Control c)
        {
            while (c != null)
            {
                if (c is WheelSelector) return true;
                c = c.Parent;
            }
            return false;
        }

        // ------------------------------------------------------------------ 配置与设备

        private void LoadConfigAndDevices()
        {
            string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(exeDir)) exeDir = Environment.CurrentDirectory;
            _cfgPath = Path.Combine(exeDir, "receiver.cfg");
            if (!File.Exists(_cfgPath)) ProfileLibrary.WriteDefault(_cfgPath);

            string selected;
            _profiles = ProfileLibrary.Load(_cfgPath, out selected);

            var names = new List<string>(_profiles.Keys);
            _profileBox.Items = names.ToArray();
            int si = names.IndexOf(selected);
            _profileBox.SelectedIndex = si >= 0 ? si : (names.Count > 0 ? 0 : -1);

            // 把选中档位里保存的均衡器设置套到待用实例上，
            // 这样上次调好的 EQ 在下次启动时自动恢复。
            Profile sel;
            if (_profiles.TryGetValue(selected, out sel))
            {
                ProfileLibrary.ApplyEq(sel, _idleEq);
                _active = sel;
            }

            RefreshDevices();
            AppendLog("Config: " + _cfgPath + "  (" + _profiles.Count + " profiles, selected " + selected + ")");
        }

        private void RefreshDevices()
        {
            _devices = WaveOut.ListDevices();
            string defName = WaveOut.DefaultDeviceName();
            int defIdx = FindDeviceByName(defName);

            var items = new List<string> { "SYSTEM DEFAULT" };
            foreach (var d in _devices) items.Add("[" + d.Index + "] " + d.Name);
            _deviceBox.Items = items.ToArray();

            if (_deviceIndex < 0) _deviceBox.SelectedIndex = 0;
            else _deviceBox.SelectedIndex = Math.Min(items.Count - 1, _deviceIndex + 1);

            AppendLog(defIdx >= 0
                ? "Default output: " + _devices[defIdx].Name
                : "Default output device not identified");
        }


        /// <summary>按名字找设备。默认设备名可能被截断（MMDevice 与 waveOut 的取名方式不同），
        /// 因此先精确匹配再退回前缀匹配。</summary>
        private int FindDeviceByName(string name)
        {
            if (string.IsNullOrEmpty(name) || _devices == null) return -1;
            var m = _devices.Find(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (m.Name != null) return m.Index;
            string pre = name.Substring(0, Math.Min(12, name.Length));
            m = _devices.Find(d => d.Name.StartsWith(pre, StringComparison.OrdinalIgnoreCase));
            return m.Name != null ? m.Index : -1;
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            int sel = _deviceBox.SelectedIndex;
            _deviceIndex = sel <= 0 ? -1 : sel - 1;
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            string name = _profileBox.SelectedItem;
            if (name == null || _profiles == null || !_profiles.ContainsKey(name)) return;

            // 换档位也换调音：不同用途需要不同 EQ（游戏突出脚步、音乐要平直），
            // 因此每个档位各自保存一份均衡器设置。
            var next = _profiles[name];

            // 切走之前先把当前档位的 EQ 存下来，避免调好的设置丢失
            if (_active != null && !ReferenceEquals(_active, next) && _profiles.ContainsKey(_active.Name))
                ProfileLibrary.CaptureEq(_active, _receiver != null ? _receiver.Equalizer : _idleEq);

            _active = next;

            if (_receiver != null)
            {
                AppendLog("Switching profile to " + name + ", restarting...");
                StopReceiver();
                ProfileLibrary.ApplyEq(_active, _idleEq);
                StartReceiver();
            }
            else
            {
                ProfileLibrary.ApplyEq(_active, _idleEq);
                _graph.Invalidate();
            }
        }

        // ------------------------------------------------------------------ 启停

        private void OnStartStop(object sender, EventArgs e)
        {
            if (_receiver == null) StartReceiver();
            else StopReceiver();
        }

        private void StartReceiver()
        {
            if (_active == null)
            {
                string sel = _profileBox.SelectedItem;
                if (sel != null && _profiles.ContainsKey(sel)) _active = _profiles[sel];
            }
            if (_active == null) { MessageBox.Show("No usable profile configuration."); return; }

            // 选设备：-1 表示跟随系统默认；被占用时回退 Voicemeeter Input
            int dev = _deviceIndex;
            if (dev < 0)
            {
                int defIdx = FindDeviceByName(WaveOut.DefaultDeviceName());
                if (defIdx >= 0)
                {
                    IntPtr probe;
                    int rc = WaveOut.ProbeOpen(defIdx, out probe);
                    if (rc == 0)
                    {
                        dev = defIdx;
                        AppendLog("Output: system default [" + defIdx + "] " + _devices[defIdx].Name);
                    }
                    else
                    {
                        var vm = _devices.Find(d => d.Name.IndexOf("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (vm.Name != null)
                        {
                            dev = vm.Index;
                            AppendLog("Default device busy (rc=" + rc + "), falling back to Voicemeeter Input");
                        }
                        else
                        {
                            dev = defIdx;
                            AppendLog("Default device busy and no Voicemeeter Input found, still trying the default");
                        }
                    }
                }
                else
                {
                    dev = 0;
                    AppendLog("Default device not identified, using [0] " + (_devices.Count > 0 ? _devices[0].Name : "?"));
                }
            }
            else
            {
                AppendLog("Output: selected device [" + dev + "] " + _devices[dev].Name);
            }

            _runCts = new CancellationTokenSource();
            _receiver = new SnadReceiver(dev, Program.DefaultPort, _active);
            _receiver.AudioBlockReady += OnAudioBlock;

            // 把"未启动时调好的"均衡器设置复制给接收器，
            // 这样先调 EQ 再点开始也能生效。
            try
            {
                _receiver.Equalizer.SetAllGains(_idleEq.GetGains());
                _receiver.Equalizer.PreampDb = _idleEq.PreampDb;
                _receiver.Equalizer.Enabled = _idleEq.Enabled;
            }
            catch (Exception ex)
            {
                AppendLog("Failed to copy EQ settings: " + ex.Message);
            }

            // 图上的 EQ 直接操作接收器的实例，这样拖动即时生效
            _graph.Eq = _receiver.Equalizer;

            var r = _receiver;
            _rxThread = new Thread(() =>
            {
                try { r.Run(); }
                catch (Exception ex) { BeginInvoke(new Action(() => AppendLog("Receiver thread error: " + ex.Message))); }
            })
            { IsBackground = true, Name = "snad-ui-rx" };
            _rxThread.Start();

            // 启动信标广播：手机端据此自动发现本机，用户无需知道 IP。
            // 失败不阻断接收（仍可手填地址），但必须明确报出来，否则表现为
            // "手机永远找不到电脑"，很难排查。
            try
            {
                var key = SnadBeacon.DeriveKey(SnadSecret.Psk);
                _beacon = new BeaconBroadcaster(Program.MachineDeviceId, Program.DefaultPort, key, 2000,
                    m => BeginInvoke(new Action(() => AppendLog("Beacon: " + m))));
                _beacon.Start();
                AppendLog("Beacon broadcasting: device=" + Program.MachineDeviceId +
                          ", KeyId=" + SnadSecret.KeyId + "  (phone must report the same KeyId)");
            }
            catch (Exception ex)
            {
                AppendLog("Beacon broadcast failed: " + ex.GetType().Name + ": " + ex.Message + "  (phone must enter the address manually)");
            }

            _startBtn.Text = "STOP";
            _eqBtn.Enabled = true;
            AppendLog("Started: profile " + _active.Name + ", estimated link latency ~" +
                      _active.EstimatedPipelineLatencyMs() + " ms (excludes sound card latency)");
            UpdateRunState();
        }

        private void StopReceiver()
        {
            try { if (_receiver != null) _receiver.Stop(); }
            catch (Exception ex) { AppendLog("Error while stopping: " + ex.Message); }
            try { if (_rxThread != null && _rxThread.IsAlive) _rxThread.Join(1500); }
            catch (Exception ex) { AppendLog("Error waiting for thread exit: " + ex.Message); }
            try { if (_receiver != null) _receiver.Dispose(); }
            catch (Exception ex) { AppendLog("Error disposing receiver: " + ex.Message); }

            // 停止信标广播，避免接收停止后手机仍能"发现"一个不再工作的电脑
            try { if (_beacon != null) { _beacon.Dispose(); _beacon = null; } }
            catch (Exception ex) { AppendLog("Error stopping beacon: " + ex.Message); }

            _receiver = null;
            _rxThread = null;
            if (_runCts != null) { _runCts.Dispose(); _runCts = null; }

            // 图回到"待用"实例，保持 EQ 仍可编辑
            _graph.Eq = _idleEq;

            _startBtn.Text = "START";
            AppendLog("Stopped");
            UpdateRunState();
        }

        /// <summary>刷新标题条状态与通道条的未运行态。</summary>
        private void UpdateRunState()
        {
            bool running = _receiver != null;
            _stateLabel.Text = running ? "LIVE" : "IDLE";
            _stateLabel.ForeColor = running ? Theme.Success : Theme.TextTertiary;
            _dot.BackColor = running ? Theme.Success : Theme.TextTertiary;
            _tray.Text = running ? "SNAD Audio Bridge  -  RUNNING" : "SNAD Audio Bridge";

            if (!running)
            {
                for (int i = 0; i < _metrics.Count; i++)
                {
                    _metrics[i].Value = "—";
                    _metrics[i].Bar = -1;
                    _metrics[i].Alert = false;
                }
                _rateLabel.Text = "";
            }
            LayoutTitleBar();
            _metrics.Invalidate();
        }

        // ------------------------------------------------------------------ 均衡器

        /// <summary>图上的 EQ 节点被拖动：写回档位并落盘（拖动结束时）。</summary>
        private void OnGraphBandChanged(int band, double gainDb)
        {
            SaveEqToProfile();
        }

        private void OnGraphBandReset(int band)
        {
            AppendLog("Band " + _idleEq.Bands[band].Label + " reset to 0");
            SaveEqToProfile();
        }

        /// <summary>
        /// 把当前 EQ 设置写回所在档位并落盘。
        /// 只改 eq / eqpreamp / eqgains 三个键，不动配置文件里其他内容与注释。
        /// </summary>
        private void SaveEqToProfile()
        {
            try
            {
                var eq = _receiver != null ? _receiver.Equalizer : _idleEq;
                string name = _profileBox.SelectedItem;
                if (string.IsNullOrEmpty(name)) name = _active != null ? _active.Name : "balanced";

                Profile p;
                if (_profiles != null && _profiles.TryGetValue(name, out p))
                {
                    ProfileLibrary.CaptureEq(p, eq);
                    ProfileLibrary.SaveEq(_cfgPath, name, eq);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Failed to save EQ settings: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 打开独立的均衡器窗口。
        /// 主界面的图上已可直接拖动频段，这个窗口用于预设与前置增益这类
        /// 在图上不好表达的操作。
        /// </summary>
        private void OnOpenEq(object sender, EventArgs e)
        {
            try
            {
                var eq = _receiver != null ? _receiver.Equalizer : _idleEq;

                // 用接收器的实际采样率配置一次，让曲线与实际听到的一致
                // （同一频段在 44.1k 与 48k 下系数不同）。
                // 未开始播放时退回 48k —— EQ 的频响本来就只取决于滤波器参数，
                // 与是否在放音无关，这里只是把"实际值"告诉它。
                int sr = 0;
                try { if (_receiver != null) sr = _receiver.Snapshot().SampleRate; }
                catch (Exception ex) { AppendLog("Read sample rate failed: " + ex.Message); }
                if (sr <= 0) sr = 48000;
                try { eq.Configure(sr, 2); }
                catch (Exception ex) { AppendLog("EQ configure failed: " + ex.Message); }

                using (var f = new EqForm(eq, AppendLog))
                {
                    f.ShowDialog(this);
                }
                SaveEqToProfile();
                _graph.Invalidate();
            }
            catch (Exception ex)
            {
                AppendLog("Failed to open EQ: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ 音频与刷新

        private void OnAudioBlock(byte[] pcm, int sampleRate, int channels)
        {
            // 只在 UI 线程取用最新一块，避免堆积
            lock (_audioGate)
            {
                _pendingAudio = pcm;
                _pendingBytes = pcm.Length;
                _pendingRate = sampleRate;
                _pendingCh = channels;
            }
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            byte[] pcm = null;
            int bytes = 0, rate = 0, ch = 0;
            lock (_audioGate)
            {
                if (_pendingAudio != null)
                {
                    pcm = _pendingAudio;
                    bytes = _pendingBytes; rate = _pendingRate; ch = _pendingCh;
                    _pendingAudio = null;
                }
            }
            if (pcm != null)
            {
                _analyzer.Feed(pcm, bytes, ch, rate);
                if (_stereo != null) _stereo.Feed(pcm, bytes, ch, rate);
                _graph.Invalidate();
                // 声道条按 UI 节拍重绘，不随音频块——音频块远快于刷新率，
                // 每块都重绘会白白占满 CPU。
                if (_channelStrips != null)
                    foreach (var cs in _channelStrips) cs.Invalidate();
            }

            if (_receiver == null) return;

            var m = _receiver.Snapshot();
            UpdateMetrics(m);

            _rateLabel.Text = m.SampleRate > 0
                ? (m.SampleRate / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " kHz"
                : "";

            // 异常时状态点闪烁——静止的指示灯容易被忽略
            bool bad = m.FramesDropped > 0 || m.Underruns > 0;
            _blinkTick++;
            if (bad && _blinkTick % 8 == 0) _blinkOn = !_blinkOn;
            if (!bad) _blinkOn = true;
            _dot.BackColor = bad ? (_blinkOn ? Theme.Warning : Theme.BgPanel)
                                 : Theme.Success;
        }

        /// <summary>把快照填进通道条。阈值与配色语义在这里统一决定。</summary>
        private void UpdateMetrics(SnadReceiver.Metrics m)
        {
            // 延迟：与 Profile 的估算口径一致（采集 + 网络 + 抖动缓冲 + 音频块）
            int pipeline = 21 + 8 + m.TargetMs + m.BlockMs;
            _metrics[0].Value = pipeline.ToString(CultureInfo.InvariantCulture);
            _metrics[0].Unit = "ms";
            _metrics[0].Bar = Math.Min(1.0, pipeline / 400.0);

            // 缓冲水位：以容量为满刻度，接近容量即告警
            double bufRatio = m.RingCapacityFrames > 0
                ? (double)m.BufferFrames / m.RingCapacityFrames : 0;
            _metrics[1].Value = m.BufferMs.ToString(CultureInfo.InvariantCulture);
            _metrics[1].Unit = "ms";
            _metrics[1].Bar = Math.Min(1.0, bufRatio);
            _metrics[1].Alert = bufRatio > 0.9;

            int sr = m.SampleRate > 0 ? m.SampleRate : 48000;
            _metrics[2].Value = ((long)m.RateIn).ToString(CultureInfo.InvariantCulture);
            _metrics[2].Bar = Math.Min(1.0, m.RateIn / sr);

            _metrics[3].Value = ((long)m.RateWritten).ToString(CultureInfo.InvariantCulture);
            _metrics[3].Bar = Math.Min(1.0, m.RateWritten / sr);

            _metrics[4].Value = m.FramesDropped.ToString(CultureInfo.InvariantCulture);
            _metrics[4].Bar = m.FramesDropped == 0 ? 0 : 1.0;
            _metrics[4].Alert = m.FramesDropped > 0;

            _metrics[5].Value = m.Underruns.ToString(CultureInfo.InvariantCulture);
            _metrics[5].Bar = m.Underruns == 0 ? 0 : 1.0;
            _metrics[5].Alert = m.Underruns > 0;

            _metrics.Invalidate();
        }

        /// <summary>写事件日志。所有运行期消息都汇总到这里，便于事后排查。</summary>
        /// <summary>
        /// 追加一条日志。
        ///
        /// 内容进内存缓冲，界面只更新行数——日志弹窗随时打开都能看到全部历史，
        /// 而主界面为此只占一行。行数上限防止长期运行把内存吃光。
        /// </summary>
        private const int LogMaxLines = 4000;

        private void AppendLog(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), s); return; }

            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + s;
            lock (_logBuffer)
            {
                _logBuffer.AppendLine(line);
                // 超限时砍掉前半——只保留最近的，旧日志对排查已无意义
                if (_logBuffer.Length > LogMaxLines * 40)
                {
                    int cut = _logBuffer.Length - LogMaxLines * 30;
                    int nl = _logBuffer.ToString().IndexOf('\n', cut);
                    if (nl > 0) _logBuffer.Remove(0, nl + 1);
                }
            }

            if (_logCountLabel != null && !_logCountLabel.IsDisposed)
                _logCountLabel.Text = "LOG  " + CountLogLines() + " LINES   [ click to view ]";
        }

        private int CountLogLines()
        {
            lock (_logBuffer)
            {
                int n = 0;
                for (int i = 0; i < _logBuffer.Length; i++)
                    if (_logBuffer[i] == '\n') n++;
                return n;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopReceiver();
            if (_tray != null) _tray.Visible = false;
            base.OnFormClosing(e);
        }
    }

    internal static class GuiEntry
    {
        // ---- DPI 感知 ----
        // 未声明 DPI 感知时，Windows 会把整个窗口当位图拉伸，界面变糊、控件被放大。
        // 这是"分辨率不对"的根因。必须在使用任何窗口之前设置。
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        /// <summary>按可用的最精确方式声明 DPI 感知，逐级回退。</summary>
        private static string EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return "PerMonitorV2";
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SetProcessDpiAwarenessContext: " + ex.Message); }
            try
            {
                if (SetProcessDpiAwareness(2) == 0) return "PerMonitor";
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SetProcessDpiAwareness: " + ex.Message); }
            try
            {
                if (SetProcessDPIAware()) return "System";
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SetProcessDPIAware: " + ex.Message); }
            return "none";
        }

        [STAThread]
        private static void Main()
        {
            string dpiMode = EnableDpiAwareness();
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 提高系统计时器精度：播放线程每个音频块仅十几毫秒，
                // 默认 15.6 ms 的调度粒度会直接拖慢消费速率。
                bool timerRaised = NativeTimer.Begin();

                var form = new MainForm();

                // 启动时把实际尺寸与 DPI 落盘，便于核对布局是否被系统压缩。
                // 这份自述比从外部推断可靠：外部工具看到的 DPI 可能与进程内不同。
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
                    var wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : Rectangle.Empty;
                    using (var g = form.CreateGraphics())
                    {
                        string line = string.Format(CultureInfo.InvariantCulture,
                            "{0:yyyy-MM-dd HH:mm:ss} dpiMode={1} graphicsDpi={2}x{3} " +
                            "workingArea={4}x{5} formSize={6}x{7} minSize={8}x{9} " +
                            "fonts={10}/{11}/{12}",
                            DateTime.Now, dpiMode, g.DpiX, g.DpiY,
                            wa.Width, wa.Height, form.Width, form.Height,
                            form.MinimumSize.Width, form.MinimumSize.Height,
                            Theme.MetricFontName, Theme.UiFontName, Theme.MonoFontName);
                        File.AppendAllText(Path.Combine(dir, "gui-startup.log"), line + Environment.NewLine);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("写启动诊断失败: " + ex.Message); }

                Application.Run(form);

                NativeTimer.End(timerRaised);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Startup failed:\n\n" + ex.GetType().Name + ": " + ex.Message +
                                "\n\n" + ex.StackTrace,
                                "SNAD Audio Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>系统计时器精度的申请与释放（与 Program 里用的是同一对 API）。</summary>
    internal static class NativeTimer
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);

        public static bool Begin() { return TimeBeginPeriod(1) == 0; }
        public static void End(bool wasRaised) { if (wasRaised) TimeEndPeriod(1); }
    }
}
