package com.dicaeopolis.snad

import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import java.io.ByteArrayOutputStream
import java.security.MessageDigest

/**
 * SNAD 局域网信标协议（与电脑端 SnadBeacon.cs 逐字节对应）。
 *
 * 无感：密钥在构建时注入，运行期没有任何配对步骤，收到合法信标即可接入。
 * 受限：局域网内其他设备没有预共享密钥，算不出合法 HMAC。
 * 免认证：每次接入是一次 HMAC 校验，不依赖任何服务器下发身份。
 *
 * 报文（小端序，固定头 40 字节 + 可变 device_id）：
 *   offset size  field
 *   0      8     magic "SNADBEAC"
 *   8      1     version = 1
 *   9      1     idLen
 *   10     4     seq
 *   14     8     issuedAt
 *   22     18    reserved
 *   40     idLen device_id (UTF-8)
 *   之后   4     port
 *   之后   32    hmac = HMAC-SHA256(derivedKey, 前面全部字节)
 *
 * 密钥派生：derivedKey = HMAC-SHA256(PSK, "snad-beacon-v1")
 * KeyId  ：SHA256(PSK) 前 8 字节的大写十六进制
 *
 * 两端若派生规则有半点不同，KeyId 相同也会 HMAC 失败。因此这里的实现
 * 刻意与 C# 侧保持同一顺序、同一编码，并在日志里打印 KeyId 以便比对。
 */
object SnadBeacon {

    private val MAGIC = byteArrayOf(
        'S'.code.toByte(), 'N'.code.toByte(), 'A'.code.toByte(), 'D'.code.toByte(),
        'B'.code.toByte(), 'E'.code.toByte(), 'A'.code.toByte(), 'C'.code.toByte()
    )

    const val VERSION: Byte = 1
    const val FIXED_HEADER_SIZE = 40
    const val HMAC_SIZE = 32
    const val DEFAULT_PORT = 6983

    private const val DERIVE_INFO = "snad-beacon-v1"

    /** 电脑端信标的解析结果。 */
    data class Peer(
        val deviceId: String,
        val address: String,
        val port: Int,
        val seq: Long,
        val issuedAt: Long
    )

    /** 由预共享密钥派生信标专用密钥。规则必须与 C# 端一致。 */
    fun deriveKey(psk: String): ByteArray {
        require(psk.isNotEmpty()) { "PSK 为空：构建时未注入密钥" }
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(psk.toByteArray(Charsets.US_ASCII), "HmacSHA256"))
        return mac.doFinal(DERIVE_INFO.toByteArray(Charsets.US_ASCII))
    }

    /** KeyId：SHA256(PSK) 前 8 字节的大写十六进制。用于确认两端密钥是否一致。 */
    fun computeKeyId(psk: String): String {
        if (psk.isEmpty()) return "无密钥"
        val d = MessageDigest.getInstance("SHA-256").digest(psk.toByteArray(Charsets.US_ASCII))
        val sb = StringBuilder(16)
        for (i in 0 until 8) sb.append(String.format("%02X", d[i]))
        return sb.toString()
    }

    /**
     * 校验并解析电脑端广播的信标。
     * 任何一步不通过都返回 null 并给出原因——便于区分"密钥不一致"与"不是本协议的包"。
     *
     * @param sourceAddress 由调用方从 UDP 源地址传入，不取自报文内容
     */
    fun parse(data: ByteArray, length: Int, key: ByteArray, sourceAddress: String): Result<Peer> {
        if (length < FIXED_HEADER_SIZE + 4 + HMAC_SIZE) {
            return Result.failure(IllegalArgumentException("报文过短（$length 字节）"))
        }
        for (i in MAGIC.indices) {
            if (data[i] != MAGIC[i]) return Result.failure(IllegalArgumentException("magic 不匹配"))
        }
        if (data[8] != VERSION) {
            return Result.failure(IllegalArgumentException("版本不符：${data[8]}"))
        }

        val idLen = data[9].toInt() and 0xFF
        val need = FIXED_HEADER_SIZE + idLen + 4 + HMAC_SIZE
        if (length < need) return Result.failure(IllegalArgumentException("长度与 idLen 不符"))

        // 先验 HMAC，再解释内容：不对未认证数据做任何解析
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        mac.update(data, 0, need - HMAC_SIZE)
        val expect = mac.doFinal()

        var diff = 0
        val off = need - HMAC_SIZE
        for (i in 0 until HMAC_SIZE) diff = diff or (expect[i].toInt() xor data[off + i].toInt())
        if (diff != 0) {
            return Result.failure(SecurityException("HMAC 校验失败（密钥不一致或报文被改）"))
        }

        val deviceId = String(data, FIXED_HEADER_SIZE, idLen, Charsets.UTF_8)
        val port = getInt32LE(data, FIXED_HEADER_SIZE + idLen)
        val seq = getInt32LE(data, 10).toLong() and 0xFFFFFFFFL
        val issuedAt = getInt64LE(data, 14)

        if (port <= 0 || port > 65535) {
            return Result.failure(IllegalArgumentException("端口非法：$port"))
        }
        return Result.success(Peer(deviceId, sourceAddress, port, seq, issuedAt))
    }

    /**
     * 防重放：同一 deviceId 的 seq 必须严格递增。
     * 电脑端每秒广播数次，网络乱序也会导致旧 seq 到达，两种情况一并丢弃。
     */
    class ReplayGuard {
        private val last = HashMap<String, Long>()

        @Synchronized
        fun accept(deviceId: String, seq: Long): Boolean {
            val prev = last[deviceId]
            if (prev != null && seq <= prev) return false
            last[deviceId] = seq
            return true
        }

        @Synchronized
        fun lastSeq(deviceId: String): Long = last[deviceId] ?: -1L
    }

    /** 仅用于自我测试与调试：组包（手机端一般只收不发）。 */
    fun build(deviceId: String, port: Int, seq: Long, key: ByteArray): ByteArray {
        val idBytes = deviceId.toByteArray(Charsets.UTF_8)
        require(idBytes.size <= 255) { "device_id 过长" }

        val total = FIXED_HEADER_SIZE + idBytes.size + 4 + HMAC_SIZE
        val out = ByteArrayOutputStream(total)
        out.write(MAGIC)
        out.write(VERSION.toInt())
        out.write(idBytes.size)
        out.write(int32LE(seq.toInt()))
        out.write(int64LE(System.currentTimeMillis()))
        out.write(ByteArray(18))
        out.write(idBytes)
        out.write(int32LE(port))

        val body = out.toByteArray()
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        val sig = mac.doFinal(body)
        return body + sig
    }

    private fun int32LE(v: Int) = byteArrayOf(
        (v and 0xFF).toByte(),
        ((v ushr 8) and 0xFF).toByte(),
        ((v ushr 16) and 0xFF).toByte(),
        ((v ushr 24) and 0xFF).toByte()
    )

    private fun int64LE(v: Long) = ByteArray(8) { i -> ((v ushr (8 * i)) and 0xFF).toByte() }

    private fun getInt32LE(b: ByteArray, off: Int): Int =
        (b[off].toInt() and 0xFF) or
        ((b[off + 1].toInt() and 0xFF) shl 8) or
        ((b[off + 2].toInt() and 0xFF) shl 16) or
        ((b[off + 3].toInt() and 0xFF) shl 24)

    private fun getInt64LE(b: ByteArray, off: Int): Long {
        var v = 0L
        for (i in 0 until 8) v = v or ((b[off + i].toLong() and 0xFF) shl (8 * i))
        return v
    }
}
