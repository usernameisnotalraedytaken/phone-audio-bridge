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

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "预览生成完毕" : ("有 " + failures + " 项失败"));
            return failures == 0 ? 0 : 1;
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
                s[0].Bar = 0.42; s[0].Tip = "端到端链路延迟估算，不含音频设备自身延迟";

                s[1].Label = "BUFFER"; s[1].Value = "62.4"; s[1].Unit = "ms";
                s[1].Bar = 0.52; s[1].Tip = "抖动缓冲当前水位，越接近容量越危险";

                s[2].Label = "RX"; s[2].Value = "48107"; s[2].Unit = "fps";
                s[2].Bar = 1.0; s[2].HigherIsWorse = false; s[2].Tip = "手机端送入的帧率";

                s[3].Label = "TX"; s[3].Value = "48031"; s[3].Unit = "fps";
                s[3].Bar = 1.0; s[3].HigherIsWorse = false; s[3].Tip = "实际写入声卡的帧率";

                s[4].Label = "DROP"; s[4].Value = "0"; s[4].Unit = "帧";
                s[4].Bar = 0; s[4].Tip = "缓冲溢出丢弃的帧数，非零说明水位偏高";

                s[5].Label = "UNDER"; s[5].Value = "0"; s[5].Unit = "次";
                s[5].Bar = 0; s[5].Tip = "缓冲见底导致的欠载次数，非零说明水位偏低";
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
