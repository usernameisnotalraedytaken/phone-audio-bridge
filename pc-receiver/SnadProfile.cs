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

        // ---- 均衡器 ----
        // 放在档位里而不是单独的配置文件：不同用途需要不同调音
        // （游戏要突出脚步、音乐要平直），跟随档位切换更自然。
        /// <summary>是否默认启用均衡器。默认关闭——可选效果不应擅自改变声音。</summary>
        public bool EqEnabled = false;

        /// <summary>各频段增益（dB）。空表示全平。频段顺序见 Equalizer 的构造。</summary>
        public double[] EqGains = null;

        /// <summary>前置增益（dB），用于补偿整体提升导致的削波。</summary>
        public double EqPreampDb = 0;

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

                case "eq":
                    // 接受 on/off/true/false/1/0
                    p.EqEnabled = IsTruthy(val);
                    break;
                case "eqpreamp":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out dv))
                        p.EqPreampDb = dv;
                    else
                        Logger.Warn("第 " + lineNo + " 行 eqpreamp 不是数字：" + val);
                    break;
                case "eqgains":
                    // 逗号分隔的 dB 值，按频段顺序
                    {
                        var parts = val.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        var gains = new double[parts.Length];
                        bool ok = true;
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out gains[i]))
                            { ok = false; break; }
                        }
                        if (ok && gains.Length > 0) p.EqGains = gains;
                        else Logger.Warn("第 " + lineNo + " 行 eqgains 解析失败：" + val);
                    }
                    break;

                default:
                    Logger.Warn("第 " + lineNo + " 行未知配置项：" + key);
                    break;
            }
        }

        /// <summary>把 on/off/true/false/1/0 之类的写法统一成布尔值。</summary>
        private static bool IsTruthy(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.Trim().ToLowerInvariant();
            return v == "on" || v == "true" || v == "yes" || v == "1" || v == "开";
        }

        /// <summary>
        /// 把均衡器设置写回配置文件。
        ///
        /// 为什么只改这三个键而不是整份重写：receiver.cfg 带注释、用户可能手改过其他字段，
        /// 整份重写会把这些都冲掉。这里只在对应位置就地替换或追加。
        ///
        /// 写入位置：当前选中档位所在的段落。若无 [profile:name] 段则写在文件顶层。
        /// </summary>
        public static void SaveEq(string path, string profileName, Equalizer eq)
        {
            if (string.IsNullOrEmpty(path) || eq == null) return;

            string[] lines;
            try
            {
                lines = File.Exists(path) ? File.ReadAllLines(path) : new string[0];
            }
            catch (Exception ex)
            {
                Logger.Err("读取配置失败，均衡器设置未保存：" + path, ex);
                return;
            }

            var gains = eq.GetGains();
            var sbGains = new StringBuilder();
            for (int i = 0; i < gains.Length; i++)
            {
                if (i > 0) sbGains.Append(',');
                sbGains.Append(gains[i].ToString("0.#", CultureInfo.InvariantCulture));
            }

            string targetSection = "[profile:" + profileName + "]";
            var outLines = new List<string>(lines);
            int sectionStart = -1, sectionEnd = outLines.Count;

            for (int i = 0; i < outLines.Count; i++)
            {
                string t = outLines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    if (sectionStart >= 0) { sectionEnd = i; break; }
                    if (string.Equals(t, targetSection, StringComparison.OrdinalIgnoreCase)) sectionStart = i;
                }
            }

            bool sawEq = false, sawPreamp = false, sawGains = false;
            int lo = sectionStart >= 0 ? sectionStart + 1 : 0;
            int hi = sectionStart >= 0 ? sectionEnd : outLines.Count;

            for (int i = lo; i < hi; i++)
            {
                string line = outLines[i];
                string t = line.Trim();
                if (t.StartsWith("#") || t.Length == 0) continue;
                int eqIdx = t.IndexOf('=');
                if (eqIdx <= 0) continue;
                string key = t.Substring(0, eqIdx).Trim().ToLowerInvariant();

                if (key == "eq")
                {
                    outLines[i] = "eq = " + (eq.Enabled ? "on" : "off");
                    sawEq = true;
                }
                else if (key == "eqpreamp")
                {
                    outLines[i] = "eqpreamp = " + eq.PreampDb.ToString("0.#", CultureInfo.InvariantCulture);
                    sawPreamp = true;
                }
                else if (key == "eqgains")
                {
                    outLines[i] = "eqgains = " + sbGains;
                    sawGains = true;
                }
            }

            if (sectionStart < 0)
            {
                // 该档位还没有自己的段落。
                //
                // 注意不能把这几个键插到已有段落内部——那样会改到别的档位的设置
                // （实测踩过：为"自定义档"保存时，键被插进了上一个 [profile:] 段里，
                //  结果既没建出新段，又污染了别的档位）。
                // 因此这里新建一个段落，且放在文件末尾，避免插进任何现有段。
                var block = new List<string>();
                if (outLines.Count > 0 && outLines[outLines.Count - 1].Trim().Length != 0)
                    block.Add("");
                block.Add("# 此段落由均衡器窗口自动创建");
                block.Add(targetSection);
                block.Add("eq = " + (eq.Enabled ? "on" : "off"));
                block.Add("eqpreamp = " + eq.PreampDb.ToString("0.#", CultureInfo.InvariantCulture));
                block.Add("eqgains = " + sbGains);
                outLines.AddRange(block);

                try
                {
                    File.WriteAllLines(path, outLines.ToArray());
                    Logger.Info("均衡器设置已保存到 " + path + "（新建档位 " + profileName + "，已写入 3 个键）");
                }
                catch (Exception ex)
                {
                    Logger.Err("写入配置失败，均衡器设置未保存：" + path, ex);
                }
                return;
            }

            // 已有段落：缺失的键追加到该段末尾，保持同一段内聚
            var add = new List<string>();
            if (!sawEq) add.Add("eq = " + (eq.Enabled ? "on" : "off"));
            if (!sawPreamp) add.Add("eqpreamp = " + eq.PreampDb.ToString("0.#", CultureInfo.InvariantCulture));
            if (!sawGains) add.Add("eqgains = " + sbGains);
            if (add.Count > 0) outLines.InsertRange(sectionEnd, add);

            try
            {
                File.WriteAllLines(path, outLines.ToArray());
                Logger.Info("均衡器设置已保存到 " + path + "（档位 " + profileName + "）");
            }
            catch (Exception ex)
            {
                Logger.Err("写入配置失败，均衡器设置未保存：" + path, ex);
            }
        }

        /// <summary>把档位里保存的均衡器设置套用到均衡器实例上。</summary>
        public static void ApplyEq(Profile p, Equalizer eq)
        {
            if (p == null || eq == null) return;
            if (p.EqGains != null && p.EqGains.Length > 0) eq.SetAllGains(p.EqGains);
            eq.PreampDb = p.EqPreampDb;
            eq.Enabled = p.EqEnabled;
        }

        /// <summary>把均衡器的当前设置存进档位对象（不落盘）。</summary>
        public static void CaptureEq(Profile p, Equalizer eq)
        {
            if (p == null || eq == null) return;
            p.EqGains = eq.GetGains();
            p.EqPreampDb = eq.PreampDb;
            p.EqEnabled = eq.Enabled;
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
            sb.AppendLine("# ---- 均衡器 ----");
            sb.AppendLine("# eq        是否启用（on/off）。默认 off，可选效果不擅自改变声音");
            sb.AppendLine("# eqpreamp  前置增益(dB)，用于补偿整体提升导致的削波");
            sb.AppendLine("# eqgains   各频段增益(dB)，逗号分隔，顺序为：");
            sb.AppendLine("#           105, 240, 470, 900, 1.8k, 3.5k, 6k, 8k, 12k, 16k");
            sb.AppendLine("#           范围 -12 ~ +12。105 与 12k 是搁架式，其余为峰值式");
            sb.AppendLine("# 例：低音增强 eqgains = 7,5,3,1,0,0,0,0,0,0");
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
