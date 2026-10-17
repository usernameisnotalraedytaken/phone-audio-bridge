package com.dicaeopolis.snad

import android.Manifest
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat

/**
 * 界面。除了地址输入与开关，核心是一块**实时日志窗**：
 * 任何失败都会立刻出现在这里，而不是只留一句"没声音"。
 * 日志同时经 UDP 送到电脑的 phone.log，所以不装 adb 也能排查。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var hostField: EditText
    private lateinit var portField: EditText
    private lateinit var statusView: TextView
    private lateinit var detailView: TextView
    private lateinit var logView: TextView
    private lateinit var logScroll: ScrollView
    private lateinit var startBtn: Button
    private lateinit var stopBtn: Button

    /** 自动发现结果的展示位。正常路径下这是用户唯一需要看的地方。 */
    private lateinit var discoveryView: TextView
    private lateinit var discoveryHintView: TextView

    /** 手填地址的折叠区：只在广播不可达的网络才需要展开。 */
    private lateinit var manualToggle: Button
    private lateinit var manualPanel: LinearLayout

    private var streaming = false

    /** 信标监听：自动发现电脑端。密钥在构建时注入，因此无需配对步骤。 */
    private var beacon: SnadBeaconListener? = null
    private var discoveredOnce = false
    private val logLines = ArrayDeque<String>()

    private val projLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val data = result.data
        if (result.resultCode == RESULT_OK && data != null) {
            SnadLog.info("UI", "录屏授权通过 resultCode=${result.resultCode}，启动服务")
            launchService(result.resultCode, data)
        } else {
            SnadLog.err("UI", "录屏授权被拒绝 resultCode=${result.resultCode} data=${data != null}")
            setStatus("error", "用户拒绝了录屏授权")
        }
    }

    private val audioPermLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        SnadLog.info("UI", "录音权限结果 granted=$granted")
        if (granted) requestProjection()
        else setStatus("error", "缺少录音权限，无法抓取内部音频")
    }

    private val statusReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            val s = intent?.getStringExtra(AudioCaptureService.EXTRA_STATUS) ?: return
            val d = intent.getStringExtra(AudioCaptureService.EXTRA_DETAIL) ?: ""
            setStatus(s, d)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        SnadLog.init(this)
        SnadLog.info("UI", "应用启动 version=${appVersion()} sdk=${Build.VERSION.SDK_INT} " +
                "device=${Build.MANUFACTURER} ${Build.MODEL}")
        setContentView(buildUi())
        setStatus("idle", getString(R.string.status_idle))

        // 服务与界面同进程，直接挂回调即可拿到实时日志
        SnadLog.uiSink = { line -> runOnUiThread { appendLog(line) } }
        SnadLog.recent().forEach { appendLog(it) }

        // 启动信标监听：自动发现电脑端地址，无需手填。
        // 密钥在构建时注入，因此这里没有配对步骤——这正是"无感"的含义。
        try {
            beacon = SnadBeaconListener(
                onPeer = { peer ->
                    runOnUiThread { onPeerDiscovered(peer) }
                },
                onLog = { msg -> SnadLog.info("Beacon", msg) }
            )
            beacon?.start()
        } catch (t: Throwable) {
            SnadLog.err("UI", "启动信标监听失败（可手填地址继续使用）", t)
        }
    }

    /** 收到合法信标：自动填入电脑地址与端口。 */
    private fun onPeerDiscovered(peer: SnadBeacon.Peer) {
        val addrChanged = hostField.text.toString().trim() != peer.address
        hostField.setText(peer.address)
        portField.setText(peer.port.toString())

        // 未在串流时允许点击开始；串流中保持原状态
        if (!streaming) startBtn.isEnabled = true
        discoveryView.text = getString(R.string.pc_found) + "：" + peer.address + ":" + peer.port
        discoveryHintView.text = peer.deviceId

        if (addrChanged || !discoveredOnce) {
            discoveredOnce = true
            SnadLog.info("UI", "已发现电脑 ${peer.deviceId} @ ${peer.address}:${peer.port}，已自动填入")
        }
    }

    private fun toggleManual() {
        val show = manualPanel.visibility != android.view.View.VISIBLE
        manualPanel.visibility = if (show) android.view.View.VISIBLE else android.view.View.GONE
        manualToggle.text = getString(if (show) R.string.btn_manual_hide else R.string.btn_manual)
    }

    override fun onStart() {
        super.onStart()
        val f = IntentFilter(AudioCaptureService.BROADCAST_STATUS)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                registerReceiver(statusReceiver, f, Context.RECEIVER_NOT_EXPORTED)
            } else {
                @Suppress("UnspecifiedRegisterReceiverFlag")
                registerReceiver(statusReceiver, f)
            }
        } catch (t: Throwable) {
            SnadLog.err("UI", "注册状态广播接收器失败", t)
        }
    }

    override fun onStop() {
        try {
            unregisterReceiver(statusReceiver)
        } catch (t: Throwable) {
            SnadLog.soft("UI", "注销状态广播接收器", t)
        }
        super.onStop()
    }

    override fun onDestroy() {
        // 停掉信标监听，避免 Activity 销毁后线程仍在收包
        try {
            beacon?.stop()
        } catch (t: Throwable) {
            SnadLog.soft("UI", "停止信标监听", t)
        }
        beacon = null
        SnadLog.uiSink = null
        super.onDestroy()
    }

    private fun appVersion(): String = try {
        packageManager.getPackageInfo(packageName, 0).versionName ?: "?"
    } catch (t: Throwable) {
        SnadLog.soft("UI", "读取版本号", t)
        "?"
    }

    // ---------------------------------------------------------------- 界面

    private fun buildUi(): ViewGroup {
        val pad = (16 * resources.displayMetrics.density).toInt()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        root.addView(TextView(this).apply {
            text = getString(R.string.title)
            textSize = 22f
            setTextColor(ContextCompat.getColor(context, R.color.fg))
        })
        root.addView(TextView(this).apply {
            text = getString(R.string.subtitle)
            textSize = 13f
            setTextColor(ContextCompat.getColor(context, R.color.muted))
            setPadding(0, 4, 0, pad)
        })

        // ---- 自动发现结果 ----
        // 正常路径下用户不需要输入任何地址：电脑端广播信标，手机收到即用。
        // 因此这里把"找到的电脑"作为一等公民展示，手填入口退到折叠区，
        // 只在广播不可达的网络（如校园网客户端隔离）才需要展开。
        discoveryView = TextView(this).apply {
            text = getString(R.string.finding_pc)
            textSize = 16f
            setTextColor(ContextCompat.getColor(context, R.color.accent))
            setPadding(0, 0, 0, 4)
        }
        root.addView(discoveryView)

        discoveryHintView = TextView(this).apply {
            text = getString(R.string.finding_pc_hint)
            textSize = 12f
            setTextColor(ContextCompat.getColor(context, R.color.muted))
            setPadding(0, 0, 0, pad)
        }
        root.addView(discoveryHintView)

        startBtn = Button(this).apply {
            text = getString(R.string.btn_start)
            // 未发现电脑前禁用，避免用户点了才开始想"地址填什么"
            isEnabled = false
            setOnClickListener { onStartClicked() }
        }
        root.addView(startBtn, wide())

        stopBtn = Button(this).apply {
            text = getString(R.string.btn_stop)
            isEnabled = false
            setOnClickListener {
                SnadLog.info("UI", "用户点击停止")
                startService(AudioCaptureService.stopIntent(this@MainActivity))
                setStatus("idle", "已请求停止")
            }
        }
        root.addView(stopBtn, wide())

        // ---- 手动指定（折叠）----
        manualToggle = Button(this).apply {
            text = getString(R.string.btn_manual)
            setOnClickListener { toggleManual() }
        }
        root.addView(manualToggle, wide())

        manualPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = android.view.View.GONE
        }
        manualPanel.addView(label(R.string.label_target))
        hostField = EditText(this).apply {
            hint = getString(R.string.hint_host)
            inputType = InputType.TYPE_CLASS_TEXT
            setTextColor(ContextCompat.getColor(context, R.color.fg))
        }
        manualPanel.addView(hostField, wide())
        manualPanel.addView(label(R.string.label_port))
        portField = EditText(this).apply {
            hint = SnadProtocol.DEFAULT_PORT.toString()
            inputType = InputType.TYPE_CLASS_NUMBER
            setTextColor(ContextCompat.getColor(context, R.color.fg))
        }
        manualPanel.addView(portField, wide())
        root.addView(manualPanel)

        statusView = TextView(this).apply {
            textSize = 16f
            setTextColor(ContextCompat.getColor(context, R.color.accent))
            setPadding(0, pad, 0, 0)
        }
        root.addView(statusView)

        detailView = TextView(this).apply {
            textSize = 13f
            setTextColor(ContextCompat.getColor(context, R.color.muted))
        }
        root.addView(detailView)

        // ---- 读块大小滑块 ----
        //
        // 这是手机侧唯一直接决定延迟的旋钮：read() 阻塞到凑满一块才返回，
        // 所以块越大，块内最老的样本等得越久（平均约半块，最坏一整块）。
        //
        // 上下限与默认值取自服务里的常量，避免两处各写一个数、日后对不上。
        // 改动在**下一条流**生效：中途改会让包序号与采样钟对不齐。
        root.addView(TextView(this).apply {
            textSize = 13f
            setTextColor(ContextCompat.getColor(context, R.color.muted))
            setPadding(0, pad / 2, 0, 0)
            val prefs = getSharedPreferences("snad", MODE_PRIVATE)
            val cur = prefs.getInt("chunk_frames", AudioCaptureService.CHUNK_DEFAULT_FRAMES)
            text = "读块 ${cur} 帧（%.1f ms/包）".format(cur * 1000.0 / 48000)
        })

        val chunkSlider = SeekBar(this).apply {
            val lo = AudioCaptureService.CHUNK_MIN_FRAMES
            val hi = AudioCaptureService.CHUNK_MAX_FRAMES
            max = 100
            val prefs = getSharedPreferences("snad", MODE_PRIVATE)
            val cur = prefs.getInt("chunk_frames", AudioCaptureService.CHUNK_DEFAULT_FRAMES)
            progress = ((cur - lo) * 100 / (hi - lo)).coerceIn(0, 100)
        }
        val chunkLabel = TextView(this).apply {
            textSize = 12f
            setTextColor(ContextCompat.getColor(context, R.color.muted))
        }
        chunkSlider.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                val lo = AudioCaptureService.CHUNK_MIN_FRAMES
                val hi = AudioCaptureService.CHUNK_MAX_FRAMES
                val frames = lo + (hi - lo) * progress / 100
                val ms = frames * 1000.0 / 48000
                chunkLabel.text = "$frames 帧 / %.1f ms 每包，约 %.0f 包每秒".format(ms, 48000.0 / frames)
                if (fromUser) {
                    getSharedPreferences("snad", MODE_PRIVATE).edit()
                        .putInt("chunk_frames", frames).apply()
                }
            }
            override fun onStartTrackingTouch(sb: SeekBar?) {}
            override fun onStopTrackingTouch(sb: SeekBar?) {}
        })
        root.addView(chunkSlider)
        root.addView(chunkLabel)

        root.addView(label(R.string.label_log))
        root.addView(Button(this).apply {
            text = getString(R.string.btn_copy_log)
            setOnClickListener { copyLog() }
        }, wide())

        logView = TextView(this).apply {
            textSize = 10f
            setTextColor(ContextCompat.getColor(context, R.color.fg))
            typeface = android.graphics.Typeface.MONOSPACE
            setTextIsSelectable(true)
            text = getString(R.string.log_empty)
        }
        logScroll = ScrollView(this).apply {
            addView(logView)
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                (260 * resources.displayMetrics.density).toInt()
            )
        }
        root.addView(logScroll)

        return ScrollView(this).apply { addView(root) }
    }

    private fun label(res: Int) = TextView(this).apply {
        text = getString(res)
        textSize = 13f
        setTextColor(ContextCompat.getColor(context, R.color.muted))
        setPadding(0, 8, 0, 0)
    }

    private fun wide() = LinearLayout.LayoutParams(
        LinearLayout.LayoutParams.MATCH_PARENT,
        LinearLayout.LayoutParams.WRAP_CONTENT
    )

    private fun appendLog(line: String) {
        logLines.addLast(line)
        while (logLines.size > 100) logLines.removeFirst()
        logView.text = logLines.joinToString("\n")
        // 自动滚到底部，保证最新一条始终可见
        logScroll.post { logScroll.fullScroll(ScrollView.FOCUS_DOWN) }
    }

    private fun copyLog() {
        try {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("SNAD log", logLines.joinToString("\n")))
            Toast.makeText(this, "日志已复制", Toast.LENGTH_SHORT).show()
        } catch (t: Throwable) {
            SnadLog.err("UI", "复制日志失败", t)
        }
    }

    // ---------------------------------------------------------------- 流程

    private fun onStartClicked() {
        // 正常路径下地址由信标自动填入。若为空说明既没发现电脑、也没手填，
        // 此时必须明确报错，而不是回退到某个占位常量（那会悄悄连到错误的地址）。
        val host = hostField.text.toString().trim()
        val port = portField.text.toString().trim()
            .toIntOrNull() ?: SnadProtocol.DEFAULT_PORT

        SnadLog.info("UI", "点击开始 host=$host port=$port")

        if (host.isBlank()) {
            SnadLog.err("UI", "尚未发现电脑且未手填地址，中止")
            setStatus("error", "还没找到电脑：请确认电脑端已启动接收器；" +
                    "若网络不支持广播，展开「手动指定地址」填写")
            return
        }

        try {
            getSharedPreferences("snad", MODE_PRIVATE).edit()
                .putString("host", host).putInt("port", port).apply()
        } catch (t: Throwable) {
            SnadLog.soft("UI", "保存地址到偏好设置", t)
        }

        setStatus("waiting", "请求录音权限…")
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
                PackageManager.PERMISSION_GRANTED
        SnadLog.info("UI", "RECORD_AUDIO 已授权=$granted")
        if (granted) requestProjection() else audioPermLauncher.launch(Manifest.permission.RECORD_AUDIO)
    }

    private fun requestProjection() {
        try {
            val mpm = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            setStatus("waiting", "请在弹窗中选择「整个屏幕」并确认共享音频")
            SnadLog.info("UI", "拉起系统录屏授权对话框")
            projLauncher.launch(mpm.createScreenCaptureIntent())
        } catch (t: Throwable) {
            SnadLog.err("UI", "无法拉起录屏授权对话框", t)
            setStatus("error", "无法请求录屏授权：${t.javaClass.simpleName}: ${t.message}")
        }
    }

    private fun launchService(resultCode: Int, data: Intent) {
        // 与 onStartClicked 保持同一策略：不回退占位地址。
        // 能走到这里说明 onStartClicked 已校验过 host 非空，但仍显式检查，
        // 避免以后改动流程时又出现"悄悄连到错误地址"。
        val host = hostField.text.toString().trim()
        val port = portField.text.toString().trim()
            .toIntOrNull() ?: SnadProtocol.DEFAULT_PORT
        if (host.isBlank()) {
            SnadLog.err("UI", "启动服务时地址为空，中止（不应发生：onStartClicked 已校验）")
            setStatus("error", "电脑地址为空，无法启动")
            return
        }
        try {
            ContextCompat.startForegroundService(
                this, AudioCaptureService.startIntent(this, resultCode, data, host, port)
            )
            setStatus("waiting", "服务启动中…")
        } catch (t: Throwable) {
            SnadLog.err("UI", "启动前台服务失败", t)
            setStatus("error", "启动服务失败：${t.javaClass.simpleName}: ${t.message}")
        }
    }

    private fun setStatus(status: String, detail: String) {
        runOnUiThread {
            streaming = status == "streaming"
            startBtn.isEnabled = !streaming
            stopBtn.isEnabled = streaming
            statusView.text = when (status) {
                "streaming" -> getString(R.string.status_streaming)
                "error" -> getString(R.string.status_error)
                "idle" -> getString(R.string.status_idle)
                else -> getString(R.string.status_waiting)
            }
            detailView.text = detail
            if (status == "error") {
                SnadLog.err("UI", "界面报错：$detail")
                Toast.makeText(this, detail, Toast.LENGTH_LONG).show()
            }
        }
    }
}
