// 均衡器窗口：10 段推子 + 预设 + 前置增益。
//
// 为什么做成独立窗口而不是嵌在主界面里：本机逻辑可用高度只有约 480 像素，
// 主界面已被频谱、波形、统计占满，塞不下 10 段竖向推子。
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

        private const int SliderMin = -12;
        private const int SliderMax = 12;
        private const int ColumnWidth = 56;
        private const int SliderHeight = 240;

        private readonly Equalizer _eq;
        private readonly Action<string> _log;

        private CheckBox _enable;
        private TrackBar[] _sliders;
        private Label[] _valueLabels;
        private NumericUpDown _preamp;
        private Label _hint;

        public EqForm(Equalizer eq, Action<string> log)
        {
            _eq = eq;
            _log = log;

            Text = "均衡器";
            BackColor = BgDark;
            ForeColor = FgMain;
            Font = new Font("Microsoft YaHei UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
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
            int contentWidth = bandCount * ColumnWidth + 24;

            // ---- 顶部：启用开关 + 前置增益 ----
            var top = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = BgPanel, Padding = new Padding(10, 8, 10, 8) };

            _enable = new CheckBox
            {
                Text = "启用均衡器",
                ForeColor = FgMain,
                AutoSize = true,
                Location = new Point(10, 11),
                Checked = _eq.Enabled
            };
            _enable.CheckedChanged += (s, e) =>
            {
                _eq.Enabled = _enable.Checked;
                _log("均衡器" + (_enable.Checked ? "已启用" : "已关闭"));
            };
            top.Controls.Add(_enable);

            var preLabel = new Label
            {
                Text = "前置",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(150, 13)
            };
            top.Controls.Add(preLabel);

            _preamp = new NumericUpDown
            {
                Minimum = -24,
                Maximum = 12,
                DecimalPlaces = 1,
                Increment = 0.5M,
                Width = 62,
                Location = new Point(188, 10),
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
                Text = "dB",
                ForeColor = FgDim,
                AutoSize = true,
                Location = new Point(254, 13)
            };
            top.Controls.Add(dbUnit);

            // ---- 预设 ----
            var presets = new (string Name, double[] Gains)[]
            {
                ("全平",   new double[10]),
                ("低音增强", new double[] {  7,  5,  3,  1,  0,  0,  0,  0,  0,  0 }),
                ("高音增强", new double[] {  0,  0,  0,  0,  0,  1,  3,  4,  5,  5 }),
                ("人声突出", new double[] { -2, -1,  1,  3,  4,  3,  1,  0, -1, -1 }),
                ("夜间小声", new double[] { -3, -2,  0,  2,  3,  2,  0, -2, -4, -5 }),
                ("削低频",   new double[] { -8, -6, -3,  0,  0,  0,  0,  0,  0,  0 }),
            };

            int px = 300;
            foreach (var p in presets)
            {
                var btn = new Button
                {
                    Text = p.Name,
                    Width = 74,
                    Height = 26,
                    Location = new Point(px, 9),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = BgBar,
                    ForeColor = FgMain,
                    UseVisualStyleBackColor = false,
                    Tag = p.Gains
                };
                btn.FlatAppearance.BorderColor = Accent;
                btn.Click += OnPresetClicked;
                top.Controls.Add(btn);
                px += 78;
            }

            // ---- 推子区 ----
            var sliders = new Panel { Dock = DockStyle.Fill, BackColor = BgDark, Padding = new Padding(12, 6, 12, 6) };
            _sliders = new TrackBar[bandCount];
            _valueLabels = new Label[bandCount];

            for (int i = 0; i < bandCount; i++)
            {
                int x = i * ColumnWidth;
                var band = _eq.Bands[i];

                // 频率标签
                var fl = new Label
                {
                    Text = band.Label,
                    ForeColor = FgDim,
                    Width = ColumnWidth - 6,
                    Height = 18,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(x, 2)
                };
                sliders.Controls.Add(fl);

                // 竖向推子：TrackBar 只有水平方向，用 Orientation 旋转
                var tb = new TrackBar
                {
                    Orientation = Orientation.Vertical,
                    Minimum = SliderMin,
                    Maximum = SliderMax,
                    TickFrequency = 6,
                    SmallChange = 1,
                    LargeChange = 3,
                    Width = ColumnWidth - 6,
                    Height = SliderHeight,
                    Location = new Point(x, 22),
                    BackColor = BgDark,
                    TickStyle = TickStyle.Both,
                    Value = (int)Math.Round(band.GainDb),
                    Tag = i
                };
                tb.ValueChanged += OnSliderChanged;
                sliders.Controls.Add(tb);
                _sliders[i] = tb;

                // 数值标签
                var vl = new Label
                {
                    Text = "0.0",
                    ForeColor = FgMain,
                    Width = ColumnWidth - 6,
                    Height = 18,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = new Point(x, 22 + SliderHeight + 2)
                };
                sliders.Controls.Add(vl);
                _valueLabels[i] = vl;
            }

            // ---- 底部提示 ----
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = BgPanel, Padding = new Padding(10, 6, 10, 6) };
            _hint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = FgDim,
                Text = "",
                AutoSize = false
            };
            bottom.Controls.Add(_hint);

            Controls.Add(sliders);
            Controls.Add(bottom);
            Controls.Add(top);

            // 宽度必须同时容纳推子区与预设按钮区。
            // 之前只按推子区算宽度（10 段 × 56 = 584），结果右侧预设按钮被裁掉
            // （实测"夜间小声"只显示一半、"削低频"完全看不见）。
            // 按钮从 x=300 起、每个 78 宽、共 6 个，因此按钮区右边界约 768。
            const int presetAreaRight = 300 + 6 * 78 + 10;
            ClientSize = new Size(
                Math.Max(contentWidth + 24, presetAreaRight),
                SliderHeight + 22 + 42 + 52 + 40);
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

        private void OnSliderChanged(object sender, EventArgs e)
        {
            var tb = sender as TrackBar;
            if (tb == null) return;
            int idx = (int)tb.Tag;
            _eq.SetGain(idx, tb.Value);
            if (_valueLabels != null && idx < _valueLabels.Length)
                _valueLabels[idx].Text = tb.Value.ToString("+#0;-#0;0");
            UpdateHint();
        }

        /// <summary>把 Equalizer 的当前状态同步到控件上（预设或外部改动后调用）。</summary>
        private void SyncFromEq()
        {
            for (int i = 0; i < _sliders.Length; i++)
            {
                int v = (int)Math.Round(_eq.Bands[i].GainDb);
                if (v < SliderMin) v = SliderMin;
                if (v > SliderMax) v = SliderMax;
                if (_sliders[i].Value != v) _sliders[i].Value = v;
                _valueLabels[i].Text = v.ToString("+#0;-#0;0");
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

            string s = "前置增益用于补偿整体提升，避免削波。";
            if (maxBoost > 3 && headroom < maxBoost - 3)
            {
                s = "提示：最大提升 " + maxBoost.ToString("F1") + " dB，建议把前置设为 -" +
                    Math.Max(0, maxBoost - 3).ToString("F1") + " dB 左右，否则可能削波。";
                _hint.ForeColor = Warn;
            }
            else
            {
                _hint.ForeColor = FgDim;
            }
            _hint.Text = s;
        }
    }
}
