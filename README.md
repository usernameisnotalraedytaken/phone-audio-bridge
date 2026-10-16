# SNAD — 手机音频桥

把手机的内部音频（音乐、视频、游戏声）通过局域网实时送到电脑，从电脑的音频链路出声。
**不需要 root，不需要 USB 线，手机可以在房间里自由移动。**

解决的问题：手机没有 3.5mm 接口，但电脑有完整的音频链路（DAC、功放、音箱、耳机）。

---

## 为什么不用现成方案

| 方案 | 问题 |
|---|---|
| 蓝牙 A2DP Sink（电脑当音箱） | Windows 接收端只谈 SBC 有损编码；延迟 150–250 ms；本机实测校园网/热点环境均受限 |
| VBAN Talkie | 手机端付费（HK$79.90），且定位是"无线麦克风"，不保证能抓内部音频 |
| sndcpy / scrcpy | 需要 USB 调试并插线，与"手机自由移动"冲突 |
| 校园网直连 | 客户端隔离，手机到电脑的包被 AP 丢弃 |

SNAD 的做法：手机用 Android 10+ 的 `AudioPlaybackCapture` 抓内部音频，**零编码**打包成
UDP 送出；电脑端原样播放进 Voicemeeter，与电脑音频混音后从声卡输出。

---

## 架构

```
┌─────────────┐   UDP (SNAD 协议)   ┌──────────────────────────────┐
│  Android    │ ──────────────────► │  Windows                     │
│  手机热点    │   192.168.x.x:6981  │  SnadReceiver.exe            │
│             │                     │    │                         │
│ AudioPlay-  │                     │    ▼                         │
│ backCapture │                     │  Voicemeeter Input           │
│ 48kHz/16bit │                     │    │  (虚拟输入条带)          │
│ 原生 PCM    │                     │    ▼                         │
└─────────────┘                     │  A1 总线 → Realtek 扬声器     │
                                    └──────────────────────────────┘
```

关键点：音频送进 `Voicemeeter Input` 而不是直接送扬声器，这样手机音频可以和电脑
自己的音频混合、分别调音量，再一起输出。不需要额外安装虚拟声卡。

---

## 传输协议

固定 22 字节包头 + PCM 负载，小端序：

| offset | size | 字段 |
|---|---|---|
| 0 | 4 | magic `"SNAD"` |
| 4 | 1 | version = 1 |
| 5 | 1 | channels |
| 6 | 4 | sampleRate |
| 10 | 4 | sequence (uint32，检测丢包) |
| 14 | 4 | timestampMs |
| 18 | 4 | payloadBytes |
| 22 | N | PCM16LE 交错数据 |

负载固定 4096 字节 = 1024 帧 @ 立体声，48 kHz 下约 21.3 ms/包，码率约 1.5 Mbps。

**端口 6981**，刻意避开 6980 —— 那是 Voicemeeter 自带 VBAN 服务的默认端口。

---

## 电脑端

### 编译

不需要 .NET SDK。本机没有 SDK 和 NuGet 缓存，但 VS Build Tools 自带 Roslyn，
系统有 .NET Framework 4.x 运行时程序集，因此直接调 `csc.exe`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File pc-receiver\build.ps1
```

### 运行

```powershell
cd pc-receiver
.\SnadReceiver.exe                    # 自动选 Voicemeeter Input，监听 6981
.\SnadReceiver.exe --list             # 只列出音频设备
.\SnadReceiver.exe --device 0         # 指定输出设备（0 = 扬声器）
.\SnadReceiver.exe --port 6981        # 指定端口
```

程序会每 5 秒打印统计：累计包数、数据量、码率、抖动缓冲水位、溢出计数。
**溢出持续增长说明网络抖动超过缓冲，可以调大 `Program.MaxQueuedPackets`。**

### 防火墙

手机发来的是**入站 UDP**，Windows 防火墙默认会丢弃。需要放行（管理员权限）：

```powershell
New-NetFirewallRule -DisplayName 'SNAD In (UDP 6981)' -Direction Inbound `
  -Protocol UDP -LocalPort 6981 -RemoteAddress 192.168.0.0/16 -Action Allow -Profile Any
```

`RemoteAddress` 限定私有网段，不要对全网开放。

---

## 手机端

APK 由 GitHub Actions 云构建（本机磁盘空间不足以安装 Android SDK）。
见 `.github/workflows/build-apk.yml`，推送后自动编译并发布到 Release。

### 使用

1. 手机开启**个人热点**，电脑连上（这样两台设备间没有第三方策略干扰）
2. 打开 SNAD，填电脑地址与端口（默认 6981）
3. 点「开始串流」，在系统弹窗中授权录屏/录制音频
4. 切到音乐 App 播放

### 注意事项

- **必须用热点或同一路由器**，校园网/公司网通常做客户端隔离，手机发不到电脑
- 部分 App 设置 `ALLOW_CAPTURE_BY_NONE`（多为 DRM 保护的流媒体），其音频无法被捕获，
  这是 Android 的硬限制，非 root 方案都绕不过
- Android 13+ 需要通知权限；建议把应用加入电池优化白名单，否则后台会被杀
- 手机热点建议开 **5 GHz**：2.4 GHz 与蓝牙共用频段，干扰更重

---

## 已知限制

- **延迟**：热点环境实测网关 RTT 5–8 ms，加三缓冲 120 ms，预期端到端 50–150 ms。
  适合听音乐、看视频（画面在手机上），不适合音游或实时监听。
- **只能单向**：手机 → 电脑。麦克风不走这条链路。
- **首次运行可能需调试**：`AudioPlaybackCapture` 在各厂商 ROM 上行为有差异。

---

## 开发记录

调试过程中解决的三个实际问题，记录备查：

1. **`waveOutWrite` 返回 34（`MMSYSERR_INVALPARAM`）**
   原因是在每次写入前手工清除 `WAVEHDR.dwFlags` 的 `WHDR_DONE` 位，当缓冲仍被驱动持有时
   会破坏状态机。改为**只读判断完成位、由驱动自行清除**，并预填充三个缓冲后解决。
   实测连续 250 次写入零失败。

2. **接收器输出看不到统计**
   .NET 的 `Console.Out` 在管道重定向时是块缓冲。改用 `AutoFlush = true` 的
   `StreamWriter` 包装标准输出。

3. **WSL 发的测试包到不了 Windows**
   WSL 网段是 `172.27.192.0/20`，不在防火墙规则允许的 `172.16.0.0/12` 范围内。
   测试时改用 Windows 本机 PowerShell 发送到 `127.0.0.1` 绕开防火墙，把
   "网络可达性"与"接收器逻辑"两个问题分开定位。
