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
import clip.yixing.sync.util.AppSourceHelper
import clip.yixing.sync.util.ImageLoader
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.isActive
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
    private var heartbeatJob: Job? = null
    private var legacyMigrationJob: Job? = null
    private var lastUploadHash: String? = null
    private lateinit var clipboard: ClipboardManager
    private var push: PushClient? = null
    @Volatile
    private var isApplyingRemote = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        startForeground(SyncNotificationManager.NOTIFICATION_ID_FOREGROUND, buildNotification())
        clipboard.addPrimaryClipChangedListener(listener)
        isRunning.value = true
        connectPush()
        // 启动时拉取服务器当前剪贴板作为初始记录
        scope.launch { pullAndApply() }

        // 监听记录与连接状态以实时更新前台通知/超级岛卡片
        scope.launch {
            captured.collect {
                refreshForegroundNotification()
            }
        }
        scope.launch {
            isServerConnected.collect {
                refreshForegroundNotification()
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(SyncNotificationManager.NOTIFICATION_ID_FOREGROUND, buildNotification())
        return START_STICKY
    }

    override fun onDestroy() {
        uploadJob?.cancel()
        heartbeatJob?.cancel()
        heartbeatJob = null
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
        val deviceId = SyncSettings.ensureDeviceId(ctx)
        val deviceToken = SyncSettings.deviceToken(ctx)
        if (url.isBlank() || !SyncSettings.isPaired(ctx) || deviceToken.isBlank()) {
            isServerConnected.value = false
            serverConnectionState.value = ServerConnectionState.DISCONNECTED
            return
        }
        serverConnectionState.value = ServerConnectionState.CONNECTING
        val client = PushClient(
            url,
            deviceId,
            deviceToken,
        )
        client.onEntryReceived = onEntryReceived@ { entry ->
            val localDeviceId = SyncSettings.ensureDeviceId(ctx)
            if (entry.deviceId == localDeviceId) {
                // 忽略来自本机的回显广播，避免自写回环与重复记录
                return@onEntryReceived
            }
            val text = entry.text
            val imgRef = entry.imageRef
            val isManual = entry.isManual
            if (!imgRef.isNullOrBlank()) {
                addCaptured(ctx, "[图片]", imgRef, sourceDevice = entry.deviceName ?: "其他设备", isManual = isManual)
                notifyPush(entry.deviceName ?: "其他设备", "[图片]")
            } else if (!text.isNullOrBlank()) {
                val hash = sha256(text)
                lastUploadHash = hash
                addCaptured(ctx, text, null, sourceDevice = entry.deviceName ?: "其他设备", isManual = isManual)
                isApplyingRemote = true
                try {
                    clipboard.setPrimaryClip(ClipData.newPlainText("NexClip", text))
                } finally {
                    scope.launch {
                        delay(350)
                        isApplyingRemote = false
                    }
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
            if (connected) {
                heartbeatJob?.cancel()
                heartbeatJob = scope.launch(Dispatchers.IO) {
                    while (isActive) {
                        delay(45_000)
                        try {
                            if (SyncSettings.isPaired(ctx) && SyncSettings.deviceToken(ctx).isNotBlank()) {
                                val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceToken(ctx))
                                api.getDevices()
                            }
                        } catch (_: Exception) {
                        }
                    }
                }
            } else {
                heartbeatJob?.cancel()
                heartbeatJob = null
            }
        }
        client.onAuthFailure = {
            isServerConnected.value = false
            serverConnectionState.value = ServerConnectionState.DISCONNECTED
            heartbeatJob?.cancel()
            heartbeatJob = null
            // 服务端通知凭证失效或设备已被移除,清空配对状态并断开,绝不私自重新注册建档
            SyncSettings.clearPairing(ctx)
            scope.launch {
                client.disconnect()
                if (push === client) push = null
            }
        }
        push = client
        client.connect()
    }

    private fun isFreshEntry(createdAtStr: String?, maxAgeMs: Long = 5 * 60 * 1000L): Boolean {
        if (createdAtStr.isNullOrBlank()) return false
        return try {
            val instant = java.time.Instant.parse(createdAtStr)
            val now = java.time.Instant.now()
            val diffMs = java.time.Duration.between(instant, now).abs().toMillis()
            diffMs <= maxAgeMs
        } catch (_: Exception) {
            try {
                val format = java.text.SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", java.util.Locale.US)
                format.timeZone = java.util.TimeZone.getTimeZone("UTC")
                val clean = createdAtStr.substringBefore('.').substringBefore('Z')
                val date = format.parse(clean)
                val diffMs = Math.abs(System.currentTimeMillis() - (date?.time ?: 0L))
                diffMs <= maxAgeMs
            } catch (_: Exception) {
                false
            }
        }
    }

    private fun pullAndApply() {
        try {
            if (!SyncSettings.isPaired(this) || SyncSettings.deviceToken(this).isBlank()) return
            val localDeviceId = SyncSettings.ensureDeviceId(this)
            val api = SyncApi(SyncSettings.serverUrl(this), localDeviceId, SyncSettings.deviceToken(this))
            val cur = api.getCurrent() ?: return
            if (cur.deviceId == localDeviceId) return

            // 严格时效性校验: 仅同步距今 5 分钟以内的“新鲜”剪贴板条目，禁止自动同步陈旧历史条目覆写本机剪贴板
            if (!isFreshEntry(cur.createdAt, maxAgeMs = 5 * 60 * 1000L)) {
                android.util.Log.i("NexClip", "pullAndApply ignored: remote entry is stale (${cur.createdAt})")
                return
            }

            val remoteDevName = cur.deviceName?.ifBlank { null } ?: "其他设备"
            if (!cur.imageRef.isNullOrBlank() && captured.value.firstOrNull()?.imageRef != cur.imageRef) {
                addCaptured(this, "[图片]", cur.imageRef, sourceDevice = remoteDevName, sourceApp = remoteDevName)
            } else if (!cur.text.isNullOrBlank() && captured.value.firstOrNull()?.text != cur.text) {
                val hash = sha256(cur.text)
                lastUploadHash = hash
                addCaptured(this, cur.text, null, sourceDevice = remoteDevName, sourceApp = remoteDevName)
                isApplyingRemote = true
                try {
                    clipboard.setPrimaryClip(ClipData.newPlainText("NexClip", cur.text))
                } finally {
                    scope.launch {
                        delay(1000)
                        isApplyingRemote = false
                    }
                }
            }
        } catch (e: clip.yixing.sync.data.ApiException) {
            if (e.statusCode == 401 || e.statusCode == 403 || e.statusCode == 410) {
                SyncSettings.clearPairing(this)
                push?.disconnect()
                push = null
            }
        } catch (_: Exception) {
            // 未配置/离线时静默
        }
    }

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        if (isApplyingRemote) {
            android.util.Log.i("NexClip", "OnPrimaryClipChangedListener ignored (applying remote push)")
            return@OnPrimaryClipChangedListener
        }
        val clipData = runCatching { clipboard.primaryClip }.getOrNull() ?: return@OnPrimaryClipChangedListener
        val item = clipData.getItemAt(0) ?: return@OnPrimaryClipChangedListener

        val sourcePkg = AppSourceHelper.resolvePackageName(this, clipData)
        // 关键防护: 若剪贴板内容由 NexClip 自身写入(如应用云端推送、在 App 内点击复制), 不作为新的本机记录重复捕获
        if (sourcePkg == packageName) {
            android.util.Log.i("NexClip", "OnPrimaryClipChangedListener ignored (source is self app)")
            return@OnPrimaryClipChangedListener
        }
        if (SyncSettings.isPackageFiltered(this, sourcePkg)) {
            android.util.Log.i("NexClip", "OnPrimaryClipChangedListener ignored (source package $sourcePkg is blacklisted)")
            return@OnPrimaryClipChangedListener
        }

        // 1. 检查是否复制了图片 (Uri / MIME 类型为 image/*)
        val uri = item.uri
        val mimeType = clipData.description?.let { desc ->
            (0 until desc.mimeTypeCount).map { desc.getMimeType(it) }.firstOrNull { it.startsWith("image/") }
        } ?: uri?.let { contentResolver.getType(it) }

        val sourceApp = AppSourceHelper.resolveAppName(this, sourcePkg)

        if (uri != null && mimeType?.startsWith("image/") == true) {
            val bytes = runCatching {
                contentResolver.openInputStream(uri)?.use { it.readBytes() }
            }.getOrNull()

            if (bytes != null && bytes.isNotEmpty() && bytes.size <= 30 * 1024 * 1024) {
                val hash = sha256Bytes(bytes)
                if (hash == lastUploadHash) return@OnPrimaryClipChangedListener
                lastUploadHash = hash

                // 关键修复: 1. 持久化缓存图片到本地磁盘
                ImageLoader.saveBytesToDisk(this, hash, bytes)

                // 关键修复: 2. 存入捕获历史并携带 imageRef = hash
                addCaptured(this, "[图片]", hash, sourceDevice = "本机", sourcePackage = sourcePkg, sourceApp = sourceApp)

                val clip = CapturedClip(
                    text = "[图片]",
                    time = System.currentTimeMillis(),
                    imageRef = hash,
                    sourceDevice = "本机",
                    sourcePackage = sourcePkg,
                    sourceApp = sourceApp
                )
                SyncNotificationManager.notifyNewClip(this, clip, sourceApp ?: "本机", isPush = false)
                refreshForegroundNotification()

                uploadJob?.cancel()
                uploadJob = scope.launch(Dispatchers.IO) {
                    delay(600)
                    val ctx = this@ClipboardMonitorService
                    try {
                        if (!SyncSettings.isPaired(ctx) || SyncSettings.deviceToken(ctx).isBlank()) return@launch
                        val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceToken(ctx))
                        api.uploadImage(bytes, SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceName(ctx))
                    } catch (e: clip.yixing.sync.data.ApiException) {
                        if (e.statusCode == 401 || e.statusCode == 403 || e.statusCode == 410) {
                            SyncSettings.clearPairing(ctx)
                            connectPush()
                        }
                    } catch (_: Exception) {
                    }
                }
                return@OnPrimaryClipChangedListener
            }
        }

        // 2. 纯文本处理 (安全提取纯文本，杜绝读取二进制 URI 造成 EXIF 乱码)
        val hasTextMime = clipData.description?.hasMimeType("text/*") == true
        val text = if (item.text != null) {
            item.text.toString()
        } else if (uri == null && hasTextMime) {
            item.coerceToText(this)?.toString()
        } else if (uri != null && hasTextMime && (mimeType == null || mimeType.startsWith("text/"))) {
            item.coerceToText(this)?.toString()
        } else {
            null
        }
        android.util.Log.i("NexClip", "primaryClip read: hasClip=${clipData != null}, textLen=${text?.length ?: 0}, srcPkg=$sourcePkg, srcApp=$sourceApp")
        if (text.isNullOrBlank() || text.length > 500_000) return@OnPrimaryClipChangedListener
        if (SyncSettings.isContentFiltered(this, text)) return@OnPrimaryClipChangedListener
        val hash = sha256(text)
        if (hash == lastUploadHash) return@OnPrimaryClipChangedListener
        lastUploadHash = hash
        addCaptured(this, text, null, sourceDevice = "本机", sourcePackage = sourcePkg, sourceApp = sourceApp)

        val clip = CapturedClip(
            text = text,
            time = System.currentTimeMillis(),
            sourceDevice = "本机",
            sourcePackage = sourcePkg,
            sourceApp = sourceApp
        )
        SyncNotificationManager.notifyNewClip(this, clip, sourceApp ?: "本机", isPush = false)
        refreshForegroundNotification()

        uploadJob?.cancel()
        uploadJob = scope.launch(Dispatchers.IO) {
            delay(600) // 去抖
            val ctx = this@ClipboardMonitorService
            try {
                if (!SyncSettings.isPaired(ctx) || SyncSettings.deviceToken(ctx).isBlank()) return@launch
                val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceToken(ctx))
                api.putText(text, SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceName(ctx))
            } catch (e: clip.yixing.sync.data.ApiException) {
                if (e.statusCode == 401 || e.statusCode == 403 || e.statusCode == 410) {
                    SyncSettings.clearPairing(ctx)
                    connectPush()
                }
            } catch (_: Exception) {
                // 失败静默,下一条变化再试
            }
        }
    }

    private fun notifyPush(deviceName: String, text: String) {
        val clip = CapturedClip(
            text = text,
            time = System.currentTimeMillis(),
            sourceDevice = deviceName,
            sourceApp = "来自 $deviceName"
        )
        SyncNotificationManager.notifyNewClip(this, clip, deviceName, isPush = true)
    }

    private fun sha256(s: String): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
    }

    private fun sha256Bytes(bytes: ByteArray): String {
        val md = java.security.MessageDigest.getInstance("SHA-256")
        return md.digest(bytes).joinToString("") { "%02x".format(it) }
    }

    private fun refreshForegroundNotification() {
        runCatching {
            val nm = getSystemService(NotificationManager::class.java)
            nm.notify(SyncNotificationManager.NOTIFICATION_ID_FOREGROUND, buildNotification())
        }
    }

    private fun buildNotification(): Notification {
        val latest = captured.value.firstOrNull()
        val style = SyncSettings.notificationStyle(this)
        return SyncNotificationManager.buildForegroundNotification(
            this,
            latest,
            isServerConnected.value,
            style
        )
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

        /** 刷新前台通知 (设置项变更等) */
        fun updateNotification(context: Context) {
            if (isRunning.value) {
                runCatching {
                    val latest = captured.value.firstOrNull()
                    val style = SyncSettings.notificationStyle(context)
                    val n = SyncNotificationManager.buildForegroundNotification(
                        context,
                        latest,
                        isServerConnected.value,
                        style
                    )
                    context.getSystemService(NotificationManager::class.java).notify(
                        SyncNotificationManager.NOTIFICATION_ID_FOREGROUND,
                        n
                    )
                }
            }
        }

        fun addCaptured(
            context: Context,
            text: String,
            imageRef: String? = null,
            sourceDevice: String? = null,
            sourcePackage: String? = null,
            sourceApp: String? = null,
            isManual: Boolean = false
        ) {
            val first = captured.value.firstOrNull()
            if (first != null) {
                if (!imageRef.isNullOrBlank() && first.imageRef == imageRef) return
                if (imageRef.isNullOrBlank() && first.imageRef.isNullOrBlank() && first.text == text) return
            }
            val resolvedApp = sourceApp ?: AppSourceHelper.resolveAppName(context, sourcePackage)
            val list = listOf(
                CapturedClip(
                    text = text,
                    time = System.currentTimeMillis(),
                    imageRef = imageRef,
                    sourceDevice = sourceDevice,
                    sourcePackage = sourcePackage,
                    sourceApp = resolvedApp,
                    isManual = isManual
                )
            ) + captured.value
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

        fun deleteClip(context: Context, clip: CapturedClip) {
            val list = captured.value.filterNot { it == clip || it.id == clip.id }
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

        fun clear(context: Context) = clearAll(context, true)

        /** 恢复整份记录(撤销清空) */
        fun replaceAll(context: Context, list: List<CapturedClip>) {
            persist(context, list)
            captured.value = list
        }

        /** 写回本机剪贴板并移到最新 */
        fun restoreAt(context: Context, index: Int, clip: CapturedClip) {
            runCatching {
                val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(ClipData.newPlainText("NexClip", clip.text))
            }
            val list = captured.value.toMutableList()
            val safeIndex = index.coerceIn(0, list.size)
            list.add(safeIndex, clip)
            persist(context, list)
            captured.value = list
        }

        /** 导出为备份 JSON 字符串 */
        fun exportBackup(context: Context): String {
            val clips = captured.value
            val root = JSONObject().apply {
                put("version", 1)
                put("exportedAt", System.currentTimeMillis())
                put("count", clips.size)
                val arr = JSONArray()
                clips.forEach { c ->
                    arr.put(
                        JSONObject().apply {
                            put("t", c.text)
                            put("m", c.time)
                            put("fav", c.isFavorite)
                            if (c.imageRef != null) put("img", c.imageRef)
                            if (c.sourceDevice != null) put("src", c.sourceDevice)
                            if (c.sourcePackage != null) put("pkg", c.sourcePackage)
                            if (c.sourceApp != null) put("app", c.sourceApp)
                            put("man", c.isManual)
                        }
                    )
                }
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
                            imageRef = if (o.isNull("img")) null else o.optString("img"),
                            sourceDevice = if (o.isNull("src")) null else o.optString("src"),
                            sourcePackage = if (o.isNull("pkg")) null else o.optString("pkg"),
                            sourceApp = if (o.isNull("app")) null else o.optString("app"),
                            isManual = o.optBoolean("man", false)
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
                            imageRef = if (o.isNull("img")) null else o.optString("img"),
                            sourceDevice = if (o.isNull("src")) null else o.optString("src"),
                            sourcePackage = if (o.isNull("pkg")) null else o.optString("pkg"),
                            sourceApp = if (o.isNull("app")) null else o.optString("app"),
                            isManual = o.optBoolean("man", false)
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
                        if (c.sourceDevice != null) put("src", c.sourceDevice)
                        if (c.sourcePackage != null) put("pkg", c.sourcePackage)
                        if (c.sourceApp != null) put("app", c.sourceApp)
                        put("man", c.isManual)
                    }
                )
            }
            context.getSharedPreferences(PREFS_CAPTURED, Context.MODE_PRIVATE)
                .edit().putString("clips", arr.toString()).apply()
        }
    }
}
