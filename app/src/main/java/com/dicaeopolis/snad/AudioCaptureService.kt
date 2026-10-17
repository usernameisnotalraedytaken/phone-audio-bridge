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

        /** 采样钟报文端口。与音频(6981)、文本日志(6982)、信标(6983)分开。 */
        const val CLOCK_PORT = 6984

        /** 读块帧数的可调区间。低于 240 帧（5 ms）在多数 HAL 上只会制造空读。 */
        const val CHUNK_MIN_FRAMES = 256
        const val CHUNK_MAX_FRAMES = 2048
        const val CHUNK_DEFAULT_FRAMES = 480

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
    private var seq = 0
    private var bytesSent = 0L
    private var packetsSent = 0L
    private var readErrors = 0L

    /** 读块帧数（可调）。0 表示用默认值 480 帧（10 ms，HAL 周期的整数倍）。 */
    private var chunkFrames = 0

    /** 最近发出的音频包序号与累计帧数，供采样钟报文对齐。 */
    @Volatile private var lastSentSeq = -1
    @Volatile private var lastSentFrames = 0L

    /** 采样钟报文端口。与音频(6981)、文本日志(6982)、信标(6983)分开。 */
    // CLOCK_PORT 定义在上面 companion object 里

    /** 每包字节数：读块帧数 × 声道 × 2 字节。 */
    private fun payloadBytes(): Int {
        val frames = if (chunkFrames > 0) chunkFrames else CHUNK_DEFAULT_FRAMES
        return frames * channels * 2
    }

    /**
     * 采样钟报文：每 50 ms 一包，向 PC 提供"这一帧是什么时候被产出的"。
     *
     * 字段（小端，44 字节）：
     *   0..3   "SNAD"        4  version=1     5  type='K'
     *   6..9   lastSeq                最近发出的音频包序号
     *   10..13 lastSentFrames         累计已发帧数
     *   14..21 captureNanoTime        getTimestamp() 给出的采集时刻（CLOCK_MONOTONIC）
     *   22..29 elapsedRealtimeNanos   同一时刻的 BOOTTIME 读数（用于两时钟域换算）
     *   30..33 tsFramePosition        该时刻对应的帧位置
     *   34..37 bufferFrames           AudioRecord 缓冲帧数
     *   38..41 chunkFrames            当前读块帧数
     *   42     flags                  bit0=1 表示 getTimestamp 可用
     *
     * 为什么要双时钟：getTimestamp 用 CLOCK_MONOTONIC，而音频包时间戳用
     * CLOCK_BOOTTIME（elapsedRealtime）。设备休眠时两者分叉，
     * 只给一个的话 PC 侧换算会随息屏莫名漂移。两个都给，PC 自己求差。
     *
     * getTimestamp 失败时必须如实上报 flags=0，不能拿 read 时刻冒充采集时刻。
     */
    private fun startClockReporter(ar: AudioRecord, dest: InetAddress) {
        val t = Thread({
            var sock: DatagramSocket? = null
            try {
                sock = DatagramSocket()

                // 用反射拿 AudioRecord.getTimestamp：
                // 编译期直接用嵌套类 AudioRecord.AudioTimestamp 时，CI 上解析不到
                // （compileSdk 35、无同名冲突，原因未查明），而手机上是否支持该 API
                // 本来就因厂商而异。反射可以做到"拿到就用，拿不到如实上报"，
                // 不会因为一个可选 API 让整个构建失败。
                var tsCls: Class<*>? = null
                var tsObj: Any? = null
                var getTs: java.lang.reflect.Method? = null
                var fNano: java.lang.reflect.Field? = null
                var fPos: java.lang.reflect.Field? = null
                try {
                    // 先取成局部 val 再赋值：tsCls 若是 Class<*>?，
                    // getMethod 的变参会因可空而编译不过。
                    val cls = Class.forName("android.media.AudioRecord\$AudioTimestamp")
                    val obj = cls.getDeclaredConstructor().newInstance()
                    tsCls = cls
                    tsObj = obj
                    getTs = AudioRecord::class.java.getMethod("getTimestamp", cls)
                    fNano = cls.getField("nanoTime")
                    fPos = cls.getField("framePosition")
                    SnadLog.info(TAG, "采样钟：getTimestamp 可用，将按系统采集时刻测量")
                } catch (e: Throwable) {
                    SnadLog.wrn(TAG, "采样钟：本机不支持 getTimestamp" +
                            "（${e.javaClass.simpleName}: ${e.message}），" +
                            "flags 将恒为 0，PC 侧只能退化为 read 时刻")
                }

                var failStreak = 0L
                while (running.get()) {
                    val buf = ByteArray(44)
                    buf[0] = 'S'.code.toByte(); buf[1] = 'N'.code.toByte()
                    buf[2] = 'A'.code.toByte(); buf[3] = 'D'.code.toByte()
                    buf[4] = 1
                    buf[5] = 'K'.code.toByte()

                    var ok = false
                    var capNs = 0L
                    var capPos = 0
                    // 取局部非空变量再调用：字段是 var 且可空，直接传参会因可空编译不过。
                    val mGet = getTs
                    val tObj = tsObj
                    if (mGet != null && tObj != null) {
                        try {
                            // 返回 int 状态码：0 = SUCCESS，非 0 表示该设备不支持。
                            val rc = mGet.invoke(ar, tObj) as? Int ?: -1
                            ok = rc == 0
                            if (ok) {
                                capNs = fNano?.getLong(tObj) ?: 0L
                                capPos = fPos?.getInt(tObj) ?: 0
                            }
                        } catch (e: Throwable) {
                            SnadLog.wrn(TAG, "getTimestamp 反射调用失败：${e.javaClass.simpleName}")
                        }
                    }
                    val el = SystemClock.elapsedRealtimeNanos()

                    putI32(buf, 6, lastSentSeq)
                    putI32(buf, 10, lastSentFrames.toInt())
                    putI64(buf, 14, if (ok) capNs else 0L)
                    putI64(buf, 22, el)
                    putI32(buf, 30, if (ok) capPos else 0)
                    putI32(buf, 34, ar.bufferSizeInFrames)
                    putI32(buf, 38, if (chunkFrames > 0) chunkFrames else CHUNK_DEFAULT_FRAMES)
                    buf[42] = if (ok) 1 else 0

                    try {
                        sock.send(DatagramPacket(buf, buf.size, dest, CLOCK_PORT))
                    } catch (e: Throwable) {
                        SnadLog.wrn(TAG, "采样钟报文发送失败：${e.javaClass.simpleName}: ${e.message}")
                    }

                    if (!ok) {
                        failStreak++
                        if (failStreak == 1L || failStreak % 40L == 0L) {
                            SnadLog.wrn(TAG, "getTimestamp 不可用（连续 $failStreak 次）：" +
                                    "本机可能不支持播放捕获时间戳，端到端起点只能退化为 read 时刻")
                        }
                    } else failStreak = 0

                    Thread.sleep(50)
                }
            } catch (e: Throwable) {
                SnadLog.err(TAG, "采样钟线程异常退出", e)
            } finally {
                try { sock?.close() } catch (e: Throwable) {
                    SnadLog.soft(TAG, "关闭采样钟 socket", e)
                }
            }
        }, "snad-clock")
        t.isDaemon = true
        t.start()
    }

    private fun putI32(b: ByteArray, off: Int, v: Int) {
        b[off] = (v and 0xFF).toByte()
        b[off + 1] = ((v ushr 8) and 0xFF).toByte()
        b[off + 2] = ((v ushr 16) and 0xFF).toByte()
        b[off + 3] = ((v ushr 24) and 0xFF).toByte()
    }

    private fun putI64(b: ByteArray, off: Int, v: Long) {
        for (i in 0 until 8) b[off + i] = ((v ushr (8 * i)) and 0xFF).toByte()
    }
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
                // 读块帧数来自界面滑块，改动在下一条流生效。
                // 范围与默认值在 companion object 里定义，越界时钳住并说明——
                // 静默接受一个非法值会让"为什么延迟没变"变成悬案。
                val pref = getSharedPreferences("snad", Context.MODE_PRIVATE)
                val want = pref.getInt("chunk_frames", CHUNK_DEFAULT_FRAMES)
                chunkFrames = when {
                    want < CHUNK_MIN_FRAMES -> {
                        SnadLog.wrn(TAG, "读块 $want 帧低于下限 ${CHUNK_MIN_FRAMES}，已钳到下限")
                        CHUNK_MIN_FRAMES
                    }
                    want > CHUNK_MAX_FRAMES -> {
                        SnadLog.wrn(TAG, "读块 $want 帧高于上限 ${CHUNK_MAX_FRAMES}，已钳到上限")
                        CHUNK_MAX_FRAMES
                    }
                    else -> want
                }
                SnadLog.info(TAG, "读块 $chunkFrames 帧" +
                        "（${"%.1f".format(chunkFrames * 1000.0 / 48000)} ms/包）")
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

        val minBuf = AudioRecord.getMinBufferSize(
            sampleRate, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT
        )
        if (minBuf <= 0) {
            SnadLog.wrn(TAG, "getMinBufferSize 返回异常值 $minBuf，改用 4096 兜底")
        }
        val bufSize = max(if (minBuf > 0) minBuf * 2 else 8192, SnadProtocol.PAYLOAD_BYTES * 4)
        SnadLog.info(TAG, "AudioRecord 参数：minBuf=$minBuf 实际缓冲=$bufSize " +
                "请求 ${sampleRate}Hz/2ch/16bit")

        val ar = try {
            AudioRecord.Builder()
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(sampleRate)
                        .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                        .build()
                )
                .setBufferSizeInBytes(bufSize)
                .setAudioPlaybackCaptureConfig(cfg)
                .build()
        } catch (t: Throwable) {
            SnadLog.err(TAG, "AudioRecord 构建失败（厂商 ROM 可能限制内部音频抓取）", t)
            fail("创建录音器失败：${t.javaClass.simpleName}: ${t.message}")
            stopEverything("录音器失败")
            return
        }

        if (ar.state != AudioRecord.STATE_INITIALIZED) {
            SnadLog.err(TAG, "AudioRecord 状态非 INITIALIZED，state=${ar.state}（0=INITIALIZED,1=UNINITIALIZED）")
            try { ar.release() } catch (t: Throwable) { SnadLog.soft(TAG, "释放未初始化的 AudioRecord", t) }
            fail("录音器未初始化（state=${ar.state}），可能是厂商 ROM 禁止内部音频捕获")
            stopEverything("初始化失败")
            return
        }

        record = ar
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
        val pcm = ByteArray(payloadBytes())
        try {
            ar.startRecording()
            if (ar.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                SnadLog.err(TAG, "startRecording 后状态异常：recordingState=${ar.recordingState}")
                report("error", "录音未真正启动（state=${ar.recordingState}）")
            }
            SnadLog.info(TAG, "开始串流 -> $dest:$port  ${sampleRate}Hz/${channels}ch  " +
                    "每包 ${pcm.size}B ≈ ${"%.1f".format(pcm.size * 1000.0 / (sampleRate * channels * 2))}ms")

            // 采样钟：让"手机何时真正产出这一帧"变成可观测量。
            // 单独线程、单独端口，带双时钟读数（MONOTONIC 与 BOOTTIME 分域，休眠后会分叉）。
            startClockReporter(ar, dest)

            report("streaming", "$dest:$port  ${sampleRate}Hz/${channels}ch")

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
                    val seqNow = seq++
                    lastSentSeq = seqNow
                    lastSentFrames += filled / Math.max(1, channels * 2)
                    val header = SnadProtocol.buildHeader(
                        channels = channels,
                        sampleRate = sampleRate,
                        sequence = seqNow,
                        timestampMs = (SystemClock.elapsedRealtime() and 0xFFFFFFFFL).toInt(),
                        payloadBytes = filled
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
