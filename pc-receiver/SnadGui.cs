// SNAD 接收器图形界面。
//
// 目标：双击即用。不需要命令行参数，不需要命令行窗口。
//   顶部：档位切换、输出设备选择、开始/停止
//   中部：实时频谱（foobar2000 风格）+ 波形
//   底部：延迟、缓冲水位、各级帧率、丢帧与异常计数
//
// 档位切换会重启接收线程并重新建缓冲，因此切换后会有约 0.3 秒的重连。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class MainForm : Form
    {
        // ---- 视觉 ----
        private static readonly Color BgDark = Color.FromArgb(18, 20, 26);
        private static readonly Color BgPanel = Color.FromArgb(28, 32, 40);
        private static readonly Color BgBar = Color.FromArgb(40, 46, 58);
        private static readonly Color FgMain = Color.FromArgb(226, 232, 240);
        private static readonly Color FgDim = Color.FromArgb(140, 150, 165);
        private static readonly Color Accent = Color.FromArgb(80, 200, 255);
        private static readonly Color AccentWarm = Color.FromArgb(255, 190, 90);
        private static readonly Color Warn = Color.FromArgb(255, 110, 110);
        private static readonly Color Ok = Color.FromArgb(120, 220, 140);

        // ---- 控件 ----
        private ComboBox _profileBox;
        private ComboBox _deviceBox;
        private Button _startBtn;
        private Label _statusLabel;
        private Label _latencyLabel;
        private TextBox _statsBox;
        private SpectrumPanel _spectrum;
        private WavePanel _wave;
        private NotifyIcon _tray;

        // ---- 状态 ----
        private Dictionary<string, Profile> _profiles;
        private string _cfgPath;
        private List<DeviceInfo> _devices;
        private int _deviceIndex = -1;

        private SnadReceiver _receiver;
        private Thread _rxThread;
        private CancellationTokenSource _runCts;
        private Profile _active;

        private readonly SpectrumAnalyzer _analyzer = new SpectrumAnalyzer();
        private readonly object _audioGate = new object();
        private byte[] _pendingAudio;
        private int _pendingBytes, _pendingRate, _pendingCh;

        private System.Windows.Forms.Timer _uiTimer;

        /// <summary>
        /// 从 exe 自身内嵌资源取出应用图标。
        ///
        /// 说明：/win32icon 只是把图标写进 exe 资源（供资源管理器显示），
        /// WinForms 窗口并不会自动采用它，必须显式赋给 Form.Icon 与 NotifyIcon.Icon。
        /// 此前两处都没设，所以标题栏与托盘一直是系统默认图标。
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

        public MainForm()
        {
            Text = "SNAD 音频桥 —— 接收端";
            // 显式采用内嵌图标，否则标题栏显示系统默认图标
            Icon = LoadAppIcon();
            BackColor = BgDark;
            ForeColor = FgMain;
            Font = new Font("Microsoft YaHei UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            // 按屏幕工作区自适应尺寸，并保证不低于布局所需的最小高度。
            //
            // 本机实测：屏幕 1280x720，工作区同为 1280x720，进程为 PerMonitor DPI 感知。
            // 布局所需：顶部 76 + 波形 76 + 频谱(至少约 120) + 底部 116 ≈ 390，
            // 加上边框与标题栏，取 600 高、880 宽可完整显示且不溢出屏幕。
            // 此前用"工作区减去固定边距"的写法在缩放下算得过小，界面被压扁。
            var wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1280, 800);
            int wantW = 880, wantH = 600;
            int w = Math.Min(wantW, Math.Max(620, wa.Width - 20));
            int h = Math.Min(wantH, Math.Max(460, wa.Height - 20));
            MinimumSize = new Size(Math.Min(620, wa.Width), Math.Min(460, wa.Height));
            Size = new Size(w, h);

            BuildUi();
            LoadConfigAndDevices();
        }

        // ------------------------------------------------------------------ 界面构建

        private void BuildUi()
        {
            // 紧凑布局：逻辑可用高度只有约 480 px（本机 1280x720 @ 150%）。
            //
            // 经验教训：此前全部用固定 Location + 固定高度，中文字体在 9pt 下
            // 实际行高比预期大，导致"开始"按钮的文字下沿被按钮底边裁掉。
            // 因此这里改用 AutoSize 标签，并给输入控件留足高度。

            // ---- 顶部控制条 ----
            var top = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = BgPanel, Padding = new Padding(10, 8, 10, 8) };

            var lblProfile = new Label
            {
                Text = "档位",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(10, 6)
            };
            _profileBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 132,
                Height = 30,
                Location = new Point(10, 26),
                BackColor = BgBar,
                ForeColor = FgMain,
                FlatStyle = FlatStyle.Flat
            };
            _profileBox.SelectedIndexChanged += OnProfileChanged;

            var lblDevice = new Label
            {
                Text = "输出设备",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(152, 6)
            };
            _deviceBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 240,
                Height = 30,
                Location = new Point(152, 26),
                BackColor = BgBar,
                ForeColor = FgMain,
                FlatStyle = FlatStyle.Flat
            };
            _deviceBox.SelectedIndexChanged += OnDeviceChanged;

            _startBtn = new Button
            {
                Text = "开始",
                Width = 84,
                // 高度必须够：中文在 9pt 下加上内边距与边框，26 px 会把文字下沿裁掉
                // （实测正是文字底部被按钮底边切掉）。34 px 留足余量。
                Height = 34,
                Location = new Point(402, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = BgBar,
                ForeColor = FgMain,
                // 默认 UseVisualStyleBackColor = true 时视觉样式会忽略 BackColor，
                // 按钮回落到系统浅灰底，配上浅色前景几乎看不清。必须关掉。
                UseVisualStyleBackColor = false
            };
            _startBtn.FlatAppearance.BorderColor = Accent;
            _startBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 60, 74);
            _startBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(34, 40, 52);
            _startBtn.Click += OnStartStop;

            _statusLabel = new Label
            {
                Text = "未启动",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(496, 32)
            };

            top.Controls.AddRange(new Control[] { lblProfile, _profileBox, lblDevice, _deviceBox, _startBtn, _statusLabel });

            // ---- 底部统计 ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 116, BackColor = BgPanel, Padding = new Padding(10, 6, 10, 6) };
            _latencyLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Accent,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                Text = "延迟 —"
            };
            _statsBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = BgPanel,
                ForeColor = FgDim,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f)
            };
            bottom.Controls.Add(_statsBox);
            bottom.Controls.Add(_latencyLabel);

            // ---- 中部可视化：频谱填充剩余空间，波形停靠其底部 ----
            _spectrum = new SpectrumPanel(_analyzer) { Dock = DockStyle.Fill };
            _wave = new WavePanel(_analyzer) { Dock = DockStyle.Bottom, Height = 76 };

            var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 0) };
            center.Controls.Add(_spectrum);   // Fill 先加
            center.Controls.Add(_wave);       // Bottom 后加，避免压住 Fill 区

            // WinForms 停靠规则：后添加的控件先占据边缘。
            // 因此顺序应为 center(Fill) -> bottom(Bottom) -> top(Top)。
            Controls.Add(center);
            Controls.Add(bottom);
            Controls.Add(top);

            // ---- 托盘 ----
            _tray = new NotifyIcon
            {
                // 与窗口共用同一个内嵌图标
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
            _profileBox.Items.Clear();
            foreach (var kv in _profiles) _profileBox.Items.Add(kv.Key);
            if (_profileBox.Items.Contains(selected)) _profileBox.SelectedItem = selected;
            else if (_profileBox.Items.Count > 0) _profileBox.SelectedIndex = 0;

            RefreshDevices();
        }

        private void RefreshDevices()
        {
            _devices = WaveOut.ListDevices();
            _deviceBox.Items.Clear();
            string defName = WaveOut.DefaultDeviceName();
            int defIdx = -1;
            if (!string.IsNullOrEmpty(defName))
            {
                var m = _devices.Find(d => string.Equals(d.Name, defName, StringComparison.OrdinalIgnoreCase));
                if (m.Name != null) defIdx = m.Index;
                else
                {
                    string pre = defName.Substring(0, Math.Min(12, defName.Length));
                    m = _devices.Find(d => d.Name.StartsWith(pre, StringComparison.OrdinalIgnoreCase));
                    if (m.Name != null) defIdx = m.Index;
                }
            }

            _deviceBox.Items.Add("跟随系统默认");
            foreach (var d in _devices)
                _deviceBox.Items.Add("[" + d.Index + "] " + d.Name);

            if (_deviceIndex < 0) _deviceBox.SelectedIndex = 0;
            else _deviceBox.SelectedIndex = Math.Min(_deviceBox.Items.Count - 1, _deviceIndex + 1);

            _statusLabel.Text = defIdx >= 0
                ? "系统默认：" + _devices[defIdx].Name
                : "未识别系统默认设备";
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            int sel = _deviceBox.SelectedIndex;
            _deviceIndex = sel <= 0 ? -1 : sel - 1;
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            string name = _profileBox.SelectedItem as string;
            if (name == null || !_profiles.ContainsKey(name)) return;
            _active = _profiles[name];

            // 正在运行则重启以应用新参数（缓冲尺寸需要重建）
            if (_receiver != null)
            {
                AppendStatsLine("切换档位到 " + name + "，重启接收…");
                StopReceiver();
                StartReceiver();
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
                string sel = _profileBox.SelectedItem as string;
                if (sel != null && _profiles.ContainsKey(sel)) _active = _profiles[sel];
            }
            if (_active == null) { MessageBox.Show("没有可用的档位配置。"); return; }

            // 选设备：-1 表示跟随系统默认；被占用时回退 Voicemeeter Input
            int dev = _deviceIndex;
            if (dev < 0)
            {
                string defName = WaveOut.DefaultDeviceName();
                int defIdx = -1;
                if (!string.IsNullOrEmpty(defName))
                {
                    var m = _devices.Find(d => string.Equals(d.Name, defName, StringComparison.OrdinalIgnoreCase));
                    if (m.Name != null) defIdx = m.Index;
                    else
                    {
                        string pre = defName.Substring(0, Math.Min(12, defName.Length));
                        m = _devices.Find(d => d.Name.StartsWith(pre, StringComparison.OrdinalIgnoreCase));
                        if (m.Name != null) defIdx = m.Index;
                    }
                }
                if (defIdx >= 0)
                {
                    IntPtr probe;
                    int rc = WaveOut.ProbeOpen(defIdx, out probe);
                    if (rc == 0)
                    {
                        dev = defIdx;
                        AppendStatsLine("输出：跟随系统默认 [" + defIdx + "] " + _devices[defIdx].Name);
                    }
                    else
                    {
                        var vm = _devices.Find(d => d.Name.IndexOf("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (vm.Name != null)
                        {
                            dev = vm.Index;
                            AppendStatsLine("系统默认设备被占用（rc=" + rc + "），改用 Voicemeeter Input 混音输出");
                        }
                        else
                        {
                            dev = defIdx;
                            AppendStatsLine("系统默认设备被占用且无 Voicemeeter Input，仍尝试默认设备");
                        }
                    }
                }
                else
                {
                    dev = 0;
                    AppendStatsLine("未识别系统默认设备，使用 [0] " + (_devices.Count > 0 ? _devices[0].Name : "?"));
                }
            }
            else
            {
                AppendStatsLine("输出：指定设备 [" + dev + "] " + _devices[dev].Name);
            }

            _runCts = new CancellationTokenSource();
            _receiver = new SnadReceiver(dev, Program.DefaultPort, _active);
            _receiver.AudioBlockReady += OnAudioBlock;

            var r = _receiver;
            _rxThread = new Thread(() => { try { r.Run(); } catch (Exception ex) { AppendStatsLine("接收线程异常：" + ex.Message); } })
            {
                IsBackground = true,
                Name = "snad-ui-rx"
            };
            _rxThread.Start();

            _startBtn.Text = "停止";
            _startBtn.FlatAppearance.BorderColor = Warn;
            _statusLabel.Text = "运行中 · 档位 " + _active.Name;
            _tray.Text = "SNAD 音频桥 · 运行中";
            AppendStatsLine("已启动：档位 " + _active.Name + "，预计链路延迟约 " +
                            _active.EstimatedPipelineLatencyMs() + " ms（不含音频设备自身延迟）");
        }

        private void StopReceiver()
        {
            try { if (_receiver != null) _receiver.Stop(); }
            catch (Exception ex) { AppendStatsLine("停止时异常：" + ex.Message); }
            try { if (_rxThread != null && _rxThread.IsAlive) _rxThread.Join(1500); }
            catch (Exception ex) { AppendStatsLine("等待线程退出异常：" + ex.Message); }
            try { if (_receiver != null) _receiver.Dispose(); }
            catch (Exception ex) { AppendStatsLine("释放接收器异常：" + ex.Message); }

            _receiver = null;
            _rxThread = null;
            if (_runCts != null) { _runCts.Dispose(); _runCts = null; }

            _startBtn.Text = "开始";
            _startBtn.FlatAppearance.BorderColor = Accent;
            _statusLabel.Text = "已停止";
            _tray.Text = "SNAD 音频桥";
            _latencyLabel.Text = "延迟 —";
            AppendStatsLine("已停止");
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
            lock (_audioGate)
            {
                if (_pendingAudio != null)
                {
                    pcm = _pendingAudio;
                    _pendingAudio = null;
                }
            }
            if (pcm != null)
            {
                _analyzer.Feed(pcm, pcm.Length, _pendingCh, _pendingRate);
                _spectrum.Invalidate();
                _wave.Invalidate();
            }

            if (_receiver != null)
            {
                var m = _receiver.Snapshot();
                int pipeline = 21 + 8 + m.TargetMs + m.BlockMs;
                _latencyLabel.Text = string.Format(CultureInfo.InvariantCulture,
                    "链路延迟 ≈ {0} ms   （缓冲 {1} / 目标 {2} ms，每块 {3} ms）  档位 {4}",
                    pipeline, m.BufferMs, m.TargetMs, m.BlockMs, m.ProfileName);

                bool unhealthy = m.FramesDropped > 0 || m.Underruns > 0;
                _latencyLabel.ForeColor = unhealthy ? AccentWarm : Ok;
            }
        }

        private void AppendStatsLine(string s)
        {
            if (_statsBox == null) return;
            _statsBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopReceiver();
            if (_tray != null) _tray.Visible = false;
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------ 频谱面板

    internal sealed class SpectrumPanel : Panel
    {
        private readonly SpectrumAnalyzer _an;
        public SpectrumPanel(SpectrumAnalyzer an)
        {
            _an = an;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 32, 40);
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            int w = Width, h = Height;
            g.Clear(BackColor);

            // 网格与刻度
            using (var pen = new Pen(Color.FromArgb(46, 52, 64)))
            {
                for (int db = 0; db >= -96; db -= 16)
                {
                    int y = DbToY(db, h);
                    g.DrawLine(pen, 0, y, w, y);
                }
            }
            using (var f = new Font("Consolas", 7.5f))
            using (var br = new SolidBrush(Color.FromArgb(110, 120, 135)))
            {
                for (int db = 0; db >= -96; db -= 24)
                    g.DrawString(db + " dB", f, br, 3, DbToY(db, h) + 1);
            }

            var bars = _an.BarsDb;
            var peaks = _an.PeaksDb;
            int n = bars.Length;
            if (n == 0) return;
            float bw = (float)w / n;
            bool silent = _an.IsSilent();

            for (int i = 0; i < n; i++)
            {
                int y = DbToY(bars[i], h);
                int bh = h - y;
                if (bh < 1) bh = 1;
                float x = i * bw;
                float bwFill = Math.Max(1f, bw - 1f);

                if (silent)
                {
                    // 静默：画一条贴底的细柱，用暗色，避免"没声音却满屏红色"
                    using (var br = new SolidBrush(Color.FromArgb(52, 60, 74)))
                        g.FillRectangle(br, x, h - 3, bwFill, 3);
                    continue;
                }

                // foobar2000 风格：绿→黄→红的渐变柱
                var barRect = new RectangleF(x, 0, bwFill, h);
                using (var lg = new LinearGradientBrush(barRect,
                    Color.FromArgb(60, 200, 130), Color.FromArgb(250, 90, 90),
                    LinearGradientMode.Vertical))
                {
                    // Blend 不是 IDisposable，直接赋值即可（它是普通类）
                    lg.Blend = new Blend
                    {
                        Positions = new[] { 0f, 0.55f, 0.8f, 1f },
                        Factors = new[] { 1f, 0.85f, 0.5f, 0.12f }
                    };
                    g.FillRectangle(lg, x, y, bwFill, bh);
                }

                // 峰值线
                int py = DbToY(peaks[i], h);
                using (var p = new Pen(Color.FromArgb(220, 235, 250)))
                    g.DrawLine(p, x, py, x + bwFill, py);
            }

            if (silent)
            {
                // 偏右上角放置，避免压住左下角的频率刻度
                using (var f = new Font("Consolas", 8.5f))
                using (var br = new SolidBrush(Color.FromArgb(96, 106, 122)))
                {
                    var sz = g.MeasureString("无信号", f);
                    g.DrawString("无信号", f, br, w - sz.Width - 10, 8);
                }
            }

            using (var f = new Font("Consolas", 7.5f))
            using (var br = new SolidBrush(Color.FromArgb(110, 120, 135)))
            {
                g.DrawString("30Hz", f, br, 2, h - 14);
                g.DrawString("1kHz", f, br, w / 2f - 12, h - 14);
                g.DrawString("16kHz", f, br, w - 40, h - 14);
            }
        }

        private static int DbToY(double db, int h)
        {
            // 显示范围 -96..0 dB（与 SpectrumAnalyzer.FloorDb 一致）
            double lo = SpectrumAnalyzer.FloorDb;
            double t = (db - lo) / (0.0 - lo);
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return (int)(h - t * (h - 4)) + 2;
        }
    }

    // ------------------------------------------------------------------ 波形面板

    internal sealed class WavePanel : Panel
    {
        private readonly SpectrumAnalyzer _an;
        public WavePanel(SpectrumAnalyzer an)
        {
            _an = an;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 28, 35);
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = Width, h = Height;
            g.Clear(BackColor);

            using (var pen = new Pen(Color.FromArgb(46, 52, 64)))
                g.DrawLine(pen, 0, h / 2, w, h / 2);

            var wave = _an.Waveform;
            int n = wave.Length;
            if (n < 2) return;

            var pts = new PointF[n];
            float mid = h / 2f;
            float amp = (h / 2f) - 4f;
            for (int i = 0; i < n; i++)
            {
                float x = (float)i / (n - 1) * w;
                float y = mid - (float)wave[i] * amp;
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(Color.FromArgb(120, 215, 255), 1.2f))
                g.DrawLines(pen, pts);
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
                // 控制台版在 Program.Main 里做同样的事，GUI 版必须自己做。
                bool timerRaised = NativeTimer.Begin();

                var form = new MainForm();
                System.Diagnostics.Debug.WriteLine("DPI 感知：" + dpiMode);

                // 启动时把实际尺寸与 DPI 落盘，便于核对布局是否被系统压缩。
                // 这份自述比从外部推断可靠：外部工具看到的 DPI 可能与进程内不同。
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
                    var wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : Rectangle.Empty;
                    using (var g = form.CreateGraphics())
                    {
                        float dpiX = g.DpiX, dpiY = g.DpiY;
                        string line = string.Format(CultureInfo.InvariantCulture,
                            "{0:yyyy-MM-dd HH:mm:ss} dpiMode={1} graphicsDpi={2}x{3} " +
                            "workingArea={4}x{5} formSize={6}x{7} minSize={8}x{9}",
                            DateTime.Now, dpiMode, dpiX, dpiY,
                            wa.Width, wa.Height, form.Width, form.Height,
                            form.MinimumSize.Width, form.MinimumSize.Height);
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
