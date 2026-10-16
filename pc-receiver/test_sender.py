#!/usr/bin/env python3
"""合成 SNAD 音频包 + 文本日志包，在没有手机的情况下验证接收器与日志管线。"""
import socket, struct, sys, time, math

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 6981
SECONDS = float(sys.argv[3]) if len(sys.argv) > 3 else 6.0

RATE, CH, PAYLOAD = 48000, 2, 4096
FREQ = 440.0
LOG_PORT = PORT + 1

audio = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
log = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
audio.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 1 << 20)

def send_log(seq, text):
    """文本包：14 字节头，type='T'"""
    ts = int(time.time() * 1000) & 0xFFFFFFFF
    hdr = b"SNAD" + bytes([1, ord('T')]) + struct.pack("<II", seq, ts)
    log.sendto(hdr + text.encode("utf-8"), (HOST, LOG_PORT))

def send_audio(seq, pcm, ts):
    hdr = b"SNAD" + bytes([1, CH]) + struct.pack("<IIII", RATE, seq, ts, PAYLOAD)
    audio.sendto(hdr + pcm, (HOST, PORT))

frames_per_pkt = PAYLOAD // (CH * 2)
total_frames = int(RATE * SECONDS)
t0 = time.time()
seq = 0
lseq = 0
frames = 0

send_log(lseq, "00:00:00.000 INFO  [Test] 这是来自手机的模拟日志行，用于验证日志管线")
lseq += 1
send_log(lseq, "00:00:00.001 INFO  [Test] 手机 -> 电脑 日志端口 %d" % LOG_PORT)
lseq += 1
send_log(lseq, "00:00:00.002 ERROR [Test] 模拟一条报错，带异常类型：java.lang.IllegalStateException: 演示用")
lseq += 1

print(f"audio -> {HOST}:{PORT}   log -> {HOST}:{LOG_PORT}   {SECONDS}s")

while frames < total_frames:
    n = min(frames_per_pkt, total_frames - frames)
    pcm = bytearray()
    for i in range(n):
        v = int(12000 * math.sin(2 * math.pi * FREQ * (frames + i) / RATE))
        pcm += struct.pack("<hh", v, v)
    if len(pcm) < PAYLOAD:
        pcm += b"\x00" * (PAYLOAD - len(pcm))

    send_audio(seq, bytes(pcm), int((time.time() - t0) * 1000) & 0xFFFFFFFF)
    seq += 1
    frames += n

    if seq % 100 == 0:
        send_log(lseq, "00:00:%02d.000 INFO  [Test] 已发送 %d 个音频包" % (int(time.time()-t0), seq))
        lseq += 1

    dt = (t0 + frames / RATE) - time.time()
    if dt > 0:
        time.sleep(dt)

# 故意发一个坏包，验证错误诊断可见
audio.sendto(b"XXXX" + b"\x00" * 30, (HOST, PORT))
send_log(lseq, "00:00:00.003 ERROR [Test] 已发送一个 magic 错误的坏包，接收器应当记录并丢弃")

print(f"sent {seq} audio packets, {lseq} log lines")
