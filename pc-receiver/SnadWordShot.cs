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
