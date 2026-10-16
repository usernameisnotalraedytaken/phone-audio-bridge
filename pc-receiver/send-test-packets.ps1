# SNAD test sender (run on Windows host)
# Purpose: verify audio path and log path without a phone.
#
# NOTE: must run on Windows. 127.0.0.1 inside WSL is WSL own loopback,
#       packets never reach the Windows receiver (learned the hard way).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File send-test-packets.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File send-test-packets.ps1 -Target 192.168.54.22 -Seconds 10

param(
    [string]$Target = '127.0.0.1',
    [int]$Port = 6981,
    [double]$Seconds = 6.0,
    [int]$Rate = 48000,
    [int]$Channels = 2,
    [switch]$SkipBadPacket
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$PAYLOAD = 4096
$framesPerPacket = $PAYLOAD / ($Channels * 2)
$logPort = $Port + 1

$audio = New-Object System.Net.Sockets.UdpClient
$audio.Connect($Target, $Port)
$logc = New-Object System.Net.Sockets.UdpClient
$logc.Connect($Target, $logPort)

function Send-Log([int]$seq, [string]$text) {
    $payload = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hdr = New-Object byte[] 14
    [System.Text.Encoding]::ASCII.GetBytes('SNAD').CopyTo($hdr, 0)
    $hdr[4] = 1
    $hdr[5] = [byte][char]'T'
    [BitConverter]::GetBytes($seq).CopyTo($hdr, 6)
    # ms timestamp is ~1.79e12, so compute in Int64 and truncate explicitly.
    # PowerShell coerces eagerly: [int](...) and even [int][int64](...) can overflow
    # before -band is applied, so use a typed Int64 variable.
    [int64]$msFull = ([DateTime]::UtcNow - [datetime]'1970-01-01').TotalMilliseconds
    [int]$ms = $msFull -band 0xFFFFFFFFL
    [BitConverter]::GetBytes($ms).CopyTo($hdr, 10)
    $pkt = New-Object byte[] (14 + $payload.Length)
    $hdr.CopyTo($pkt, 0); $payload.CopyTo($pkt, 14)
    [void]$logc.Send($pkt, $pkt.Length)
}

# Pre-render one PCM period and reuse it to keep up with realtime
$pcm = New-Object byte[] $PAYLOAD
for ($i = 0; $i -lt $framesPerPacket; $i++) {
    $v = [int](12000 * [Math]::Sin(2 * [Math]::PI * 440.0 * $i / $Rate))
    $b = [BitConverter]::GetBytes([int16]$v)
    $off = $i * 4
    $pcm[$off] = $b[0]; $pcm[$off + 1] = $b[1]
    $pcm[$off + 2] = $b[0]; $pcm[$off + 3] = $b[1]
}

Write-Host ("audio -> " + $Target + ":" + $Port + "   log -> " + $Target + ":" + $logPort + "   " + $Seconds + "s")

$lseq = 0
Send-Log $lseq "---- test sender started (simulated phone log) ----"; $lseq++
Send-Log $lseq ("INFO  [Test] audio port " + $Port + " / log port " + $logPort); $lseq++
Send-Log $lseq "ERROR [Test] simulated failure: java.lang.IllegalStateException: demo"; $lseq++

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$seq = 0
$totalFrames = [int]($Rate * $Seconds)
$frame = 0

while ($frame -lt $totalFrames) {
    $n = [Math]::Min($framesPerPacket, $totalFrames - $frame)
    $hdr = New-Object byte[] 22
    [System.Text.Encoding]::ASCII.GetBytes('SNAD').CopyTo($hdr, 0)
    $hdr[4] = 1
    $hdr[5] = [byte]$Channels
    [BitConverter]::GetBytes([int]$Rate).CopyTo($hdr, 6)
    [BitConverter]::GetBytes([int]$seq).CopyTo($hdr, 10)
    [BitConverter]::GetBytes([int]($sw.ElapsedMilliseconds -band 0xFFFFFFFF)).CopyTo($hdr, 14)
    [BitConverter]::GetBytes([int]$PAYLOAD).CopyTo($hdr, 18)

    $pkt = New-Object byte[] (22 + $PAYLOAD)
    $hdr.CopyTo($pkt, 0)
    $pcm.CopyTo($pkt, 22)
    [void]$audio.Send($pkt, $pkt.Length)

    $seq++
    $frame += $n

    if ($seq % 100 -eq 0) {
        Send-Log $lseq ("INFO  [Test] sent " + $seq + " audio packets, " + [math]::Round($sw.Elapsed.TotalSeconds,1) + "s")
        $lseq++
    }

    $target = ($frame / $Rate) * 1000.0
    $wait = $target - $sw.Elapsed.TotalMilliseconds
    if ($wait -gt 1) { Start-Sleep -Milliseconds ([int]$wait) }
}

if (-not $SkipBadPacket) {
    # deliberately send a bad packet to prove diagnostics are visible (no silent failure)
    $bad = New-Object byte[] 34
    [System.Text.Encoding]::ASCII.GetBytes('XXXX').CopyTo($bad, 0)
    [void]$audio.Send($bad, $bad.Length)
    Send-Log $lseq "ERROR [Test] sent one bad-magic packet; receiver must log and drop it"; $lseq++
}

$audio.Close(); $logc.Close()
Write-Host ("done: " + $seq + " audio packets, " + $lseq + " log lines, " + [math]::Round($sw.Elapsed.TotalSeconds,2) + "s")
