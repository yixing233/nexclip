package clip.yixing.sync.service

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
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

    enum class ServerConnectionState {
        DISCONNECTED,
        CONNECTING,
        CONNECTED,
    }

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

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(1, buildNotification())
        return START_STICKY
    }

    override fun onDestroy() {
        uploadJob?.cancel()
        push?.disconnect()
        push = null
        clipboard.removePrimaryClipChangedListener(listener)
        isRunning.value = false
        isServerConnected.value = false
        serverConnectionState.value = ServerConnectionState.DISCONNECTED
        super.onDestroy()
    }

    /** 连接 SignalR:收到推送 → 记录 + 写回剪贴板 + 通知 */
    private fun connectPush() {
        val ctx = this
        push?.disconnect()
        val url = SyncSettings.serverUrl(ctx)
        if (url.isBlank()) {
            isServerConnected.value = false
            serverConnectionState.value = ServerConnectionState.DISCONNECTED
            return
        }
        serverConnectionState.value = ServerConnectionState.CONNECTING
        val client = PushClient(
            url,
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
        client.onStateChanged = { state ->
            val connected = state == "connected"
            isServerConnected.value = connected
            serverConnectionState.value = if (connected) {
                ServerConnectionState.CONNECTED
            } else {
                ServerConnectionState.DISCONNECTED
            }
        }
        push = client
        client.connect()
    }

    private fun pullAndApply() {
        try {
            val api = SyncApi(SyncSettings.serverUrl(this))
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
        if (SyncSettings.isContentFiltered(this, text)) return@OnPrimaryClipChangedListener
        val hash = sha256(text)
        if (hash == lastUploadHash) return@OnPrimaryClipChangedListener
        lastUploadHash = hash
        addCaptured(this, text)
        uploadJob?.cancel()
        uploadJob = scope.launch {
            delay(600) // 去抖
            val ctx = this@ClipboardMonitorService
            try {
                val api = SyncApi(SyncSettings.serverUrl(ctx))
                api.putText(text, SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceName(ctx))
            } catch (_: Exception) {
                // 失败静默,下一条变化再试
            }
        }
    }

    private fun notifyPush(deviceName: String, text: String) {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        runCatching {
            val pi = PendingIntent.getActivity(
                this, 0, Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            val n = NotificationCompat.Builder(this, "clipboard_sync_push")
                .setSmallIcon(R.drawable.ic_notification_nc)
                .setContentTitle("收到来自 $deviceName 的内容")
                .setContentText(text.take(100))
                .setStyle(NotificationCompat.BigTextStyle().bigText(text))
                .setContentIntent(pi)
                .setAutoCancel(true)
                .setShowWhen(true)
                .setPriority(NotificationCompat.PRIORITY_DEFAULT)
                .build()
            getSystemService(NotificationManager::class.java).notify((System.currentTimeMillis() % 100000).toInt(), n)
        }
    }

    private fun sha256(s: String): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    private fun buildNotification(): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(this, "clipboard_monitor")
            .setSmallIcon(R.drawable.ic_notification_nc)
            .setContentTitle("剪贴板同步已开启")
            .setContentText("正在后台实时同步与监听剪贴板变化")
            .setContentIntent(pi)
            .setOngoing(true)
            .setShowWhen(false)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    companion object {
        private const val PREFS_CAPTURED = "captured_clips"

        /** 服务运行状态(UI 开关用) */
        val isRunning = MutableStateFlow(false)

        /** 服务端连接状态(PushClient 实时连接) */
        val isServerConnected = MutableStateFlow(false)

        /** 服务端连接阶段,用于区分连接中与连接失败 */
        val serverConnectionState = MutableStateFlow(ServerConnectionState.DISCONNECTED)

        /** 捕获/接收的剪贴板记录(最新在前) */
        val captured = MutableStateFlow<List<CapturedClip>>(emptyList())

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, ClipboardMonitorService::class.java))
        }

        fun stop(context: Context) {
            isServerConnected.value = false
            serverConnectionState.value = ServerConnectionState.DISCONNECTED
            context.stopService(Intent(context, ClipboardMonitorService::class.java))
        }

        /** 新增一条记录(最新在前,持久化) */
        fun addCaptured(context: Context, text: String, imageRef: String? = null) {
            val list = listOf(CapturedClip(text = text, time = System.currentTimeMillis(), imageRef = imageRef)) + captured.value
            persist(context, list)
            captured.value = list
        }

        fun toggleFavorite(context: Context, clip: CapturedClip) {
            val list = captured.value.map {
                if (it == clip || it.id == clip.id) it.copy(isFavorite = !it.isFavorite) else it
            }
            persist(context, list)
            captured.value = list
        }

        fun updateClip(context: Context, oldClip: CapturedClip, newText: String) {
            val list = captured.value.map {
                if (it == oldClip || it.id == oldClip.id) it.copy(text = newText) else it
            }
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

        fun deleteClips(context: Context, targetClips: Collection<CapturedClip>) {
            val ids = targetClips.map { it.id }.toSet()
            val list = captured.value.filterNot { it.id in ids }
            persist(context, list)
            captured.value = list
        }

        /** 清空记录(支持保留收藏项) */
        fun clearAll(context: Context, keepFavorites: Boolean = true) {
            val list = if (keepFavorites) captured.value.filter { it.isFavorite } else emptyList()
            persist(context, list)
            captured.value = list
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

        /** 导出为备份 JSON 字符串 */
        fun exportBackup(context: Context): String {
            val list = captured.value
            val arr = JSONArray()
            list.forEach { c ->
                arr.put(
                    JSONObject().apply {
                        put("t", c.text)
                        put("m", c.time)
                        put("fav", c.isFavorite)
                        if (c.imageRef != null) put("img", c.imageRef)
                    }
                )
            }
            val root = JSONObject().apply {
                put("version", 1)
                put("timestamp", System.currentTimeMillis())
                put("count", list.size)
                put("clips", arr)
            }
            return root.toString(2)
        }

        /** 从 JSON 备份导入并合并记录，返回导入的记录条数 */
        fun importBackup(context: Context, jsonString: String): Int {
            val importedList = mutableListOf<CapturedClip>()
            val root = JSONObject(jsonString)
            val arr = if (root.has("clips")) root.getJSONArray("clips") else JSONArray(jsonString)
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                val text = o.optString("t", "")
                if (text.isNotBlank()) {
                    importedList.add(
                        CapturedClip(
                            text = text,
                            time = o.optLong("m", System.currentTimeMillis()),
                            isFavorite = o.optBoolean("fav", false),
                            imageRef = if (o.isNull("img")) null else o.optString("img")
                        )
                    )
                }
            }
            if (importedList.isEmpty()) return 0

            val current = captured.value
            val combined = (importedList + current).distinctBy { it.id }.sortedByDescending { it.time }
            persist(context, combined)
            captured.value = combined
            return importedList.size
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
                    list.add(
                        CapturedClip(
                            text = o.optString("t"),
                            time = o.optLong("m"),
                            isFavorite = o.optBoolean("fav", false),
                            imageRef = if (o.isNull("img")) null else o.optString("img")
                        )
                    )
                }
            } catch (_: Exception) {
            }
            captured.value = list
        }

        private fun persist(context: Context, list: List<CapturedClip>) {
            val maxHistory = SyncSettings.maxHistory(context)
            val favorites = list.filter { it.isFavorite }
            val nonFavorites = list.filterNot { it.isFavorite }.take(maxHistory)
            val toSave = (favorites + nonFavorites).distinctBy { it.id }.sortedByDescending { it.time }

            val arr = JSONArray()
            toSave.forEach { c ->
                arr.put(
                    JSONObject().apply {
                        put("t", c.text)
                        put("m", c.time)
                        put("fav", c.isFavorite)
                        if (c.imageRef != null) put("img", c.imageRef)
                    }
                )
            }
            context.getSharedPreferences(PREFS_CAPTURED, Context.MODE_PRIVATE)
                .edit().putString("clips", arr.toString()).apply()
        }
    }
}
