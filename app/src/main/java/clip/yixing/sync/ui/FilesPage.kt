package clip.yixing.sync.ui

import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.data.FileTransferInfo
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.service.SyncNotificationManager
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
 * 文件互传页 —— **即时传输任务视图**。
 *
 * 产品语义(2026-11-14 用户确认):文件互传是「即时传递」,不是网盘。
 * 服务器虽然留存文件(可下载),但本页只关心**当次会话的收发过程与结果**:
 *
 *   ┌────────────────────────────┐
 *   │ ▶ 发送:本次选了哪些文件、每个文件 上传中/完成/失败      │
 *   │ ◆ 接收:对端发来了什么、保存到哪、能否打开              │
 *   └────────────────────────────┘
 *
 * 不再有"聊天气泡流 / 历史文件列表"两种视图 —— 历史浏览是网页控制台的事。
 * 页面只有一个纵向滚动的时间线:新任务插在最上面,老任务往下沉,
 * 关闭页面即结束本次会话展示(下次进来重新开始)。
 *
 * 隐私/语义约束:文件不进剪贴板历史;收到推送只展示,不触发任何粘贴行为。
 */
/** 单个传输任务的状态 */
private enum class TransferStatus { UPLOADING, DONE, FAILED, RECEIVED }

/** 一条传输任务(发送或接收) */
private data class TransferTask(
    val key: String,                 // 唯一键(fileId 或 本地临时 id)
    val fileName: String,
    val size: Long,
    val status: TransferStatus,
    val direction: Direction,
    val peerName: String,            // 对端设备名(接收方)或 "本机"(发送方展示用)
    val errorMessage: String? = null,
    val savedUri: Uri? = null,       // 接收文件已保存的位置
) { enum class Direction { SEND, RECEIVE } }

@Composable
fun FilesPage(
    snackbarHostState: top.yukonga.miuix.kmp.basic.SnackbarHostState?,
    pendingShareUris: List<Uri> = emptyList(),
    onShareConsumed: () -> Unit = {},
    onBack: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    // 对端推送仍走 fileTransfers StateFlow(服务层维护),但这里只把它当
    // "新到达事件源",转换为任务卡片后即消费,不做历史列表展示。
    val pushedFiles = ClipboardMonitorService.fileTransfers.collectAsState().value
    val seenFileIds = remember { mutableStateListOf<String>() }

    // 当次会话的任务时间线(新在上)
    val tasks = remember { mutableStateListOf<TransferTask>() }

    var deleteConfirmFor by remember { mutableStateOf<TransferTask?>(null) }

    val barSurface = MiuixTheme.colorScheme.surface
    val pageBackdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )

    fun buildApi(): SyncApi? {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isBlank() || !SyncSettings.isPaired(context) || SyncSettings.deviceToken(context).isBlank()) {
            return null
        }
        return SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
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

    fun upsertTask(task: TransferTask) {
        val idx = tasks.indexOfFirst { it.key == task.key }
        if (idx >= 0) tasks[idx] = task else tasks.add(0, task)
    }

    fun copyUriToTemp(uri: Uri): File? = runCatching {
        val resolver = context.contentResolver
        val name = queryDisplayName(context, uri) ?: "file_${System.currentTimeMillis()}"
        val safeName = name.replace(Regex("[\\\\/:*?\"<>|\\x00-\\x1f]"), "_").ifBlank { "file" }
        val dir = File(context.cacheDir, "files_upload").apply { mkdirs() }
        val target = File(dir, "${System.currentTimeMillis()}_$safeName")
        resolver.openInputStream(uri)?.use { input ->
            target.outputStream().use { output -> input.copyTo(output) }
        } ?: return null
        target
    }.getOrNull()

    /** 批量上传:每个文件独立任务卡(排队→上传中→完成/失败) */
    fun uploadFiles(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置,无法发送", SnackType.Error) }
            return
        }
        scope.launch {
            var ok = 0
            var failed = 0
            for (uri in uris) {
                val displayName = withContext(Dispatchers.IO) { queryDisplayName(context, uri) } ?: "文件"
                val key = "send_${System.currentTimeMillis()}_$displayName"
                val size = withContext(Dispatchers.IO) {
                    runCatching { File(copyUriToTemp(uri)?.absolutePath ?: "").length() }.getOrDefault(0L)
                }
                upsertTask(TransferTask(key, displayName, size, TransferStatus.UPLOADING, TransferTask.Direction.SEND, "本机"))

                val temp = withContext(Dispatchers.IO) { copyUriToTemp(uri) }
                if (temp == null) {
                    upsertTask(TransferTask(key, displayName, size, TransferStatus.FAILED, TransferTask.Direction.SEND, "本机", "无法读取文件"))
                    failed++
                    continue
                }
                try {
                    val info = withContext(Dispatchers.IO) {
                        api.uploadFile(temp, SyncSettings.ensureDeviceId(context), SyncSettings.deviceName(context))
                    }
                    upsertTask(
                        TransferTask(key, info.fileName, info.size, TransferStatus.DONE, TransferTask.Direction.SEND, "本机")
                    )
                    // 标记该 fileId 已展示,避免推送回显再生成一张重复卡
                    seenFileIds.add(info.fileId)
                    ok++
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    handleError(e, "上传失败")
                    upsertTask(TransferTask(key, displayName, size, TransferStatus.FAILED, TransferTask.Direction.SEND, "本机", e.message ?: "上传失败"))
                    failed++
                } finally {
                    temp.delete()
                }
            }
            val parts = mutableListOf<String>()
            if (ok > 0) parts.add("成功 $ok 个")
            if (failed > 0) parts.add("失败 $failed 个")
            if (parts.isNotEmpty()) {
                snackbarHostState?.showAppSnack("发送完成:" + parts.joinToString(", "), if (failed > 0) SnackType.Error else SnackType.Success)
            }
        }
    }

    // 对端推送 → 接收任务卡(按 fileId 去重;本机回显跳过)
    LaunchedEffect(pushedFiles) {
        for (i in pushedFiles.indices) {
            val info = pushedFiles[i]
            if (info.fileId in seenFileIds) continue
            seenFileIds.add(info.fileId)
            if (info.deviceId == SyncSettings.ensureDeviceId(context)) continue
            upsertTask(
                TransferTask(
                    key = info.fileId,
                    fileName = info.fileName,
                    size = info.size,
                    status = TransferStatus.RECEIVED,
                    direction = TransferTask.Direction.RECEIVE,
                    peerName = info.deviceName ?: "其他设备",
                    savedUri = null,
                )
            )
        }
    }

    LaunchedEffect(pendingShareUris) {
        if (pendingShareUris.isNotEmpty()) {
            uploadFiles(pendingShareUris)
            onShareConsumed()
        }
    }

    val pickFilesLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenMultipleDocuments()
    ) { uris: List<Uri> ->
        uploadFiles(uris)
    }

    /** 保存接收的文件到 Downloads/NexClip,返回 Uri */
    fun saveReceived(task: TransferTask, onSaved: (Uri?) -> Unit) {
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置", SnackType.Error) }
            return
        }
        scope.launch {
            try {
                snackbarHostState?.showAppSnack("正在下载 ${task.fileName}…", SnackType.Info)
                val bytes = withContext(Dispatchers.IO) { api.downloadFile(task.key) }
                if (bytes == null) {
                    snackbarHostState?.showAppSnack("文件已丢失(可能被服务器清理)", SnackType.Error)
                    onSaved(null)
                    return@launch
                }
                val uri = withContext(Dispatchers.IO) { saveToDownloads(context, task.fileName, bytes) }
                if (uri != null) {
                    snackbarHostState?.showAppSnack("已保存到 Downloads/NexClip", SnackType.Success)
                    upsertTask(task.copy(status = TransferStatus.RECEIVED, savedUri = uri))
                } else {
                    snackbarHostState?.showAppSnack("保存失败", SnackType.Error)
                }
                onSaved(uri)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                handleError(e, "下载失败")
                onSaved(null)
            }
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.background)
            // 消费空白区点击:否则触摸穿透到底下的 Pager,误触首页剪贴板卡片
            // (真机确认:文件页点中间会点开首页当前图片)。
            .clickable(indication = null, interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() }) { }
    ) {
        // 1. 任务时间线
        Box(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(pageBackdrop)
        ) {
            if (tasks.isEmpty()) {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(horizontal = 32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Icon(
                        imageVector = LucideIcons.UploadCloud,
                        contentDescription = "暂无传输",
                        modifier = Modifier.size(44.dp),
                        tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.5f)
                    )
                    Spacer(Modifier.height(12.dp))
                    Text(
                        text = "即时文件互传",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        color = MiuixTheme.colorScheme.onSurface
                    )
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = "选择文件即可发送,同账号下的网页与其它设备立即收到;对方发来的文件也会出现在这里,保存后即可打开。",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        textAlign = TextAlign.Center,
                        lineHeight = 18.sp
                    )
                }
            } else {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .padding(
                            start = 16.dp,
                            end = 16.dp,
                            top = 124.dp,
                            bottom = 100.dp,
                        ),
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    tasks.forEach { task ->
                        TransferTaskCard(
                            task = task,
                            onDownloadClick = {
                                saveReceived(task) { uri ->
                                    if (uri != null) {
                                        runCatching {
                                            context.startActivity(
                                                Intent(Intent.ACTION_VIEW).apply {
                                                    setDataAndType(uri, clip.yixing.sync.data.mimeTypeOf(task.fileName))
                                                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                                                }
                                            )
                                        }
                                    }
                                }
                            },
                        )
                    }
                }
            }
        }

        // 2. 顶部毛玻璃栏:返回 + 标题 + 设备在线摘要
        Box(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
        ) {
            BarBlurSurface(backdrop = pageBackdrop) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .statusBarsPadding()
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
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
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(8.dp))
                                .background(
                                    if (ClipboardMonitorService.isServerConnected.collectAsState().value)
                                        MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
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
                                            if (ClipboardMonitorService.isServerConnected.collectAsState().value) Color(0xFF34C759)
                                            else MiuixTheme.colorScheme.onBackgroundVariant
                                        )
                                )
                                Spacer(Modifier.width(5.dp))
                                Text(
                                    text = if (ClipboardMonitorService.isServerConnected.collectAsState().value) "已连接" else "未连接",
                                    fontSize = 12.sp,
                                    fontWeight = FontWeight.Medium,
                                    color = if (ClipboardMonitorService.isServerConnected.collectAsState().value) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                        }
                        Spacer(Modifier.width(8.dp))
                    }
                }
            }
        }

        // 3. 底部玻璃输入条:选择文件发送(多选)
        Box(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
        ) {
            BarBlurSurface(backdrop = pageBackdrop) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .navigationBarsPadding()
                        .padding(horizontal = 16.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    top.yukonga.miuix.kmp.basic.Button(
                        onClick = { pickFilesLauncher.launch(arrayOf("*/*")) },
                        colors = top.yukonga.miuix.kmp.basic.ButtonDefaults.buttonColorsPrimary(),
                        modifier = Modifier
                            .weight(1f)
                            .height(44.dp)
                    ) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            Icon(
                                imageVector = LucideIcons.UploadCloud,
                                contentDescription = null,
                                modifier = Modifier.size(18.dp),
                                tint = Color.White
                            )
                            Text("选择文件发送(可多选)", color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                        }
                    }
                }
            }
        }
    }

    // 删除确认(发送端删除文件即撤回全组可见性)
    val target = deleteConfirmFor
    if (target != null) {
        top.yukonga.miuix.kmp.window.WindowDialog(
            show = true,
            title = "删除文件",
            summary = "确定在服务器上删除「${target.fileName}」吗?所有设备将不再能下载它。",
            onDismissRequest = { deleteConfirmFor = null }
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                top.yukonga.miuix.kmp.basic.TextButton(
                    text = "取消",
                    onClick = { deleteConfirmFor = null },
                    modifier = Modifier.weight(1f)
                )
                top.yukonga.miuix.kmp.basic.Button(
                    onClick = {
                        deleteConfirmFor = null
                        scope.launch {
                            try {
                                val api = buildApi()
                                if (api != null) withContext(Dispatchers.IO) { api.deleteFile(target.key) }
                            } catch (_: Exception) {
                            }
                        }
                    },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("删除")
                }
            }
        }
    }
}

/** 传输任务卡:图标 + 文件名 + 状态行 + (接收文件的)保存/打开动作 */
@Composable
private fun TransferTaskCard(
    task: TransferTask,
    onDownloadClick: () -> Unit,
) {
    top.yukonga.miuix.kmp.basic.Card(
        modifier = Modifier.fillMaxWidth(),
        insideMargin = PaddingValues(horizontal = 14.dp, vertical = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(
                imageVector = resolveFileTypeIcon(task.fileName),
                contentDescription = null,
                modifier = Modifier.size(26.dp),
                tint = resolveFileTypeIconTint(task.fileName, MiuixTheme.colorScheme.primary, MiuixTheme.colorScheme.onBackgroundVariant)
            )
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = task.fileName,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onSurface,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(3.dp))
                val statusText = when (task.status) {
                    TransferStatus.UPLOADING -> "上传中…"
                    TransferStatus.DONE -> "已送达 · ${task.peerName}"
                    TransferStatus.FAILED -> "失败: ${task.errorMessage ?: "未知错误"}"
                    TransferStatus.RECEIVED -> "来自 ${task.peerName} · ${formatFileSize(task.size)}"
                }
                Text(
                    text = statusText,
                    fontSize = 11.sp,
                    color = when (task.status) {
                        TransferStatus.FAILED -> MiuixTheme.colorScheme.error
                        TransferStatus.UPLOADING -> MiuixTheme.colorScheme.primary
                        else -> MiuixTheme.colorScheme.onBackgroundVariant
                    },
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            when (task.status) {
                TransferStatus.UPLOADING -> LoadingSpinner(modifier = Modifier.size(18.dp), color = MiuixTheme.colorScheme.primary)
                TransferStatus.DONE -> Icon(
                    imageVector = LucideIcons.Check,
                    contentDescription = "已送达",
                    modifier = Modifier.size(20.dp),
                    tint = Color(0xFF34C759)
                )
                TransferStatus.RECEIVED -> Text(
                    text = "保存并打开",
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.primary,
                    modifier = Modifier.clickable(onClick = onDownloadClick)
                )
                TransferStatus.FAILED -> Icon(
                    imageVector = LucideIcons.X,
                    contentDescription = "失败",
                    modifier = Modifier.size(20.dp),
                    tint = MiuixTheme.colorScheme.error
                )
            }
        }
    }
}

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

/** 按扩展名选择条目图标色调 */
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

/** content:// 或 file:// 的显示名 */
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

/**
 * 保存到系统 Downloads/NexClip。同名条目覆盖写,防 (1) 副本累积。
 */
private fun saveToDownloads(context: Context, fileName: String, bytes: ByteArray): Uri? = runCatching {
    val safeName = fileName.replace(Regex("[\\\\/:*?\"<>|\\x00-\\x1f]"), "_").ifBlank { "file" }
    val mimeType = clip.yixing.sync.data.mimeTypeOf(safeName)
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
                put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
                put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/NexClip")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
            resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
        }
        resolver.openOutputStream(uri, "wt")?.use { it.write(bytes) }
        if (existing == null) {
            val done = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
            resolver.update(uri, done, null, null)
        }
        uri
    } else {
        val downloadsDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        val targetDir = File(downloadsDir, "NexClip").apply { if (!exists()) mkdirs() }
        val targetFile = File(targetDir, safeName)
        java.io.FileOutputStream(targetFile).use { it.write(bytes) }
        context.sendBroadcast(Intent(Intent.ACTION_MEDIA_SCANNER_SCAN_FILE, Uri.fromFile(targetFile)))
        Uri.fromFile(targetFile)
    }
}.getOrNull()

/** "12.3 KB / 4.5 MB" 人类可读大小 */
internal fun formatFileSize(bytes: Long): String = when {
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> String.format(Locale.US, "%.1f KB", bytes / 1024.0)
    bytes < 1024L * 1024 * 1024 -> String.format(Locale.US, "%.1f MB", bytes / 1024.0 / 1024.0)
    else -> String.format(Locale.US, "%.2f GB", bytes / 1024.0 / 1024.0 / 1024.0)
}
