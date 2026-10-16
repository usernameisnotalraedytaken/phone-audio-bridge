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

# 正在运行的进程会锁住 exe，导致 csc 报 CS2012 无法写入。
#
# 需要停的不止两个产物：检查工具（SnadShot / SnadPreview / SnadEqShot /
# SnadWordShot）也各自是独立 exe，同名进程若在运行同样会锁住目标文件。
# 这里统一停掉所有本项目的可执行文件，并等待句柄真正释放——
# Stop-Process 返回后文件锁可能还残留一小段时间，实测 800ms 不够稳。
$ourExes = @('SnadReceiver', 'SnadGui', 'SnadShot', 'SnadPreview', 'SnadEqShot', 'SnadWordShot')
foreach ($name in $ourExes) {
    $running = Get-Process $name -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host ("停止正在运行的 " + $name + " pid=" + (($running | ForEach-Object { $_.Id }) -join ','))
        $running | Stop-Process -Force
    }
}

# 等待目标文件可写。逐个尝试打开独占句柄，成功即说明锁已释放。
$targets = @('SnadReceiver.exe', 'SnadGui.exe')
for ($i = 0; $i -lt 40; $i++) {
    $busy = $false
    foreach ($t in $targets) {
        if (-not (Test-Path $t)) { continue }
        try {
            $fs = [System.IO.File]::Open((Join-Path (Get-Location) $t), 'Open', 'ReadWrite', 'None')
            $fs.Close(); $fs.Dispose()
        } catch { $busy = $true }
    }
    if (-not $busy) { break }
    if ($i -eq 39) {
        Write-Host "警告：仍有目标文件被占用，编译可能失败" -ForegroundColor Yellow
    }
    Start-Sleep -Milliseconds 200
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

# ---- 预共享密钥注入 ----
# 密钥不存放在源码里，构建时从仓库外的 .snad-key 读入并生成一个编译单元。
# 缺密钥直接报错停止：绝不能回退到某个默认密钥，那等于没有保护。
$keyFile = Join-Path (Split-Path -Parent $here) '.snad-key'
if (-not (Test-Path $keyFile)) {
    throw ("缺少预共享密钥文件：" + $keyFile + "`n" +
           "该文件由仓库根目录的 .snad-key 提供（已 gitignore）。`n" +
           "生成方式：python3 -c `"import os,base64;print(base64.b32encode(os.urandom(32)).decode())`"")
}
$psk = (Get-Content -LiteralPath $keyFile -Raw).Trim()
if ($psk.Length -lt 16) { throw ("密钥文件内容过短（" + $psk.Length + " 字符），疑似损坏：" + $keyFile) }

# KeyId 用于确认两端是否使用同一份密钥。规则必须与 Android 端一致：
#   KeyId = SHA256(PSK) 前 8 字节的大写十六进制
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = $sha.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($psk))
$sha.Dispose()
$keyId = -join ($hash[0..7] | ForEach-Object { $_.ToString('X2') })

$secretCs = @"
// 本文件由 build.ps1 生成，请勿手工编辑，也不要提交到版本库。
// 内容来自仓库根目录的 .snad-key（预共享密钥）。
namespace Snad
{
    internal static class SnadSecret
    {
        internal const string KeyId = "$keyId";
        internal const string Psk = "$psk";
    }
}
"@
# 用 UTF8 无 BOM 写出，避免 csc 把 BOM 当成源码内容
[System.IO.File]::WriteAllText((Join-Path $here 'SnadSecret.cs'), $secretCs, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("密钥已注入：KeyId=" + $keyId + "（两端此值必须一致）")

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
$consoleSources = @('SnadReceiver.cs', 'SnadProfile.cs', 'SnadBeacon.cs', 'SnadBeaconService.cs',
                    'SnadEq.cs', 'SnadSecret.cs')
$consoleArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+',
                 '/out:SnadReceiver.exe', '/define:CONSOLE_BUILD')
foreach ($a in $coreRefs) { $consoleArgs += ("/r:" + (Join-Path $fw $a)) }
$consoleArgs += $consoleSources
& $csc @consoleArgs
if ($LASTEXITCODE -ne 0) { throw "控制台版编译失败，csc 退出码 $LASTEXITCODE" }

# ---- 1b) 信标协议验证工具（独立入口，用于在手机端实现前先证明协议可用）----
if (Test-Path 'SnadBeaconTest.cs') {
    $testSources = @('SnadBeacon.cs', 'SnadBeaconService.cs', 'SnadSecret.cs', 'SnadBeaconTest.cs')
    $testArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+',
                  '/main:Snad.BeaconTest', '/out:SnadBeaconTest.exe')
    foreach ($a in $coreRefs) { $testArgs += ("/r:" + (Join-Path $fw $a)) }
    $testArgs += $testSources
    & $csc @testArgs
    if ($LASTEXITCODE -ne 0) { throw "信标验证工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 1c) 均衡器 DSP 验证工具 ----
if (Test-Path 'SnadEqTest.cs') {
    $eqTestArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+',
                    '/main:Snad.EqTest', '/out:SnadEqTest.exe')
    foreach ($a in $coreRefs) { $eqTestArgs += ("/r:" + (Join-Path $fw $a)) }
    $eqTestArgs += @('SnadEq.cs', 'SnadEqTest.cs')
    & $csc @eqTestArgs
    if ($LASTEXITCODE -ne 0) { throw "均衡器验证工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 1d) 均衡器界面自渲染检查工具 ----
if (Test-Path 'SnadEqShot.cs') {
    $eqShotArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                    '/main:Snad.EqShot', '/out:SnadEqShot.exe')
    foreach ($a in $guiRefs) { $eqShotArgs += ("/r:" + (Join-Path $fw $a)) }
    $eqShotArgs += @('SnadEq.cs', 'SnadEqForm.cs', 'SnadEqShot.cs',
                     'SnadTheme.cs', 'SnadControls.cs', 'SnadWidgets.cs', 'SnadWordmark.cs', 'SnadGraph.cs',
                     'SnadMetricStrip.cs', 'SnadSpectrum.cs')
    & $csc @eqShotArgs
    if ($LASTEXITCODE -ne 0) { throw "均衡器界面检查工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 1e) 配置读写往返验证工具 ----
if (Test-Path 'SnadConfigTest.cs') {
    $cfgTestArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+',
                     '/main:Snad.ConfigTest', '/out:SnadConfigTest.exe')
    foreach ($a in $coreRefs) { $cfgTestArgs += ("/r:" + (Join-Path $fw $a)) }
    # 需要 SnadReceiver.cs 是因为 Logger 定义在其中；而它又引用 SnadSecret，
    # 因此密钥单元也必须一起编译。
    $cfgTestArgs += @('SnadReceiver.cs', 'SnadProfile.cs', 'SnadEq.cs', 'SnadSecret.cs', 'SnadConfigTest.cs')
    & $csc @cfgTestArgs
    if ($LASTEXITCODE -ne 0) { throw "配置验证工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 1f) 界面自渲染检查工具（主界面 + 均衡器）----
if (Test-Path 'SnadShot.cs') {
    $shotArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                  '/main:Snad.Shot', '/out:SnadShot.exe')
    foreach ($a in $guiRefs) { $shotArgs += ("/r:" + (Join-Path $fw $a)) }
    $shotArgs += @('SnadReceiver.cs', 'SnadProfile.cs', 'SnadBeacon.cs', 'SnadBeaconService.cs',
                   'SnadEq.cs', 'SnadEqForm.cs', 'SnadSpectrum.cs', 'SnadGui.cs',
                   'SnadTheme.cs', 'SnadControls.cs', 'SnadWidgets.cs', 'SnadWordmark.cs', 'SnadGraph.cs', 'SnadMetricStrip.cs',
                   'SnadSecret.cs', 'SnadShot.cs')
    & $csc @shotArgs
    if ($LASTEXITCODE -ne 0) { throw "界面检查工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 1h) 字标放大预览工具 ----
if (Test-Path 'SnadWordShot.cs') {
    $wordArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                  '/main:Snad.WordShot', '/out:SnadWordShot.exe')
    foreach ($a in $guiRefs) { $wordArgs += ("/r:" + (Join-Path $fw $a)) }
    $wordArgs += @('SnadTheme.cs', 'SnadWordmark.cs', 'SnadWordShot.cs')
    & $csc @wordArgs
    if ($LASTEXITCODE -ne 0) { throw "字标预览工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 1g) UI 组件预览工具（频谱图与通道条单独渲染）----
if (Test-Path 'SnadPreview.cs') {
    $prevArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                  '/main:Snad.Preview', '/out:SnadPreview.exe')
    foreach ($a in $guiRefs) { $prevArgs += ("/r:" + (Join-Path $fw $a)) }
    $prevArgs += @('SnadTheme.cs', 'SnadControls.cs', 'SnadWidgets.cs', 'SnadWordmark.cs', 'SnadGraph.cs', 'SnadMetricStrip.cs',
                   'SnadSpectrum.cs', 'SnadEq.cs', 'SnadPreview.cs')
    & $csc @prevArgs
    if ($LASTEXITCODE -ne 0) { throw "UI 预览工具编译失败，csc 退出码 $LASTEXITCODE" }
}

# ---- 2) GUI 版 ----
if ($buildGui) {
    $guiSources = @('SnadReceiver.cs', 'SnadProfile.cs', 'SnadBeacon.cs', 'SnadBeaconService.cs',
                    'SnadSecret.cs', 'SnadEq.cs', 'SnadEqForm.cs',
                    'SnadTheme.cs', 'SnadControls.cs', 'SnadWidgets.cs', 'SnadWordmark.cs', 'SnadGraph.cs', 'SnadMetricStrip.cs',
                    'SnadGui.cs', 'SnadSpectrum.cs')
    $missing = $guiSources | Where-Object { -not (Test-Path $_) }
    if ($missing.Count -eq 0) {
        $guiArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+',
                     '/main:Snad.GuiEntry', '/out:SnadGui.exe')
        # 嵌入应用图标：双击时不再显示 .NET 默认图标
        if (Test-Path 'app.ico') { $guiArgs += '/win32icon:app.ico' }
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
