// 配置读写往返验证。
//
// 为什么需要它：SaveEq 是就地改文件（为了不冲掉用户注释），
// 这种"改几行"的逻辑容易在边界上出错——段落找不到、键重复、缩进错位。
// 这里用临时文件做完整往返：写入 -> 读回 -> 逐字段比对。
//
// 用法：SnadConfigTest.exe [临时目录]

using System;
using System.Collections.Generic;
using System.IO;

namespace Snad
{
    internal static class ConfigTest
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + what);
            if (!ok) _failures++;
        }

        private static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "snad-cfgtest");
            Directory.CreateDirectory(dir);
            string cfg = Path.Combine(dir, "receiver.cfg");
            if (File.Exists(cfg)) File.Delete(cfg);

            Console.WriteLine("=== SNAD 配置读写往返验证 ===");
            Console.WriteLine("测试文件：" + cfg);
            Console.WriteLine();

            // ---- 1. 首次运行应生成带注释的默认配置 ----
            ProfileLibrary.WriteDefault(cfg);
            Check(File.Exists(cfg), "WriteDefault 生成了配置文件");
            string original = File.ReadAllText(cfg);
            Check(original.Contains("#"), "生成的配置带注释");
            Check(original.Contains("profile = balanced"), "含默认档位选择");

            // ---- 2. 载入默认配置 ----
            string selected;
            var profiles = ProfileLibrary.Load(cfg, out selected);
            Check(profiles.Count >= 3, "载入到 " + profiles.Count + " 个档位（应含内置三个）");
            Check(selected == "balanced", "选中的档位为 balanced（实为 " + selected + "）");

            // ---- 3. EQ 设置写回后应能读回 ----
            Console.WriteLine();
            Console.WriteLine("--- 均衡器设置写回与读回 ---");
            var eq = new Equalizer();
            eq.Configure(48000, 2);
            var want = new double[] { 7, 5, 3, 1, 0, -1, -2, 1, 4, 6 };
            eq.SetAllGains(want);
            eq.PreampDb = -4.5;
            eq.Enabled = true;

            ProfileLibrary.SaveEq(cfg, "balanced", eq);

            string afterSave = File.ReadAllText(cfg);
            Check(afterSave.Contains("#"), "保存后注释仍在（未被整份重写冲掉）");
            Check(afterSave.Contains("eq = on"), "写入了 eq = on");

            var reloaded = ProfileLibrary.Load(cfg, out selected);
            Profile bal;
            Check(reloaded.TryGetValue("balanced", out bal), "重新载入后仍能找到 balanced 档位");
            if (bal != null)
            {
                Check(bal.EqEnabled, "eq 启用状态读回正确（true）");
                Check(Math.Abs(bal.EqPreampDb - (-4.5)) < 0.01, "eqpreamp 读回正确（" + bal.EqPreampDb + "）");
                if (bal.EqGains == null)
                {
                    Check(false, "eqgains 未读回");
                }
                else
                {
                    Check(bal.EqGains.Length == want.Length,
                          "eqgains 长度一致（" + bal.EqGains.Length + " vs " + want.Length + "）");
                    bool allMatch = bal.EqGains.Length == want.Length;
                    for (int i = 0; allMatch && i < want.Length; i++)
                        if (Math.Abs(bal.EqGains[i] - want[i]) > 0.01) allMatch = false;
                    Check(allMatch, "eqgains 每个值都一致");
                }
            }

            // ---- 4. 再次保存应就地更新，不重复追加 ----
            Console.WriteLine();
            Console.WriteLine("--- 重复保存不应产生重复键 ---");
            eq.SetAllGains(new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });
            eq.PreampDb = 0;
            eq.Enabled = false;
            ProfileLibrary.SaveEq(cfg, "balanced", eq);

            string text = File.ReadAllText(cfg);
            int eqCount = CountKey(text, "eq");
            int preampCount = CountKey(text, "eqpreamp");
            int gainsCount = CountKey(text, "eqgains");
            Check(eqCount == 1, "顶层 eq 键只出现 1 次（实为 " + eqCount + "）");
            Check(preampCount == 1, "顶层 eqpreamp 键只出现 1 次（实为 " + preampCount + "）");
            Check(gainsCount == 1, "顶层 eqgains 键只出现 1 次（实为 " + gainsCount + "）");

            reloaded = ProfileLibrary.Load(cfg, out selected);
            if (reloaded.TryGetValue("balanced", out bal))
            {
                Check(!bal.EqEnabled, "再次保存后启用状态为 false");
                Check(Math.Abs(bal.EqPreampDb) < 0.01, "再次保存后前置增益为 0");
            }

            // ---- 5. 写入不存在的档位段时应新建段落 ----
            Console.WriteLine();
            Console.WriteLine("--- 为不存在的档位保存 EQ ---");
            eq.Enabled = true;
            eq.SetGain(0, 9);
            ProfileLibrary.SaveEq(cfg, "自定义档", eq);

            reloaded = ProfileLibrary.Load(cfg, out selected);
            Profile custom;
            Check(reloaded.TryGetValue("自定义档", out custom), "新建的档位段可被载入");
            if (custom != null)
            {
                Check(custom.EqEnabled, "新建档位的 eq 状态正确");
                Check(custom.EqGains != null && custom.EqGains.Length > 0 &&
                      Math.Abs(custom.EqGains[0] - 9) < 0.01,
                      "新建档位的第一个频段增益为 9");
            }

            // ---- 6. 原有档位不应被破坏 ----
            Console.WriteLine();
            Console.WriteLine("--- 其他档位应保持完好 ---");
            foreach (var name in new[] { "lowlatency", "highquality" })
            {
                Profile p;
                bool found = reloaded.TryGetValue(name, out p);
                Check(found, "档位 " + name + " 仍在");
                if (found) Check(p.TargetBufferMs > 0, "  " + name + " 的 targetbuffer 有效（" + p.TargetBufferMs + "ms）");
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "全部断言通过" : ("有 " + _failures + " 项断言失败"));
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>统计顶层（非注释）中某键出现的次数。</summary>
        private static int CountKey(string text, string key)
        {
            int n = 0;
            foreach (var raw in text.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith("[")) continue;
                int i = t.IndexOf('=');
                if (i <= 0) continue;
                if (string.Equals(t.Substring(0, i).Trim(), key, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }
    }
}
