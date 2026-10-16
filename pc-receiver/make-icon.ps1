# 生成应用图标 app.ico（多尺寸，供 GUI 双击启动时显示）。
#
# 图形：深色圆角底 + 中心圆点 + 三段同心声波弧，与 SNAD 的"音频桥"含义对应。
# 不依赖任何外部图像库，全部用 System.Drawing 绘制后打包成 PNG 压缩的 ICO。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outPath = 'C:\Users\26347\Downloads\bluetooth\phone-audio-bridge\pc-receiver\app.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$pngData = New-Object System.Collections.ArrayList

foreach ($szRaw in $sizes) {
    $sz = [int]$szRaw
    $bmp = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # 背景：深蓝黑
    $g.Clear([System.Drawing.Color]::FromArgb(255, 11, 16, 32))

    $c = [double]$sz / 2.0
    $accent = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)

    # 三段同心弧（声波）。角度 -55..55 度，朝右开口。
    $radii = @(0.20, 0.32, 0.44)
    foreach ($rf in $radii) {
        $r = $c * [double]$rf * 1.7
        $pw = [float]([Math]::Max(1.0, $sz * 0.07))
        $pen = New-Object System.Drawing.Pen $accent, $pw
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $rect = New-Object System.Drawing.RectangleF ([float]($c - $r)), ([float]($c - $r)), ([float]($r * 2)), ([float]($r * 2))
        $g.DrawArc($pen, $rect, -55.0, 110.0)
        $pen.Dispose()
    }

    # 中心圆点（声源）
    $dotR = [float]($sz * 0.085)
    $cx = [float]($c - $sz * 0.30)
    $cy = [float]$c
    $brush = New-Object System.Drawing.SolidBrush $accent
    $g.FillEllipse($brush, [float]($cx - $dotR), [float]($cy - $dotR), [float]($dotR * 2), [float]($dotR * 2))
    $brush.Dispose()

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$pngData.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

# ---- 打包成 ICO（每项为 PNG 数据）----
$fs = New-Object System.IO.FileStream $outPath, ([System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([UInt16]0)                      # 保留
$bw.Write([UInt16]1)                      # 类型：图标
$bw.Write([UInt16]$sizes.Count)           # 图像数量

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = [int]$sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }   # 256 用 0 表示
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)                    # 调色板数
    $bw.Write([Byte]0)                    # 保留
    $bw.Write([UInt16]1)                  # 颜色平面
    $bw.Write([UInt16]32)                 # 位深
    $len = [UInt32]$pngData[$i].Length
    $bw.Write($len)
    $bw.Write([UInt32]$offset)
    $offset += [int]$len
}
foreach ($d in $pngData) { $bw.Write($d) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host ("app.ico 已生成：" + (Get-Item $outPath).Length + " 字节，" + $sizes.Count + " 个尺寸")
