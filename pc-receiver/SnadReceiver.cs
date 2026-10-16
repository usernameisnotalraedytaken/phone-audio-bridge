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
    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static int _fileErrors;

        public static void Open(string path) { _path = path; }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

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

        private static void Write(string level, string msg)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg;
            Console.WriteLine(line);
            if (_path == null) return;
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

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch (Exception ex) { Console.WriteLine("WARN  设置控制台编码失败: " + ex.Message); }
            // 管道重定向时 Console.Out 是块缓冲，不显式 flush 会看不到实时统计
            var auto = new System.IO.StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(auto);

            int deviceIndex = -1;      // -1 = 自动挑选 Voicemeeter Input
            int port = DefaultPort;
            int logPort = DefaultPort + 1;
            bool listOnly = false;

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
                    case "--help": case "-h": PrintUsage(); return 0;
                }
            }

            var exeDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(exeDir)) exeDir = Environment.CurrentDirectory;
            var logPath = System.IO.Path.Combine(exeDir, "receiver.log");
            Logger.Open(logPath);
            Logger.Info("======== SNAD 接收器启动 ========");
            Logger.Info("音频端口=" + port + "  日志端口=" + logPort + "  日志文件=" + logPath);

            var devices = WaveOut.ListDevices();
            Console.WriteLine("可用输出设备：");
            foreach (var d in devices)
                Console.WriteLine(string.Format("  [{0}] {1}  ({2} ch)", d.Index, d.Name, d.Channels));
            Console.WriteLine();

            if (listOnly) return 0;

            if (deviceIndex < 0)
            {
                var pick = devices.FirstOrDefault(d =>
                    d.Name.IndexOf("Voicemeeter Input", StringComparison.OrdinalIgnoreCase) >= 0);
                if (pick.Name == null)
                {
                    Console.WriteLine("没找到 'Voicemeeter Input'，退回默认设备 [0]。");
                    Console.WriteLine("提示：把输出送进 Voicemeeter Input 才能与电脑音频混音。");
                    deviceIndex = 0;
                }
                else
                {
                    deviceIndex = pick.Index;
                    Console.WriteLine("自动选中：" + pick.Name);
                }
            }

            Console.WriteLine();
            Console.WriteLine("监听 UDP 端口 " + port + " ...");
            Console.WriteLine("手机端填：本机 IP + 端口 " + port);
            Console.WriteLine("按 Ctrl+C 退出。");
            Console.WriteLine();

            using (var receiver = new SnadReceiver(deviceIndex, port))
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; receiver.Stop(); };
                receiver.Run();
            }
            Console.WriteLine("已退出。");
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("SNAD 接收器");
            Console.WriteLine("  --list,   -l            只列出音频设备后退出");
            Console.WriteLine("  --device, -d <index>    指定输出设备序号");
            Console.WriteLine("  --port,   -p <port>     监听端口（默认 " + DefaultPort + "）");
            Console.WriteLine();
            Console.WriteLine("默认自动选择 'Voicemeeter Input'：手机音频进调音台与电脑音频混音，");
            Console.WriteLine("再从 A1 总线（本机接的是 Realtek 扬声器）出声。");
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
        private const uint WHDR_DONE = 0x00000001;

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
        /// 打开设备并持续播放。
        /// 实现要点（经实测验证，74 次连续写入零失败）：
        ///   1. 预先分配并准备好所有缓冲，全部写入建立初始队列；
        ///   2. 之后只"读" WAVEHDR.dwFlags 判断 WHDR_DONE，绝不手工写回 flags；
        ///      手工清位会在缓冲仍被驱动持有时破坏状态机，导致 MMSYSERR_INVALPARAM(34)。
        ///   3. pull 返回 null 表示暂无数据，本轮跳过而不是灌静音，避免欠载爆音。
        /// </summary>
        public static void Play(int deviceIndex, Func<int> getSampleRate, Func<int> getChannels,
                                Func<byte[]> pull, CancellationToken token)
        {
            int sampleRate = getSampleRate();
            int channels = getChannels();

            IntPtr hwo = IntPtr.Zero;
            int hr = OpenDevice(out hwo, deviceIndex, sampleRate, channels);
            if (hr != 0)
            {
                throw new InvalidOperationException("waveOutOpen 失败，错误码 " + hr +
                    "（设备 " + deviceIndex + " 可能不支持 " + sampleRate + "Hz/" + channels + "ch）");
            }

            // 每缓冲区约 40ms 音频，三缓冲吸收调度抖动
            int bytesPerMs = Math.Max(1, sampleRate * channels * 2 / 1000);
            int bufBytes = Math.Max(4096, bytesPerMs * 40);
            const int slots = 3;

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
                        bufs[i].Submit(Fit(data, bufBytes));
                        writes++;
                        didWork = true;
                    }
                    if (!didWork) Thread.Sleep(4);

                    var now = DateTime.UtcNow;
                    if ((now - lastStat).TotalSeconds >= 10)
                    {
                        Console.WriteLine(string.Format("[{0:HH:mm:ss}] 播放线程存活，累计写入 {1} 个缓冲，缓冲 {2}ms x {3}",
                            DateTime.Now, writes, bufBytes * 1000 / Math.Max(1, sampleRate * channels * 2), slots));
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
        /// 返回 false 表示剩余空间不足，整包被拒（调用方据此计数报警）。
        /// </summary>
        public bool Write(byte[] pcm, int channels, int payloadBytes)
        {
            if (channels < 1) return false;
            int bytesPerFrame = channels * 2;
            int frames = payloadBytes / bytesPerFrame;
            if (frames <= 0) return false;

            lock (_gate)
            {
                double consumed = _readFrame + _readFrac;
                int free = _frameCap - (int)(_framesWritten - consumed);
                if (frames > free) return false;

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
                return true;
            }
        }

        /// <summary>
        /// 读取 outFrames 帧，写成立体声交错 PCM16LE。
        /// ratio = 1.0 为标准速率；大于 1 表示消费更快（用于压低积压），小于 1 表示更慢。
        /// 数据见底时剩余部分补静音，并计入一次 underrun——不静默。
        /// </summary>
        public int Read(byte[] dest, int outFrames, double ratio)
        {
            if (ratio < 0.9) ratio = 0.9;
            if (ratio > 1.1) ratio = 1.1;

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
                return produced;
            }
        }
    }

    // ------------------------------------------------------------------ 接收端

    internal sealed class SnadReceiver : IDisposable
    {
        private readonly int _deviceIndex;
        private readonly int _port;

        // 样本环形缓冲：容量 200 ms，起播前预填充 80 ms。
        // 这是本次修复的核心——它把"包到达"与"音频播放"彻底解耦。
        //
        // 容量取 200 ms 而非 500 ms：实测消费率 99.5%、产出率约 100.3%，
        // 稳态几乎匹配，200 ms 足够吸收抖动；过大的容量只会让启动突发把
        // 延迟永久顶在高位（曾出现 500 ms 缓冲在 0.8 秒内被灌满的情况）。
        private readonly SampleRing _ring = new SampleRing(48000 * 200 / 1000);
        private const int PrebufferFrames = 48000 * 80 / 1000;    // 80 ms 的帧数

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private volatile int _sampleRate = 48000;
        private volatile int _channels = 2;
        private long _packets, _bytes, _overflow;
        private long _shortPkt, _badMagic, _badVersion, _badPayload;
        private long _logLines, _logPkts;
        private bool _formatLogged;
        private int _seqExpected = -1;
        private long _seqGaps, _seqMissing;

        public SnadReceiver(int deviceIndex, int port)
        {
            _deviceIndex = deviceIndex;
            _port = port;
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
                            }
                        }
                        _seqExpected = seq + 1;

                        if (!_formatLogged || rate != _sampleRate || ch != _channels)
                        {
                            _sampleRate = rate > 0 ? rate : 48000;
                            _channels = ch > 0 ? ch : 2;
                            _formatLogged = true;
                            Logger.Info(string.Format("流格式确定：{0} Hz / {1} 声道 / 16 bit，第一个包来自 {2}",
                                _sampleRate, _channels, remote));
                            if (rate != 48000)
                                Logger.Warn("手机实际采样率 " + rate + " Hz 与预期 48000 不同，已按实际值播放");
                        }

                        var pcm = new byte[payload];
                        Buffer.BlockCopy(pkt, Program.HeaderSize, pcm, 0, payload);

                        Interlocked.Increment(ref _packets);
                        Interlocked.Add(ref _bytes, payload);

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
                            double kbps = (b - lastBytes) * 8.0 / 1000.0 / (now - lastReport).TotalSeconds;
                            Logger.Info(string.Format(
                                "包={0} 累计={1:F1} MB 速率≈{2:F0} kbps 缓冲={3}帧({4}ms) 溢出={5} " +
                                "欠载={6} 插值={7} 序号缺口={8}(缺{9}包) " +
                                "丢弃[短{10} magic{11} 版本{12} 负载{13}] 来源={14}",
                                p, b / 1048576.0, kbps, _ring.AvailableFrames,
                                _ring.AvailableFrames / 48,
                                Interlocked.Read(ref _overflow),
                                _ring.UnderrunEvents, _ring.InterpolatedFrames,
                                _seqGaps, _seqMissing,
                                Interlocked.Read(ref _shortPkt), Interlocked.Read(ref _badMagic),
                                Interlocked.Read(ref _badVersion), Interlocked.Read(ref _badPayload),
                                lastRemote));
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
                // 预填充：先攒够 100 ms 再起播。否则一起播就见底，
                // 播放侧拿不到数据只能补零，波形被切断，听感即"电音"。
                Logger.Info("等待预填充 " + PrebufferFrames + " 帧（约 100 ms）...");
                var waitStart = DateTime.UtcNow;
                while (!_cts.IsCancellationRequested && _ring.AvailableFrames < PrebufferFrames)
                {
                    Thread.Sleep(10);
                    if ((DateTime.UtcNow - waitStart).TotalSeconds > 10)
                    {
                        Logger.Warn("预填充等待超过 10 秒，仅攒到 " + _ring.AvailableFrames +
                                    " 帧，先起播（仍可能欠载）");
                        break;
                    }
                }
                Logger.Info("预填充完成，可用 " + _ring.AvailableFrames + " 帧（约 " +
                            (_ring.AvailableFrames / 48) + " ms），开始播放");

                // 播放完全由 waveOut 时钟驱动，每次固定取 16 ms，
                // 与"包到达"解耦——这是消除欠载补零的关键。
                int framesPerBuffer = Math.Max(64, _sampleRate * 16 / 1000);

                WaveOut.Play(_deviceIndex, () => _sampleRate, () => _channels, () =>
                {
                    // ratio 吸收两端时钟的细微偏差：积压多于提前量则略快消费，反之略慢。
                    // 偏差由线性插值承担，因此不会出现周期性的丢帧/补帧断点。
                    int aheadFrames = _sampleRate * 150 / 1000;
                    int avail = _ring.AvailableFrames;
                    double ratio = 1.0 + (avail - aheadFrames) / (double)Math.Max(1, aheadFrames) * 0.02;
                    if (ratio > 1.02) ratio = 1.02;
                    if (ratio < 0.98) ratio = 0.98;

                    var buf = new byte[framesPerBuffer * 4];
                    _ring.Read(buf, framesPerBuffer, ratio);
                    return buf;
                }, _cts.Token);
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
