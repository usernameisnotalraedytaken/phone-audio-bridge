// SNAD 接收器 —— 接收手机音频并通过 Windows waveOut 播放
//
// 为什么用 waveOut 而不是 WASAPI/NAudio：
//   本机没有 .NET SDK、没有 Windows SDK、没有 NuGet 缓存，磁盘仅剩 ~2GB。
//   waveOut 是 winmm.dll 里的系统 API，零依赖、零安装，能满足"低延迟播放原始 PCM"的需求。
//   代价是不支持事件驱动独占模式，但对本用途（送进 Voicemeeter 混音）完全够用。
//
// 编译（本机已验证可行的方式，不需要 SDK）：
//   csc.exe /target:exe /out:SnadReceiver.exe ^
//     /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll ^
//     /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll ^
//     /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll SnadReceiver.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Snad
{
    /// <summary>
    /// 统一日志出口。同时写控制台与持久文件（receiver.log），并允许被接收循环复用。
    /// 原则：不允许静默失败——任何异常都必须带上类型与消息落盘。
    /// </summary>
    /// <summary>
    /// 日志档位。
    ///   None    —— 完全不写文件（控制台仍有输出）。周期统计、逐包明细等一概不落盘。
    ///   Verbose —— 全部落盘，并在周期统计之外追加逐包明细与内部状态。
    /// </summary>
    internal enum LogVerbosity { None = 0, Verbose = 1 }

    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static int _fileErrors;
        private static LogVerbosity _verbosity = LogVerbosity.None;

        public static LogVerbosity Verbosity { get { return _verbosity; } }
        public static bool FileEnabled { get { return _path != null && _verbosity != LogVerbosity.None; } }
        public static bool Verbose { get { return _verbosity == LogVerbosity.Verbose; } }

        public static void Open(string path, LogVerbosity verbosity)
        {
            _path = path;
            _verbosity = verbosity;
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        /// <summary>
        /// 逐包/高频明细：仅在 Verbose 档下才落盘，避免长时间运行时日志膨胀。
        /// 控制台仍会打印，便于前台观察。
        /// </summary>
        public static void Trace(string msg)
        {
            if (_verbosity != LogVerbosity.Verbose)
            {
                Console.WriteLine(Stamp("TRACE") + " " + msg);
                return;
            }
            Write("TRACE", msg);
        }

        public static void Err(string what, Exception ex)
        {
            Write("ERROR", what + " -> " + ex.GetType().Name + ": " + ex.Message);
            if (ex.StackTrace != null)
            {
                foreach (var l in ex.StackTrace.Split('\n'))
                    Write("      ", l.Trim());
            }
            var inner = ex.InnerException;
            while (inner != null)
            {
                Write("      ", "caused by " + inner.GetType().Name + ": " + inner.Message);
                inner = inner.InnerException;
            }
        }

        private static string Stamp(string level)
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level;
        }

        private static void Write(string level, string msg)
        {
            var line = Stamp(level) + " " + msg;
            Console.WriteLine(line);

            if (_path == null) return;
            if (_verbosity == LogVerbosity.None)
            {
                // none 档：只把 ERROR 落盘。出问题时仍留有线索，
                // 但正常运行完全不产生磁盘写入。
                if (level != "ERROR" && level != "WARN ") return;
            }

            try
            {
                lock (Gate) { System.IO.File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            }
            catch (Exception ex)
            {
                // 写文件失败最多提示 3 次，避免异常处理本身刷屏
                if (_fileErrors++ < 3)
                    Console.WriteLine("WARN  写日志文件失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    internal static class Program
    {
        internal const int DefaultPort = 6981;   // 避开 Voicemeeter 的 VBAN 服务（占用 6980）
        internal const int HeaderSize = 22;

        /// <summary>日志端口 = 音频端口 + 1。</summary>
        internal const int LogPortOffset = 1;

        /// <summary>
        /// 包队列容量。仅作为接收线程与播放线程之间的极小过渡容器。
        /// 实测 Voicemeeter Input 以 99.5% 实时消费，手机侧产出约 100.3% 实时，
        /// 两者接近匹配；真正需要防的是启动瞬间的突发。
        /// 原先把这里当作抖动缓冲并把容量设为 60 包（1.28 秒），既无依据，
        /// 又让启动时 64 个包（65536 帧）瞬间灌入仅 24000 帧的环形缓冲，
        /// 直接撞满并从此再也排不空。
        /// </summary>
        internal const int QueueCapacity = 8;

        /// <summary>
        /// 信标里使用的本机标识。用途仅是让手机区分"是哪台电脑"，
        /// 不承担授权职责（授权完全由 HMAC 保证），因此可以稳定公开。
        /// 用机器名 + 密钥指纹前 4 位：换机器或换密钥时标识随之变化，便于识别。
        /// </summary>
        internal static readonly string MachineDeviceId = BuildMachineDeviceId();

        private static string BuildMachineDeviceId()
        {
            string name;
            try { name = Environment.MachineName; }
            catch (Exception) { name = "pc"; }
            if (string.IsNullOrEmpty(name)) name = "pc";
            // 去掉可能干扰显示的字符，保留可读性
            var sb = new StringBuilder();
            foreach (char c in name)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            string clean = sb.Length > 0 ? sb.ToString() : "pc";

            string kid = SnadSecret.KeyId;
            string suffix = string.IsNullOrEmpty(kid) ? "nokey" : kid.Substring(0, Math.Min(4, kid.Length));
            return "snad-" + clean + "-" + suffix;
        }

        // ---- 系统计时器精度 ----
        // 本机实测：Stopwatch 计时的 Thread.Sleep(100) 实际睡了 223 ms，
        // DateTime.UtcNow 的粒度是 13.4 ms —— 系统计时器处于默认的 15.6 ms 档。
        // 播放循环每个音频块只有 16 ms，与睡眠粒度同量级，线程必然晚醒，
        // 消费速率因此稳定低于实时，缓冲被顶满并持续丢帧。
        // 所有实时音频程序都必须先把计时器精度提到 1 ms，这是前提而非优化。
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private const uint TimerResolutionMs = 1;

#if CONSOLE_BUILD
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception ex) { Console.WriteLine("WARN  设置控制台编码失败: " + ex.Message); }
            // 管道重定向时 Console.Out 是块缓冲，不显式 flush 会看不到实时统计
            var auto = new System.IO.StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(auto);

            // 时钟基准自检：不联网、不需要手机，先证明"已知答案能算回来"。
            // 端到端延迟的可信度完全建立在这一层上，所以它必须能单独验证。
            if (args.Length > 0 && args[0] == "--timesync-selftest")
            {
                Console.WriteLine("=== 时钟基准自检 ===");
                return SnadTimeSync.SelfTest();
            }

            // 提高系统计时器精度到 1 ms。不做这一步，播放线程会被 15.6 ms 的
            // 调度粒度拖慢，而音频块只有 16 ms，消费必然跟不上产出。
            uint tRes = TimeBeginPeriod(TimerResolutionMs);
            if (tRes != 0)
            {
                Console.WriteLine("WARN  timeBeginPeriod 失败 rc=" + tRes + "，播放时序可能不准");
            }

            int deviceIndex = -1;      // -1 = 跟随系统默认播放设备
            int port = DefaultPort;
            int logPort = DefaultPort + 1;
            bool listOnly = false;
            // 默认 verbose：启动阶段的信息（档位、设备选择、信标广播、缓冲换算）
            // 是排查问题的第一手材料，默认就写下来。
            // 之前默认 none 只在 WARN/ERROR 落盘，结果调试时连"信标广播已启动"
            // 都看不到，只能去翻 stdout —— 默认值应该偏向可排查，而不是偏向省空间。
            // 日常嫌吵可显式加 --log none。
            LogVerbosity verbosity = LogVerbosity.Verbose;
            string profileOverride = null;   // --profile 覆盖配置文件中的选择

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--list": case "-l": listOnly = true; break;
                    case "--device": case "-d":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out deviceIndex);
                        break;
                    case "--port": case "-p":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out port);
                        break;
                    case "--log-port":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out logPort);
                        break;
                    case "--log":
                        if (i + 1 < args.Length)
                        {
                            string v = args[++i].Trim().ToLowerInvariant();
                            if (v == "verbose" || v == "on" || v == "1") verbosity = LogVerbosity.Verbose;
                            else if (v == "none" || v == "off" || v == "0") verbosity = LogVerbosity.None;
                            else { Console.WriteLine("WARN  --log 只接受 none 或 verbose，收到: " + v); }
                        }
                        break;
                    case "--profile": case "-P":
                        if (i + 1 < args.Length) profileOverride = args[++i].Trim();
                        else Console.WriteLine("WARN  --profile 需要跟一个档位名");
                        break;
                    case "--help": case "-h": PrintUsage(); return 0;
                }
            }

            var exeDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(exeDir)) exeDir = Environment.CurrentDirectory;
            var logPath = System.IO.Path.Combine(exeDir, "receiver.log");
            Logger.Open(logPath, verbosity);
            Logger.Info("======== SNAD 接收器启动 ========");
            Logger.Info("音频端口=" + port + "  日志端口=" + logPort + "  日志档位=" + verbosity +
                        "  日志文件=" + logPath);

            // ---- 载入参数档位 ----
            // 配置文件不存在时自动写出一份带注释的默认配置，便于用户直接修改。
            string cfgPath = System.IO.Path.Combine(exeDir, "receiver.cfg");
            if (!System.IO.File.Exists(cfgPath)) ProfileLibrary.WriteDefault(cfgPath);
            string selectedProfile;
            var profiles = ProfileLibrary.Load(cfgPath, out selectedProfile);
            if (!string.IsNullOrEmpty(profileOverride))
            {
                if (profiles.ContainsKey(profileOverride)) selectedProfile = profileOverride;
                else Logger.Warn("--profile 指定的档位不存在：" + profileOverride + "，改用 " + selectedProfile);
            }
            Profile active;
            if (!profiles.TryGetValue(selectedProfile, out active)) active = profiles["balanced"];
            Logger.Info("已载入 " + profiles.Count + " 个档位：" +
                        string.Join(" / ", new List<string>(profiles.Keys).ToArray()));
            foreach (var kv in profiles)
                Logger.Info("  " + kv.Value);

            var devices = WaveOut.ListDevices();
            Logger.Info("可用输出设备共 " + devices.Count + " 个：");
            foreach (var d in devices)
                Logger.Info(string.Format("  [{0}] {1}  ({2} ch)", d.Index, d.Name, d.Channels));

            if (listOnly) return 0;

            if (deviceIndex < 0)
            {
                // 默认策略：跟随 Windows 的系统默认播放设备。
                // 这样用户在系统里切换扬声器/耳机/USB 声卡时，手机音频跟着走。
                //
                // 唯一需要回退的情形：默认设备被 Voicemeeter 独占（rc=4 MMSYSERR_ALLOCATED）。
                // 那时把音频送进 Voicemeeter Input，由 Voicemeeter 负责送到它自己占用的
                // A1 设备——这正是让手机音频与电脑音频混音的路径。
                string defName = WaveOut.DefaultDeviceName();
                Logger.Info("系统默认播放设备识别为：" +
                            (string.IsNullOrEmpty(defName) ? "(未取到)" : "\"" + defName + "\""));
                int defIndex = -1;

                if (!string.IsNullOrEmpty(defName))
                {
                    var m = devices.FirstOrDefault(d =>
                        string.Equals(d.Name, defName, StringComparison.OrdinalIgnoreCase));
                    if (m.Name == null)
                    {
                        // 名字可能有细微差异（waveOut 名会被截断到 31 字符），退回前缀匹配
                        string prefix = defName.Substring(0, Math.Min(12, defName.Length));
                        m = devices.FirstOrDefault(d =>
                            d.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        Logger.Info("默认设备名未精确匹配，尝试前缀 \"" + prefix + "\"：" +
                                    (m.Name == null ? "仍失败" : "命中 [" + m.Index + "]"));
                    }
                    if (m.Name != null) defIndex = m.Index;
                }

                if (defIndex >= 0)
                {
                    // 试探能否打开。打不开说明被 Voicemeeter 等独占。
                    IntPtr probe;
                    int prc = WaveOut.ProbeOpen(defIndex, out probe);
                    Logger.Info("试探打开默认设备 [" + defIndex + "] 返回 rc=" + prc +
                                (prc == 4 ? "（MMSYSERR_ALLOCATED，被占用）" : ""));
                    if (prc == 0)
                    {
                        deviceIndex = defIndex;
                        Logger.Info("输出设备：跟随系统默认 [" + defIndex + "] " + devices[defIndex].Name);
                    }
                    else
                    {
                        Logger.Warn("系统默认设备 [" + defIndex + "] " + devices[defIndex].Name +
                                    " 无法打开（rc=" + prc + "，通常是被 Voicemeeter 独占）");
                        var vm = devices.FirstOrDefault(d =>
                            d.Name.IndexOf("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (vm.Name != null)
                        {
                            deviceIndex = vm.Index;
                            Logger.Info("输出设备：回退到 [" + vm.Index + "] " + vm.Name +
                                        " —— 音频经 Voicemeeter 混音后从其 A1 设备输出");
                        }
                        else
                        {
                            deviceIndex = defIndex;
                            Logger.Warn("未找到 Voicemeeter Input，仍尝试默认设备");
                        }
                    }
                }
                else
                {
                    var vm = devices.FirstOrDefault(d =>
                        d.Name.IndexOf("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) >= 0);
                    deviceIndex = vm.Name != null ? vm.Index : 0;
                    Logger.Warn("未能把系统默认设备映射到 waveOut 设备，改用 [" + deviceIndex + "] " +
                                devices[deviceIndex].Name);
                }
            }

            Console.WriteLine();
            Console.WriteLine("监听 UDP 端口 " + port + " ...");
            Console.WriteLine("手机端填：本机 IP + 端口 " + port);
            Console.WriteLine("按 Ctrl+C 退出。");
            Console.WriteLine();

            // 启动信标广播：手机端据此自动发现本机，无需手填地址。
            // 密钥来自构建时注入的 SnadSecret；缺失时明确报错，不静默跳过广播
            // （否则表现为"手机永远找不到电脑"，很难排查）。
            BeaconBroadcaster beacon = null;
            try
            {
                byte[] beaconKey = SnadBeacon.DeriveKey(SnadSecret.Psk);
                beacon = new BeaconBroadcaster(MachineDeviceId, port, beaconKey, 2000,
                    m => Logger.Warn("信标：" + m));
                beacon.Start();
                Logger.Info("信标广播已启动：device=" + MachineDeviceId + " 端口=" + port +
                            " KeyId=" + SnadSecret.KeyId + "（手机端此值必须一致）");
            }
            catch (Exception ex)
            {
                Logger.Err("信标广播启动失败（手机端将无法自动发现本机，仍可手填地址）", ex);
            }

            using (var receiver = new SnadReceiver(deviceIndex, port, active))
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; receiver.Stop(); };
                receiver.Run();
            }

            if (beacon != null) { beacon.Dispose(); Logger.Info("信标广播已停止"); }

            // 恢复系统计时器精度，避免影响其它程序
            uint tResEnd = TimeEndPeriod(TimerResolutionMs);
            if (tResEnd != 0)
                Console.WriteLine("WARN  timeEndPeriod 失败 rc=" + tResEnd);

            Console.WriteLine("已退出。");
            return 0;
        }
#endif

        private static void PrintUsage()
        {
            Console.WriteLine("SNAD 接收器");
            Console.WriteLine("  --list,   -l            只列出音频设备后退出");
            Console.WriteLine("  --device, -d <index>    指定输出设备序号（默认跟随系统默认设备）");
            Console.WriteLine("  --port,   -p <port>     监听端口（默认 " + DefaultPort + "）");
            Console.WriteLine("  --log-port <port>       手机日志端口（默认 " + (DefaultPort + 1) + "）");
            Console.WriteLine("  --log <none|verbose>    日志档位（默认 none）");
            Console.WriteLine();
            Console.WriteLine("输出设备选择：默认跟随 Windows 的系统默认播放设备，因此在系统里");
            Console.WriteLine("切换扬声器/耳机时手机音频会跟着走。若默认设备被 Voicemeeter 独占");
            Console.WriteLine("（waveOutOpen 返回 4），则自动改用 Voicemeeter Input，由 Voicemeeter");
            Console.WriteLine("混音后从其 A1 设备输出。");
            Console.WriteLine();
            Console.WriteLine("日志档位：");
            Console.WriteLine("  none     正常运行不写文件，仅 WARN/ERROR 落盘（推荐，零持续写入）");
            Console.WriteLine("  verbose  全部落盘，含逐包明细与周期统计，用于排查问题");
        }
    }

    // ------------------------------------------------------------------ waveOut

    internal struct DeviceInfo
    {
        public int Index;
        public string Name;
        public int Channels;
    }

    internal static class WaveOut
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private const uint WAVE_FORMAT_PCM = 1;

        /// <summary>音频块回调的异常计数，只提示有限次数以免刷屏。</summary>
        private static int _blockCbErrors;
        private const uint WHDR_DONE = 0x00000001;
        private const int WAVE_MAPPER = -1;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS pwoc, int cbwoc);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat,
            IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        /// <summary>
        /// 枚举所有 waveOut 输出设备。
        /// </summary>
        public static List<DeviceInfo> ListDevices()
        {
            var list = new List<DeviceInfo>();
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEOUTCAPS();
                if (waveOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(typeof(WAVEOUTCAPS))) != 0) continue;
                list.Add(new DeviceInfo
                {
                    Index = i,
                    Name = caps.szPname ?? ("device " + i),
                    Channels = caps.wChannels
                });
            }
            return list;
        }

        /// <summary>
        /// 取当前 Windows 默认播放设备的友好名。
        ///
        /// 不能用 waveOutGetDevCaps(WAVE_MAPPER)：它返回的是遗留别名
        /// "Microsoft 声音映射器"，不是真实设备名，无法用来匹配设备列表（已实测）。
        ///
        /// 因此走 MMDevice API（Core Audio）：枚举 Render 端点，取
        /// PKEY_Device_DeviceDesc 作为名字、PKEY_Device_FriendlyName 作为回退，
        /// 并与注册表 MMDevices\Render 下 Role=1 的端点 ID 比对，确认哪个才是默认。
        /// 全部通过 COM 互操作完成，不依赖任何外部库。
        /// </summary>
        public static string DefaultDeviceName()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            IMMDevice defaultDev = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out defaultDev);

                string defaultId = null;
                defaultDev.GetId(out defaultId);
                if (string.IsNullOrEmpty(defaultId)) return null;

                // 从注册表读出该端点的友好名（最可靠，且与 waveOut 显示名一致）
                string regName = ReadEndpointNameFromRegistry(defaultId);
                if (!string.IsNullOrEmpty(regName)) return regName;

                // 回退：直接用属性存储里的名称
                return ReadDevicePropertyName(defaultDev);
            }
            catch (Exception ex)
            {
                Logger.Warn("读取系统默认播放设备失败：" + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (defaultDev != null) Marshal.ReleaseComObject(defaultDev);
                if (collection != null) Marshal.ReleaseComObject(collection);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static string ReadDevicePropertyName(IMMDevice dev)
        {
            IPropertyStore store = null;
            try
            {
                dev.OpenPropertyStore(0 /*STGM_READ*/, out store);
                var keyFriendly = new PROPERTYKEY
                {
                    fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                    pid = 14
                };
                PROPVARIANT pv;
                if (store.GetValue(ref keyFriendly, out pv) == 0)
                {
                    string s = pv.GetString();
                    PropVariantClear(ref pv);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                return null;
            }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        /// <summary>
        /// 从 HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{endpoint}\Properties
        /// 读取设备名。键名形如 "{a45c254e-...},14"（PKEY_Device_FriendlyName）。
        /// </summary>
        private static string ReadEndpointNameFromRegistry(string endpointId)
        {
            try
            {
                string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\"
                             + endpointId + @"\Properties";
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                           Microsoft.Win32.RegistryHive.LocalMachine,
                           Microsoft.Win32.RegistryView.Registry64))
                using (var k = baseKey.OpenSubKey(sub))
                {
                    if (k == null) return null;
                    foreach (var name in k.GetValueNames())
                    {
                        // PKEY_Device_FriendlyName 的 pid 是 14
                        if (name.EndsWith(",14", StringComparison.Ordinal))
                        {
                            var v = k.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读注册表端点名失败：" + ex.Message);
            }
            return null;
        }

        // ---- MMDevice API 互操作声明 ----

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            int GetDevice(string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out int count);
            int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            int OpenPropertyStore(int access, out IPropertyStore properties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out int count);
            int GetAt(int index, out PROPERTYKEY key);
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1, wReserved2, wReserved3;
            public IntPtr p;
            public int p2;

            public string GetString()
            {
                // VT_LPWSTR = 31
                if (vt != 31 || p == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(p);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        /// <summary>
        /// 打开设备并持续播放。
        /// 实现要点（经实测验证，74 次连续写入零失败）：
        ///   1. 预先分配并准备好所有缓冲，全部写入建立初始队列；
        ///   2. 之后只"读" WAVEHDR.dwFlags 判断 WHDR_DONE，绝不手工写回 flags；
        ///      手工清位会在缓冲仍被驱动持有时破坏状态机，导致 MMSYSERR_INVALPARAM(34)。
        ///   3. pull 返回 null 表示暂无数据，本轮跳过而不是灌静音，避免欠载爆音。
        /// </summary>
        public static void Play(int deviceIndex, Func<int> getSampleRate, Func<int> getChannels,
                                Func<byte[]> pull, CancellationToken token, Profile profile,
                                Action<byte[], int, int> onBlock = null, Equalizer eq = null)
        {
            int sampleRate = getSampleRate();
            int channels = getChannels();

            // 均衡器必须知道实际采样率才能算对系数（同一个频段在 44.1k 与 48k 下系数不同）
            if (eq != null) eq.Configure(sampleRate, channels);

            IntPtr hwo = IntPtr.Zero;
            int hr = OpenDevice(out hwo, deviceIndex, sampleRate, channels);
            if (hr != 0)
            {
                throw new InvalidOperationException("waveOutOpen 失败，错误码 " + hr +
                    "（设备 " + deviceIndex + " 可能不支持 " + sampleRate + "Hz/" + channels + "ch）");
            }

            // 缓冲池：16 个 16 ms 缓冲 = 256 ms 同时在飞，轮询间隔 1 ms。
            //
            // 实测依据（_vmprobe/probe.cs）：
            //   Voicemeeter Input 可连排 24 个缓冲而不被拒，起步约 54 ms 后
            //   每个缓冲精确 16 ms 回收一次。
            // 原先只用 3 个 40 ms 缓冲配合 4 ms 轮询，一旦循环晚几毫秒就有多个
            // 缓冲同时在飞，而 Thread.Sleep 把补充循环整体串行化，补充速度跟不上
            // 回收速度，设备随即饿死——实测消费只有 18986 帧/s（应为 48000）。
            // 缓冲池规模由 profile 决定：
            //   低延迟档要少缓冲（在飞时延小），高品质档要多缓冲（抗调度抖动）。
            int bytesPerMs = Math.Max(1, sampleRate * channels * 2 / 1000);
            int bufBytes = Math.Max(2048, bytesPerMs * Math.Max(2, profile.BlockMs));
            int slots = Math.Max(2, profile.WaveOutBuffers);
            int pollMs = Math.Max(1, profile.PollMs);

            var bufs = new Buf[slots];
            try
            {
                for (int i = 0; i < slots; i++)
                {
                    bufs[i] = new Buf(hwo, bufBytes);
                    // 预填充：首轮就有队列，避免起播卡顿
                    var first = pull();
                    bufs[i].Submit(first == null ? new byte[bufBytes] : Fit(first, bufBytes));
                }

                long writes = 0;
                var lastStat = DateTime.UtcNow;

                while (!token.IsCancellationRequested)
                {
                    bool didWork = false;
                    for (int i = 0; i < slots; i++)
                    {
                        if (!bufs[i].IsDone) continue;
                        var data = pull();
                        if (data == null) continue;
                        var fitted = Fit(data, bufBytes);

                        // 均衡器就地处理。放在提交与可视化回调之前，
                        // 这样频谱画的就是实际听到的声音（包含 EQ 效果）。
                        // Enabled 为 false 时 Process 立即返回，无额外开销。
                        if (eq != null && eq.Enabled)
                        {
                            try { eq.Process(fitted, fitted.Length); }
                            catch (Exception ex)
                            {
                                // EQ 出错不能拖垮播放：关掉它并报告一次
                                eq.Enabled = false;
                                Logger.Err("均衡器处理异常，已自动关闭", ex);
                            }
                        }

                        bufs[i].Submit(fitted);
                        // 通知 GUI：这块音频即将播放，可用于绘制波形/频谱。
                        // 回调抛异常不能影响播放，因此单独捕获并只提示有限次数。
                        if (onBlock != null)
                        {
                            try { onBlock(fitted, sampleRate, channels); }
                            catch (Exception ex)
                            {
                                if (_blockCbErrors++ < 3)
                                    Logger.Warn("音频块回调异常（第 " + _blockCbErrors + " 次）：" + ex.Message);
                            }
                        }
                        writes++;
                        didWork = true;
                    }
                    if (!didWork) Thread.Sleep(pollMs);

                    var now = DateTime.UtcNow;
                    if ((now - lastStat).TotalSeconds >= 10)
                    {
                        // 注意：占位符数量必须与参数个数一致。
                        // 此前这里写了 {0}{1}{2}{3} 却只传 3 个参数，运行 10 秒后
                        // 必然抛 FormatException，被 catch 吞掉后播放线程静默退出。
                        Logger.Trace(string.Format(
                            "播放线程存活：累计写入 {0} 个缓冲，每块 {1}ms x {2} 个缓冲（在飞 {3}ms）",
                            writes, bufBytes * 1000 / Math.Max(1, sampleRate * channels * 2),
                            slots, bufBytes * slots * 1000 / Math.Max(1, sampleRate * channels * 2)));
                        lastStat = now;
                    }
                }
            }
            finally
            {
                Logger.Info("播放线程收尾：reset + 释放缓冲 + close");
                try { waveOutReset(hwo); }
                catch (Exception ex) { Logger.Err("waveOutReset", ex); }
                if (bufs != null) foreach (var b in bufs) if (b != null) b.Dispose();
                try { waveOutClose(hwo); }
                catch (Exception ex) { Logger.Err("waveOutClose", ex); }
            }
        }

        /// <summary>
        /// 试探某设备能否打开（打开后立即关闭）。
        /// 用于判断系统默认设备是否被 Voicemeeter 之类的程序独占：
        /// 被独占时 waveOutOpen 返回 4 = MMSYSERR_ALLOCATED。
        /// </summary>
        public static int ProbeOpen(int deviceIndex, out IntPtr hwo)
        {
            return OpenDevice(out hwo, deviceIndex, 48000, 2);
        }

        private static int OpenDevice(out IntPtr hwo, int deviceIndex, int sampleRate, int channels)
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = (ushort)WAVE_FORMAT_PCM,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                cbSize = 0
            };
            return waveOutOpen(out hwo, deviceIndex, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        }

        private static byte[] Fit(byte[] data, int size)
        {
            if (data.Length == size) return data;
            var outBuf = new byte[size];
            Buffer.BlockCopy(data, 0, outBuf, 0, Math.Min(data.Length, size));
            return outBuf;
        }

        private sealed class Buf : IDisposable
        {
            private readonly IntPtr _hwo;
            private IntPtr _header;
            private IntPtr _data;
            private readonly int _size;
            private readonly int _headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            private readonly byte[] _staging;

            public Buf(IntPtr hwo, int size)
            {
                _hwo = hwo;
                _size = size;
                _staging = new byte[size];
                _data = Marshal.AllocHGlobal(size);
                _header = Marshal.AllocHGlobal(_headerSize);
                var hdr = new WAVEHDR { lpData = _data, dwBufferLength = (uint)size };
                Marshal.StructureToPtr(hdr, _header, false);
                int hr = waveOutPrepareHeader(_hwo, _header, _headerSize);
                if (hr != 0) throw new InvalidOperationException("waveOutPrepareHeader 失败 " + hr);
            }

            /// <summary>只读判断，绝不写回 dwFlags。</summary>
            public bool IsDone
            {
                get
                {
                    var h = (WAVEHDR)Marshal.PtrToStructure(_header, typeof(WAVEHDR));
                    return (h.dwFlags & WHDR_DONE) != 0;
                }
            }

            /// <summary>把数据写入已准备的缓冲并提交给驱动。</summary>
            public void Submit(byte[] data)
            {
                int n = Math.Min(data.Length, _size);
                Marshal.Copy(data, 0, _data, n);
                var h = (WAVEHDR)Marshal.PtrToStructure(_header, typeof(WAVEHDR));
                h.dwBufferLength = (uint)n;
                Marshal.StructureToPtr(h, _header, false);
                int hr = waveOutWrite(_hwo, _header, _headerSize);
                if (hr != 0)
                {
                    throw new InvalidOperationException(string.Format(
                        "waveOutWrite 失败 {0}（缓冲 {1}B，已提交长度 {2}B，dwFlags=0x{3:X}，dwBufferLength={4}）",
                        hr, _size, n, h.dwFlags, h.dwBufferLength));
                }
            }

            public void Dispose()
            {
                try { waveOutUnprepareHeader(_hwo, _header, _headerSize); }
                catch (Exception ex) { Logger.Err("waveOutUnprepareHeader（释放缓冲时）", ex); }
                if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
                if (_header != IntPtr.Zero) { Marshal.FreeHGlobal(_header); _header = IntPtr.Zero; }
            }
        }
    }

    // ------------------------------------------------------------------ 样本环形缓冲

    /// <summary>
    /// 样本环形缓冲：接收线程按包写入，播放线程按时钟读取。
    ///
    /// 存储约定：以"帧"为单位，每帧占 2 个连续槽位（左、右声道）。
    /// 帧容量强制为偶数，因此槽位下标永不跨帧，无需处理环形边界的拼接。
    ///
    /// 读侧使用分数帧位置并做线性插值：两端时钟的细微偏差由插值比例吸收，
    /// 而不是靠周期性丢帧或补帧，因此不会产生周期性的断点。
    /// </summary>
    internal sealed class SampleRing
    {
        private readonly float[] _slots;      // 长度 = 帧容量 * 2
        private readonly int _frameCap;
        private readonly object _gate = new object();
        private long _framesWritten;          // 累计写入帧数，只增
        private long _readFrame;              // 已消费的整数帧位置
        private double _readFrac;             // 已消费的小数偏移，用于插值
        private long _underrunEvents;
        private long _interpolatedFrames;
        private long _framesDropped;
        private long _framesProduced;      // 写入环形的累计帧数
        private long _framesConsumed;      // 读出的累计帧数
        private long _readCalls;

        public SampleRing(int frameCapacity)
        {
            int cap = Math.Max(1024, frameCapacity);
            if (cap % 2 != 0) cap++;
            _frameCap = cap;
            _slots = new float[_frameCap * 2];
        }

        public int CapacityFrames { get { return _frameCap; } }
        public long FramesWritten { get { lock (_gate) return _framesWritten; } }
        public long UnderrunEvents { get { return Interlocked.Read(ref _underrunEvents); } }
        public long InterpolatedFrames { get { return Interlocked.Read(ref _interpolatedFrames); } }
        public long FramesDropped { get { return Interlocked.Read(ref _framesDropped); } }
        public long FramesProduced { get { return Interlocked.Read(ref _framesProduced); } }
        public long FramesConsumed { get { return Interlocked.Read(ref _framesConsumed); } }
        public long ReadCalls { get { return Interlocked.Read(ref _readCalls); } }

        /// <summary>当前可读帧数。</summary>
        public int AvailableFrames
        {
            get
            {
                lock (_gate)
                {
                    double consumed = _readFrame + _readFrac;
                    return (int)Math.Max(0, _framesWritten - consumed);
                }
            }
        }

        /// <summary>
        /// 写入 PCM16LE 交错数据，channels 支持 1 或 2。
        ///
        /// 空间不足时不再拒收整包，而是丢弃最旧的帧以腾出空间。
        /// 原因：拒收整包会在音频流里挖出一个 21 ms 的洞；实测每秒丢 23 个包，
        /// 等价于 23 Hz 的调制，听感就是明显的"电音"。丢弃最旧帧只损失约 21 ms
        /// 的已过期音频，最新的连续数据得以保留，因此不会产生周期性空洞。
        ///
        /// 返回 false 表示发生了丢弃（调用方据此计数，但不作为错误处理）。
        /// </summary>
        public bool Write(byte[] pcm, int channels, int payloadBytes)
        {
            if (channels < 1) return false;
            int bytesPerFrame = channels * 2;
            int frames = payloadBytes / bytesPerFrame;
            if (frames <= 0) return false;

            bool dropped = false;
            lock (_gate)
            {
                if (frames > _frameCap) frames = _frameCap;

                // 消费位置必须包含分数部分。此前只减 _readFrame 而漏掉 _readFrac，
                // 使 avail 偏大、需要丢弃的帧数算少，音频因而越过容量边界写穿环形缓冲，
                // 表现为水位远超容量（实测到 3843 ms，而容量只有 150 ms）
                // 以及随之而来的欠载与丢帧。
                double consumed = _readFrame + _readFrac;
                int avail = (int)(_framesWritten - consumed);

                // 需要腾出 frames 个空位；必要时把读取位置向前推（丢弃最旧数据）
                int need = frames - (_frameCap - avail);
                if (need > 0)
                {
                    if (need > avail) need = avail;
                    _readFrame += need;
                    _readFrac = 0;
                    Interlocked.Add(ref _framesDropped, need);
                    dropped = true;
                }

                for (int f = 0; f < frames; f++)
                {
                    int b = f * bytesPerFrame;
                    short l = (short)(pcm[b] | (pcm[b + 1] << 8));
                    short r = channels >= 2 ? (short)(pcm[b + 2] | (pcm[b + 3] << 8)) : l;

                    int slot = (int)(_framesWritten % _frameCap) * 2;
                    _slots[slot] = l / 32768f;
                    _slots[slot + 1] = r / 32768f;
                    _framesWritten++;
                }
                Interlocked.Add(ref _framesProduced, frames);
            }
            return !dropped;
        }

        /// <summary>
        /// 读取 outFrames 帧，写成立体声交错 PCM16LE。
        /// ratio = 1.0 为标准速率；大于 1 表示消费更快（用于压低积压），小于 1 表示更慢。
        /// 数据见底时剩余部分补静音，并计入一次 underrun——不静默。
        /// </summary>
        public int Read(byte[] dest, int outFrames, double ratio)
        {
            if (ratio < 0.8) ratio = 0.8;
            if (ratio > 1.2) ratio = 1.2;

            lock (_gate)
            {
                int produced = 0;
                for (int f = 0; f < outFrames; f++)
                {
                    if (_readFrame + 1 >= _framesWritten)
                    {
                        Interlocked.Increment(ref _underrunEvents);
                        for (int k = f; k < outFrames; k++)
                        {
                            dest[k * 4] = 0; dest[k * 4 + 1] = 0;
                            dest[k * 4 + 2] = 0; dest[k * 4 + 3] = 0;
                        }
                        return produced;
                    }

                    int s0 = (int)(_readFrame % _frameCap) * 2;
                    int s1 = (int)((_readFrame + 1) % _frameCap) * 2;
                    double frac = _readFrac;

                    float l = (float)(_slots[s0] * (1 - frac) + _slots[s1] * frac);
                    float r = (float)(_slots[s0 + 1] * (1 - frac) + _slots[s1 + 1] * frac);
                    if (frac > 0.0001) Interlocked.Increment(ref _interpolatedFrames);

                    short ls = (short)Math.Max(-32768, Math.Min(32767, l * 32767f));
                    short rs = (short)Math.Max(-32768, Math.Min(32767, r * 32767f));
                    dest[f * 4] = (byte)(ls & 0xFF);
                    dest[f * 4 + 1] = (byte)((ls >> 8) & 0xFF);
                    dest[f * 4 + 2] = (byte)(rs & 0xFF);
                    dest[f * 4 + 3] = (byte)((rs >> 8) & 0xFF);
                    produced++;

                    _readFrac += ratio;
                    while (_readFrac >= 1.0) { _readFrac -= 1.0; _readFrame++; }
                }
                Interlocked.Add(ref _framesConsumed, produced);
                Interlocked.Increment(ref _readCalls);
                return produced;
            }
        }
    }

    // ------------------------------------------------------------------ 接收端

    internal sealed class SnadReceiver : IDisposable
    {
        private readonly int _deviceIndex;
        private readonly int _port;

        // 样本环形缓冲：目标水位 80 ms、容量 150 ms、起播预填充 60 ms。
        //
        // 水位依据实测标定，不是拍脑袋：
        //   网络抖动 5-8 ms（热点 RTT），零丢包（序号缺口恒为 0）；
        //   手机产出约 48128 帧/s，本机消费 48000 帧/s，漂移约 0.27%；
        //   50 ms 目标实测会出现欠载（Windows 线程调度抖动），
        //   120 ms 则从未欠载但延迟偏高。80 ms 为折中。
        //
        // 注意：尺寸必须按"实际采样率"换算，不能把 48000 写死。
        // 采样率要等第一个包才能确定，因此环形缓冲延迟到那时再建。
        private SampleRing _ring;
        private int _prebufferFrames;
        private volatile bool _ringReady;      // 环形缓冲已按实际采样率建好

        /// <summary>
        /// 当前生效的参数档位。所有影响延迟与音质的取值都由此提供，
        /// 不写死在代码里——换场景只需换 profile 或改 receiver.cfg。
        /// </summary>
        private readonly Profile _profile;

        public Profile CurrentProfile { get { return _profile; } }

        /// <summary>
        /// 均衡器。由 GUI 或控制台在启动前配置；播放线程在提交音频前调用它。
        /// 采样率由 WaveOut.Play 在打开设备时按实际值配置，因此这里不必预设。
        /// </summary>
        public Equalizer Equalizer { get { return _eq; } }

        private readonly Equalizer _eq = new Equalizer();

        /// <summary>
        /// 每次向 waveOut 提交一块音频时触发，供 GUI 绘制波形/频谱。
        /// 参数为立体声交错 PCM16LE。
        /// </summary>
        public event Action<byte[], int, int> AudioBlockReady;

        /// <summary>供 GUI 轮询的实时指标快照。</summary>
        public sealed class Metrics
        {
            public int SampleRate;
            public int Channels;
            public long Packets;
            public double Kbps;
            public int BufferFrames;
            public int BufferMs;
            public int RingCapacityFrames;
            public double RateIn, RateWritten, RateRead;
            public double ReadCallsPerSec;
            public long FramesDropped, Underruns, Interpolated, SeqGaps, SeqMissing;
            public long ShortPkt, BadMagic, BadVersion, BadPayload;
            public int TargetMs, CapacityMs, BlockMs, WaveOutBuffers;
            public string ProfileName;
        }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private volatile int _sampleRate = 48000;
        private volatile int _channels = 2;
        private long _packets, _bytes, _overflow;
        private long _shortPkt, _badMagic, _badVersion, _badPayload;

        // ---- 端到端延迟测量的第一段：包到达 ----
        //
        // 手机在每个音频包里带了采集时刻（单调时钟 ms）。要把它换算成延迟，
        // 缺的是两台机器的时钟差。这里先取**最小的 (到达 − 采集)** 作为该差值的
        // 下界估计：最理想的那个包几乎没排队，所以它最接近真实的时钟差。
        //
        // 误差 = 该包真实的网络单向时延（局域网 1–10 ms）。这个误差是**已知方向**的：
        // 估计值偏大，因此算出的延迟偏小。等手机端能回显信标后改用往返校准，
        // 误差可降到 RTT/2 且方向对称。
        private long _clockSkewFloorMs = long.MaxValue;   // min(到达 − 采集)
        private long _lastArrivalMs;                       // 最近一包到达时刻（本机单调 ms）
        private uint _lastPhoneTsMs;                       // 该包的采集时刻

        /// <summary>时钟校准器。收到第一个音频包时创建（此时才知道手机地址）。</summary>
        private SnadClockSync _clockSync;
        private long _clockSyncStartFailures;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static double NowMs() { return Stopwatch.GetTimestamp() * MsPerTick; }
        private long _logLines, _logPkts;
        private bool _formatLogged;
        private int _seqExpected = -1;
        private long _seqGaps, _seqMissing;
        private long _framesIn;            // 从网络收到的累计帧数（包级，未丢）
        private long _lastFramesIn, _lastFramesWritten, _lastFramesRead, _lastReadCalls;
        // 最近一次统计周期的速率，供 GUI 快照读取（避免 GUI 自己再算一遍）。
        // 注意不能标 volatile：C# 不允许 volatile double（64 位读写非原子）。
        // 这些值每 5 秒才更新一次，GUI 读到略旧的值无影响。
        private double _lastRateIn, _lastRateWritten, _lastRateRead, _lastRateCalls, _lastKbps;

        public SnadReceiver(int deviceIndex, int port, Profile profile)
        {
            _deviceIndex = deviceIndex;
            _port = port;
            _profile = profile ?? new Profile();
            Logger.Info("生效档位：" + _profile);
        }

        /// <summary>采集一份实时指标，供 GUI 显示。</summary>
        public Metrics Snapshot()
        {
            var m = new Metrics
            {
                SampleRate = _sampleRate,
                Channels = _channels,
                Packets = Interlocked.Read(ref _packets),
                BufferFrames = _ringReady ? _ring.AvailableFrames : 0,
                RingCapacityFrames = _ringReady ? _ring.CapacityFrames : _profile.RingCapacityFrames(_sampleRate),
                FramesDropped = _ringReady ? _ring.FramesDropped : 0,
                Underruns = _ringReady ? _ring.UnderrunEvents : 0,
                Interpolated = _ringReady ? _ring.InterpolatedFrames : 0,
                SeqGaps = _seqGaps,
                SeqMissing = _seqMissing,
                ShortPkt = Interlocked.Read(ref _shortPkt),
                BadMagic = Interlocked.Read(ref _badMagic),
                BadVersion = Interlocked.Read(ref _badVersion),
                BadPayload = Interlocked.Read(ref _badPayload),
                TargetMs = _profile.TargetBufferMs,
                CapacityMs = _profile.RingCapacityMs,
                BlockMs = _profile.BlockMs,
                WaveOutBuffers = _profile.WaveOutBuffers,
                ProfileName = _profile.Name,
            };
            m.BufferMs = m.SampleRate > 0 ? m.BufferFrames * 1000 / m.SampleRate : 0;
            m.RateIn = _lastRateIn;
            m.RateWritten = _lastRateWritten;
            m.RateRead = _lastRateRead;
            m.ReadCallsPerSec = _lastRateCalls;
            m.Kbps = _lastKbps;
            return m;
        }

        public void Stop()
        {
            try { _cts.Cancel(); }
            catch (Exception ex) { Logger.Err("取消令牌", ex); }
        }

        /// <summary>
        /// 接收手机日志文本包并落盘为 phone.log。
        /// 这是"日志传电脑能直接读"的实现：手机端任何报错都会实时出现在这个文件里。
        /// </summary>
        private void LogListener()
        {
            var logPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "phone.log");
            Logger.Info("手机日志监听已启动，端口 " + (_port + 1) + "，写入 " + logPath);

            var buffer = new StringBuilder();
            DateTime lastFlush = DateTime.UtcNow;

            try
            {
                using (var sock = new UdpClient(_port + 1))
                {
                    sock.Client.ReceiveBufferSize = 1 << 20;
                    var remote = new IPEndPoint(IPAddress.Any, 0);

                    while (!_cts.IsCancellationRequested)
                    {
                        byte[] pkt;
                        try { pkt = sock.Receive(ref remote); }
                        catch (SocketException ex) { Logger.Warn("日志端口接收异常，继续: " + ex.Message); continue; }
                        catch (ObjectDisposedException) { break; }

                        // 文本包头 14 字节，type 位于第 5 字节
                        if (pkt.Length < 14 || pkt[0] != 'S' || pkt[1] != 'N' || pkt[2] != 'A' || pkt[3] != 'D')
                        {
                            Logger.Warn("日志端口收到非 SNAD 包，长度 " + pkt.Length + "，来自 " + remote);
                            continue;
                        }
                        if (pkt[5] != (byte)'T')
                        {
                            Logger.Warn("日志端口收到非文本包 type=" + pkt[5] + "，来自 " + remote);
                            continue;
                        }

                        var text = Encoding.UTF8.GetString(pkt, 14, pkt.Length - 14);
                        Interlocked.Increment(ref _logPkts);
                        buffer.Append(text).Append(Environment.NewLine);
                        Interlocked.Add(ref _logLines, 1);

                        var now = DateTime.UtcNow;
                        if (buffer.Length > 8192 || (now - lastFlush).TotalSeconds >= 1)
                        {
                            System.IO.File.AppendAllText(logPath, buffer.ToString(), Encoding.UTF8);
                            buffer.Clear();
                            lastFlush = now;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Err("日志监听线程异常退出", ex);
            }
            finally
            {
                try
                {
                    if (buffer.Length > 0)
                        System.IO.File.AppendAllText(logPath, buffer.ToString(), Encoding.UTF8);
                }
                catch (Exception ex) { Logger.Err("最后刷写 phone.log", ex); }
                Logger.Info("日志监听结束，共收到 " + Interlocked.Read(ref _logLines) + " 行");
            }
        }

        public void Run()
        {
            var audio = new Thread(AudioLoop) { IsBackground = true, Name = "snad-audio" };
            audio.Start();

            var logThread = new Thread(LogListener) { IsBackground = true, Name = "snad-log" };
            logThread.Start();

            try
            {
                using (var sock = new UdpClient(_port))
                {
                    sock.Client.ReceiveBufferSize = 1 << 20;
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    var lastReport = DateTime.UtcNow;
                    long lastBytes = 0;
                    string lastRemote = "(尚未收到)";

                    while (!_cts.IsCancellationRequested)
                    {
                        byte[] pkt;
                        try { pkt = sock.Receive(ref remote); }
                        catch (SocketException ex)
                        {
                            Logger.Warn("音频端口接收异常，继续: " + ex.Message);
                            continue;
                        }
                        catch (ObjectDisposedException) { break; }

                        lastRemote = remote.ToString();

                        if (pkt.Length < Program.HeaderSize)
                        {
                            Interlocked.Increment(ref _shortPkt);
                            if (Interlocked.Read(ref _shortPkt) <= 3)
                                Logger.Warn("丢弃过短包：" + pkt.Length + " 字节（需 ≥ " + Program.HeaderSize + "），来自 " + remote);
                            continue;
                        }
                        if (pkt[0] != 'S' || pkt[1] != 'N' || pkt[2] != 'A' || pkt[3] != 'D')
                        {
                            Interlocked.Increment(ref _badMagic);
                            if (Interlocked.Read(ref _badMagic) <= 3)
                                Logger.Warn("丢弃 magic 不匹配的包，首字节=" + pkt[0] + "，来自 " + remote);
                            continue;
                        }
                        if (pkt[4] != 1)
                        {
                            Interlocked.Increment(ref _badVersion);
                            if (Interlocked.Read(ref _badVersion) <= 3)
                                Logger.Warn("丢弃协议版本不支持的包：version=" + pkt[4]);
                            continue;
                        }

                        int rate = BitConverter.ToInt32(pkt, 6);
                        int ch = pkt[5];

                        // 手机采集时刻（单调时钟低 32 位，ms）。
                        //
                        // 这个字段协议里一直有，接收端此前从未读取——
                        // 时间戳在线上跑了几万包，却没人用它。端到端延迟要可测，
                        // 起点只能是它：没有它就只剩"包到达 PC"这个更晚的时刻。
                        uint phoneTsMs = BitConverter.ToUInt32(pkt, 14);

                        int seq = BitConverter.ToInt32(pkt, 10);
                        int payload = BitConverter.ToInt32(pkt, 18);
                        if (payload <= 0 || Program.HeaderSize + payload > pkt.Length)
                        {
                            Interlocked.Increment(ref _badPayload);
                            if (Interlocked.Read(ref _badPayload) <= 3)
                                Logger.Warn("丢弃负载长度非法的包：声明 " + payload + " 字节，实际可用 " +
                                            (pkt.Length - Program.HeaderSize) + " 字节");
                            continue;
                        }

                        // 包序号连续性检查——丢包会体现为音质下降，必须可见
                        if (_seqExpected >= 0 && seq != _seqExpected)
                        {
                            int missing = seq - _seqExpected;
                            if (missing > 0)
                            {
                                _seqGaps++;
                                _seqMissing += missing;
                                Logger.Trace(string.Format(
                                    "序号跳跃：期望 {0} 收到 {1}，缺 {2} 包（累计缺口 {3} 次/{4} 包）",
                                    _seqExpected, seq, missing, _seqGaps, _seqMissing));
                            }
                            else
                            {
                                // 乱序或重传，不算丢包但要留痕
                                Logger.Trace(string.Format(
                                    "序号回退：期望 {0} 收到 {1}（乱序或重传）", _seqExpected, seq));
                            }
                        }
                        _seqExpected = seq + 1;

                        if (!_formatLogged || rate != _sampleRate || ch != _channels)
                        {
                            _sampleRate = rate > 0 ? rate : 48000;
                            _channels = ch > 0 ? ch : 2;

                            // 环形缓冲按实际采样率换算，避免把 48000 写死。
                            // 尺寸全部由 profile 提供，并按实际采样率换算，不写死 48000。
                            int capFrames = _profile.RingCapacityFrames(_sampleRate);
                            _ring = new SampleRing(capFrames);
                            _prebufferFrames = _profile.PrebufferFrames(_sampleRate);
                            _formatLogged = true;
                            _ringReady = true;   // 此刻起 _ring 才可被安全访问

                            Logger.Info(string.Format(
                                "流格式确定：{0} Hz / {1} 声道 / 16 bit，第一个包来自 {2}",
                                _sampleRate, _channels, remote));
                            Logger.Info(string.Format(
                                "档位 {0}：容量 {1} 帧（{2} ms）、预填充 {3} 帧（{4} ms）、" +
                                "每块 {5} 帧（{6} ms）、waveOut {7} 个、预计链路延迟约 {8} ms",
                                _profile.Name,
                                _ring.CapacityFrames, _ring.CapacityFrames * 1000 / _sampleRate,
                                _prebufferFrames, _prebufferFrames * 1000 / _sampleRate,
                                _profile.BlockFrames(_sampleRate), _profile.BlockMs,
                                _profile.WaveOutBuffers, _profile.EstimatedPipelineLatencyMs()));
                            if (rate != 48000)
                                Logger.Warn("手机采样率 " + rate + " Hz（非 48000），已按实际值处理");
                        }

                        // 逐包明细：仅 Verbose 档落盘。
                        // 首 5 个包全记，之后每 50 个记一次，兼顾可读性与体积。
                        long pktNo = Interlocked.Read(ref _packets);
                        if (pktNo < 5 || pktNo % 50 == 0)
                        {
                            Logger.Trace(string.Format(
                                "包#{0} seq={1} {2}字节 {3}帧 缓冲={4}帧 来自 {5}",
                                pktNo, seq, payload, payload / Math.Max(1, _channels * 2),
                                _ring.AvailableFrames, remote));
                        }

                        // 记录这一包的到达时刻，并与手机采集时刻相减。
                        // 差值的**最小值**就是两端时钟差的下界估计：
                        // 最理想的那个包几乎没排队，它最接近真实时钟差。
                        {
                            double arrivalMs = NowMs();
                            long diff = (long)arrivalMs - phoneTsMs;
                            if (diff < _clockSkewFloorMs) _clockSkewFloorMs = diff;
                            _lastArrivalMs = (long)arrivalMs;
                            _lastPhoneTsMs = phoneTsMs;
                        }

                        // 第一个音频包带来了手机地址——从这一刻起才能做时钟校准。
                        // 失败必须留痕：没有校准，端到端延迟就不能报数。
                        if (_clockSync == null)
                        {
                            try
                            {
                                _clockSync = new SnadClockSync(
                                    SnadSecret.Psk, remote.Address, SnadBeacon.DefaultPort,
                                    msg => Logger.Info(msg));
                                _clockSync.Start();
                                Logger.Info("时钟校准已启动：目标 " + remote.Address +
                                            ":" + SnadBeacon.DefaultPort);
                            }
                            catch (Exception ex)
                            {
                                _clockSyncStartFailures++;
                                _clockSync = null;
                                Logger.Err("时钟校准启动失败（端到端延迟将不可用）", ex);
                            }
                        }

                        var pcm = new byte[payload];
                        Buffer.BlockCopy(pkt, Program.HeaderSize, pcm, 0, payload);

                        Interlocked.Increment(ref _packets);
                        Interlocked.Add(ref _bytes, payload);
                        Interlocked.Add(ref _framesIn, payload / Math.Max(1, _channels * 2));

                        if (!_ringReady)
                        {
                            // 还没收到第一个包，无从得知采样率，本包无法处理（正常不会发生：
                            // 格式判定就在本循环内、紧随其后完成）。留痕而非静默跳过。
                            Logger.Trace("收到包但环形缓冲尚未建立（缺少格式信息），本包丢弃");
                            continue;
                        }

                        if (!_ring.Write(pcm, _channels, payload))
                        {
                            long ov = Interlocked.Increment(ref _overflow);
                            if (ov <= 3 || ov % 100 == 0)
                                Logger.Warn("环形缓冲已满，丢弃第 " + ov + " 个包（可用 " +
                                            _ring.AvailableFrames + " 帧 / 容量 " + _ring.CapacityFrames +
                                            " 帧）。持续出现说明播放侧未跟上");
                        }

                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalSeconds >= 5)
                        {
                            long p = Interlocked.Read(ref _packets);
                            long b = Interlocked.Read(ref _bytes);
                            double dt = (now - lastReport).TotalSeconds;
                            double kbps = (b - lastBytes) * 8.0 / 1000.0 / dt;

                            // ---- 速率账本：把每一级的实际帧率打出来 ----
                            // 这是排查"电音"的关键仪器。三者必须都接近 48000：
                            //   in  = 网络到达的帧率（手机产出侧）
                            //   wr  = 真正写进环形缓冲的帧率
                            //   rd  = 播放侧读出的帧率
                            // in 与 rd 的差值就是漂移量；若 wr < in 说明写不进去（丢帧）。
                            long fIn = Interlocked.Read(ref _framesIn);
                            long fWr = _ring.FramesProduced;
                            long fRd = _ring.FramesConsumed;
                            long rc = _ring.ReadCalls;
                            double rateIn = (fIn - _lastFramesIn) / dt;
                            double rateWr = (fWr - _lastFramesWritten) / dt;
                            double rateRd = (fRd - _lastFramesRead) / dt;
                            double rateCalls = (rc - _lastReadCalls) / dt;

                            Logger.Info(string.Format(
                                "包={0} {1:F0}kbps 缓冲={2}帧({3}ms) " +
                                "速率[到{4:F0} 写{5:F0} 读{6:F0}] 差[读-到={7:+0;-0;0}] " +
                                "Read调用={8:F0}/s(每块{9:F1}ms) 丢最旧帧={10} 欠载={11} 插值={12} " +
                                "序号缺口={13}(缺{14}包) 丢弃[短{15} magic{16} 版本{17} 负载{18}]",
                                p, kbps, _ring.AvailableFrames, _ring.AvailableFrames / 48,
                                rateIn, rateWr, rateRd, rateRd - rateIn,
                                rateCalls, rateCalls > 0 ? 1000.0 / rateCalls : 0,
                                _ring.FramesDropped, _ring.UnderrunEvents, _ring.InterpolatedFrames,
                                _seqGaps, _seqMissing,
                                Interlocked.Read(ref _shortPkt), Interlocked.Read(ref _badMagic),
                                Interlocked.Read(ref _badVersion), Interlocked.Read(ref _badPayload)));

                            // 端到端测量的当前进度：手机采集 → 现在，已经走了多久。
                            //
                            // 它还不是完整延迟：还差"最后这块音频真正播出的时刻"。
                            // 但那一段是确定的（缓冲区里还有多少帧 ÷ 采样率），
                            // 因此这里先给出**已确定的**部分，不掺估计。
                            if (_clockSkewFloorMs != long.MaxValue)
                            {
                                double sinceArrival = NowMs() - _lastArrivalMs;
                                double inFlight = (_lastArrivalMs - _lastPhoneTsMs) - _clockSkewFloorMs;
                                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                                    "[端到端] 手机采集→现在 = {0:F1} ms（含在途 {1:F1} + 到达后 {2:F1}）；" +
                                    "时钟差下界 {3} ms（误差 ≤ 最小网络单向时延）",
                                    inFlight + sinceArrival, inFlight, sinceArrival,
                                    _clockSkewFloorMs));
                            }

                            // 时钟校准状态必须每报一次：没有它，上面那个数只能算"已走过的时长"，
                            // 不能算端到端延迟。缺样本时说清楚缺，而不是沉默。
                            if (_clockSync == null)
                            {
                                Logger.Warn("[时钟] 尚未启动（启动失败 " + _clockSyncStartFailures +
                                            " 次），端到端延迟不可用");
                            }
                            else if (!_clockSync.HasCalibration)
                            {
                                Logger.Warn("[时钟] 未校准：已失败 " + _clockSync.Failures +
                                            " 次" + (_clockSync.LastError != null
                                                ? "，最后一次原因：" + _clockSync.LastError : "") +
                                            "；端到端延迟不可用");
                            }
                            else
                            {
                                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                                    "[时钟] 已校准：Δ={0:F2} ms（样本 {1}，最优 RTT {2:F1} ms，失败 {3} 次）",
                                    _clockSync.OffsetMs, _clockSync.Samples,
                                    _clockSync.BestRttMs, _clockSync.Failures));
                            }

                            _lastFramesIn = fIn;
                            _lastFramesWritten = fWr;
                            _lastFramesRead = fRd;
                            _lastReadCalls = rc;
                            // 同时写入快照字段供 GUI 读取
                            _lastRateIn = rateIn;
                            _lastRateWritten = rateWr;
                            _lastRateRead = rateRd;
                            _lastRateCalls = rateCalls;
                            _lastKbps = kbps;
                            lastReport = now;
                            lastBytes = b;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Err("接收主循环异常退出", ex);
            }
        }

        private void AudioLoop()
        {
            try
            {
                // 先等第一个包确定格式。环形缓冲要按实际采样率换算尺寸，
                // 不能把 48000 写死，因此这里必须等到 _ringReady。
                var waitFmt = DateTime.UtcNow;
                while (!_cts.IsCancellationRequested && !_ringReady)
                {
                    Thread.Sleep(10);
                    if ((DateTime.UtcNow - waitFmt).TotalSeconds > _profile.FormatWaitSec)
                    {
                        Logger.Warn("等待首个数据包超过 " + _profile.FormatWaitSec + " 秒仍未收到，" +
                                    "播放线程退出。请确认手机端已开始串流、且与本机在同一网络。");
                        return;
                    }
                }
                if (_cts.IsCancellationRequested) return;

                // 预填充：先攒够设定水位再起播。否则一起播就见底，
                // 播放侧拿不到数据只能补零，波形被切断，听感即"电音"。
                Logger.Info(string.Format("等待预填充 {0} 帧（约 {1} ms，采样率 {2} Hz）...",
                            _prebufferFrames, _prebufferFrames * 1000 / _sampleRate, _sampleRate));
                var waitStart = DateTime.UtcNow;
                while (!_cts.IsCancellationRequested && _ring.AvailableFrames < _prebufferFrames)
                {
                    Thread.Sleep(10);
                    if ((DateTime.UtcNow - waitStart).TotalSeconds > _profile.PrebufferWaitSec)
                    {
                        Logger.Warn("预填充等待超过 " + _profile.PrebufferWaitSec + " 秒，仅攒到 " +
                                    _ring.AvailableFrames + " 帧（目标 " + _prebufferFrames +
                                    "），先起播（仍可能欠载）");
                        break;
                    }
                }
                Logger.Info(string.Format("预填充完成，可用 {0} 帧（约 {1} ms），开始播放",
                            _ring.AvailableFrames, _ring.AvailableFrames * 1000 / _sampleRate));

                // 每块时长、缓冲个数、轮询间隔、水位与漂移摆幅全部取自 profile，
                // 因此换场景只需换档位，不必改代码。
                int framesPerBuffer = _profile.BlockFrames(_sampleRate);
                int targetFrames = _profile.TargetFrames(_sampleRate);
                double gain = _profile.DriftGain;
                double range = _profile.DriftRange;

                Logger.Info(string.Format(
                    "播放参数：每块 {0} 帧（{1} ms）、目标水位 {2} 帧（{3} ms）、" +
                    "漂移摆幅 ±{4}%、增益 {5}",
                    framesPerBuffer, framesPerBuffer * 1000 / _sampleRate,
                    targetFrames, _profile.TargetBufferMs, range * 100, gain));

                WaveOut.Play(_deviceIndex, () => _sampleRate, () => _channels, () =>
                {
                    // ratio 吸收两端时钟的速率差。
                    //
                    // 实测数据：手机产出约 48,128 帧/s，本机消费 48,000 帧/s，
                    // 产出快约 0.27%。若比率上限只有 ±2%，在某些时段（消费率
                    // 实测 99.5% 实时）绝对差会超出可修正范围，缓冲被顶满，
                    // 于是每秒丢约 23 个整包 = 23 Hz 的调制音，听感即"电音"。
                    //
                    // 因此把范围放宽到 ±8%：远大于 0.27% 的实际漂移，
                    // 保证控制器总能找到把水位拉回目标的工作点。
                    // 稳态下比率会非常接近 1.0，插值带来的音高变化不可察觉。
                    // 目标水位 80 ms。
                    // 标定过程：50 ms 时水位落在 41-67 ms，55 秒内出现 7 次欠载
                    // （丢帧 3129 帧），说明余量不足以吸收 Windows 线程调度抖动；
                    // 120 ms 则从未欠载但延迟偏高。80 ms 是两者之间的折中。
                    int avail = _ring.AvailableFrames;
                    double ratio = 1.0 + (avail - targetFrames) / (double)Math.Max(1, targetFrames) * gain;
                    double hi = 1.0 + range, lo = 1.0 - range;
                    if (ratio > hi) ratio = hi;
                    if (ratio < lo) ratio = lo;

                    var buf = new byte[framesPerBuffer * 4];
                    _ring.Read(buf, framesPerBuffer, ratio);
                    return buf;
                }, _cts.Token, _profile, AudioBlockReady, Equalizer);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("音频线程正常取消退出");
            }
            catch (Exception ex)
            {
                Logger.Err("音频线程异常退出", ex);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
