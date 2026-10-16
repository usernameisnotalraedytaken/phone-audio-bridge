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
        private CollapsibleLog _logHost;
        private NotifyIcon _tray;
        private Panel _titleBar;
        private Panel _graphHost;
        private Panel _controlsRow;
        private Label _titleMain, _titleSub, _stateLabel, _rateLabel;
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
            Text = "SNAD 音频桥 —— 接收端";
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
            _titleMain = new Label
            {
                Text = "SNAD",
                ForeColor = Theme.TextPrimary,
                Font = Theme.Ui(11f, FontStyle.Bold),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(16, 10)
            };
            _titleSub = new Label
            {
                Text = "AUDIO BRIDGE",
                ForeColor = Theme.TextTertiary,
                Font = Theme.Ui(8f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(74, 14)
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
            _titleBar.Controls.AddRange(new Control[] { _titleMain, _titleSub, _dot, _stateLabel, _rateLabel });
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

            _startBtn = new FlatButton("开始", true) { Width = 88, Height = 32, Location = new Point(0, 10) };
            _startBtn.Click += OnStartStop;

            _eqBtn = new FlatButton("均衡器") { Width = 88, Height = 32, Location = new Point(0, 10) };
            _eqBtn.Click += OnOpenEq;

            controls.Controls.AddRange(new Control[] { _profileBox, _deviceBox, _startBtn, _eqBtn });
            controls.Resize += (s, e) => LayoutControlRow();

            // ---- 事件日志（默认折叠）----
            // 日志是排查用的，正常使用时只占地方。折叠后只留一条标题栏，
            // 点击展开；标题栏显示行数，折叠时也知道有没有新日志。
            var logHost = new CollapsibleLog { BackColor = Theme.BgPanel };

            // ---- 组装 ----
            // 不用 Dock 排版面：WinForms 的停靠顺序由控件在集合里的次序决定，
            // 实测 BringToFront 无法可靠改变布局顺序，通道条会跑到图上面
            // （与 K 方向相反）。这里改为显式摆放，位置全部由 LayoutAll 算出。
            Controls.Add(graphHost);
            Controls.Add(logHost);
            Controls.Add(controls);
            Controls.Add(_metrics);
            Controls.Add(_titleBar);

            _graphHost = graphHost;
            _controlsRow = controls;
            _logHost = logHost;

            // 每一块都不再自行停靠，统一由 LayoutAll 定位
            _titleBar.Dock = DockStyle.None;
            _metrics.Dock = DockStyle.None;
            controls.Dock = DockStyle.None;
            graphHost.Dock = DockStyle.None;
            logHost.Dock = DockStyle.None;

            Resize += (s, e) => LayoutAll();
            LayoutAll();

            // 点击滚轮以外的地方就收起。
            // 两个滚轮各自处理自己的点击，因此能走到窗体这一层的点击都算"别处"。
            MouseDown += (s, e) => CloseAllWheels();

            // ---- 托盘 ----
            _tray = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "SNAD 音频桥",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("退出", null, (s, e) => { StopReceiver(); _tray.Visible = false; Application.Exit(); });
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
            string[] units = { "ms", "ms", "fps", "fps", "帧", "次" };
            string[] tips =
            {
                "端到端链路延迟估算（采集 + 网络 + 抖动缓冲 + 音频块），不含声卡自身延迟",
                "抖动缓冲当前水位。接近容量会开始丢帧，接近零会欠载",
                "手机端送入的帧率。应与采样率一致（44.1k 或 48k）",
                "实际写入声卡的帧率。持续低于 RX 说明消费端跟不上",
                "缓冲溢出而丢弃的帧数。非零说明水位偏高或突发过大",
                "缓冲见底导致的欠载次数。非零说明水位偏低或调度抖动大"
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
            const int titleH = 40, metricsH = 88, controlsH = 92;   // controlsH 要容下滚轮的三行
            const int logMinH = 90, graphMinH = 150;

            // 两块弹性区域：图优先，日志保底
            int fixedH = titleH + metricsH + controlsH;
            int flexible = h - fixedH;
            int graphH = flexible / 2 + 60;           // 图约占弹性部分的六成
            if (graphH < graphMinH) graphH = graphMinH;
            int logH = flexible - graphH;
            if (logH < logMinH) { logH = logMinH; graphH = Math.Max(graphMinH, flexible - logMinH); }

            _titleBar.Bounds = new Rectangle(0, 0, w, titleH);
            _graphHost.Bounds = new Rectangle(0, titleH, w, graphH);
            _metrics.Bounds = new Rectangle(0, titleH + graphH, w, metricsH);
            _controlsRow.Bounds = new Rectangle(0, titleH + graphH + metricsH, w, controlsH);
            _logHost.Bounds = new Rectangle(0, titleH + graphH + metricsH + controlsH, w,
                                            Math.Max(10, h - (titleH + graphH + metricsH + controlsH)));

            LayoutTitleBar();
            LayoutControlRow();
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

            // 设备滚轮吃掉左侧剩余空间，并保证不与按钮重叠。
            int deviceLeft = _profileBox.Left + _profileBox.Width + 10;
            int deviceMax = _startBtn.Left - 10 - deviceLeft;
            _deviceBox.Left = deviceLeft;
            _deviceBox.Width = Math.Max(140, Math.Min(280, deviceMax));
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
            AppendLog("配置载入：" + _cfgPath + "（" + _profiles.Count + " 个档位，选中 " + selected + "）");
        }

        private void RefreshDevices()
        {
            _devices = WaveOut.ListDevices();
            string defName = WaveOut.DefaultDeviceName();
            int defIdx = FindDeviceByName(defName);

            var items = new List<string> { "跟随系统默认" };
            foreach (var d in _devices) items.Add("[" + d.Index + "] " + d.Name);
            _deviceBox.Items = items.ToArray();

            if (_deviceIndex < 0) _deviceBox.SelectedIndex = 0;
            else _deviceBox.SelectedIndex = Math.Min(items.Count - 1, _deviceIndex + 1);

            AppendLog(defIdx >= 0
                ? "系统默认输出：" + _devices[defIdx].Name
                : "未识别系统默认输出设备");
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
                AppendLog("切换档位到 " + name + "，重启接收…");
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
            if (_active == null) { MessageBox.Show("没有可用的档位配置。"); return; }

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
                        AppendLog("输出：跟随系统默认 [" + defIdx + "] " + _devices[defIdx].Name);
                    }
                    else
                    {
                        var vm = _devices.Find(d => d.Name.IndexOf("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (vm.Name != null)
                        {
                            dev = vm.Index;
                            AppendLog("系统默认设备被占用（rc=" + rc + "），改用 Voicemeeter Input 混音输出");
                        }
                        else
                        {
                            dev = defIdx;
                            AppendLog("系统默认设备被占用且无 Voicemeeter Input，仍尝试默认设备");
                        }
                    }
                }
                else
                {
                    dev = 0;
                    AppendLog("未识别系统默认设备，使用 [0] " + (_devices.Count > 0 ? _devices[0].Name : "?"));
                }
            }
            else
            {
                AppendLog("输出：指定设备 [" + dev + "] " + _devices[dev].Name);
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
                AppendLog("复制均衡器设置失败：" + ex.Message);
            }

            // 图上的 EQ 直接操作接收器的实例，这样拖动即时生效
            _graph.Eq = _receiver.Equalizer;

            var r = _receiver;
            _rxThread = new Thread(() =>
            {
                try { r.Run(); }
                catch (Exception ex) { BeginInvoke(new Action(() => AppendLog("接收线程异常：" + ex.Message))); }
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
                    m => BeginInvoke(new Action(() => AppendLog("信标：" + m))));
                _beacon.Start();
                AppendLog("信标广播已启动：device=" + Program.MachineDeviceId +
                          "，KeyId=" + SnadSecret.KeyId + "（手机端此值必须一致）");
            }
            catch (Exception ex)
            {
                AppendLog("信标广播启动失败：" + ex.GetType().Name + ": " + ex.Message + "（手机端需手填地址）");
            }

            _startBtn.Text = "停止";
            _eqBtn.Enabled = true;
            AppendLog("已启动：档位 " + _active.Name + "，预计链路延迟约 " +
                      _active.EstimatedPipelineLatencyMs() + " ms（不含声卡自身延迟）");
            UpdateRunState();
        }

        private void StopReceiver()
        {
            try { if (_receiver != null) _receiver.Stop(); }
            catch (Exception ex) { AppendLog("停止时异常：" + ex.Message); }
            try { if (_rxThread != null && _rxThread.IsAlive) _rxThread.Join(1500); }
            catch (Exception ex) { AppendLog("等待线程退出异常：" + ex.Message); }
            try { if (_receiver != null) _receiver.Dispose(); }
            catch (Exception ex) { AppendLog("释放接收器异常：" + ex.Message); }

            // 停止信标广播，避免接收停止后手机仍能"发现"一个不再工作的电脑
            try { if (_beacon != null) { _beacon.Dispose(); _beacon = null; } }
            catch (Exception ex) { AppendLog("停止信标广播异常：" + ex.Message); }

            _receiver = null;
            _rxThread = null;
            if (_runCts != null) { _runCts.Dispose(); _runCts = null; }

            // 图回到"待用"实例，保持 EQ 仍可编辑
            _graph.Eq = _idleEq;

            _startBtn.Text = "开始";
            AppendLog("已停止");
            UpdateRunState();
        }

        /// <summary>刷新标题条状态与通道条的未运行态。</summary>
        private void UpdateRunState()
        {
            bool running = _receiver != null;
            _stateLabel.Text = running ? "LIVE" : "IDLE";
            _stateLabel.ForeColor = running ? Theme.Success : Theme.TextTertiary;
            _dot.BackColor = running ? Theme.Success : Theme.TextTertiary;
            _tray.Text = running ? "SNAD 音频桥 · 运行中" : "SNAD 音频桥";

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
            AppendLog("频段 " + _idleEq.Bands[band].Label + " 已归零");
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
                AppendLog("保存均衡器设置失败：" + ex.GetType().Name + ": " + ex.Message);
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
                using (var f = new EqForm(eq, AppendLog))
                {
                    f.ShowDialog(this);
                }
                SaveEqToProfile();
                _graph.Invalidate();
            }
            catch (Exception ex)
            {
                AppendLog("打开均衡器失败：" + ex.GetType().Name + ": " + ex.Message);
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
                _graph.Invalidate();
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
        private void AppendLog(string s)
        {
            if (_logHost == null || _logHost.IsDisposed) return;
            if (_logHost.InvokeRequired) { _logHost.BeginInvoke(new Action<string>(AppendLog), s); return; }
            _logHost.Append(s);
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
            return "未设置";
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
                MessageBox.Show("启动失败：\n\n" + ex.GetType().Name + ": " + ex.Message +
                                "\n\n" + ex.StackTrace,
                                "SNAD 音频桥", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
