package com.dicaeopolis.snad

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketException
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * 监听电脑端广播的 SNAD 信标，自动发现接收端地址。
 *
 * 这是"无感"的实现：手机不需要知道电脑 IP，也不必手动填写；
 * 电脑每 2 秒广播一次，手机收到合法信标即可自动接入。
 *
 * 安全边界（与电脑端约定一致）：
 *   - 密钥在构建时注入，运行期没有配对流程
 *   - 局域网内其他设备即使抓到广播，没有密钥就算不出合法 HMAC，一律丢弃
 *   - seq 严格递增，旧报文与乱序报文都被丢弃
 *
 * 使用方式：start() 后通过 onPeer 回调获得对端；stop() 结束线程。
 */
class SnadBeaconListener(
    private val onPeer: (SnadBeacon.Peer) -> Unit,
    private val onLog: (String) -> Unit
) {
    companion object {
        private const val TAG = "BeaconRx"
        private const val SOCKET_TIMEOUT_MS = 1000
        private const val MAX_PACKET = 2048
    }

    private val running = AtomicBoolean(false)
    private var thread: Thread? = null
    private val guard = SnadBeacon.ReplayGuard()

    private val acceptedCount = AtomicLong(0)
    private val rejectedCount = AtomicLong(0)

    val accepted: Long get() = acceptedCount.get()
    val rejected: Long get() = rejectedCount.get()

    /** 上一次成功解析出的对端，便于界面在未收到新信标时仍能显示。 */
    @Volatile
    var lastPeer: SnadBeacon.Peer? = null
        private set

    fun start() {
        if (running.getAndSet(true)) return
        val t = Thread({ loop() }, "snad-beacon-listen")
        t.isDaemon = true
        t.start()
        thread = t
        onLog("信标监听已启动，端口 ${SnadBeacon.DEFAULT_PORT}")
    }

    fun stop() {
        if (!running.getAndSet(false)) return
        // 关闭动作由 loop 内的超时轮询兜底：每秒检查一次 running，
        // 因此这里不必强关 socket（强关会抛异常且容易与 loop 竞争）。
        val t = thread
        if (t != null && t.isAlive) {
            try { t.join(2000) } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
                onLog("等待监听线程退出被中断")
            }
        }
        thread = null
    }

    private fun loop() {
        var socket: DatagramSocket? = null
        try {
            // 密钥必须存在。缺少时明确失败并停止，绝不降级为"不校验"——
            // 那会让同网段任何设备都能伪装成电脑。
            val psk = SnadSecret.PSK
            if (psk.isEmpty()) {
                onLog("错误：本 APK 未注入预共享密钥，无法安全识别电脑端。" +
                      "请使用正确配置密钥构建的版本（见 README 的密钥注入说明）。")
                return
            }
            val key = SnadBeacon.deriveKey(psk)
            onLog("密钥就绪，KeyId=${SnadSecret.KEY_ID}（应与电脑端一致）")

            socket = DatagramSocket(null)
            socket.reuseAddress = true
            socket.broadcast = true
            socket.soTimeout = SOCKET_TIMEOUT_MS
            socket.bind(InetSocketAddress(SnadBeacon.DEFAULT_PORT))

            val buf = ByteArray(MAX_PACKET)
            while (running.get()) {
                val packet = DatagramPacket(buf, buf.size)
                try {
                    socket.receive(packet)
                } catch (e: SocketTimeoutException) {
                    continue          // 正常：用于周期性检查 running
                } catch (e: SocketException) {
                    if (running.get()) {
                        onLog("信标接收异常，继续：${e.javaClass.simpleName}: ${e.message}")
                        continue
                    }
                    break
                }

                val src = packet.address?.hostAddress ?: "?"
                val result = SnadBeacon.parse(packet.data, packet.length, key, src)

                result.fold(
                    onSuccess = { peer ->
                        if (!guard.accept(peer.deviceId, peer.seq)) {
                            rejectedCount.incrementAndGet()
                            onLog("丢弃重放/乱序信标：device=${peer.deviceId} seq=${peer.seq}" +
                                  "（已见 ${guard.lastSeq(peer.deviceId)}）")
                        } else {
                            acceptedCount.incrementAndGet()
                            val changed = lastPeer?.address != peer.address || lastPeer?.port != peer.port
                            lastPeer = peer
                            if (changed) {
                                onLog("发现电脑：${peer.deviceId} @ ${peer.address}:${peer.port}")
                            }
                            onPeer(peer)
                        }
                    },
                    onFailure = { err ->
                        rejectedCount.incrementAndGet()
                        // 只在前 3 次与每 50 次记录，避免同网段无关广播刷屏；
                        // 但不静默——密钥不一致时必须能从日志看出来。
                        val n = rejectedCount.get()
                        if (n <= 3 || n % 50 == 0L) {
                            onLog("丢弃信标（来自 $src）：${err.message}")
                        }
                    }
                )
            }
        } catch (e: Throwable) {
            onLog("信标监听线程异常退出：${e.javaClass.simpleName}: ${e.message}")
        } finally {
            try { socket?.close() } catch (e: Throwable) {
                onLog("关闭监听 socket 异常：${e.message}")
            }
            onLog("信标监听已停止（接受 $accepted，丢弃 $rejected）")
        }
    }
}
