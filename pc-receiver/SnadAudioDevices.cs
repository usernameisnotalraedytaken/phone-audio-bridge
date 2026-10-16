// 音频设备枚举与能力探测。
//
// 这份逻辑被三处共用，因此必须是共享代码而不是各写一份：
//   SnadAudioProbe    列出设备与能力（命令行工具）
//   Profile 编辑器     用能力数据限制可选项，不让用户选出跑不起来的组合
//   接收器             启动时校验所选后端/设备/格式是否真的可用
//
// 探测出的关键事实（本机实测，见 AUDIO-CAPABILITIES.md）：
//   物理声卡   支持独占（pcm16/pcm24），最小周期 2.00ms
//   Voicemeeter 虚拟设备  一律不支持独占（0x88890008），最小周期 2.00ms

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Snad
{
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ComImport]
    internal class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClientProbe
    {
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

    /// <summary>设备枚举与能力探测。</summary>
    internal static class DeviceEnumerator
    {
        private const int eRender = 0;
        private const int DEVICE_STATE_ACTIVE = 1;
        private const int CLSCTX_ALL = 23;
        private const int EXCLUSIVE = 1;
        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        private static readonly PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        /// <summary>
        /// 枚举全部活动输出设备并探测其能力。
        /// 单个设备探测失败不影响其余设备——只记录原因，不抛。
        /// </summary>
        public static List<DeviceCapability> Probe()
        {
            var list = new List<DeviceCapability>();
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());

            IMMDeviceCollection coll;
            int hr = enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out coll);
            if (hr != 0) throw new InvalidOperationException("EnumAudioEndpoints 失败 hr=0x" + hr.ToString("X8"));

            int count;
            coll.GetCount(out count);

            for (int i = 0; i < count; i++)
            {
                IMMDevice dev;
                if (coll.Item(i, out dev) != 0) continue;

                var cap = new DeviceCapability { Index = i };
                try
                {
                    dev.GetId(out cap.Id);
                    cap.Name = FriendlyName(dev);
                    ProbeOne(dev, cap);
                }
                catch (Exception ex)
                {
                    // 单台设备探测失败（驱动异常、被独占占用等）不影响其他设备，
                    // 但要把原因留在名字里，不能在界面上显示成一个正常的空设备
                    cap.Name = cap.Name + "  [探测失败: " + ex.Message + "]";
                }
                list.Add(cap);
            }
            return list;
        }

        private static void ProbeOne(IMMDevice dev, DeviceCapability cap)
        {
            object o;
            Guid iid = IID_IAudioClient;
            if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) return;
            var ac = (IAudioClientProbe)o;

            long def, min;
            ac.GetDevicePeriod(out def, out min);
            cap.DefaultPeriodMs = def / 10000.0;
            cap.MinPeriodMs = min / 10000.0;

            IntPtr fmt;
            if (ac.GetMixFormat(out fmt) != 0 || fmt == IntPtr.Zero) return;

            var wf = (AudioCodec.WAVEFORMATEX)Marshal.PtrToStructure(fmt, typeof(AudioCodec.WAVEFORMATEX));
            cap.MixRate = (int)wf.nSamplesPerSec;
            cap.MixChannels = wf.nChannels;
            cap.MixBits = wf.wBitsPerSample;
            // 关键：SubFormat 必须从扩展结构里读，靠位深猜会在 32bit 整数的设备上判错
            cap.MixSampleFormat = AudioCodec.FormatFromMix(fmt);
            cap.MixDescription = AudioCodec.Describe(fmt);
            Marshal.FreeCoTaskMem(fmt);

            // 独占模式支持哪些格式。四种都探——少探一种就会出现"字段永远是 false"
            // 的不诚实状态（那正是先前的警告所指）。
            foreach (var pair in new[]
            {
                new { F = SampleFormat.Pcm16, Set = (Action<bool>)(v => cap.ExclusivePcm16 = v) },
                new { F = SampleFormat.Pcm24, Set = (Action<bool>)(v => cap.ExclusivePcm24 = v) },
                new { F = SampleFormat.Pcm32, Set = (Action<bool>)(v => cap.ExclusivePcm32 = v) },
                new { F = SampleFormat.Float32, Set = (Action<bool>)(v => cap.ExclusiveFloat32 = v) },
            })
            {
                IntPtr built = AudioCodec.BuildFormat(pair.F, cap.MixChannels, cap.MixRate);
                IntPtr closest;
                int r = ac.IsFormatSupported(EXCLUSIVE, built, out closest);
                if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
                Marshal.FreeCoTaskMem(built);
                pair.Set(r == 0);
            }
        }

        /// <summary>取默认输出设备的能力。找不到时返回 null。</summary>
        public static DeviceCapability Default()
        {
            var all = Probe();
            if (all.Count == 0) return null;

            try
            {
                var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                IMMDevice def;
                if (enumerator.GetDefaultAudioEndpoint(eRender, 0, out def) == 0)
                {
                    string id;
                    if (def.GetId(out id) == 0)
                    {
                        foreach (var d in all) if (d.Id == id) return d;
                    }
                }
            }
            catch { }

            return all[0];
        }

        private static string FriendlyName(IMMDevice dev)
        {
            try
            {
                IPropertyStore store;
                if (dev.OpenPropertyStore(0, out store) != 0) return "(未知设备)";
                PROPVARIANT pv;
                var key = PKEY_FriendlyName;
                if (store.GetValue(ref key, out pv) != 0) return "(未知设备)";
                if (pv.pointerValue != IntPtr.Zero)
                {
                    string s = Marshal.PtrToStringUni(pv.pointerValue);
                    Marshal.FreeCoTaskMem(pv.pointerValue);
                    return string.IsNullOrEmpty(s) ? "(空名称)" : s;
                }
                return "(未知设备)";
            }
            catch (Exception ex)
            {
                return "(名称读取失败: " + ex.Message + ")";
            }
        }
    }
}
