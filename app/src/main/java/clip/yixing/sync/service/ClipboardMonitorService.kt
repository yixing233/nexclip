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
    private var lastLocalText: String? = null
    private var lastLocalImgHash: String? = null
    private var lastLocalTime: Long = 0L
    private lateinit var clipboard: ClipboardManager
    private var push: PushClient? = null
    @Volatile
    private var isApplyingRemote = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        startForeground(SyncNotificationManager.NOTIFICATION_ID_FOREGROUND, buildNotification())
        clipboard.addPrimaryClipChangedListener(listener)
        isRunning.value = true
        connectPush()

        // 初始化 Shizuku 免 Root 剪贴板监听
        clip.yixing.sync.shizuku.ShizukuClipboardManager.init(this)
        scope.launch {
            clip.yixing.sync.shizuku.ShizukuClipboardManager.status.collect {
                updateShizukuListenerState()
            }
        }

        // 监听连接状态以实时更新前台通知状态
        scope.launch {
            isServerConnected.collect {
                refreshForegroundNotification()
            }
        }

        // 监听捕获历史变化以实时更新常驻通知最新条目
        scope.launch {
            captured.collect {
                refreshForegroundNotification()
            }
        }
    }

    private fun updateShizukuListenerState() {
        val captureMethod = SyncSettings.captureMethod(this)
        val shizukuActive = clip.yixing.sync.shizuku.ShizukuClipboardManager.status.value == clip.yixing.sync.shizuku.ShizukuClipboardManager.ShizukuStatus.AUTHORIZED_RUNNING
        if ((captureMethod == clip.yixing.sync.util.CaptureMethod.AUTO || captureMethod == clip.yixing.sync.util.CaptureMethod.SHIZUKU) && shizukuActive) {
            clip.yixing.sync.shizuku.ShizukuClipboardMonitor.start(this)
        } else {
            clip.yixing.sync.shizuku.ShizukuClipboardMonitor.stop()
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(SyncNotificationManager.NOTIFICATION_ID_FOREGROUND, buildNotification())
        updateShizukuListenerState()
        return START_STICKY
    }

    override fun onDestroy() {
        if (instance == this) instance = null
        uploadJob?.cancel()
        heartbeatJob?.cancel()
        heartbeatJob = null
        push?.disconnect()
        push = null
        clipboard.removePrimaryClipChangedListener(listener)
        clip.yixing.sync.shizuku.ShizukuClipboardMonitor.stop()
        clip.yixing.sync.shizuku.ShizukuClipboardManager.unregisterClipboardListener()
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
                notifyPush(entry.deviceName ?: "其他设备", "[图片]", imgRef)
            } else if (!text.isNullOrBlank()) {
                val hash = sha256(text)
                lastUploadHash = hash
                addCaptured(ctx, text, null, sourceDevice = entry.deviceName ?: "其他设备", isManual = isManual)
                isApplyingRemote = true
                val newClip = ClipData.newPlainText("NexClip", text)
                try {
                    val method = SyncSettings.captureMethod(ctx)
                    val useShizuku = (method == clip.yixing.sync.util.CaptureMethod.AUTO || method == clip.yixing.sync.util.CaptureMethod.SHIZUKU)
                    val shizukuApplied = if (useShizuku) clip.yixing.sync.shizuku.ShizukuClipboardManager.setPrimaryClip(newClip) else false
                    if (!shizukuApplied) {
                        clipboard.setPrimaryClip(newClip)
                    }
                } finally {
                    scope.launch {
                        delay(500)
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

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        if (isApplyingRemote) {
            android.util.Log.i("NexClip", "OnPrimaryClipChangedListener ignored (applying remote push)")
            return@OnPrimaryClipChangedListener
        }
        val clipData = runCatching { clipboard.primaryClip }.getOrNull() ?: return@OnPrimaryClipChangedListener
        processIncomingClip(clipData)
    }

    fun processIncomingClip(clipData: ClipData, overrideSourcePkg: String? = null) {
        val item = clipData.getItemAt(0) ?: return

        val isMarkedInternal = clipData.description?.extras?.getBoolean("is_nexclip_internal") == true
        val sourcePkg = overrideSourcePkg?.takeIf { it.isNotBlank() } ?: AppSourceHelper.resolvePackageName(this, clipData)
        // 关键防护 1: 若剪贴板内容由 NexClip 自身在软件内复制，保持原位且不生成新条目、不上报
        if (isInternalCopy || sourcePkg == packageName || isMarkedInternal) {
            android.util.Log.i("NexClip", "Incoming clip ignored (source is self app or marked internal, maintaining original state)")
            return
        }
        if (SyncSettings.isPackageFiltered(this, sourcePkg)) {
            android.util.Log.i("NexClip", "Incoming clip ignored (source package $sourcePkg is blacklisted)")
            return
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
                // 关键防护 2: 检查是否与近期应用内点击复制的图片 Hash 一致
                if (isRecentInternalCopy(null, hash)) {
                    android.util.Log.i("NexClip", "Incoming image ignored (matches recent in-app copied image hash $hash)")
                    return
                }

                val now = System.currentTimeMillis()
                if (hash == lastLocalImgHash && (now - lastLocalTime) < 800L) {
                    android.util.Log.i("NexClip", "Incoming image ignored (debounced within 800ms)")
                    return
                }
                lastLocalImgHash = hash
                lastLocalTime = now
                lastUploadHash = hash

                // 关键修复: 1. 持久化缓存图片到本地磁盘
                ImageLoader.saveBytesToDisk(this, hash, bytes)

                // 关键修复: 2. 存入捕获历史并携带 imageRef = hash
                addCaptured(this, "[图片]", hash, sourceDevice = "本机", sourcePackage = sourcePkg, sourceApp = sourceApp)
                refreshForegroundNotification()

                val clip = CapturedClip(
                    text = "[图片]",
                    time = System.currentTimeMillis(),
                    imageRef = hash,
                    sourceDevice = "本机",
                    sourcePackage = sourcePkg,
                    sourceApp = sourceApp
                )
                SyncNotificationManager.notifyNewClip(this, clip, sourceApp ?: "本机", isPush = false)

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
                return
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
        android.util.Log.i("NexClip", "primaryClip read: hasClip=true, textLen=${text?.length ?: 0}, srcPkg=$sourcePkg, srcApp=$sourceApp")
        if (text.isNullOrBlank() || text.length > 500_000) return
        if (SyncSettings.isContentFiltered(this, text)) return

        val hash = sha256(text)
        // 关键防护 2: 检查是否与近期应用内点击复制的文本 Hash 一致
        if (isRecentInternalCopy(text, null)) {
            android.util.Log.i("NexClip", "Incoming text ignored (matches recent in-app copied text hash)")
            return
        }

        val now = System.currentTimeMillis()
        if (text == lastLocalText && (now - lastLocalTime) < 800L) {
            android.util.Log.i("NexClip", "Incoming text ignored (debounced within 800ms)")
            return
        }
        lastLocalText = text
        lastLocalTime = now
        lastUploadHash = hash

        addCaptured(this, text, null, sourceDevice = "本机", sourcePackage = sourcePkg, sourceApp = sourceApp)
        refreshForegroundNotification()

        val clip = CapturedClip(
            text = text,
            time = System.currentTimeMillis(),
            sourceDevice = "本机",
            sourcePackage = sourcePkg,
            sourceApp = sourceApp
        )
        SyncNotificationManager.notifyNewClip(this, clip, sourceApp ?: "本机", isPush = false)

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

    private fun notifyPush(deviceName: String, text: String, imgRef: String? = null) {
        val clip = CapturedClip(
            text = text,
            imageRef = imgRef,
            time = System.currentTimeMillis(),
            sourceDevice = deviceName,
            sourceApp = "来自 $deviceName"
        )
        SyncNotificationManager.notifyNewClip(this, clip, deviceName, isPush = true)
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

        @Volatile
        var instance: ClipboardMonitorService? = null
            private set

        fun onClipCaptured(clipData: ClipData, sourcePkg: String? = null) {
            instance?.processIncomingClip(clipData, sourcePkg)
        }

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

        /**
         * 记录由 App 内部发起复制的内容 Hash (SHA256 文本或 imageRef) 及时间戳
         */
        private val internalCopyHashes = java.util.concurrent.ConcurrentHashMap<String, Long>()

        @Volatile
        var isInternalCopy: Boolean = false
            private set

        fun sha256(s: String): String {
            val md = java.security.MessageDigest.getInstance("SHA-256")
            return md.digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
        }

        fun sha256Bytes(bytes: ByteArray): String {
            val md = java.security.MessageDigest.getInstance("SHA-256")
            return md.digest(bytes).joinToString("") { "%02x".format(it) }
        }

        fun registerInternalCopy(text: String?, imageRef: String? = null) {
            val hash = if (!imageRef.isNullOrBlank()) {
                imageRef
            } else if (!text.isNullOrBlank()) {
                sha256(text)
            } else {
                null
            }
            if (hash != null) {
                val now = System.currentTimeMillis()
                internalCopyHashes[hash] = now
                internalCopyHashes.entries.removeIf { now - it.value > 30_000L }
            }
        }

        fun isRecentInternalCopy(text: String?, imageRef: String? = null): Boolean {
            val hash = if (!imageRef.isNullOrBlank()) {
                imageRef
            } else if (!text.isNullOrBlank()) {
                sha256(text)
            } else {
                null
            }
            if (hash == null) return false
            val time = internalCopyHashes[hash] ?: return false
            return (System.currentTimeMillis() - time) <= 30_000L
        }

        fun copyToClipboardInternal(
            context: Context,
            clipData: ClipData,
            rawText: String? = null,
            imageRef: String? = null
        ) {
            isInternalCopy = true
            val text = rawText ?: runCatching { clipData.getItemAt(0)?.text?.toString() }.getOrNull()
            registerInternalCopy(text, imageRef)

            runCatching {
                val extras = clipData.description?.extras ?: android.os.PersistableBundle()
                extras.putBoolean("is_nexclip_internal", true)
                extras.putString("source_pkg", context.packageName)
                clipData.description?.extras = extras
            }

            try {
                val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(clipData)
            } finally {
                android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({
                    isInternalCopy = false
                }, 1500L)
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
            // 查找是否已经存在相同内容的条目 (图片匹配 imageRef，文本匹配 text)
            val existingIndex = captured.value.indexOfFirst {
                if (!imageRef.isNullOrBlank()) {
                    it.imageRef == imageRef
                } else {
                    it.imageRef.isNullOrBlank() && it.text == text
                }
            }

            // 如果已经提供了 sourceApp 或者来自其他远程设备，则不通过本地包名反查覆盖
            val isRemote = !sourceDevice.isNullOrBlank() && sourceDevice != "本机"
            val resolvedApp = if (isRemote) {
                sourceApp
            } else {
                sourceApp ?: AppSourceHelper.resolveAppName(context, sourcePackage)
            }

            // 如果是远程设备来源，严禁保留/继承本地包名，避免被当作本机 App 显示图标与来源
            val finalSourceDevice = sourceDevice ?: (if (existingIndex != -1) captured.value[existingIndex].sourceDevice else null)
            val isFinalRemote = !finalSourceDevice.isNullOrBlank() && finalSourceDevice != "本机"

            val finalSourcePackage = if (isFinalRemote) {
                null
            } else {
                sourcePackage ?: (if (existingIndex != -1) captured.value[existingIndex].sourcePackage else null)
            }

            val list = if (existingIndex != -1) {
                // 如果是软件外复制的现有条目 -> 将原有条目的时间更新为最新，并移到首位，保留收藏/标签等原有属性
                val existing = captured.value[existingIndex]
                val updated = existing.copy(
                    time = System.currentTimeMillis(),
                    sourceDevice = finalSourceDevice,
                    sourcePackage = finalSourcePackage,
                    sourceApp = if (isFinalRemote) (sourceApp ?: existing.sourceApp?.takeIf { existing.sourceDevice != "本机" }) else (resolvedApp ?: existing.sourceApp),
                    isManual = isManual || existing.isManual
                )
                listOf(updated) + captured.value.filterIndexed { index, _ -> index != existingIndex }
            } else {
                // 新条目 -> 插入首位
                listOf(
                    CapturedClip(
                        text = text,
                        time = System.currentTimeMillis(),
                        imageRef = imageRef,
                        sourceDevice = finalSourceDevice,
                        sourcePackage = finalSourcePackage,
                        sourceApp = if (isFinalRemote) sourceApp else resolvedApp,
                        isManual = isManual
                    )
                ) + captured.value
            }

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

        fun updateMonitoringState(context: Context) {
            val intent = Intent(context, ClipboardMonitorService::class.java).apply {
                action = "clip.yixing.sync.ACTION_UPDATE_MONITOR"
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                runCatching { context.startForegroundService(intent) }
            } else {
                runCatching { context.startService(intent) }
            }
        }
    }
}
