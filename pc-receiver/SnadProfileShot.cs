// 配置编辑器自渲染检查。
//
// 与 SnadShot / SnadEqShot 同一思路：界面问题只有画出来才看得见。
// DrawToBitmap 是进程内渲染，不受 DPI 虚拟化影响，坐标不会错位。
//
// 除了出图，这里还断言几件"看起来对但可能没接上"的事：
//   - 每个字段都创建了编辑器控件（漏建的话界面上会是空的）
//   - 下拉的候选项数量与设备表一致（设备没枚举到时会是 1 项）
//   - 切换档位后控件值真的变了（只改列表不改控件是常见缺陷）
//   - 改参数后延迟读数随之变化（摘要没接上的话它永远是同一个数）
//
// 用法：SnadProfileShot.exe <输出目录>

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Snad
{
    internal static class ProfileShot
    {
        private static int _fail;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine("  " + (ok ? "[通过] " : "[失败] ") + what);
            if (!ok) _fail++;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : "profile-shot";
            Directory.CreateDirectory(dir);
            Console.WriteLine("=== 配置编辑器自渲染检查 ===");

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // 用临时配置，避免污染真实的 receiver.cfg
                string cfg = Path.Combine(dir, "profiles.cfg");
                if (File.Exists(cfg)) File.Delete(cfg);
                ProfileLibrary.WriteDefault(cfg);

                string selected;
                var profiles = ProfileLibrary.Load(cfg, out selected);
                Check(profiles.Count >= 3, "载入到 " + profiles.Count + " 个档位");

                var logs = new List<string>();
                var f = new ProfileForm(profiles, cfg, selected, m => logs.Add(m), () => { });

                // 必须真正显示窗口，DrawToBitmap 才会画到子控件
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-5000, -5000);
                f.Show();
                for (int i = 0; i < 14; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                // ---- 断言：字段编辑器都建出来了 ----
                var steppers = FindAll<FlatStepper>(f);
                var dropdowns = FindAll<FlatDropdown>(f);
                Check(steppers.Count >= 12, "数字编辑器 " + steppers.Count + " 个（应 ≥12）");
                Check(dropdowns.Count == 3, "下拉编辑器 " + dropdowns.Count + " 个（后端/设备/位深 = 3）");

                // ---- 断言：设备下拉真的枚举到了设备 ----
                // 设备下拉的项数最多（含"跟随系统默认"再加每台设备一项），
                // 用它作为设备下拉来检查；后端只有 3 项、位深只有 4 项。
                int maxItems = 0;
                FlatDropdown deviceDd = null;
                foreach (var d in dropdowns)
                    if (d.Items.Length > maxItems) { maxItems = d.Items.Length; deviceDd = d; }
                Check(maxItems > 1, "设备下拉有 " + maxItems + " 项（含\"跟随系统默认\"）");
                if (deviceDd != null && maxItems > 1)
                    Console.WriteLine("        首项：" + deviceDd.Items[0]);

                // ---- 出图：默认档位 ----
                Save(f, Path.Combine(dir, "profile-editor.png"));

                // ---- 断言：改参数后延迟读数变化 ----
                string before = ReadSummary(f);
                if (steppers.Count > 0)
                {
                    // 找一个"抖动缓冲目标"（它的变化会直接改变预计延迟）
                    var target = steppers[0];
                    foreach (var s in steppers)
                        if (s.Unit == "ms" && s.Max > 300) { target = s; break; }

                    double old = target.Value;
                    target.Value = Math.Min(target.Max, old + 60);
                    for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }

                    string after = ReadSummary(f);
                    Check(before != after, "改缓冲目标后延迟读数变化：" + before + " -> " + after);
                    target.Value = old;
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                }

                // ---- 出图：切到另一个档位 ----
                var list = FindFirst<ListBox>(f);
                if (list != null && list.Items.Count > 1)
                {
                    int otherIdx = list.SelectedIndex == 0 ? 1 : 0;
                    list.SelectedIndex = otherIdx;
                    for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    Save(f, Path.Combine(dir, "profile-editor-2.png"));

                    // 切档后控件值应当变化（至少有一个不同）
                    bool same = true;
                    string cur = ReadSummary(f);
                    if (cur != before) same = false;
                    Check(!same || profiles.Count < 2, "切换档位后摘要随之更新：" + cur);
                }

                // ---- 出图：窄窗口（检验最小尺寸下不重叠）----
                f.ClientSize = f.MinimumSize;
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                Save(f, Path.Combine(dir, "profile-editor-min.png"));
                Check(f.ClientSize.Width >= f.MinimumSize.Width && f.ClientSize.Height >= f.MinimumSize.Height,
                      "最小尺寸下窗口不被压缩：" + f.ClientSize);

                // ---- 断言：后端切换后"哪些项生效"跟着变 ----
                //
                // 这一条针对的是"可操作但无效"的控件：
                // waveOut 下 WASAPI PERIOD 没有作用，必须被压暗；
                // 切到独占后位深才可选，同时 waveOut 的缓冲池失去意义。
                CheckApplicability(f, dir);

                // ---- 断言：日志里没有异常 ----
                bool clean = logs.Count == 0;
                Check(clean, "构造与渲染过程中无告警" + (clean ? "" : "：" + string.Join(" | ", logs.ToArray())));

                f.Close();
                Console.WriteLine();
                Console.WriteLine(_fail == 0 ? "配置编辑器检查通过" : ("有 " + _fail + " 项失败"));
                Console.WriteLine("图已保存到 " + Path.GetFullPath(dir));
                return _fail == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[失败] 异常：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static int deviceDd_Items(FlatDropdown d) { return d.Items.Length; }

        /// <summary>
        /// 验证"不生效的项被压暗"以及"切后端后生效范围会变"。
        ///
        /// 这几条无法靠看图发现：控件明明灰着也画得出来，
        /// 但 Enabled 是不是真的跟着后端走，只能断言。
        /// </summary>
        private static void CheckApplicability(Form f, string dir)
        {
            Console.WriteLine();
            Console.WriteLine("--- 生效范围随后端变化 ---");

            var dropdowns = FindAll<FlatDropdown>(f);
            FlatDropdown backendDd = null;
            foreach (var d in dropdowns)
                for (int i = 0; i < d.Items.Length; i++)
                    if (d.Items[i] == "waveOut") { backendDd = d; break; }

            if (backendDd == null)
            {
                Check(false, "找到后端下拉框");
                return;
            }

            // 按项数把三个下拉分开：设备最多、位深次之、后端恰好 3 项
            FlatDropdown formatDd = null, deviceDd = null;
            foreach (var d in dropdowns)
            {
                if (ReferenceEquals(d, backendDd)) continue;
                if (d.Items.Length == 11 || d.Items.Length > 6) deviceDd = d;
                else if (formatDd == null || d.Items.Length > formatDd.Items.Length) formatDd = d;
            }
            _ = deviceDd;

            // waveOut：位深不可选（只有 pcm16），周期无效
            int woIdx = Array.IndexOf(backendDd.Items, "waveOut");
            backendDd.SelectedIndex = woIdx;
            for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }

            Check(formatDd != null && !formatDd.Enabled, "waveOut 下位深不可选（已压暗）");
            Check(formatDd != null && formatDd.Items.Length == 1 && formatDd.Items[0] == "pcm16",
                  "waveOut 下位深只有 pcm16（实为 " +
                  (formatDd != null ? string.Join("/", formatDd.Items) : "无") + "）");

            var steppers = FindAll<FlatStepper>(f);
            // 周期是唯一上限 50 的整数步进器；缓冲池上限 64
            FlatStepper period = null, pool = null;
            foreach (var s in steppers)
            {
                if (Math.Abs(s.Max - 50) < 0.01 && s.Unit == "ms") period = s;
                if (Math.Abs(s.Max - 64) < 0.01) pool = s;
            }
            Check(period != null && !period.Enabled, "waveOut 下 WASAPI 周期被压暗");
            Check(pool != null && pool.Enabled, "waveOut 下缓冲池可用");

            // 切到独占：位深应可选，且选项来自设备能力
            int exIdx = Array.IndexOf(backendDd.Items, "WASAPI Exclusive");
            if (exIdx < 0) exIdx = backendDd.Items.Length - 1;
            backendDd.SelectedIndex = exIdx;
            for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }

            Check(formatDd != null && formatDd.Enabled, "WASAPI 独占下位深可选");
            Check(period != null && period.Enabled, "WASAPI 独占下周期可用");
            Check(pool != null && !pool.Enabled, "WASAPI 独占下缓冲池被压暗（不受它控制）");
            if (formatDd != null)
                Console.WriteLine("        设备支持的位深：" + string.Join(" / ", formatDd.Items));

            Save(f, Path.Combine(dir, "profile-editor-wasapi.png"));

            // 还原
            backendDd.SelectedIndex = woIdx;
            for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
        }

        /// <summary>读摘要标签文字。摘要没有对外属性，按内容前缀找。</summary>
        private static string ReadSummary(Form f)
        {
            foreach (var c in AllControls(f))
            {
                var l = c as Label;
                if (l != null && l.Text != null &&
                    l.Text.StartsWith("EST. LATENCY", StringComparison.Ordinal))
                    return l.Text;
            }
            return "(未找到摘要)";
        }

        private static void Save(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
                Console.WriteLine("  已出图 " + path + "  (" + bmp.Width + "×" + bmp.Height + ")");
            }
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in AllControls(c)) yield return d;
            }
        }

        private static T FindFirst<T>(Control root) where T : Control
        {
            foreach (var c in AllControls(root)) { var t = c as T; if (t != null) return t; }
            return null;
        }

        private static List<T> FindAll<T>(Control root) where T : Control
        {
            var list = new List<T>();
            foreach (var c in AllControls(root)) { var t = c as T; if (t != null) list.Add(t); }
            return list;
        }
    }
}
