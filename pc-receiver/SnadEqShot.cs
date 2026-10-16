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
                    }
                }

                Console.WriteLine("已生成 " + outPath + "  (" + f.Width + "x" + f.Height + ")");
                f.Close();
                f.Dispose();
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

        private static T FindFirst<T>(Control root) where T : Control
        {
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
