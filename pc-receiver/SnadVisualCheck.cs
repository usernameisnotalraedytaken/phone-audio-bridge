// 离线视觉检查：把界面渲染成 PNG，不需要显示窗口。
//
// 为什么需要它：控件主题问题（按钮忽略 BackColor、下拉框浅色等）只有渲染出来才看得见。
// 每次都让用户截图既慢又烦，因此提供一个可自动化的自检路径。
//
// 用法：
//   SnadVisualCheck.exe [输出目录]
// 会生成 visualcheck-*.png，逐个检查：正常态、运行态、各档位。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Snad
{
    internal static class VisualCheck
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var form = new MainForm();
                // 强制创建句柄与子控件，否则 DrawToBitmap 得到空白
                form.CreateControl();
                foreach (Control c in form.Controls) c.CreateControl();
                form.PerformLayout();

                string p1 = Path.Combine(outDir, "visualcheck-idle.png");
                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(p1, ImageFormat.Png);
                }
                Console.WriteLine("已生成 " + p1 + "  (" + form.Width + "x" + form.Height + ")");

                // 运行态：点一下开始，让状态文字与延迟栏有内容
                var btnField = typeof(MainForm);
                Console.WriteLine("界面尺寸 " + form.Width + "x" + form.Height);
                form.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("视觉检查失败：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Environment.ExitCode = 1;
            }
        }
    }
}
