# 音频设备能力探测结果

**探测工具**：`pc-receiver/SnadAudioProbe.exe`
**探测时间**：2026-10-17
**机器**：本机（Windows 11 Pro 25H2 build 26200，Realtek 板载 + Voicemeeter 虚拟设备）

---

## 结论摘要

| 设备 | 默认周期 | 最小周期 | 混音格式 | 独占模式 |
|---|---|---|---|---|
| 扬声器 (Realtek(R) Audio) | 10.00 ms | 3.00 ms | 48000 / 2ch / 32bit float | **可用：pcm16 / pcm24** |
| 耳机 (Realtek(R) Audio) | 10.00 ms | 3.00 ms | 48000 / 2ch / 32bit float | **可用：pcm16 / pcm24** |
| Voicemeeter Input | 10.00 ms | 2.00 ms | 48000 / 2ch / 32bit float | 可用：pcm16 / pcm24 |
| Voicemeeter VAIO3 Input | 10.00 ms | 2.00 ms | 同上 | 可用：pcm16 / pcm24 |
| Voicemeeter In 1 / 2 / 3 / 4 / 5 | 10.00 ms | 2.00 ms | 同上 | 可用：pcm16 / pcm24 |
| Voicemeeter AUX Input | 10.00 ms | 2.00 ms | 同上 | 可用：pcm16 / pcm24 |

所有设备**都不支持** pcm32 与 float32 的独占模式，只接受 pcm16 / pcm24。

**订正说明**：本文件早先记录"Voicemeeter 设备一律不支持独占（0x88890008）"，
那是**测量工具本身有缺陷**导致的误判——旧工具构造的是**非扩展** WAVEFORMATEX，
而设备要求 `WAVEFORMATEXTENSIBLE`（带 `SubFormat`）。
改用正确格式后，Voicemeeter 设备同样支持独占 pcm16 / pcm24。

由此得到一条通用教训：**能力探测的错误会伪装成设备限制**。
看到"设备不支持"时，先怀疑自己的探测方式。

---

## 这个结果对计划的影响

**独占模式所有设备都可用**，因此"走 Voicemeeter"与"直连物理声卡"不再是
"能不能用独占"的区别，而是**两个独立的可选维度**：

| | 走 Voicemeeter | 直连物理声卡 |
|---|---|---|
| 共享模式 | 可用 | 可用 |
| 独占模式 | 可用（pcm16/pcm24） | 可用（pcm16/pcm24） |
| 最小周期 | 2.00 ms | 3.00 ms |
| 音频能否被其他程序处理 | 能（这正是 Voicemeeter 的用途） | 不能（独占会占住设备） |

因此 profile 里 `device` 与 `backend` 是**正交的两维**：
"直连声卡求低延迟"与"经 Voicemeeter 便于录制/混音"应当并存可切换，
而不是去改系统默认设备。

**注意**：独占会占住设备，其他程序在此期间无法使用它。
若还需要 Voicemeeter 的路由能力，就不该选独占——这是使用取舍，不是技术限制。

---

## 附带发现

- 混音格式是 **32bit float**，说明设备原生工作在更高精度；
  现在协议传 PCM16 是**人为的瓶颈**，不是硬件限制。
  但独占模式只接受 pcm16 / pcm24（不接受 float32），
  所以走独占的话 24bit 是能拿到的最高精度。
- `IAudioClient` 的所有方法必须标 `[PreserveSig]`，否则失败时运行时会抛
  `COMException: 未实现`，掩盖真实 HRESULT，导致无从判断是哪个参数不合法。
- 独占模式**不接受 `AUDCLNT_STREAMFLAGS_EVENTCALLBACK`**，传了必然失败
  （`0x88890013` 或 `0x88890016`）。详见 `LATENCY-MEASURED.md`。

---

## 如何复现

```powershell
cd pc-receiver
.\SnadAudioProbe.exe          # 列出设备与关键结论
.\SnadAudioProbe.exe 详细      # 打印每个设备的完整探测过程
```
