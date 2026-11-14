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
import clip.yixing.sync.root.AccessibilityGuard
import clip.yixing.sync.util.AppSourceHelper
import clip.yixing.sync.util.ImageLoader
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import android.widget.Toast
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

    /**
     * root 守护无障碍服务的巡检任务。
     *
     * 刻意独立于推送心跳:无障碍服务被关闭时,推送通道可能同时也断了,
     * 若把巡检挂在"已连接"分支里,最需要它的时候反而不跑。
     *
     * 30 秒一轮;$AccessibilityGuard 内部还有 60 秒节流兜底,不会频繁触发 su。
     */
    private var accessibilityGuardJob: Job? = null

    /**
     * 单张图片读入内存的上限。
     *
     * 这是防 OOM 的保护,不是上传判据 —— 上传前会把图压到服务端允许的 10MB 内
     * (见 ImageLoader.compressForUpload)。超过此值的图会直接跳过并记录日志,
     * 因为整张读进内存再做任何处理都已经有内存风险。
     */
    private val MAX_RAW_IMAGE_READ_BYTES = 64 * 1024 * 1024

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
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        startForeground(SyncNotificationManager.NOTIFICATION_ID_FOREGROUND, buildNotification())
        clipboard.addPrimaryClipChangedListener(listener)
        isRunning.value = true
        connectPush()
        startAccessibilityGuard()

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
        accessibilityGuardJob?.cancel()
        accessibilityGuardJob = null
        push?.disconnect()
        push = null
        clipboard.removePrimaryClipChangedListener(listener)
        clip.yixing.sync.shizuku.ShizukuClipboardMonitor.stop()
        clip.yixing.sync.shizuku.ShizukuClipboardManager.unregisterClipboardListener()
        isRunning.value = false
        isServerConnected.value = false
        serverConnectionState.value = ServerConnectionState.DISCONNECTED
        // 落盘已改到后台单线程执行, 服务销毁时把队列里待写的记录排空,
        // 否则刚捕获的最后一条可能随进程回收一起丢失。
        flushPendingPersist()
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
                // 连接建立后拉一次文件互传列表(服务端是权威来源,这里只做展示缓存)
                scope.launch(Dispatchers.IO) {
                    try {
                        if (SyncSettings.isPaired(ctx) && SyncSettings.deviceToken(ctx).isNotBlank()) {
                            val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceToken(ctx))
                            val (items, _) = api.listFiles()
                            fileTransfers.value = items
                        }
                    } catch (_: Exception) {
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
        // 文件互传推送:更新内存列表 + 系统通知。
        // 关键语义:文件不在剪贴板历史里,绝不走 addCaptured/自动粘贴链路。
        client.onFileTransferred = { info ->
            // 过滤本机上传的回显(服务端按用户组广播,包含上传者自己)
            if (info.deviceId != SyncSettings.ensureDeviceId(ctx)) {
                fileTransfers.value = listOf(info) + fileTransfers.value.filter { it.fileId != info.fileId }
                if (info.fileId in autoSaveFileIds.value) {
                    // 确认制:本端已接受 → 文件本体此刻到达,自动下载保存
                    autoSaveFileIds.value = autoSaveFileIds.value - info.fileId
                    scope.launch(Dispatchers.IO) {
                        var saved = false
                        try {
                            val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceToken(ctx))
                            val bytes = api.downloadFile(info.fileId)
                            if (bytes != null) {
                                clip.yixing.sync.ui.saveToDownloads(ctx, info.fileName, bytes)
                                saved = true
                            }
                        } catch (e: Exception) {
                            android.util.Log.w("ClipboardMonitor", "确认制自动保存失败: ${e.message}")
                        }
                        CoroutineScope(Dispatchers.Main).launch {
                            Toast.makeText(
                                ctx,
                                if (saved) "已接受,文件保存到 Downloads" else "已接受,但保存失败",
                                Toast.LENGTH_SHORT
                            ).show()
                        }
                    }
                } else {
                    SyncNotificationManager.notifyFileReceived(ctx, info.fileName, info.deviceName ?: "其他设备")
                }
            } else {
                fileTransfers.value = listOf(info) + fileTransfers.value.filter { it.fileId != info.fileId }
            }
        }
        client.onFileTransferRequested = { info ->
            // 确认制:目标设备收到发送请求 → 通知栏按钮 接受/拒绝(网络操作在 NotificationActionReceiver)
            if (info.deviceId != SyncSettings.ensureDeviceId(ctx)) {
                pendingFileRequests.value = listOf(info) + pendingFileRequests.value.filter { it.fileId != info.fileId }
                SyncNotificationManager.notifyFileConsentRequest(ctx, info.fileName, info.deviceName ?: "其他设备", info.fileId)
            }
        }
        client.onFileTransferResponded = { fileId, action, _ ->
            // 响应事件只发给发送方设备;安卓当前仅作为接收方消费确认制,发送任务状态在发送端 UI
        }
        client.onFileDeleted = { fileId ->
            fileTransfers.value = fileTransfers.value.filter { it.fileId != fileId }
            pendingFileRequests.value = pendingFileRequests.value.filter { it.fileId != fileId }
        }
        push = client
        client.connect()
    }

    /**
     * 启动 root 守护巡检(仅当用户开启该功能时真正干活)。
     *
     * 所有 su 调用都在 Dispatchers.IO 上执行 —— su 可能阻塞数秒等用户授权,
     * 放主线程会直接 ANR。
     */
    private fun startAccessibilityGuard() {
        val ctx = this
        if (!SyncSettings.accessibilityGuardEnabled(ctx)) {
            android.util.Log.i("NexClip", "无障碍守护未启用(用户设置),跳过巡检")
            return
        }
        accessibilityGuardJob?.cancel()
        accessibilityGuardJob = scope.launch(Dispatchers.IO) {
            // 启动时先检查一次:应用被系统重启后无障碍可能已被关掉
            runCatching { AccessibilityGuard.ensureEnabled(ctx) }
                .onFailure { android.util.Log.w("NexClip", "无障碍守护首次检查失败: ${it.message}") }

            while (isActive) {
                delay(30_000)
                try {
                    if (!SyncSettings.accessibilityGuardEnabled(ctx)) continue
                    val result = AccessibilityGuard.ensureEnabled(ctx)
                    when (result) {
                        AccessibilityGuard.LastResult.Success ->
                            android.util.Log.i("NexClip", "无障碍服务已被 root 自动重新启用")
                        AccessibilityGuard.LastResult.NoRoot ->
                            android.util.Log.d("NexClip", "无障碍守护:root 不可用或未授权")
                        AccessibilityGuard.LastResult.Failed ->
                            android.util.Log.w("NexClip", "无障碍守护:写入后仍未生效,可能被 ROM 拦截")
                        else -> Unit // NotNeeded / Throttled / Never:无需日志
                    }
                } catch (e: Exception) {
                    android.util.Log.w("NexClip", "无障碍守护巡检异常: ${e.javaClass.simpleName} ${e.message}")
                }
            }
        }
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

            // 读入上限:这里只是防止把几百 MB 的图整体读进内存导致 OOM,
            // 与"能否上传"无关 —— 上传前 compressForUpload 会压到服务端 10MB 以内。
            // 服务端上限见 ImageLoader.SERVER_IMAGE_LIMIT_BYTES。
            if (bytes != null && bytes.isNotEmpty() && bytes.size <= MAX_RAW_IMAGE_READ_BYTES) {
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

                        // 上传前压缩:服务端只收 10MB,而手机原图/长截图经常超过。
                        // 本地缓存的仍是原图(上面已 saveBytesToDisk),这里只压上传的那份。
                        val compressed = ImageLoader.compressForUpload(ctx, bytes, uri)
                        if (compressed == null) {
                            android.util.Log.w("NexClip", "图片无法压缩(可能已损坏),跳过上传: ${bytes.size} bytes")
                            SyncNotificationManager.notifyUploadFailed(ctx, "图片过大或已损坏,未能同步")
                            return@launch
                        }
                        if (compressed.degraded) {
                            android.util.Log.i(
                                "NexClip",
                                "图片已压缩上传: ${compressed.originalWidth}x${compressed.originalHeight} " +
                                    "-> ${compressed.width}x${compressed.height} " +
                                    "(${bytes.size} -> ${compressed.bytes.size} bytes, ${compressed.mimeType})"
                            )
                        }

                        val api = SyncApi(SyncSettings.serverUrl(ctx), SyncSettings.ensureDeviceId(ctx), SyncSettings.deviceToken(ctx))
                        api.uploadImage(
                            compressed.bytes,
                            SyncSettings.ensureDeviceId(ctx),
                            SyncSettings.deviceName(ctx),
                            mimeType = compressed.mimeType,
                        )
                    } catch (e: clip.yixing.sync.data.ApiException) {
                        // 只有服务端明确表示"凭证失效 / 设备被移除"时才清空本地配对。
                        // 403(身份不匹配)与"缺少凭证"都不清 —— 凭证本身可能仍然有效,
                        // 误清会让用户白配对一次(2026-10-23 线上故障即该模式的放大版)。
                        if (SyncSettings.shouldClearPairingOn(e)) {
                            android.util.Log.w("NexClip", "图片上传鉴权失败,清除配对: ${e.message}")
                            SyncSettings.clearPairing(ctx)
                            connectPush()
                        } else {
                            android.util.Log.w("NexClip", "图片上传失败(保留配对): status=${e.statusCode} ${e.message}")
                        }
                    } catch (e: Exception) {
                        // 不得静默:服务端 413(体积超限)等都在这里,用户此前完全无感知
                        android.util.Log.w("NexClip", "图片上传异常(保留配对): ${e.javaClass.simpleName} ${e.message}")
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
                if (SyncSettings.shouldClearPairingOn(e)) {
                    android.util.Log.w("NexClip", "文本上传鉴权失败,清除配对: ${e.message}")
                    SyncSettings.clearPairing(ctx)
                    connectPush()
                } else {
                    // 保留配对:下一条变化会再试,不必惊动用户
                    android.util.Log.w("NexClip", "文本上传失败(保留配对): status=${e.statusCode} ${e.message}")
                }
            } catch (e: Exception) {
                android.util.Log.w("NexClip", "文本上传异常(保留配对): ${e.javaClass.simpleName} ${e.message}")
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

        /**
         * 文件互传列表(最新在前)。
         *
         * 仅内存缓存,权威来源是服务端 /api/files;不持久化 —— 文件与剪贴板
         * 不同,重连后首拉即可恢复,持久化反而会造成与服务端状态漂移。
         */
        val fileTransfers = MutableStateFlow<List<clip.yixing.sync.data.FileTransferInfo>>(emptyList())

        /**
         * 确认制传输:待本端确认的发送请求(最新在前)。
         * 仅内存缓存,权威来源是服务端 /api/files 的 pending 记录。
         */
        val pendingFileRequests = MutableStateFlow<List<clip.yixing.sync.data.FileTransferInfo>>(emptyList())

        /** 确认制:已接受、等待文件本体到达后自动保存的 fileId 集合 */
        val autoSaveFileIds = MutableStateFlow<Set<String>>(emptySet())

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

        @Volatile
        var isApplyingRemote: Boolean = false

        /** 十六进制字符表, 供 [toHex] 使用 */
        private val HEX_CHARS = "0123456789abcdef".toCharArray()

        /**
         * 字节数组转小写十六进制。
         *
         * 原实现用 `joinToString { "%02x".format(it) }`: 每个字节都要走一次 String.format
         * (即一次 Formatter 构造与解析), 对大文本/大图片的哈希而言开销远超散列本身。
         * 这里改为查表, 结果与原实现逐字符相同。
         */
        private fun toHex(bytes: ByteArray): String {
            val out = CharArray(bytes.size * 2)
            var i = 0
            for (b in bytes) {
                val v = b.toInt() and 0xFF
                out[i++] = HEX_CHARS[v ushr 4]
                out[i++] = HEX_CHARS[v and 0x0F]
            }
            return String(out)
        }

        fun sha256(s: String): String {
            val md = java.security.MessageDigest.getInstance("SHA-256")
            return toHex(md.digest(s.toByteArray()))
        }

        fun sha256Bytes(bytes: ByteArray): String {
            val md = java.security.MessageDigest.getInstance("SHA-256")
            return toHex(md.digest(bytes))
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

            captured.value = persist(context, list)
        }

        fun toggleFavorite(context: Context, clip: CapturedClip) {
            val list = captured.value.map {
                if (it == clip || it.id == clip.id) it.copy(isFavorite = !it.isFavorite) else it
            }
            captured.value = persist(context, list)
        }

        fun updateClip(context: Context, oldClip: CapturedClip, newText: String) {
            val list = captured.value.map {
                if (it == oldClip || it.id == oldClip.id) it.copy(text = newText) else it
            }
            captured.value = persist(context, list)
        }

        fun deleteAt(context: Context, index: Int) {
            val list = captured.value.toMutableList()
            if (index !in list.indices) return
            list.removeAt(index)
            captured.value = persist(context, list)
        }

        fun deleteClip(context: Context, clip: CapturedClip) {
            val list = captured.value.filterNot { it == clip || it.id == clip.id }
            captured.value = persist(context, list)
        }

        fun deleteClips(context: Context, targetClips: Collection<CapturedClip>) {
            val ids = targetClips.map { it.id }.toSet()
            val list = captured.value.filterNot { it.id in ids }
            captured.value = persist(context, list)
        }

        /** 清空记录(支持保留收藏项) */
        fun clearAll(context: Context, keepFavorites: Boolean = true) {
            val list = if (keepFavorites) captured.value.filter { it.isFavorite } else emptyList()
            captured.value = persist(context, list)
        }

        fun clear(context: Context) = clearAll(context, true)

        /** 恢复整份记录(撤销清空) */
        fun replaceAll(context: Context, list: List<CapturedClip>) {
            captured.value = persist(context, list)
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
            captured.value = persist(context, list)
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
            captured.value = persist(context, combined)
            return importedList.size
        }

        /** 启动时恢复本地记录 */
        fun loadCaptured(context: Context) {
            // 落盘已改到后台线程, 读取前必须先把队列排空: 否则可能读到尚未写入的旧快照,
            // 再用它覆盖内存里更新的记录(原同步落盘不存在这个窗口)。
            flushPendingPersist()
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

        /**
         * 落盘用的单线程执行器。
         *
         * 必须串行: [persist] 每次都整体覆盖写同一条 key, 若并发执行, 后入队的旧快照可能
         * 覆盖先入队的新快照, 导致磁盘内容回退。单线程天然保证 FIFO。
         */
        private val persistExecutor = java.util.concurrent.Executors.newSingleThreadExecutor { r ->
            Thread(r, "NexClip-Persist").apply { isDaemon = true }
        }

        /**
         * 等待已排队的落盘任务全部完成。
         *
         * 仅在进程即将结束的路径上调用(服务销毁)。加超时是因为: 若落盘线程卡在磁盘 IO,
         * onDestroy 绝不能一起卡住 —— 那样会触发 ANR, 比丢一条记录严重得多。
         */
        fun flushPendingPersist() {
            runCatching {
                val latch = java.util.concurrent.CountDownLatch(1)
                persistExecutor.execute { latch.countDown() }
                latch.await(2, java.util.concurrent.TimeUnit.SECONDS)
            }
        }

        /**
         * 落盘并返回真正被保存下来的列表(已按「记录上限」裁剪、去重、按时间倒序)。
         *
         * 调用方必须用返回值回写 [captured],否则内存里的列表会比磁盘上的长
         * (例如上限 100 时内存停在 101),重启后又突然变短,并导致记录页触底分页
         * 永远差最后一条而一直显示"正在加载更多"。
         *
         * 裁剪与返回值同步完成(内存态必须立即可见, 否则界面会闪回旧列表); 而 JSON
         * 序列化与写入挪到后台线程 —— 本方法在剪贴板捕获路径上由主线程调用, 每次都要
         * 序列化最多 200 条记录, 是主线程上最大的一笔同步开销。
         */
        private fun persist(context: Context, list: List<CapturedClip>): List<CapturedClip> {
            val maxHistory = SyncSettings.maxHistory(context)
            val favorites = list.filter { it.isFavorite }
            val nonFavorites = list.filterNot { it.isFavorite }.take(maxHistory)
            val toSave = (favorites + nonFavorites).distinctBy { it.id }.sortedByDescending { it.time }

            val appContext = context.applicationContext
            persistExecutor.execute {
                runCatching {
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
                    val json = arr.toString()
                    appContext.getSharedPreferences(PREFS_CAPTURED, Context.MODE_PRIVATE)
                        .edit().putString("clips", json).apply()
                }
            }
            return toSave
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
