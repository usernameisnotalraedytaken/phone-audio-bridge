// 均衡器窗口：10 段推子 + 预设 + 前置增益 + 联动拖动。
//
// 为什么做成独立窗口而不是嵌在主界面里：主界面逻辑可用高度只有约 480 像素，
// 已被频谱、波形、统计占满，塞不下 10 段竖向推子。
//
// 改动直接作用于正在运行的 Equalizer 实例，因此拖动推子能实时听到变化，
// 不需要重启接收器。

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class EqForm : Form
    {
        private static readonly Color BgDark = Color.FromArgb(18, 20, 26);
        private static readonly Color BgPanel = Color.FromArgb(26, 29, 36);
        private static readonly Color BgBar = Color.FromArgb(36, 40, 50);
        private static readonly Color FgMain = Color.FromArgb(226, 232, 240);
        private static readonly Color FgDim = Color.FromArgb(146, 156, 172);
        private static readonly Color Accent = Color.FromArgb(76, 194, 255);
        private static readonly Color Warn = Color.FromArgb(255, 138, 92);
        private static readonly Color Grid = Color.FromArgb(44, 49, 60);

        private const int SliderMin = -12;
        private const int SliderMax = 12;

        // 尺寸：间距放宽，避免控件挤在一起（用户反馈"显得局促"）
        private const int ColumnWidth = 72;
        private const int SliderWidth = 40;
        private const int SliderHeight = 300;
        private const int TopRowHeight = 54;
        private const int PresetRowHeight = 50;
        private const int HintHeight = 56;
        private const int BandLabelHeight = 22;
        private const int ReadoutHeight = 24;
        private const int BandGap = 8;

        private readonly Equalizer _eq;
        private readonly Action<string> _log;

        private CheckBox _enable;
        private CheckBox _couple;
        private TrackBar[] _sliders;
        private Label[] _valueLabels;
        private NumericUpDown _preamp;
        private Label _hint;

        /// <summary>防止联动拖动时事件递归。</summary>
        private bool _adjusting;

        /// <summary>
        /// 联动强度：相邻频段跟随的比例按这个标准差衰减。
        /// 1.1 表示隔一个频段约跟随 66%，隔两个约 19%。
        /// </summary>
        private double _coupleSigma = 1.1;

        public EqForm(Equalizer eq, Action<string> log)
        {
            _eq = eq;
            _log = log;

            Text = "均衡器";
            BackColor = BgDark;
            ForeColor = FgMain;
            Font = new Font("Microsoft YaHei UI", 9f);
            // 不用 FixedToolWindow：它会按内容自动收缩，把显式设置的 ClientSize 绕过，
            // 结果窗口比预设按钮区窄，最右侧按钮被裁（实测 758 宽装不下 808 的内容）。
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            BuildUi();
            SyncFromEq();
        }

        private void BuildUi()
        {
            int bandCount = _eq.Bands.Count;

            // ---- 第一行：启用 / 联动 / 前置增益 ----
            var top = new Panel { Dock = DockStyle.Top, Height = TopRowHeight, BackColor = BgPanel, Padding = new Padding(16, 12, 16, 10) };

            _enable = new CheckBox
            {
                Text = "启用均衡器",
                ForeColor = FgMain,
                AutoSize = true,
                Location = new Point(16, 17),
                Checked = _eq.Enabled
            };
            _enable.CheckedChanged += (s, e) =>
            {
                _eq.Enabled = _enable.Checked;
                _log("均衡器" + (_enable.Checked ? "已启用" : "已关闭"));
            };
            top.Controls.Add(_enable);

            // 联动拖动：拖一个推子时相邻推子按距离比例跟随，
            // 用来整体调整一段频带，而不是逐段抠。
            _couple = new CheckBox
            {
                Text = "联动拖动",
                ForeColor = FgMain,
                AutoSize = true,
                Location = new Point(150, 17),
                Checked = false
            };
            _couple.CheckedChanged += (s, e) =>
                _log("联动拖动" + (_couple.Checked ? "已开启（拖一个推子会带动相邻频段）" : "已关闭"));
            top.Controls.Add(_couple);

            var preLabel = new Label
            {
                Text = "前置",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(268, 19)
            };
            top.Controls.Add(preLabel);

            _preamp = new NumericUpDown
            {
                Minimum = -24,
                Maximum = 12,
                DecimalPlaces = 1,
                Increment = 0.5M,
                Width = 66,
                Height = 26,
                Location = new Point(306, 15),
                BackColor = BgBar,
                ForeColor = FgMain,
                BorderStyle = BorderStyle.FixedSingle,
                Value = (decimal)_eq.PreampDb
            };
            _preamp.ValueChanged += (s, e) =>
            {
                _eq.PreampDb = (double)_preamp.Value;
                UpdateHint();
            };
            top.Controls.Add(_preamp);

            var dbUnit = new Label
            {
                Text = "dB（补偿整体提升，避免削波）",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(378, 19)
            };
            top.Controls.Add(dbUnit);

            // ---- 第二行：预设 ----
            var presets = new (string Name, double[] Gains)[]
            {
                ("全平",     new double[10]),
                ("低音增强", new double[] {  7,  5,  3,  1,  0,  0,  0,  0,  0,  0 }),
                ("高音增强", new double[] {  0,  0,  0,  0,  0,  1,  3,  4,  5,  5 }),
                ("人声突出", new double[] { -2, -1,  1,  3,  4,  3,  1,  0, -1, -1 }),
                ("夜间小声", new double[] { -3, -2,  0,  2,  3,  2,  0, -2, -4, -5 }),
                ("削低频",   new double[] { -8, -6, -3,  0,  0,  0,  0,  0,  0,  0 }),
            };

            var presetRow = new Panel { Dock = DockStyle.Top, Height = PresetRowHeight, BackColor = BgDark, Padding = new Padding(16, 8, 16, 8) };
            int px = 16;
            foreach (var p in presets)
            {
                var btn = new Button
                {
                    Text = p.Name,
                    // 按钮留足宽度与间距。FixedToolWindow 会把窗口收缩到内容宽度，
                    // 按钮挨太紧会让整个窗口显得局促（用户反馈"UI 显得局促"）。
                    Width = 96,
                    Height = 32,
                    Location = new Point(px, 9),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = BgBar,
                    ForeColor = FgMain,
                    UseVisualStyleBackColor = false,
                    Tag = p.Gains
                };
                btn.FlatAppearance.BorderColor = Accent;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 60, 74);
                btn.Click += OnPresetClicked;
                presetRow.Controls.Add(btn);
                px += 104;
            }

            // ---- 第三行：推子 ----
            int sliderAreaWidth = bandCount * ColumnWidth;
            var sliders = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                Padding = new Padding(16, 10, 16, 8)
            };
            _sliders = new TrackBar[bandCount];
            _valueLabels = new Label[bandCount];

            for (int i = 0; i < bandCount; i++)
            {
                int x = 16 + i * ColumnWidth;
                var band = _eq.Bands[i];

                var fl = new Label
                {
                    Text = band.Label,
                    ForeColor = FgDim,
                    Width = ColumnWidth,
                    Height = BandLabelHeight,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(x, 10)
                };
                sliders.Controls.Add(fl);

                var tb = new TrackBar
                {
                    Orientation = Orientation.Vertical,
                    Minimum = SliderMin,
                    Maximum = SliderMax,
                    TickFrequency = 6,
                    SmallChange = 1,
                    LargeChange = 3,
                    Width = SliderWidth,
                    Height = SliderHeight,
                    Location = new Point(x + (ColumnWidth - SliderWidth) / 2,
                                         10 + BandLabelHeight + BandGap),
                    BackColor = BgDark,
                    TickStyle = TickStyle.Both,
                    Value = (int)Math.Round(band.GainDb),
                    Tag = i
                };
                tb.ValueChanged += OnSliderChanged;
                sliders.Controls.Add(tb);
                _sliders[i] = tb;

                var vl = new Label
                {
                    Text = "0",
                    ForeColor = FgMain,
                    Width = ColumnWidth,
                    Height = ReadoutHeight,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(x, 10 + BandLabelHeight + BandGap + SliderHeight + 2)
                };
                sliders.Controls.Add(vl);
                _valueLabels[i] = vl;
            }

            // ---- 第四行：提示 ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = HintHeight, BackColor = BgPanel, Padding = new Padding(16, 10, 16, 10) };
            _hint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = FgDim,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            bottom.Controls.Add(_hint);

            Controls.Add(sliders);
            Controls.Add(bottom);
            Controls.Add(presetRow);
            Controls.Add(top);

            // 宽度要同时容纳推子区与预设按钮区，且不低于两者中的较大者。
            int presetAreaRight = 16 + presets.Length * 104 + 8;
            int sliderAreaRight = 16 + sliderAreaWidth + 16;
            int wantW = Math.Max(sliderAreaRight, presetAreaRight);
            int wantH = TopRowHeight + PresetRowHeight + HintHeight +
                        10 + BandLabelHeight + BandGap + SliderHeight + 2 + ReadoutHeight + 10;
            ClientSize = new Size(wantW, wantH);
            MinimumSize = new Size(wantW + 16, wantH + 40);   // 加上边框与标题栏
        }

        private void OnPresetClicked(object sender, EventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var gains = btn.Tag as double[];
            if (gains == null) return;

            _eq.SetAllGains(gains);
            SyncFromEq();
            _log("均衡器预设：" + btn.Text);
        }

        /// <summary>
        /// 推子变动。
        ///
        /// 联动模式下：以被拖动的频段为中心，相邻频段按高斯权重跟随，
        /// 形成一条平滑的调整曲线，而不是只动一个频段。
        /// 权重按距离衰减（sigma=1.1 时，紧邻约 66%，隔一个约 19%）。
        ///
        /// 拖动会连续触发本事件，因此这里批量设值、最后统一重算一次系数，
        /// 避免每个频段各自重算三角函数。
        /// </summary>
        private void OnSliderChanged(object sender, EventArgs e)
        {
            if (_adjusting) return;
            var tb = sender as TrackBar;
            if (tb == null) return;

            int idx = (int)tb.Tag;
            double oldGain = _eq.Bands[idx].GainDb;
            double newGain = tb.Value;
            double delta = newGain - oldGain;
            if (Math.Abs(delta) < 1e-9) return;

            _adjusting = true;
            try
            {
                int n = _eq.Bands.Count;
                for (int j = 0; j < n; j++)
                {
                    double w;
                    if (j == idx) w = 1.0;
                    else if (_couple.Checked)
                    {
                        double d = j - idx;
                        w = Math.Exp(-(d * d) / (2.0 * _coupleSigma * _coupleSigma));
                    }
                    else w = 0.0;

                    if (w <= 0.01) continue;

                    double target = _eq.Bands[j].GainDb + delta * w;
                    _eq.SetGainNoRebuild(j, target);

                    int v = (int)Math.Round(_eq.Bands[j].GainDb);
                    if (v < SliderMin) v = SliderMin;
                    if (v > SliderMax) v = SliderMax;
                    if (_sliders[j].Value != v) _sliders[j].Value = v;
                    _valueLabels[j].Text = v.ToString("+#0;-#0;0");
                }
                _eq.RebuildAllFilters();
            }
            finally
            {
                _adjusting = false;
            }
            UpdateHint();
        }

        /// <summary>把 Equalizer 的当前状态同步到控件上（预设或外部改动后调用）。</summary>
        private void SyncFromEq()
        {
            _adjusting = true;
            try
            {
                for (int i = 0; i < _sliders.Length; i++)
                {
                    int v = (int)Math.Round(_eq.Bands[i].GainDb);
                    if (v < SliderMin) v = SliderMin;
                    if (v > SliderMax) v = SliderMax;
                    if (_sliders[i].Value != v) _sliders[i].Value = v;
                    _valueLabels[i].Text = v.ToString("+#0;-#0;0");
                }
            }
            finally
            {
                _adjusting = false;
            }
            _enable.Checked = _eq.Enabled;
            UpdateHint();
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

            string s;
            if (maxBoost > 3 && headroom < maxBoost - 3)
            {
                s = "提示：最大提升 " + maxBoost.ToString("F1") + " dB，建议把前置设为 -" +
                    Math.Max(0, maxBoost - 3).ToString("F1") + " dB 左右，否则可能削波。";
                _hint.ForeColor = Warn;
            }
            else
            {
                s = "前置增益用于补偿整体提升，避免削波。";
                if (_couple != null && _couple.Checked)
                    s += "　联动拖动已开启：拖一个推子会带动相邻频段。";
                _hint.ForeColor = FgDim;
            }
            _hint.Text = s;
        }
    }
}
