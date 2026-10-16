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
foreach ($asm in @('mscorlib.dll', 'System.dll', 'System.Core.dll')) {
    if (-not (Test-Path (Join-Path $fw $asm))) { throw ("缺少参考程序集 " + (Join-Path $fw $asm)) }
}

Write-Host "csc : $csc"
Write-Host "refs: $fw"

& $csc /nologo /target:exe /platform:x64 /optimize+ `
    /out:SnadReceiver.exe `
    "/r:$fw\mscorlib.dll" "/r:$fw\System.dll" "/r:$fw\System.Core.dll" `
    SnadReceiver.cs

if ($LASTEXITCODE -ne 0) { throw "编译失败，csc 退出码 $LASTEXITCODE" }

Get-Item SnadReceiver.exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host ""
Write-Host ("编译成功。运行： " + (Join-Path $here "SnadReceiver.exe") + " --list   查看设备列表")
