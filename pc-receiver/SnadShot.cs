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

            s[0].Value = "148.0"; s[0].Bar = 0.37;     // 延迟 148ms
            s[1].Value = "62.4"; s[1].Bar = 0.52;      // 缓冲 62ms / 容量 150ms
            s[2].Value = "48107"; s[2].Bar = 1.0; s[2].HigherIsWorse = false;
            s[3].Value = "48031"; s[3].Bar = 1.0; s[3].HigherIsWorse = false;
            s[4].Value = "0"; s[4].Bar = 0;
            s[5].Value = "0"; s[5].Bar = 0;

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
