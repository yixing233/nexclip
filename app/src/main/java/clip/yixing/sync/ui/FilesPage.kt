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
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
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
import androidx.compose.ui.graphics.asImageBitmap
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
import top.yukonga.miuix.kmp.utils.overScrollVertical
import java.io.File
import java.text.SimpleDateFormat
import java.util.Locale

/**
 * 文件互传页(全屏覆盖页,聊天式布局 —— 仿 ManualPushPage):
 * 底部玻璃输入条(选择文件) + 气泡流(自己上传的靠右、收到的靠左)。
 *
 * 数据流:
 *  - 列表:ClipboardMonitorService.fileTransfers StateFlow(推送/上传回显/首拉共同维护)
 *  - 上传:SAF 多选 → content:// 复制到 cacheDir 临时文件 → api.uploadFile
 *  - 下载:api.downloadFile → MediaStore Downloads/NexClip(同名覆盖写,防 (1) 副本)
 *  - 点击:文本类内存预览;其它类型下载后交给系统应用打开(有 snack 反馈)
 *
 * 隐私/语义约束:文件不进剪贴板历史;收到推送只展示,不触发任何粘贴行为。
 */
@Composable
fun FilesPage(
    snackbarHostState: top.yukonga.miuix.kmp.basic.SnackbarHostState?,
    pendingShareUris: List<Uri> = emptyList(),
    onShareConsumed: () -> Unit = {},
    onBack: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val files = ClipboardMonitorService.fileTransfers.collectAsState().value
    val selfDeviceId = remember { SyncSettings.ensureDeviceId(context) }

    var downloadingFileId by remember { mutableStateOf<String?>(null) }
    var deleteTarget by remember { mutableStateOf<FileTransferInfo?>(null) }
    val uploadingNames = remember { mutableStateListOf<String>() }
    var previewTarget by remember { mutableStateOf<Pair<FileTransferInfo, String>?>(null) }
    // 设备在线卡片(与即时互传页同款):进入页面拉取,连接恢复时刷新
    var deviceList by remember { mutableStateOf<List<clip.yixing.sync.data.DeviceInfo>>(emptyList()) }
    // 管理视图切换(false=聊天流,true=列表流)
    var showListView by remember { mutableStateOf(false) }

    val refreshDevices: suspend () -> Unit = {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isNotBlank() && SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
            try {
                val api = SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
                val remoteDevices = withContext(Dispatchers.IO) { api.getDevices() }
                // 文件互传是全组广播,设备卡片只做"谁能收到"的展示,过滤本机
                deviceList = remoteDevices.filter { it.id != selfDeviceId }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                android.util.Log.w("NexClipFiles", "refreshDevices: ${e.message}")
            }
        } else {
            deviceList = emptyList()
        }
    }

    LaunchedEffect(Unit) { refreshDevices() }

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

    /** 批量上传:气泡流顶部插入"上传中"条目,完成转为正式气泡 */
    fun uploadFiles(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置,无法上传", SnackType.Error) }
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

    /** 下载并用系统应用打开(非文本点击路径) */
    fun downloadAndOpen(info: FileTransferInfo) {
        val api = buildApi() ?: run {
            scope.launch { snackbarHostState?.showAppSnack("未配对或服务器未配置", SnackType.Error) }
            return
        }
        scope.launch {
            downloadingFileId = info.fileId
            snackbarHostState?.showAppSnack("正在下载 ${info.fileName}…", SnackType.Info)
            try {
                val bytes = withContext(Dispatchers.IO) { api.downloadFile(info.fileId) }
                if (bytes == null) {
                    snackbarHostState?.showAppSnack("文件已丢失(可能被服务器清理)", SnackType.Error)
                    return@launch
                }
                val uri = withContext(Dispatchers.IO) { saveToDownloads(context, info.fileName, bytes) }
                if (uri != null) {
                    snackbarHostState?.showAppSnack("已保存到 Downloads/NexClip", SnackType.Success)
                    runCatching {
                        context.startActivity(
                            Intent(Intent.ACTION_VIEW).apply {
                                setDataAndType(uri, clip.yixing.sync.data.mimeTypeOf(info.fileName))
                                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                            }
                        )
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
            try {
                val ok = withContext(Dispatchers.IO) { api.deleteFile(info.fileId) }
                if (ok) {
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
            }
        }
    }

    /** 点击气泡:文本类内存预览,其它类型下载+打开(均有提示,不再静默) */
    fun onItemClicked(info: FileTransferInfo) {
        val ext = info.fileName.substringAfterLast('.', "").lowercase()
        if (ext in TEXT_PREVIEW_EXTS) {
            val api = buildApi() ?: return
            scope.launch {
                downloadingFileId = info.fileId
                snackbarHostState?.showAppSnack("正在读取 ${info.fileName}…", SnackType.Info)
                try {
                    val bytes = withContext(Dispatchers.IO) { api.downloadFile(info.fileId) }
                    if (bytes == null) {
                        snackbarHostState?.showAppSnack("文件已丢失(可能被服务器清理)", SnackType.Error)
                        return@launch
                    }
                    val text = withContext(Dispatchers.IO) { String(bytes, Charsets.UTF_8).take(20_000) }
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
            downloadAndOpen(info)
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.background)
    ) {
        // 1. 聊天气泡流(底层,reverseLayout: 新消息在底部)
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
                        text = "点击下方按钮选择文件,同一账号下的网页与其它设备都会收到;对方发来的文件也会出现在这里。",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        textAlign = TextAlign.Center,
                        lineHeight = 18.sp
                    )
                }
            } else if (showListView) {
                // 列表视图(右上角图标切换):紧凑卡片,新→旧
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical(),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = 124.dp,
                        bottom = 100.dp,
                    ),
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    items(files, key = { it.fileId }) { info ->
                        FileListCard(
                            info = info,
                            isBusy = downloadingFileId == info.fileId,
                            onDownload = { downloadAndOpen(info) },
                            onDeleteRequest = { deleteTarget = info },
                            onClick = { onItemClicked(info) },
                        )
                    }
                }
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical(),
                    reverseLayout = true,
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        // 顶栏毛玻璃高度(statusBar+56dp)+设备卡片行(~34dp)再留呼吸空间
                        top = 132.dp,
                        bottom = 96.dp,
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    // 上传中条目(最新,所以在 reverseLayout 的首位)
                    items(uploadingNames, key = { "uploading_$it" }) { name ->
                        UploadingBubble(name = name)
                    }
                    items(files, key = { it.fileId }) { info ->
                        FileBubble(
                            info = info,
                            isSelf = info.deviceId == selfDeviceId,
                            isBusy = downloadingFileId == info.fileId,
                            onDownload = { downloadAndOpen(info) },
                            onDeleteRequest = { deleteTarget = info },
                            onClick = { onItemClicked(info) },
                        )
                    }
                }
            }
        }

        // 2. 顶部毛玻璃栏:返回 + 标题 + 视图切换 + 设备在线卡片行
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
                        // 视图切换:聊天流 ↔ 紧凑列表
                        IconButton(onClick = { showListView = !showListView }) {
                            Icon(
                                imageVector = if (showListView) LucideIcons.MessageSquare else LucideIcons.Layers,
                                contentDescription = if (showListView) "聊天视图" else "列表视图",
                                tint = MiuixTheme.colorScheme.onSurface
                            )
                        }
                    }
                    // 设备在线卡片行(与即时互传页同款语言:谁能收到一眼可见)。
                    // 横向滚动 —— 设备多时不挤压换行(首轮真机 6 台溢出翻车的教训)。
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .horizontalScroll(rememberScrollState())
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 8.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                            val onlineNow = deviceList.filter { it.online }
                            // 收起为计数摘要:全部设备/在线数
                            Box(
                                modifier = Modifier
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(
                                        if (onlineNow.isNotEmpty()) MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                                        else MiuixTheme.colorScheme.surfaceContainer
                                    )
                                    .padding(horizontal = 10.dp, vertical = 5.dp)
                            ) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Icon(
                                        imageVector = LucideIcons.Monitor,
                                        contentDescription = null,
                                        modifier = Modifier.size(13.dp),
                                        tint = if (onlineNow.isNotEmpty()) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                    Spacer(Modifier.width(5.dp))
                                    Text(
                                        text = if (onlineNow.isNotEmpty()) "${onlineNow.size}/${deviceList.size} 台设备在线" else "无设备在线",
                                        fontSize = 12.sp,
                                        fontWeight = FontWeight.Medium,
                                        color = if (onlineNow.isNotEmpty()) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                            }
                            // 逐设备小芯片(在线高亮,离线灰);横向滚动不截断设备数
                            deviceList.forEach { dev ->
                                Box(
                                    modifier = Modifier
                                        .clip(RoundedCornerShape(8.dp))
                                        .background(
                                            if (dev.online) MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                                            else MiuixTheme.colorScheme.surfaceContainer
                                        )
                                        .padding(horizontal = 9.dp, vertical = 5.dp)
                                ) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Icon(
                                            imageVector = resolveDeviceIcon(dev.name, dev.platform),
                                            contentDescription = dev.name,
                                            modifier = Modifier.size(12.dp),
                                            tint = if (dev.online) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                                        )
                                        Spacer(Modifier.width(4.dp))
                                        Text(
                                            text = dev.name,
                                            fontSize = 11.sp,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                            color = if (dev.online) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant
                                        )
                                    }
                                }
                            }
                        }
                }
            }
        }

        // 3. 底部玻璃输入条:选择文件(主入口,与即时互传页同一布局语言)
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
                    // 选择文件主按钮(primary 实底,显眼;Miuix Button 自带圆角与按压反馈)
                    top.yukonga.miuix.kmp.basic.Button(
                        onClick = { if (uploadingNames.isEmpty()) pickFilesLauncher.launch(arrayOf("*/*")) },
                        colors = top.yukonga.miuix.kmp.basic.ButtonDefaults.buttonColorsPrimary(),
                        modifier = Modifier
                            .weight(1f)
                            .height(44.dp)
                    ) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            if (uploadingNames.isNotEmpty()) {
                                LoadingSpinner(modifier = Modifier.size(16.dp), color = Color.White)
                                Text("正在上传 ${uploadingNames.size} 个…", color = Color.White, fontSize = 14.sp)
                            } else {
                                Icon(
                                    imageVector = LucideIcons.UploadCloud,
                                    contentDescription = null,
                                    modifier = Modifier.size(18.dp),
                                    tint = Color.White
                                )
                                Text("选择文件发送", color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.Medium)
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
        top.yukonga.miuix.kmp.window.WindowDialog(
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
                        .heightIn(max = 320.dp)
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
                            downloadAndOpen(info)
                        },
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("下载保存")
                    }
                }
            }
        }
    }

    // 删除确认弹窗
    val target = deleteTarget
    if (target != null) {
        top.yukonga.miuix.kmp.window.WindowDialog(
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

/** 可文本预览的扩展名集合 */
private val TEXT_PREVIEW_EXTS = setOf("txt", "md", "log", "json", "csv", "xml", "html", "kt", "java", "py", "sh", "ts")

/** 判断是否图片文件(缩略图气泡用) */
private fun isImageFile(fileName: String): Boolean =
    fileName.substringAfterLast('.', "").lowercase() in setOf("png", "jpg", "jpeg", "gif", "webp", "bmp")

/**
 * 图片文件气泡缩略图:下载字节 → 解码(服务端 Files 与剪贴板 ImageRef 机制不同,
 * 不能复用 ImageLoader.loadImageBitmap,这里直接 downloadFile 字节解码 + 内存缓存)。
 */
private val thumbCache = android.util.LruCache<String, androidx.compose.ui.graphics.ImageBitmap>(24)

@Composable
private fun FileImageThumbnail(imageRef: String) {
    val context = LocalContext.current
    var bitmap by remember(imageRef) { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(thumbCache.get(imageRef)) }
    LaunchedEffect(imageRef) {
        if (bitmap != null) return@LaunchedEffect
        bitmap = thumbCache.get(imageRef)
        if (bitmap != null) return@LaunchedEffect
        val loaded = runCatching {
            val serverUrl = SyncSettings.serverUrl(context)
            if (serverUrl.isBlank() || !SyncSettings.isPaired(context)) return@runCatching null
            val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
            api.downloadFile(imageRef)?.let { bytes ->
                android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    ?.asImageBitmap()
            }
        }.getOrNull()
        if (loaded != null) {
            thumbCache.put(imageRef, loaded)
            bitmap = loaded
        }
    }
    val shown = bitmap
    if (shown != null) {
        Image(
            bitmap = shown,
            contentDescription = "文件缩略图",
            modifier = Modifier
                .size(width = 140.dp, height = 96.dp)
                .clip(RoundedCornerShape(10.dp)),
            contentScale = androidx.compose.ui.layout.ContentScale.Crop
        )
    } else {
        // 加载中/失败:类型图标占位(不破坏气泡布局)
        Icon(
            imageVector = LucideIcons.Image,
            contentDescription = null,
            modifier = Modifier.size(38.dp),
            tint = Color.White.copy(alpha = 0.85f)
        )
    }
}

/** 上传中气泡(靠右,带转圈) */
@Composable
private fun UploadingBubble(name: String) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.End
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp)
        ) {
            Text(text = "本机", fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = MiuixTheme.colorScheme.primary)
            Spacer(Modifier.width(6.dp))
            Text(text = "上传中", fontSize = 10.sp, color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f))
        }
        Row(
            modifier = Modifier
                .widthIn(max = 280.dp)
                .clip(RoundedCornerShape(topStart = 16.dp, topEnd = 4.dp, bottomStart = 16.dp, bottomEnd = 16.dp))
                .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.85f))
                .padding(horizontal = 14.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            LoadingSpinner(modifier = Modifier.size(14.dp), color = Color.White)
            Spacer(Modifier.width(8.dp))
            Text(
                text = name,
                fontSize = 13.sp,
                color = Color.White,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}

/** 文件气泡:自己上传的靠右(primary 底白字),收到的靠左(卡片底),含类型图标/大小/时间/操作 */
@Composable
private fun FileBubble(
    info: FileTransferInfo,
    isSelf: Boolean,
    isBusy: Boolean,
    onDownload: () -> Unit,
    onDeleteRequest: () -> Unit,
    onClick: () -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = if (isSelf) Alignment.End else Alignment.Start
    ) {
        // 头部:设备名 + 时间(收到的在左,自己的在右)
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp)
        ) {
            if (!isSelf) {
                Text(
                    text = info.deviceName ?: "其他设备",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                Spacer(Modifier.width(6.dp))
            }
            Text(
                text = formatFileTime(info.createdAt),
                fontSize = 10.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f)
            )
            if (isSelf) {
                Spacer(Modifier.width(6.dp))
                Text(text = "本机", fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = MiuixTheme.colorScheme.primary)
            }
        }
        Spacer(Modifier.height(2.dp))

        // 气泡本体:不对称圆角(与即时互传页同一套语言)。
        // 单击 = 预览/打开;长按 = 下载/删除菜单(操作不再裸露成文字行)。
        val bubbleShape = if (isSelf) {
            RoundedCornerShape(topStart = 16.dp, topEnd = 4.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
        } else {
            RoundedCornerShape(topStart = 4.dp, topEnd = 16.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
        }

        var showActions by remember(info.fileId) { mutableStateOf(false) }

        Row(
            modifier = Modifier
                .widthIn(max = 300.dp)
                .clip(bubbleShape)
                .background(if (isSelf) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.surfaceContainer)
                .combinedClickable(
                    onClick = onClick,
                    onLongClick = { showActions = !showActions },
                )
                .padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            // 图片文件:直接显示缩略图(加载失败退回类型图标)
            if (isImageFile(info.fileName)) {
                FileImageThumbnail(imageRef = info.fileId)
                Spacer(Modifier.width(10.dp))
            } else {
                val icon = resolveFileTypeIcon(info.fileName)
                if (isSelf) {
                    Icon(imageVector = icon, contentDescription = null, modifier = Modifier.size(24.dp), tint = Color.White.copy(alpha = 0.9f))
                } else {
                    Icon(
                        imageVector = icon,
                        contentDescription = null,
                        modifier = Modifier.size(24.dp),
                        tint = resolveFileTypeIconTint(info.fileName, MiuixTheme.colorScheme.primary, MiuixTheme.colorScheme.onBackgroundVariant)
                    )
                }
                Spacer(Modifier.width(10.dp))
            }
            Column {
                Text(
                    text = info.fileName,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Medium,
                    color = if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(2.dp))
                Text(
                    text = formatFileSize(info.size) + if (isBusy) " · 处理中…" else "",
                    fontSize = 11.sp,
                    color = if (isSelf) Color.White.copy(alpha = 0.75f) else MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
        }

        // 长按展开的操作行(仅该气泡):半透明浮层贴在气泡下方,点击操作后收起
        if (showActions) {
            Row(
                horizontalArrangement = Arrangement.spacedBy(18.dp),
                modifier = Modifier
                    .padding(top = 4.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(MiuixTheme.colorScheme.surfaceContainerHigh)
                    .padding(horizontal = 14.dp, vertical = 8.dp)
            ) {
                Text(
                    text = "下载",
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.primary,
                    modifier = Modifier.clickable { showActions = false; onDownload() }
                )
                Text(
                    text = "删除",
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.error,
                    modifier = Modifier.clickable { showActions = false; onDeleteRequest() }
                )
            }
        }
    }
}

/** 列表视图条目(紧凑卡片,操作直接可见 —— 与聊天视图互补,批量管理场景用) */
@Composable
private fun FileListCard(
    info: FileTransferInfo,
    isBusy: Boolean,
    onDownload: () -> Unit,
    onDeleteRequest: () -> Unit,
    onClick: () -> Unit,
) {
    top.yukonga.miuix.kmp.basic.Card(
        modifier = Modifier.fillMaxWidth(),
        insideMargin = PaddingValues(horizontal = 14.dp, vertical = 12.dp),
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
                )
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
                    text = "${formatFileSize(info.size)} · ${info.deviceName ?: info.deviceId} · ${formatFileTime(info.createdAt)}",
                    fontSize = 11.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            if (isBusy) {
                LoadingSpinner(modifier = Modifier.size(18.dp), color = MiuixTheme.colorScheme.primary)
            } else {
                IconButton(onClick = onDownload) {
                    Icon(imageVector = LucideIcons.Download, contentDescription = "下载", tint = MiuixTheme.colorScheme.primary)
                }
                IconButton(onClick = onDeleteRequest) {
                    Icon(imageVector = LucideIcons.Trash2, contentDescription = "删除", tint = MiuixTheme.colorScheme.onBackgroundVariant)
                }
            }
        }
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
 * 保存到系统 Downloads/NexClip。同名条目覆盖写,防 (1) 副本累积。
 * 返回保存位置 Uri;失败返回 null。
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

/** 绝对时间("今天 14:32 / 11-08 22:40") */
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
