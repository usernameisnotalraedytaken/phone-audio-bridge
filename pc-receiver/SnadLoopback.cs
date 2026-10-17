// 回环测量：用 WASAPI loopback 录下"实际从设备放出来的声音"，
// 与"我们交出去的样本"对齐，得到**真实**的输出链路延迟。
//
// 为什么必须做这个：
//   此前三档位的 386 / 421 / 1052 ms 全是公式推的，
//   其中 pool（WaveOutBuffers × BlockMs）是最大一项，却从未被验证过。
//   计划 §3.2 要求"回环测出真实端到端延迟，与估计值对比"；
//   没有这一步，后面 ③ 的缓冲策略就没有可比较的基准。
//
// 方法（靠内容对齐，不靠墙钟）：
//   1. 先在目标设备上开 loopback 采集，持续记录样本与累计帧号；
//   2. 用**生产代码同一条路径**送出测试信号（waveOut 走 WaveOut.Play，
//      WASAPI 走 WasapiSink），信号是一段伪随机噪声串，特征唯一；
//   3. 在采集到的波形里找这段串的位置；
//   4. 延迟 = (采集到它的位置 − 交出它时的采集帧号) / 采样率。
//
//   这样得到的延迟包含：输出缓冲池 + 设备缓冲 + 系统混音 +
//   采集侧自身的缓冲。它不含 DAC 之后到实际出声的部分——那需要麦克风。
//
// 局限（必须写在输出里，不能让人误读成"总延迟"）：
//   - 独占模式无法用 loopback 采集（绕过了混音器），需要物理回环；
//   - 本方法含采集侧缓冲，属"偏高"的估计，偏多少无法分离。
//
// 用法：
//   SnadLoopback.exe [选项]
//     backend <waveout|wasapi_shared>   默认 waveout
//     device <索引>                     默认 0（设备列表见 SnadAudioProbe）
//     block <ms>                        每次提交的块时长，默认 16
//     buffers <个数>                    waveOut 缓冲池个数，默认 16
//     repeat <次数>                     重复测量次数，取中位数，默认 5

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Snad
{
    // ---- loopback 采集所需的 COM 接口（IAudioCaptureClient）----

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames,
                                    out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }

    /// <summary>
    /// 在某个输出设备上做 loopback 采集。
    ///
    /// 注意：采集的是"该设备正在播放的混音"，因此独占模式下拿不到——
    /// 独占绕过了混音器，这也是本工具只测共享路径的原因。
    /// </summary>
    internal sealed class LoopbackCapture : IDisposable
    {
        private const int CLSCTX_ALL = 23;
        private const int eRender = 0;
        private const int DEVICE_STATE_ACTIVE = 1;
        private const int SHARED = 0;
        private const int LOOPBACK = 0x00020000;

        private static readonly Guid IID_IAudioClient =
            new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient =
            new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private IMMDevice _device;
        private IAudioClientProbe _client;
        private IAudioCaptureClient _capture;
        private int _channels;
        private int _bytesPerSample;
        private SampleFormat _sampleFormat;

        private Thread _thread;
        private volatile bool _running;
        private readonly List<float> _samples = new List<float>();   // 单声道混下来的样本
        private readonly object _gate = new object();
        private long _frames;                                        // 累计采集帧数

        /// <summary>采集到的单声道样本（调用方应保证已停止）。</summary>
        public float[] Samples { get { lock (_gate) return _samples.ToArray(); } }

        /// <summary>已采集的总帧数，用于把"交出样本的时刻"换算到采集时间轴上。</summary>
        public long Frames { get { lock (_gate) return _frames; } }

        public int SampleRate { get; private set; }

        /// <summary>打开指定索引的输出设备的 loopback。</summary>
        public void Open(int deviceIndex)
        {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());

            IMMDeviceCollection coll;
            int hr = enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out coll);
            if (hr != 0) throw new InvalidOperationException("EnumAudioEndpoints 失败 hr=0x" + hr.ToString("X8"));

            int count;
            coll.GetCount(out count);
            if (deviceIndex < 0 || deviceIndex >= count)
                throw new InvalidOperationException("设备索引越界：" + deviceIndex + "（共 " + count + " 台）");

            hr = coll.Item(deviceIndex, out _device);
            if (hr != 0) throw new InvalidOperationException("打开设备失败 hr=0x" + hr.ToString("X8"));

            var iid = IID_IAudioClient;
            object obj;
            hr = _device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
            if (hr != 0) throw new InvalidOperationException(
                "IAudioClient 激活失败 hr=0x" + hr.ToString("X8") + "（" + WasapiSink.HresultName(hr) + "）");
            _client = (IAudioClientProbe)obj;

            IntPtr mix;
            hr = _client.GetMixFormat(out mix);
            if (hr != 0) throw new InvalidOperationException("GetMixFormat 失败 hr=0x" + hr.ToString("X8"));

            // 混音格式的采样率/声道数要从 WAVEFORMATEX 前 12 字节读；
            // 位深不能只看容器宽度，必须走 FormatFromMix 读 SubFormat
            var wf = (AudioCodec.WAVEFORMATEX)Marshal.PtrToStructure(mix, typeof(AudioCodec.WAVEFORMATEX));
            SampleRate = (int)wf.nSamplesPerSec;
            _channels = wf.nChannels;

            var sampleFormat = AudioCodec.FormatFromMix(mix);
            _sampleFormat = sampleFormat;
            _bytesPerSample = AudioCodec.BytesPerSample(sampleFormat);

            // 共享模式 + LOOPBACK；不传 EVENTCALLBACK，改为轮询。
            // 缓冲时长给 1 秒而不是 0：实测传 0 时轮询侧一个包都拿不到
            // （GetNextPacketSize 恒为 0），共享模式下这个值是允许指定的。
            hr = _client.Initialize(SHARED, LOOPBACK, 10_000_000, 0, mix, IntPtr.Zero);
            Marshal.FreeCoTaskMem(mix);
            if (hr != 0) throw new InvalidOperationException(
                "Initialize(loopback) 失败 hr=0x" + hr.ToString("X8") + "（" + WasapiSink.HresultName(hr) + "）");

            var capIid = IID_IAudioCaptureClient;
            object capObj;
            hr = _client.GetService(ref capIid, out capObj);
            if (hr != 0) throw new InvalidOperationException("GetService(IAudioCaptureClient) 失败 hr=0x" + hr.ToString("X8"));
            _capture = (IAudioCaptureClient)capObj;
        }

        public void Start()
        {
            int hr = _client.Start();
            if (hr != 0) throw new InvalidOperationException("采集 Start 失败 hr=0x" + hr.ToString("X8"));

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "loopback-capture" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null) { _thread.Join(800); _thread = null; }
            try { _client.Stop(); } catch { }
        }

        private void Loop()
        {
            byte[] raw = new byte[65536];
            while (_running)
            {
                int pending;
                if (_capture.GetNextPacketSize(out pending) != 0) { Thread.Sleep(3); continue; }
                if (pending <= 0) { Thread.Sleep(3); continue; }

                IntPtr data;
                int frames, flags;
                long devPos, qpcPos;
                int hr = _capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos);
                if (hr != 0) { Thread.Sleep(3); continue; }
                if (frames <= 0) { _capture.ReleaseBuffer(0); continue; }

                int bytes = frames * _channels * _bytesPerSample;
                if (raw.Length < bytes) raw = new byte[bytes];
                Marshal.Copy(data, raw, 0, bytes);
                _capture.ReleaseBuffer(frames);

                // 按设备实际的混音格式解码，而不是一律当 float32——
                // 混音格式是 pcm16 的设备上那样读会把波形读成噪声
                lock (_gate)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float s = 0;
                        for (int c = 0; c < _channels; c++)
                            s += AudioCodec.Decode(raw, (i * _channels + c) * _bytesPerSample, _sampleFormat);
                        _samples.Add(s / _channels);
                    }
                    _frames += frames;
                }
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            if (_capture != null) { Marshal.ReleaseComObject(_capture); _capture = null; }
            if (_client != null) { Marshal.ReleaseComObject(_client); _client = null; }
            if (_device != null) { Marshal.ReleaseComObject(_device); _device = null; }
        }
    }

    internal static class LoopbackTest
    {
        private const int BurstFrames = 4096;
        private const float BurstAmp = 0.5f;

        private static int Main(string[] args)
        {
            string backend = "waveout";
            int deviceIndex = 0, blockMs = 16, buffers = 16, repeat = 5, prebufferMs = 60;

            // 端到端模式：e2e <档位名>。非空时走"接收器 → 环形缓冲 → 输出"的完整链路。
            string e2eProfile = null;
            int e2ePort = 6981;
            double primeSec = 3.0;
            double watchSec = 0;      // >0 时只监听并报峰值，不播放、不测量

            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                string k = args[i].ToLowerInvariant();
                string v = args[i + 1];
                switch (k)
                {
                    case "backend": backend = v.ToLowerInvariant(); break;
                    case "device": deviceIndex = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "block": blockMs = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "buffers": buffers = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "repeat": repeat = Math.Max(1, int.Parse(v, CultureInfo.InvariantCulture)); break;
                    case "prebuffer": prebufferMs = Math.Max(0, int.Parse(v, CultureInfo.InvariantCulture)); break;
                    case "e2e": e2eProfile = v; break;
                    case "watch": watchSec = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "port": e2ePort = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "prime": primeSec = double.Parse(v, CultureInfo.InvariantCulture); break;
                }
            }

            Console.WriteLine("SNAD 回环测量 —— 测的是输出链路的真实延迟");
            Console.WriteLine("  后端    " + backend);
            Console.WriteLine("  设备    [" + deviceIndex + "]");
            if (backend == "waveout")
                Console.WriteLine("  块/池   " + blockMs + " ms × " + buffers);
            Console.WriteLine();

            var results = new List<double>();

            using (var cap = new LoopbackCapture())
            {
                try
                {
                    cap.Open(deviceIndex);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("打不开 loopback：" + ex.Message);
                    Console.WriteLine();
                    Console.WriteLine("独占模式下无法用 loopback 采集（绕过了系统混音器），");
                    Console.WriteLine("这种情况只能用物理回环（线路输出接线路输入）测。");
                    return 2;
                }

                // 必须真的启动采集线程。漏掉这一步时 Frames 恒为 0，
                // 表现为"每次都说采集样本不足"，很容易误判成设备不支持 loopback。
                cap.Start();
                Thread.Sleep(150);      // 让采集先跑起来，避免把起播前的静音算进去

                Console.WriteLine("采集设备就绪：" + cap.SampleRate + " Hz（已启动，累计 " +
                                  cap.Frames + " 帧）");
                Console.WriteLine();

                if (watchSec > 0)
                {
                    // 只监听：报出窗口内峰值与过阈样本占比。
                    // 用来分辨"设备根本没出声"和"出了声但不是我们要的串"——
                    // 这两种成因的处理完全不同。
                    long startFrames = cap.Frames;
                    Thread.Sleep((int)(watchSec * 1000));
                    float[] all = cap.Samples;
                    int from = (int)Math.Max(0, startFrames);
                    double peak = 0;
                    long above = 0;
                    for (int i = from; i < all.Length; i++)
                    {
                        double a = Math.Abs(all[i]);
                        if (a > peak) peak = a;
                        if (a >= 0.01) above++;
                    }
                    int total = Math.Max(1, all.Length - from);
                    Console.WriteLine("监听 " + watchSec.ToString("F1") + " 秒（" + total + " 帧）：");
                    Console.WriteLine("  峰值        " + peak.ToString("F5"));
                    Console.WriteLine("  ≥0.01 占比  " + (above * 100.0 / total).ToString("F2") + " %");
                    Console.WriteLine("  结论        " + (peak < 0.005 ? "设备基本静音" : "设备有输出"));
                    return 0;
                }

                if (e2eProfile != null)
                {
                    // 端到端：信号经由真实的接收器（环形缓冲 → 输出）再被录回。
                    // 直接写 sink 的测法绕过了抖动缓冲，只覆盖输出段；
                    // 计划 §3.2 要求覆盖"抖动缓冲 + 播放池 + 声卡"，必须走这一条。
                    // 每轮都重启接收器。
                    //
                    // 复用同一个接收器时，轮与轮之间会累积积压：
                    // 发送端的节拍用 Thread.Sleep 实现，平均略慢于实时，
                    // 于是环里的积压逐轮减少，测出来的延迟也跟着逐轮变小
                    // （实测 335 → 201 → 184 ms）。这种数不能比较。
                    // 每轮从同一个初始状态起测，才是可比的。
                    for (int r = 0; r < repeat; r++)
                    {
                        using (var recv = StartReceiver(e2eProfile))
                        {
                            try
                            {
                                double ms = RunEndToEnd(cap, recv, e2ePort, deviceIndex,
                                                        primeSec, r + 1, repeat);
                                if (ms > 0) results.Add(ms);
                            }
                            finally { StopReceiver(recv); }
                        }
                        Thread.Sleep(300);
                    }
                }
                else
                {
                    for (int r = 0; r < repeat; r++)
                    {
                        double ms = RunOnce(cap, backend, deviceIndex, blockMs, buffers, prebufferMs, r + 1, repeat);
                        if (ms > 0) results.Add(ms);
                        Thread.Sleep(220);
                    }
                }
            }

            if (results.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("未测到信号——检查设备是否在放音、音量是否为 0。");
                return 1;
            }

            results.Sort();
            double median = results[results.Count / 2];

            Console.WriteLine();
            Console.WriteLine("=== 结果 ===");
            Console.WriteLine("  样本数    " + results.Count);
            Console.WriteLine("  最小      " + results[0].ToString("F2") + " ms");
            Console.WriteLine("  中位数    " + median.ToString("F2") + " ms");
            Console.WriteLine("  最大      " + results[results.Count - 1].ToString("F2") + " ms");
            Console.WriteLine();
            Console.WriteLine("  该值包含：输出缓冲池 + 设备缓冲 + 系统混音 + 采集侧缓冲。");
            Console.WriteLine("  不包含：DAC 之后到实际出声（需麦克风回环）。");
            return 0;
        }

        /// <summary>
        /// 起一个真实的接收器进程（用指定档位），测完随进程退出一起结束。
        ///
        /// 为什么要起真进程而不是在工具里内嵌接收器：
        ///   要测的是"接收器 + 环形缓冲 + 输出"这条真实链路，
        ///   内嵌一份就等于测一份仿制品。用磁盘上那个二进制，测的才是会被交付的东西。
        /// </summary>
        private static Process StartReceiver(string profile)
        {
            string exe = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(LoopbackTest).Assembly.Location),
                "SnadReceiver.exe");

            if (!System.IO.File.Exists(exe))
                throw new InvalidOperationException("找不到接收器：" + exe +
                    "（端到端模式需要同目录下的 SnadReceiver.exe）");

            var psi = new ProcessStartInfo(exe, "--profile \"" + profile + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
                // 不要重定向 stdout/stderr：接收器日志量大，管道缓冲写满后
                // 它会阻塞在写日志上，表现为"测量工具无输出、也没测到信号"。
                // 实测就踩过这个坑。
            };

            var p = Process.Start(psi);
            // 起播要等它的格式判定与预填充完成；给它一点时间
            Thread.Sleep(2200);
            return p;
        }

        /// <summary>结束子进程。它是本工具起的测量用进程，不是用户的应用。</summary>
        private static void StopReceiver(Process p)
        {
            try
            {
                if (p != null && !p.HasExited) p.Kill();
                if (p != null) p.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// 端到端测一次：先按实时喂若干秒让接收器进入稳态，
        /// 再送一个噪声串，记录"交出该包时采集端已采到第几帧"，
        /// 最后在采集里找到它——差值就是**从进入接收器到出现在声卡输出**的整段延迟。
        /// </summary>
        private static double RunEndToEnd(LoopbackCapture cap, Process recv, int port,
                                          int deviceIndex, double primeSec, int round, int total)
        {
            int rate = cap.SampleRate;
            int channels = 2;
            const int blockMs = 21;                       // 与手机端包长一致
            int framesPerPacket = rate * blockMs / 1000;
            int payloadBytes = framesPerPacket * channels * 2;

            var burst = BuildBurstPcm16(rate, channels);   // 4096 帧的单声道串
            int burstFrames = BurstFrames;

            long handedAt = -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using (var sock = new System.Net.Sockets.UdpClient())
            {
                sock.Client.SendBufferSize = 1 << 20;
                var target = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port);

                int seq = 0;
                long streamFrame = 0;
                double pacePerPacket = blockMs;

                // 稳态预热：让接收器把环形缓冲填到目标水位
                long primePackets = (long)(primeSec * 1000 / pacePerPacket);
                // 串之后再来一秒静音，保证它已经被采到
                long tailPackets = (long)(1000 / pacePerPacket);

                long totalPackets = primePackets + (burstFrames / framesPerPacket + 1) + tailPackets;
                long burstStartPacket = primePackets;

                var pcm = new byte[payloadBytes];
                for (long pk = 0; pk < totalPackets; pk++)
                {
                    while (sw.Elapsed.TotalMilliseconds < pk * pacePerPacket) Thread.Sleep(1);

                    Array.Clear(pcm, 0, pcm.Length);
                    for (int f = 0; f < framesPerPacket; f++)
                    {
                        long sf = streamFrame + f;
                        int b = (int)(sf - burstStartPacket * framesPerPacket);
                        short s = 0;
                        if (b >= 0 && b < burstFrames)
                        {
                            if (b == 0) Volatile.Write(ref handedAt, cap.Frames);
                            s = burst[b * channels];
                        }
                        for (int c = 0; c < channels; c++)
                        {
                            pcm[(f * channels + c) * 2] = (byte)(s & 0xFF);
                            pcm[(f * channels + c) * 2 + 1] = (byte)((s >> 8) & 0xFF);
                        }
                    }

                    var ts = (int)((sw.ElapsedMilliseconds) & 0xFFFFFFFFL);
                    var pkt = BuildPacket(channels, rate, seq, ts, payloadBytes, pcm);
                    try { sock.Send(pkt, pkt.Length, target); } catch { }
                    seq++;
                    streamFrame += framesPerPacket;
                }
            }

            // 等采集端把尾巴收进来
            Thread.Sleep(250);

            if (handedAt < 0)
            {
                Console.WriteLine("  第 " + round + " 次：未记录到交付时刻");
                return -1;
            }

            float[] all = cap.Samples;

            // 只从"交出时刻"往后找。
            //
            // 之前从 handedAt - 250ms 开始搜，结果相关器在静音段锁到窗口起点，
            // 算出 -250ms 这种负数；而信号物理上不可能早于它被交出的时刻。
            // 采集端自身有缓冲，可能让首个样本略晚出现，但绝不会更早。
            int from = (int)Math.Max(0, handedAt);
            if (all.Length <= from + 1000)
            {
                Console.WriteLine("  第 " + round + " 次：采集样本不足（共 " + all.Length +
                                  " 帧，起点 " + from + "）");
                return -1;
            }

            // 先报出窗口内峰值：区分"设备根本没出声"与"出了声但不是我们要的串"。
            // 这两种成因的处理完全不同，不能只回一句"未找到"。
            double peakAmp = 0;
            for (int i = from; i < all.Length; i++)
            {
                double a = Math.Abs(all[i]);
                if (a > peakAmp) peakAmp = a;
            }

            // 先用包络找到"真的有声音"的起点，再在它附近用相关细化。
            // 直接全局相关会在静音段锁到起点，给出 0 或负数的假结果。
            int found = FindBurstEnvelope(all, from, 8, 0.08);
            if (found >= 0)
            {
                var burstF = BuildBurstFloat(rate, channels);
                int coarse = found;
                int fine = FindBurst(all, Math.Max(from, coarse - 400), burstF, rate);
                if (fine >= 0 && Math.Abs(fine - coarse) <= 400) found = fine;
            }
            else
            {
                // 包络没找到时再退回全局相关，但要求相关峰足够显著
                found = FindBurst(all, from, BuildBurstFloat(rate, channels), rate);
            }
            if (found < 0)
            {
                Console.WriteLine("  第 " + round + " 次：采集中未找到信号" +
                                  "（窗口 " + from + ".." + all.Length + "，峰值 " +
                                  peakAmp.ToString("F4") + (peakAmp < 0.005 ? "，几乎是静音" : "") + "）");
                return -1;
            }

            double ms = (found - handedAt) * 1000.0 / rate;
            Console.WriteLine("  第 " + round + "/" + total + " 次：" + ms.ToString("F2") + " ms" +
                              "   (交付帧 " + handedAt + " → 检出帧 " + found + ")" +
                              "   接收器仍运行=" + (!recv.HasExited));
            return ms;
        }

        /// <summary>构造一个 v1 音频包（与手机端一致）。</summary>
        private static byte[] BuildPacket(int channels, int rate, int seq, int timestampMs,
                                          int payloadBytes, byte[] pcm)
        {
            var pkt = new byte[WireProtocol.HeaderSizeV1 + payloadBytes];
            pkt[0] = (byte)'S'; pkt[1] = (byte)'N'; pkt[2] = (byte)'A'; pkt[3] = (byte)'D';
            pkt[4] = WireProtocol.Version1;
            pkt[5] = (byte)channels;
            WireProtocol.PutInt32LE(pkt, 6, rate);
            WireProtocol.PutInt32LE(pkt, 10, seq);
            WireProtocol.PutInt32LE(pkt, 14, timestampMs);
            WireProtocol.PutInt32LE(pkt, 18, payloadBytes);
            Buffer.BlockCopy(pcm, 0, pkt, WireProtocol.HeaderSizeV1, payloadBytes);
            return pkt;
        }

        /// <summary>测一次：送一段噪声串，在采集里找它，算时间差。</summary>
        private static double RunOnce(LoopbackCapture cap, string backend, int deviceIndex,
                                      int blockMs, int buffers, int prebufferMs, int round, int total)
        {
            int rate = cap.SampleRate;
            int channels = 2;

            // 采集用的是 MMDevice 端点编号，waveOut 用的是另一套编号。
            // 两边都用同一个数字 → 可能"送到 A、采 B"，于是永远采不到信号
            // （实测就是这样：采集帧数一直是 0）。因此按设备名把两套编号对齐。
            int wavOutIndex = deviceIndex;
            if (backend == "waveout")
            {
                wavOutIndex = MapToWaveOutIndex(deviceIndex);
                if (wavOutIndex < 0)
                {
                    Console.WriteLine("  无法把 MMDevice[" + deviceIndex + "] 对应到 waveOut 设备，跳过");
                    return -1;
                }
                // 把映射结果打印出来：两套编号不同，若映射错了，
                // 后面看到的就是"送到 A、采 B"却毫无提示
                string mapName = "?";
                try
                {
                    foreach (var d in WaveOut.ListDevices())
                        if (d.Index == wavOutIndex) { mapName = d.Name; break; }
                }
                catch { }
                Console.WriteLine("  waveOut 设备 [" + wavOutIndex + "] " + mapName);
            }

            // 采集时间轴上"交出第一个样本"的位置。用 long 读取，避免与采集线程竞争。
            long handedAt = -1;
            long startFrames = cap.Frames;
            // 喂数据的节拍基准：每次测量独立起算
            var paceClock = System.Diagnostics.Stopwatch.StartNew();

            // 起播时允许先灌入的帧数（对应接收器的 prebuffer）
            int prebufferFrames = rate * prebufferMs / 1000;

            var burst16 = BuildBurstPcm16(rate, channels);
            var burstF = BuildBurstFloat(rate, channels);

            int guardFrames = rate / 2;          // 先静音 0.5s，确保采集已经在跑
            int tailFrames = rate;               // 之后静音 1s，留出捕获余量
            int totalFrames = guardFrames + BurstFrames + tailFrames;

            using (var cts = new CancellationTokenSource())
            {
                Thread worker = null;

                if (backend == "waveout")
                {                    var profile = new Profile
                    {
                        Backend = "waveout",
                        BlockMs = blockMs,
                        WaveOutBuffers = buffers,
                        PollMs = 1,
                        LeadMs = 0
                    };
                    int pos = 0;                     // 已交付的帧
                    int blockFrames = Math.Max(1, rate * blockMs / 1000);

                    worker = new Thread(() =>
                    {
                        try
                        {
                            WaveOut.Play(wavOutIndex, () => rate, () => channels, () =>
                            {
                                if (pos >= totalFrames) { Thread.Sleep(2); return null; }
                                if (pos == 0) Volatile.Write(ref handedAt, cap.Frames);

                                // 按实时节奏喂，但先放行 prebuffer 那么多帧：
                                // 真实接收器起播时环里已有预填充，池会被先灌一批，
                                // 那批数据就是稳态延迟的一部分。若从头就按实时喂，
                                // 池永远来不及预填充，测出的值会偏小一个池深。
                                PaceTo(pos - prebufferFrames, rate, paceClock);

                                int n = Math.Min(blockFrames, totalFrames - pos);
                                int off = pos;
                                pos += n;

                                var buf = new byte[n * channels * 2];
                                for (int f = 0; f < n; f++)
                                {
                                    int g = off + f - guardFrames;      // 相对噪声串起点的帧号
                                    // burst16 本来就是交错的 short[]，直接取，
                                    // 不要再拆成字节再拼回去（那样符号扩展会出错）
                                    short s = 0;
                                    if (g >= 0 && g < BurstFrames)
                                        s = burst16[g * channels];
                                    for (int c = 0; c < channels; c++)
                                    {
                                        buf[(f * channels + c) * 2] = (byte)(s & 0xFF);
                                        buf[(f * channels + c) * 2 + 1] = (byte)((s >> 8) & 0xFF);
                                    }
                                }
                                return buf;
                            }, cts.Token, profile);
                        }
                        catch (Exception ex) { Console.WriteLine("  [worker] " + ex.Message); }
                    }) { IsBackground = true };
                    worker.Start();
                }
                else
                {
                    // WASAPI 共享：用手上的 sink，和接收器走同一条代码
                    var dev = DeviceEnumerator.Probe()[deviceIndex];
                    worker = new Thread(() =>
                    {
                        try
                        {
                            using (var sink = new WasapiSink())
                            {
                                sink.Open(dev, dev.MixSampleFormat, rate, channels, 0, false);
                                sink.Start();

                                var blockF = Math.Max(1, rate * blockMs / 1000);
                                int pos = 0;
                                while (pos < totalFrames)
                                {
                                    if (pos == 0) Volatile.Write(ref handedAt, cap.Frames);

                                    // 与 waveOut 路径一致：先放行预填充，之后按实时喂
                                    PaceTo(pos - prebufferFrames, rate, paceClock);

                                    int n = Math.Min(blockF, totalFrames - pos);
                                    var buf = new byte[n * channels * 4];
                                    for (int f = 0; f < n; f++)
                                    {
                                        int g = pos + f - guardFrames;
                                        float v = 0;
                                        if (g >= 0 && g < BurstFrames) v = burstF[(g * 2)];
                                        int bo = (f * channels) * 4;
                                        Buffer.BlockCopy(BitConverter.GetBytes(v), 0, buf, bo, 4);
                                        Buffer.BlockCopy(BitConverter.GetBytes(v), 0, buf, bo + 4, 4);
                                    }
                                    int wrote = 0;
                                    while (wrote < buf.Length) wrote += sink.Write(buf, wrote, buf.Length - wrote);
                                    pos += n;
                                }
                                sink.Stop();
                            }
                        }
                        catch (Exception ex) { Console.WriteLine("  [worker] " + ex.Message); }
                    }) { IsBackground = true };
                    worker.Start();
                }

                // 等到信号送完 + 尾巴采完
                Thread.Sleep((int)((guardFrames + BurstFrames + tailFrames) * 1000L / rate) + 250);
                cts.Cancel();
                if (worker != null) worker.Join(1500);
            }

            if (handedAt < 0)
            {
                Console.WriteLine("  第 " + round + " 次：未记录到交付时刻");
                return -1;
            }

            // 等采集线程把尾巴收进来
            Thread.Sleep(120);

            float[] all = cap.Samples;
            int from = (int)Math.Max(0, startFrames);
            if (all.Length <= from + 1000)
            {
                // 报出实际抓到的帧数，而不是只说"不足"：
                // 0 帧说明采集端没在出数据（多半是采到了另一台设备），
                // 有几千帧说明只是被切掉的位置不对——两种成因完全不同。
                Console.WriteLine("  第 " + round + " 次：采集样本不足（共 " + all.Length +
                                  " 帧，起点 " + from + "）");
                return -1;
            }

            int found = FindBurst(all, from, burstF, rate);
            if (found < 0)
            {
                Console.WriteLine("  第 " + round + " 次：采集中未找到信号");
                return -1;
            }

            // 采集到的位置相对"交出时刻"的差值，就是整条输出链路的延迟。
            // 必须减去静音前缀：噪声串位于流的 guardFrames 处，
            // 不减去的话量到的是"前缀 + 延迟"，看起来大得离谱。
            double ms = (found - handedAt - guardFrames) * 1000.0 / rate;
            Console.WriteLine("  第 " + round + "/" + total + " 次：" + ms.ToString("F2") + " ms" +
                              "   (交付帧 " + handedAt + " → 检出帧 " + found +
                              "，已减去前缀 " + guardFrames + " 帧)");
            return ms;
        }

        /// <summary>
        /// 按实时节拍等待，使第 <paramref name="framePos"/> 帧恰好在
        /// "流开始后 framePos/rate 秒"交出。
        ///
        /// 为什么必须控速：真实接收器由网络按实时喂数据，缓冲池会先填满、
        /// 之后稳定停在某个深度，那个深度就是稳态延迟的一部分。
        /// 如果测试源"能多快给多快"，池被瞬间灌满后一直有货，
        /// 测出来只剩设备延迟——池深那一段被悄悄漏掉。
        /// </summary>
        private static void PaceTo(int framePos, int rate, System.Diagnostics.Stopwatch clock)
        {
            long dueMs = framePos * 1000L / rate;
            while (clock.ElapsedMilliseconds < dueMs)
            {
                long remain = dueMs - clock.ElapsedMilliseconds;
                if (remain > 2) Thread.Sleep(1);
                else Thread.SpinWait(200);
            }
        }

        /// <summary>
        /// 把 MMDevice 端点编号换算成 waveOut 设备编号。
        ///
        /// 两套编号来自不同 API（EnumAudioEndpoints 与 waveOutGetNumDevs），
        /// 顺序与成员都可能不同，唯一可靠的关联是设备名。
        /// 找不到时返回 -1，由调用方明确报错——不要退回某个默认编号，
        /// 那会变成"测了另一台设备却当成这台"。
        /// </summary>
        private static int MapToWaveOutIndex(int mmIndex)
        {
            string want;
            try
            {
                var caps = DeviceEnumerator.Probe();
                if (mmIndex < 0 || mmIndex >= caps.Count) return -1;
                want = caps[mmIndex].Name;
            }
            catch { return -1; }

            try
            {
                var devices = WaveOut.ListDevices();
                foreach (var d in devices)
                    if (string.Equals(d.Name, want, StringComparison.Ordinal)) return d.Index;
                // 名称常有后缀差异，退一步做包含匹配
                foreach (var d in devices)
                    if (d.Name != null && want != null &&
                        (d.Name.Contains(want) || want.Contains(d.Name))) return d.Index;
            }
            catch { }
            return -1;
        }

        /// <summary>伪随机噪声串：自相关接近冲激，因此在混音里也能唯一定位。</summary>
        private static short[] BuildBurstPcm16(int rate, int channels)
        {
            var rnd = new Random(20261017);
            var mono = new float[BurstFrames];
            for (int i = 0; i < BurstFrames; i++)
                mono[i] = (float)((rnd.NextDouble() * 2 - 1) * BurstAmp);

            var outp = new short[BurstFrames * channels];
            for (int i = 0; i < BurstFrames; i++)
                for (int c = 0; c < channels; c++)
                    outp[i * channels + c] = (short)Math.Round(mono[i] * 32767);
            _ = rate;
            return outp;
        }

        private static float[] BuildBurstFloat(int rate, int channels)
        {
            var rnd = new Random(20261017);      // 同一个种子 → 与 PCM16 版同序列
            var outp = new float[BurstFrames * channels];
            for (int i = 0; i < BurstFrames; i++)
            {
                float v = (float)((rnd.NextDouble() * 2 - 1) * BurstAmp);
                for (int c = 0; c < channels; c++) outp[i * channels + c] = v;
            }
            _ = rate;
            return outp;
        }

        /// <summary>
        /// 在采集里找噪声串的起点。
        ///
        /// 先按 8 帧步长粗搜、再在峰值附近细搜：直接全量相关是
        /// 96k × 4096 的量级，慢且没必要——噪声串的能量集中，
        /// 粗搜不会错过几帧的偏移。
        /// </summary>
        private static int FindBurst(float[] capture, int from, float[] burst, int rate)
        {
            int ch = 2;
            int len = burst.Length / ch;
            var tmpl = new float[len];
            for (int i = 0; i < len; i++) tmpl[i] = burst[i * ch];

            int hi = capture.Length - len;
            if (hi <= from) return -1;

            // 先看窗口里到底有没有信号。
            // 全是静音时相关系数处处为 0，最佳位置会退化成搜索起点，
            // 于是算出 0.00 ms 甚至负数——那种数是假的，要说"未找到"。
            double peak = 0;
            for (int i = Math.Max(0, from); i < Math.Min(capture.Length, hi + len); i++)
            {
                double a = Math.Abs(capture[i]);
                if (a > peak) peak = a;
            }
            if (peak < 0.005) return -1;      // 明显低于噪声地板，视作没有信号

            double best = double.MinValue;
            int bestAt = -1;

            for (int off = from; off < hi; off += 8)
            {
                double s = 0;
                for (int i = 0; i < len; i += 2) s += capture[off + i] * tmpl[i];
                if (s > best) { best = s; bestAt = off; }
            }
            if (bestAt < 0) return -1;

            // 相关峰必须为正，且要明显高于搜索起点处的值。
            // 否则说明只是噪声里的偶然最大值，宁可报未找到。
            double atStart = 0;
            for (int i = 0; i < len; i += 4) atStart += capture[from + i] * tmpl[i];
            if (best <= 0 || best < atStart * 4) return -1;

            int lo = Math.Max(from, bestAt - 12), hh = Math.Min(hi, bestAt + 12);
            for (int off = lo; off < hh; off++)
            {
                double s = 0;
                for (int i = 0; i < len; i++) s += capture[off + i] * tmpl[i];
                if (s > best) { best = s; bestAt = off; }
            }
            _ = rate;
            return bestAt;
        }

        /// <summary>
        /// 按包络定位噪声串起点：连续若干个样本同时超过阈值，才算"这里真的是信号"。
        ///
        /// 为什么不用全局相关：静音段的相关值恒为 0，最大值会退化成搜索起点，
        /// 于是算出 0.00 或负的假数。先按幅度找到候选区间，
        /// 再在局部做相关细化，才能既稳又准。
        /// </summary>
        private static int FindBurstEnvelope(float[] capture, int from, int minAmpSamples, double threshold)
        {
            int n = 0;
            for (int i = from; i < capture.Length; i++)
            {
                if (Math.Abs(capture[i]) >= threshold)
                {
                    if (++n >= minAmpSamples) return i - n + 1;
                }
                else n = 0;
            }
            return -1;
        }
    }
}
