package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.input.rememberTextFieldState
import androidx.compose.foundation.text.input.setTextAndPlaceCursorAtEnd
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.ClipboardTest
import clip.yixing.sync.util.ImageLoader
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.Refresh
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.theme.MiuixTheme
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 手动推送与跨设备即时互传页面（聊天流形式设计）
 */
@Composable
fun ManualPushPage(
    modifier: Modifier = Modifier,
    snackbarHostState: SnackbarHostState? = null,
    onBack: () -> Unit = {}
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val listState = rememberLazyListState()

    val selfDeviceId = remember { SyncSettings.ensureDeviceId(context) }
    val selfDeviceName = remember { SyncSettings.deviceName(context) }
    val isServerConnected by ClipboardMonitorService.isServerConnected.collectAsState()
    val capturedList by ClipboardMonitorService.captured.collectAsState()

    // 聊天消息列表（按时间升序，最旧在上，最新在下）
    val chatMessages = remember(capturedList) {
        capturedList.sortedBy { it.time }
    }

    var onlineDevicesCount by remember { mutableIntStateOf(0) }
    var isSending by remember { mutableStateOf(false) }

    // 输入框状态
    val inputTextState = rememberTextFieldState()
    var selectedImageBytes by remember { mutableStateOf<ByteArray?>(null) }
    var previewImageClip by remember { mutableStateOf<CapturedClip?>(null) }

    // 相册图片选择器
    val imagePickerLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.GetContent()
    ) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                try {
                    val bytes = withContext(Dispatchers.IO) {
                        val stream: InputStream? = context.contentResolver.openInputStream(uri)
                        stream?.use { input ->
                            val bmp = BitmapFactory.decodeStream(input)
                            if (bmp != null) {
                                val out = ByteArrayOutputStream()
                                bmp.compress(android.graphics.Bitmap.CompressFormat.PNG, 90, out)
                                out.toByteArray()
                            } else {
                                input.readBytes()
                            }
                        }
                    }
                    if (bytes != null && bytes.isNotEmpty()) {
                        selectedImageBytes = bytes
                    } else {
                        snackbarHostState?.showAppSnack("无法读取选中的图片", SnackType.Error)
                    }
                } catch (e: Exception) {
                    snackbarHostState?.showAppSnack("图片加载失败: ${e.message}", SnackType.Error)
                }
            }
        }
    }

    // 更新在线设备统计
    LaunchedEffect(isServerConnected) {
        val serverUrl = SyncSettings.serverUrl(context)
        if (isServerConnected && serverUrl.isNotBlank() && SyncSettings.isPaired(context)) {
            try {
                val api = SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
                val devices = withContext(Dispatchers.IO) { api.getDevices() }
                onlineDevicesCount = devices.count { it.online }
            } catch (_: Exception) {
            }
        } else {
            onlineDevicesCount = 0
        }
    }

    // 首次进入或消息更新时自动滚动到底部
    LaunchedEffect(chatMessages.size) {
        if (chatMessages.isNotEmpty()) {
            listState.animateScrollToItem(chatMessages.size - 1)
        }
    }

    // 检查当前剪贴板是否有内容可快捷填入
    val currentSystemClipText = remember { ClipboardTest.readClipboard(context) ?: "" }

    // 发送消息核心逻辑
    val onSendMessage: () -> Unit = {
        val textToSend = inputTextState.text.toString().trim()
        val imageToSend = selectedImageBytes

        if (textToSend.isEmpty() && imageToSend == null) {
            scope.launch { snackbarHostState?.showAppSnack("请输入内容或选择图片", SnackType.Info) }
        } else {
            val serverUrl = SyncSettings.serverUrl(context)
            if (serverUrl.isBlank()) {
                scope.launch { snackbarHostState?.showAppSnack("请先在设置中配置服务器地址", SnackType.Info) }
            } else if (!SyncSettings.isPaired(context)) {
                scope.launch { snackbarHostState?.showAppSnack("设备尚未配对，请先完成配对", SnackType.Info) }
            } else {
                isSending = true
                scope.launch {
                    try {
                        val api = SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
                        withContext(Dispatchers.IO) {
                            if (imageToSend != null) {
                                // 上传图片
                                val entry = api.uploadImage(imageToSend, selfDeviceId, selfDeviceName)
                                // 保存至本地缓存
                                val key = entry.imageRef ?: "img_${System.currentTimeMillis()}"
                                ImageLoader.saveBytesToDisk(context, key, imageToSend)
                                // 写入本地记录
                                ClipboardMonitorService.addCaptured(context, textToSend.ifEmpty { "[图片]" }, key, sourceDevice = "本机")
                            } else {
                                // 上传文本
                                api.putText(textToSend, selfDeviceId, selfDeviceName)
                                ClipboardMonitorService.addCaptured(context, textToSend, null, sourceDevice = "本机")
                            }
                        }

                        // 清空输入框与选图
                        inputTextState.setTextAndPlaceCursorAtEnd("")
                        selectedImageBytes = null
                        snackbarHostState?.showAppSnack("已推送至已连接设备", SnackType.Success)

                        // 滚动到底部
                        if (chatMessages.isNotEmpty()) {
                            listState.animateScrollToItem(chatMessages.size - 1)
                        }
                    } catch (e: Exception) {
                        snackbarHostState?.showAppSnack("推送失败: ${e.message ?: "网络异常"}", SnackType.Error)
                    } finally {
                        isSending = false
                    }
                }
            }
        }
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.background)
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .imePadding()
        ) {
            // 1. 顶栏
            TopAppBar(
                title = "跨设备互传",
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Back,
                            contentDescription = "返回",
                            tint = MiuixTheme.colorScheme.onSurface
                        )
                    }
                },
                actions = {
                    IconButton(
                        onClick = {
                            scope.launch {
                                val url = SyncSettings.serverUrl(context)
                                if (url.isNotBlank() && SyncSettings.isPaired(context)) {
                                    try {
                                        val api = SyncApi(url, selfDeviceId, SyncSettings.deviceToken(context))
                                        val (history, _) = withContext(Dispatchers.IO) { api.getHistory(0, 30) }
                                        history.reversed().forEach { entry ->
                                            if (entry.isImage) {
                                                ClipboardMonitorService.addCaptured(context, entry.text ?: "[图片]", entry.imageRef, sourceDevice = entry.deviceName)
                                            } else if (!entry.text.isNullOrBlank()) {
                                                ClipboardMonitorService.addCaptured(context, entry.text, null, sourceDevice = entry.deviceName)
                                            }
                                        }
                                        snackbarHostState?.showAppSnack("同步记录已刷新", SnackType.Success)
                                    } catch (e: Exception) {
                                        snackbarHostState?.showAppSnack("刷新失败: ${e.message}", SnackType.Error)
                                    }
                                }
                            }
                        }
                    ) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Refresh,
                            contentDescription = "刷新记录",
                            tint = MiuixTheme.colorScheme.onSurface
                        )
                    }
                }
            )

            // 连接状态提示副栏
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .clip(CircleShape)
                        .background(if (isServerConnected) Color(0xFF34C759) else MiuixTheme.colorScheme.error)
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    text = if (isServerConnected) "云端同步在线 · 在线设备: ${onlineDevicesCount} 台" else "未连接到同步服务器",
                    fontSize = 12.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }

            Spacer(Modifier.height(4.dp))

            // 2. 聊天消息流列表
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
            ) {
                if (chatMessages.isEmpty()) {
                    // 空状态提示
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(horizontal = 32.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                    ) {
                        Icon(
                            imageVector = LucideIcons.MessageSquare,
                            contentDescription = "暂无消息",
                            modifier = Modifier.size(48.dp),
                            tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.5f)
                        )
                        Spacer(Modifier.height(12.dp))
                        Text(
                            text = "跨设备即时互传",
                            fontSize = 17.sp,
                            fontWeight = FontWeight.Bold,
                            color = MiuixTheme.colorScheme.onSurface
                        )
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = "在下方输入文本或选择相册图片，即可向已连接的 PC、手机等多端设备实时推送。",
                            fontSize = 13.sp,
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            textAlign = TextAlign.Center,
                            lineHeight = 18.sp
                        )
                    }
                } else {
                    LazyColumn(
                        state = listState,
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        items(chatMessages, key = { it.id }) { clip ->
                            val isSelf = clip.sourceDevice == null || clip.sourceDevice == selfDeviceName || clip.sourceDevice == "本机"
                            ChatMessageItem(
                                clip = clip,
                                isSelf = isSelf,
                                onImageClick = { previewImageClip = clip },
                                onCopyText = { text ->
                                    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                    cm.setPrimaryClip(ClipData.newPlainText("NexClip", text))
                                    scope.launch { snackbarHostState?.showAppSnack("已复制到剪贴板", SnackType.Success) }
                                }
                            )
                        }
                    }
                }
            }

            // 3. 底部输入与多媒体发送面板
            SurfaceBottomInputBar(
                inputTextState = inputTextState,
                selectedImageBytes = selectedImageBytes,
                isSending = isSending,
                quickClipboardText = currentSystemClipText.takeIf { it.isNotBlank() && it != inputTextState.text.toString() },
                onClearSelectedImage = { selectedImageBytes = null },
                onPickImage = { imagePickerLauncher.launch("image/*") },
                onPasteClipboard = {
                    inputTextState.setTextAndPlaceCursorAtEnd(currentSystemClipText)
                },
                onSend = onSendMessage
            )
        }

        // 大图全屏查看器
        val previewTarget = previewImageClip
        if (previewTarget != null) {
            OverlayDialog(
                show = true,
                title = "图片预览",
                onDismissRequest = { previewImageClip = null }
            ) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    var bitmap by remember { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
                    LaunchedEffect(previewTarget) {
                        bitmap = ImageLoader.loadImageBitmap(context, previewTarget.imageRef, previewTarget.text)
                    }

                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap!!,
                            contentDescription = "查看大图",
                            modifier = Modifier
                                .fillMaxWidth()
                                .heightIn(max = 320.dp)
                                .clip(RoundedCornerShape(12.dp)),
                            contentScale = ContentScale.Fit
                        )
                    } else {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(160.dp),
                            contentAlignment = Alignment.Center
                        ) {
                            LoadingSpinner(
                                modifier = Modifier.size(28.dp),
                                color = MiuixTheme.colorScheme.primary,
                                strokeWidth = 3.dp
                            )
                        }
                    }

                    Spacer(Modifier.height(16.dp))

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        Button(
                            onClick = {
                                scope.launch {
                                    val ok = ImageLoader.copyImageToClipboard(context, previewTarget.imageRef, previewTarget.text)
                                    snackbarHostState?.showAppSnack(if (ok) "已复制图片到剪贴板" else "复制失败", if (ok) SnackType.Success else SnackType.Error)
                                    previewImageClip = null
                                }
                            },
                            colors = ButtonDefaults.buttonColors(
                                color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                contentColor = MiuixTheme.colorScheme.primary
                            ),
                            modifier = Modifier.weight(1f)
                        ) {
                            Icon(imageVector = LucideIcons.Copy, contentDescription = "复制", modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("复制")
                        }

                        Button(
                            onClick = {
                                scope.launch {
                                    val ok = ImageLoader.saveToGallery(context, previewTarget.imageRef, previewTarget.text)
                                    snackbarHostState?.showAppSnack(if (ok) "已保存至系统相册" else "保存失败", if (ok) SnackType.Success else SnackType.Error)
                                    previewImageClip = null
                                }
                            },
                            colors = ButtonDefaults.buttonColors(
                                color = MiuixTheme.colorScheme.primary,
                                contentColor = Color.White
                            ),
                            modifier = Modifier.weight(1f)
                        ) {
                            Icon(imageVector = LucideIcons.Download, contentDescription = "保存相册", modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("保存相册")
                        }
                    }
                }
            }
        }
    }
}

/**
 * 单条聊天消息气泡渲染（左侧为其他设备接收，右侧为本机发送）
 */
@Composable
private fun ChatMessageItem(
    clip: CapturedClip,
    isSelf: Boolean,
    onImageClick: () -> Unit,
    onCopyText: (String) -> Unit
) {
    val context = LocalContext.current
    val timeStr = remember(clip.time) {
        SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(clip.time))
    }

    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = if (isSelf) Alignment.End else Alignment.Start
    ) {
        // 设备名称与时间标签
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp)
        ) {
            if (!isSelf) {
                val devLabel = clip.sourceDevice ?: "远端设备"
                val devIcon = when {
                    devLabel.contains("Mac", ignoreCase = true) -> "🍎"
                    devLabel.contains("Windows", ignoreCase = true) || devLabel.contains("PC", ignoreCase = true) -> "💻"
                    devLabel.contains("iOS", ignoreCase = true) || devLabel.contains("iPhone", ignoreCase = true) -> "📱"
                    else -> "🌐"
                }
                Text(
                    text = "$devIcon $devLabel",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                Spacer(Modifier.width(6.dp))
            }

            Text(
                text = timeStr,
                fontSize = 10.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f)
            )

            if (isSelf) {
                Spacer(Modifier.width(4.dp))
                Text(
                    text = "📱 本机",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.primary
                )
            }
        }

        Spacer(Modifier.height(2.dp))

        // 消息气泡卡片
        val bubbleShape = if (isSelf) {
            RoundedCornerShape(topStart = 16.dp, topEnd = 4.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
        } else {
            RoundedCornerShape(topStart = 4.dp, topEnd = 16.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
        }

        Box(
            modifier = Modifier
                .widthIn(max = 280.dp)
                .clip(bubbleShape)
                .background(
                    if (isSelf) MiuixTheme.colorScheme.primary
                    else MiuixTheme.colorScheme.surfaceContainer
                )
                .padding(if (clip.isImage) 4.dp else 12.dp)
        ) {
            if (clip.isImage) {
                // 图片消息气泡
                Column {
                    var bitmap by remember { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
                    LaunchedEffect(clip) {
                        bitmap = ImageLoader.loadImageBitmap(context, clip.imageRef, clip.text)
                    }

                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap!!,
                            contentDescription = "图片消息",
                            modifier = Modifier
                                .fillMaxWidth()
                                .heightIn(max = 200.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .clickable { onImageClick() },
                            contentScale = ContentScale.Crop
                        )
                    } else {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(120.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .background(Color.Black.copy(alpha = 0.08f)),
                            contentAlignment = Alignment.Center
                        ) {
                            Text("正在加载图片...", fontSize = 12.sp, color = MiuixTheme.colorScheme.onBackgroundVariant)
                        }
                    }

                    if (clip.text.isNotBlank() && clip.text != "[图片]") {
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = clip.text,
                            fontSize = 13.sp,
                            color = if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface,
                            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
                        )
                    }
                }
            } else {
                // 纯文本消息气泡
                Row(verticalAlignment = Alignment.Top) {
                    Text(
                        text = clip.text,
                        fontSize = 14.sp,
                        lineHeight = 20.sp,
                        color = if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface,
                        modifier = Modifier
                            .weight(1f, fill = false)
                            .clickable { onCopyText(clip.text) }
                    )
                }
            }
        }
    }
}

/**
 * 底部输入与多媒体附件面板
 */
@Composable
private fun SurfaceBottomInputBar(
    inputTextState: TextFieldState,
    selectedImageBytes: ByteArray?,
    isSending: Boolean,
    quickClipboardText: String?,
    onClearSelectedImage: () -> Unit,
    onPickImage: () -> Unit,
    onPasteClipboard: () -> Unit,
    onSend: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MiuixTheme.colorScheme.surface)
            .navigationBarsPadding()
            .padding(horizontal = 12.dp, vertical = 8.dp)
    ) {
        // 1. 剪贴板快捷粘贴芯片 (若当前剪贴板有内容且未输入)
        if (quickClipboardText != null) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(8.dp))
                        .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
                        .clickable { onPasteClipboard() }
                        .padding(horizontal = 10.dp, vertical = 4.dp)
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(
                            imageVector = LucideIcons.ClipboardCheck,
                            contentDescription = "快捷粘贴",
                            tint = MiuixTheme.colorScheme.primary,
                            modifier = Modifier.size(14.dp)
                        )
                        Spacer(Modifier.width(4.dp))
                        Text(
                            text = "粘贴剪贴板: ${quickClipboardText.replace('\n', ' ').take(18)}...",
                            fontSize = 12.sp,
                            color = MiuixTheme.colorScheme.primary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                }
            }
        }

        // 2. 待发图片预览条
        if (selectedImageBytes != null) {
            val previewBitmap = remember(selectedImageBytes) {
                BitmapFactory.decodeByteArray(selectedImageBytes, 0, selectedImageBytes.size)?.asImageBitmap()
            }
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(60.dp)
                        .clip(RoundedCornerShape(8.dp))
                ) {
                    if (previewBitmap != null) {
                        Image(
                            bitmap = previewBitmap,
                            contentDescription = "待发图片",
                            modifier = Modifier.fillMaxSize(),
                            contentScale = ContentScale.Crop
                        )
                    }
                }
                Spacer(Modifier.width(8.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text("已选择图片 (${selectedImageBytes.size / 1024} KB)", fontSize = 12.sp, fontWeight = FontWeight.Medium)
                    Text("点击发送将同时推送此图片", fontSize = 11.sp, color = MiuixTheme.colorScheme.onBackgroundVariant)
                }
                IconButton(onClick = onClearSelectedImage) {
                    Icon(imageVector = LucideIcons.X, contentDescription = "移除图片", tint = MiuixTheme.colorScheme.error)
                }
            }
        }

        // 3. 输入框与操作按钮
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            // 选择图片按钮
            IconButton(
                onClick = onPickImage,
                modifier = Modifier.size(38.dp)
            ) {
                Icon(
                    imageVector = LucideIcons.ImagePlus,
                    contentDescription = "选择图片",
                    tint = if (selectedImageBytes != null) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                )
            }

            Spacer(Modifier.width(6.dp))

            // 文本输入框
            TextField(
                state = inputTextState,
                label = "输入要推送的文本或粘贴…",
                useLabelAsPlaceholder = true,
                modifier = Modifier.weight(1f)
            )

            Spacer(Modifier.width(6.dp))

            // 发送按钮
            IconButton(
                onClick = onSend,
                enabled = !isSending,
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(
                        if (!isSending) MiuixTheme.colorScheme.primary
                        else MiuixTheme.colorScheme.surfaceContainer
                    )
            ) {
                if (isSending) {
                    LoadingSpinner(
                        modifier = Modifier.size(18.dp),
                        color = Color.White,
                        strokeWidth = 2.dp
                    )
                } else {
                    Icon(
                        imageVector = LucideIcons.Send,
                        contentDescription = "发送",
                        tint = Color.White,
                        modifier = Modifier.size(18.dp)
                    )
                }
            }
        }
    }
}

/**
 * 纯 Compose Canvas 实现的轻量平滑转圈加载指示器
 */
@Composable
private fun LoadingSpinner(
    modifier: Modifier = Modifier,
    color: Color = Color.White,
    strokeWidth: Dp = 2.dp
) {
    val infiniteTransition = rememberInfiniteTransition(label = "loading_spinner")
    val rotation by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(
            animation = tween(900, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "rotation"
    )
    Canvas(modifier = modifier.graphicsLayer { rotationZ = rotation }) {
        drawArc(
            color = color,
            startAngle = 0f,
            sweepAngle = 280f,
            useCenter = false,
            style = Stroke(width = strokeWidth.toPx(), cap = StrokeCap.Round)
        )
    }
}
