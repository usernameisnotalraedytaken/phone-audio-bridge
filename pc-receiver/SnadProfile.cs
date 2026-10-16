// SNAD 接收器的可调参数集合（Profile）。
//
// 设计目标：所有影响延迟与音质的参数都不写死在代码里，而是由 profile 提供、
// 并可从 receiver.cfg 覆盖。这样在不同使用场景之间切换只需改配置，无需重新编译。
//
// 两类参数的取向不同，不能混为一谈：
//   控制延迟：抖动缓冲水位、起播预填充、每块帧数
//   控制音质/稳定：环形容量、waveOut 缓冲数、漂移比率的摆幅
// 低延迟档牺牲抗抖动余量换时间，高品质档反过来。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Snad
{
    internal sealed class Profile
    {
        // ---- 身份 ----
        public string Name = "balanced";
        public string Description = "";

        // ---- 延迟相关 ----
        /// <summary>抖动缓冲目标水位（毫秒）。这是延迟的最大单项。</summary>
        public int TargetBufferMs = 80;

        /// <summary>起播前预填充量（毫秒）。越大起播越稳、等待越久。</summary>
        public int PrebufferMs = 60;

        /// <summary>环形缓冲容量（毫秒）。决定能吸收多大的突发。</summary>
        public int RingCapacityMs = 150;

        /// <summary>每次提交给 waveOut 的音频时长（毫秒）。</summary>
        public int BlockMs = 16;

        /// <summary>轮询间隔（毫秒）。</summary>
        public int PollMs = 1;

        // ---- 音质/稳定性相关 ----
        /// <summary>waveOut 缓冲个数。越多越抗调度抖动，内存占用与在飞时延越高。</summary>
        public int WaveOutBuffers = 16;

        /// <summary>播完提前量（毫秒）。给时钟偏差留的余地，越大越不容易欠载。</summary>
        public int LeadMs = 40;

        /// <summary>漂移比率的摆幅上限（例如 0.08 表示 1.0±8%）。</summary>
        public double DriftRange = 0.08;

        /// <summary>漂移控制的比例增益。越大收敛越快但越容易振荡。</summary>
        public double DriftGain = 0.05;

        /// <summary>读侧单次允许的比率上下限（安全钳位，防止异常值）。</summary>
        public double RatioMin = 0.7;
        public double RatioMax = 1.3;

        // ---- 超时 ----
        public int FormatWaitSec = 15;
        public int PrebufferWaitSec = 10;

        public int TargetFrames(int sampleRate) { return Math.Max(1, sampleRate * TargetBufferMs / 1000); }
        public int PrebufferFrames(int sampleRate) { return Math.Max(1, sampleRate * PrebufferMs / 1000); }
        public int RingCapacityFrames(int sampleRate) { return Math.Max(1024, sampleRate * RingCapacityMs / 1000); }
        public int BlockFrames(int sampleRate) { return Math.Max(64, sampleRate * BlockMs / 1000); }
        public int LeadFrames(int sampleRate) { return Math.Max(1, sampleRate * LeadMs / 1000); }

        /// <summary>按此 profile 估算的端到端延迟（毫秒），不含音频设备自身延迟。</summary>
        public int EstimatedPipelineLatencyMs()
        {
            return 21 + 8 + TargetBufferMs + BlockMs;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}（目标 {1}ms / 容量 {2}ms / 预填充 {3}ms / 块 {4}ms / waveOut {5} 个）",
                Name, TargetBufferMs, RingCapacityMs, PrebufferMs, BlockMs, WaveOutBuffers);
        }
    }

    internal static class ProfileLibrary
    {
        /// <summary>
        /// 内置预设。用户可以在 receiver.cfg 里覆盖任意字段，或在 [profile:xxx] 段里新增档位。
        /// </summary>
        public static Dictionary<string, Profile> BuiltIn()
        {
            var d = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);

            d["lowlatency"] = new Profile
            {
                Name = "lowlatency",
                Description = "低延迟优先：适合游戏等对延迟敏感、可容忍少量欠载的场景",
                // 块时长不可低于约 16 ms：waveOut 每次提交有固定开销（实测约 2-3 ms），
                // 8 ms 的块实际每次耗时 10.7 ms，读速率掉到 35966 帧/s（应为 48000），
                // 缓冲持续溢出并大量丢帧——比 16 ms 的块更差。
                // 因此低延迟靠压"缓冲水位"实现，而不是压块大小。
                // 水位标定：30 ms 时欠载约每秒 1.2 次（余量不足以吸收 Windows 调度抖动），
                // 80 ms 则从不欠载。60 ms 是实测可用的最低可靠水位。
                TargetBufferMs = 60,
                PrebufferMs = 40,
                RingCapacityMs = 130,
                BlockMs = 16,
                PollMs = 1,
                // 缓冲池不可过小：6 个时实测 waveOut 频繁无缓冲可用（欠载 7→39 持续增长，
                // 读速率掉到 43197 帧/s）。16 个经实测可靠，故低延迟档也保持 16——
                // 低延迟应通过压"抖动缓冲水位"实现，而不是压缓冲池。
                WaveOutBuffers = 16,
                LeadMs = 25,
                DriftRange = 0.03,
                DriftGain = 0.08,
                FormatWaitSec = 10,
                PrebufferWaitSec = 6,
            };

            d["balanced"] = new Profile
            {
                Name = "balanced",
                Description = "延迟与音质兼顾：默认档，实测稳态零丢帧",
                TargetBufferMs = 80,
                PrebufferMs = 60,
                RingCapacityMs = 150,
                BlockMs = 16,
                PollMs = 1,
                WaveOutBuffers = 16,
                LeadMs = 40,
                DriftRange = 0.08,
                DriftGain = 0.05,
                FormatWaitSec = 15,
                PrebufferWaitSec = 10,
            };

            d["highquality"] = new Profile
            {
                Name = "highquality",
                Description = "音质与稳定优先：不丢任何音频，时钟修正从容，延迟偏高",
                TargetBufferMs = 250,
                PrebufferMs = 150,
                RingCapacityMs = 500,
                BlockMs = 21,
                PollMs = 1,
                WaveOutBuffers = 32,
                LeadMs = 80,
                DriftRange = 0.08,
                DriftGain = 0.03,
                FormatWaitSec = 20,
                PrebufferWaitSec = 15,
            };

            return d;
        }

        /// <summary>
        /// 从配置文件加载。格式（键名不区分大小写）：
        ///
        ///   profile = balanced          # 选中哪个档位
        ///   targetbuffer = 60           # 覆盖当前档位的字段
        ///
        ///   [profile:gaming]            # 定义一个自定义档位
        ///   targetbuffer = 25
        ///   block = 6
        ///
        /// 未出现的字段沿用内置预设值，因此配置文件可以只写要改的项。
        /// </summary>
        public static Dictionary<string, Profile> Load(string path, out string selected)
        {
            var lib = BuiltIn();
            selected = "balanced";

            if (!File.Exists(path)) return lib;

            Profile cur = null;      // 当前正在编辑的 profile；null 表示全局
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex)
            {
                Logger.Warn("读取配置失败，改用内置预设：" + ex.Message);
                return lib;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("//")) continue;

                // 段头 [profile:名称]
                if (line.StartsWith("["))
                {
                    int end = line.IndexOf(']');
                    if (end < 0) { Logger.Warn("配置第 " + (i + 1) + " 行段头缺少 ]：" + line); continue; }
                    string sec = line.Substring(1, end - 1).Trim();
                    if (sec.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
                    {
                        string nm = sec.Substring("profile:".Length).Trim();
                        if (nm.Length == 0) { Logger.Warn("配置第 " + (i + 1) + " 行段名为空"); continue; }
                        if (!lib.TryGetValue(nm, out cur))
                        {
                            cur = new Profile { Name = nm, Description = "自定义档位" };
                            lib[nm] = cur;
                            Logger.Info("配置新增档位：" + nm);
                        }
                    }
                    else
                    {
                        Logger.Warn("配置第 " + (i + 1) + " 行未知段名：" + sec);
                        cur = null;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    // 允许无等号的孤立档位名，等价于 profile = xxx
                    if (cur == null && lib.ContainsKey(line)) { selected = line; continue; }
                    Logger.Warn("配置第 " + (i + 1) + " 行缺少 = ：" + line);
                    continue;
                }

                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();

                // 全局键
                if (cur == null && k.Equals("profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (lib.ContainsKey(v)) selected = v;
                    else Logger.Warn("配置指定的档位不存在：" + v);
                    continue;
                }

                Profile tgt = cur;
                if (tgt == null)
                {
                    if (!lib.TryGetValue(selected, out tgt)) tgt = lib["balanced"];
                }

                Apply(tgt, k, v, i + 1);
            }

            Logger.Info("配置载入：" + path + "（" + lib.Count + " 个档位，选中 " + selected + "）");
            return lib;
        }

        private static void Apply(Profile p, string key, string val, int lineNo)
        {
            int iv; double dv;
            switch (key.ToLowerInvariant())
            {
                case "name": p.Name = val; break;
                case "description": p.Description = val; break;

                case "targetbuffer":
                    if (int.TryParse(val, out iv)) p.TargetBufferMs = iv;
                    else Logger.Warn("第 " + lineNo + " 行 targetbuffer 不是整数：" + val);
                    break;
                case "prebuffer":
                    if (int.TryParse(val, out iv)) p.PrebufferMs = iv;
                    else Logger.Warn("第 " + lineNo + " 行 prebuffer 不是整数：" + val);
                    break;
                case "ringcapacity":
                    if (int.TryParse(val, out iv)) p.RingCapacityMs = iv;
                    else Logger.Warn("第 " + lineNo + " 行 ringcapacity 不是整数：" + val);
                    break;
                case "block":
                    if (int.TryParse(val, out iv)) p.BlockMs = iv;
                    else Logger.Warn("第 " + lineNo + " 行 block 不是整数：" + val);
                    break;
                case "poll":
                    if (int.TryParse(val, out iv)) p.PollMs = Math.Max(1, iv);
                    else Logger.Warn("第 " + lineNo + " 行 poll 不是整数：" + val);
                    break;
                case "waveoutbuffers":
                    if (int.TryParse(val, out iv)) p.WaveOutBuffers = Math.Max(2, iv);
                    else Logger.Warn("第 " + lineNo + " 行 waveoutbuffers 不是整数：" + val);
                    break;
                case "lead":
                    if (int.TryParse(val, out iv)) p.LeadMs = iv;
                    else Logger.Warn("第 " + lineNo + " 行 lead 不是整数：" + val);
                    break;

                case "driftrange":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) p.DriftRange = dv;
                    else Logger.Warn("第 " + lineNo + " 行 driftrange 不是小数：" + val);
                    break;
                case "driftgain":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) p.DriftGain = dv;
                    else Logger.Warn("第 " + lineNo + " 行 driftgain 不是小数：" + val);
                    break;
                case "ratiomin":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) p.RatioMin = dv;
                    else Logger.Warn("第 " + lineNo + " 行 ratiomin 不是小数：" + val);
                    break;
                case "ratiomax":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) p.RatioMax = dv;
                    else Logger.Warn("第 " + lineNo + " 行 ratiomax 不是小数：" + val);
                    break;

                case "formatwait":
                    if (int.TryParse(val, out iv)) p.FormatWaitSec = iv;
                    else Logger.Warn("第 " + lineNo + " 行 formatwait 不是整数：" + val);
                    break;
                case "prebufferwait":
                    if (int.TryParse(val, out iv)) p.PrebufferWaitSec = iv;
                    else Logger.Warn("第 " + lineNo + " 行 prebufferwait 不是整数：" + val);
                    break;

                default:
                    Logger.Warn("第 " + lineNo + " 行未知配置项：" + key);
                    break;
            }
        }

        /// <summary>写出带注释的默认配置，首次运行时生成，便于用户直接改。</summary>
        public static void WriteDefault(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SNAD 接收器配置");
            sb.AppendLine("# 首次运行自动生成。所有字段都可省略，省略即沿用内置预设。");
            sb.AppendLine("# 修改后重启接收器生效。");
            sb.AppendLine();
            sb.AppendLine("# 当前选用的档位：lowlatency / balanced / highquality，或下面自定义的档位名");
            sb.AppendLine("profile = balanced");
            sb.AppendLine();
            sb.AppendLine("# ---- 内置档位的字段含义 ----");
            sb.AppendLine("# targetbuffer   抖动缓冲目标水位(ms)   延迟的最大单项");
            sb.AppendLine("# prebuffer      起播预填充(ms)         越大起播越稳、等待越久");
            sb.AppendLine("# ringcapacity   环形缓冲容量(ms)       决定能吸收多大突发");
            sb.AppendLine("# block          每次提交给 waveOut 的时长(ms)");
            sb.AppendLine("# poll           轮询间隔(ms)");
            sb.AppendLine("# waveoutbuffers waveOut 缓冲个数        越多越抗调度抖动");
            sb.AppendLine("# lead           播完提前量(ms)         给时钟偏差留的余地");
            sb.AppendLine("# driftrange     漂移比率摆幅(如 0.08 = ±8%)");
            sb.AppendLine("# driftgain      漂移控制增益           越大收敛快、易振荡");
            sb.AppendLine("# ratiomin/max   读侧比率安全钳位");
            sb.AppendLine("# formatwait     等待首个包的秒数");
            sb.AppendLine("# prebufferwait  等待预填充的秒数");
            sb.AppendLine();
            sb.AppendLine("# ---- 自定义档位示例（去掉 # 即可启用）----");
            sb.AppendLine("# [profile:gaming]");
            sb.AppendLine("# description = 自用游戏档：尽量压延迟");
            sb.AppendLine("# targetbuffer = 25");
            sb.AppendLine("# prebuffer = 15");
            sb.AppendLine("# ringcapacity = 60");
            sb.AppendLine("# block = 6");
            sb.AppendLine("# waveoutbuffers = 6");
            sb.AppendLine("# lead = 20");
            sb.AppendLine("# driftrange = 0.02");
            sb.AppendLine("# driftgain = 0.10");
            sb.AppendLine();
            sb.AppendLine("# ---- 自定义档位示例：听音乐，容忍延迟换稳定 ----");
            sb.AppendLine("# [profile:music]");
            sb.AppendLine("# description = 自用音乐档：不丢音频");
            sb.AppendLine("# targetbuffer = 300");
            sb.AppendLine("# prebuffer = 200");
            sb.AppendLine("# ringcapacity = 600");
            sb.AppendLine("# block = 21");
            sb.AppendLine("# waveoutbuffers = 32");
            sb.AppendLine("# lead = 100");
            sb.AppendLine("# driftgain = 0.02");

            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
            catch (Exception ex) { Logger.Warn("写默认配置失败：" + ex.Message); }
        }
    }
}
