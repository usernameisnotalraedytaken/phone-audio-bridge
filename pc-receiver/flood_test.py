#!/usr/bin/env python3
import socket, struct, sys, time
HOST = sys.argv[1]; PORT = int(sys.argv[2]); N = int(sys.argv[3])
RATE, CH, PAYLOAD = 48000, 2, 4096
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 1<<20)
pcm = bytes(PAYLOAD)
t0 = time.time()
for seq in range(N):
    h = b"SNAD" + bytes([1, CH]) + struct.pack("<IIII", RATE, seq, seq*21, PAYLOAD)
    s.sendto(h+pcm, (HOST, PORT))
print(f"flooded {N} packets in {time.time()-t0:.2f}s")
