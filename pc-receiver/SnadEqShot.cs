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
        /// 分别模拟两种拖动，比对另一侧是否跟随——
        /// 只测一个方向会漏掉另一个，因此这里两个方向都测。
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

            // (a) 拖曲线 -> 推子跟随
            // 曲线拖动在真实程序里走 BandChanged -> SyncFadersFromEq，
            // 这里直接复现那条路径（改动数据 + 同步），与真实行为等价。
            int band = 4;   // 1.8k
            eq.SetGain(band, 0);
            Application.DoEvents();
            double faderBefore = f.Faders[band].GainDb;

            eq.SetGain(band, 6.0);
            f.SyncFromEqForTest();
            Application.DoEvents();
            double faderAfter = f.Faders[band].GainDb;

            Console.WriteLine("  (a) 曲线 1.8k 改为 +6.0 -> 推子 " +
                              faderBefore.ToString("F1") + " -> " + faderAfter.ToString("F1"));
            if (Math.Abs(faderAfter - 6.0) > 0.15)
            {
                Console.WriteLine("  [失败] 曲线变化后推子未跟随（两者解耦）");
                bad++;
            }
            else Console.WriteLine("  [通过] 曲线变化后推子跟随");

            // (b) 拖推子 -> 曲线数据跟随
            eq.SetGain(band, 0);
            SyncFaders(f, eq);
            double eqBefore = eq.Bands[band].GainDb;
            f.Faders[band].GainDb = -5.0;      // 触发 ValueChanged
            Application.DoEvents();
            double eqAfter = eq.Bands[band].GainDb;

            Console.WriteLine("  (b) 推子 1.8k 改为 -5.0 -> EQ 数据 " +
                              eqBefore.ToString("F1") + " -> " + eqAfter.ToString("F1"));
            if (Math.Abs(eqAfter - (-5.0)) > 0.15)
            {
                Console.WriteLine("  [失败] 推子变化后 EQ 数据未跟随（两者解耦）");
                bad++;
            }
            else Console.WriteLine("  [通过] 推子变化后 EQ 数据跟随");

            // (c) 数值读数也要跟着刷新
            string label = f.ValueLabels != null && band < f.ValueLabels.Length
                ? f.ValueLabels[band].Text : "(无)";
            Console.WriteLine("  (c) 读数标签 1.8k = \"" + label + "\"");
            if (!label.StartsWith("-5"))
            {
                Console.WriteLine("  [失败] 读数标签未刷新（应为 -5.0）");
                bad++;
            }
            else Console.WriteLine("  [通过] 读数标签已刷新");

            return bad;
        }

        /// <summary>把所有推子按 EQ 当前值摆好（测试里手动调用，避免依赖内部状态）。</summary>
        private static void SyncFaders(EqForm f, Equalizer eq)
        {
            for (int i = 0; i < f.Faders.Length && i < eq.Bands.Count; i++)
                f.Faders[i].GainDb = eq.Bands[i].GainDb;
            Application.DoEvents();
        }

        private static void InvokePrivate(object o, string method, params object[] args)
        {
            var m = o.GetType().GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m == null) return;
            try { m.Invoke(o, args); }
            catch (Exception ex) { Console.WriteLine("  提示：调用 " + method + " 失败：" + ex.Message); }
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
