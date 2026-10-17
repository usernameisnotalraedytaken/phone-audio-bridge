# 量一下设备名在实际可用宽度下会不会被截断。
#
# 起因：设备滚轮里长名字显示不全。与其猜"宽度够不够"或按名字规律做缩短
# （那是面向特例编程），不如把字宽量出来和可用宽度比一比。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File measure.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$consolas = New-Object System.Drawing.Font 'Consolas', 9.5

# 主界面横向分配（与 MainForm.LayoutControlRow 保持一致）
$formW = 960
$padLeft = 12
$padRight = 12
$profileW = 150
$startW = 88
$eqW = 88
$gap = 12
$btnBlock = $eqW + 8 + $startW
$deviceW = $formW - $padLeft - $profileW - $gap - $btnBlock - $gap - $padRight

Write-Host ("窗口宽 {0}  档位 {1}  设备滚轮可用 {2}  右侧按钮区 {3}" -f $formW, $profileW, $deviceW, $btnBlock)
Write-Host ""

# 各设备名在 Consolas 9.5pt 下的宽度（取本机实际输出设备名做样本）
$names = @(
    'SYSTEM DEFAULT',
    '[0] 耳机 (Realtek(R) Audio)',
    '[1] Voicemeeter Input (VB-Audio Voicemeeter VAIO)',
    '[2] Voicemeeter VAIO3 Input (VB-Audio Voicemeeter VAIO)',
    '[3] Voicemeeter In 3 (VB-Audio Voicemeeter VAIO)',
    '[5] Voicemeeter In 5 (VB-Audio Voicemeeter VAIO)',
    '[7] 扬声器 (Realtek(R) Audio)',
    '[9] Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)'
)

$bmp = New-Object System.Drawing.Bitmap 1, 1
$g = [System.Drawing.Graphics]::FromImage($bmp)
$usable = $deviceW - 22      # 两侧留白 + 箭头占位
$worst = 0

foreach ($n in $names) {
    $w = [int]$g.MeasureString($n, $consolas).Width
    if ($w -gt $worst) { $worst = $w }
    $mark = if ($w -le $usable) { 'OK ' } else { 'CUT' }
    Write-Host ("{0} {1,5}px  {2}" -f $mark, $w, $n)
}

Write-Host ""
Write-Host ("最长名字 {0}px，可用 {1}px" -f $worst, $usable)
if ($worst -le $usable) {
    Write-Host "结论：宽度足够，无需截断"
} else {
    Write-Host ("结论：仍缺 {0}px，需要加宽或缩小字号" -f ($worst - $usable))
}

$g.Dispose(); $bmp.Dispose(); $consolas.Dispose()
