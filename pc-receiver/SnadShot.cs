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
                string p = Path.Combine(dir, "main.png");
                Render(f, p);
                Console.WriteLine("主界面 " + f.Width + "x" + f.Height + " -> " + p);

                // 检查均衡器按钮是否真的可见、未被其他控件遮挡
                var btn = FindEqButton(f);
                if (btn == null)
                {
                    Console.WriteLine("  [失败] 未找到均衡器按钮");
                    failures++;
                }
                else
                {
                    Console.WriteLine("  均衡器按钮位置 " + btn.Left + "," + btn.Top +
                                      " 尺寸 " + btn.Width + "x" + btn.Height +
                                      " 可见=" + btn.Visible);
                    if (btn.Width < 20 || btn.Height < 20)
                    {
                        Console.WriteLine("  [失败] 按钮尺寸异常");
                        failures++;
                    }
                    // 检查按钮右边缘是否在窗口内
                    var screenPt = f.PointToClient(btn.PointToScreen(new Point(btn.Width - 1, btn.Height / 2)));
                    if (screenPt.X >= f.ClientSize.Width)
                    {
                        Console.WriteLine("  [失败] 按钮超出窗口右边界（" + screenPt.X + " >= " + f.ClientSize.Width + "）");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine("  [通过] 按钮完整位于窗口内（右边缘 x=" + screenPt.X +
                                          "，窗口宽 " + f.ClientSize.Width + "）");
                    }
                }

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

                // ---- 联动拖动行为 ----
                failures += CheckCoupling(f, eq);

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

        /// <summary>
        /// 验证联动拖动：拖动中间某个推子，相邻频段应按高斯权重跟随，
        /// 且远处的频段几乎不动。
        /// </summary>
        private static int CheckCoupling(EqForm f, Equalizer eq)
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 联动拖动 ---");

            var couple = FindCheckBox(f, "联动拖动");
            if (couple == null)
            {
                Console.WriteLine("  [失败] 未找到联动拖动复选框");
                return 1;
            }

            var drag = FindSlider(f, 4);     // 1.8k，正中间
            var near = FindSlider(f, 3);     // 900，紧邻
            var far = FindSlider(f, 9);      // 16k，最远
            if (drag == null || near == null || far == null)
            {
                Console.WriteLine("  [失败] 未找到推子");
                return 1;
            }

            // 先关闭联动：只应动被拖的那个
            couple.Checked = false;
            eq.SetAllGains(new double[10]);
            drag.Value = 0;
            Application.DoEvents();
            drag.Value = 6;
            Application.DoEvents();

            Console.WriteLine("  关闭联动：拖 1.8k 到 +6");
            Console.WriteLine("    1.8k = " + eq.Bands[4].GainDb.ToString("F1") +
                              " / 900 = " + eq.Bands[3].GainDb.ToString("F1") +
                              " / 16k = " + eq.Bands[9].GainDb.ToString("F1"));
            if (Math.Abs(eq.Bands[4].GainDb - 6) > 0.6)
            { Console.WriteLine("  [失败] 被拖频段未到 +6"); bad++; }
            if (Math.Abs(eq.Bands[3].GainDb) > 0.1 || Math.Abs(eq.Bands[9].GainDb) > 0.1)
            { Console.WriteLine("  [失败] 关闭联动时相邻频段仍被带动"); bad++; }
            if (bad == 0) Console.WriteLine("  [通过] 仅被拖频段变化，相邻未受影响");

            // 打开联动：相邻应跟随，且越远跟随越少
            int bad2 = 0;
            couple.Checked = true;
            eq.SetAllGains(new double[10]);
            foreach (var tb in new[] { drag, near, far }) if (tb.Value != 0) tb.Value = 0;
            Application.DoEvents();
            drag.Value = 6;
            Application.DoEvents();

            double g3 = eq.Bands[3].GainDb, g5 = eq.Bands[5].GainDb, g9 = eq.Bands[9].GainDb;
            Console.WriteLine("  开启联动：拖 1.8k 到 +6");
            Console.WriteLine("    1.8k = " + eq.Bands[4].GainDb.ToString("F2") +
                              " / 900 = " + g3.ToString("F2") +
                              " / 3.5k = " + g5.ToString("F2") +
                              " / 16k = " + g9.ToString("F2"));
            if (Math.Abs(eq.Bands[4].GainDb - 6) > 0.6)
            { Console.WriteLine("  [失败] 被拖频段未到 +6"); bad2++; }
            if (g3 < 1.0 || g5 < 1.0)
            { Console.WriteLine("  [失败] 紧邻频段跟随不足（应约 +3.9）"); bad2++; }
            if (Math.Abs(g9) > 0.5)
            { Console.WriteLine("  [失败] 最远频段被带动过多（应几乎为 0）"); bad2++; }
            if (g3 > eq.Bands[4].GainDb || g5 > eq.Bands[4].GainDb)
            { Console.WriteLine("  [失败] 相邻频段跟随量超过了被拖频段"); bad2++; }
            if (bad2 == 0) Console.WriteLine("  [通过] 相邻跟随且按距离衰减，远处几乎不动");

            return bad + bad2;
        }
    }
}
