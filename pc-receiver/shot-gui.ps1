# 截取 SnadGui 主窗口，用于验证界面渲染。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File shot-gui.ps1 [-Out <路径>]

param(
    [string]$Out = 'C:\Users\26347\Downloads\bluetooth\bt-probe\gui.png',
    [int]$WaitMs = 900
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinApi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@

# 关键：本脚本必须自己声明 DPI 感知。
# 否则 GetWindowRect 返回被系统虚拟化过的坐标（按 96 DPI 折算），
# 而 CopyFromScreen 用的是物理像素，两者不一致会抓到完全错误的区域
# （实测抓到了另一个窗口的内容）。
$dpiOk = $false
try { $dpiOk = [WinApi]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch { }
if (-not $dpiOk) { try { $dpiOk = ([WinApi]::SetProcessDpiAwareness(2) -eq 0) } catch { } }
if (-not $dpiOk) { try { $dpiOk = [WinApi]::SetProcessDPIAware() } catch { } }
Write-Host ("DPI 感知: " + $dpiOk)

$p = Get-Process SnadGui -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "SnadGui 未运行"; exit 1 }

$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Write-Host "主窗口句柄为 0（窗口可能尚未创建或已隐藏）"; exit 1 }

[void][WinApi]::ShowWindow($h, 5)      # SW_SHOW
[void][WinApi]::SetForegroundWindow($h)
Start-Sleep -Milliseconds $WaitMs

$r = New-Object WinApi+RECT
if (-not [WinApi]::GetWindowRect($h, [ref]$r)) { Write-Host "GetWindowRect 失败"; exit 1 }

$w = $r.Right - $r.Left
$hgt = $r.Bottom - $r.Top
if ($w -le 0 -or $hgt -le 0) { Write-Host ("窗口尺寸异常: " + $w + "x" + $hgt); exit 1 }

Write-Host ("窗口 " + $w + "x" + $hgt + " @ (" + $r.Left + "," + $r.Top + ")")

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Host ("已保存 " + $Out + "  (" + (Get-Item $Out).Length + " 字节)")
