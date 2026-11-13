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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.calculateEndPadding
import androidx.compose.foundation.layout.calculateStartPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.LaunchedEffect
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
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.window.WindowDialog
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.utils.overScrollVertical
import java.io.File
import java.text.SimpleDateFormat
import java.util.Locale

/**
 * 文件互传页(全屏覆盖页,仿 ManualPushPage 骨架)。
 *
 * 数据流:
 *  - 列表:ClipboardMonitorService.fileTransfers StateFlow(服务推送/上传回显/首拉共同维护)
 *  - 上传:SAF 多选 → content:// 复制到 cacheDir 临时文件 → api.uploadFile
 *  - 下载:api.downloadFile → MediaStore Downloads/NexClip(仿 ImageLoader.saveToGallery)
 *  - 删除:api.deleteFile → 服务端广播 FileDeleted → 服务层同步移除本地列表
 *
 * 隐私/语义约束:文件不进剪贴板历史;收到推送只展示,不触发任何粘贴行为。
 */
@Composable
fun FilesPage(
    snackbarHostState: SnackbarHostState?,
    pendingShareUris: List<Uri> = emptyList(),
    onShareConsumed: () -> Unit = {},
    onBack: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val files = ClipboardMonitorService.fileTransfers.collectAsState().value
    val isServerConnected = ClipboardMonitorService.isServerConnected.collectAsState().value

    var deletingFileId by remember { mutableStateOf<String?>(null) }
    var downloadingFileId by remember { mutableStateOf<String?>(null) }
    // 删除确认弹窗目标
    var deleteTarget by remember { mutableStateOf<FileTransferInfo?>(null) }
    // 上传中的文件名队列(逐文件进度展示,完成一个移除一个)
    val uploadingNames = remember { mutableStateListOf<String>() }
    // 文本预览弹窗目标(点击 .txt/.md/.log 条目时展示内容)
    var previewTarget by remember { mutableStateOf<Pair<FileTransferInfo, String>?>(null) }

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

    /** 统一错误处理:凭证类错误清配对(服务层会断开重连到未配对态),普通错误弹 snack */
    fun handleError(e: Exception, fallback: String) {
        scope.launch {
            if (e is clip.yixing.sync.data.ApiException && SyncSettings.shouldClearPairingOn(e)) {
                snackbarHostState?.showAppSnack("配对已失效,请重新配对", SnackType.Error)
            } else {
                snackbarHostState?.showAppSnack(e.message ?: fallback, SnackType.Error)
            }
        }
    }

    /** content:// → cacheDir 临时文件(含查询原始文件名),返回 null 表示读取失败 */
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

    /** 批量上传:逐个进行;每个文件在列表顶部显示独立进度项,完成即转为正式条目 */
    fun uploadFiles(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val api = buildApi() ?: run {
            scope.launch {
                snackbarHostState?.showAppSnack("未配对或服务器未配置,无法上传", SnackType.Error)
            }
            return
        }
        scope.launch {
            var ok = 0
            var failed = 0
            for (uri in uris) {
                val displayName = withContext(Dispatchers.IO) { queryDisplayName(context, uri) } ?: "文件"
                uploadingNames.add(displayName)
                val temp = withContext(Dispatchers.IO) { copyUriToTemp(uri) }
                if (temp == null) {
                    failed++
                    uploadingNames.remove(displayName)
                    continue
                }
                try {
                    val info = withContext(Dispatchers.IO) {
                        api.uploadFile(temp, SyncSettings.ensureDeviceId(context), SyncSettings.deviceName(context))
                    }
                    // 本机上传的回显也会经 FileTransferred 推回,这里直接乐观插入保证即时可见
                    ClipboardMonitorService.fileTransfers.value =
                        listOf(info) + ClipboardMonitorService.fileTransfers.value.filter { it.fileId != info.fileId }
                    ok++
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    handleError(e, "上传失败")
                    failed++
                } finally {
                    uploadingNames.remove(displayName)
                    temp.delete()
                }
            }
            val parts = mutableListOf<String>()
            if (ok > 0) parts.add("成功 $ok 个")
            if (failed > 0) parts.add("失败 $failed 个")
            if (parts.isNotEmpty()) {
                snackbarHostState?.showAppSnack(
                    "上传完成:" + parts.joinToString(", "),
                    if (failed > 0) SnackType.Error else SnackType.Success,
                )
            }
        }
    }

    /** 系统分享进入:pendingShareUris 非空时自动开始上传并消费 */
    LaunchedEffect(pendingShareUris) {
        if (pendingShareUris.isNotEmpty()) {
            uploadFiles(pendingShareUris)
            onShareConsumed()
        }
    }

    /** SAF 多选(文档/任意类型) */
    val pickFilesLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenMultipleDocuments()
    ) { uris: List<Uri> ->
        uploadFiles(uris)
    }

    /** 下载后用系统应用打开(ACTION_VIEW);无匹配应用时仅 snack 提示已保存 */
    fun openWithViewer(context: Context, uri: Uri, mimeType: String) {
        runCatching {
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, mimeType)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        }
    }

    /** 下载并可选立即打开 */
    fun downloadFile(info: FileTransferInfo, openAfter: Boolean = false) {
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置", SnackType.Error) }
            return
        }
        scope.launch {
            downloadingFileId = info.fileId
            try {
                val bytes = withContext(Dispatchers.IO) { api.downloadFile(info.fileId) }
                if (bytes == null) {
                    snackbarHostState?.showAppSnack("文件已丢失(可能被服务器清理)", SnackType.Error)
                    return@launch
                }
                val saved = withContext(Dispatchers.IO) { saveToDownloads(context, info.fileName, bytes) }
                if (saved != null) {
                    snackbarHostState?.showAppSnack("已保存到 Downloads/NexClip: ${info.fileName}", SnackType.Success)
                    if (openAfter) {
                        openWithViewer(context, saved, clip.yixing.sync.data.mimeTypeOf(info.fileName))
                    }
                } else {
                    snackbarHostState?.showAppSnack("保存失败", SnackType.Error)
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                handleError(e, "下载失败")
            } finally {
                downloadingFileId = null
            }
        }
    }

    fun deleteFile(info: FileTransferInfo) {
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置", SnackType.Error) }
            return
        }
        scope.launch {
            deletingFileId = info.fileId
            try {
                val ok = withContext(Dispatchers.IO) { api.deleteFile(info.fileId) }
                if (ok) {
                    // 服务端会广播 FileDeleted,这里乐观移除保证即时反馈
                    ClipboardMonitorService.fileTransfers.value =
                        ClipboardMonitorService.fileTransfers.value.filter { it.fileId != info.fileId }
                    snackbarHostState?.showAppSnack("已删除: ${info.fileName}", SnackType.Success)
                } else {
                    snackbarHostState?.showAppSnack("删除失败或无权删除", SnackType.Error)
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                handleError(e, "删除失败")
            } finally {
                deletingFileId = null
            }
        }
    }

    /**
     * 点击条目:
     *  - 文本类(.txt/.md/.log/.json 等)下载到内存直接预览,不落盘
     *  - 其它类型走"下载到 Downloads → 系统应用打开"一条龙
     */
    fun onItemClicked(info: FileTransferInfo) {
        val textExt = info.fileName.substringAfterLast('.', "").lowercase()
        if (textExt in setOf("txt", "md", "log", "json", "csv", "xml", "kt", "java", "py", "sh")) {
            val api = buildApi() ?: return
            scope.launch {
                downloadingFileId = info.fileId
                try {
                    val bytes = withContext(Dispatchers.IO) { api.downloadFile(info.fileId) }
                    if (bytes == null) {
                        snackbarHostState?.showAppSnack("文件已丢失(可能被服务器清理)", SnackType.Error)
                        return@launch
                    }
                    val text = withContext(Dispatchers.IO) {
                        String(bytes, Charsets.UTF_8).take(20_000) // 预览截断 20k 字符,避免巨型日志卡 UI
                    }
                    previewTarget = info to text
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    handleError(e, "读取失败")
                } finally {
                    downloadingFileId = null
                }
            }
        } else {
            // 非文本:下载到 Downloads 后交给系统应用打开
            downloadFile(info, openAfter = true)
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.background)
    ) {
        // 1. 文件列表(底层,毛玻璃顶栏下方穿透折射)
        Box(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(pageBackdrop)
        ) {
            val isUploadingNow = uploadingNames.isNotEmpty()
            if (files.isEmpty() && !isUploadingNow) {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(horizontal = 32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Icon(
                        imageVector = LucideIcons.FolderOpen,
                        contentDescription = "暂无文件",
                        modifier = Modifier.size(44.dp),
                        tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.5f)
                    )
                    Spacer(Modifier.height(12.dp))
                    Text(
                        text = "文件互传",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        color = MiuixTheme.colorScheme.onSurface
                    )
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = "上传文件后,同一账号下的网页与其它设备都会收到通知;收到文件也可以在这里下载。",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        textAlign = TextAlign.Center,
                        lineHeight = 18.sp
                    )
                    Spacer(Modifier.height(20.dp))
                    // 空状态主行动按钮:入口比顶栏小图标显眼得多
                    top.yukonga.miuix.kmp.basic.Button(
                        onClick = { if (!isUploadingNow) pickFilesLauncher.launch(arrayOf("*/*")) }
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
                            Text("选择文件上传", color = Color.White)
                        }
                    }
                }
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical(),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = 108.dp,
                        bottom = 24.dp,
                    ),
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    // 逐文件上传进度:每个文件一行(文件名 + 转圈),完成即移除
                    items(uploadingNames, key = { "uploading_$it" }) { name ->
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .background(MiuixTheme.colorScheme.surfaceContainer)
                                .padding(horizontal = 14.dp, vertical = 12.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            LoadingSpinner(modifier = Modifier.size(16.dp), color = MiuixTheme.colorScheme.primary)
                            Spacer(Modifier.width(10.dp))
                            Text(
                                name,
                                fontSize = 13.sp,
                                color = MiuixTheme.colorScheme.onSurface,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f)
                            )
                            Text(
                                "上传中",
                                fontSize = 11.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                    items(files, key = { it.fileId }) { info ->
                        FileTransferItem(
                            info = info,
                            isBusy = deletingFileId == info.fileId || downloadingFileId == info.fileId,
                            onDownload = { downloadFile(info) },
                            onDeleteRequest = { deleteTarget = info },
                            onClick = { onItemClicked(info) },
                        )
                    }
                    // 列表底部大按钮:主行动入口(顶栏图标保留为快捷方式)
                    item {
                        top.yukonga.miuix.kmp.basic.Button(
                            onClick = { if (!isUploadingNow) pickFilesLauncher.launch(arrayOf("*/*")) },
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 8.dp)
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
                                Text("上传文件", color = Color.White)
                            }
                        }
                    }
                }
            }
        }

        // 2. 顶部毛玻璃栏:返回 + 标题 + 连接状态 + 上传
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
                        // 连接状态点(与即时互传页同一约定)
                        Box(
                            modifier = Modifier
                                .size(8.dp)
                                .clip(RoundedCornerShape(4.dp))
                                .background(
                                    if (isServerConnected) Color(0xFF34C759)
                                    else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f)
                                )
                        )
                        Spacer(Modifier.width(10.dp))
                        IconButton(
                            onClick = {
                                if (uploadingNames.isNotEmpty()) return@IconButton
                                pickFilesLauncher.launch(arrayOf("*/*"))
                            }
                        ) {
                            if (uploadingNames.isNotEmpty()) {
                                LoadingSpinner(modifier = Modifier.size(20.dp), color = MiuixTheme.colorScheme.primary)
                            } else {
                                Icon(
                                    imageVector = LucideIcons.UploadCloud,
                                    contentDescription = "上传文件",
                                    tint = MiuixTheme.colorScheme.onSurface
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    // 文本预览弹窗
    val preview = previewTarget
    if (preview != null) {
        val (info, text) = preview
        WindowDialog(
            show = true,
            title = info.fileName,
            summary = "内容预览(仅前 2 万字符)",
            onDismissRequest = { previewTarget = null }
        ) {
            Column {
                Text(
                    text = text,
                    fontSize = 12.sp,
                    lineHeight = 17.sp,
                    color = MiuixTheme.colorScheme.onSurface,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(320.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer)
                        .padding(10.dp)
                        .verticalScroll(rememberScrollState())
                )
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    top.yukonga.miuix.kmp.basic.TextButton(
                        text = "关闭",
                        onClick = { previewTarget = null },
                        modifier = Modifier.weight(1f)
                    )
                    top.yukonga.miuix.kmp.basic.Button(
                        onClick = {
                            previewTarget = null
                            downloadFile(info)
                        },
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("下载保存")
                    }
                }
            }
        }
    }

    // 删除确认弹窗(与服务端删除联动,删除即广播全组)
    val target = deleteTarget
    if (target != null) {
        WindowDialog(
            show = true,
            title = "删除文件",
            summary = "确定删除「${target.fileName}」吗?同账号下所有设备都将不再可见。",
            onDismissRequest = { deleteTarget = null }
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                top.yukonga.miuix.kmp.basic.TextButton(
                    text = "取消",
                    onClick = { deleteTarget = null },
                    modifier = Modifier.weight(1f)
                )
                top.yukonga.miuix.kmp.basic.Button(
                    onClick = {
                        deleteTarget = null
                        deleteFile(target)
                    },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("删除")
                }
            }
        }
    }
}

/** 单个文件条目卡片。点击卡片本体 = 预览(文本)或下载打开(其它类型) */
@Composable
private fun FileTransferItem(
    info: FileTransferInfo,
    isBusy: Boolean,
    onDownload: () -> Unit,
    onDeleteRequest: () -> Unit,
    onClick: () -> Unit,
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(0.dp))
            .clickable(onClick = onClick),
        insideMargin = cardContentPadding,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(
                imageVector = resolveFileTypeIcon(info.fileName),
                contentDescription = null,
                modifier = Modifier.size(26.dp),
                tint = resolveFileTypeIconTint(
                    info.fileName,
                    MiuixTheme.colorScheme.primary,
                    MiuixTheme.colorScheme.onBackgroundVariant,
                ).copy(alpha = 0.85f)
            )
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = info.fileName,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onSurface,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(2.dp))
                Text(
                    text = "${formatFileSize(info.size)} · ${info.deviceName ?: info.deviceId}",
                    fontSize = 11.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(2.dp))
                Text(
                    text = formatFileTime(info.createdAt),
                    fontSize = 11.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f)
                )
            }
            if (isBusy) {
                LoadingSpinner(modifier = Modifier.size(18.dp), color = MiuixTheme.colorScheme.primary)
            } else {
                IconButton(onClick = onDownload) {
                    Icon(
                        imageVector = LucideIcons.Download,
                        contentDescription = "下载",
                        tint = MiuixTheme.colorScheme.primary
                    )
                }
                IconButton(onClick = onDeleteRequest) {
                    Icon(
                        imageVector = LucideIcons.Trash2,
                        contentDescription = "删除",
                        tint = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            }
        }
    }
}

/** 绝对时间("今天 14:32 / 11-08 22:40")——文件不比消息,精确时间更重要 */
internal fun formatFileTime(createdAt: String): String {
    val time = runCatching {
        SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.US).apply {
            timeZone = java.util.TimeZone.getTimeZone("UTC")
        }.parse(createdAt.take(19))
    }.getOrNull() ?: return createdAt
    val now = java.util.Calendar.getInstance()
    val cal = java.util.Calendar.getInstance().apply { this.time = time }
    return when {
        now.get(java.util.Calendar.YEAR) == cal.get(java.util.Calendar.YEAR) &&
            now.get(java.util.Calendar.DAY_OF_YEAR) == cal.get(java.util.Calendar.DAY_OF_YEAR) ->
            "今天 " + SimpleDateFormat("HH:mm", Locale.getDefault()).format(time)
        else -> SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(time)
    }
}

/** 按扩展名选择条目图标(图片/视频/音频/压缩包/代码文本/通用文档) */
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

/** 按扩展名选择条目图标色调(图标区分类型的同时用颜色强化) */
internal fun resolveFileTypeIconTint(fileName: String, primary: Color, onBackgroundVariant: Color): Color {
    val ext = fileName.substringAfterLast('.', "").lowercase()
    return when (ext) {
        "png", "jpg", "jpeg", "gif", "webp", "bmp", "svg" -> Color(0xFF8B5CF6)      // 紫:图片
        "mp4", "mov", "mkv", "avi", "webm" -> Color(0xFFEF4444)                      // 红:视频
        "mp3", "wav", "flac", "aac", "ogg", "m4a" -> Color(0xFF10B981)               // 绿:音频
        "zip", "7z", "rar", "tar", "gz" -> Color(0xFFF59E0B)                         // 黄:压缩包
        "txt", "md", "log", "json", "csv", "xml", "html", "kt", "java", "py", "sh", "ts" -> Color(0xFF3B82F6) // 蓝:代码
        else -> Color(0xFF6B7280)                                                    // 灰:其它文档
    }.let { if (it == Color(0xFF6B7280)) onBackgroundVariant else it }
}

/** content:// 或 file:// 的显示名(OpenableColumns.DISPLAY_NAME) */
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
 * 保存到系统 Downloads/NexClip(Q+ 走 MediaStore.Downloads,老版本走公共目录)。
 * 返回保存位置的 Uri(Q+ 为 content uri,老版本为 file uri);失败返回 null。
 *
 * 重复下载处理:同一文件名在 Downloads/NexClip 下已存在时**复用该条目覆盖写**,
 * 而不是再 insert 一份(否则系统会自动改名成 "file (1).txt",越下越多)。
 */
private fun saveToDownloads(context: Context, fileName: String, bytes: ByteArray): Uri? = runCatching {
    val safeName = fileName.replace(Regex("[\\\\/:*?\"<>|\\x00-\\x1f]"), "_").ifBlank { "file" }
    val mimeType = clip.yixing.sync.data.mimeTypeOf(safeName)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        val resolver = context.contentResolver
        // 先查已有条目(限定 NexClip 目录内同名且同 MIME,避免误伤用户自己的同名文件)
        val existing = resolver.query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.MediaColumns.DISPLAY_NAME} = ? AND ${MediaStore.MediaColumns.RELATIVE_PATH} = ?",
            arrayOf(safeName, Environment.DIRECTORY_DOWNLOADS + "/NexClip"),
            null,
        )?.use { c -> if (c.moveToFirst()) c.getLong(0) else null }

        val uri = if (existing != null) {
            // 覆盖已有条目:重置 IS_PENDING 后直接写
            ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI, existing)
        } else {
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, safeName)
                put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
                put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/NexClip")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
            val inserted = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: return null
            inserted
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
