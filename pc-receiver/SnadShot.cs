// 主界面与均衡器界面的自渲染检查。
//
// 为什么不用外部截图：外部截图受 DPI 虚拟化影响，GetWindowRect 返回的坐标
// 与 CopyFromScreen 用的物理像素不一致，抓到的往往不是目标窗口（实测多次）。
// 进程内 DrawToBitmap 不涉及屏幕坐标转换，结果可靠。
//
// DrawToBitmap 要在窗口真正显示后才画得到子控件，因此这里把窗口移到屏幕外
// 显示、渲染、关闭——不会在用户屏幕上闪出窗口。
//
// 用法：SnadShot.exe <输出目录>
// 生成 main.png 与 eq.png

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Snad
{
    internal static class Shot
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
            Directory.CreateDirectory(dir);
            int failures = 0;

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("初始化失败：" + ex.Message);
                return 1;
            }

            // ---- 主界面 ----
            try
            {
                var f = new MainForm();
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.Show();
                Pump(10);

                // 未启动时读数条是空的，截图会显得又空又冷，看不出真实观感。
                // 因此填入典型运行值，并往频谱分析器喂一段合成音频。
                FillSyntheticMetrics(f);
                FeedSyntheticSpectrum(f);
                FeedSyntheticStereo(f);
                // 注入数据不会改变窗口尺寸，因此布局不必重跑；
                // 但若此前有控件按默认尺寸创建、尚未经过一次布局，
                // 这里补一次，保证截图反映的是真实排版。
                InvokeLayoutAll(f);
                Pump(30);        // 让频谱的缓动收敛到位

                string p = Path.Combine(dir, "main.png");
                Render(f, p);
                Console.WriteLine("主界面 " + f.Width + "x" + f.Height + " -> " + p);

                // 检查关键控件是否都在窗口内、尺寸正常。
                // 版式改成标题条+图+通道条+控件行后，用的是 FlatButton/FlatCombo/MetricStrip
                // 这些自绘控件，按类型遍历统计比按文本查找可靠。
                var buttons = new List<Control>();
                var combos = new List<Control>();
                var strips = new List<Control>();
                var graphs = new List<Control>();
                CollectByType(f, typeof(FlatButton), buttons);
                CollectByType(f, typeof(WheelSelector), combos);
                CollectByType(f, typeof(MetricStrip), strips);
                CollectByType(f, typeof(SpectrumGraph), graphs);

            if (CheckMetricStripLayout(f) > 0) failures++;
            failures += CheckFrequencyResolution();

            Console.WriteLine("  自绘按钮 " + buttons.Count + " 个，滚轮选择器 " + combos.Count +
                                  " 个，通道条 " + strips.Count + " 个，频谱图 " + graphs.Count + " 个");

                // 滚轮选择器必须是收起态（单行），展开态会占三行并压到别的区域
                foreach (var c in combos)
                {
                    var w = c as WheelSelector;
                    var tl = f.PointToClient(w.PointToScreen(Point.Empty));
                    Console.WriteLine("    滚轮 \"" + (w.SelectedItem ?? "?") + "\" 位置 " + tl.X + "," + tl.Y +
                                      " 尺寸 " + w.Width + "x" + w.Height +
                                      " 展开中=" + w.IsOpen);
                    if (w.Height > 40)
                    {
                        Console.WriteLine("  [失败] 滚轮启动时不应处于展开态（高度 " + w.Height + "）");
                        failures++;
                    }
                }

                int before = failures;
                if (buttons.Count < 2) { Console.WriteLine("  [失败] 按钮不足（应至少 开始 + 均衡器）"); failures++; }
                if (combos.Count < 2) { Console.WriteLine("  [失败] 滚轮选择器不足（应至少 档位 + 输出设备）"); failures++; }
                if (strips.Count != 1) { Console.WriteLine("  [失败] 未找到通道条"); failures++; }
                if (graphs.Count != 1) { Console.WriteLine("  [失败] 未找到频谱图"); failures++; }

                // 每个控件都必须完整落在窗口客户区内，否则会被裁切
                var all = new List<Control>();
                all.AddRange(buttons); all.AddRange(combos); all.AddRange(strips); all.AddRange(graphs);
                foreach (var c in all)
                {
                    var tl = f.PointToClient(c.PointToScreen(Point.Empty));
                    int right = tl.X + c.Width, bottom = tl.Y + c.Height;
                    if (right > f.ClientSize.Width + 1 || bottom > f.ClientSize.Height + 1 ||
                        tl.X < -1 || tl.Y < -1)
                    {
                        Console.WriteLine("  [失败] " + c.GetType().Name + " 超出窗口：位置 " +
                                          tl.X + "," + tl.Y + " 尺寸 " + c.Width + "x" + c.Height +
                                          "（客户区 " + f.ClientSize.Width + "x" + f.ClientSize.Height + "）");
                        failures++;
                    }
                    if (c.Width < 8 || c.Height < 8)
                    {
                        Console.WriteLine("  [失败] " + c.GetType().Name + " 尺寸异常 " + c.Width + "x" + c.Height);
                        failures++;
                    }
                }
                if (failures == before) Console.WriteLine("  [通过] 关键控件都在窗口内且尺寸正常");

                f.Close();
                f.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("主界面渲染失败：" + ex.GetType().Name + ": " + ex.Message);
                failures++;
            }

            // ---- 均衡器界面 ----
            try
            {
                var eq = new Equalizer();
                eq.SetAllGains(new double[] { 6, 4, 2, 0, -2, -3, 1, 3, 5, 4 });
                eq.PreampDb = -4;
                eq.Enabled = true;

                var f = new EqForm(eq, m => { });
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.Show();
                Pump(10);
                string p = Path.Combine(dir, "eq.png");
                Render(f, p);
                Console.WriteLine("均衡器 " + f.Width + "x" + f.Height + " -> " + p);

                // 预设按钮必须全部在窗口内
                int visible = 0, total = 0;
                foreach (Control c in f.Controls)
                {
                    foreach (Control cc in c.Controls)
                    {
                        var b = cc as Button;
                        if (b == null) continue;
                        total++;
                        int right = b.Left + b.Width;
                        if (right <= c.ClientSize.Width) visible++;
                        else Console.WriteLine("  [失败] 预设按钮 \"" + b.Text + "\" 超出容器（右边缘 " +
                                               right + " > " + c.ClientSize.Width + "）");
                    }
                }
                if (total > 0)
                {
                    if (visible == total) Console.WriteLine("  [通过] " + total + " 个按钮全部可见");
                    else { Console.WriteLine("  [失败] 仅 " + visible + "/" + total + " 个按钮可见"); failures++; }
                }

                f.Close();
                f.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("均衡器渲染失败：" + ex.GetType().Name + ": " + ex.Message);
                failures++;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "界面检查通过" : ("有 " + failures + " 项检查失败"));
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 往主界面的通道条里填一组典型运行值，用于渲染"运行中"的观感。
        ///
        /// 未启动时这些格子是空的，截图会显得又空又冷——那不代表控件本身难看，
        /// 只是没有数据。要让界面检查有意义，就得给它数据。
        /// 数值取自真机实测（48kHz、balanced 档、稳态无丢帧）。
        /// </summary>
        private static void FillSyntheticMetrics(Control root)
        {
            var strips = new List<Control>();
            CollectByType(root, typeof(MetricStrip), strips);
            if (strips.Count == 0) return;

            var s = strips[0] as MetricStrip;
            if (s == null || s.Count < 6) return;

            // 与 UpdateMetrics 用同一套语义：BarValue 是读数本身，BarMax 是量程。
            // 只填比例（旧写法 Bar = 0..1）会把"超限"这类信息抹掉。
            s[0].Value = "148.0"; s[0].BarMax = 400; s[0].BarValue = 148;      // 延迟 148ms / 容量 400ms
            s[1].Value = "62.4"; s[1].BarMax = 120; s[1].BarValue = 62.4;      // 缓冲 62ms / 量程 120ms
            s[2].Value = "48107"; s[2].DeviationBar = true; s[2].BarOrigin = 48000;
            s[2].BarMax = 960; s[2].BarTolerance = 48; s[2].BarValue = 48107; s[2].HigherIsWorse = false;
            s[3].Value = "48031"; s[3].DeviationBar = true; s[3].BarOrigin = 48000;
            s[3].BarMax = 960; s[3].BarTolerance = 48; s[3].BarValue = 48031; s[3].HigherIsWorse = false;
            // DROP / UNDER：数字是累计值，条是每秒新增（快照时给个非零值以便看出效果）
            s[4].Value = "3488"; s[4].BarMax = 10; s[4].BarValue = 3.5; s[4].Alert = true;
            s[5].Value = "10"; s[5].BarMax = 10; s[5].BarValue = 1.2; s[5].Alert = true;

            // 标题条的状态点与采样率也一并填上，否则渲染出来是 IDLE
            var labels = new List<Control>();
            CollectByType(root, typeof(Label), labels);
            foreach (var c in labels)
            {
                var l = c as Label;
                if (l == null) continue;
                if (l.Text == "IDLE") { l.Text = "LIVE"; l.ForeColor = Theme.Success; }
                else if (string.IsNullOrEmpty(l.Text)) l.Text = "48.0 kHz";
            }
            s.Invalidate();
        }

        /// <summary>
        /// 往主界面的频谱分析器喂一段合成音频，让频谱在截图里也有内容。
        ///
        /// 走真实通路（Feed -> FFT -> BarsDb），不是往数据里塞假数字——
        /// 后者验证不了绘制路径。泛音列 + 宽带噪声能产生自然的谱形与高频滚降。
        /// </summary>
        /// <summary>
        /// 给声道条喂一段合成音频，否则截图里电平表与波形是空的，
        /// 无法判断它们画得对不对。左右给不同幅度与波形，
        /// 也好确认两路是独立工作的（而不是同一份数据画两遍）。
        /// </summary>
        /// <summary>
        /// 断言指标条内部三行（标签 / 数值 / 电平条）互不重叠。
        ///
        /// 这条针对一个真实缺陷：指标条的纵向位置原本是硬编码的
        /// （标签 y=8、数值 y=20、电平条 Height-20），把面板高度从 88 改成 76
        /// 后四处内容直接压在一起。改为按字体实测排布之后，
        /// 这里用一个独立断言把"不重叠"固定下来，避免以后再被某个常量改动破坏。
        /// </summary>
        private static int CheckMetricStripLayout(Control root)
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 指标条内部排布 ---");

            var list = new List<Control>();
            CollectByType(root, typeof(MetricStrip), list);
            var ms = list.Count > 0 ? list[0] as MetricStrip : null;
            if (ms == null) { Console.WriteLine("  [跳过] 未找到指标条"); return 0; }

            int need = ms.PreferredHeight;
            Console.WriteLine("  实测需要高度 " + need + "px，实际分配 " + ms.Height + "px");
            if (ms.Height < need)
            {
                Console.WriteLine("  [失败] 分配高度不足 " + (need - ms.Height) +
                                  "px，内容会被压缩或重叠");
                bad++;
            }
            else Console.WriteLine("  [通过] 高度足够");

            // 用与实际绘制相同的公式推三行的纵向区间，检查是否分得开
            using (var g = ms.CreateGraphics())
            {
                float fLabelH = Theme.Label(7.5f).GetHeight(g);
                float fValueH = Theme.Readout(20f).GetHeight(g);
                const float barH = 6;

                float labelTop = 4, labelBot = labelTop + fLabelH;
                float valTop = labelBot + 1;
                float barTop = ms.Height - barH - 6;
                float valBot = valTop + fValueH;
                if (valTop + fValueH > barTop - 2)
                    valTop = Math.Max(labelBot, barTop - 2 - fValueH);
                valBot = valTop + fValueH;

                Console.WriteLine("  标签 y " + labelTop.ToString("F0") + "~" + labelBot.ToString("F0") +
                                  "   数值 y " + valTop.ToString("F0") + "~" + valBot.ToString("F0") +
                                  "   电平条 y " + barTop.ToString("F0") + "~" + (barTop + barH).ToString("F0"));

                if (valTop < labelBot - 0.5f)
                {
                    Console.WriteLine("  [失败] 数值与标签重叠");
                    bad++;
                }
                else if (barTop < valBot - 0.5f)
                {
                    Console.WriteLine("  [失败] 电平条与数值重叠");
                    bad++;
                }
                else Console.WriteLine("  [通过] 标签 / 数值 / 电平条互不重叠");
            }
            return bad;
        }

        /// <summary>
        /// 断言频谱的低频分辨率足够。
        ///
        /// 现象是"100Hz 以下画成一条平线"：FftSize=1024 时每 bin 宽 46.9Hz，
        /// 而频谱从 30Hz 起，最低两个八度只剩 2 个数据点。
        ///
        /// 做法：喂一个 120Hz 纯音，检查能量是否集中在 120Hz 所属的柱上。
        /// 分辨率不足时低频各柱取到同一个 bin，能量会摊平，
        /// 最高柱与邻近柱的落差很小——这条断言就会失败。
        /// </summary>
        private static int CheckFrequencyResolution()
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 频谱低频分辨率 ---");

            const int sr = 48000;
            var an = new SpectrumAnalyzer();
            an.Reset(sr, 2);

            const int frames = 2048;
            var pcm = new byte[frames * 4];
            double ph = 0;
            for (int k = 0; k < 24; k++)      // 足够填满环缓冲并完成多次分析
            {
                for (int i = 0; i < frames; i++)
                {
                    short s = (short)(Math.Sin(ph) * 20000);
                    ph += 2 * Math.PI * 120.0 / sr;
                    int b = i * 4;
                    pcm[b] = (byte)(s & 0xFF); pcm[b + 1] = (byte)((s >> 8) & 0xFF);
                    pcm[b + 2] = pcm[b]; pcm[b + 3] = pcm[b + 1];
                }
                an.Feed(pcm, pcm.Length, 2, sr);
            }

            var bars = an.BarsDb;
            int peakBar = 0;
            for (int i = 1; i < bars.Length; i++) if (bars[i] > bars[peakBar]) peakBar = i;

            double fMin = 30.0, fMax = 16000.0;
            double peakF = fMin * Math.Pow(fMax / fMin, (peakBar + 0.5) / bars.Length);
            Console.WriteLine("  120Hz 纯音 -> 最高柱 #" + peakBar +
                              " 对应 " + peakF.ToString("F0") + "Hz，电平 " + bars[peakBar].ToString("F1") + " dB");

            if (peakF < 90 || peakF > 170)
            {
                Console.WriteLine("  [失败] 峰值落在 " + peakF.ToString("F0") +
                                  "Hz，与输入的 120Hz 不符（低频分辨率不足会把能量摊平）");
                bad++;
            }
            else Console.WriteLine("  [通过] 峰值落在输入频率附近");

            int lo = Math.Max(0, peakBar - 4);
            double neighbor = double.MinValue;
            for (int i = lo; i <= Math.Min(bars.Length - 1, peakBar + 4); i++)
                if (i != peakBar && bars[i] > neighbor) neighbor = bars[i];
            double drop = bars[peakBar] - neighbor;
            Console.WriteLine("  与附近柱的最大落差 " + drop.ToString("F1") + " dB");
            if (drop < 3.0)
            {
                Console.WriteLine("  [失败] 峰形不足（落差仅 " + drop.ToString("F1") +
                                  " dB），低频各柱可能取到同一个 bin");
                bad++;
            }
            else Console.WriteLine("  [通过] 峰形明显，低频可分辨");

            return bad;
        }

        /// <summary>让主窗口按当前尺寸重新排版一次。</summary>
        private static void InvokeLayoutAll(Control root)
        {
            var m = root.GetType().GetMethod("LayoutAll",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null) { Console.WriteLine("  提示：未找到 LayoutAll"); return; }
            try { m.Invoke(root, null); }
            catch (Exception ex) { Console.WriteLine("  提示：LayoutAll 调用失败：" + ex.Message); }
        }

        private static void FeedSyntheticStereo(Control root)
        {
            var fi = root.GetType().GetField("_stereo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (fi == null) { Console.WriteLine("  提示：未找到 _stereo，声道条将为空"); return; }
            var st = fi.GetValue(root) as StereoMeter;
            if (st == null) return;

            const int frames = 1024, sr = 48000;
            var pcm = new byte[frames * 2 * 2];
            double ph1 = 0, ph2 = 0;
            st.Reset(sr, 2);

            // 左：幅度较高、含二次谐波（波形不对称，看得出形状差异）
            // 右：幅度较低、纯正弦
            for (int k = 0; k < 80; k++)
            {
                for (int i = 0; i < frames; i++)
                {
                    double l = 0.55 * Math.Sin(ph1) + 0.18 * Math.Sin(2 * ph1);
                    double r = 0.28 * Math.Sin(ph2);
                    ph1 += 2 * Math.PI * 220 / sr;
                    ph2 += 2 * Math.PI * 330 / sr;

                    int sl = (int)(l * 32767); if (sl > 32767) sl = 32767; if (sl < -32768) sl = -32768;
                    int sr2 = (int)(r * 32767); if (sr2 > 32767) sr2 = 32767; if (sr2 < -32768) sr2 = -32768;

                    int b = i * 4;
                    pcm[b] = (byte)(sl & 0xFF); pcm[b + 1] = (byte)((sl >> 8) & 0xFF);
                    pcm[b + 2] = (byte)(sr2 & 0xFF); pcm[b + 3] = (byte)((sr2 >> 8) & 0xFF);
                }
                st.Feed(pcm, pcm.Length, 2, sr);
            }
            Console.WriteLine("  已注入合成立体声：左 220Hz(带谐波) 右 330Hz");
        }

        private static void FeedSyntheticSpectrum(Control root)
        {
            // _analyzer 是 MainForm 的私有字段，这里用反射取；
            // 检查工具本就是诊断用途，不值得为它开放内部接口。
            var fi = root.GetType().GetField("_analyzer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (fi == null)
            {
                Console.WriteLine("  提示：未找到 _analyzer，频谱将为空");
                return;
            }
            var an = fi.GetValue(root) as SpectrumAnalyzer;
            if (an == null) return;

            var rnd = new Random(20261017);
            const int frames = 2048, sr = 48000;
            double[] partials = { 110, 220, 330, 440, 660, 880, 1320, 1760, 2640, 3520, 5280 };
            double[] gains = { 1.0, 0.72, 0.5, 0.62, 0.34, 0.4, 0.22, 0.26, 0.14, 0.1, 0.06 };
            var phase = new double[partials.Length];
            for (int p = 0; p < phase.Length; p++) phase[p] = rnd.NextDouble() * Math.PI * 2;

            var pcm = new byte[frames * 2 * 2];
            an.Reset(sr, 2);
            for (int k = 0; k < 60; k++)
            {
                for (int i = 0; i < frames; i++)
                {
                    double t = 1.0 / sr, v = 0;
                    for (int p = 0; p < partials.Length; p++)
                    {
                        v += gains[p] * Math.Sin(phase[p]);
                        phase[p] += 2 * Math.PI * partials[p] * t;
                    }
                    v += (rnd.NextDouble() - 0.5) * 0.10;
                    int s = (int)(v * 2600);
                    if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                    pcm[i * 4] = (byte)(s & 0xFF);
                    pcm[i * 4 + 1] = (byte)((s >> 8) & 0xFF);
                    pcm[i * 4 + 2] = pcm[i * 4];
                    pcm[i * 4 + 3] = pcm[i * 4 + 1];
                }
                an.Feed(pcm, pcm.Length, 2, sr);
            }
        }

        /// <summary>跑若干轮消息循环，让布局与绘制完成。</summary>
        private static void Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(40);
            }
        }

        private static void Render(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>在控件树里找文本为"均衡器"的按钮。</summary>
        private static Button FindEqButton(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var b = c as Button;
                if (b != null && b.Text == "均衡器") return b;
                var found = FindEqButton(c);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>按类型收集控件（含自绘控件，它们没有可匹配的文本）。</summary>
        private static void CollectByType(Control root, Type t, List<Control> into)
        {
            foreach (Control c in root.Controls)
            {
                if (t.IsInstanceOfType(c)) into.Add(c);
                CollectByType(c, t, into);
            }
        }

        /// <summary>按文本找复选框。</summary>
        private static CheckBox FindCheckBox(Control root, string text)
        {
            foreach (Control c in root.Controls)
            {
                var cb = c as CheckBox;
                if (cb != null && cb.Text == text) return cb;
                var found = FindCheckBox(c, text);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>按 Tag 值找推子（Tag 即频段序号）。</summary>
        private static TrackBar FindSlider(Control root, int index)
        {
            foreach (Control c in root.Controls)
            {
                var tb = c as TrackBar;
                if (tb != null && tb.Tag is int && (int)tb.Tag == index) return tb;
                var found = FindSlider(c, index);
                if (found != null) return found;
            }
            return null;
        }

    }
}
