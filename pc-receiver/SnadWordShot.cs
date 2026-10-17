// 字标放大预览。
//
// 字标只有 240x34，在整窗截图里看不出字距与标记的细节，
// 因此单独放大渲染一张，用于判断"logo 感"是否成立。
//
// 用法：SnadWordShot.exe <输出目录>

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Snad
{
    internal static class WordShot
    {
        /// <summary>
        /// 断言字标完整渲染，末尾不被截断。
        ///
        /// 做法：把字标放进承载窗体，渲染后从右往左找第一个有内容的像素列。
        /// 若它紧贴控件右边界，说明文字被切掉了（曾经副题 "AUDIO BRIDGE"
        /// 显示成 "AUDIO BR"，就是因为宽度写死 240 而实际需要约 255）。
        /// </summary>
        private static int CheckWordmarkFits()
        {
            int bad = 0;
            Console.WriteLine();
            Console.WriteLine("--- 字标是否完整显示 ---");

            using (var host = new Form())
            {
                host.FormBorderStyle = FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-6000, -6000);
                host.BackColor = Theme.BgPanel;
                host.ClientSize = new Size(420, 40);

                var wm = new Wordmark { MainText = "SNAD", SubText = "AUDIO BRIDGE", Height = 36, Left = 0, Top = 0 };
                host.Controls.Add(wm);
                host.Show();
                for (int k = 0; k < 6; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }

                int need = wm.PreferredPixelWidth;
                Console.WriteLine("  需要宽度 " + need + "px，控件宽度 " + wm.Width + "px");
                if (wm.Width < need)
                {
                    Console.WriteLine("  [失败] 控件比内容窄 " + (need - wm.Width) + "px，末尾会被截断");
                    bad++;
                }
                else Console.WriteLine("  [通过] 控件宽度足够容纳内容");

                using (var bmp = new Bitmap(wm.Width, wm.Height))
                {
                    wm.DrawToBitmap(bmp, new Rectangle(0, 0, wm.Width, wm.Height));

                    // 找最右侧有内容的像素列
                    int lastInk = -1;
                    for (int x = wm.Width - 1; x >= 0; x--)
                    {
                        for (int y = 0; y < wm.Height; y++)
                        {
                            var c = bmp.GetPixel(x, y);
                            // 背景是 BgPanel(18,21,27)，明显亮于它的即为内容
                            if (c.R > 40 || c.G > 40 || c.B > 45) { lastInk = x; break; }
                        }
                        if (lastInk >= 0) break;
                    }
                    Console.WriteLine("  最右侧内容像素 x=" + lastInk + "（控件宽 " + wm.Width + "）");
                    if (lastInk < 0)
                    {
                        Console.WriteLine("  [失败] 整块字标没有渲染出任何内容");
                        bad++;
                    }
                    else if (lastInk >= wm.Width - 2)
                    {
                        Console.WriteLine("  [失败] 内容顶到右边界，末尾被截断");
                        bad++;
                    }
                    else Console.WriteLine("  [通过] 末尾留有 " + (wm.Width - 1 - lastInk) + "px 余量，未截断");

                    bmp.Save(Path.Combine(Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                        "wordmark-fit.png"), ImageFormat.Png);
                }
                host.Controls.Remove(wm);
                wm.Dispose();
            }
            return bad;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(dir);
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 三种候选：带标记 / 不带标记 / 更大字距
                var variants = new (string Name, bool Mark, float Tracking)[]
                {
                    ("A 带声波标记", true, 3.0f),
                    ("B 无标记",     false, 3.0f),
                    ("C 大字距",     true, 5.0f),
                };

                const int scale = 4, pad = 20;
                int cellW = 300 * scale + pad * 2;
                int cellH = 40 * scale + pad * 2;
                int W = cellW + 200;
                int H = cellH * variants.Length + pad;

                using (var bmp = new Bitmap(W, H))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        Theme.SetupQuality(g);
                        using (var bg = new SolidBrush(Theme.BgPanel))
                            g.FillRectangle(bg, 0, 0, W, H);

                        for (int i = 0; i < variants.Length; i++)
                        {
                            var v = variants[i];
                            int y = pad + i * cellH;

                            using (var wm = new Wordmark
                            {
                                MainText = "SNAD",
                                SubText = "AUDIO BRIDGE",
                                ShowMark = v.Mark,
                                Tracking = v.Tracking,
                                Width = 300,
                                Height = 40
                            })
                            {
                                // 字标是自绘控件，必须真正显示才会绘制，
                                // 因此放进一个离屏 Form 承载
                                using (var host = new Form())
                                {
                                    host.FormBorderStyle = FormBorderStyle.None;
                                    host.ShowInTaskbar = false;
                                    host.StartPosition = FormStartPosition.Manual;
                                    host.Location = new Point(-6000, -6000);
                                    host.BackColor = Theme.BgPanel;
                                    host.ClientSize = new Size(300, 40);
                                    wm.Dock = DockStyle.Fill;
                                    host.Controls.Add(wm);
                                    host.Show();
                                    for (int k = 0; k < 6; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }

                                    using (var small = new Bitmap(300, 40))
                                    {
                                        wm.DrawToBitmap(small, new Rectangle(0, 0, 300, 40));
                                        g.DrawImage(small, pad, y, 300 * scale, 40 * scale);
                                    }
                                    host.Controls.Remove(wm);
                                }
                            }

                            var f = Theme.Label(9f);
                            using (var br = new SolidBrush(Theme.TextTertiary))
                                g.DrawString(v.Name, f, br, 300 * scale + pad * 2 + 12, y + cellH / 2f - 8);

                            using (var pen = new Pen(Color.FromArgb(60, Theme.Line), 1f))
                                g.DrawLine(pen, pad, y + cellH - pad, W - pad, y + cellH - pad);
                        }
                    }
                    string p = Path.Combine(dir, "wordmark.png");
                    bmp.Save(p, ImageFormat.Png);
                    Console.WriteLine("已生成 " + p + "  (" + W + "x" + H + ")");
                }

                // ---- 断言：字标必须完整显示，不被截断 ----
                return CheckWordmarkFits();
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
