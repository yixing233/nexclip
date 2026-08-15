package clip.yixing.sync.service

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import clip.yixing.sync.MainActivity
import clip.yixing.sync.R
import clip.yixing.sync.data.PushClient
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

/**
 * 前台服务:监听本机剪贴板变化 → 记录 + 上传服务器;
 * 同时订阅 SignalR 推送:收到新剪贴板 → 记录 + 写回本机剪贴板(需模块白名单或前台焦点)。
 */
class ClipboardMonitorService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var uploadJob: Job? = null
    private var lastUploadHash = ""
    private lateinit var clipboard: ClipboardManager
    private var push: PushClient? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        startForeground(1, buildNotification())
        clipboard.addPrimaryClipChangedListener(listener)
        isRunning.value = true
        connectPush()
        // 启动时拉取服务器当前剪贴板作为初始记录
        scope.launch { pullAndApply() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        uploadJob?.cancel()
        push?.disconnect()
        push = null
        clipboard.removePrimaryClipChangedListener(listener)
        isRunning.value = false
        super.onDestroy()
    }

    /** 连接 SignalR:收到推送 → 记录 + 写回剪贴板 + 通知 */
    private fun connectPush() {
        val ctx = this
        push?.disconnect()
        val client = PushClient(
            SyncSettings.serverUrl(ctx),
            SyncSettings.serverToken(ctx),
            SyncSettings.ensureDeviceId(ctx),
        )
        client.onEntryReceived = { entry ->
            val text = entry.text
            if (!text.isNullOrBlank()) {
                addCaptured(ctx, text)
                runCatching {
                    clipboard.setPrimaryClip(ClipData.newPlainText("SyncClipboard", text))
                }
                notifyPush(entry.deviceName ?: "其他设备", text)
            }
        }
        push = client
        client.connect()
    }

    private fun pullAndApply() {
        try {
            val api = SyncApi(SyncSettings.serverUrl(this), SyncSettings.serverToken(this))
            val cur = api.getCurrent() ?: return
            if (!cur.text.isNullOrBlank() && captured.value.firstOrNull()?.text != cur.text) {
                addCaptured(this, cur.text)
            }
        } catch (_: Exception) {
            // 未配置/离线时静默
        }
    }

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        val item = clipboard.primaryClip?.getItemAt(0)
        val text = item?.coerceToText(this)?.toString()
        if (text.isNullOrBlank() || text.length > 500_000) return@OnPrimaryClipChangedListener
        val hash = sha256(text)
        if (hash == lastUploadHash) return@OnPrimaryClipChangedListener
        lastUploadHash = hash
        addCaptured(this, text)
        uploadJob?.cancel()
        uploadJob = scope.launch {
            delay(600) // 去抖
            val ctx = this@ClipboardMonitorService
            try {
                val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.serverToken(ctx))
                api.putText(text, SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceName(ctx))
            } catch (_: Exception) {
                // 失败静默,下一条变化再试
            }
        }
    }

    private fun notifyPush(deviceName: String, text: String) {
        if (Build.VERSION.SDK_INT < 33) return
        runCatching {
            val n = NotificationCompat.Builder(this, "clipboard_monitor")
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle("收到来自 " + deviceName + " 的剪贴板")
                .setContentText(text.take(60))
                .setAutoCancel(true)
                .build()
            getSystemService(NotificationManager::class.java).notify(1001, n)
        }
    }

    private fun sha256(s: String): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    private fun buildNotification(): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, "clipboard_monitor")
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(getString(R.string.notification_text))
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val PREFS_CAPTURED = "captured_clips"

        /** 服务运行状态(UI 开关用) */
        val isRunning = MutableStateFlow(false)

        /** 捕获/接收的剪贴板记录(最新在前) */
        val captured = MutableStateFlow<List<CapturedClip>>(emptyList())

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, ClipboardMonitorService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, ClipboardMonitorService::class.java))
        }

        /** 新增一条记录(最新在前,持久化) */
        fun addCaptured(context: Context, text: String) {
            val list = listOf(CapturedClip(text, System.currentTimeMillis())) + captured.value
            persist(context, list)
            captured.value = list
        }

        fun deleteAt(context: Context, index: Int) {
            val list = captured.value.toMutableList()
            if (index !in list.indices) return
            list.removeAt(index)
            persist(context, list)
            captured.value = list
        }

        /** 清空全部记录(可撤销) */
        fun clearAll(context: Context) {
            persist(context, emptyList())
            captured.value = emptyList()
        }

        /** 恢复整份记录(撤销清空) */
        fun replaceAll(context: Context, list: List<CapturedClip>) {
            persist(context, list)
            captured.value = list
        }

        /** 写回本机剪贴板并移到最新 */
        fun restoreAt(context: Context, index: Int, clip: CapturedClip) {
            runCatching {
                val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(ClipData.newPlainText("SyncClipboard", clip.text))
            }
            val list = captured.value.toMutableList()
            if (index in list.indices) list.removeAt(index)
            list.add(0, clip)
            persist(context, list)
            captured.value = list
        }

        /** 启动时恢复本地记录 */
        fun loadCaptured(context: Context) {
            val raw = context.getSharedPreferences(PREFS_CAPTURED, Context.MODE_PRIVATE)
                .getString("clips", null) ?: return
            val list = mutableListOf<CapturedClip>()
            try {
                val arr = JSONArray(raw)
                for (i in 0 until arr.length()) {
                    val o = arr.getJSONObject(i)
                    list.add(CapturedClip(o.optString("t"), o.optLong("m")))
                }
            } catch (_: Exception) {
            }
            captured.value = list
        }

        private fun persist(context: Context, list: List<CapturedClip>) {
            val arr = JSONArray()
            list.take(200).forEach { c ->
                arr.put(JSONObject().put("t", c.text).put("m", c.time))
            }
            context.getSharedPreferences(PREFS_CAPTURED, Context.MODE_PRIVATE)
                .edit().putString("clips", arr.toString()).apply()
        }
    }
}
