// 配置编辑器。
//
// 为什么做成独立弹窗：与 EQ 窗口、日志窗口同一形态，主界面保持仪表盘。
//
// 设计上的三条硬要求（都由本项目已确立的语言推出）：
//   1. 设备能力决定可选项——设备下拉的内容来自 DeviceEnumerator.Probe()，
//      并显示设备名而非 GUID。用户选不出"设备不存在"的配置。
//   2. 每项标注"需重启"还是"立即生效"。后端/设备/位深/周期改了必须重开流，
//      缓冲与增益可以热改；不标出来用户只能猜。
//   3. 改任何参数都实时重算预计延迟。否则调参是盲调——
//      延迟与稳定性之间的取舍是这个功能存在的全部意义。
//
// 布局全部由测量推导，没有写死的坐标：行高取自字体与控件实测高度，
// 标签列宽取最长标签的实测宽度，两列按可用宽度均分。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Snad
{
    internal sealed class ProfileForm : Form
    {
        private enum Kind { Int, Double, Enum }

        /// <summary>
        /// 一个可编辑项。
        ///
        /// 用委托而不是反射：字段名与 cfg 键名不一致（targetbuffer ↔ TargetBufferMs），
        /// 反射需要额外的映射表与异常处理，显式委托更直白也更快、更易查错。
        /// </summary>
        private sealed class Field
        {
            public string Key;             // cfg 键名
            public string Label;
            public Kind Kind;
            public bool Restart;           // true = 需重启，false = 立即生效
            public double Min, Max, Step;
            public int Decimals;
            public string Unit = "";
            public string[] Options = new string[0];    // Enum 用（存键值）
            public Func<string, string> Display;        // 键值 → 显示文本

            public Func<Profile, double> GetNumber;
            public Action<Profile, double> SetNumber;
            public Func<Profile, string> GetText;
            public Action<Profile, string> SetText;

            // 运行时控件。生效时机不靠控件表达——
            // 那由字段落在哪个分组里决定（见 LayoutFields），
            // 逐行挂"重启"小标签的做法已废弃：黄色小字看起来像可点的按钮，
            // 而且信息重复了十遍，噪声盖过了内容。
            public Label LabelCtl;
            public Control Editor;
        }

        private readonly string _cfgPath;
        private readonly Action<string> _log;
        private readonly Action _onSaved;
        private readonly Dictionary<string, Profile> _profiles;

        private Profile _current;
        private bool _loading;
        private bool _dirty;

        private ListBox _list;
        private Panel _content;
        private Label _summary, _breakdown, _dirtyLabel;

        /// <summary>
        /// 当前被禁用项的**原因**。
        ///
        /// 计划 §4.4 明确要求"不可用原因……而不是静默禁灰"。
        /// 灰掉一个控件却不说为什么，等于让用户去猜——而这里的原因
        /// （后端不适用、设备不支持）恰好是无法从界面上推断出来的。
        /// </summary>
        private Label _notes;
        private FlatButton _saveBtn, _revertBtn, _deleteBtn, _newBtn, _copyBtn;
        private FlatButton _renameBtn, _resetBtn, _exportBtn, _importBtn;

        private readonly List<Field> _fields = new List<Field>();
        private DeviceCapability[] _devices = new DeviceCapability[0];

        // 布局度量，LayoutEditor 与 LayoutFields 共用
        private int _pad, _rowH, _labelW, _listW;

        public ProfileForm(Dictionary<string, Profile> profiles, string cfgPath, string selected,
                           Action<string> log, Action onSaved)
        {
            _profiles = profiles ?? new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
            _cfgPath = cfgPath;
            _log = log;
            _onSaved = onSaved;

            Text = "SNAD  CONFIGURATION";
            Icon = AppIcon.Load();
            BackColor = Theme.BgBase;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(800, 540);
            ClientSize = new Size(900, 548);
            ShowInTaskbar = false;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent;

            // 顺序要紧：先建字段表，再枚举设备。
            // ReloadDevices 是把设备填进"device"那个字段的 Options 里的，
            // 字段表还没建时它什么也填不到——实测表现为设备下拉只剩
            // "跟随系统默认"一项，而界面上看不出任何异常。
            BuildFields();
            ReloadDevices();
            BuildUi();

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.S) { SaveCurrent(); e.Handled = true; }
            };

            FormClosing += (s, e) =>
            {
                if (!_dirty) return;
                // 有未保存改动时明确询问，不静默丢弃
                var r = MessageBox.Show(this, "当前档位有未保存的修改，要保存吗？",
                                        "未保存的修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes) SaveCurrent();
            };
        }

        // ------------------------------------------------------------------ 字段表

        private void BuildFields()
        {
            // ---- 输出链路（改了要重开流）----
            _fields.Add(new Field
            {
                Key = "backend", Label = "BACKEND", Kind = Kind.Enum, Restart = true,
                Options = new[] { "waveout", "wasapi_shared", "wasapi_exclusive" },
                Display = v => v == "waveout" ? "waveOut"
                             : v == "wasapi_shared" ? "WASAPI Shared"
                             : "WASAPI Exclusive",
                GetText = p => p.Backend,
                SetText = (p, v) => p.Backend = v
            });
            _fields.Add(new Field
            {
                Key = "device", Label = "DEVICE", Kind = Kind.Enum, Restart = true,
                Options = new[] { "" },       // ReloadDevices 里填充
                Display = DeviceDisplay,
                GetText = p => p.DeviceId,
                SetText = (p, v) => p.DeviceId = v
            });
            _fields.Add(new Field
            {
                Key = "format", Label = "FORMAT", Kind = Kind.Enum, Restart = true,
                Options = new[] { "pcm16", "pcm24", "pcm32", "float32" },
                Display = v => v,
                GetText = p => p.Format,
                SetText = (p, v) => p.Format = v
            });
            _fields.Add(new Field
            {
                Key = "samplerate", Label = "SAMPLE RATE", Kind = Kind.Enum, Restart = true,
                Options = new[] { "follow", "44100", "48000", "96000" },
                Display = v => v == "follow" ? "follow source" : v + " Hz",
                GetText = p => p.SampleRate,
                SetText = (p, v) => p.SampleRate = v
            });
            _fields.Add(new Field
            {
                Key = "wasapiperiod", Label = "WASAPI PERIOD", Kind = Kind.Int, Restart = true,
                Min = 0, Max = 50, Step = 1, Unit = "ms",
                GetNumber = p => p.WasapiPeriodMs, SetNumber = (p, v) => p.WasapiPeriodMs = (int)v
            });
            // waveOut 的缓冲池深度属于"输出"而不是"漂移"——
            // 它决定同时在飞的音频量，是输出通道的形状参数。
            _fields.Add(new Field
            {
                Key = "waveoutbuffers", Label = "WAVEOUT BUFS", Kind = Kind.Int, Restart = true,
                Min = 2, Max = 64, Step = 1,
                GetNumber = p => p.WaveOutBuffers, SetNumber = (p, v) => p.WaveOutBuffers = (int)v
            });

            // ---- 缓冲 ----
            _fields.Add(new Field
            {
                Key = "targetbuffer", Label = "TARGET BUFFER", Kind = Kind.Int, Restart = false,
                Min = 10, Max = 400, Step = 5, Unit = "ms",
                GetNumber = p => p.TargetBufferMs, SetNumber = (p, v) => p.TargetBufferMs = (int)v
            });

            // ---- 缓冲策略（③）：目标水位由谁决定 ----
            _fields.Add(new Field
            {
                Key = "buffermode", Label = "BUFFER MODE", Kind = Kind.Enum, Restart = false,
                Options = new[] { "static", "adaptive", "calibrated" },
                Display = v => v == "static" ? "static (fixed)"
                             : v == "adaptive" ? "adaptive"
                             : "calibrated",
                GetText = p => p.BufferMode,
                SetText = (p, v) => p.BufferMode = v
            });
            _fields.Add(new Field
            {
                Key = "minbuffer", Label = "MIN BUFFER", Kind = Kind.Int, Restart = false,
                Min = 5, Max = 400, Step = 5, Unit = "ms",
                GetNumber = p => p.MinBufferMs, SetNumber = (p, v) => p.MinBufferMs = (int)v
            });
            _fields.Add(new Field
            {
                Key = "maxbuffer", Label = "MAX BUFFER", Kind = Kind.Int, Restart = false,
                Min = 10, Max = 600, Step = 5, Unit = "ms",
                GetNumber = p => p.MaxBufferMs, SetNumber = (p, v) => p.MaxBufferMs = (int)v
            });
            _fields.Add(new Field
            {
                Key = "adaptrate", Label = "ADAPT RATE", Kind = Kind.Double, Restart = false,
                Min = 0.01, Max = 1.0, Step = 0.05, Decimals = 2,
                GetNumber = p => p.AdaptRate, SetNumber = (p, v) => p.AdaptRate = v
            });
            _fields.Add(new Field
            {
                Key = "calibratesec", Label = "CALIBRATE", Kind = Kind.Int, Restart = false,
                Min = 1, Max = 120, Step = 1, Unit = "s",
                GetNumber = p => p.CalibrateSec, SetNumber = (p, v) => p.CalibrateSec = (int)v
            });
            _fields.Add(new Field
            {
                Key = "prebuffer", Label = "PREBUFFER", Kind = Kind.Int, Restart = true,
                Min = 0, Max = 400, Step = 5, Unit = "ms",
                GetNumber = p => p.PrebufferMs, SetNumber = (p, v) => p.PrebufferMs = (int)v
            });
            _fields.Add(new Field
            {
                Key = "ringcapacity", Label = "RING CAPACITY", Kind = Kind.Int, Restart = true,
                Min = 20, Max = 600, Step = 10, Unit = "ms",
                GetNumber = p => p.RingCapacityMs, SetNumber = (p, v) => p.RingCapacityMs = (int)v
            });
            _fields.Add(new Field
            {
                Key = "block", Label = "BLOCK", Kind = Kind.Int, Restart = true,
                Min = 2, Max = 100, Step = 1, Unit = "ms",
                GetNumber = p => p.BlockMs, SetNumber = (p, v) => p.BlockMs = (int)v
            });

            // ---- 漂移与读节奏（可热改）----
            _fields.Add(new Field
            {
                Key = "lead", Label = "LEAD", Kind = Kind.Int, Restart = false,
                Min = 0, Max = 200, Step = 5, Unit = "ms",
                GetNumber = p => p.LeadMs, SetNumber = (p, v) => p.LeadMs = (int)v
            });
            _fields.Add(new Field
            {
                Key = "driftrange", Label = "DRIFT RANGE", Kind = Kind.Double, Restart = false,
                Min = 0.01, Max = 0.30, Step = 0.01, Decimals = 2,
                GetNumber = p => p.DriftRange, SetNumber = (p, v) => p.DriftRange = v
            });
            _fields.Add(new Field
            {
                Key = "driftgain", Label = "DRIFT GAIN", Kind = Kind.Double, Restart = false,
                Min = 0.01, Max = 0.50, Step = 0.01, Decimals = 2,
                GetNumber = p => p.DriftGain, SetNumber = (p, v) => p.DriftGain = v
            });
            _fields.Add(new Field
            {
                Key = "poll", Label = "POLL", Kind = Kind.Int, Restart = false,
                Min = 1, Max = 20, Step = 1, Unit = "ms",
                GetNumber = p => p.PollMs, SetNumber = (p, v) => p.PollMs = (int)v
            });
            _fields.Add(new Field
            {
                Key = "ratiomin", Label = "RATIO MIN", Kind = Kind.Double, Restart = false,
                Min = 0.50, Max = 1.00, Step = 0.01, Decimals = 2,
                GetNumber = p => p.RatioMin, SetNumber = (p, v) => p.RatioMin = v
            });
            _fields.Add(new Field
            {
                Key = "ratiomax", Label = "RATIO MAX", Kind = Kind.Double, Restart = false,
                Min = 1.00, Max = 1.60, Step = 0.01, Decimals = 2,
                GetNumber = p => p.RatioMax, SetNumber = (p, v) => p.RatioMax = v
            });

            // ---- 超时 ----
            _fields.Add(new Field
            {
                Key = "formatwait", Label = "FORMAT WAIT", Kind = Kind.Int, Restart = true,
                Min = 1, Max = 60, Step = 1, Unit = "s",
                GetNumber = p => p.FormatWaitSec, SetNumber = (p, v) => p.FormatWaitSec = (int)v
            });
            _fields.Add(new Field
            {
                Key = "prebufferwait", Label = "PREBUF WAIT", Kind = Kind.Int, Restart = true,
                Min = 1, Max = 60, Step = 1, Unit = "s",
                GetNumber = p => p.PrebufferWaitSec, SetNumber = (p, v) => p.PrebufferWaitSec = (int)v
            });
        }

        /// <summary>
        /// 枚举设备。设备清单成为档位字段的候选项，能力用于校验。
        /// 探测失败不能静默——档位里填的设备可能在别的机器上不存在。
        /// </summary>
        private void ReloadDevices()
        {
            try
            {
                _devices = DeviceEnumerator.Probe().ToArray();
            }
            catch (Exception ex)
            {
                _devices = new DeviceCapability[0];
                if (_log != null) _log("枚举音频设备失败：" + ex.GetType().Name + ": " + ex.Message);
            }

            var opts = new List<string> { "" };        // 空 = 跟随系统默认
            foreach (var d in _devices) opts.Add(d.Id);

            foreach (var f in _fields)
                if (f.Key == "device") f.Options = opts.ToArray();
        }

        /// <summary>设备 Id 显示成可读名。空表示跟随系统默认。</summary>
        private string DeviceDisplay(string id)
        {
            if (string.IsNullOrEmpty(id)) return "System default";
            foreach (var d in _devices)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d.Name;
            return "(unavailable) " + id;
        }

        // ------------------------------------------------------------------ 界面

        private void BuildUi()
        {
            var fLabel = Theme.Label(8f);
            var fNorm = Theme.Metric(8.5f);

            // ---- 布局度量：全部由字体实测推导，不写死像素 ----
            _pad = 12;
            _rowH = Math.Max(26, (int)Math.Ceiling(Math.Max(fLabel.Height, fNorm.Height) * 1.9f));
            _listW = 180;

            int labelW = 0;
            using (var g = CreateGraphics())
                foreach (var f in _fields)
                {
                    int w = (int)Math.Ceiling(g.MeasureString(f.Label, fLabel).Width);
                    if (w > labelW) labelW = w;
                }
            _labelW = labelW + 10;

            // ---- 顶部说明 ----
            var head = new Label
            {
                Text = "Smaller buffers mean lower latency and less tolerance for network jitter.",
                ForeColor = Theme.TextTertiary,
                Font = fLabel,
                AutoSize = false,
                BackColor = Theme.BgPanel,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(_pad, 0, _pad, 0),
                UseMnemonic = false
            };

            // ---- 左侧档位列表 ----
            _list = new ListBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.BgPanel,
                ForeColor = Theme.TextSecondary,
                Font = fNorm,
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = _rowH,
                TabStop = true
            };
            _list.DrawItem += OnDrawListItem;
            _list.SelectedIndexChanged += (s, e) => OnSelectProfile();

            // ---- 右侧内容 ----
            _content = new Panel { BackColor = Theme.BgBase };

            // ---- 底部：延迟摘要 + 按钮 ----
            _summary = new Label
            {
                Text = "—",
                ForeColor = Theme.TextPrimary,
                Font = Theme.Readout(13f),
                AutoSize = false,
                BackColor = Theme.BgPanel,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(_pad, 0, 0, 0),
                UseMnemonic = false
            };
            _breakdown = new Label
            {
                Text = "",
                ForeColor = Theme.TextTertiary,
                Font = fLabel,
                AutoSize = false,
                BackColor = Theme.BgPanel,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(_pad, 0, 0, 0),
                UseMnemonic = false
            };
            _dirtyLabel = new Label
            {
                Text = "",
                ForeColor = Theme.Warning,
                Font = fLabel,
                AutoSize = false,
                BackColor = Theme.BgPanel,
                TextAlign = ContentAlignment.MiddleRight
            };

            _notes = new Label
            {
                Text = "",
                ForeColor = Theme.Warning,
                Font = fLabel,
                AutoSize = false,
                BackColor = Theme.BgBase,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };

            _saveBtn = new FlatButton("SAVE", true) { Enabled = false };
            _saveBtn.Click += (s, e) => SaveCurrent();
            _revertBtn = new FlatButton("REVERT") { Enabled = false };
            _revertBtn.Click += (s, e) => { LoadIntoEditors(_current); _dirty = false; UpdateDirtyUi(); };
            _deleteBtn = new FlatButton("DELETE");
            _deleteBtn.Click += (s, e) => DeleteCurrent();

            _newBtn = new FlatButton("NEW");
            _newBtn.Click += (s, e) => CreateNew(false);
            _copyBtn = new FlatButton("COPY");
            _copyBtn.Click += (s, e) => CreateNew(true);

            // 计划 §4.5 还要求 重命名 / 重置为内置默认 / 导入 / 导出。
            // 前四项属于"档位生命周期"，与右侧的 REVERT/SAVE（本次编辑）分开摆，
            // 免得把"改一个字段"和"删掉整个档位"混在同一组里。
            _renameBtn = new FlatButton("RENAME");
            _renameBtn.Click += (s, e) => RenameCurrent();
            _resetBtn = new FlatButton("RESET");
            _resetBtn.Click += (s, e) => ResetCurrent();
            _exportBtn = new FlatButton("EXPORT");
            _exportBtn.Click += (s, e) => ExportProfiles();
            _importBtn = new FlatButton("IMPORT");
            _importBtn.Click += (s, e) => ImportProfiles();

            Controls.Add(head);
            Controls.Add(_list);
            Controls.Add(_content);
            Controls.Add(_summary);
            Controls.Add(_breakdown);
            Controls.Add(_dirtyLabel);
            Controls.Add(_notes);
            Controls.Add(_newBtn);
            Controls.Add(_copyBtn);
            Controls.Add(_deleteBtn);
            Controls.Add(_renameBtn);
            Controls.Add(_resetBtn);
            Controls.Add(_exportBtn);
            Controls.Add(_importBtn);
            Controls.Add(_saveBtn);
            Controls.Add(_revertBtn);

            CreateEditors(fLabel, fNorm);

            // 最小尺寸按内容算，而不是写死一个数。
            // 写死过 540，结果加入缓冲策略那 5 个字段后，最小尺寸下
            // 左列最后一行被内容面板裁掉（截图可见 RATIO MAX 被切一半）。
            ApplyContentMinimumSize();

            // 所有定位集中在 LayoutEditor，避免散落各处而逐渐错位
            Resize += (s, e) => LayoutEditor(head);
            LayoutEditor(head);

            ReloadList(null);
        }

        /// <summary>
        /// 为每个字段创建标签、编辑器与"需重启/立即"标记。
        /// 编辑器只在构造时建一次，之后 relocation 只改 Bounds。
        /// </summary>
        private void CreateEditors(Font fLabel, Font fNorm)
        {
            foreach (var f in _fields)
            {
                f.LabelCtl = new Label
                {
                    Text = f.Label,
                    ForeColor = Theme.TextSecondary,
                    Font = fLabel,
                    AutoSize = false,
                    BackColor = Theme.BgBase,
                    TextAlign = ContentAlignment.MiddleLeft
                };

                if (f.Kind == Kind.Enum)
                {
                    var dd = new FlatDropdown { Font = fNorm };
                    var items = new string[f.Options.Length];
                    for (int i = 0; i < f.Options.Length; i++)
                        items[i] = f.Display != null ? f.Display(f.Options[i]) : f.Options[i];
                    dd.Items = items;

                    // 捕获 f 到局部，避免闭包共用循环变量
                    var field = f;
                    dd.SelectionChanged += (s, e) =>
                    {
                        if (_loading) return;
                        MarkDirty();
                        // 后端或设备一变，可选项与生效范围都跟着变：
                        // 独占下设备换了，能选的位深可能完全不同
                        if (field.Key == "backend" || field.Key == "device")
                            UpdateApplicability();
                        else
                            UpdateSummary();
                    };
                    f.Editor = dd;
                }
                else
                {
                    // FlatStepper 的范围/步长/小数位/单位是 Configure 一次设定，
                    // 没有独立属性——与它的既有用法保持一致
                    var st = new FlatStepper { Font = fNorm };
                    st.Configure(f.Min, f.Max, f.Step, f.Decimals, f.Unit);
                    st.ValueChanged += (s, e) => { if (!_loading) { MarkDirty(); UpdateSummary(); } };
                    f.Editor = st;
                }

                // 给控件起名（= 配置键）。自渲染检查工具靠它精确定位某个字段，
                // 否则只能按"项数最多"之类的启发式去猜，字段一多就猜错
                // （实测：加了 BUFFER MODE 之后，测试把位深下拉认成了它）。
                f.Editor.Name = "field:" + f.Key;
                f.LabelCtl.Name = "label:" + f.Key;

                _content.Controls.Add(f.LabelCtl);
                _content.Controls.Add(f.Editor);
            }
        }

        private void MarkDirty()
        {
            if (_loading || _dirty) return;
            _dirty = true;
            UpdateDirtyUi();
        }

        private void LayoutEditor(Control head)
        {
            if (head == null || _list == null || _content == null) return;

            int w = ClientSize.Width, h = ClientSize.Height;
            int headH = Math.Max(30, (int)Math.Ceiling(Theme.Label(8f).Height * 2.2f));
            int btnH = _rowH + 4;
            int footH = _rowH * 3 + btnH + _pad * 2;   // 原因 + 摘要 + 分解 + 按钮行

            head.Bounds = new Rectangle(0, 0, w, headH);

            int bodyTop = headH;
            int bodyH = Math.Max(120, h - headH - footH);

            _list.Bounds = new Rectangle(_pad, bodyTop + _pad, _listW, Math.Max(60, bodyH - _pad * 2));

            int contentX = _pad + _listW + _pad;
            int contentW = Math.Max(220, w - contentX - _pad);
            _content.Bounds = new Rectangle(contentX, bodyTop + _pad, contentW, Math.Max(60, bodyH - _pad * 2));

            int sumTop = bodyTop + bodyH;
            // 顺序：原因 → 延迟摘要 → 分解 → 按钮
            _notes.Bounds = new Rectangle(_pad, sumTop, Math.Max(80, w - _pad * 2), _rowH);
            _summary.Bounds = new Rectangle(0, sumTop + _rowH, Math.Max(80, w), _rowH + 2);
            _breakdown.Bounds = new Rectangle(0, sumTop + _rowH * 2 + 2, Math.Max(80, w), _rowH);

            int btnY = h - btnH - _pad;

            // 两组按钮，按职责分开：
            //   左侧：档位生命周期（新建/复制/重命名/重置/删除/导入/导出）
            //   右侧：本次编辑（撤销/保存）
            // 混在一排会让"删掉整个档位"和"撤销一个字段"看起来同等危险。
            const int bw = 80, gap = 6;
            var leftGroup = new FlatButton[]
            {
                _newBtn, _copyBtn, _renameBtn, _deleteBtn, _resetBtn, _exportBtn, _importBtn
            };
            int bx = _pad;
            foreach (var b in leftGroup)
            {
                b.Bounds = new Rectangle(bx, btnY, bw, btnH);
                bx += bw + gap;
            }

            _saveBtn.Bounds = new Rectangle(w - _pad - bw, btnY, bw, btnH);
            _revertBtn.Bounds = new Rectangle(_saveBtn.Left - gap - bw, btnY, bw, btnH);

            int dirtyW = Math.Max(0, bx - _pad - 8);
            _dirtyLabel.Bounds = new Rectangle(bx, btnY, Math.Max(0, _revertBtn.Left - gap - bx), btnH);
            _ = dirtyW;

            LayoutFields();
        }

        /// <summary>把字段排成两列。左右各两组，分组标题也占一行高度。</summary>
        private void LayoutFields()
        {
            int cw = _content.ClientSize.Width;
            if (cw <= 0) return;

            // 按"改了要不要重开流"分列——这是用户唯一真正需要预先知道的事，
            // 因此让**结构**来回答，而不是在每一行挂一个黄色小标签。
            //
            // 两列各自是完整的一组，标题直接说明后果：
            //   左：改完立刻生效，可以边听边调
            //   右：保存后音频流会重开
            //
            // 分组不再手写键名列表，而是由 Field.Restart 自动划分，
            // 免得分组与字段标记各自演化、逐渐对不上（前一版就出现过
            // "live" 组里混进需重启的项）。
            var live = _fields.FindAll(f => !f.Restart);
            var cold = _fields.FindAll(f => f.Restart);

            int gap = 30;
            int colW = Math.Max(230, (cw - gap) / 2);
            int editorH = Math.Max(20, _rowH - 4);

            LayoutSection("APPLIES IMMEDIATELY", "Tweak while audio is playing",
                          live, 0, colW, editorH);
            LayoutSection("NEEDS RESTART", "The stream reopens when you save",
                          cold, colW + gap, colW, editorH);
        }

        /// <summary>排一组字段：两行标题（名称 + 后果说明）之后接各行。</summary>
        private void LayoutSection(string title, string subtitle, List<Field> items,
                                   int x0, int colW, int editorH)
        {
            int titleH = Math.Max(16, (int)Math.Ceiling(Theme.Label(8.5f).Height * 1.45f));
            int subH = Math.Max(13, (int)Math.Ceiling(Theme.Label(7f).Height * 1.5f));
            int editorW = Math.Max(70, colW - _labelW - 4);

            var tl = new Label
            {
                Text = title,
                ForeColor = Theme.Accent,
                Font = Theme.Label(8.5f),
                AutoSize = false,
                BackColor = Theme.BgBase,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false          // 否则 & 会被当成快捷键前缀吞掉
            };
            _content.Controls.Add(tl);
            tl.Bounds = new Rectangle(x0, 0, colW, titleH);

            var sl = new Label
            {
                Text = subtitle,
                ForeColor = Theme.TextTertiary,
                Font = Theme.Label(7f),
                AutoSize = false,
                BackColor = Theme.BgBase,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };
            _content.Controls.Add(sl);
            sl.Bounds = new Rectangle(x0, titleH, colW, subH);

            int y = titleH + subH + 4;
            foreach (var f in items)
            {
                f.LabelCtl.Bounds = new Rectangle(x0, y, _labelW - 6, editorH);
                f.Editor.Bounds = new Rectangle(x0 + _labelW, y, editorW, editorH);
                y += _rowH;
            }
        }

        // ------------------------------------------------------------------ 档位列表

        private void ReloadList(string select)
        {
            var names = new List<string>(_profiles.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            _loading = true;
            try
            {
                _list.Items.Clear();
                foreach (var n in names) _list.Items.Add(n);

                int idx = 0;
                if (!string.IsNullOrEmpty(select))
                {
                    int found = names.FindIndex(n => string.Equals(n, select, StringComparison.OrdinalIgnoreCase));
                    if (found >= 0) idx = found;
                }
                if (_list.Items.Count > 0) _list.SelectedIndex = idx;
            }
            finally { _loading = false; }

            OnSelectProfile();
        }

        private void OnDrawListItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;

            var g = e.Graphics;
            Theme.SetupQuality(g);

            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = e.Bounds;

            using (var bg = new SolidBrush(sel ? Theme.BgActive : Theme.BgPanel))
                g.FillRectangle(bg, r);
            if (sel)
                using (var br = new SolidBrush(Theme.Accent))
                    g.FillRectangle(br, r.Left, r.Top, 2, r.Height);

            string name = Convert.ToString(_list.Items[e.Index]);
            using (var br = new SolidBrush(sel ? Theme.TextPrimary : Theme.TextSecondary))
                g.DrawString(name, e.Font, br, r.Left + 10, r.Top + (r.Height - e.Font.Height) / 2f);
        }

        private void OnSelectProfile()
        {
            if (_loading) return;
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _list.Items.Count) return;

            // 切档位前把当前改动写回内存对象（不落盘），
            // 否则来回切换会丢掉未保存的修改
            if (_current != null && _dirty) CaptureEditors(_current);

            string name = Convert.ToString(_list.Items[i]);
            Profile p;
            if (!_profiles.TryGetValue(name, out p)) return;

            _current = p;
            LoadIntoEditors(p);
            _dirty = false;
            UpdateDirtyUi();
        }

        // ------------------------------------------------------------------ 加载与保存

        private void LoadIntoEditors(Profile p)
        {
            if (p == null) return;

            _loading = true;
            try
            {
                foreach (var f in _fields)
                {
                    if (f.Kind == Kind.Enum)
                    {
                        var dd = f.Editor as FlatDropdown;
                        if (dd == null) continue;

                        string want = f.GetText(p) ?? "";
                        int at = Array.IndexOf(f.Options, want);
                        dd.SelectedIndex = at >= 0 ? at : 0;
                    }
                    else
                    {
                        var st = f.Editor as FlatStepper;
                        if (st != null) st.Value = f.GetNumber(p);
                    }
                }
            }
            finally { _loading = false; }

            // 载入完再按后端/设备定一次"哪些项生效"，
            // 这样切到另一个档位时，压暗状态也会跟着更新
            UpdateApplicability();
        }

        private void CaptureEditors(Profile p)
        {
            if (p == null) return;
            foreach (var f in _fields)
            {
                if (f.Kind == Kind.Enum)
                {
                    var dd = f.Editor as FlatDropdown;
                    if (dd == null) continue;
                    int i = dd.SelectedIndex;
                    if (i >= 0 && i < f.Options.Length) f.SetText(p, f.Options[i]);
                }
                else
                {
                    var st = f.Editor as FlatStepper;
                    if (st != null) f.SetNumber(p, st.Value);
                }
            }
        }

        private void SaveCurrent()
        {
            if (_current == null) return;
            CaptureEditors(_current);

            // 一致性检查：不一致的参数不阻止保存，但必须说出来，
            // 否则用户会得到一个"能存但行为奇怪"的档位
            var warnings = Validate(_current);
            if (_log != null)
                foreach (var w in warnings) _log("[配置检查] " + _current.Name + "：" + w);

            if (ProfileLibrary.SaveProfile(_cfgPath, _current))
            {
                _dirty = false;
                UpdateDirtyUi();
                if (_log != null) _log("配置已保存：" + _current.Name);
                if (_onSaved != null) _onSaved();
            }
            else if (_log != null) _log("配置保存失败：" + _current.Name);
        }

        /// <summary>参数一致性检查。返回问题描述列表，不抛异常、不阻塞保存。</summary>
        private List<string> Validate(Profile p)
        {
            var w = new List<string>();
            bool wasapi = p.Backend != null &&
                          p.Backend.StartsWith("wasapi", StringComparison.OrdinalIgnoreCase);

            if (!wasapi && p.Format != "pcm16")
                w.Add("waveOut 后端只接受 pcm16，当前位深 " + p.Format + " 不会生效");
            if (!wasapi && p.WasapiPeriodMs > 0)
                w.Add("waveOut 不使用 WASAPI 周期，该值被忽略");

            if (p.RingCapacityMs < p.TargetBufferMs)
                w.Add("环形容量(" + p.RingCapacityMs + "ms) 小于抖动缓冲目标(" +
                      p.TargetBufferMs + "ms)，水位永远到不了目标值");
            if (p.PrebufferMs > p.RingCapacityMs)
                w.Add("起播预填充(" + p.PrebufferMs + "ms) 大于环形容量(" + p.RingCapacityMs + "ms)");
            if (p.RatioMin >= p.RatioMax)
                w.Add("读比率下限(" + p.RatioMin + ") 应小于上限(" + p.RatioMax + ")");
            if (p.BlockMs < 16 && !wasapi)
                w.Add("waveOut 下块时长小于 16ms 实测读速率会低于 48000，建议 16ms 起");

            // 指定采样率而没有重采样：与源不一致时会直接拒绝启动。
            // 与其等运行到一半才发现，不如保存时先说清楚。
            int wantRate = p.DeclaredSampleRate();
            if (wantRate > 0)
                w.Add("采样率固定为 " + wantRate + " Hz：手机若送来别的采样率会拒绝启动" +
                      "（本版无重采样）。不确定就选 follow");

            // 设备相关的组合校验：明确指定设备时，检查后端与位深是否被支持
            if (!string.IsNullOrEmpty(p.DeviceId))
            {
                DeviceCapability dev = null;
                foreach (var d in _devices)
                    if (string.Equals(d.Id, p.DeviceId, StringComparison.OrdinalIgnoreCase)) { dev = d; break; }

                if (dev == null)
                    w.Add("指定的输出设备不在当前系统中，启动时会失败");
                else
                {
                    if (p.Backend == "wasapi_exclusive")
                    {
                        SampleFormat want;
                        switch (p.Format)
                        {
                            case "pcm24": want = SampleFormat.Pcm24; break;
                            case "pcm32": want = SampleFormat.Pcm32; break;
                            case "float32": want = SampleFormat.Float32; break;
                            default: want = SampleFormat.Pcm16; break;
                        }
                        if (!dev.ExclusiveSupports(want))
                            w.Add("设备「" + dev.Name + "」不支持独占模式下的 " + p.Format +
                                  "（可用：" + dev.ExclusiveFormatsText() + "）");
                    }
                    if (p.WasapiPeriodMs > 0 && p.WasapiPeriodMs < dev.MinPeriodMs)
                        w.Add("周期 " + p.WasapiPeriodMs + "ms 低于设备最小周期 " +
                              dev.MinPeriodMs.ToString("F1", CultureInfo.InvariantCulture) +
                              "ms，实际会被抬到下限");
                }
            }
            return w;
        }

        /// <summary>
        /// 新建档位。copy=true 时以当前档位为模板——这是最常用的场景：
        /// 从 balanced 出发调一个自己的档位，而不是从零填 17 个参数。
        /// </summary>
        private void CreateNew(bool copy)
        {
            string seed = copy && _current != null ? _current.Name : "新档位";
            string name = PromptName(copy ? "复制档位" : "新建档位",
                                     copy ? seed + " 副本" : seed);
            if (name == null) return;

            name = name.Trim();
            if (name.Length == 0) return;

            if (_profiles.ContainsKey(name))
            {
                MessageBox.Show(this, "已存在同名档位：" + name, "名称冲突",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Profile created;
            if (copy && _current != null)
            {
                // 先捕获编辑器里的当前值，复制出来的才是"看到的那个"
                CaptureEditors(_current);
                created = CloneFor(_current);
            }
            else
            {
                // 新档位以 balanced 为底，而不是硬编码一堆数字——
                // 内置默认值只应存在于一处
                Profile basis;
                if (!_profiles.TryGetValue("balanced", out basis) || basis == null)
                {
                    var defaults = ProfileLibrary.BuiltIn();
                    if (!defaults.TryGetValue("balanced", out basis) || basis == null)
                    {
                        MessageBox.Show(this, "找不到可作为模板的 balanced 档位，无法新建。",
                                        "新建失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                created = CloneFor(basis);
            }
            created.Name = name;

            // 立刻落盘：新建的档位如果在保存前被切走，改动就无处可存
            if (!ProfileLibrary.SaveProfile(_cfgPath, created))
            {
                MessageBox.Show(this, "写入配置失败，档位未创建。", "新建失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _profiles[name] = created;
            if (_log != null) _log((copy ? "已复制档位：" : "已新建档位：") + name);

            ReloadList(name);
            if (_onSaved != null) _onSaved();
        }

        /// <summary>
        /// 取一个名称。用独立小弹窗而不是输入框对话框——
        /// WinForms 没有内置的取字符串对话框，自己搭一个才能保持配色一致。
        /// 返回 null 表示取消。
        /// </summary>
        private string PromptName(string title, string initial)
        {
            using (var dlg = new Form())
            {
                dlg.Text = title;
                dlg.Icon = AppIcon.Load();
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.BackColor = Theme.BgBase;
                dlg.Font = Theme.Ui(9f);

                var lab = new Label
                {
                    Text = "档位名称",
                    ForeColor = Theme.TextSecondary,
                    Font = Theme.Label(8f),
                    AutoSize = false,
                    BackColor = Theme.BgBase,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var box = new TextBox
                {
                    Text = initial ?? "",
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Theme.BgRaised,
                    ForeColor = Theme.TextPrimary,
                    Font = Theme.Ui(9.5f)
                };
                var ok = new FlatButton("OK", true);
                var cancel = new FlatButton("CANCEL");

                int rowH = Math.Max(26, (int)Math.Ceiling(Theme.Label(8f).Height * 1.9f));
                int pad = 14;
                int w = 340, h = pad * 3 + rowH * 3;

                lab.Bounds = new Rectangle(pad, pad, w - pad * 2, rowH - 6);
                box.Bounds = new Rectangle(pad, pad + rowH, w - pad * 2, rowH);
                ok.Bounds = new Rectangle(w - pad - 84, h - pad - rowH, 84, rowH);
                cancel.Bounds = new Rectangle(ok.Left - 8 - 84, ok.Top, 84, rowH);

                dlg.ClientSize = new Size(w, h);
                dlg.Controls.Add(lab);
                dlg.Controls.Add(box);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);

                string result = null;
                ok.Click += (s, e) => { result = box.Text; dlg.DialogResult = DialogResult.OK; dlg.Close(); };
                cancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
                box.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Enter) { result = box.Text; dlg.DialogResult = DialogResult.OK; dlg.Close(); }
                    else if (e.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); }
                };
                dlg.AcceptButton = null;

                box.SelectAll();
                box.Focus();

                return dlg.ShowDialog(this) == DialogResult.OK ? result : null;
            }
        }

        /// <summary>重命名当前档位。配置文件里是"改段名"，因此要删旧段+写新段。</summary>
        private void RenameCurrent()
        {
            if (_current == null) return;

            string oldName = _current.Name;
            string newName = PromptName("重命名档位", oldName);
            if (newName == null) return;
            newName = newName.Trim();
            if (newName.Length == 0 || newName == oldName) return;

            if (_profiles.ContainsKey(newName))
            {
                MessageBox.Show(this, "已存在同名档位：" + newName, "名称冲突",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 先把编辑器的值收进对象，否则重命名会把未保存的改动丢掉
            CaptureEditors(_current);
            _current.Name = newName;

            // 先写新段再删旧段：中途失败时宁可多一个段，也不要两个都没了
            if (!ProfileLibrary.SaveProfile(_cfgPath, _current))
            {
                _current.Name = oldName;
                MessageBox.Show(this, "写入配置失败，重命名已回滚。", "重命名失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ProfileLibrary.DeleteProfile(_cfgPath, oldName);

            _profiles.Remove(oldName);
            _profiles[newName] = _current;
            if (_log != null) _log("档位已重命名：" + oldName + " → " + newName);

            _dirty = false;
            UpdateDirtyUi();
            ReloadList(newName);
            if (_onSaved != null) _onSaved();
        }

        /// <summary>
        /// 重置为内置默认值。
        ///
        /// 内置档位有代码里的默认值可回退；自定义档位没有"内置默认"，
        /// 因此以 balanced 为底、保留名字——重置的目的是"回到一个已知可用的状态"，
        /// 而不是把档位删掉。
        /// </summary>
        private void ResetCurrent()
        {
            if (_current == null) return;

            var builtins = ProfileLibrary.BuiltIn();
            Profile basis;
            string src;
            if (builtins.TryGetValue(_current.Name, out basis) && basis != null) src = "内置默认值";
            else if (builtins.TryGetValue("balanced", out basis) && basis != null) src = "balanced 默认值";
            else
            {
                MessageBox.Show(this, "找不到可用的内置默认值。", "重置失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var r = MessageBox.Show(this,
                "把「" + _current.Name + "」重置为" + src + "？\n\n当前参数会被覆盖（EQ 设置保留）。",
                "重置档位", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;

            var fresh = CloneFor(basis);
            fresh.Name = _current.Name;            // 名字不变
            fresh.EqEnabled = _current.EqEnabled;  // 调音不因重置参数而丢
            fresh.EqGains = _current.EqGains;
            fresh.EqPreampDb = _current.EqPreampDb;

            if (!ProfileLibrary.SaveProfile(_cfgPath, fresh))
            {
                MessageBox.Show(this, "写入配置失败，未重置。", "重置失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _profiles[fresh.Name] = fresh;
            _current = fresh;
            LoadIntoEditors(fresh);
            _dirty = false;
            UpdateDirtyUi();
            if (_log != null) _log("档位已重置：" + fresh.Name + "（来源：" + src + "）");
            if (_onSaved != null) _onSaved();
        }

        /// <summary>
        /// 导出全部档位到文件。
        ///
        /// 导出的是**配置片段**（[profile:名字] + 键值），因此可以直接
        /// 拼回 receiver.cfg，也可以拿到另一台机器上导入——这就是计划说的"可分享"。
        /// </summary>
        private void ExportProfiles()
        {
            if (_profiles.Count == 0) return;

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "导出档位";
                dlg.Filter = "SNAD 档位 (*.cfg)|*.cfg|所有文件 (*.*)|*.*";
                dlg.FileName = "snad-profiles.cfg";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# SNAD 档位导出");
                sb.AppendLine("# 导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("# 用法：拼回 receiver.cfg，或用 IMPORT 载入");
                sb.AppendLine();

                foreach (var kv in _profiles)
                {
                    var p = kv.Value;
                    sb.AppendLine("[profile:" + p.Name + "]");
                    foreach (var pair in ProfileLibrary.ToPairs(p))
                        sb.AppendLine(pair.Key + " = " + pair.Value);
                    sb.AppendLine();
                }

                try
                {
                    System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                    if (_log != null) _log("已导出 " + _profiles.Count + " 个档位到 " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    // 导出失败必须说出来，否则用户以为文件已经在那儿了
                    MessageBox.Show(this, "导出失败：" + ex.Message, "导出",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (_log != null) _log("导出失败：" + ex.Message);
                }
            }
        }

        /// <summary>从配置文件导入档位。同名档位询问是否覆盖，不静默替换。</summary>
        private void ImportProfiles()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "导入档位";
                dlg.Filter = "SNAD 档位 (*.cfg)|*.cfg|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string selected;
                Dictionary<string, Profile> incoming;
                try
                {
                    incoming = ProfileLibrary.Load(dlg.FileName, out selected);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "读取失败：" + ex.Message, "导入",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (incoming == null || incoming.Count == 0)
                {
                    MessageBox.Show(this, "该文件里没有可识别的档位。", "导入",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int added = 0, replaced = 0, skipped = 0;
                foreach (var kv in incoming)
                {
                    bool exists = _profiles.ContainsKey(kv.Key);
                    if (exists)
                    {
                        var r = MessageBox.Show(this,
                            "档位「" + kv.Key + "」已存在，要覆盖吗？\n\n选「否」则跳过该项。",
                            "同名档位", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (r == DialogResult.Cancel) break;
                        if (r == DialogResult.No) { skipped++; continue; }
                    }

                    if (ProfileLibrary.SaveProfile(_cfgPath, kv.Value))
                    {
                        _profiles[kv.Key] = kv.Value;
                        if (exists) replaced++; else added++;
                    }
                    else if (_log != null) _log("导入失败：" + kv.Key);
                }

                if (_log != null)
                    _log("导入完成：新增 " + added + "，覆盖 " + replaced + "，跳过 " + skipped);

                ReloadList(_current != null ? _current.Name : null);
                if (_onSaved != null) _onSaved();
            }
        }

        private void DeleteCurrent()
        {
            if (_current == null) return;

            var r = MessageBox.Show(this,
                "删除档位「" + _current.Name + "」？\n\n内置档位删除后会回退到代码里的默认值。",
                "删除档位", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;

            string name = _current.Name;
            if (ProfileLibrary.DeleteProfile(_cfgPath, name))
            {
                if (_log != null) _log("已删除档位：" + name);
                _profiles.Remove(name);
                _current = null;
                _dirty = false;
                UpdateDirtyUi();
                ReloadList(null);
                if (_onSaved != null) _onSaved();
            }
            else if (_log != null) _log("删除档位失败（该档位可能没有独立段落）：" + name);
        }

        private void UpdateDirtyUi()
        {
            if (_dirtyLabel != null) _dirtyLabel.Text = _dirty ? "● UNSAVED" : "";
            if (_saveBtn != null) _saveBtn.Enabled = _dirty;
            if (_revertBtn != null) _revertBtn.Enabled = _dirty;
        }

        /// <summary>
        /// 按"字段最多的那一列需要多高"反推窗口最小尺寸。
        ///
        /// 两列的行数是不对称的（热改项比需重启项多），所以不能对两列取平均，
        /// 也不能写死——字段表一变，写死的值就会开始裁内容。
        /// </summary>
        private void ApplyContentMinimumSize()
        {
            int hot = 0, cold = 0;
            foreach (var f in _fields)
            {
                if (f.Restart) cold++; else hot++;
            }
            int rows = Math.Max(hot, cold);

            // 每组两条标题行（名称 + 后果说明）+ 行间距
            int titleH = Math.Max(16, (int)Math.Ceiling(Theme.Label(8.5f).Height * 1.45f));
            int subH = Math.Max(13, (int)Math.Ceiling(Theme.Label(7f).Height * 1.5f));
            int headerBlock = titleH + subH + 4;

            int contentH = headerBlock + rows * _rowH;
            // 留一行余量：算出来刚好等于内容高度时，任何取整都会让最后一行
            // 压到面板边缘并被裁掉（实测就是这样，RATIO MAX 只剩半行可见）
            contentH += _rowH;
            int headH = Math.Max(30, (int)Math.Ceiling(Theme.Label(8f).Height * 2.2f));
            int btnH = _rowH + 4;
            int footH = _rowH * 3 + btnH + _pad * 2;

            int minH = headH + contentH + _pad * 2 + footH;
            int minW = _pad + _listW + _pad + 2 * 240 + 30 + _pad;   // 列表 + 两列各 240 + 间距

            var size = new Size(Math.Max(MinimumSize.Width, minW), Math.Max(MinimumSize.Height, minH));
            MinimumSize = size;

            // 当前尺寸若小于新的最小尺寸，撑开到刚好够——否则一打开就裁内容
            int cw = Math.Max(ClientSize.Width, size.Width);
            int ch = Math.Max(ClientSize.Height, size.Height);
            if (cw != ClientSize.Width || ch != ClientSize.Height)
                ClientSize = new Size(cw, ch);
        }

        /// <summary>
        /// 当前后端/设备决定哪些项真正生效，并把不适用的控件压暗。
        ///
        /// 为什么必须做：waveOut 下 "WASAPI PERIOD" 完全没有作用，
        /// 但界面上它是个可点的步进器——用户会调它、并以为变快了。
        /// 一个可操作却无效的控件比缺失的控件更糟：它制造的是错误认知。
        /// 选择禁用而非隐藏，是为了保持两列布局稳定，
        /// 同时告诉用户"这一项存在，但你的后端用不到它"。
        /// </summary>
        private void UpdateApplicability()
        {
            if (_loading) return;

            var backendF = FindField("backend");
            var formatF = FindField("format");
            var periodF = FindField("wasapiperiod");
            var poolF = FindField("waveoutbuffers");
            if (backendF == null || formatF == null) return;

            string backend = SelectedValue(backendF, "waveout");
            string deviceId = SelectedValue(FindField("device"), "");
            bool wasapi = backend.StartsWith("wasapi", StringComparison.OrdinalIgnoreCase);

            // waveOut 用不到 WASAPI 周期；WASAPI 用不到 waveOut 的缓冲池
            if (periodF != null && periodF.Editor != null) periodF.Editor.Enabled = wasapi;
            if (poolF != null && poolF.Editor != null) poolF.Editor.Enabled = !wasapi;

            // 位深只在独占模式下可选：
            //   waveOut 只接受 pcm16；
            //   共享模式的格式由系统混音格式决定，选什么都没用；
            //   只有独占才会真正按所选位深协商。
            string[] opts = FormatOptionsFor(backend, deviceId);
            ApplyOptions(formatF, opts);

            bool selectable = backend == "wasapi_exclusive" && opts.Length > 1;
            if (formatF.Editor != null) formatF.Editor.Enabled = selectable;

            // 不可选时把值归到唯一那一项，避免"显示 pcm24、实际 pcm16"
            string cur = SelectedValue(formatF, "pcm16");
            if (Array.IndexOf(opts, cur) < 0 && opts.Length > 0)
            {
                int at = Array.IndexOf(formatF.Options, opts[0]);
                var dd = formatF.Editor as FlatDropdown;
                if (dd != null && at >= 0)
                {
                    _loading = true;
                    try { dd.SelectedIndex = at; }
                    finally { _loading = false; }
                }
            }

            // 把"为什么这些控件是灰的"写在界面上，而不是让用户猜
            var why = new List<string>();
            if (!wasapi)
            {
                why.Add("waveOut 固定 pcm16，位深不可选");
                why.Add("WASAPI 周期对 waveOut 无效");
            }
            else
            {
                why.Add("WAVEOUT BUFS 只对 waveOut 有效");
                if (backend == "wasapi_shared")
                    why.Add("共享模式的位深由系统混音格式决定");
            }

            DeviceCapability dev = FindDevice(deviceId);
            if (dev != null && wasapi && backend == "wasapi_exclusive")
            {
                if (opts.Length == 1)
                    why.Add("该设备独占只支持 " + opts[0]);
                else
                    why.Add("该设备独占支持 " + string.Join("/", opts));
            }
            if (_notes != null)
                _notes.Text = why.Count == 0 ? "" : "禁用原因：" + string.Join("；", why.ToArray());

            UpdateSummary();
        }

        /// <summary>
        /// 当前后端/设备下可选的位深。
        ///
        /// 独占模式按设备实际能力给选项，所以"设备不支持的位深"
        /// 在界面上就选不出来，不必等运行时报错。
        /// </summary>
        private string[] FormatOptionsFor(string backend, string deviceId)
        {
            if (backend == null || !backend.StartsWith("wasapi", StringComparison.OrdinalIgnoreCase))
                return new[] { "pcm16" };

            DeviceCapability dev = FindDevice(deviceId);
            if (dev == null) return new[] { "pcm16" };

            if (backend == "wasapi_shared")
            {
                // 共享模式实际使用设备混音格式，没有选择余地
                return new[] { FormatName(dev.MixSampleFormat) };
            }

            var list = new List<string>();
            if (dev.ExclusiveSupports(SampleFormat.Pcm16)) list.Add("pcm16");
            if (dev.ExclusiveSupports(SampleFormat.Pcm24)) list.Add("pcm24");
            if (dev.ExclusiveSupports(SampleFormat.Pcm32)) list.Add("pcm32");
            if (dev.ExclusiveSupports(SampleFormat.Float32)) list.Add("float32");
            if (list.Count == 0) list.Add("pcm16");
            return list.ToArray();
        }

        private static string FormatName(SampleFormat f)
        {
            switch (f)
            {
                case SampleFormat.Pcm24: return "pcm24";
                case SampleFormat.Pcm32: return "pcm32";
                case SampleFormat.Float32: return "float32";
                default: return "pcm16";
            }
        }

        /// <summary>空 deviceId 表示跟随系统默认，此时用默认设备的能力判断。</summary>
        private DeviceCapability FindDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                try { return DeviceEnumerator.Default(); }
                catch { return null; }
            }
            foreach (var d in _devices)
                if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        private Field FindField(string key)
        {
            foreach (var f in _fields) if (f.Key == key) return f;
            return null;
        }

        private static string SelectedValue(Field f, string fallback)
        {
            if (f == null) return fallback;
            var dd = f.Editor as FlatDropdown;
            if (dd == null) return fallback;
            int i = dd.SelectedIndex;
            return i >= 0 && i < f.Options.Length ? f.Options[i] : fallback;
        }

        /// <summary>替换下拉选项，尽量保持当前选中值。</summary>
        private void ApplyOptions(Field f, string[] opts)
        {
            var dd = f.Editor as FlatDropdown;
            if (dd == null) return;

            if (f.Options.Length == opts.Length)
            {
                bool same = true;
                for (int i = 0; i < opts.Length; i++)
                    if (f.Options[i] != opts[i]) { same = false; break; }
                if (same) return;
            }

            string cur = SelectedValue(f, null);
            f.Options = opts;

            var items = new string[opts.Length];
            for (int i = 0; i < opts.Length; i++)
                items[i] = f.Display != null ? f.Display(opts[i]) : opts[i];

            _loading = true;
            try
            {
                dd.Items = items;
                int at = cur != null ? Array.IndexOf(opts, cur) : -1;
                dd.SelectedIndex = at >= 0 ? at : 0;
            }
            finally { _loading = false; }
        }

        /// <summary>
        /// 实时重算预计延迟。
        ///
        /// 这是这个界面存在的意义：延迟与稳定性之间的取舍必须当场可见。
        /// </summary>
        private void UpdateSummary()
        {
            if (_current == null || _summary == null || _breakdown == null) return;

            // 用编辑器的当前值（而非档位对象里的旧值）算，
            // 这样拖动步进器时摘要会同步变化
            var probe = CloneFor(_current);
            CaptureEditors(probe);

            // 估算模型已按回环实测改过（见 Profile.EstimatedPipelineLatencyMs 的注释）：
            // 不再把 waveOut 缓冲池计入延迟，输出链路用实测值/后端典型值。
            // 因此这里的数字会明显小于旧版，且分解里带 ~ 的项表示"估计"，
            // 不带则表示实测或配置值。
            int est = probe.EstimatedPipelineLatencyMs();
            _summary.Text = "EST. LATENCY   ~" + est + " ms";

            string s = probe.LatencyBreakdown();
            s += "      absorbs ±" +
                 (probe.TargetBufferMs / 2.0).ToString("F0", CultureInfo.InvariantCulture) +
                 " ms of jitter";
            if (probe.BufferMode != "static")
                s += "   （实际水位随策略变化）";
            _breakdown.Text = s;
        }

        /// <summary>
        /// 复制一个档位对象。
        ///
        /// 这里必须**逐字段**复制：任何漏掉的字段都会在保存时被写成默认值，
        /// 而界面上看不出异常——这类静默丢配置比崩溃更难查。
        /// 新增字段时务必同步这里（ToPairs 与解析处也是三处一起改）。
        /// </summary>
        private static Profile CloneFor(Profile src)
        {
            return new Profile
            {
                Name = src.Name,
                Description = src.Description,
                Backend = src.Backend,
                DeviceId = src.DeviceId,
                Format = src.Format,
                SampleRate = src.SampleRate,
                WasapiPeriodMs = src.WasapiPeriodMs,
                TargetBufferMs = src.TargetBufferMs,
                // 缓冲策略（③）
                BufferMode = src.BufferMode,
                MinBufferMs = src.MinBufferMs,
                MaxBufferMs = src.MaxBufferMs,
                AdaptRate = src.AdaptRate,
                CalibrateSec = src.CalibrateSec,
                PrebufferMs = src.PrebufferMs,
                RingCapacityMs = src.RingCapacityMs,
                BlockMs = src.BlockMs,
                PollMs = src.PollMs,
                WaveOutBuffers = src.WaveOutBuffers,
                LeadMs = src.LeadMs,
                DriftRange = src.DriftRange,
                DriftGain = src.DriftGain,
                RatioMin = src.RatioMin,
                RatioMax = src.RatioMax,
                FormatWaitSec = src.FormatWaitSec,
                PrebufferWaitSec = src.PrebufferWaitSec,
                // 标定与上报（②④）
                OutputLegMs = src.OutputLegMs,
                CalibratedOn = src.CalibratedOn,
                ReportedCaptureMs = src.ReportedCaptureMs,
                EqEnabled = src.EqEnabled,
                EqPreampDb = src.EqPreampDb,
                EqGains = src.EqGains
            };
        }
    }
}
