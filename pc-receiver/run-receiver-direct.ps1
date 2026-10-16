# 直接把手机音频送到 Realtek 扬声器，绕过 Voicemeeter。
# 用途：分离"SNAD 通路是否完好"与"Voicemeeter 路由是否配置正确"两个问题。
#
# 设备序号说明（本机实测）：
#   [0] 耳机 (Realtek(R) Audio)
#   [7] 扬声器 (Realtek(R) Audio)
param([int]$Device = 7)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
Write-Host ("使用设备 [" + $Device + "]")
& .\SnadReceiver.exe --device $Device
