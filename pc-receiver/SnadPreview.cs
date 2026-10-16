// UI 组件预览工具：把频谱图与通道条单独渲染出来。
//
// 为什么要它：这两个组件是本次改版的视觉主体，先把它们调到位再装进主界面，
// 比一次性改完再回头找问题高效得多。
//
// 频谱需要音频输入才会有数据，因此这里合成一段"像音频"的频谱
// （对数频率上的多峰 + 滚降 + 抖动），用于确认绘制效果，
// 不是等真实音频来撞运气。
//
// 用法：SnadPreview.exe <输出目录>

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Snad
{
    internal static class Preview
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(dir);

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

            Console.WriteLine("字体：指标=" + Theme.MetricFontName +
                              "  界面=" + Theme.UiFontName +
                              "  等宽=" + Theme.MonoFontName);

            int failures = 0;

            // ---- 1. 频谱图 + EQ 曲线（有信号） ----
            try
            {
                var an = new SpectrumAnalyzer();
                var eq = new Equalizer();
                eq.Configure(48000, 2);
                eq.Enabled = true;
                // 造一条有起伏的 EQ 曲线，便于看清曲线与节点的关系
                eq.SetAllGains(new double[] { 6, 4.5, 2, 0, -2.5, -3.5, 0.5, 2.5, 4.5, 3 });
                eq.PreampDb = -3;

                var graph = new SpectrumGraph(an) { Eq = eq, Width = 900, Height = 300 };
                FeedSynthetic(an, 60);
                PumpGraph(graph, 30);
                Save(graph, Path.Combine(dir, "graph-active.png"));
                Console.WriteLine("频谱图（有信号） " + graph.Width + "x" + graph.Height);
            }
            catch (Exception ex)
            {
                Console.WriteLine("频谱图渲染失败：" + ex.GetType().Name + ": " + ex.Message);
                failures++;
            }

            // ---- 2. 频谱图（静默） ----
            try
            {
                var an = new SpectrumAnalyzer();
                var eq = new Equalizer();
                eq.Configure(48000, 2);
                eq.Enabled = false;
                var graph = new SpectrumGraph(an) { Eq = eq, Width = 900, Height = 300 };
                PumpGraph(graph, 10);
                Save(graph, Path.Combine(dir, "graph-silent.png"));
                Console.WriteLine("频谱图（静默） " + graph.Width + "x" + graph.Height);
            }
            catch (Exception ex)
            {
                Console.WriteLine("静默态渲染失败：" + ex.Message);
                failures++;
            }

            // ---- 3. 通道条 ----
            try
            {
                var strip = new MetricStrip(6) { Width = 900, Height = 88 };
                FillStrip(strip, running: true);
                Save(strip, Path.Combine(dir, "strip-running.png"));
                Console.WriteLine("通道条（运行中） " + strip.Width + "x" + strip.Height);

                var strip2 = new MetricStrip(6) { Width = 900, Height = 88 };
                FillStrip(strip2, running: false);
                Save(strip2, Path.Combine(dir, "strip-idle.png"));
                Console.WriteLine("通道条（未启动） " + strip2.Width + "x" + strip2.Height);
            }
            catch (Exception ex)
            {
                Console.WriteLine("通道条渲染失败：" + ex.Message);
                failures++;
            }

            // ---- 4. 组合成完整界面预览 ----
            try
            {
                RenderComposite(dir);
                Console.WriteLine("组合预览已生成");
            }
            catch (Exception ex)
            {
                Console.WriteLine("组合预览失败：" + ex.GetType().Name + ": " + ex.Message);
                // 打印堆栈，否则"参数无效"这类 GDI+ 报错根本定位不到是哪一行
                Console.WriteLine(ex.StackTrace);
                failures++;
            }

            // ---- 5. 滚轮选择器的三种状态 ----
            try
            {
                failures += RenderWheelStates(dir);
                Console.WriteLine("滚轮选择器三态已生成");
            }
            catch (Exception ex)
            {
                Console.WriteLine("滚轮三态渲染失败：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                failures++;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "预览生成完毕" : ("有 " + failures + " 项失败"));
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 分别渲染滚轮选择器的三个状态：静止 / 悬停 / 展开。
        ///
        /// 静止与悬停由鼠标事件驱动，离屏渲染时无法自然触发，因此用反射直接设
        /// 对应字段——比伪造鼠标消息可靠，也不会真的移动用户的光标。
        /// </summary>
        private static int RenderWheelStates(string dir)
        {
            int bad = 0;
            string[] items = { "lowlatency", "balanced", "highquality" };

            var w1 = new WheelSelector { Width = 200 };
            w1.Items = items;
            w1.SelectText("balanced");

            var w2 = new WheelSelector { Width = 200 };
            w2.Items = items;
            w2.SelectText("balanced");
            SetPrivate(w2, "_hover", true);

            var w3 = new WheelSelector { Width = 200 };
            w3.Items = items;
            w3.Open();                            // 先展开
            w3.SelectText("balanced");            // 再选中：Open 里 _offset 会被设为当时的 _index，
                                                  // 顺序颠倒会导致选中项不在中间（踩过）
            SetPrivate(w3, "_hover", true);
            SetPrivate(w3, "_expand", 1.0);      // 直接推到展开终态
            SetPrivate(w3, "_offset", 1.0);
            // Open 时 SelectText 还没执行，高度按 0 号项算；按最终状态重新撑开。
            // 否则渲染画布比控件高，滚轮会被画在画布顶部而不是居中（踩过）。
            w3.Height = 75;
            // 让高亮条落在第一行：_offset 为 1 时第一行即选中项
            SetPrivate(w3, "_offset", 1.0);

            using (var b1 = RenderToBitmap(w1))
            using (var b2 = RenderToBitmap(w2))
            using (var b3 = RenderToBitmap(w3))
            {
                // 打印展开态的实际渲染尺寸与关键字段，避免靠猜（这几项都踩过坑）
                Console.WriteLine("  静止态：位图=" + b1.Width + "x" + b1.Height);
                Console.WriteLine("  悬停态：位图=" + b2.Width + "x" + b2.Height);
                Console.WriteLine("  展开态：位图=" + b3.Width + "x" + b3.Height +
                                  " SelectedIndex=" + w3.SelectedIndex +
                                  " _offset=" + GetPrivate(w3, "_offset") +
                                  " _expand=" + GetPrivate(w3, "_expand"));

                // ---- 断言：高亮必须在正中 ----
                // 之前把高亮放在第一行，选中项永远贴顶，观感是"错位"而非"滚轮"。
                // 这里直接比对：展开态下选中项中心应接近控件竖直中点。
                double rowH = w3.Height / 3.0;
                double offset = Convert.ToDouble(GetPrivate(w3, "_offset"));
                double centerY = (offset + 0.5) * rowH;
                double mid = w3.Height / 2.0;
                if (Math.Abs(centerY - mid) > rowH * 0.6)
                {
                    Console.WriteLine("  [失败] 高亮未落在正中：选中项中心 y=" + centerY.ToString("F1") +
                                      "，控件中点 y=" + mid.ToString("F1"));
                    bad++;
                }
                else
                {
                    Console.WriteLine("  [通过] 高亮落在正中（选中项中心 y=" + centerY.ToString("F1") +
                                      "，控件中点 y=" + mid.ToString("F1") + "）");
                }

                // ---- 断言：切换模型的三种行为 ----
                // 交互模型（用户明确要求）：点一下展开并保持；
                // 期间可拖动或按方向键修改；再点一下、或点别处才收起。
                WheelSelector.DebugGestures = true;

                // (a) 点一下 -> 展开并保持
                var wa = new WheelSelector { Width = 200 };
                wa.Items = items;
                wa.SelectText("balanced");
                InvokeMouse(wa, "OnMouseDown", 100, 12);
                InvokeMouse(wa, "OnMouseUp", 100, 12);
                Console.WriteLine("  (a) 点一下：展开=" + wa.IsOpen + " 高度=" + wa.Height);
                if (!wa.IsOpen)
                {
                    Console.WriteLine("  [失败] 点一下未能保持展开");
                    bad++;
                }
                else Console.WriteLine("  [通过] 点一下展开并保持");

                // (b) 拖动 -> 改变选择但**不收起**，且灵敏度合理
                SetPrivate(wa, "_expand", 1.0);
                int before = wa.SelectedIndex;
                InvokeMouse(wa, "OnMouseDown", 100, 40);
                InvokeMouse(wa, "OnMouseMove", 100, 40 - 14);   // 上移一项的距离
                InvokeMouse(wa, "OnMouseUp", 100, 40 - 14);
                Console.WriteLine("  (b) 拖动 14px：选中 " + before + " -> " + wa.SelectedIndex +
                                  " 展开=" + wa.IsOpen);
                if (wa.SelectedIndex == before)
                {
                    Console.WriteLine("  [失败] 拖动 14px 未改变选择（灵敏度过低）");
                    bad++;
                }
                else if (!wa.IsOpen)
                {
                    Console.WriteLine("  [失败] 拖动后自动收起了（应保持展开以便继续微调）");
                    bad++;
                }
                else Console.WriteLine("  [通过] 拖动改变选择且保持展开");

                // (c) 拖动不会一下到底：14px 只应移动一项
                var wc = new WheelSelector { Width = 200 };
                wc.Items = new string[10];
                for (int i = 0; i < 10; i++) wc.Items[i] = "item" + i;
                wc.SelectedIndex = 5;
                wc.Open();
                SetPrivate(wc, "_expand", 1.0);
                int c0 = wc.SelectedIndex;
                InvokeMouse(wc, "OnMouseDown", 100, 60);
                InvokeMouse(wc, "OnMouseMove", 100, 60 - 14);
                InvokeMouse(wc, "OnMouseUp", 100, 60 - 14);
                int steps = Math.Abs(wc.SelectedIndex - c0);
                Console.WriteLine("  (c) 拖动 14px 跨越 " + steps + " 项（应为 1）");
                if (steps != 1)
                {
                    Console.WriteLine("  [失败] 灵敏度异常：14px 跨越了 " + steps + " 项");
                    bad++;
                }
                else Console.WriteLine("  [通过] 灵敏度合理：14px 恰好一项");

                // (d) 展开态再点一下 -> 收起
                var wd = new WheelSelector { Width = 200 };
                wd.Items = items;
                wd.SelectText("balanced");
                wd.Open();
                SetPrivate(wd, "_expand", 1.0);
                InvokeMouse(wd, "OnMouseDown", 100, 30);
                InvokeMouse(wd, "OnMouseUp", 100, 30);
                Console.WriteLine("  (d) 展开态再点一下：展开=" + wd.IsOpen);
                if (wd.IsOpen)
                {
                    Console.WriteLine("  [失败] 再点一下未能收起");
                    bad++;
                }
                else Console.WriteLine("  [通过] 再点一下收起");

                WheelSelector.DebugGestures = false;

                b1.Save(Path.Combine(dir, "wheel-1-idle.png"), ImageFormat.Png);
                b2.Save(Path.Combine(dir, "wheel-2-hover.png"), ImageFormat.Png);
                b3.Save(Path.Combine(dir, "wheel-3-open.png"), ImageFormat.Png);

                // 拼一张放大对照图。
                //
                // 关键：三态用**同一个缩放倍数**绘制，且不按格子高度裁剪缩放。
                // 之前按格子高度等比放大，高的展开态被放得更大，看起来"宽度远宽于前两态"
                // ——那是我绘图缩放造成的假象，不是控件真的变形。
                const int scale = 3, gap = 18, labelW = 96;
                int cellH = 108;
                int cellW = 200 * scale;
                int W = labelW + cellW + 32;
                int H = gap + (cellH + gap) * 3;
                using (var bmp = new Bitmap(W, H))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        Theme.SetupQuality(g);
                        using (var bg = new SolidBrush(Theme.BgBase))
                            g.FillRectangle(bg, 0, 0, W, H);

                        string[] names = { "IDLE", "HOVER", "OPEN" };
                        var bmps = new[] { b1, b2, b3 };
                        var fl = Theme.Label(9f);
                        for (int i = 0; i < 3; i++)
                        {
                            int y = gap + i * (cellH + gap);
                            using (var br = new SolidBrush(Theme.TextTertiary))
                                g.DrawString(names[i], fl, br, 14, y + cellH / 2f - 8);

                            var cell = new Rectangle(labelW, y, cellW + 24, cellH);
                            using (var pen = new Pen(Color.FromArgb(80, Theme.GridMajor), 1f))
                                g.DrawRectangle(pen, cell);

                            // 统一 scale 倍；只做上限约束，不做放大填充
                            int dw = bmps[i].Width * scale, dh = bmps[i].Height * scale;
                            int maxH = cell.Height - 10;
                            if (dh > maxH)
                            {
                                // 超高时按**同一**比例缩小，宽度不会被拉伸
                                float k = (float)maxH / dh;
                                dw = (int)(dw * k);
                                dh = maxH;
                            }
                            g.DrawImage(bmps[i], cell.X + 12, cell.Y + (cell.Height - dh) / 2f, dw, dh);
                        }
                    }
                    bmp.Save(Path.Combine(dir, "wheel-states.png"), ImageFormat.Png);
                }
            }
            return bad;
        }

        private static void SetPrivate(object o, string field, object value)
        {
            var f = o.GetType().GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f == null)
            {
                Console.WriteLine("  提示：未找到字段 " + field + "，该状态可能无法精确渲染");
                return;
            }
            f.SetValue(o, value);
        }

        private static object GetPrivate(object o, string field)
        {
            var f = o.GetType().GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f == null ? "?" : f.GetValue(o);
        }

        /// <summary>调用受保护的 OnClick。用于验证"点选后收起"这条行为。</summary>
        private static void InvokeOnClick(Control c)
        {
            var m = c.GetType().GetMethod("OnClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null)
            {
                Console.WriteLine("  提示：找不到 OnClick，无法验证收起行为");
                return;
            }
            m.Invoke(c, new object[] { EventArgs.Empty });
        }

        /// <summary>调用受保护的鼠标事件方法。必须用完整序列（按下->松开->Click）
        /// 才能复现真实手势，只调其中一个会漏掉"点开被自己收起"这类缺陷。</summary>
        private static void InvokeMouse(Control c, string method, int x, int y)
        {
            var m = c.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null)
            {
                Console.WriteLine("  提示：找不到 " + method);
                return;
            }
            m.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
        }

        /// <summary>
        /// 合成一段"像音频"的立体声 PCM16LE 喂给分析器。
        ///
        /// 用真实信号走真实通路（Feed -> FFT -> BarsDb），
        /// 而不是往 BarsDb 里塞假数字——后者验证不了绘制路径。
        /// 泛音列 + 宽带噪声能产生自然的谱形与高频滚降。
        /// </summary>
        private static void FeedSynthetic(SpectrumAnalyzer an, int blocks)
        {
            var rnd = new Random(20261017);
            const int frames = 2048;
            const int sr = 48000;

            // 基频 + 泛音，模拟有音高的内容；高频gain递减产生自然滚降
            double[] partials = { 110, 220, 330, 440, 660, 880, 1320, 1760, 2640, 3520, 5280 };
            double[] gains = { 1.0, 0.72, 0.5, 0.62, 0.34, 0.4, 0.22, 0.26, 0.14, 0.1, 0.06 };
            var phase = new double[partials.Length];
            for (int p = 0; p < phase.Length; p++) phase[p] = rnd.NextDouble() * Math.PI * 2;

            var pcm = new byte[frames * 2 * 2];   // 立体声 16 位
            an.Reset(sr, 2);

            for (int k = 0; k < blocks; k++)
            {
                for (int i = 0; i < frames; i++)
                {
                    double t = 1.0 / sr;
                    double v = 0;
                    for (int p = 0; p < partials.Length; p++)
                    {
                        v += gains[p] * Math.Sin(phase[p]);
                        phase[p] += 2 * Math.PI * partials[p] * t;
                    }
                    v += (rnd.NextDouble() - 0.5) * 0.10;      // 宽带噪声

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

        /// <summary>
        /// 让频谱的平滑缓动收敛到目标形状。
        ///
        /// 频谱柱每帧只走目标的一部分（上升快下落慢），刚喂完信号时柱高还没到位，
        /// 直接截图会以为是画错了。因此先跑若干次绘制把缓动推到位。
        /// </summary>
        private static void PumpGraph(Control c, int rounds)
        {
            using (var host = new Form())
            {
                host.FormBorderStyle = FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-6000, -6000);
                host.ClientSize = new Size(c.Width, c.Height);
                host.BackColor = Theme.BgBase;
                c.Dock = DockStyle.Fill;
                host.Controls.Add(c);
                host.Show();
                for (int i = 0; i < rounds; i++)
                {
                    host.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(16);
                }
                host.Controls.Remove(c);
            }
        }

        private static void FillStrip(MetricStrip s, bool running)
        {
            if (running)
            {
                s[0].Label = "LATENCY"; s[0].Value = "148.0"; s[0].Unit = "ms";

                s[1].Label = "BUFFER"; s[1].Value = "62.4"; s[1].Unit = "ms";

                s[2].Label = "RX"; s[2].Value = "48107"; s[2].Unit = "fps";

                s[3].Label = "TX"; s[3].Value = "48031"; s[3].Unit = "fps";

                s[4].Label = "DROP"; s[4].Value = "0"; s[4].Unit = "frames";

                s[5].Label = "UNDER"; s[5].Value = "0"; s[5].Unit = "underruns";
            }
            else
            {
                s[0].Label = "LATENCY"; s[0].Value = "—";
                s[1].Label = "BUFFER"; s[1].Value = "—";
                s[2].Label = "RX"; s[2].Value = "—";
                s[3].Label = "TX"; s[3].Value = "—";
                s[4].Label = "DROP"; s[4].Value = "—";
                s[5].Label = "UNDER"; s[5].Value = "—";
            }
        }

        /// <summary>
        /// 把控件渲染成 PNG。
        ///
        /// 关键：Control.DrawToBitmap 对没有父窗口的独立控件无效
        /// （没有窗口句柄，画出来是红色叉线）。必须先放进一个离屏 Form
        /// 并真正显示，让它走完整的绘制路径。
        /// </summary>
        private static void Save(Control c, string path)
        {
            using (var host = new Form())
            {
                host.FormBorderStyle = FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-6000, -6000);
                host.ClientSize = new Size(c.Width, c.Height);
                host.BackColor = Theme.BgBase;
                c.Dock = DockStyle.Fill;
                host.Controls.Add(c);
                host.Show();
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }

                if (c.Width != host.ClientSize.Width || c.Height != host.ClientSize.Height)
                    Console.WriteLine("  提示：控件实际尺寸 " + c.Width + "x" + c.Height +
                                      "，请求 " + host.ClientSize.Width + "x" + host.ClientSize.Height);

                using (var bmp = new Bitmap(c.Width, c.Height))
                {
                    c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                host.Controls.Remove(c);
            }
        }

        /// <summary>把一个控件渲染成位图（内部用离屏 Form 承载，理由见 Save）。</summary>
        private static Bitmap RenderToBitmap(Control c)
        {
            using (var host = new Form())
            {
                host.FormBorderStyle = FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-6000, -6000);
                host.ClientSize = new Size(c.Width, c.Height);
                host.BackColor = Theme.BgBase;
                c.Dock = DockStyle.Fill;
                host.Controls.Add(c);
                host.Show();
                for (int i = 0; i < 25; i++) { host.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(16); }

                var bmp = new Bitmap(c.Width, c.Height);
                c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
                host.Controls.Remove(c);
                return bmp;
            }
        }

        /// <summary>把图与通道条按 K 方向的上下分区拼成一张完整界面预览。</summary>
        private static void RenderComposite(string dir)
        {
            int W = 920, H = 566;
            var an = new SpectrumAnalyzer();
            var eq = new Equalizer();
            eq.Configure(48000, 2);
            eq.Enabled = true;
            eq.SetAllGains(new double[] { 6, 4.5, 2, 0, -2.5, -3.5, 0.5, 2.5, 4.5, 3 });
            eq.PreampDb = -3;
            FeedSynthetic(an, 60);

            var graph = new SpectrumGraph(an) { Eq = eq, Width = W, Height = 336 };
            var strip = new MetricStrip(6) { Width = W, Height = 88 };
            FillStrip(strip, running: true);

            using (var graphBmp = RenderToBitmap(graph))
            using (var stripBmp = RenderToBitmap(strip))
            using (var bmp = new Bitmap(W, H))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    Theme.SetupQuality(g);
                    using (var bg = new SolidBrush(Theme.BgBase))
                        g.FillRectangle(bg, 0, 0, W, H);

                    // 标题条
                    using (var br = new SolidBrush(Theme.BgPanel))
                        g.FillRectangle(br, 0, 0, W, 36);
                    // Theme 返回缓存字体，不要用 using 包（会被 Dispose 导致后续绘制报错）
                    var fTitle = Theme.Ui(11f, FontStyle.Bold);
                    var fSub = Theme.Ui(8f);
                    var fMetric = Theme.Metric(9f);
                    using (var br = new SolidBrush(Theme.TextPrimary))
                        g.DrawString("SNAD", fTitle, br, 16, 9);
                    using (var br = new SolidBrush(Theme.TextTertiary))
                        g.DrawString("AUDIO BRIDGE", fSub, br, 74, 13);

                    // 右侧状态点与采样率
                    using (var br = new SolidBrush(Theme.Success))
                        g.FillEllipse(br, W - 150, 15, 7, 7);
                    using (var br = new SolidBrush(Theme.TextSecondary))
                        g.DrawString("LIVE", fMetric, br, W - 136, 11);
                    using (var br = new SolidBrush(Theme.TextPrimary))
                        g.DrawString("48.0 kHz", fMetric, br, W - 92, 11);

                    g.DrawImage(graphBmp, 0, 40);
                    g.DrawImage(stripBmp, 0, 40 + graph.Height + 6);

                    // 底部控件行
                    int cy = 40 + graph.Height + 6 + strip.Height + 12;
                    DrawFakeButton(g, 16, cy, 180, 34, "BALANCED", false);
                    DrawFakeButton(g, 204, cy, 240, 34, "SYSTEM DEFAULT", false);
                    DrawFakeButton(g, W - 172, cy, 80, 34, "开始", true);
                    DrawFakeButton(g, W - 84, cy, 68, 34, "EQ", false);
                }
                bmp.Save(Path.Combine(dir, "composite.png"), ImageFormat.Png);
            }
        }

        private static void DrawFakeButton(Graphics g, float x, float y, float w, float h, string text, bool primary)
        {
            var r = new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1);
            using (var br = new SolidBrush(primary ? Theme.Accent : Theme.BgRaised))
                Theme.FillRound(g, br, r, 5f);
            using (var pen = new Pen(primary ? Color.FromArgb(160, Theme.Scale(Theme.Accent, 1.15f))
                                             : Color.FromArgb(120, Theme.Line), 1f))
                Theme.DrawRound(g, pen, r, 5f);
            var f = Theme.Ui(9f);
            var sz = g.MeasureString(text, f);
            using (var br = new SolidBrush(primary ? Color.FromArgb(255, 10, 14, 18) : Theme.TextPrimary))
                g.DrawString(text, f, br, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2);
        }
    }
}
