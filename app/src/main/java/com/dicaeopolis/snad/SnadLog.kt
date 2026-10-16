package com.dicaeopolis.snad

import android.content.Context
import android.content.Intent
import android.util.Log
import java.io.File
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 统一日志出口。设计目标：出了问题能在电脑上直接读到原因，不需要 adb、不需要 root。
 *
 * 一次调用同时送往四个地方：
 *   1. Android logcat      —— 有 adb 时可用
 *   2. 界面状态栏          —— 用户立刻看到
 *   3. 应用私有日志文件     —— 断网也能事后回看
 *   4. UDP 文本包到电脑     —— 电脑端落地成 phone.log，直接可读（核心手段）
 *
 * 刻意不做静默失败：任何异常都会被记录为带类型与消息的 ERROR 行。
 */
object SnadLog {

    private const val TAG = "SNAD"

    /** 日志包与音频包分端口发送，互不干扰。 */
    const val LOG_PORT = 6982
    private const val MAX_TEXT = 1100          // 单包留足余量，避免超过 MTU 被分片

    /** 内存中的最近日志，界面可以随时回看。 */
    private const val RING = 500
    private val ring = ArrayDeque<String>(RING)

    private val stamp = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    @Volatile private var ctx: Context? = null
    @Volatile private var host: InetAddress? = null
    @Volatile private var port: Int = LOG_PORT
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var seq: Int = 0
    @Volatile private var tcpSendFailures: Int = 0

    /** 界面回调。Service 与 Activity 运行在同进程，直接持有引用即可。 */
    @Volatile var uiSink: ((String) -> Unit)? = null

    fun init(context: Context) {
        ctx = context.applicationContext
    }

    fun setTarget(host: String, port: Int) {
        this.port = port
        try {
            this.host = InetAddress.getByName(host)
            socket?.close()
            socket = try {
                DatagramSocket()
            } catch (t: Throwable) {
                error("SnadLog", "日志 socket 创建失败", t)
                null
            }
        } catch (t: Throwable) {
            error("SnadLog", "日志目标地址解析失败 host=$host", t)
            this.host = null
        }
    }

    fun close() {
        try { socket?.close() } catch (t: Throwable) { /* 关闭失败无需上报，但要留痕 */ warn("SnadLog", "close 异常: ${t.javaClass.simpleName}: ${t.message}") }
        socket = null
    }

    fun i(tag: String, msg: String) = emit("INFO ", tag, msg, null)
    fun w(tag: String, msg: String) = emit("WARN ", tag, msg, null)
    fun e(tag: String, msg: String, t: Throwable? = null) = emit("ERROR", tag, msg, t)

    /** 记录一次失败但不抛出的操作，把"被吞掉的异常"变成可见记录。 */
    fun soft(tag: String, what: String, t: Throwable) {
        emit("WARN ", tag, "$what 失败：${t.javaClass.name}: ${t.message}", t)
    }

    fun recent(): List<String> = synchronized(ring) { ring.toList() }

    // ------------------------------------------------------------------ 内部

    private fun emit(level: String, tag: String, msg: String, t: Throwable?) {
        val head = "${stamp.format(Date())} $level [$tag] $msg"
        val line = if (t == null) head else "$head\n    ${describe(t)}"

        // 1. logcat
        when (level) {
            "ERROR" -> Log.e(TAG, head, t)
            "WARN " -> Log.w(TAG, head, t)
            else -> Log.i(TAG, head)
        }

        // 2. 内存 + 界面
        synchronized(ring) {
            ring.addLast(line)
            while (ring.size > RING) ring.removeFirst()
        }
        uiSink?.invoke(line)

        // 3. 文件
        appendToFile(line)

        // 4. 传到电脑
        sendUdp(level, tag, line)
    }

    private fun describe(t: Throwable): String {
        val sb = StringBuilder()
        sb.append(t.javaClass.name)
        sb.append(": ").append(t.message ?: "(无消息)")
        t.stackTrace.take(6).forEach { sb.append("\n      at ").append(it.toString()) }
        val cause = t.cause
        if (cause != null && cause !== t) {
            sb.append("\n    caused by ").append(cause.javaClass.name).append(": ").append(cause.message ?: "(无消息)")
        }
        return sb.toString()
    }

    private fun appendToFile(line: String) {
        val c = ctx ?: return
        try {
            val f = File(c.filesDir, "snad.log")
            // 简单轮转：超过 512KB 就截掉前半
            if (f.exists() && f.length() > 512 * 1024) {
                val keep = f.readLines().takeLast(500)
                f.writeText(keep.joinToString("\n") + "\n")
            }
            f.appendText(line + "\n")
        } catch (t: Throwable) {
            // 这里再失败就只上 logcat，避免递归写日志
            Log.e(TAG, "写日志文件失败: ${t.javaClass.name}: ${t.message}")
        }
    }

    private fun sendUdp(level: String, tag: String, line: String) {
        val h = host ?: return
        val s = socket ?: return
        try {
            val body = "[$level] [$tag] $line"
            val chunks = body.chunked(MAX_TEXT)
            for (c in chunks) {
                val payload = c.toByteArray(Charsets.UTF_8)
                val hdr = SnadProtocol.buildTextHeader(seq++, System.currentTimeMillis(), payload.size)
                val out = ByteArray(hdr.size + payload.size)
                System.arraycopy(hdr, 0, out, 0, hdr.size)
                System.arraycopy(payload, 0, out, hdr.size, payload.size)
                s.send(DatagramPacket(out, out.size, h, port))
            }
        } catch (t: Throwable) {
            // 日志通道失败不能影响主流程，但必须留痕（有次数上限，避免刷屏）
            tcpSendFailures++
            if (tcpSendFailures <= 5 || tcpSendFailures % 100 == 0) {
                Log.e(TAG, "发送日志到电脑失败(第 $tcpSendFailures 次): ${t.javaClass.name}: ${t.message}")
            }
        }
    }
}
