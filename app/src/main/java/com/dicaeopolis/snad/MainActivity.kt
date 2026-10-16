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
        if (addrChanged || !discoveredOnce) {
            discoveredOnce = true
            SnadLog.info("UI", "已发现电脑 ${peer.deviceId} @ ${peer.address}:${peer.port}，已自动填入")
            setStatus("idle", "已发现电脑 ${peer.address}:${peer.port}")
        }
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

        root.addView(label(R.string.label_target))
        hostField = EditText(this).apply {
            hint = getString(R.string.hint_host)
            inputType = InputType.TYPE_CLASS_TEXT
            setTextColor(ContextCompat.getColor(context, R.color.fg))
        }
        root.addView(hostField, wide())

        root.addView(label(R.string.label_port))
        portField = EditText(this).apply {
            hint = SnadProtocol.DEFAULT_PORT.toString()
            inputType = InputType.TYPE_CLASS_NUMBER
            setTextColor(ContextCompat.getColor(context, R.color.fg))
        }
        root.addView(portField, wide())

        startBtn = Button(this).apply {
            text = getString(R.string.btn_start)
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
        val host = hostField.text.toString().trim().ifEmpty { getString(R.string.hint_host) }
        val port = portField.text.toString().trim().ifEmpty { SnadProtocol.DEFAULT_PORT.toString() }
            .toIntOrNull() ?: SnadProtocol.DEFAULT_PORT

        SnadLog.info("UI", "点击开始 host=$host port=$port")

        if (host.isBlank()) {
            SnadLog.err("UI", "地址为空，中止")
            setStatus("error", "请先填电脑地址")
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
        val host = hostField.text.toString().trim().ifEmpty { getString(R.string.hint_host) }
        val port = portField.text.toString().trim().ifEmpty { SnadProtocol.DEFAULT_PORT.toString() }
            .toIntOrNull() ?: SnadProtocol.DEFAULT_PORT
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
