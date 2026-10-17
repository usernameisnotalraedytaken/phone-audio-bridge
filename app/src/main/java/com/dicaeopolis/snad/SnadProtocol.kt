package com.dicaeopolis.snad

/**
 * SNAD —— 手机音频桥的自定义传输协议。
 *
 * 设计目标：零编码。手机抓到的 PCM 直接封包经 UDP 送出，电脑端原样播放，
 * 因此音质瓶颈只取决于抓取环节（Android 的 AudioPlaybackCapture 输出 16bit PCM），
 * 不存在 SBC/AAC 那种有损二次编码。
 *
 * 包头固定 20 字节，小端序：
 *
 *   offset  size  field
 *   0       4     magic  'S','N','A','D'
 *   4       1     version = 1
 *   5       1     channels
 *   6       4     sampleRate
 *   10      4     sequence   (uint32，用于检测丢包/乱序)
 *   14      4     timestampMs (uint32，手机侧单调时钟低 32 位)
 *   18      4     payloadBytes
 *   22      N     PCM16LE 交错数据
 */
object SnadProtocol {
    val MAGIC = byteArrayOf('S'.code.toByte(), 'N'.code.toByte(), 'A'.code.toByte(), 'D'.code.toByte())
    const val VERSION: Byte = 1
    const val HEADER_SIZE = 22
    const val DEFAULT_PORT = 6981   // 避开电脑上 Voicemeeter 的 VBAN 服务（占用 6980）

    /**
     * 每个 UDP 包携带的 PCM 字节数。
     * 4096 字节 = 1024 帧 @ 立体声 16bit，48kHz 下约 21.3 ms —— 折中：包不太多，
     * 丢一个包也不会造成明显爆音。
     */
    const val PAYLOAD_BYTES = 4096

    // ---- 文本日志包（type = 'T'），发往 SnadLog.LOG_PORT，负载为 UTF-8 文本 ----
    const val TYPE_AUDIO: Byte = 'A'.code.toByte()
    const val TYPE_TEXT: Byte = 'T'.code.toByte()
    const val TEXT_HEADER_SIZE = 14

    fun buildHeader(channels: Int, sampleRate: Int, sequence: Int, timestampMs: Int, payloadBytes: Int): ByteArray {
        val h = ByteArray(HEADER_SIZE)
        System.arraycopy(MAGIC, 0, h, 0, 4)
        h[4] = VERSION
        h[5] = channels.toByte()
        putIntLE(h, 6, sampleRate)
        putIntLE(h, 10, sequence)
        putIntLE(h, 14, timestampMs)
        putIntLE(h, 18, payloadBytes)
        return h
    }

    /**
     * 文本包固定 14 字节头（与音频包头不同，用 type 字段区分）：
     *   0..3   magic "SNAD"
     *   4      version
     *   5      type = 'T'
     *   6..9   sequence
     *   10..13 timestampMs
     * 之后为 UTF-8 文本。电脑端落成 phone.log，无需 adb 即可直接阅读。
     */
    fun buildTextHeader(sequence: Int, timestampMs: Long, payloadBytes: Int): ByteArray {
        val h = ByteArray(TEXT_HEADER_SIZE)
        System.arraycopy(MAGIC, 0, h, 0, 4)
        h[4] = VERSION
        h[5] = TYPE_TEXT
        putIntLE(h, 6, sequence)
        putIntLE(h, 10, (timestampMs and 0xFFFFFFFFL).toInt())
        return h
    }

    private fun putIntLE(b: ByteArray, off: Int, v: Int) {
        b[off] = (v and 0xFF).toByte()
        b[off + 1] = ((v ushr 8) and 0xFF).toByte()
        b[off + 2] = ((v ushr 16) and 0xFF).toByte()
        b[off + 3] = ((v ushr 24) and 0xFF).toByte()
    }

    // ---- 能力上报（type = 'C'，14 字节，发往日志端口）----
    //
    // 用途：让电脑端把"手机采集延迟"从猜测变成已知。
    // 电脑端估算里的 capture 固定按 8 ms（典型值）；上报之后可以用实测值。
    //
    //   0..3  magic
    //   4     version
    //   5     type = 'C'
    //   6..9  captureRate（u32，采集采样率）
    //   10..11 captureBufferMs（u16，采集缓冲）
    //   12    formatMask（u8）：bit0 pcm16 / bit1 pcm24 / bit2 pcm32 / bit3 float32
    //   13    保留
    const val TYPE_CAPABILITY: Byte = 'C'.code.toByte()

    fun buildCapability(captureRate: Int, captureBufferMs: Int, formatMask: Int): ByteArray {
        val h = ByteArray(TEXT_HEADER_SIZE)
        System.arraycopy(MAGIC, 0, h, 0, 4)
        h[4] = VERSION
        h[5] = TYPE_CAPABILITY
        putIntLE(h, 6, captureRate)
        putIntLE(h, 10, captureBufferMs and 0xFFFF)
        h[12] = (formatMask and 0xFF).toByte()
        h[13] = 0
        return h
    }
}
