// WASAPI 设备能力探测。
//
// 这是 v3 计划里"能力探测"的第一手数据来源：编辑器只允许用户选择
// 这里探测出来的可用组合，因此必须先把它做成可信的工具。
//
// 探测内容（对每个活动输出设备）：
//   - 设备 ID 与 friendly name
//   - 共享模式的混音格式（GetMixFormat）
//   - 设备周期范围（GetDevicePeriod：默认周期 / 最小周期）
//   - 独占模式下是否支持 pcm16 / pcm24 / float32（IsFormatSupported）
//   - 共享模式下的建议格式是否被接受
//
// 用法：SnadAudioProbe.exe [详细]
//   无参数  只列设备与关键结论
//   详细    打印每个设备的完整探测过程

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Snad
{
    // ---------------------------------------------------------------- COM 声明

    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ComImport]
    internal class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IPropertyStore props);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        int GetCount(out int count);
        int GetAt(int index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        int Commit();
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        // 全部标 [PreserveSig]：否则失败时运行时会抛"未实现"之类的
        // COMException，掩盖真实 HRESULT，无从判断是哪个参数不合法。
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity,
                                     IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    // ---------------------------------------------------------------- 探测

    internal static class AudioProbe
    {
        private const int eRender = 0, eCapture = 1;
        private const int DEVICE_STATE_ACTIVE = 1;
        private const int CLSCTX_ALL = 23;
        private const int AUDCLNT_SHAREMODE_SHARED = 0, AUDCLNT_SHAREMODE_EXCLUSIVE = 1;
        private const int WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        // PKEY_Device_FriendlyName
        private static readonly PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        private static int Main(string[] args)
        {
            bool verbose = args.Length > 0;
            try
            {
                var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                IMMDeviceCollection coll;
                int hr = enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out coll);
                if (hr != 0) { Console.WriteLine("EnumAudioEndpoints 失败 hr=0x" + hr.ToString("X8")); return 2; }

                int count;
                coll.GetCount(out count);
                Console.WriteLine("活动输出设备：" + count);
                Console.WriteLine();

                var rows = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev;
                    if (coll.Item(i, out dev) != 0) continue;
                    rows.Add(Describe(dev, i, verbose));
                    Console.WriteLine();
                }

                Console.WriteLine("=== 汇总 ===");
                foreach (var r in rows) Console.WriteLine("  " + r);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("探测异常：" + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static string Describe(IMMDevice dev, int index, bool verbose)
        {
            string id = "";
            dev.GetId(out id);
            string name = FriendlyName(dev);

            Console.WriteLine("[" + index + "] " + name);
            Console.WriteLine("    id: " + id);

            object o;
            // 静态只读字段不能按 ref 传递，因此先拷到局部变量
            Guid iidClient = IID_IAudioClient;
            if (dev.Activate(ref iidClient, CLSCTX_ALL, IntPtr.Zero, out o) != 0)
            {
                Console.WriteLine("    激活 IAudioClient 失败（设备可能被独占占用）");
                return name + "  ->  无法激活";
            }
            var ac = (IAudioClient)o;

            long defPeriod = 0, minPeriod = 0;
            ac.GetDevicePeriod(out defPeriod, out minPeriod);
            Console.WriteLine("    默认周期 " + (defPeriod / 10000.0).ToString("F2") + " ms" +
                              "   最小周期 " + (minPeriod / 10000.0).ToString("F2") + " ms");

            IntPtr mixPtr;
            if (ac.GetMixFormat(out mixPtr) == 0 && mixPtr != IntPtr.Zero)
            {
                var mf = (WAVEFORMATEX)Marshal.PtrToStructure(mixPtr, typeof(WAVEFORMATEX));
                Console.WriteLine("    混音格式 " + DescribeFormat(mf));

                // 共享模式下混音格式必然被接受，这里只作为对照
                IntPtr closest;
                int hrShared = ac.IsFormatSupported(AUDCLNT_SHAREMODE_SHARED, mixPtr, out closest);
                Console.WriteLine("    共享模式接受混音格式：" + (hrShared == 0 ? "是" : "否 hr=0x" + hrShared.ToString("X8")));
                if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
                Marshal.FreeCoTaskMem(mixPtr);

                uint rate = mf.nSamplesPerSec;
                ushort ch = mf.nChannels;

                // 独占模式：探测三种位深
                Console.WriteLine("    独占模式格式支持：");
                var ok = new List<string>();
                foreach (var probe in new[]
                {
                    new { Name = "pcm16", Tag = (ushort)WAVE_FORMAT_PCM, Bits = (ushort)16 },
                    new { Name = "pcm24", Tag = (ushort)WAVE_FORMAT_PCM, Bits = (ushort)24 },
                    new { Name = "pcm32", Tag = (ushort)WAVE_FORMAT_PCM, Bits = (ushort)32 },
                    new { Name = "float32", Tag = (ushort)WAVE_FORMAT_IEEE_FLOAT, Bits = (ushort)32 },
                })
                {
                    IntPtr fmt = BuildFormat(probe.Tag, ch, rate, probe.Bits, false);
                    IntPtr cm;
                    int hr = ac.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, fmt, out cm);
                    if (cm != IntPtr.Zero) Marshal.FreeCoTaskMem(cm);
                    Marshal.FreeCoTaskMem(fmt);
                    bool yes = hr == 0;
                    if (yes) ok.Add(probe.Name);
                    Console.WriteLine("      " + probe.Name.PadRight(8) + (yes ? "支持" : "不支持 hr=0x" + hr.ToString("X8")));
                }

                return name + "  ->  " + (ok.Count > 0 ? "独占可用: " + string.Join("/", ok.ToArray()) : "独占不可用");
            }

            Console.WriteLine("    GetMixFormat 失败");
            return name + "  ->  无法取得混音格式";
        }

        private static IntPtr BuildFormat(ushort tag, ushort channels, uint rate, ushort bits, bool extensible)
        {
            ushort blockAlign = (ushort)(channels * bits / 8);
            uint avgBytes = rate * blockAlign;

            if (!extensible)
            {
                var wf = new WAVEFORMATEX
                {
                    wFormatTag = tag,
                    nChannels = channels,
                    nSamplesPerSec = rate,
                    nAvgBytesPerSec = avgBytes,
                    nBlockAlign = blockAlign,
                    wBitsPerSample = bits,
                    cbSize = 0
                };
                IntPtr p = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WAVEFORMATEX)));
                Marshal.StructureToPtr(wf, p, false);
                return p;
            }

            return IntPtr.Zero;
        }

        private static string DescribeFormat(WAVEFORMATEX wf)
        {
            string kind;
            ushort bits = wf.wBitsPerSample;

            if (wf.wFormatTag == WAVE_FORMAT_EXTENSIBLE)
                kind = "extensible";
            else if (wf.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
                kind = "float";
            else if (wf.wFormatTag == WAVE_FORMAT_PCM)
                kind = "pcm";
            else
                kind = "tag=0x" + wf.wFormatTag.ToString("X4");

            return wf.nSamplesPerSec + " Hz / " + wf.nChannels + " ch / " + bits + " bit / " + kind;
        }

        private static string FriendlyName(IMMDevice dev)
        {
            try
            {
                IPropertyStore store;
                if (dev.OpenPropertyStore(0, out store) != 0) return "(无法读取名称)";
                PROPVARIANT pv;
                var key = PKEY_FriendlyName;
                if (store.GetValue(ref key, out pv) != 0) return "(无法读取名称)";
                if (pv.pointerValue != IntPtr.Zero)
                {
                    string s = Marshal.PtrToStringUni(pv.pointerValue);
                    Marshal.FreeCoTaskMem(pv.pointerValue);
                    return s ?? "(空名称)";
                }
                return "(空)";
            }
            catch (Exception ex)
            {
                return "(读取名称异常: " + ex.Message + ")";
            }
        }
    }
}
