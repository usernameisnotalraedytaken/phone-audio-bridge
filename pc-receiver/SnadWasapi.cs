// WASAPI 输出后端（共享模式与独占模式）。
//
// 为什么值得做：
//   waveOut 的周期约 10ms，必须靠深缓冲池（16 个 16ms 块 = 256ms 在飞）
//   换稳定。WASAPI 的事件/轮询驱动可以把周期压到毫秒级，在飞量降到十几毫秒。
//
// 两种模式的**机制不同**，这是实测得出的，不是设计选择：
//
//   共享模式  经过系统混音器。用 AUDCLNT_STREAMFLAGS_EVENTCALLBACK +
//             SetEventHandle，由系统在需要数据时唤醒渲染线程。
//             格式与采样率**必须**跟随设备混音格式。
//
//   独占模式  绕开混音器，可压到设备最小周期。
//             但**不接受 EVENTCALLBACK 标志**——实测传了它一律失败：
//                 带标志  -> AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED(0x88890013)
//                            或 AUDCLNT_E_INVALID_STREAM_FLAG(0x88890016)
//                 不带标志 -> 成功
//             因此独占模式改用**轮询**（按 1/4 周期唤醒）。
//             代价是唤醒精度不如事件驱动，但独占的周期本来就很短（本机 3ms），
//             轮询间隔 1ms 以内足够。
//
// 缓冲时长：
//   独占模式下 periodicity 传 0 即可，设备会自行取合适的周期；
//   实测 3ms 周期配 6/12/24ms 缓冲，或周期 0 配 10~200ms 缓冲，都被接受。
//   本实现取"2 倍最小周期"起步，兼顾低延迟与调度余量。
//
// 其他实测约束：
//   - SetEventHandle 必须在 Initialize **之后**调用，否则 NOT_INITIALIZED
//   - 探测/打开失败后必须显式 ReleaseComObject，否则设备被占住，
//     后续一律 DEVICE_IN_USE

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Snad
{
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int numFramesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int numFramesWritten, int flags);
    }

    internal sealed class WasapiSink : IAudioSink
    {
        private const int eRender = 0, CLSCTX_ALL = 23;
        private const int SHARED = 0, EXCLUSIVE = 1;
        private const int FLAG_EVENTCALLBACK = 0x00040000;
        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

        // ------------------------------------------------------------ 状态

        private bool _exclusive;
        private string _name = "wasapi";
        private int _rate = 48000, _channels = 2, _bytesPerSample = 2;
        private SampleFormat _format = SampleFormat.Pcm16;
        private IAudioClientProbe _client;
        private IAudioRenderClient _render;
        private EventWaitHandle _bufferEvent;
        private IntPtr _formatPtr = IntPtr.Zero;
        private int _bufferFrames;
        private int _pollMs = 1;
        private Thread _thread;
        private volatile bool _running;

        private float[] _ring;          // 内部环形缓冲（float 交错）
        private int _ringFrames;
        private int _ringRead, _ringWrite, _ringCount;
        private readonly object _ringGate = new object();

        public string Name { get { return _name; } }
        public double CapacityMs { get { return _rate > 0 ? _ringFrames * 1000.0 / _rate : 0; } }

        /// <summary>
        /// 环形缓冲还能立刻接受多少帧。
        ///
        /// 调用方应当**先看这个再取数据**：先 pull 再阻塞在 Write 里，
        /// 会把上游（接收器环形缓冲）抽干，而这段等待期间到达的新数据
        /// 只能把上游灌满并溢出——实测表现为上游读速率只有实时的 76%、
        /// 大量丢最旧帧，而 sink 自己其实是按 48000/s 正常渲染的。
        /// </summary>
        public int FreeFrames
        {
            get { lock (_ringGate) return Math.Max(0, _ringFrames - _ringCount); }
        }

        /// <summary>当前排队等待播放的毫秒数。延迟估计里 soundcard 那一项取自这里。</summary>
        public double QueuedMs
        {
            get { lock (_ringGate) return _rate > 0 ? _ringCount * 1000.0 / _rate : 0; }
        }

        /// <summary>设备侧缓冲的毫秒数（WASAPI 内部分配的缓冲，与我们自己的环形缓冲无关）。</summary>
        public double DeviceBufferMs { get { return _rate > 0 ? _bufferFrames * 1000.0 / _rate : 0; } }

        /// <summary>唤醒间隔（毫秒）。独占模式为轮询周期，共享模式为事件超时。</summary>
        public int PollMs { get { return _pollMs; } }

        /// <summary>
        /// 系统报告的输出流延迟（毫秒）。
        /// 该值未必被驱动提供，拿不到时如实返回 0 而不是编一个数。
        /// </summary>
        public double StreamLatencyMs
        {
            get
            {
                if (_client == null) return 0;
                long l;
                return _client.GetStreamLatency(out l) == 0 ? l / 10000.0 : 0;
            }
        }

        /// <summary>
        /// 打开设备。
        /// 失败时**不静默回退**：如实抛错并说明原因，
        /// 因为"该设备是否支持所选模式与格式"本身就是要告诉用户的信息。
        /// </summary>
        public void Open(DeviceCapability device, SampleFormat format, int sampleRate, int channels,
                         int periodMs, bool exclusive)
        {
            Open(device, format, sampleRate, channels, periodMs, exclusive, 0, 0);
        }

        /// <summary>
        /// 打开设备。<paramref name="queueMs"/> 是调用方希望队列能容纳的深度
        /// （通常取上游抖动缓冲的目标水位），<paramref name="maxQueueMs"/> 是上限。
        ///
        /// 队列深度本身就是延迟，所以不是越大越好；但它必须容得下上游维持的水位，
        /// 否则上游会被反复抽干——这正是 WASAPI 路径读速率只有 30k/s 的成因。
        /// </summary>
        public void Open(DeviceCapability device, SampleFormat format, int sampleRate, int channels,
                         int periodMs, bool exclusive, int queueMs, int maxQueueMs)
        {
            if (device == null) throw new ArgumentNullException("device");

            _exclusive = exclusive;
            _name = (exclusive ? "wasapi-exclusive" : "wasapi-shared") + " / " + device.Name;

            IMMDevice dev;
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            int hr = string.IsNullOrEmpty(device.Id)
                ? enumerator.GetDefaultAudioEndpoint(eRender, 0, out dev)
                : enumerator.GetDevice(device.Id, out dev);
            if (hr != 0) throw new InvalidOperationException("取得音频设备失败 hr=0x" + hr.ToString("X8"));

            object o;
            Guid iid = IID_IAudioClient;
            hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
            if (hr != 0) throw new InvalidOperationException(
                "激活 IAudioClient 失败 hr=0x" + hr.ToString("X8") + "（" + HresultName(hr) + "）");
            _client = (IAudioClientProbe)o;

            if (exclusive)
            {
                _formatPtr = AudioCodec.BuildFormat(format, channels, sampleRate);
                IntPtr closest;
                hr = _client.IsFormatSupported(EXCLUSIVE, _formatPtr, out closest);
                if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
                if (hr != 0)
                {
                    Marshal.FreeCoTaskMem(_formatPtr); _formatPtr = IntPtr.Zero;
                    throw new InvalidOperationException(
                        "设备不支持独占模式下的 " + AudioCodec.Name(format) + " @ " + sampleRate + "Hz，" +
                        "hr=0x" + hr.ToString("X8") + "（" + HresultName(hr) + "）");
                }
                _rate = sampleRate;
                _channels = channels;
                _format = format;
            }
            else
            {
                hr = _client.GetMixFormat(out _formatPtr);
                if (hr != 0) throw new InvalidOperationException("GetMixFormat 失败 hr=0x" + hr.ToString("X8"));

                var mix = (AudioCodec.WAVEFORMATEX)Marshal.PtrToStructure(
                    _formatPtr, typeof(AudioCodec.WAVEFORMATEX));

                // 共享模式必须跟随设备混音格式，否则 Initialize 失败。
                // 这里如实报错而不是自作主张重采样——重采样引入额外延迟与音质损失，
                // 应由 profile 声明 sample_rate=follow 来处理。
                if (mix.nSamplesPerSec != sampleRate || mix.nChannels != channels)
                {
                    throw new InvalidOperationException(
                        "共享模式要求跟随设备混音格式 " + mix.nSamplesPerSec + "Hz/" + mix.nChannels + "ch，" +
                        "而请求的是 " + sampleRate + "Hz/" + channels + "ch（" + AudioCodec.Describe(_formatPtr) + "）");
                }

                _rate = sampleRate;
                _channels = channels;
                _format = AudioCodec.FormatFromMix(_formatPtr);
            }
            _bytesPerSample = AudioCodec.BytesPerSample(_format);

            long defaultPeriod, minPeriod;
            _client.GetDevicePeriod(out defaultPeriod, out minPeriod);

            long bufferHns;
            long periodicityHns;
            int flags;

            if (exclusive)
            {
                // 不带 EVENTCALLBACK：独占模式不接受它（实测）
                flags = 0;
                periodicityHns = 0;                 // 设备自适应周期
                bufferHns = Math.Max(minPeriod * 2, Math.Max(1, periodMs) * 10000L);
                _pollMs = Math.Max(1, (int)Math.Round(device.MinPeriodMs / 4.0));
            }
            else
            {
                flags = FLAG_EVENTCALLBACK;
                periodicityHns = 0;                 // 共享模式周期由系统定，必须传 0
                bufferHns = Math.Max(defaultPeriod * 2, Math.Max(1, periodMs) * 10000L);
                _pollMs = 10;                       // 仅作事件超时，正常由事件唤醒
            }

            hr = _client.Initialize(exclusive ? EXCLUSIVE : SHARED, flags,
                                    bufferHns, periodicityHns, _formatPtr, IntPtr.Zero);
            if (hr != 0)
            {
                throw new InvalidOperationException(
                    "IAudioClient.Initialize 失败 hr=0x" + hr.ToString("X8") + "（" + HresultName(hr) + "）" +
                    "（" + (exclusive ? "独占" : "共享") + " 缓冲=" + (bufferHns / 10000) + "ms 周期=" +
                    (periodicityHns / 10000) + "ms flags=0x" + flags.ToString("X") + " 格式=" +
                    AudioCodec.Describe(_formatPtr) + "）");
            }

            if (!exclusive)
            {
                // 事件驱动：必须在 Initialize 之后设置，否则 NOT_INITIALIZED（实测）
                _bufferEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                int hrEv = _client.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
                if (hrEv != 0)
                {
                    throw new InvalidOperationException(
                        "SetEventHandle 失败 hr=0x" + hrEv.ToString("X8") + "（" + HresultName(hrEv) + "）");
                }
            }

            hr = _client.GetBufferSize(out _bufferFrames);
            if (hr != 0) throw new InvalidOperationException("GetBufferSize 失败 hr=0x" + hr.ToString("X8"));

            Guid rid = IID_IAudioRenderClient;
            object ro;
            hr = _client.GetService(ref rid, out ro);
            if (hr != 0) throw new InvalidOperationException("取得 IAudioRenderClient 失败 hr=0x" + hr.ToString("X8"));
            _render = (IAudioRenderClient)ro;

            // 内部环形缓冲深度。
            //
            // 关键认识：写入侧永远有数据（网络持续到达），因此这个环形缓冲
            // **会稳定停在满的状态**。而"停在满"意味着每帧音频都要在里面等
            // 整个容量的时间才轮到播放——**它直接等于该后端的稳态延迟**。
            // 所以它不是"越大越安全"，而是"多大就加多少延迟"。
            //
            // 容量由"上游愿意供多少"决定，而不是只看设备缓冲。
            //
            // 这一条是实测改出来的：原先取 max(40ms, 2×设备缓冲)=44ms，
            // 结果上游的 80ms 抖动缓冲被 sink 一次灌满就抽到 16ms，
            // 之后整条链路在"刚好够"的状态下跑，读速率只有 30k/s（应 48k），
            // 设备大量时间在放静音，采集里根本听不到送出的信号。
            //
            // 队列必须容得下上游打算维持的水位（QueueMs 由调用方按档位给出），
            // 否则水位会被 sink 反复抽干。上限仍受 maxQueueMs 约束，
            // 因为队列深度本身也是延迟。
            int wantMs = Math.Max(40, (int)Math.Ceiling(_bufferFrames * 1000.0 / _rate) * 2);
            if (queueMs > 0) wantMs = Math.Max(wantMs, queueMs);
            if (maxQueueMs > 0 && wantMs > maxQueueMs) wantMs = maxQueueMs;

            int ringMs = wantMs;
            _ringFrames = Math.Max(_bufferFrames * 2, _rate * ringMs / 1000);
            _ring = new float[_ringFrames * _channels];
        }

        public void Start()
        {
            if (_client == null) throw new InvalidOperationException("尚未 Open");
            int hr = _client.Start();
            if (hr != 0) throw new InvalidOperationException("IAudioClient.Start 失败 hr=0x" + hr.ToString("X8"));

            _running = true;
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "wasapi-render" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_bufferEvent != null) { try { _bufferEvent.Set(); } catch { } }
            if (_thread != null) { try { _thread.Join(1500); } catch { } _thread = null; }
            if (_client != null) { try { _client.Stop(); } catch { } }
        }

        // ------------------------------------------------------------ 写入

        public int Write(byte[] buffer, int offset, int count)
        {
            return Write(buffer, offset, count, _format);
        }

        /// <summary>
        /// 写入音频，并显式声明**输入数据的位深**。
        ///
        /// 这一条是修 bug 修的：接收器的 pull 交出的是 PCM16（环形缓冲的出口固定是 16bit），
        /// 而共享模式下 sink 的设备格式是 float32。原先 Write 一律按设备格式解码，
        /// 于是把两字节样本当四字节浮点读——设备收到的是稀疏的满幅垃圾。
        /// 实测证据：接上接收器后设备输出峰值 0.99999、但只有 3% 的样本有内容；
        /// 而同一 sink 直连测试（写入方按 float32 供数）一切正常。
        ///
        /// 因此输入格式与设备格式必须分开：输入按 <paramref name="inputFormat"/> 解码，
        /// 存进环形缓冲的中间表示始终是 float，输出时再按设备格式编码。
        /// </summary>
        public int Write(byte[] buffer, int offset, int count, SampleFormat inputFormat)
        {
            if (_ring == null) return 0;
            int inBytesPerSample = AudioCodec.BytesPerSample(inputFormat);
            int frameBytes = inBytesPerSample * _channels;
            int frames = count / frameBytes;
            if (frames <= 0) return 0;

            int taken;
            lock (_ringGate)
            {
                taken = Math.Min(frames, _ringFrames - _ringCount);
                for (int i = 0; i < taken; i++)
                {
                    int src = offset + i * frameBytes;
                    int dst = _ringWrite * _channels;
                    for (int c = 0; c < _channels; c++)
                        _ring[dst + c] = AudioCodec.Decode(buffer, src + c * inBytesPerSample, inputFormat);
                    _ringWrite = (_ringWrite + 1) % _ringFrames;
                }
                _ringCount += taken;
                _writtenFrames += taken;      // 累计写入帧（诊断用，不重置）
            }
            return taken * frameBytes;
        }

        private long _writtenFrames;
        private long _renderedFrames;

        /// <summary>累计写入帧数。诊断用：与渲染侧累计值对比可发现"写进去了但没播出"的差额。</summary>
        public long WrittenFrames { get { return Interlocked.Read(ref _writtenFrames); } }

        /// <summary>累计渲染（真正提交给设备）帧数。</summary>
        public long RenderedFrames { get { return Interlocked.Read(ref _renderedFrames); } }

        // ------------------------------------------------------------ 渲染线程

        private void RenderLoop()
        {
            int frameBytes = _bytesPerSample * _channels;
            var scratch = new byte[Math.Max(1, _bufferFrames) * frameBytes];

            // 诊断计数：接进接收器后出现"队列一直贴上限"的异常，
            // 需要分清是渲染线程没取数据，还是取到了写不进去。
            long iters = 0, wroteFrames = 0, zeroAvail = 0, zeroRing = 0;
            var lastStat = DateTime.UtcNow;

            while (_running)
            {
                // 共享模式等事件（超时兜底）；独占模式直接按短间隔轮询
                if (_exclusive) Thread.Sleep(_pollMs);
                else _bufferEvent.WaitOne(_pollMs);

                if (!_running) break;
                iters++;

                int padding;
                if (_client.GetCurrentPadding(out padding) != 0) continue;

                int avail = _bufferFrames - padding;
                if (avail <= 0) { zeroAvail++; continue; }

                int frames;
                lock (_ringGate) frames = Math.Min(avail, _ringCount);
                if (frames <= 0) { zeroRing++; continue; }

                IntPtr data;
                if (_render.GetBuffer(frames, out data) != 0) continue;

                int bytes = frames * frameBytes;
                lock (_ringGate)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        int src = _ringRead * _channels;
                        int dst = i * frameBytes;
                        for (int c = 0; c < _channels; c++)
                            AudioCodec.Encode(scratch, dst + c * _bytesPerSample, _ring[src + c], _format);
                        _ringRead = (_ringRead + 1) % _ringFrames;
                    }
                    _ringCount -= frames;
                }
                Marshal.Copy(scratch, 0, data, bytes);
                _render.ReleaseBuffer(frames, 0);
                wroteFrames += frames;
                Interlocked.Add(ref _renderedFrames, frames);

                var now = DateTime.UtcNow;
                if ((now - lastStat).TotalSeconds >= 10)
                {
                    System.Diagnostics.Debug.WriteLine(string.Format(
                        "[渲染] 轮询 {0} 次，取出 {1} 帧（{2:F0} ms），缓冲满 {3}，环形空 {4}，设备缓冲 {5} 帧",
                        iters, wroteFrames, wroteFrames * 1000.0 / _rate, zeroAvail, zeroRing, _bufferFrames));
                    if (_verbose != null)
                        _verbose(string.Format(
                            "[渲染] 轮询 {0} 次，取出 {1} 帧（{2:F0} ms），缓冲满 {3} 次，环形空 {4} 次，设备缓冲 {5} 帧",
                            iters, wroteFrames, wroteFrames * 1000.0 / _rate, zeroAvail, zeroRing, _bufferFrames));
                    lastStat = now;
                }
            }

            if (_verbose != null)
                _verbose(string.Format(
                    "[渲染] 结束：轮询 {0} 次，取出 {1} 帧（{2:F0} ms），缓冲满 {3} 次，环形空 {4} 次",
                    iters, wroteFrames, wroteFrames * 1000.0 / _rate, zeroAvail, zeroRing));
        }

        /// <summary>诊断输出回调。设置后渲染线程会周期性报告内部计数。</summary>
        private Action<string> _verbose;

        /// <summary>打开诊断输出。仅在排查问题时使用，正常路径不设。</summary>
        public void SetVerbose(Action<string> sink) { _verbose = sink; }

        public void Dispose()
        {
            Stop();
            if (_formatPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(_formatPtr); _formatPtr = IntPtr.Zero; }
            if (_bufferEvent != null) { _bufferEvent.Dispose(); _bufferEvent = null; }

            // 显式释放 COM 对象：不释放会把设备一直占住，
            // 后续打开一律 DEVICE_IN_USE（实测踩到）
            if (_render != null) { try { Marshal.ReleaseComObject(_render); } catch { } _render = null; }
            if (_client != null) { try { Marshal.ReleaseComObject(_client); } catch { } _client = null; }
        }

        /// <summary>
        /// 常见 HRESULT 的可读名称。
        /// WASAPI 的错误码不查表基本无从判断，而"独占不可用"这类结论
        /// 必须能直接告诉用户原因，不能只丢一个十六进制数。
        /// </summary>
        internal static string HresultName(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x88890001: return "AUDCLNT_E_NOT_INITIALIZED";
                case 0x88890002: return "AUDCLNT_E_ALREADY_INITIALIZED";
                case 0x88890003: return "AUDCLNT_E_WRONG_ENDPOINT_TYPE";
                case 0x88890004: return "AUDCLNT_E_DEVICE_INVALIDATED";
                case 0x88890005: return "AUDCLNT_E_NOT_STOPPED";
                case 0x88890006: return "AUDCLNT_E_BUFFER_TOO_LARGE";
                case 0x88890007: return "AUDCLNT_E_OUT_OF_ORDER";
                case 0x88890008: return "AUDCLNT_E_UNSUPPORTED_FORMAT";
                case 0x88890009: return "AUDCLNT_E_INVALID_SIZE";
                case 0x8889000A: return "AUDCLNT_E_DEVICE_IN_USE";
                case 0x8889000B: return "AUDCLNT_E_BUFFER_OPERATION_PENDING";
                case 0x8889000C: return "AUDCLNT_E_THREAD_NOT_REGISTERED";
                case 0x8889000E: return "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED";
                case 0x8889000F: return "AUDCLNT_E_ENDPOINT_CREATE_FAILED";
                case 0x88890010: return "AUDCLNT_E_SERVICE_NOT_RUNNING";
                case 0x88890011: return "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED";
                case 0x88890012: return "AUDCLNT_E_EXCLUSIVE_MODE_ONLY";
                case 0x88890013: return "AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED";
                case 0x88890015: return "AUDCLNT_E_INVALID_DEVICE_PERIOD";
                case 0x88890016: return "AUDCLNT_E_INVALID_STREAM_FLAG";
                case 0x88890017: return "AUDCLNT_E_EVENTHANDLE_NOT_SET";
                case 0x88890018: return "AUDCLNT_E_INCORRECT_BUFFER_SIZE";
                case 0x88890019: return "AUDCLNT_E_BUFFER_SIZE_ERROR";
                case 0x88890020: return "AUDCLNT_E_CPUUSAGE_EXCEEDED";
                case 0x80070005: return "E_ACCESSDENIED";
                case 0x8007000E: return "E_OUTOFMEMORY";
                case 0x80070057: return "E_INVALIDARG";
                case 0x80004005: return "E_FAIL";
                default: return "未知";
            }
        }
    }
}
