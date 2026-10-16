using System;
using System.Runtime.InteropServices;
using System.Text;
class VmState {
    const string DLL = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_Login();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_Logout();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_GetParameterFloat(string n, out float v);
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    static extern int VBVMR_GetParameterStringW(string n, StringBuilder v);

    static void Main() {
        int r = VBVMR_Login();
        Console.WriteLine("Login: " + r);
        if (r != 0 && r != 1) return;

        Console.WriteLine("\n--- 各条带是否有信号（电平，-60..0 dB）---");
        for (int i = 0; i < 3; i++) {
            float lvl = -999;
            // 电平用 GetLevel，这里退而用 Gain/Mute/A1 判断路由
            float gain, mute, a1, b1;
            VBVMR_GetParameterFloat("Strip[" + i + "].Gain", out gain);
            VBVMR_GetParameterFloat("Strip[" + i + "].Mute", out mute);
            VBVMR_GetParameterFloat("Strip[" + i + "].A1", out a1);
            VBVMR_GetParameterFloat("Strip[" + i + "].B1", out b1);
            Console.WriteLine(string.Format("  Strip[{0}] Gain={1,6:F1} Mute={2} A1={3} B1={4}", i, gain, mute, a1, b1));
        }
        Console.WriteLine("\n--- 总线 ---");
        for (int i = 0; i < 2; i++) {
            float mute, gain;
            VBVMR_GetParameterFloat("Bus[" + i + "].Mute", out mute);
            VBVMR_GetParameterFloat("Bus[" + i + "].Gain", out gain);
            Console.WriteLine(string.Format("  Bus[{0}] Gain={1,6:F1} Mute={2}", i, gain, mute));
        }
        Console.WriteLine("\n--- 尝试更多设备选择参数名（只读探测）---");
        string[] names = {
            "Strip[0].device.wdm", "Strip[0].device.name", "Strip[0].device",
            "Strip[0].device.WDM", "Strip[0].Device.name",
            "Strip[0].device.mme", "Strip[0].device.ks", "Strip[0].device.asio",
            "Strip[0].name", "Strip[0].DeviceName", "Strip[0].device.driver",
            "Strip[1].device.wdm", "Strip[1].device.name", "Strip[1].device",
            "Strip[1].device.WDM", "Strip[1].Device.name",
            "Strip[2].device.wdm", "Strip[2].device.name",
            "Bus[0].device.wdm", "Bus[0].device.name", "Bus[0].device",
            "Bus[1].device.wdm", "Bus[1].device.name"
        };
        foreach (var n in names) {
            var sb = new StringBuilder(400);
            int rc = VBVMR_GetParameterStringW(n, sb);
            if (rc == 0) Console.WriteLine("  OK    " + n + " = \"" + sb + "\"");
        }
        Console.WriteLine("(只列出返回 OK 的；其余参数名都不被 Standard 版接受)");
        VBVMR_Logout();
    }
}
