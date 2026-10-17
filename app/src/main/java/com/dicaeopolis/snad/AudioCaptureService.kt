package com.dicaeopolis.snad

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.SystemClock
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.max

/**
 * 前台服务：抓取设备内部音频，封装为 SNAD 包经 UDP 发出。
 *
 * 为什么必须是前台服务：Android 10+ 的 MediaProjection 在应用退到后台后会被系统回收，
 * 而用户显然会切到音乐 App 去放歌，所以后台存活是硬需求。
 *
 * 错误处理原则：不静默。任何一步失败都会把异常类型、消息、堆栈经 SnadLog 送出，
 * 电脑端 phone.log 里能看到完整原因。
 */
class AudioCaptureService : Service() {

    companion object {
        private const val TAG = "Service"
        private const val CHANNEL_ID = "snad_stream"
        private const val NOTIF_ID = 4201

        const val ACTION_START = "com.dicaeopolis.snad.START"
        const val ACTION_STOP = "com.dicaeopolis.snad.STOP"
        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_STATUS = "status"
        const val EXTRA_DETAIL = "detail"

        const val BROADCAST_STATUS = "com.dicaeopolis.snad.STATUS"

        /** 界面用来启动服务，避免各处重复拼 Intent。 */
        fun startIntent(ctx: Context, resultCode: Int, data: Intent, host: String, port: Int): Intent =
            Intent(ctx, AudioCaptureService::class.java)
                .setAction(ACTION_START)
                .putExtra(EXTRA_RESULT_CODE, resultCode)
                .putExtra(EXTRA_RESULT_DATA, data)
                .putExtra(EXTRA_HOST, host)
                .putExtra(EXTRA_PORT, port)

        fun stopIntent(ctx: Context): Intent =
            Intent(ctx, AudioCaptureService::class.java).setAction(ACTION_STOP)
    }

    private val running = AtomicBoolean(false)
    private var thread: HandlerThread? = null
    private var worker: Handler? = null
    private var projection: MediaProjection? = null
    private var record: AudioRecord? = null
    private var socket: DatagramSocket? = null

    private var sampleRate = 48000
    private var channels = 2

    /** 实际选中的采集编码（pcm16 或 float32），决定包头版本与能力上报。 */
    private var captureEncoding = AudioFormat.ENCODING_PCM_16BIT
    private var seq = 0
    private var bytesSent = 0L
    private var packetsSent = 0L
    private var readErrors = 0L
    private var consecutiveSilence = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        SnadLog.init(this)

        if (intent == null) {
            SnadLog.err(TAG, "onStartCommand 收到 null intent（服务可能被系统重启），不做任何事")
            return START_NOT_STICKY
        }

        SnadLog.info(TAG, "onStartCommand action=${intent.action} startId=$startId")

        when (intent.action) {
            ACTION_STOP -> {
                SnadLog.info(TAG, "收到停止指令")
                stopEverything("用户停止")
                return START_NOT_STICKY
            }
            ACTION_START -> {
                if (running.get()) {
                    SnadLog.wrn(TAG, "服务已在运行，忽略本次重复启动")
                    return START_NOT_STICKY
                }
                val code = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                @Suppress("DEPRECATION")
                val data: Intent? = intent.getParcelableExtra(EXTRA_RESULT_DATA)
                val host = intent.getStringExtra(EXTRA_HOST).orEmpty()
                val port = intent.getIntExtra(EXTRA_PORT, SnadProtocol.DEFAULT_PORT)

                SnadLog.info(TAG, "启动参数 host=$host port=$port resultCode=$code data=${data != null}")

                if (data == null || code == 0) {
                    fail("缺少 MediaProjection 授权数据（resultCode=$code, data=${data != null}）。" +
                            "请重新点击开始串流并允许录屏。")
                    return START_NOT_STICKY
                }
                if (host.isBlank()) {
                    fail("电脑地址为空")
                    return START_NOT_STICKY
                }
                SnadLog.setTarget(host, SnadLog.LOG_PORT)

                startForegroundCompat()
                startCapture(code, data, host, port)
            }
            else -> SnadLog.wrn(TAG, "未知 action=${intent.action}，忽略")
        }
        return START_NOT_STICKY
    }

    // ---------------------------------------------------------------- 前台通知

    private fun startForegroundCompat() {
        try {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val ch = NotificationChannel(CHANNEL_ID, "音频串流", NotificationManager.IMPORTANCE_LOW)
                ch.description = "SNAD 音频桥运行状态"
                ch.setShowBadge(false)
                nm.createNotificationChannel(ch)
            }
            val notif: Notification = Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("SNAD 音频桥")
                .setContentText("正在把手机音频送到电脑")
                .setSmallIcon(android.R.drawable.ic_media_play)
                .setOngoing(true)
                .build()

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                // Android 14 起必须显式声明类型，否则抛 SecurityException
                startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            } else {
                startForeground(NOTIF_ID, notif)
            }
            SnadLog.info(TAG, "前台服务已启动（type=mediaProjection, sdk=${Build.VERSION.SDK_INT}）")
        } catch (t: Throwable) {
            SnadLog.err(TAG, "startForeground 失败，服务无法在后台存活", t)
            fail("启动前台服务失败：${t.javaClass.simpleName}: ${t.message}")
        }
    }

    // ---------------------------------------------------------------- 抓取主流程

    private fun startCapture(resultCode: Int, resultData: Intent, host: String, port: Int) {
        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager

        val proj = try {
            mpm.getMediaProjection(resultCode, resultData)
        } catch (t: Throwable) {
            SnadLog.err(TAG, "getMediaProjection 抛异常", t)
            fail("获取 MediaProjection 失败：${t.javaClass.simpleName}: ${t.message}")
            stopEverything("授权失败")
            return
        }
        if (proj == null) {
            fail("getMediaProjection 返回 null（授权可能已过期，Android 要求每次重新授权）")
            stopEverything("授权无效")
            return
        }
        projection = proj
        SnadLog.info(TAG, "MediaProjection 获取成功")

        // 硬件侧仍需要 VirtualDisplay 才肯出数据；尺寸给最小，我们不要画面。
        try {
            proj.createVirtualDisplay(
                "snad-vd", 1, 1, 1,
                android.hardware.display.DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                null, null, null
            )
            SnadLog.info(TAG, "VirtualDisplay 创建成功（1x1，仅用于满足抓取前提）")
        } catch (t: Throwable) {
            SnadLog.soft(TAG, "createVirtualDisplay（无画面需求，可继续）", t)
        }

        val cfg = try {
            AudioPlaybackCaptureConfiguration.Builder(proj)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()
        } catch (t: Throwable) {
            SnadLog.err(TAG, "构建 AudioPlaybackCaptureConfiguration 失败", t)
            fail("抓取配置失败：${t.javaClass.simpleName}: ${t.message}")
            stopEverything("配置失败")
            return
        }

        // 位深选择（计划 §6）：优先 float32，失败退回 pcm16。
        //
        // 为什么要试：计划要求"不再受 16bit 限制"。抓取侧若能拿到 float32，
        // 整条链路就不必在采集处就把精度砍到 16bit；拿不到则如实退回并上报 pcm16，
        // 不假装支持。
        val candidates = mutableListOf<Int>()
        candidates.add(AudioFormat.ENCODING_PCM_FLOAT)
        candidates.add(AudioFormat.ENCODING_PCM_16BIT)

        var chosenEncoding = AudioFormat.ENCODING_PCM_16BIT
        var arOrNull: AudioRecord? = null
        for (enc in candidates) {
            val minB = AudioRecord.getMinBufferSize(
                sampleRate, AudioFormat.CHANNEL_IN_STEREO, enc
            )
            val bytesPerSample = if (enc == AudioFormat.ENCODING_PCM_FLOAT) 4 else 2
            val buf = max(
                if (minB > 0) minB * 2 else 8192,
                SnadProtocol.PAYLOAD_BYTES * bytesPerSample
            )
            try {
                val cand = AudioRecord.Builder()
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setEncoding(enc)
                            .setSampleRate(sampleRate)
                            .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                            .build()
                    )
                    .setBufferSizeInBytes(buf)
                    .setAudioPlaybackCaptureConfig(cfg)
                    .build()

                if (cand.state == AudioRecord.STATE_INITIALIZED) {
                    arOrNull = cand
                    chosenEncoding = enc
                    break
                }
                SnadLog.wrn(TAG, "编码 ${encodingName(enc)} 不可用（state=${cand.state}），尝试下一种")
                try { cand.release() } catch (t: Throwable) { SnadLog.soft(TAG, "释放候选录音器", t) }
            } catch (t: Throwable) {
                SnadLog.wrn(TAG, "编码 ${encodingName(enc)} 构建失败：${t.javaClass.simpleName}，尝试下一种")
            }
        }

        if (arOrNull == null) {
            fail("创建录音器失败：float32 与 pcm16 都不可用（厂商 ROM 可能限制内部音频抓取）")
            stopEverything("录音器失败")
            return
        }

        val minBuf = AudioRecord.getMinBufferSize(
            sampleRate, AudioFormat.CHANNEL_IN_STEREO, chosenEncoding
        )
        val chosenBytesPerSample = if (chosenEncoding == AudioFormat.ENCODING_PCM_FLOAT) 4 else 2
        val bufSize = max(if (minBuf > 0) minBuf * 2 else 8192,
                          SnadProtocol.PAYLOAD_BYTES * chosenBytesPerSample)
        SnadLog.info(TAG, "AudioRecord 参数：minBuf=$minBuf 实际缓冲=$bufSize " +
                "请求 ${sampleRate}Hz/2ch/${encodingName(chosenEncoding)}")

        val ar = arOrNull

        if (ar.state != AudioRecord.STATE_INITIALIZED) {
            SnadLog.err(TAG, "AudioRecord 状态非 INITIALIZED，state=${ar.state}（0=INITIALIZED,1=UNINITIALIZED）")
            try { ar.release() } catch (t: Throwable) { SnadLog.soft(TAG, "释放未初始化的 AudioRecord", t) }
            fail("录音器未初始化（state=${ar.state}），可能是厂商 ROM 禁止内部音频捕获")
            stopEverything("初始化失败")
            return
        }

        record = ar
        captureEncoding = chosenEncoding
        channels = ar.channelCount.coerceAtLeast(1)
        sampleRate = ar.sampleRate
        SnadLog.info(TAG, "AudioRecord 就绪：实际 ${sampleRate}Hz / ${channels}ch " +
                "(请求 48000Hz/2ch，若不同说明设备没接受该格式)")

        val sock = try {
            DatagramSocket()
        } catch (t: Throwable) {
            SnadLog.err(TAG, "UDP socket 创建失败", t)
            fail("创建 UDP socket 失败：${t.javaClass.simpleName}: ${t.message}")
            try { ar.release() } catch (t2: Throwable) { SnadLog.soft(TAG, "释放 AudioRecord", t2) }
            stopEverything("socket 失败")
            return
        }
        socket = sock

        val dest = try {
            InetAddress.getByName(host)
        } catch (t: Throwable) {
            SnadLog.err(TAG, "地址解析失败 host=$host", t)
            fail("电脑地址无法解析：$host")
            closeQuietly(ar, sock)
            stopEverything("地址无效")
            return
        }

        val ht = HandlerThread("snad-sender").also { it.start() }
        thread = ht
        val h = Handler(ht.looper)
        worker = h

        running.set(true)
        seq = 0
        bytesSent = 0
        packetsSent = 0
        readErrors = 0
        consecutiveSilence = 0

        h.post { sendLoop(ar, sock, dest, port) }
    }

    private fun sendLoop(ar: AudioRecord, sock: DatagramSocket, dest: InetAddress, port: Int) {
        // 包长按位深缩放，保持"每包约 21ms"不变：
        // float32 每样本 4 字节，若仍用 4096 字节，包长会减半、包率翻倍。
        val bytesPerSample = if (captureEncoding == AudioFormat.ENCODING_PCM_FLOAT) 4 else 2
        val payloadBytes = SnadProtocol.PAYLOAD_BYTES * bytesPerSample / 2
        val pcm = ByteArray(payloadBytes)
        try {
            ar.startRecording()
            if (ar.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                SnadLog.err(TAG, "startRecording 后状态异常：recordingState=${ar.recordingState}")
                report("error", "录音未真正启动（state=${ar.recordingState}）")
            }
            SnadLog.info(TAG, "开始串流 -> $dest:$port  ${sampleRate}Hz/${channels}ch  " +
                    "每包 ${pcm.size}B ≈ ${"%.1f".format(pcm.size * 1000.0 / (sampleRate * channels * bytesPerSample))}ms " +
                    "位深 ${encodingName(captureEncoding)}")
            report("streaming", "$dest:$port  ${sampleRate}Hz/${channels}ch")

            // 上报采集侧能力：让电脑端把"采集延迟"从按典型值猜变成已知。
            // 采集缓冲取 AudioRecord 的真实值，而不是写死一个数——
            // 各机型差异很大，猜出来的值会让延迟估算失真。
            sendCapability(sock, dest, port, ar)

            var lastLog = SystemClock.elapsedRealtime()

            while (running.get()) {
                var filled = 0
                // AudioRecord 一次读不满是常态，凑满一包再发，避免碎包泛洪
                while (filled < pcm.size && running.get()) {
                    val n = try {
                        ar.read(pcm, filled, pcm.size - filled)
                    } catch (t: Throwable) {
                        SnadLog.err(TAG, "AudioRecord.read 抛异常", t)
                        -1
                    }
                    if (n > 0) {
                        filled += n
                        continue
                    }

                    readErrors++
                    // 前几次以及每 200 次记录一次，避免刷屏但绝不静默
                    if (readErrors <= 5 || readErrors % 200 == 0L) {
                        SnadLog.wrn(TAG, "read 返回 $n（累计 $readErrors 次）。" +
                                "0=暂无数据且未溢出, ERROR_INVALID_OPERATION=-3, ERROR_BAD_VALUE=-2, ERROR_DEAD_OBJECT=-6")
                    }
                    if (n == AudioRecord.ERROR_DEAD_OBJECT) {
                        SnadLog.err(TAG, "AudioRecord 已失效（ERROR_DEAD_OBJECT），停止并提示重新授权")
                        report("error", "录音对象已失效，请停止后重新开始")
                        running.set(false)
                        break
                    }
                    try { Thread.sleep(5) } catch (ie: InterruptedException) {
                        SnadLog.wrn(TAG, "等待线程被中断：${ie.message}")
                        Thread.currentThread().interrupt()
                    }
                }
                if (!running.get()) break
                if (filled <= 0) continue

                val out = try {
                    // 位深由采集格式决定：float32 用 v2 包头声明，pcm16 继续用 v1，
                    // 这样旧电脑端仍能收 pcm16 流（向后兼容），新端才能收到高精度。
                    val header = SnadProtocol.buildHeaderVersioned(
                        channels = channels,
                        sampleRate = sampleRate,
                        sequence = seq++,
                        timestampMs = (SystemClock.elapsedRealtime() and 0xFFFFFFFFL).toInt(),
                        payloadBytes = filled,
                        formatCode = formatCodeFor(chosenEncoding)
                    )
                    val buf = ByteArray(header.size + filled)
                    System.arraycopy(header, 0, buf, 0, header.size)
                    System.arraycopy(pcm, 0, buf, header.size, filled)
                    buf
                } catch (t: Throwable) {
                    SnadLog.err(TAG, "组包失败", t)
                    continue
                }

                try {
                    sock.send(DatagramPacket(out, out.size, dest, port))
                    bytesSent += filled
                    packetsSent++
                } catch (t: Throwable) {
                    SnadLog.err(TAG, "UDP 发送失败（第 $packetsSent 包）", t)
                    report("error", "发送失败：${t.javaClass.simpleName}: ${t.message}")
                    // 网络类错误不致命，继续尝试；连续失败会在下面的周期日志里体现
                }

                val now = SystemClock.elapsedRealtime()
                if (now - lastLog >= 10_000) {
                    val kbps = bytesSent * 8.0 / 1000.0 / ((now - lastLog) / 1000.0)
                    SnadLog.info(TAG, "周期统计：$packetsSent 包 / ${bytesSent / 1024} KB / ≈${"%.0f".format(kbps)} kbps" +
                            " / read 异常 $readErrors 次")
                    bytesSent = 0
                    lastLog = now
                    report("streaming", "$dest:$port  ${sampleRate}Hz/${channels}ch  已发 $packetsSent 包")
                }
            }
        } catch (t: Throwable) {
            SnadLog.err(TAG, "发送线程异常退出", t)
            if (running.get()) report("error", "发送线程异常：${t.javaClass.simpleName}: ${t.message}")
        } finally {
            SnadLog.info(TAG, "发送线程结束，累计 $packetsSent 包")
            closeQuietly(ar, sock)
            record = null
            socket = null
        }
    }

    // ---------------------------------------------------------------- 收尾与状态

    private fun closeQuietly(ar: AudioRecord?, sock: DatagramSocket?) {
        try { ar?.stop() } catch (t: Throwable) { SnadLog.soft(TAG, "AudioRecord.stop", t) }
        try { ar?.release() } catch (t: Throwable) { SnadLog.soft(TAG, "AudioRecord.release", t) }
        try { sock?.close() } catch (t: Throwable) { SnadLog.soft(TAG, "DatagramSocket.close", t) }
    }

    /**
     * 上报采集侧能力（type = 'C'，发往日志端口）。
     *
     * 采集缓冲取 AudioRecord 的真实 bufferSizeInFrames —— 各机型差异很大，
     * 写死一个典型值会让电脑端的延迟估算失真。取不到时上报 0，
     * 电脑端会退回按典型值估算并标注"估计"。
     */
    private fun sendCapability(sock: DatagramSocket, dest: InetAddress, port: Int, ar: AudioRecord) {
        try {
            val frames = runCatching { ar.bufferSizeInFrames }.getOrDefault(0)
            val bufferMs = if (frames > 0 && sampleRate > 0) {
                Math.round(frames * 1000.0 / sampleRate).toInt()
            } else 0

            // 按**实际选中的**编码上报，不声称没实现的能力。
            // float32 成功则置 bit3，否则只报 pcm16（bit0）。
            val formatMask = if (captureEncoding == AudioFormat.ENCODING_PCM_FLOAT) (0x01 or 0x08) else 0x01
            val pkt = SnadProtocol.buildCapability(sampleRate, bufferMs, formatMask)
            sock.send(DatagramPacket(pkt, pkt.size, dest, port + 1))
            SnadLog.info(TAG, "能力上报：${sampleRate}Hz，采集缓冲 ${bufferMs}ms（$frames 帧），" +
                    "位深 ${encodingName(captureEncoding)}")
        } catch (t: Throwable) {
            // 上报失败不影响串流，但必须留痕，否则电脑端会一直用估计值而无人知道原因
            SnadLog.soft(TAG, "能力上报发送失败", t)
        }
    }

    /** 编码名，仅用于日志。 */
    private fun encodingName(enc: Int): String = when (enc) {
        AudioFormat.ENCODING_PCM_FLOAT -> "float32"
        AudioFormat.ENCODING_PCM_16BIT -> "pcm16"
        else -> "enc$enc"
    }

    /** 把 AudioRecord 的编码映射成协议里的位深码：0=pcm16 3=float32。 */
    private fun formatCodeFor(enc: Int): Int = when (enc) {
        AudioFormat.ENCODING_PCM_FLOAT -> 3
        else -> 0
    }

    private fun fail(msg: String) {
        SnadLog.err(TAG, "失败：$msg")
        report("error", msg)
    }

    private fun stopEverything(reason: String?) {
        val wasRunning = running.getAndSet(false)
        worker?.removeCallbacksAndMessages(null)
        try { record?.stop() } catch (t: Throwable) { SnadLog.soft(TAG, "停止时 AudioRecord.stop", t) }
        try { projection?.stop() } catch (t: Throwable) { SnadLog.soft(TAG, "停止时 MediaProjection.stop", t) }
        thread?.quitSafely()
        thread = null
        worker = null
        projection = null
        SnadLog.close()

        if (wasRunning) {
            SnadLog.info(TAG, "已停止（$reason）共 $packetsSent 包")
            report("idle", "已停止，共发送 $packetsSent 包")
        }
        stopForegroundCompat()
        stopSelf()
    }

    private fun stopForegroundCompat() {
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) stopForeground(STOP_FOREGROUND_REMOVE)
            else @Suppress("DEPRECATION") stopForeground(true)
        } catch (t: Throwable) {
            SnadLog.soft(TAG, "stopForeground", t)
        }
    }

    private fun report(status: String, detail: String) {
        try {
            sendBroadcast(
                Intent(BROADCAST_STATUS)
                    .setPackage(packageName)
                    .putExtra(EXTRA_STATUS, status)
                    .putExtra(EXTRA_DETAIL, detail)
            )
        } catch (t: Throwable) {
            SnadLog.soft(TAG, "发送状态广播", t)
        }
        try {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(
                NOTIF_ID,
                Notification.Builder(this, CHANNEL_ID)
                    .setContentTitle("SNAD 音频桥")
                    .setContentText(detail)
                    .setSmallIcon(android.R.drawable.ic_media_play)
                    .setOngoing(true)
                    .build()
            )
        } catch (t: Throwable) {
            SnadLog.soft(TAG, "更新通知", t)
        }
    }

    override fun onDestroy() {
        SnadLog.info(TAG, "onDestroy")
        running.set(false)
        try { record?.release() } catch (t: Throwable) { SnadLog.soft(TAG, "onDestroy 释放 AudioRecord", t) }
        try { projection?.stop() } catch (t: Throwable) { SnadLog.soft(TAG, "onDestroy 停止 MediaProjection", t) }
        try { socket?.close() } catch (t: Throwable) { SnadLog.soft(TAG, "onDestroy 关闭 socket", t) }
        thread?.quitSafely()
        super.onDestroy()
    }
}
