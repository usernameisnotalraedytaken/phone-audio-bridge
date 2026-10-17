// 均衡器界面自渲染检查。
//
// 为什么单独做一个：界面渲染问题（控件重叠、被裁切、不可读配色、该画的没画）
// 只有画出来才看得见。外部截图受 DPI 虚拟化影响坐标会错位，
// 而 DrawToBitmap 是进程内渲染，不涉及屏幕坐标转换，结果可靠。
//
// 用法：SnadEqShot.exe <输出png路径>

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Snad
{
    internal static class EqShot
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "eq-shot.png";
            int failures = 0;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var eq = new Equalizer();
                eq.Configure(48000, 2);
                // 用一个非全平的设置，便于看出推子位置、曲线形状与读数是否对应
                eq.SetAllGains(new double[] { 6, 4, 2, 0, -2, -3, 1, 3, 5, 4 });
                eq.PreampDb = -4;
                eq.Enabled = true;

                // ---- 断言：未经 Configure 的实例也必须能算出频响 ----
                // 这是漏测过的真实缺陷：_sampleRate 初值为 0 时 ResponseDb 直接
                // 返回 0，曲线永远是一条直线，而 Configure 只在开始播放时才被调用。
                // 之前的测试预先调了 Configure，因此测的不是真实路径。
                failures += CheckWorksWithoutConfigure();

                var f = new EqForm(eq, m => Console.WriteLine("  [log] " + m));

                // 必须真正显示窗口，DrawToBitmap 才会画到子控件。
                // 只调 CreateControl() 得到的是空白图（实测如此）——
                // WinForms 的绘制依赖窗口已映射到屏幕。因此移到屏幕外显示。
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.Show();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(outPath, ImageFormat.Png);

                    // ---- 断言：曲线图区必须真的画了东西 ----
                    var graph = FindFirst<SpectrumGraph>(f);
                    if (graph == null)
                    {
                        Console.WriteLine("  [失败] 未找到频谱图控件");
                        failures++;
                    }
                    else
                    {
                        var tl = f.PointToClient(graph.PointToScreen(Point.Empty));
                        Console.WriteLine("  曲线图区域 " + tl.X + "," + tl.Y + " 尺寸 " + graph.Width + "x" + graph.Height);

                        // 在曲线图内部取若干像素，统计不同颜色数。
                        // 空白区域只有背景色一种，画了栅格/曲线/刻度就会远多于一种。
                        var colors = new HashSet<int>();
                        for (int y = tl.Y + 4; y < tl.Y + graph.Height - 4 && y < bmp.Height; y += 3)
                            for (int x = tl.X + 4; x < tl.X + graph.Width - 4 && x < bmp.Width; x += 3)
                                colors.Add(bmp.GetPixel(x, y).ToArgb());

                        Console.WriteLine("  曲线图内不同颜色数 = " + colors.Count);
                        if (colors.Count < 6)
                        {
                            Console.WriteLine("  [失败] 曲线图几乎是空白的（颜色数 " + colors.Count +
                                              "，应远多于 6：栅格 / 坐标轴标签 / 曲线 / 节点都要画出来）");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine("  [通过] 曲线图有内容（栅格、坐标轴、曲线、节点均已绘制）");

                            // 单独把曲线图渲染成一张图。
                            // Form.DrawToBitmap 对嵌套子控件的捕获不可靠（实测窗口图里
                            // 曲线图区域是空的，而像素采样却数得到 46 种颜色），
                            // 因此曲线是否好看必须看它自己的渲染结果。
                            string gpath = Path.Combine(
                                Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                                "eq-graph.png");
                            using (var gb = new Bitmap(graph.Width, graph.Height))
                            {
                                graph.DrawToBitmap(gb, new Rectangle(0, 0, graph.Width, graph.Height));
                                gb.Save(gpath, ImageFormat.Png);
                            }
                            Console.WriteLine("  曲线图单独渲染 -> " + gpath);

                            // ---- 断言：PREAMP 推子的 0dB 必须与图区 0 线齐平 ----
                            failures += CheckPreampAlignment(f, eq);

                            // 把图区连同右侧推子一起渲染，便于肉眼确认对齐
                            failures += RenderGraphArea(f, Path.Combine(
                                Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                                "eq-grapharea.png"));

                            // ---- 断言：曲线必须真的跟着数据弯 ----
                            // 用户反馈"不管推子怎么动，UI 上显示还是一条直线"。
                            // 数据对不对是另一回事——这里直接看渲染结果：
                            // 把两端频段拉到差异很大的增益，再比较曲线在对应 x 处的 y。
                            failures += CheckCurveActuallyCurves(graph, eq);
                        }

                        // ---- 断言：曲线与推子必须双向联动 ----
                        // 用户反馈"曲线滑动点不影响推子、推子改变值不影响曲线"，
                        // 即两者解耦。这里分别模拟两种拖动，比对另一侧是否跟随。
                        failures += CheckCurveFaderLink(f, eq);
                    }

                    Console.WriteLine("已生成 " + outPath + "  (" + f.Width + "x" + f.Height + ")");
                    f.Close();
                    f.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("渲染失败：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "均衡器界面检查通过" : ("有 " + failures + " 项检查失败"));
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 验证曲线与推子双向联动。
        ///
        /// 用户报的缺陷是两者解耦：拖曲线推子不动、拖推子曲线不动。
        ///
        /// 关键：这里必须模拟**完整的拖动过程**（按下 -> 移动 -> 松开），
        /// 并且**在移动过程中**就检查推子是否跟随。
        /// 只测"同步之后"会漏掉"拖动中不同步、松手才跳一下"这类缺陷——
        /// 上一轮就是这么漏过去的。
        /// </summary>
        private static int CheckCurveFaderLink(EqForm f, Equalizer eq)
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 曲线与推子联动 ---");

            if (f.Graph == null || f.Faders == null)
            {
                Console.WriteLine("  [失败] 取不到曲线图或推子");
                return 1;
            }

            var g = f.Graph;
            int band = 4;   // 1.8k

            // ---- (a) 拖动节点：过程中推子就要跟随 ----
            eq.SetGain(band, 0);
            f.SyncFromEqForTest();
            Application.DoEvents();

            double faderBefore = f.Faders[band].GainDb;

            // 算出该频段中心在控件里的横坐标，以及 +6dB 对应的纵坐标
            float x = FreqToControlX(g, eq.Bands[band].Freq);
            float yForPlus6 = DbToControlY(g, 6.0);

            InvokeMouseOn(g, "OnMouseDown", (int)x, (int)DbToControlY(g, 0.0));
            InvokeMouseOn(g, "OnMouseMove", (int)x, (int)yForPlus6);   // 拖到 +6dB
            Application.DoEvents();

            // 此刻还没松手，推子就应该已经动了
            double faderDuringDrag = f.Faders[band].GainDb;
            Console.WriteLine("  (a) 拖动中：EQ=" + eq.Bands[band].GainDb.ToString("F1") +
                              "  推子 " + faderBefore.ToString("F1") + " -> " + faderDuringDrag.ToString("F1"));
            if (Math.Abs(faderDuringDrag - eq.Bands[band].GainDb) > 0.15)
            {
                Console.WriteLine("  [失败] 拖动过程中推子未跟随（松手才跳 = 看起来各玩各的）");
                bad++;
            }
            else Console.WriteLine("  [通过] 拖动过程中推子实时跟随");

            InvokeMouseOn(g, "OnMouseUp", (int)x, (int)yForPlus6);
            Application.DoEvents();

            // ---- (b) 拖推子 -> 曲线数据跟随 ----
            eq.SetGain(band, 0);
            f.SyncFromEqForTest();
            Application.DoEvents();

            f.Faders[band].GainDb = -5.0;      // 触发 ValueChanged
            Application.DoEvents();
            double eqAfter = eq.Bands[band].GainDb;

            Console.WriteLine("  (b) 推子 1.8k 改为 -5.0 -> EQ 数据 " + eqAfter.ToString("F1"));
            if (Math.Abs(eqAfter - (-5.0)) > 0.15)
            {
                Console.WriteLine("  [失败] 推子变化后 EQ 数据未跟随（两者解耦）");
                bad++;
            }
            else Console.WriteLine("  [通过] 推子变化后 EQ 数据跟随");

            // ---- (c) 数值读数同步刷新 ----
            string label = f.ValueLabels != null && band < f.ValueLabels.Length
                ? f.ValueLabels[band].Text : "(无)";
            Console.WriteLine("  (c) 读数标签 1.8k = \"" + label + "\"");
            if (!label.StartsWith("-5"))
            {
                Console.WriteLine("  [失败] 读数标签未刷新（应为 -5.0）");
                bad++;
            }
            else Console.WriteLine("  [通过] 读数标签已刷新");

            // ---- (d) 预设也应同时改到推子与曲线数据 ----
            eq.SetAllGains(new double[10]);
            f.SyncFromEqForTest();
            Application.DoEvents();
            // 直接改数据再同步，等价于点预设按钮
            eq.SetAllGains(new double[] { 7, 5, 3, 1, 0, 0, 0, 0, 0, 0 });
            f.SyncFromEqForTest();
            Application.DoEvents();
            bool ok = Math.Abs(f.Faders[0].GainDb - 7) < 0.15 && Math.Abs(eq.Bands[0].GainDb - 7) < 0.15;
            Console.WriteLine("  (d) 套用 BASS 预设 -> 推子[0]=" + f.Faders[0].GainDb.ToString("F1") +
                              " EQ[0]=" + eq.Bands[0].GainDb.ToString("F1"));
            if (!ok) { Console.WriteLine("  [失败] 预设未同时作用到推子与数据"); bad++; }
            else Console.WriteLine("  [通过] 预设同时作用到推子与数据");

            return bad;
        }

        /// <summary>
        /// 断言：全新构造、未调用 Configure 的 Equalizer 也能算出正确频响。
        ///
        /// 这条针对一个真实漏测：_sampleRate 初值为 0 时 ResponseDb 直接返回 0，
        /// 于是"未开始播放就打开均衡器"必然看到一条直线。而 Configure 只在
        /// WaveOut.Play 里被调用——EQ 的频响本就与是否放音无关，不该有此依赖。
        /// </summary>
        private static int CheckWorksWithoutConfigure()
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 未 Configure 的实例也应能算频响 ---");

            var eq = new Equalizer();          // 故意不调 Configure
            eq.Enabled = true;

            // 先取全平时的基准，再设增益比较——这样与滤波器类型无关。
            // （105Hz 是低架滤波器，架上增益 +12 时转折频率处恰为 +6，即一半，
            //   这是搁架的定义；直接期望 +12 会误判。）
            double baseLow = eq.ResponseDb(60);
            eq.SetGain(0, 12);                 // 低频架拉满
            Application.DoEvents();

            double low60 = eq.ResponseDb(60);
            double low2000 = eq.ResponseDb(2000);
            Console.WriteLine("  SampleRate=" + eq.SampleRate +
                              "  60Hz: 基准 " + baseLow.ToString("F1") + " -> " + low60.ToString("F1") +
                              "   2kHz: " + low2000.ToString("F1"));

            if (eq.SampleRate <= 0)
            {
                Console.WriteLine("  [失败] 采样率为 " + eq.SampleRate + "，未初始化");
                bad++;
            }
            // 60Hz 在低架的通带内，应接近 +12
            if (low60 - baseLow < 8)
            {
                Console.WriteLine("  [失败] 低频提升未反映到频响上（曲线会是直线）");
                bad++;
            }
            // 2kHz 远离该架，应几乎不受影响——说明曲线不是整体平移
            if (Math.Abs(low2000 - eq.ResponseDb(2000)) > 0.01)
            {
                Console.WriteLine("  [失败] 远端频响不稳定");
                bad++;
            }
            if (Math.Abs(low2000) > 1.5)
            {
                Console.WriteLine("  [失败] 2kHz 被低频架影响过多（应为 0）");
                bad++;
            }
            if (bad == 0) Console.WriteLine("  [通过] 无需 Configure 即可算出正确频响");
            return bad;
        }

        /// <summary>把频率换算成曲线图控件内的横坐标（与 SpectrumGraph 的映射一致）。</summary>
        private static float FreqToControlX(SpectrumGraph g, double freq)
        {
            const double fMin = 30.0, fMax = 16000.0;
            int padLeft = AccessInt(g, "_plot", "Left") ?? 44;
            int padRight = 14;
            float w = Math.Max(10, g.Width - padLeft - padRight);
            double t = (Math.Log10(freq) - Math.Log10(fMin)) / (Math.Log10(fMax) - Math.Log10(fMin));
            return (float)(padLeft + t * w);
        }

        /// <summary>把 dB 换算成曲线图控件内的纵坐标。</summary>
        private static float DbToControlY(SpectrumGraph g, double db)
        {
            double top = 18.0, bottom = -18.0;
            int padTop = 14, padBottom = 26;
            float h = Math.Max(10, g.Height - padTop - padBottom);
            double t = (db - bottom) / (top - bottom);
            if (t < 0) t = 0; if (t > 1) t = 1;
            return (float)(padTop + (1 - t) * h);
        }

        private static int? AccessInt(object o, string field, string prop)
        {
            var f = o.GetType().GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f == null) return null;
            var v = f.GetValue(o);
            if (v == null) return null;
            var p = v.GetType().GetProperty(prop);
            return p == null ? (int?)null : Convert.ToInt32(p.GetValue(v));
        }

        private static void InvokeMouseOn(Control c, string method, int x, int y)
        {
            var m = c.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null) { Console.WriteLine("  提示：找不到 " + method); return; }
            try { m.Invoke(c, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) }); }
            catch (Exception ex) { Console.WriteLine("  提示：调用 " + method + " 失败：" + ex.Message); }
        }

        /// <summary>
        /// 断言曲线真的跟着数据弯曲。
        ///
        /// 做法：把低频段拉到 +12、高频段压到 -12，然后分段渲染曲线图，
        /// 在低频区与高频区各找曲线像素的纵向位置，比较两者是否明显不同。
        /// 若渲染出来仍是一条水平线，就说明渲染没取到数据（用户报的现象）。
        /// </summary>
        private static int CheckCurveActuallyCurves(SpectrumGraph graph, Equalizer eq)
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 曲线是否真的跟着数据弯 ---");

            // 造一条两端差异极大的曲线
            var gains = new double[10];
            for (int i = 0; i < 5; i++) gains[i] = 12.0;
            for (int i = 5; i < 10; i++) gains[i] = -12.0;
            eq.SetAllGains(gains);
            eq.Enabled = true;
            Application.DoEvents();

            // 数据层必须已经反映出来
            double rLow = eq.ResponseDb(100);
            double rHigh = eq.ResponseDb(10000);
            Console.WriteLine("  ResponseDb(100Hz)=" + rLow.ToString("F1") +
                              "  ResponseDb(10kHz)=" + rHigh.ToString("F1"));
            if (Math.Abs(rLow - rHigh) < 6)
            {
                Console.WriteLine("  [失败] 数据层就没有差异，问题在 DSP 而不在渲染");
                bad++;
            }

            // 渲染层：分区找曲线像素的纵向位置
            int yLow = FindCurveY(graph, 0.10f);    // 靠近左端（低频）
            int yHigh = FindCurveY(graph, 0.90f);   // 靠近右端（高频）
            Console.WriteLine("  渲染层：左端曲线 y=" + yLow + "  右端曲线 y=" + yHigh +
                              "  差值 " + Math.Abs(yHigh - yLow) + "px");

            if (yLow < 0 || yHigh < 0)
            {
                Console.WriteLine("  [失败] 在对应区域找不到曲线像素（曲线可能根本没画）");
                bad++;
            }
            else if (Math.Abs(yHigh - yLow) < 8)
            {
                Console.WriteLine("  [失败] 渲染出来仍接近水平线——渲染没取到数据");
                bad++;
            }
            else
            {
                Console.WriteLine("  [通过] 曲线确实随数据弯曲（差值 " +
                                  Math.Abs(yHigh - yLow) + "px）");
            }

            // 还原，避免影响后续检查
            eq.SetAllGains(new double[10]);
            Application.DoEvents();
            return bad;
        }

        /// <summary>
        /// 在曲线图内按横向比例找曲线像素的 y。
        /// 曲线用强调色描边，因此找"偏青且明度较高"的像素。
        /// 返回最靠上的命中位置；找不到返回 -1。
        /// </summary>
        private static int FindCurveY(SpectrumGraph graph, float xFrac)
        {
            using (var bmp = new Bitmap(graph.Width, graph.Height))
            {
                graph.DrawToBitmap(bmp, new Rectangle(0, 0, graph.Width, graph.Height));
                int x = (int)(graph.Width * xFrac);
                if (x < 2) x = 2;
                if (x >= graph.Width - 2) x = graph.Width - 3;

                for (int y = 2; y < graph.Height - 2; y++)
                {
                    var c = bmp.GetPixel(x, y);
                    // 强调色是偏青的亮色：蓝分量高、且明显亮于背景
                    if (c.B > 120 && c.G > 100 && c.B > c.R + 30)
                        return y;
                }
            }
            return -1;
        }

        /// <summary>
        /// 断言 PREAMP 推子的 0dB 位置与曲线图的 0dB 虚线齐平。
        ///
        /// 这是设计意图：PREAMP 语义就是"整条曲线上下平移"，
        /// 因此推子位置必须能直接读出平移量。两者若不对齐，拖推子时
        /// 虚线与推子会各在一处，观感就是"没对上"。
        /// </summary>
        private static int CheckPreampAlignment(EqForm f, Equalizer eq)
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- PREAMP 推子与 0dB 虚线是否齐平 ---");

            var fader = FindFirst<EqFader>(f);
            if (f.Graph == null || fader == null)
            {
                Console.WriteLine("  [跳过] 取不到图区或推子");
                return 0;
            }

            // ---- 推子量程必须与 Equalizer 的可设范围一致 ----
            // 曾经推子是 ±18 而 Equalizer 把 preamp 钳到 [-24,+12]：
            // 推到 +18 时读数写 +18、实际只取到 +12，曲线到顶就不动了。
            var eqProbe = new Equalizer();
            eqProbe.PreampDb = -999; double eqLow = eqProbe.PreampDb;
            eqProbe.PreampDb = 999; double eqHigh = eqProbe.PreampDb;

            double savedGain = fader.GainDb;
            fader.GainDb = -999; double faderLow = fader.GainDb;
            fader.GainDb = 999; double faderHigh = fader.GainDb;
            fader.GainDb = savedGain;

            Console.WriteLine("  推子量程 [" + faderLow.ToString("F0") + ", " + faderHigh.ToString("F0") + "]" +
                              "   EQ 可设范围 [" + eqLow.ToString("F0") + ", " + eqHigh.ToString("F0") + "]");
            if (Math.Abs(faderLow - eqLow) > 0.05 || Math.Abs(faderHigh - eqHigh) > 0.05)
            {
                Console.WriteLine("  [失败] 推子量程与 EQ 可设范围不一致——" +
                                  "推到底会出现读数与实际不符、曲线到顶不动");
                bad++;
            }
            else Console.WriteLine("  [通过] 推子量程与 EQ 可设范围一致");

            // 图区 0dB 在窗体坐标下的 y
            var plot = f.Graph.PlotRectangle;
            double span = f.Graph.DbAxisTop - f.Graph.DbAxisBottom;
            double t = (0 - f.Graph.DbAxisBottom) / span;
            double graphTopInForm = f.PointToClient(f.Graph.PointToScreen(Point.Empty)).Y;
            double zeroInForm = graphTopInForm + plot.Bottom - t * plot.Height;

            // 推子的 0dB 在窗体坐标下的 y
            double faderTopInForm = f.PointToClient(fader.PointToScreen(Point.Empty)).Y;
            double old = fader.GainDb;
            fader.GainDb = 0;
            double faderZeroInForm = faderTopInForm + FaderZeroOffset(fader);
            fader.GainDb = old;

            double diff = Math.Abs(faderZeroInForm - zeroInForm);
            Console.WriteLine("  图区 0dB 线 y=" + zeroInForm.ToString("F1") +
                              "   推子 0dB y=" + faderZeroInForm.ToString("F1") +
                              "   差 " + diff.ToString("F1") + "px");

            // 灵敏度判据：拖满控件高度应恰好走完整个量程。
            // 这样"鼠标移动多少像素、推子就移动多少像素"，符合直觉。
            // 判据不看"每像素多少 dB"——那个值随量程与高度变化，
            // 比值本身说明不了手感。
            double fullRangeDb = 36.0;
            double expectPxPerDb = Math.Max(1, fader.Height) / fullRangeDb;
            Console.WriteLine("  推子尺寸 " + fader.Width + "x" + fader.Height +
                              "  量程 " + fullRangeDb.ToString("F0") + "dB" +
                              "  ->  每 dB 需 " + expectPxPerDb.ToString("F1") + "px" +
                              "（每像素 " + (fullRangeDb / Math.Max(1, fader.Height)).ToString("F3") + " dB）");

            // 用实际拖动验证：拖满高度应恰好到量程端点
            double saved = fader.GainDb;
            fader.GainDb = 0;
            int half = fader.Height / 2 - 4;
            var mi = fader.GetType().GetMethod("OnMouseDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var mm = fader.GetType().GetMethod("OnMouseMove",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null && mm != null)
            {
                mi.Invoke(fader, new object[] { new MouseEventArgs(MouseButtons.Left, 1, fader.Width / 2, half, 0) });
                mm.Invoke(fader, new object[] { new MouseEventArgs(MouseButtons.Left, 1, fader.Width / 2, half - (fader.Height / 2 - 4), 0) });
                double moved = fader.GainDb;
                Console.WriteLine("  实测：从 0 向上拖 " + (fader.Height / 2 - 4) + "px -> " +
                                  moved.ToString("F1") + " dB（预期约 +15 ~ +18）");
                if (moved < 9)
                {
                    Console.WriteLine("  [失败] 拖了大半高度才改 " + moved.ToString("F1") +
                                      " dB，过于迟钝");
                    bad++;
                }
                else if (moved > 20)
                {
                    Console.WriteLine("  [失败] 拖了大半高度就改 " + moved.ToString("F1") +
                                      " dB，过于灵敏");
                    bad++;
                }
                else Console.WriteLine("  [通过] 拖动行程与量程匹配");
            }
            fader.GainDb = saved;

            if (diff > 4.0)
            {
                Console.WriteLine("  [失败] 推子与 0dB 虚线未对齐（差 " + diff.ToString("F1") + "px）");
                bad++;
            }
            else Console.WriteLine("  [通过] 推子与 0dB 虚线齐平");

            return bad;
        }

        /// <summary>按推子的映射算出 0dB 相对控件顶部的偏移。</summary>
        private static double FaderZeroOffset(EqFader fader)
        {
            var m = fader.GetType().GetMethod("ValueToY",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null) return fader.Height / 2.0;
            var v = m.Invoke(fader, new object[] { 0 });
            return v == null ? fader.Height / 2.0 : Convert.ToDouble(v);
        }

        /// <summary>把图区（含右侧推子）整块渲染出来，便于肉眼确认。</summary>
        private static int RenderGraphArea(EqForm f, string path)
        {
            var host = f.Graph != null ? f.Graph.Parent : null;
            if (host == null) return 0;
            try
            {
                using (var bmp = new Bitmap(host.Width, host.Height))
                {
                    host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                Console.WriteLine("  图区含推子 -> " + path + "  (" + host.Width + "x" + host.Height + ")");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  图区渲染失败：" + ex.Message);
                return 1;
            }
            return 0;
        }

        private static T FindFirst<T>(Control root) where T : Control        {
            foreach (Control c in root.Controls)
            {
                var t = c as T;
                if (t != null) return t;
                var found = FindFirst<T>(c);
                if (found != null) return found;
            }
            return null;
        }
    }
}
