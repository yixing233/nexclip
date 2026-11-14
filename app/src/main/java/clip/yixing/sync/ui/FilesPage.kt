package clip.yixing.sync.ui

import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.data.FileTransferInfo
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.SnackType
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.theme.MiuixTheme
import java.io.File
import java.text.SimpleDateFormat
import java.util.Locale

/**
 * 文件互传页 —— 即时传输,两阶段交互:
 *
 *   ① 选择文件 → 进入「待发送」队列(可逐个移除,不发送)
 *   ② 确认发送 → 任务卡显示逐文件进度/速率/耗时,完成后即送达目标设备
 *
 * 发送前必须选目标设备(设备选择器,仿即时互传页);服务器按 deviceIds 定向推送。
 * 关闭页面即结束会话展示;服务器侧留存文件供网页端下载,但本页不做历史浏览。
 *
 * 隐私/语义约束:文件不进剪贴板历史;收到推送只展示,不触发任何粘贴行为。
 */

/** 待发送文件(本地队列项) */
private data class PendingFile(
    val uri: Uri,
    val displayName: String,
    val size: Long,
    val previewBitmap: ImageBitmap?,
)

/** 一条发送任务(确认后) */
private data class SendTask(
    val key: String,
    val fileName: String,
    val size: Long,
    val sent: Long,
    val done: Boolean,
    val failed: Boolean,
    val errorMessage: String? = null,
    val elapsedMs: Long = 0,
) {
    val speedBytesPerSec: Long
        get() = if (elapsedMs > 0) sent * 1000 / elapsedMs else 0
}

@Composable
fun FilesPage(
    snackbarHostState: top.yukonga.miuix.kmp.basic.SnackbarHostState?,
    pendingShareUris: List<Uri> = emptyList(),
    onShareConsumed: () -> Unit = {},
    onBack: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val selfDeviceId = remember { SyncSettings.ensureDeviceId(context) }
    val isServerConnected = ClipboardMonitorService.isServerConnected.collectAsState().value

    // ---- 阶段①:待发送队列 ----
    val pendingFiles = remember { mutableStateListOf<PendingFile>() }

    // ---- 阶段②:发送任务(确认后) ----
    val sendTasks = remember { mutableStateListOf<SendTask>() }
    val sending = sendTasks.isNotEmpty() && sendTasks.any { !it.done && !it.failed }

    // ---- 目标设备(仿即时互传页) ----
    var deviceList by remember { mutableStateOf<List<clip.yixing.sync.data.DeviceInfo>>(emptyList()) }
    var selectedDeviceIds by remember { mutableStateOf<Set<String>>(emptySet()) }

    // 背景毛玻璃 Backdrop(顶栏/悬浮按钮折射用)
    val barSurface = MiuixTheme.colorScheme.surface
    val pageBackdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )

    val refreshDevices: suspend () -> Unit = {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isNotBlank() && SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
            try {
                val api = SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
                val remote = withContext(Dispatchers.IO) { api.getDevices() }
                deviceList = remote.filter { it.id != selfDeviceId }
                // 默认全选在线设备
                selectedDeviceIds = deviceList.filter { it.online }.map { it.id }.toSet()
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                android.util.Log.w("NexClipFiles", "refreshDevices: ${e.message}")
            }
        } else {
            deviceList = emptyList()
            selectedDeviceIds = emptySet()
        }
    }

    LaunchedEffect(Unit) { refreshDevices() }

    fun buildApi(): SyncApi? {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isBlank() || !SyncSettings.isPaired(context) || SyncSettings.deviceToken(context).isBlank()) return null
        return SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
    }

    fun handleError(e: Exception, fallback: String) {
        scope.launch {
            if (e is clip.yixing.sync.data.ApiException && SyncSettings.shouldClearPairingOn(e)) {
                snackbarHostState?.showAppSnack("配对已失效,请重新配对", SnackType.Error)
            } else {
                snackbarHostState?.showAppSnack(e.message ?: fallback, SnackType.Error)
            }
        }
    }

    /** uri → PendingFile(含缩略图预加载) */
    fun loadPendingFile(uri: Uri): PendingFile? = runCatching {
        val resolver = context.contentResolver
        val name = queryDisplayName(context, uri) ?: "file_${System.currentTimeMillis()}"
        val size = runCatching {
            resolver.openAssetFileDescriptor(uri, "r")?.use { it.length } ?: 0L
        }.getOrDefault(0L)
        val bitmap = if (name.substringAfterLast('.', "").lowercase() in
            setOf("png", "jpg", "jpeg", "gif", "webp", "bmp")
        ) {
            runCatching {
                resolver.openInputStream(uri)?.use { input ->
                    val opts = BitmapFactory.Options().apply { inSampleSize = 4 }
                    BitmapFactory.decodeStream(input, null, opts)?.asImageBitmap()
                }
            }.getOrNull()
        } else null
        PendingFile(uri, name, size, bitmap)
    }.getOrNull()

    fun addPending(uris: List<Uri>) {
        scope.launch {
            for (uri in uris) {
                val pf = withContext(Dispatchers.IO) { loadPendingFile(uri) }
                if (pf != null && pendingFiles.none { it.uri == uri }) pendingFiles.add(pf)
            }
        }
    }

    LaunchedEffect(pendingShareUris) {
        if (pendingShareUris.isNotEmpty()) {
            addPending(pendingShareUris)
            onShareConsumed()
        }
    }

    val pickFilesLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenMultipleDocuments()
    ) { uris: List<Uri> ->
        addPending(uris)
    }

    /** 确认发送:逐文件上传,任务卡实时更新进度/速率 */
    fun confirmSend() {
        if (pendingFiles.isEmpty()) {
            scope.launch { snackbarHostState?.showAppSnack("请先选择要发送的文件", SnackType.Info) }
            return
        }
        if (selectedDeviceIds.isEmpty()) {
            scope.launch { snackbarHostState?.showAppSnack("请至少选择一台接收设备", SnackType.Info) }
            return
        }
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置", SnackType.Error) }
            return
        }
        val targets = selectedDeviceIds.toSet()
        val queue = pendingFiles.toList()
        pendingFiles.clear()

        scope.launch {
            for (pf in queue) {
                val key = "send_${System.currentTimeMillis()}_${pf.displayName}"
                sendTasks.add(0, SendTask(key, pf.displayName, pf.size, 0, done = false, failed = false))
                fun update(t: SendTask) {
                    val idx = sendTasks.indexOfFirst { it.key == key }
                    if (idx >= 0) sendTasks[idx] = t
                }
                // 本地缓存临时文件(进度回调要流式读盘,拿稳定 File 引用)
                val temp = withContext(Dispatchers.IO) { copyUriToTemp(context, pf.uri) }
                if (temp == null) {
                    update(SendTask(key, pf.displayName, pf.size, 0, done = true, failed = true, errorMessage = "无法读取文件"))
                    continue
                }
                try {
                    val startMs = System.currentTimeMillis()
                    var lastSent = 0L
                    val info = withContext(Dispatchers.IO) {
                        api.uploadFile(
                            temp,
                            SyncSettings.ensureDeviceId(context),
                            SyncSettings.deviceName(context),
                            targetDeviceIds = targets,
                        ) { sent, total ->
                            // 进度回调在 IO 线程;节流到 ~100ms 一次更新 UI
                            if (sent - lastSent >= total / 20 || sent == total) {
                                lastSent = sent
                                val elapsed = System.currentTimeMillis() - startMs
                                update(
                                    SendTask(key, pf.displayName, total, sent, done = false, failed = false, elapsedMs = elapsed)
                                )
                            }
                        }
                    }
                    update(
                        SendTask(key, info.fileName, info.size, info.size, done = true, failed = false,
                            elapsedMs = System.currentTimeMillis() - startMs)
                    )
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    handleError(e, "上传失败")
                    update(SendTask(key, pf.displayName, pf.size, 0, done = true, failed = true, errorMessage = e.message ?: "上传失败"))
                } finally {
                    temp.delete()
                }
            }
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.background)
            // 消费空白区点击:覆盖层不做触摸穿透
            .clickable(
                indication = null,
                interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() },
            ) { }
    ) {
        // 1. 主内容(纵向滚动 + 越界回弹)
        Column(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(pageBackdrop)
                .verticalScroll(rememberScrollState())
                .padding(
                    start = 16.dp,
                    end = 16.dp,
                    top = 124.dp,
                    bottom = 110.dp,
                ),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            // 设备选择器(发送目标,必选)
            DevicePickerRow(
                devices = deviceList,
                selectedIds = selectedDeviceIds,
                onToggle = { id ->
                    selectedDeviceIds = if (id in selectedDeviceIds) {
                        selectedDeviceIds - id
                    } else {
                        selectedDeviceIds + id
                    }
                },
            )

            // 待确认的传输请求(确认制:目标设备需接受)
            val pendingRequests = ClipboardMonitorService.pendingFileRequests.collectAsState().value
            val requestBusy = remember { mutableStateMapOf<String, Boolean>() }
            fun handleConsent(info: clip.yixing.sync.data.FileTransferInfo, accept: Boolean) {
                if (requestBusy[info.fileId] == true) return
                requestBusy[info.fileId] = true
                scope.launch {
                    try {
                        val api = buildApi() ?: run {
                            snackbarHostState?.showAppSnack("未配对或服务器未配置", SnackType.Error)
                            return@launch
                        }
                        if (accept) {
                            api.acceptFile(info.fileId)
                            val bytes = withContext(Dispatchers.IO) { api.downloadFile(info.fileId) }
                            if (bytes != null) {
                                saveToDownloads(context, info.fileName, bytes)
                                snackbarHostState?.showAppSnack("已接受,文件保存到 Downloads", SnackType.Success)
                            } else {
                                snackbarHostState?.showAppSnack("已接受,但文件已不可下载", SnackType.Error)
                            }
                        } else {
                            api.rejectFile(info.fileId)
                            snackbarHostState?.showAppSnack("已拒绝该文件", SnackType.Success)
                        }
                        ClipboardMonitorService.pendingFileRequests.value =
                            ClipboardMonitorService.pendingFileRequests.value.filter { it.fileId != info.fileId }
                    } catch (e: Exception) {
                        snackbarHostState?.showAppSnack(e.message ?: "操作失败", SnackType.Error)
                    } finally {
                        requestBusy[info.fileId] = false
                    }
                }
            }
            if (pendingRequests.isNotEmpty()) {
                Text(
                    text = "待确认 (${pendingRequests.size})",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    modifier = Modifier.padding(top = 4.dp)
                )
                pendingRequests.forEach { req ->
                    val busy = requestBusy[req.fileId] == true
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 6.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = req.fileName,
                                fontSize = 13.sp,
                                fontWeight = FontWeight.Medium,
                                color = MiuixTheme.colorScheme.onSurface,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                text = "${req.deviceName ?: "其他设备"} 请求发送 · ${req.size / 1024} KB",
                                fontSize = 11.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            top.yukonga.miuix.kmp.basic.Button(
                                onClick = { handleConsent(req, true) },
                                enabled = !busy,
                                colors = top.yukonga.miuix.kmp.basic.ButtonDefaults.buttonColorsPrimary(),
                                modifier = Modifier.height(34.dp)
                            ) {
                                Text(text = "接受", fontSize = 12.sp)
                            }
                            top.yukonga.miuix.kmp.basic.Button(
                                onClick = { handleConsent(req, false) },
                                enabled = !busy,
                                modifier = Modifier.height(34.dp)
                            ) {
                                Text(text = "拒绝", fontSize = 12.sp)
                            }
                        }
                    }
                }
            }

            // 待发送队列
            if (pendingFiles.isNotEmpty()) {
                Text(
                    text = "待发送 (${pendingFiles.size})",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    modifier = Modifier.padding(top = 4.dp)
                )
                pendingFiles.forEach { pf ->
                    PendingFileCard(
                        file = pf,
                        onRemove = { pendingFiles.removeAll { it.uri == pf.uri } },
                    )
                }
            }

            // 发送任务(进度/速率/耗时)
            if (sendTasks.isNotEmpty()) {
                Text(
                    text = "传输记录",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    modifier = Modifier.padding(top = 4.dp)
                )
                sendTasks.forEach { task ->
                    SendTaskCard(task = task)
                }
            }

            if (pendingFiles.isEmpty() && sendTasks.isEmpty()) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 120.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Icon(
                        imageVector = LucideIcons.UploadCloud,
                        contentDescription = null,
                        modifier = Modifier.size(44.dp),
                        tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.5f)
                    )
                    Spacer(Modifier.height(12.dp))
                    Text(
                        text = "选择文件发送给在线设备",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        color = MiuixTheme.colorScheme.onSurface
                    )
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = "支持多选;发送前可移除不需要的文件。文件将即时送达所选设备。",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        textAlign = TextAlign.Center,
                        lineHeight = 18.sp
                    )
                }
            }
        }

        // 2. 顶部毛玻璃栏:返回 + 标题 + 连接徽标
        Box(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
        ) {
            BarBlurSurface(backdrop = pageBackdrop) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .statusBarsPadding()
                        .height(56.dp)
                        .padding(horizontal = 8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Back,
                            contentDescription = "返回",
                            tint = MiuixTheme.colorScheme.onSurface
                        )
                    }
                    Spacer(Modifier.width(4.dp))
                    Text(
                        text = "文件互传",
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold,
                        color = MiuixTheme.colorScheme.onSurface,
                        modifier = Modifier.weight(1f)
                    )
                    // 连接徽标(胶囊,与设备卡同一语言,替代裸绿点)
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(8.dp))
                            .background(
                                if (isServerConnected) MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                                else MiuixTheme.colorScheme.surfaceContainer
                            )
                            .padding(horizontal = 10.dp, vertical = 5.dp)
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Box(
                                modifier = Modifier
                                    .size(7.dp)
                                    .clip(RoundedCornerShape(3.5.dp))
                                    .background(
                                        if (isServerConnected) Color(0xFF34C759)
                                        else MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                            )
                            Spacer(Modifier.width(5.dp))
                            Text(
                                text = if (isServerConnected) "已连接" else "未连接",
                                fontSize = 12.sp,
                                fontWeight = FontWeight.Medium,
                                color = if (isServerConnected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                    Spacer(Modifier.width(8.dp))
                }
            }
        }

        // 3. 悬浮确认发送按钮(无底栏背景;待发送非空时才出现,带确认语义)
        if (pendingFiles.isNotEmpty()) {
            Box(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .navigationBarsPadding()
                    .padding(bottom = 20.dp)
            ) {
                top.yukonga.miuix.kmp.basic.Button(
                    onClick = { confirmSend() },
                    colors = top.yukonga.miuix.kmp.basic.ButtonDefaults.buttonColorsPrimary(),
                    modifier = Modifier
                        .padding(horizontal = 24.dp)
                        .fillMaxWidth()
                        .height(48.dp)
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        Icon(
                            imageVector = LucideIcons.Send,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp),
                            tint = Color.White
                        )
                        Text(
                            text = "发送 ${pendingFiles.size} 个文件",
                            color = Color.White,
                            fontSize = 15.sp,
                            fontWeight = FontWeight.SemiBold
                        )
                    }
                }
            }
        } else {
            // 无待发送文件:悬浮加号按钮(添加文件入口)
            Box(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .navigationBarsPadding()
                    .padding(bottom = 28.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(56.dp)
                        .clip(RoundedCornerShape(28.dp))
                        .background(MiuixTheme.colorScheme.primary)
                        .clickable { pickFilesLauncher.launch(arrayOf("*/*")) },
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = LucideIcons.Plus,
                        contentDescription = "添加文件",
                        modifier = Modifier.size(26.dp),
                        tint = Color.White
                    )
                }
            }
        }
    }
}

/** 目标设备选择行(仿即时互传页 chips;文件互传是定向推送,必须选择) */
@Composable
private fun DevicePickerRow(
    devices: List<clip.yixing.sync.data.DeviceInfo>,
    selectedIds: Set<String>,
    onToggle: (String) -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (devices.isEmpty()) {
            Text(
                text = "暂无其它设备",
                fontSize = 12.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                modifier = Modifier.padding(vertical = 6.dp)
            )
        }
        devices.forEach { dev ->
            val selected = dev.id in selectedIds
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .clip(RoundedCornerShape(10.dp))
                    .background(
                        if (selected) MiuixTheme.colorScheme.primary.copy(alpha = 0.15f)
                        else MiuixTheme.colorScheme.surfaceContainer
                    )
                    .clickable { onToggle(dev.id) }
                    .padding(horizontal = 12.dp, vertical = 8.dp)
            ) {
                Icon(
                    imageVector = resolveDeviceIcon(dev.name, dev.platform),
                    contentDescription = null,
                    modifier = Modifier.size(15.dp),
                    tint = if (selected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                )
                Spacer(Modifier.width(6.dp))
                Column {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = dev.name,
                            fontSize = 12.sp,
                            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                            color = if (selected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        if (dev.online) {
                            Spacer(Modifier.width(4.dp))
                            Box(
                                modifier = Modifier
                                    .size(5.dp)
                                    .clip(RoundedCornerShape(2.5.dp))
                                    .background(Color(0xFF34C759))
                            )
                        }
                    }
                }
            }
        }
    }
}

/** 待发送文件卡:缩略图(图片真图/类型图标) + 名字/大小/来源时间 + 移除按钮 */
@Composable
private fun PendingFileCard(
    file: PendingFile,
    onRemove: () -> Unit,
) {
    top.yukonga.miuix.kmp.basic.Card(
        modifier = Modifier.fillMaxWidth(),
        insideMargin = PaddingValues(horizontal = 12.dp, vertical = 10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            val preview = file.previewBitmap
            if (preview != null) {
                Image(
                    bitmap = preview,
                    contentDescription = null,
                    modifier = Modifier
                        .size(46.dp)
                        .clip(RoundedCornerShape(8.dp)),
                    contentScale = ContentScale.Crop
                )
            } else {
                Box(
                    modifier = Modifier
                        .size(46.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = resolveFileTypeIcon(file.displayName),
                        contentDescription = null,
                        modifier = Modifier.size(22.dp),
                        tint = resolveFileTypeIconTint(file.displayName, MiuixTheme.colorScheme.primary, MiuixTheme.colorScheme.onBackgroundVariant)
                    )
                }
            }
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = file.displayName,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onSurface,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(2.dp))
                Text(
                    text = formatFileSize(file.size),
                    fontSize = 11.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
            // 移除按钮(未发送前可反悔)
            IconButton(onClick = onRemove) {
                Icon(
                    imageVector = LucideIcons.X,
                    contentDescription = "移除",
                    tint = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
        }
    }
}

/** 发送任务卡:进度条 + 实时速率 + 已用时间 */
@Composable
private fun SendTaskCard(task: SendTask) {
    top.yukonga.miuix.kmp.basic.Card(
        modifier = Modifier.fillMaxWidth(),
        insideMargin = PaddingValues(horizontal = 12.dp, vertical = 10.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier
                    .size(46.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(MiuixTheme.colorScheme.surfaceContainer),
                contentAlignment = Alignment.Center
            ) {
                when {
                    task.failed -> Icon(
                        imageVector = LucideIcons.X,
                        contentDescription = "失败",
                        modifier = Modifier.size(22.dp),
                        tint = MiuixTheme.colorScheme.error
                    )
                    task.done -> Icon(
                        imageVector = LucideIcons.Check,
                        contentDescription = "完成",
                        modifier = Modifier.size(22.dp),
                        tint = Color(0xFF34C759)
                    )
                    else -> LoadingSpinner(modifier = Modifier.size(20.dp), color = MiuixTheme.colorScheme.primary)
                }
            }
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = task.fileName,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onSurface,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(3.dp))
                val statusText = when {
                    task.failed -> "失败: ${task.errorMessage ?: "未知错误"}"
                    task.done -> "已送达 · ${formatDuration(task.elapsedMs)}"
                    else -> {
                        val pct = if (task.size > 0) (task.sent * 100 / task.size) else 0
                        "$pct% · ${formatFileSize(task.sent)}/${formatFileSize(task.size)} · " +
                            "${formatSpeed(task.speedBytesPerSec)} · ${formatDuration(task.elapsedMs)}"
                    }
                }
                Text(
                    text = statusText,
                    fontSize = 11.sp,
                    color = when {
                        task.failed -> MiuixTheme.colorScheme.error
                        task.done -> Color(0xFF34C759)
                        else -> MiuixTheme.colorScheme.onBackgroundVariant
                    },
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                // 进度条(未完成时)
                if (!task.done && !task.failed && task.size > 0) {
                    Spacer(Modifier.height(6.dp))
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(4.dp)
                            .clip(RoundedCornerShape(2.dp))
                            .background(MiuixTheme.colorScheme.surfaceContainer)
                    ) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth(task.sent.toFloat() / task.size.toFloat())
                                .height(4.dp)
                                .clip(RoundedCornerShape(2.dp))
                                .background(MiuixTheme.colorScheme.primary)
                        )
                    }
                }
            }
        }
    }
}

/** content:// → cacheDir 临时文件(流式上传用) */
private fun copyUriToTemp(context: Context, uri: Uri): File? = runCatching {
    val name = queryDisplayName(context, uri) ?: "file"
    val dir = File(context.cacheDir, "files_upload").apply { mkdirs() }
    val target = File(dir, "${System.currentTimeMillis()}_$name")
    context.contentResolver.openInputStream(uri)?.use { input ->
        target.outputStream().use { output -> input.copyTo(output) }
    } ?: return null
    target
}.getOrNull()

private fun queryDisplayName(context: Context, uri: Uri): String? = runCatching {
    if (uri.scheme == "content") {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) cursor.getString(idx) else null
            } else null
        }
    } else {
        uri.lastPathSegment
    }
}.getOrNull()

private fun formatSpeed(bytesPerSec: Long): String = when {
    bytesPerSec <= 0 -> "--"
    bytesPerSec < 1024 * 1024 -> String.format(Locale.US, "%.1f MB/s", bytesPerSec / 1024.0 / 1024.0)
    else -> String.format(Locale.US, "%.0f KB/s", bytesPerSec / 1024.0)
}

private fun formatDuration(ms: Long): String = when {
    ms <= 0 -> ""
    ms < 60_000 -> "${ms / 1000} 秒"
    else -> "${ms / 60_000} 分 ${ms % 60_000 / 1000} 秒"
}

/** 保存到 Downloads/NexClip(同名覆盖;通知栏接受动作也会调用) */
fun saveToDownloads(context: Context, fileName: String, bytes: ByteArray): Uri? = runCatching {
    val safeName = fileName.replace(Regex("[\\\\/:*?\"<>|\\x00-\\x1f]"), "_").ifBlank { "file" }
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        val resolver = context.contentResolver
        val existing = resolver.query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.MediaColumns.DISPLAY_NAME} = ? AND ${MediaStore.MediaColumns.RELATIVE_PATH} = ?",
            arrayOf(safeName, Environment.DIRECTORY_DOWNLOADS + "/NexClip"),
            null,
        )?.use { c -> if (c.moveToFirst()) c.getLong(0) else null }
        val uri = if (existing != null) {
            ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI, existing)
        } else {
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, safeName)
                put(MediaStore.MediaColumns.MIME_TYPE, clip.yixing.sync.data.mimeTypeOf(safeName))
                put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/NexClip")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
            resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
        }
        resolver.openOutputStream(uri, "wt")?.use { it.write(bytes) }
        if (existing == null) {
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
        }
        uri
    } else {
        val targetDir = File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            "NexClip"
        ).apply { if (!exists()) mkdirs() }
        val targetFile = File(targetDir, safeName)
        java.io.FileOutputStream(targetFile).use { it.write(bytes) }
        context.sendBroadcast(Intent(Intent.ACTION_MEDIA_SCANNER_SCAN_FILE, Uri.fromFile(targetFile)))
        Uri.fromFile(targetFile)
    }
}.getOrNull()

/** 按扩展名选择条目图标 */
internal fun resolveFileTypeIcon(fileName: String): ImageVector {
    val ext = fileName.substringAfterLast('.', "").lowercase()
    return when (ext) {
        "png", "jpg", "jpeg", "gif", "webp", "bmp", "svg" -> LucideIcons.Image
        "mp4", "mov", "mkv", "avi", "webm" -> LucideIcons.Video
        "mp3", "wav", "flac", "aac", "ogg", "m4a" -> LucideIcons.Music
        "zip", "7z", "rar", "tar", "gz" -> LucideIcons.FileArchive
        "txt", "md", "log", "json", "csv", "xml", "html", "kt", "java", "py", "sh", "ts" -> LucideIcons.Code
        else -> LucideIcons.FileText
    }
}

/** 按扩展名选择图标色调 */
internal fun resolveFileTypeIconTint(fileName: String, primary: Color, onBackgroundVariant: Color): Color {
    val ext = fileName.substringAfterLast('.', "").lowercase()
    return when (ext) {
        "png", "jpg", "jpeg", "gif", "webp", "bmp", "svg" -> Color(0xFF8B5CF6)
        "mp4", "mov", "mkv", "avi", "webm" -> Color(0xFFEF4444)
        "mp3", "wav", "flac", "aac", "ogg", "m4a" -> Color(0xFF10B981)
        "zip", "7z", "rar", "tar", "gz" -> Color(0xFFF59E0B)
        "txt", "md", "log", "json", "csv", "xml", "html", "kt", "java", "py", "sh", "ts" -> Color(0xFF3B82F6)
        else -> onBackgroundVariant
    }
}

/** "12.3 KB / 4.5 MB" */
internal fun formatFileSize(bytes: Long): String = when {
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> String.format(Locale.US, "%.1f KB", bytes / 1024.0)
    bytes < 1024L * 1024 * 1024 -> String.format(Locale.US, "%.1f MB", bytes / 1024.0 / 1024.0)
    else -> String.format(Locale.US, "%.2f GB", bytes / 1024.0 / 1024.0 / 1024.0)
}
