package com.dicaeopolis.snad

import android.os.SystemClock
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * 端到端延迟的时钟基准（与电脑端 SnadTimeSync.cs 逐字节对应）。
 *
 * 画面：电脑不知道手机时钟的零点，手机也不知道电脑的，因此单程无法区分
 * "网络慢"与"时钟差"。只有往返能消掉单程时延：
 *
 *     RTT = (T4 − T1) − (S − R)
 *     Δ   ≈ T1 + RTT/2 − R
 *
 * 手机侧只负责"收到探测 → 立刻回显两个时刻"。
 *
 * 错误一律通过 Result 返回，绝不在无回复、HMAC 不符时返回一个假的时间。
 */
object SnadTimeSync {
    val MAGIC = "SNADBEAC".toByteArray(Charsets.US_ASCII)
    const val VERSION: Byte = 1
    const val TYPE_PROBE: Byte = 'T'.code.toByte()
    const val TYPE_REPLY: Byte = 't'.code.toByte()

    const val PROBE_SIZE = 18 + 32
    const val REPLY_SIZE = 26 + 32
    private const val HMAC_SIZE = 32

    private val INFO = "snad-timesync-v1".toByteArray(Charsets.US_ASCII)

    /** 由预共享密钥派生时间同步专用密钥（与信标密钥相互独立）。 */
    fun deriveKey(psk: String): ByteArray {
        require(psk.isNotEmpty()) { "PSK 为空：构建时未注入密钥" }
        val sha = MessageDigest.getInstance("SHA-256")
        sha.update(psk.toByteArray(Charsets.US_ASCII))
        val base = sha.digest()
        sha.reset()
        sha.update(base)
        sha.update(INFO)
        return sha.digest()
    }

    /** 手机单调时钟（ms，低 32 位）。与音频包里的时间戳同源，可直接相减。 */
    fun nowMs(): Long = SystemClock.elapsedRealtime() and 0xFFFFFFFFL

    /**
     * 解析探测包并组回复。
     *
     * 返回 Result.success(回复字节)，失败时 Result.failure 带上**具体原因**：
     * 长度、magic、版本、类型、HMAC 分别报，便于在 phone.log 里定位，
     * 而不是笼统地说"同步失败"。
     */
    fun replyTo(data: ByteArray, len: Int, key: ByteArray): Result<ByteArray> {
        if (len < PROBE_SIZE) return Result.failure(IllegalArgumentException("探测包过短：$len 字节"))
        for (i in MAGIC.indices) {
            if (data[i] != MAGIC[i]) return Result.failure(IllegalArgumentException("magic 不符"))
        }
        if (data[8] != VERSION) return Result.failure(IllegalArgumentException("版本不符：${data[8]}"))
        if (data[9] != TYPE_PROBE) return Result.failure(IllegalArgumentException("类型不是探测包：${data[9]}"))

        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        mac.update(data, 0, 18)
        val expect = mac.doFinal()

        var diff = 0
        for (i in 0 until HMAC_SIZE) diff = diff or (expect[i].toInt() xor data[18 + i].toInt())
        if (diff != 0) return Result.failure(SecurityException("HMAC 校验失败（密钥不一致或报文被改）"))

        val recvMs = nowMs()
        val nonce = getInt32LE(data, 10)
        val t1 = getInt32LE(data, 14)

        val out = ByteArrayOutputStream(REPLY_SIZE)
        out.write(MAGIC)
        out.write(VERSION.toInt())
        out.write(TYPE_REPLY.toInt())
        out.write(int32LE(nonce))
        out.write(int32LE(t1))
        out.write(int32LE(recvMs.toInt()))
        // sendMs 必须取在**写入之前**这一瞬间：组包耗时会被 PC 侧从 RTT 里扣掉，
        // 若这里用旧时刻，扣掉的就不是真实处理时间，Δ 会跟着偏。
        val sendMs = nowMs()
        out.write(int32LE(sendMs.toInt()))

        val body = out.toByteArray()
        val mac2 = Mac.getInstance("HmacSHA256")
        mac2.init(SecretKeySpec(key, "HmacSHA256"))
        return Result.success(body + mac2.doFinal(body))
    }

    private fun int32LE(v: Int) = byteArrayOf(
        (v and 0xFF).toByte(),
        ((v ushr 8) and 0xFF).toByte(),
        ((v ushr 16) and 0xFF).toByte(),
        ((v ushr 24) and 0xFF).toByte()
    )

    private fun getInt32LE(b: ByteArray, off: Int): Int =
        (b[off].toInt() and 0xFF) or
        ((b[off + 1].toInt() and 0xFF) shl 8) or
        ((b[off + 2].toInt() and 0xFF) shl 16) or
        ((b[off + 3].toInt() and 0xFF) shl 24)
}
