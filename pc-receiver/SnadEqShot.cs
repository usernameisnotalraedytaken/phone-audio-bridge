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
