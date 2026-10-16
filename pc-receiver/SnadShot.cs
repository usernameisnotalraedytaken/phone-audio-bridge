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
    }
}
