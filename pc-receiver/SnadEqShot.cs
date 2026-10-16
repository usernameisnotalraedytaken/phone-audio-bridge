// 均衡器界面自渲染检查。
//
// 为什么单独做一个：界面渲染问题（控件重叠、被裁切、不可读配色）只有画出来才看得见。
// 外部截图受 DPI 虚拟化影响坐标会错位，而 DrawToBitmap 是进程内渲染，
// 不涉及屏幕坐标转换，结果可靠。
//
// 用法：SnadEqShot.exe <输出png路径>

using System;
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
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var eq = new Equalizer();
                // 用一个非全平的设置，便于看出推子位置与数值标签是否对应
                eq.SetAllGains(new double[] { 6, 4, 2, 0, -2, -3, 1, 3, 5, 4 });
                eq.PreampDb = -4;
                eq.Enabled = true;

                var f = new EqForm(eq, m => Console.WriteLine("  [log] " + m));

                // 必须真正显示窗口，DrawToBitmap 才会画到子控件。
                // 只调 CreateControl() 得到的是空白图（实测如此）——
                // WinForms 的绘制依赖窗口已映射到屏幕。
                // 因此移到屏幕外显示、渲染、再关闭。
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();

                // 等一帧，确保布局与绘制都完成
                for (int i = 0; i < 8; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }

                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                }
                Console.WriteLine("已生成 " + outPath + "  (" + f.Width + "x" + f.Height + ")");
                f.Close();
                f.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("渲染失败：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }
    }
}
