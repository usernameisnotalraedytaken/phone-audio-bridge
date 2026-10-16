using System;
using System.Runtime.InteropServices;
using System.Threading;
class VmLevel {
    const string DLL = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_Login();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_Logout();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_GetLevel(int nType, int nChannel, out float pValue);

    // nType: 0=pre-fader input, 1=post-fader input, 2=post-mute input, 3=output bus
    static string Level(int type, int ch) {
        float v;
        int rc = VBVMR_GetLevel(type, ch, out v);
        if (rc != 0) return "rc=" + rc;
        return v.ToString("F2");
    }

    static void Main() {
        int r = VBVMR_Login();
        if (r != 0 && r != 1) { Console.WriteLine("Login 失败 rc=" + r); return; }
        Console.WriteLine("采样 12 次 x 每 500ms，观察哪一路有信号\n");

        string[] labels = {
            "Strip0(麦克风) pre", "Strip1(VIRTUAL?) pre", "Strip2 pre",
            "Strip0 post", "Strip1 post", "Strip2 post",
            "Bus0(A1) out", "Bus1(A2) out"
        };

        Console.WriteLine(string.Format("{0,-22} {1,8} {2,8} {3,8} {4,8} {5,8} {6,8}",
            "时刻", "S0pre", "S1pre", "S2pre", "S0post", "S1post", "S2post"));

        float maxS0 = 0, maxS1 = 0, maxS2 = 0, maxBus0 = 0;
        for (int t = 0; t < 12; t++) {
            float s0 = 0, s1 = 0, s2 = 0, s0p = 0, s1p = 0, s2p = 0, b0 = 0;
            VBVMR_GetLevel(0, 0, out s0);
            VBVMR_GetLevel(0, 1, out s1);
            VBVMR_GetLevel(0, 2, out s2);
            VBVMR_GetLevel(1, 0, out s0p);
            VBVMR_GetLevel(1, 1, out s1p);
            VBVMR_GetLevel(1, 2, out s2p);
            VBVMR_GetLevel(3, 0, out b0);
            if (s0 > maxS0) maxS0 = s0;
            if (s1 > maxS1) maxS1 = s1;
            if (s2 > maxS2) maxS2 = s2;
            if (b0 > maxBus0) maxBus0 = b0;
            Console.WriteLine(string.Format("{0,-22} {1,8:F2} {2,8:F2} {3,8:F2} {4,8:F2} {5,8:F2} {6,8:F2}",
                DateTime.Now.ToString("HH:mm:ss.fff"), s0, s1, s2, s0p, s1p, s2p));
            Thread.Sleep(500);
        }
        Console.WriteLine(string.Format("\n峰值: Strip0={0:F2}  Strip1={1:F2}  Strip2={2:F2}  Bus0(A1)={3:F2}",
            maxS0, maxS1, maxS2, maxBus0));
        Console.WriteLine("(单位 dB，-60 以下基本等于无声；> -40 说明确实有音频流过)");
        VBVMR_Logout();
    }
}
