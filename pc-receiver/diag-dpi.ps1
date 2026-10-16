# 诊断 GUI 的 DPI 感知状态与实际可用空间。
# 目的：确认窗口尺寸是按逻辑像素还是物理像素计算，从而定出合理的布局尺寸。

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DpiInfo {
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr h, out int awareness);
}
"@

# 本进程的 DPI 感知（PowerShell 自身，通常为 unaware）
$aw = -1
try { [void][DpiInfo]::GetProcessDpiAwareness([IntPtr]::Zero, [ref]$aw) } catch { }
Write-Host ("PowerShell 进程 DPI 感知 = " + $aw + "  (0=unaware 1=system 2=permonitor)")

$ctx = [DpiInfo]::GetThreadDpiAwarenessContext()
$aware = [DpiInfo]::GetAwarenessFromDpiAwarenessContext($ctx)
Write-Host ("当前线程 awareness = " + $aware + "  (0=unaware 1=system 2=permonitor)")
Write-Host ("系统 DPI = " + [DpiInfo]::GetDpiForSystem())

$s = [System.Windows.Forms.Screen]::PrimaryScreen
Write-Host ("Screen.Bounds      = " + $s.Bounds.Width + "x" + $s.Bounds.Height)
Write-Host ("Screen.WorkingArea = " + $s.WorkingArea.Width + "x" + $s.WorkingArea.Height)

Write-Host ("SM_CXSCREEN = " + [DpiInfo]::GetSystemMetrics(0) + "  SM_CYSCREEN = " + [DpiInfo]::GetSystemMetrics(1))
Write-Host ("SM_CXVIRTUALSCREEN = " + [DpiInfo]::GetSystemMetrics(78))

# SnadGui 状态
$p = Get-Process SnadGui -ErrorAction SilentlyContinue
if ($p) {
    Write-Host ("SnadGui pid=" + $p.Id + " 主窗口句柄=" + $p.MainWindowHandle)
    try {
        $m = [DpiInfo]::GetProcessDpiAwareness($p.Handle, [ref]$aw)
        Write-Host ("SnadGui DPI 感知 = " + $aw + "  (rc=" + $m + ")")
    } catch { Write-Host ("读取 SnadGui DPI 感知失败: " + $_.Exception.Message) }
} else {
    Write-Host "SnadGui 未运行"
}
