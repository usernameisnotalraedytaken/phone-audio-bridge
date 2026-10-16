using System;
using System.Runtime.InteropServices;
using System.Text;
class VmApi {
    const string DLL = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_Login();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_Logout();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_GetVoicemeeterType(out int t);
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)] static extern int VBVMR_GetParameterFloat(string n, out float v);
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    static extern int VBVMR_GetParameterStringW(string n, StringBuilder v);

    static void Main() {
        int r = VBVMR_Login();
        Console.WriteLine("Login rc=" + r);
        if (r != 0 && r != 1) { Console.WriteLine("无法连接 Voicemeeter"); return; }
        int type; VBVMR_GetVoicemeeterType(out type);
        Console.WriteLine("Voicemeeter Type = " + type + "  (1=Standard)");

        Console.WriteLine("\n########## 1. 条带实时状态 ##########");
        for (int i = 0; i < 3; i++) {
            float g, m, a1, b1, mc;
            VBVMR_GetParameterFloat("Strip[" + i + "].Gain", out g);
            VBVMR_GetParameterFloat("Strip[" + i + "].Mute", out m);
            VBVMR_GetParameterFloat("Strip[" + i + "].A1", out a1);
            VBVMR_GetParameterFloat("Strip[" + i + "].B1", out b1);
            VBVMR_GetParameterFloat("Strip[" + i + "].MC", out mc);
            Console.WriteLine(string.Format("  Strip[{0}] Gain={1,6:F1} Mute={2} A1={3} B1={4} MC={5}", i, g, m, a1, b1, mc));
        }
        Console.WriteLine("  (A1=1 表示该条带送往 A1 总线 → 扬声器)");

        Console.WriteLine("\n########## 2. 穷举设备选择参数名 ##########");
        Console.WriteLine("  格式：索引 x 后缀，看哪个被接受");
        string[] idx = new string[0];
        var idxList = new System.Collections.Generic.List<string>();
        for (int i = 0; i <= 3; i++) idxList.Add(i.ToString());
        idx = idxList.ToArray();

        string[] suffixes = {
            ".device.wdm", ".device.WDM", ".device.name", ".device.Name",
            ".device", ".Device", ".device.mme", ".device.ks", ".device.asio",
            ".device.driver", ".device.wdmname", ".DeviceName", ".name",
            ".device.audiodriver", ".Audiodriver", ".device.driver.name",
            ".Device.name", ".device.Name"
        };
        int found = 0;
        foreach (var i in idx) {
            foreach (var s in suffixes) {
                var n = "Strip[" + i + "]" + s;
                var sb = new StringBuilder(500);
                int rc = VBVMR_GetParameterStringW(n, sb);
                if (rc == 0) {
                    Console.WriteLine("  OK(读)  " + n + " = \"" + sb + "\"");
                    found++;
                }
            }
        }
        Console.WriteLine("  读取成功的字符串参数数：" + found);

        Console.WriteLine("\n########## 3. 总线设备参数 ##########");
        foreach (var i in idx) {
            foreach (var s in new[] { ".device.wdm", ".device.name", ".device" }) {
                var n = "Bus[" + i + "]" + s;
                var sb = new StringBuilder(500);
                int rc = VBVMR_GetParameterStringW(n, sb);
                if (rc == 0) Console.WriteLine("  OK(读)  " + n + " = \"" + sb + "\"");
            }
        }

        Console.WriteLine("\n########## 4. 全局/系统参数（看有哪些字符串参数可用）##########");
        foreach (var n in new[] {
            "Voicemeeter.Type", "Voicemeeter.Version", "Command.Name",
            "Strip[0].Label", "Strip[0].label", "Bus[0].Label",
            "Strip[0].Color", "Strip[0].color" }) {
            var sb = new StringBuilder(500);
            int rc = VBVMR_GetParameterStringW(n, sb);
            if (rc == 0) Console.WriteLine("  OK(读)  " + n + " = \"" + sb + "\"");
        }

        VBVMR_Logout();
        Console.WriteLine("\n完成");
    }
}
