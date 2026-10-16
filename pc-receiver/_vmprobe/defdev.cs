using System;
using System.Runtime.InteropServices;
class DefDev {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CAPS { public ushort mid, pid; public uint ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string name; public uint formats; public ushort ch, r1; public uint sup; }
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCaps(IntPtr id, ref CAPS c, int cb);
    [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
    static void Main() {
        var d = new CAPS();
        int hr = waveOutGetDevCaps(new IntPtr(-1), ref d, Marshal.SizeOf(typeof(CAPS)));
        Console.WriteLine("WAVE_MAPPER(-1) 查询结果 rc=" + hr + "  默认设备=\"" + d.name + "\"");
        int n = waveOutGetNumDevs();
        Console.WriteLine("设备总数=" + n);
        for (int i = 0; i < n; i++) {
            var c = new CAPS();
            if (waveOutGetDevCaps(new IntPtr(i), ref c, Marshal.SizeOf(typeof(CAPS))) == 0)
                Console.WriteLine("  [" + i + "] \"" + c.name + "\"");
        }
    }
}
