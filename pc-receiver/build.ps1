# 编译 SNAD 电脑端接收器
#
# 为什么不用 dotnet build / MSBuild 项目文件：
#   本机没有 .NET SDK（C:\Program Files\dotnet\sdk 不存在），也没有 Windows SDK
#   和 NuGet 缓存，SDK 式项目会报 "无法解析 SDK Microsoft.NET.Sdk"。
#   但 VS Build Tools 自带 Roslyn 编译器，且系统有 .NET Framework 4.x 运行时程序集，
#   因此直接调用 csc.exe 针对运行时程序集编译，零依赖、零安装。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

# 若接收器正在运行，它会锁住 exe，导致 csc 报 CS2012 无法写入。
# 先停掉再编译；编译后不自动重启（由调用方决定用什么参数启动）。
$running = Get-Process SnadReceiver -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ("停止正在运行的接收器 pid=" + (($running | ForEach-Object { $_.Id }) -join ','))
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) {
    # 退回到其它可能位置
    $candidates = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio' -Recurse -Filter 'csc.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'Roslyn' } | Select-Object -First 1
    if ($candidates) { $csc = $candidates.FullName }
    else { throw "找不到 csc.exe。请确认已安装 Visual Studio Build Tools（含 Roslyn 编译器）。" }
}

$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

# WinForms 版（带 GUI）。缺 System.Windows.Forms 时退回控制台版。
$guiRefs = @('mscorlib.dll', 'System.dll', 'System.Core.dll',
             'System.Windows.Forms.dll', 'System.Drawing.dll')
$buildGui = $true
foreach ($asm in $guiRefs) {
    if (-not (Test-Path (Join-Path $fw $asm))) { $buildGui = $false; break }
}

$coreRefs = @('mscorlib.dll', 'System.dll', 'System.Core.dll')
foreach ($asm in $coreRefs) {
    if (-not (Test-Path (Join-Path $fw $asm))) { throw ("缺少参考程序集 " + (Join-Path $fw $asm)) }
}

Write-Host "csc : $csc"
Write-Host "refs: $fw"

# ---- 1) 控制台版（无 GUI，便于在无桌面环境或脚本中运行）----
$consoleSources = @('SnadReceiver.cs', 'SnadProfile.cs')
$consoleArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+',
                 '/out:SnadReceiver.exe', '/define:CONSOLE_BUILD')
foreach ($a in $coreRefs) { $consoleArgs += ("/r:" + (Join-Path $fw $a)) }
$consoleArgs += $consoleSources
& $csc @consoleArgs
if ($LASTEXITCODE -ne 0) { throw "控制台版编译失败，csc 退出码 $LASTEXITCODE" }

# ---- 2) GUI 版 ----
if ($buildGui) {
    $guiSources = @('SnadReceiver.cs', 'SnadProfile.cs', 'SnadGui.cs', 'SnadSpectrum.cs')
    $missing = $guiSources | Where-Object { -not (Test-Path $_) }
    if ($missing.Count -eq 0) {
        $guiArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                     '/main:Snad.GuiEntry', '/out:SnadGui.exe')
        # /win32icon 把图标写进 exe 资源，供资源管理器显示（WinForms 不会自动采用它）；
        # /resource 再把同一个 ico 作为托管资源嵌入，使程序能取到各尺寸的原生图标。
        # 只用 ExtractAssociatedIcon 的话拿到的是 32x32，标题栏需要 16x16，会缩放发虚。
        if (Test-Path 'app.ico') {
            $guiArgs += '/win32icon:app.ico'
            $guiArgs += '/resource:app.ico,AppIcon.ico'
            Write-Host "图标：win32icon + 嵌入资源 AppIcon.ico"
        }
        foreach ($a in $guiRefs) { $guiArgs += ("/r:" + (Join-Path $fw $a)) }
        $guiArgs += $guiSources
        & $csc @guiArgs
        if ($LASTEXITCODE -ne 0) { throw "GUI 版编译失败，csc 退出码 $LASTEXITCODE" }
    } else {
        Write-Host ("跳过 GUI 版，缺少源文件: " + ($missing -join ', '))
    }
}

Get-Item SnadReceiver.exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
if (Test-Path SnadGui.exe) {
    Get-Item SnadGui.exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
}
Write-Host ""
Write-Host ("编译成功。控制台版： " + (Join-Path $here "SnadReceiver.exe"))
if (Test-Path SnadGui.exe) { Write-Host ("            GUI 版： " + (Join-Path $here "SnadGui.exe")) }
