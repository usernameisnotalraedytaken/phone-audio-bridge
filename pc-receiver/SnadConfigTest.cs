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

            // ---- 7. 后端与音质参数的完整往返 ----
            //
            // v3 新增 backend / device / format / wasapiperiod 四个维度。
            // 它们此前只存在于内存（Profile 有字段但没有落盘路径），
            // 于是"改了也存不下来"。这里验证整档保存与读回。
            Console.WriteLine();
            Console.WriteLine("--- 后端与音质参数往返 ---");

            const string deviceId = "{0.0.0.00000000}.{7b5a235f-3f3d-4f5e-a070-7f439722d43b}";
            var wasapi = new Profile
            {
                Name = "往返测试",
                Backend = "wasapi_exclusive",
                DeviceId = deviceId,
                Description = "含中文的说明",
                Tags = "lowlatency,wasapi",
                Dither = true,
                Format = "pcm24",
                SampleRate = "48000",
                WasapiPeriodMs = 3,
                TargetBufferMs = 45,
                BlockMs = 8,
                WaveOutBuffers = 4,
                LeadMs = 20,
                DriftGain = 0.07,
                DriftRange = 0.05,
                // ③ 缓冲策略
                BufferMode = "adaptive",
                MinBufferMs = 35,
                MaxBufferMs = 180,
                AdaptRate = 0.35,
                CalibrateSec = 12,
                // ②④ 标定与上报
                OutputLegMs = 31,
                CalibratedOn = "Realtek 测试设备",
                ReportedCaptureMs = 17,
                EqEnabled = true,
                EqPreampDb = -3.5
            };
            wasapi.EqGains = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            Check(ProfileLibrary.SaveProfile(cfg, wasapi), "SaveProfile 返回成功");

            reloaded = ProfileLibrary.Load(cfg, out selected);
            Profile rt;
            Check(reloaded.TryGetValue("往返测试", out rt), "保存的档位可被载入");
            if (rt != null)
            {
                Check(rt.Backend == "wasapi_exclusive", "backend 读回正确（" + rt.Backend + "）");
                Check(rt.DeviceId == deviceId, "device 读回正确（" + rt.DeviceId + "）");
                Check(rt.Format == "pcm24", "format 读回正确（" + rt.Format + "）");
                Check(rt.SampleRate == "48000", "samplerate 读回正确（" + rt.SampleRate + "）");
                Check(rt.Tags == "lowlatency,wasapi", "tags 读回正确（" + rt.Tags + "）");
                Check(rt.Dither, "dither 读回为 true");
                Check(rt.DeclaredSampleRate() == 48000, "DeclaredSampleRate 解析为 48000");
                Check(new Profile { SampleRate = "follow" }.DeclaredSampleRate() == 0,
                      "follow 解析为 0（跟随源）");
                Check(rt.WasapiPeriodMs == 3, "wasapiperiod 读回正确（" + rt.WasapiPeriodMs + "）");
                Check(rt.TargetBufferMs == 45, "targetbuffer 读回正确（" + rt.TargetBufferMs + "）");
                Check(rt.BlockMs == 8, "block 读回正确（" + rt.BlockMs + "）");
                Check(rt.WaveOutBuffers == 4, "waveoutbuffers 读回正确（" + rt.WaveOutBuffers + "）");
                Check(rt.LeadMs == 20, "lead 读回正确（" + rt.LeadMs + "）");
                Check(Math.Abs(rt.DriftGain - 0.07) < 0.001, "driftgain 读回正确（" + rt.DriftGain + "）");
                Check(Math.Abs(rt.DriftRange - 0.05) < 0.001, "driftrange 读回正确（" + rt.DriftRange + "）");
                Check(rt.EqEnabled, "eq 状态读回正确");
                Check(Math.Abs(rt.EqPreampDb - (-3.5)) < 0.01, "eqpreamp 读回正确（" + rt.EqPreampDb + "）");
                Check(rt.EqGains != null && rt.EqGains.Length == 10 && Math.Abs(rt.EqGains[9] - 10) < 0.01,
                      "eqgains 读回正确");

                // ③ 缓冲策略必须能往返：漏掉任何一项都会在保存时被清零，
                // 而界面上看不出异常，属于静默丢配置
                Check(rt.BufferMode == "adaptive", "buffermode 读回正确（" + rt.BufferMode + "）");
                Check(rt.MinBufferMs == 35, "minbuffer 读回正确（" + rt.MinBufferMs + "）");
                Check(rt.MaxBufferMs == 180, "maxbuffer 读回正确（" + rt.MaxBufferMs + "）");
                Check(Math.Abs(rt.AdaptRate - 0.35) < 0.001, "adaptrate 读回正确（" + rt.AdaptRate + "）");
                Check(rt.CalibrateSec == 12, "calibratesec 读回正确（" + rt.CalibrateSec + "）");

                // ②④ 标定与上报
                Check(rt.OutputLegMs == 31, "outputleg 读回正确（" + rt.OutputLegMs + "）");
                Check(rt.CalibratedOn == "Realtek 测试设备", "calibratedon 读回正确（" + rt.CalibratedOn + "）");
                Check(rt.ReportedCaptureMs == 17, "capturebuffer 读回正确（" + rt.ReportedCaptureMs + "）");
                Check(rt.OutputLegIsMeasured, "OutputLegMs > 0 时判定为已标定");
            }

            // 二次保存应就地更新，不产生重复键
            wasapi.TargetBufferMs = 55;
            ProfileLibrary.SaveProfile(cfg, wasapi);
            string afterSecond = File.ReadAllText(cfg);
            Check(CountKeyIn(afterSecond, "[profile:往返测试]", "targetbuffer") == 1,
                  "二次保存后 targetbuffer 在该段内只出现一次");
            Check(CountKeyIn(afterSecond, "[profile:往返测试]", "backend") == 1,
                  "二次保存后 backend 在该段内只出现一次");

            reloaded = ProfileLibrary.Load(cfg, out selected);
            if (reloaded.TryGetValue("往返测试", out rt))
                Check(rt.TargetBufferMs == 55, "二次保存后就地更新为 55（实为 " + rt.TargetBufferMs + "）");

            // 非法取值必须被拒绝并保留原值，而不是静默接受
            Console.WriteLine();
            Console.WriteLine("--- 非法取值应被拒绝 ---");
            ProfileLibrary.SaveFields(cfg, "非法测试", new[]
            {
                new KeyValuePair<string, string>("backend", "占位后端"),
                new KeyValuePair<string, string>("format", "pcm8")
            }, "测试用");
            reloaded = ProfileLibrary.Load(cfg, out selected);
            Profile bad;
            if (reloaded.TryGetValue("非法测试", out bad))
            {
                Check(bad.Backend == "waveout", "非法 backend 被拒绝，保留默认 waveout（实为 " + bad.Backend + "）");
                Check(bad.Format == "pcm16", "非法 format 被拒绝，保留默认 pcm16（实为 " + bad.Format + "）");
            }
            else Check(false, "非法测试档位应仍能被载入（只是取值回退到默认）");

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

        /// <summary>
        /// 统计**指定段落内**某键出现的次数。
        /// 用于验证二次保存是就地更新而不是又追加一个键——
        /// 后者会让配置越存越长，且读回时以哪个为准变得不确定。
        /// </summary>
        private static int CountKeyIn(string text, string section, string key)
        {
            int n = 0;
            bool inside = false;
            foreach (var raw in text.Split('\n'))
            {
                string t = raw.Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    inside = string.Equals(t, section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inside || t.Length == 0 || t.StartsWith("#")) continue;
                int i = t.IndexOf('=');
                if (i <= 0) continue;
                if (string.Equals(t.Substring(0, i).Trim(), key, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }
    }
}
