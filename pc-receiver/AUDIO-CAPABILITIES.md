# 音频设备能力探测结果

**探测工具**：`pc-receiver/SnadAudioProbe.exe`
**探测时间**：2026-10-17
**机器**：本机（Windows 11 Pro 25H2 build 26200，Realtek 板载 + Voicemeeter 虚拟设备）

---

## 结论摘要

| 设备 | 默认周期 | 最小周期 | 混音格式 | 独占模式 |
|---|---|---|---|---|
| 扬声器 (Realtek(R) Audio) | 10.00 ms | 2.00 ms | 48000 / 2ch / 32bit extensible | **可用：pcm16 / pcm24** |
| 耳机 (Realtek(R) Audio) | 10.00 ms | 2.00 ms | 48000 / 2ch / 32bit extensible | **可用：pcm16 / pcm24** |
| Voicemeeter Input | 10.00 ms | 2.00 ms | 48000 / 2ch / 32bit extensible | 不可用 |
| Voicemeeter VAIO3 Input | 10.00 ms | 2.00 ms | 同上 | 不可用 |
| Voicemeeter In 1 / 2 / 3 / 4 / 5 | 10.00 ms | 2.00 ms | 同上 | 不可用 |
| Voicemeeter AUX Input | 10.00 ms | 2.00 ms | 同上 | 不可用 |

Voicemeeter 设备在独占模式下对 pcm16 / pcm24 / pcm32 / float32 **全部返回
`0x88890008`（AUDCLNT_E_UNSUPPORTED_FORMAT）**。

---

## 这个结果对计划的影响

**这是决定性的约束，而且无法靠推理得出：**

- **走 Voicemeeter ⇒ 只能用共享模式** ⇒ 拿不到独占能提供的小周期
- **直连物理声卡 ⇒ 独占可用**（pcm16 / pcm24）⇒ 延迟量级才有变化的空间

因此 profile 里 `backend` 与 `device` **必须联动约束**：
编辑器不允许出现"Voicemeeter + 独占"这种跑不起来的组合。

这也印证了"设备进 profile"的必要性——"走 Voicemeeter 的档位"与
"直连声卡求低延迟的档位"是两套完全不同的方案，应当并存可切换，
而不是去改系统默认设备。

---

## 附带发现

- 物理设备的**最小周期是 2.00 ms**（默认 10.00 ms）。这意味着独占模式下
  周期可以远小于当前 `waveOut` 的实践下限（16 ms 块）——低延迟的空间在这里。
- 混音格式是 **32bit extensible**，说明设备原生工作在更高位深；
  现在协议传 PCM16 是**人为的瓶颈**，不是硬件限制。
- `IAudioClient` 的所有方法必须标 `[PreserveSig]`，否则失败时运行时会抛
  `COMException: 未实现`，掩盖真实 HRESULT，导致无从判断是哪个参数不合法。
  这一点在写代码时踩过（见 `SnadAudioProbe.cs` 的注释）。

---

## 如何复现

```powershell
cd pc-receiver
.\SnadAudioProbe.exe          # 列出设备与关键结论
.\SnadAudioProbe.exe 详细      # 打印每个设备的完整探测过程
```
