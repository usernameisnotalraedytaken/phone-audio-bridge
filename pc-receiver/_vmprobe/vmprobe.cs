using System;
using System.Runtime.InteropServices;
using System.Text;

class VmProbe {
    const string DLL = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    static extern int VBVMR_Login();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    static extern int VBVMR_Logout();
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    static extern int VBVMR_GetVoicemeeterType(out int pType);
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    static extern int VBVMR_GetVoicemeeterVersion(out int pVersion);
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    static extern int VBVMR_GetParameterStringW(string paramName, StringBuilder paramValue);
    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    static extern int VBVMR_GetParameterFloat(string paramName, out float pValue);

    static string Codes(int r) {
        switch (r) {
            case 0: return "OK";
            case -1: return "not logged in";
            case -2: return "no server";
            case -3: return "unknown parameter";
            case -5: return "structure mismatch";
            default: return "code " + r;
        }
    }

    static void GetStr(string name) {
        var sb = new StringBuilder(512);
        int r = VBVMR_GetParameterStringW(name, sb);
        Console.WriteLine(string.Format("  {0,-34} -> {1,-18} value=\"{2}\"", name, Codes(r), sb.ToString()));
    }

    static void GetF(string name) {
        float v;
        int r = VBVMR_GetParameterFloat(name, out v);
        Console.WriteLine(string.Format("  {0,-34} -> {1,-18} value={2}", name, Codes(r), r == 0 ? v.ToString("F2") : "-"));
    }

    static void Main() {
        int r = VBVMR_Login();
        Console.WriteLine("Login: " + Codes(r));
        if (r != 0 && r != 1) return;

        int type, ver;
        VBVMR_GetVoicemeeterType(out type);
        VBVMR_GetVoicemeeterVersion(out ver);
        Console.WriteLine("Type=" + type + " (1=Standard 2=Banana 3=Potato)  Version=" + ver);
        Console.WriteLine();

        Console.WriteLine("--- 字符串参数探测（只读）---");
        for (int i = 1; i <= 3; i++) {
            GetStr("Strip[" + i + "].device.name");
        }
        for (int i = 1; i <= 3; i++) {
            GetStr("Strip[" + i + "].Label");
        }
        GetStr("Bus[0].device.name");
        GetStr("Bus[1].device.name");
        Console.WriteLine();

        Console.WriteLine("--- 数值参数探测（确认参数命名规则）---");
        for (int i = 1; i <= 3; i++) {
            GetF("Strip[" + i + "].Gain");
            GetF("Strip[" + i + "].Mute");
            GetF("Strip[" + i + "].A1");
        }
        Console.WriteLine();

        Console.WriteLine("--- 设备选择相关的可能参数名 ---");
        foreach (var n in new[] {
            "Strip[2].device.wdm", "Strip[2].device.mme", "Strip[2].device.ks",
            "Strip[2].device.asio", "Strip[2].device",
            "Strip[1].device.name", "Strip[2].device.name"
        }) GetStr(n);

        VBVMR_Logout();
        Console.WriteLine("\nLogout done");
    }
}
