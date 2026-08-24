package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.data.DeviceInfo
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
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
import top.yukonga.miuix.kmp.basic.SnackbarResult
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.Refresh
import top.yukonga.miuix.kmp.icon.extended.Search
import top.yukonga.miuix.kmp.window.WindowDialog
import top.yukonga.miuix.kmp.theme.MiuixTheme
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import kotlin.math.abs

/**
 * 手动推送与跨设备即时互传页面（聊天流形式设计）
 * - 顶栏与底部输入栏均引入原生液态毛玻璃模糊（BarBlurSurface）
 * - 增加目标设备筛选与选择功能，默认全选所有在线设备
 * - 移除粘贴剪贴板功能
 * - 采用 reverseLayout 倒序流，默认直达底部且进入 0 卡顿
 * - 乐观即时上屏 + 全矢量 Lucide 图标 + 固定紧凑小标题
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

    BackHandler(enabled = true) {
        onBack()
    }

    val selfDeviceId = remember { SyncSettings.ensureDeviceId(context) }
    val selfDeviceName = remember { SyncSettings.deviceName(context) }
    val isServerConnected by ClipboardMonitorService.isServerConnected.collectAsState()
    val capturedList by ClipboardMonitorService.captured.collectAsState()

    // 聊天消息列表（仅展示手动互传/手动发送的消息，最新在前，配合 reverseLayout = true 默认直接渲染在底部）
    val chatMessages = remember(capturedList) { capturedList.filter { it.isManual } }

    // 远端设备列表与目标选择状态（默认全选所有在线设备）
    var deviceList by remember { mutableStateOf<List<DeviceInfo>>(emptyList()) }
    var selectedDeviceIds by remember { mutableStateOf<Set<String>>(emptySet()) }
    var onlineDevicesCount by remember { mutableIntStateOf(0) }
    var isSending by remember { mutableStateOf(false) }

    // 输入框状态
    val inputTextState = rememberTextFieldState()
    var selectedImageBytes by remember { mutableStateOf<ByteArray?>(null) }
    var previewImageClip by remember { mutableStateOf<CapturedClip?>(null) }
    var showClearConfirmDialog by remember { mutableStateOf(false) }

    // 背景毛玻璃 Backdrop
    val barSurface = MiuixTheme.colorScheme.surface
    val pageBackdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )

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

    // 页面进入或消息数量更新时，自动锚定滚动到底部 (reverseLayout 下 index 0 即为最底部最新消息)
    LaunchedEffect(Unit) {
        listState.scrollToItem(0)
    }

    LaunchedEffect(chatMessages.size) {
        if (chatMessages.isNotEmpty()) {
            listState.animateScrollToItem(0)
        }
    }

    // 刷新在线设备列表与目标选择状态
    val refreshDevices: suspend () -> Unit = {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isNotBlank() && SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
            try {
                val api = SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
                val remoteDevices = withContext(Dispatchers.IO) { api.getDevices() }
                val others = remoteDevices.filter { it.id != selfDeviceId }
                deviceList = others
                onlineDevicesCount = others.count { it.online }
                // 默认仅全选在线设备；若无设备在线则保持为空，绝不默认勾选离线设备
                val onlineIds = others.filter { it.online }.map { it.id }.toSet()
                selectedDeviceIds = onlineIds
            } catch (e: Exception) {
                android.util.Log.e("NexClip", "ManualPushPage refreshDevices error", e)
            }
        } else {
            onlineDevicesCount = 0
            deviceList = emptyList()
            selectedDeviceIds = emptySet()
        }
    }

    // 进入页面时立即拉取在线设备列表，并在连接就绪时自动刷新
    LaunchedEffect(Unit) {
        refreshDevices()
    }

    LaunchedEffect(isServerConnected) {
        if (isServerConnected) {
            refreshDevices()
        }
    }

    // 发送消息核心逻辑（乐观即时上屏，异步网络推送，0 界面卡顿，明确反馈送达）
    val onSendMessage: () -> Unit = {
        val textToSend = inputTextState.text.toString().trim()
        val imageToSend = selectedImageBytes

        if (textToSend.isEmpty() && imageToSend == null) {
            scope.launch { snackbarHostState?.showAppSnack("请输入内容或选择图片", SnackType.Info) }
        } else if (deviceList.isNotEmpty() && selectedDeviceIds.isEmpty()) {
            scope.launch { snackbarHostState?.showAppSnack("请至少选择一台目标设备", SnackType.Info) }
        } else {
            val serverUrl = SyncSettings.serverUrl(context)
            if (serverUrl.isBlank()) {
                scope.launch { snackbarHostState?.showAppSnack("请先在设置中配置服务器地址", SnackType.Info) }
            } else if (!SyncSettings.isPaired(context)) {
                scope.launch { snackbarHostState?.showAppSnack("设备尚未配对，请先完成配对", SnackType.Info) }
            } else {
                val targetNames = if (selectedDeviceIds.isNotEmpty() && selectedDeviceIds.size < deviceList.size) {
                    deviceList.filter { it.id in selectedDeviceIds }.map { it.name }
                } else {
                    deviceList.map { it.name }
                }
                val targetSummary = if (targetNames.isNotEmpty()) targetNames.joinToString("、") else "所有在线设备"

                // 1. 立即清空输入框与选图状态，释放 UI 响应
                inputTextState.setTextAndPlaceCursorAtEnd("")
                selectedImageBytes = null
                isSending = true

                // 2. 乐观立即上屏：先写入本地数据库/内存流，消息瞬间出现在底部！
                val localImageKey = if (imageToSend != null) "local_${System.currentTimeMillis()}" else null
                if (imageToSend != null && localImageKey != null) {
                    ImageLoader.saveBytesToDisk(context, localImageKey, imageToSend)
                }
                ClipboardMonitorService.addCaptured(
                    context = context,
                    text = textToSend.ifEmpty { "[图片]" },
                    imageRef = localImageKey,
                    sourceDevice = "本机",
                    isManual = true
                )

                // 立即滚动至最底部最新消息
                scope.launch {
                    listState.animateScrollToItem(0)
                }

                // 3. 异步后台网络上传并反馈送达状态
                scope.launch {
                    try {
                        val api = SyncApi(serverUrl, selfDeviceId, SyncSettings.deviceToken(context))
                        withContext(Dispatchers.IO) {
                            if (imageToSend != null) {
                                val entry = api.uploadImage(imageToSend, selfDeviceId, selfDeviceName, isManual = true)
                                val serverKey = entry.imageRef ?: localImageKey ?: "img_${System.currentTimeMillis()}"
                                ImageLoader.saveBytesToDisk(context, serverKey, imageToSend)
                            } else {
                                if (selectedDeviceIds.isNotEmpty() && selectedDeviceIds.size < deviceList.size) {
                                    api.sendToDevices(textToSend, selfDeviceId, selfDeviceName, selectedDeviceIds.toList())
                                } else {
                                    api.putText(textToSend, selfDeviceId, selfDeviceName, isManual = true)
                                }
                            }
                        }
                        snackbarHostState?.showAppSnack("已成功送达至 $targetSummary", SnackType.Success)
                    } catch (e: Exception) {
                        snackbarHostState?.showAppSnack("网络推送失败: ${e.message ?: "网络异常"}", SnackType.Error)
                    } finally {
                        isSending = false
                    }
                }
            }
        }
    }

    val statusBarTop = WindowInsets.statusBars.asPaddingValues().calculateTopPadding()
    val navBarBottom = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
    val imeBottom = WindowInsets.ime.asPaddingValues().calculateBottomPadding()

    // 当软键盘弹出或收起时，自动滚动消息列表至最新一条，且动态调整底部 padding
    LaunchedEffect(imeBottom) {
        if (imeBottom > 0.dp) {
            listState.animateScrollToItem(0)
        }
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.background)
    ) {
        // 1. 聊天消息流列表（置于底层，穿透并在顶部/底部毛玻璃下产生模糊折射）
        Box(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(pageBackdrop)
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
                        modifier = Modifier.size(44.dp),
                        tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.5f)
                    )
                    Spacer(Modifier.height(12.dp))
                    Text(
                        text = "即时互传",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        color = MiuixTheme.colorScheme.onSurface
                    )
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = "在下方输入文本或选择相册图片，即可向已选中的 PC、手机等多端设备实时推送。",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        textAlign = TextAlign.Center,
                        lineHeight = 18.sp
                    )
                }
            } else {
                LazyColumn(
                    state = listState,
                    reverseLayout = true,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(
                        top = statusBarTop + 104.dp,
                        bottom = if (imeBottom > 0.dp) imeBottom + 72.dp else navBarBottom + 88.dp,
                        start = 16.dp,
                        end = 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    itemsIndexed(chatMessages, key = { _, clip -> clip.id }) { index, clip ->
                        val isSelf = clip.sourceDevice == null || clip.sourceDevice == "本机"
                        // 智能合并相邻 3 分钟内的连续消息时间戳
                        val prevOlderClip = chatMessages.getOrNull(index + 1)
                        val showTimeHeader = prevOlderClip == null || abs(clip.time - prevOlderClip.time) >= 3 * 60 * 1000L

                        Column(modifier = Modifier.fillMaxWidth()) {
                            if (showTimeHeader) {
                                ChatTimeHeader(clip.time)
                            }
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
        }

        // 2. 顶部毛玻璃栏 (固定小标题 + 设备选择筛选器)
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
                    // 标准小标题顶栏
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

                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = "即时互传",
                                fontSize = 17.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = MiuixTheme.colorScheme.onSurface,
                                maxLines = 1
                            )
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Box(
                                    modifier = Modifier
                                        .size(6.dp)
                                        .clip(CircleShape)
                                        .background(if (isServerConnected) Color(0xFF34C759) else MiuixTheme.colorScheme.error)
                                )
                                Spacer(Modifier.width(4.dp))
                                Text(
                                    text = if (isServerConnected) "云端在线 · 在线设备: ${onlineDevicesCount} 台" else "未连接服务器",
                                    fontSize = 11.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                                    maxLines = 1
                                )
                            }
                        }

                        IconButton(
                            onClick = {
                                showClearConfirmDialog = true
                            },
                            enabled = chatMessages.isNotEmpty()
                        ) {
                            Icon(
                                imageVector = LucideIcons.Trash2,
                                contentDescription = "清空消息",
                                tint = if (chatMessages.isNotEmpty()) MiuixTheme.colorScheme.onSurface else MiuixTheme.colorScheme.onSurface.copy(alpha = 0.3f)
                            )
                        }

                        IconButton(
                            onClick = {
                                scope.launch {
                                    val url = SyncSettings.serverUrl(context)
                                    if (url.isBlank() || !SyncSettings.isPaired(context)) {
                                        snackbarHostState?.showAppSnack("请先连接并配对服务器", SnackType.Info)
                                    } else {
                                        try {
                                            refreshDevices()
                                            snackbarHostState?.showAppSnack("在线设备列表已刷新", SnackType.Success)
                                        } catch (e: Exception) {
                                            snackbarHostState?.showAppSnack("刷新失败: ${e.message}", SnackType.Error)
                                        }
                                    }
                                }
                            }
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Refresh,
                                contentDescription = "刷新设备",
                                tint = MiuixTheme.colorScheme.onSurface
                            )
                        }
                    }

                    // 目标设备选择条（默认全选在线设备，支持单个或一键切换）
                    if (deviceList.isNotEmpty()) {
                        DeviceSelectorChipsRow(
                            devices = deviceList,
                            selectedIds = selectedDeviceIds,
                            onToggleDevice = { id ->
                                selectedDeviceIds = if (id in selectedDeviceIds) {
                                    selectedDeviceIds - id
                                } else {
                                    selectedDeviceIds + id
                                }
                            },
                            onToggleAllOnline = {
                                val onlineIds = deviceList.filter { it.online }.map { it.id }.toSet()
                                selectedDeviceIds = if (onlineIds.isNotEmpty() && selectedDeviceIds.containsAll(onlineIds)) {
                                    emptySet()
                                } else {
                                    onlineIds
                                }
                            }
                        )
                    }

                    Spacer(Modifier.height(4.dp))
                }
            }
        }

        // 3. 底部输入与发送面板 (置底毛玻璃容器)
        Box(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .imePadding()
        ) {
            BarBlurSurface(backdrop = pageBackdrop) {
                SurfaceBottomInputBar(
                    inputTextState = inputTextState,
                    selectedImageBytes = selectedImageBytes,
                    isSending = isSending,
                    onClearSelectedImage = { selectedImageBytes = null },
                    onPickImage = { imagePickerLauncher.launch("image/*") },
                    onSend = onSendMessage
                )
            }
        }

        // 大图全屏查看器（支持图片全屏手势缩放、平移、双击放大、复制、保存相册与系统分享）
        FullscreenImagePreviewDialog(
            show = previewImageClip != null,
            imageRef = previewImageClip?.imageRef,
            rawText = previewImageClip?.text,
            onDismissRequest = { previewImageClip = null }
        )

        // 清空互传消息二次确认弹窗
        WindowDialog(
            show = showClearConfirmDialog,
            title = "清空互传消息",
            summary = "确定要清空即时互传的所有聊天记录吗？此操作仅清空互传流，不会影响普通剪贴板历史记录，且可撤销。",
            onDismissRequest = { showClearConfirmDialog = false }
        ) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Button(
                    onClick = { showClearConfirmDialog = false },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        val snapshot = chatMessages
                        ClipboardMonitorService.deleteClips(context, snapshot)
                        showClearConfirmDialog = false
                        scope.launch {
                            val result = snackbarHostState?.showAppSnack(
                                "已清空互传消息",
                                SnackType.Success,
                                actionLabel = "撤销"
                            )
                            if (result == SnackbarResult.ActionPerformed) {
                                val current = ClipboardMonitorService.captured.value
                                ClipboardMonitorService.replaceAll(context, snapshot + current)
                            }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.error,
                        contentColor = Color.White
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("清空")
                }
            }
        }
    }
}

/**
 * 目标设备多选过滤与快速切换芯片条
 */
@Composable
private fun DeviceSelectorChipsRow(
    devices: List<DeviceInfo>,
    selectedIds: Set<String>,
    onToggleDevice: (String) -> Unit,
    onToggleAllOnline: () -> Unit
) {
    val onlineDevices = remember(devices) { devices.filter { it.online } }
    val isAllOnlineSelected = remember(onlineDevices, selectedIds) {
        onlineDevices.isNotEmpty() && selectedIds.containsAll(onlineDevices.map { it.id })
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 2.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        // 全选在线设备按钮芯片
        Box(
            modifier = Modifier
                .clip(RoundedCornerShape(8.dp))
                .background(
                    if (isAllOnlineSelected) MiuixTheme.colorScheme.primary.copy(alpha = 0.15f)
                    else MiuixTheme.colorScheme.surfaceContainer
                )
                .border(
                    width = 1.dp,
                    color = if (isAllOnlineSelected) MiuixTheme.colorScheme.primary else Color.Transparent,
                    shape = RoundedCornerShape(8.dp)
                )
                .clickable { onToggleAllOnline() }
                .padding(horizontal = 10.dp, vertical = 5.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    imageVector = if (isAllOnlineSelected) LucideIcons.Check else LucideIcons.Globe,
                    contentDescription = "全选在线设备",
                    modifier = Modifier.size(13.dp),
                    tint = if (isAllOnlineSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                )
                Spacer(Modifier.width(4.dp))
                Text(
                    text = "全部在线 (${onlineDevices.size})",
                    fontSize = 12.sp,
                    fontWeight = if (isAllOnlineSelected) FontWeight.SemiBold else FontWeight.Normal,
                    color = if (isAllOnlineSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                )
            }
        }

        // 单个设备选择芯片
        devices.forEach { dev ->
            val isSelected = dev.id in selectedIds
            val devIcon: ImageVector = resolveDeviceIcon(dev.name, dev.platform)

            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(
                        if (isSelected) MiuixTheme.colorScheme.primary.copy(alpha = 0.15f)
                        else MiuixTheme.colorScheme.surfaceContainer
                    )
                    .border(
                        width = 1.dp,
                        color = if (isSelected) MiuixTheme.colorScheme.primary else Color.Transparent,
                        shape = RoundedCornerShape(8.dp)
                    )
                    .clickable { onToggleDevice(dev.id) }
                    .padding(horizontal = 9.dp, vertical = 5.dp)
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    // 在线状态指示圆点
                    Box(
                        modifier = Modifier
                            .size(6.dp)
                            .clip(CircleShape)
                            .background(if (dev.online) Color(0xFF34C759) else Color.Gray.copy(alpha = 0.5f))
                    )
                    Spacer(Modifier.width(5.dp))
                    Icon(
                        imageVector = devIcon,
                        contentDescription = dev.name,
                        modifier = Modifier.size(13.dp),
                        tint = if (isSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                    )
                    Spacer(Modifier.width(4.dp))
                    Text(
                        text = dev.name,
                        fontSize = 12.sp,
                        fontWeight = if (isSelected) FontWeight.SemiBold else FontWeight.Normal,
                        color = if (isSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                    )
                }
            }
        }
    }
}

/**
 * 格式化即时互传消息流居中时间戳
 */
private fun formatChatTimeHeader(time: Long): String {
    val now = Calendar.getInstance()
    val msgTime = Calendar.getInstance().apply { timeInMillis = time }
    val isSameDay = now.get(Calendar.YEAR) == msgTime.get(Calendar.YEAR) &&
            now.get(Calendar.DAY_OF_YEAR) == msgTime.get(Calendar.DAY_OF_YEAR)
    val isYesterday = now.get(Calendar.YEAR) == msgTime.get(Calendar.YEAR) &&
            now.get(Calendar.DAY_OF_YEAR) - msgTime.get(Calendar.DAY_OF_YEAR) == 1
    val isSameYear = now.get(Calendar.YEAR) == msgTime.get(Calendar.YEAR)

    val timeFormat = SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(time))
    return when {
        isSameDay -> timeFormat
        isYesterday -> "昨天 $timeFormat"
        isSameYear -> SimpleDateFormat("M月d日 HH:mm", Locale.getDefault()).format(Date(time))
        else -> SimpleDateFormat("yyyy年M月d日 HH:mm", Locale.getDefault()).format(Date(time))
    }
}

/** 居中轻量毛玻璃时间戳气泡 */
@Composable
private fun ChatTimeHeader(time: Long) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        contentAlignment = Alignment.Center
    ) {
        Box(
            modifier = Modifier
                .clip(RoundedCornerShape(10.dp))
                .background(MiuixTheme.colorScheme.surfaceContainerHigh.copy(alpha = 0.75f))
                .padding(horizontal = 10.dp, vertical = 2.5.dp)
        ) {
            Text(
                text = formatChatTimeHeader(time),
                fontSize = 11.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.75f),
                fontWeight = FontWeight.Medium
            )
        }
    }
}

/**
 * 单条聊天消息气泡渲染（全部使用 Lucide 原生矢量图标，杜绝 Emoji）
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
        // 设备名称与时间标签（全矢量 Lucide 图标）
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp)
        ) {
            if (!isSelf) {
                val devLabel = clip.sourceDevice ?: "远端设备"
                val devIcon: ImageVector = resolveDeviceIcon(devLabel)
                Icon(
                    imageVector = devIcon,
                    contentDescription = devLabel,
                    modifier = Modifier.size(13.dp),
                    tint = MiuixTheme.colorScheme.primary
                )
                Spacer(Modifier.width(4.dp))
                Text(
                    text = devLabel,
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
                Spacer(Modifier.width(6.dp))
                Text(
                    text = "本机",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = MiuixTheme.colorScheme.primary
                )
                Spacer(Modifier.width(3.dp))
                Icon(
                    imageVector = LucideIcons.Smartphone,
                    contentDescription = "本机",
                    modifier = Modifier.size(12.dp),
                    tint = MiuixTheme.colorScheme.primary
                )
                Spacer(Modifier.width(4.dp))
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .background(Color(0xFF34C759).copy(alpha = 0.12f))
                        .padding(horizontal = 4.dp, vertical = 1.dp)
                ) {
                    Icon(
                        imageVector = LucideIcons.Check,
                        contentDescription = "已送达",
                        modifier = Modifier.size(10.dp),
                        tint = Color(0xFF34C759)
                    )
                    Spacer(Modifier.width(2.dp))
                    Text(
                        text = "已送达",
                        fontSize = 9.sp,
                        fontWeight = FontWeight.Medium,
                        color = Color(0xFF34C759)
                    )
                }
            }
        }

        Spacer(Modifier.height(2.dp))

        // 消息气泡卡片（限制最大宽度 280dp 与最大高度 360dp，超长文本支持折叠展开）
        val bubbleShape = if (isSelf) {
            RoundedCornerShape(topStart = 16.dp, topEnd = 4.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
        } else {
            RoundedCornerShape(topStart = 4.dp, topEnd = 16.dp, bottomStart = 16.dp, bottomEnd = 16.dp)
        }

        var isExpanded by remember { mutableStateOf(false) }
        var canExpand by remember { mutableStateOf(false) }

        Box(
            modifier = Modifier
                .widthIn(min = 40.dp, max = if (clip.isImage) 240.dp else 280.dp)
                .heightIn(max = 360.dp)
                .clip(bubbleShape)
                .background(
                    if (isSelf) MiuixTheme.colorScheme.primary
                    else MiuixTheme.colorScheme.surfaceContainer
                )
                .padding(if (clip.isImage) 4.dp else 12.dp)
        ) {
            if (clip.isImage) {
                // 图片消息气泡（严格限制缩略图高度在 90dp ~ 220dp 之间）
                Column {
                    var bitmap by remember(clip.imageRef, clip.text) {
                        mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null)
                    }
                    var isLoading by remember(clip.imageRef, clip.text) {
                        mutableStateOf(true)
                    }
                    LaunchedEffect(clip.imageRef, clip.text) {
                        isLoading = true
                        bitmap = ImageLoader.loadImageBitmap(context, clip.imageRef, clip.text)
                        isLoading = false
                    }

                    if (bitmap != null) {
                        Box(
                            modifier = Modifier
                                .clip(RoundedCornerShape(12.dp))
                                .clickable { onImageClick() }
                        ) {
                            Image(
                                bitmap = bitmap!!,
                                contentDescription = "图片消息",
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .heightIn(min = 90.dp, max = 220.dp),
                                contentScale = ContentScale.Crop
                            )
                            // 放大镜微角标
                            Box(
                                modifier = Modifier
                                    .align(Alignment.BottomEnd)
                                    .padding(4.dp)
                                    .clip(RoundedCornerShape(4.dp))
                                    .background(Color.Black.copy(alpha = 0.55f))
                                    .padding(horizontal = 5.dp, vertical = 2.dp)
                            ) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Icon(
                                        imageVector = MiuixIcons.Normal.Search,
                                        contentDescription = "查看大图",
                                        tint = Color.White,
                                        modifier = Modifier.size(10.dp)
                                    )
                                    Spacer(Modifier.width(2.dp))
                                    Text(
                                        text = "预览",
                                        color = Color.White,
                                        fontSize = 9.sp
                                    )
                                }
                            }
                        }
                    } else if (isLoading) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(120.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .background(Color.Black.copy(alpha = 0.08f)),
                            contentAlignment = Alignment.Center
                        ) {
                            LoadingSpinner(
                                modifier = Modifier.size(20.dp),
                                color = if (isSelf) Color.White else MiuixTheme.colorScheme.primary,
                                strokeWidth = 2.dp
                            )
                        }
                    } else {
                        // 加载失败或图片已失效状态
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(90.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .background(Color.Black.copy(alpha = 0.08f))
                                .clickable { onImageClick() },
                            contentAlignment = Alignment.Center
                        ) {
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                Icon(
                                    imageVector = LucideIcons.Image,
                                    contentDescription = "图片",
                                    tint = (if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface).copy(alpha = 0.6f),
                                    modifier = Modifier.size(22.dp)
                                )
                                Spacer(Modifier.height(4.dp))
                                Text(
                                    text = "点击尝试重载预览",
                                    fontSize = 11.sp,
                                    color = (if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface).copy(alpha = 0.6f)
                                )
                            }
                        }
                    }

                    if (clip.text.isNotBlank() && clip.text != "[图片]") {
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = clip.text,
                            fontSize = 13.sp,
                            maxLines = 4,
                            overflow = TextOverflow.Ellipsis,
                            color = if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface,
                            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
                        )
                    }
                }
            } else {
                // 纯文本消息气泡（长文本限制最多 8 行，并提供展开/收起按钮）
                Column {
                    Text(
                        text = clip.text,
                        fontSize = 14.sp,
                        lineHeight = 20.sp,
                        maxLines = if (isExpanded) 18 else 8,
                        overflow = TextOverflow.Ellipsis,
                        onTextLayout = { textLayoutResult ->
                            if (!canExpand && textLayoutResult.hasVisualOverflow) {
                                canExpand = true
                            }
                        },
                        color = if (isSelf) Color.White else MiuixTheme.colorScheme.onSurface,
                        modifier = Modifier.clickable { onCopyText(clip.text) }
                    )

                    if (canExpand) {
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = if (isExpanded) "收起" else "展开全文",
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Medium,
                            color = if (isSelf) Color.White.copy(alpha = 0.85f) else MiuixTheme.colorScheme.primary,
                            modifier = Modifier
                                .clip(RoundedCornerShape(4.dp))
                                .clickable { isExpanded = !isExpanded }
                                .padding(vertical = 2.dp)
                        )
                    }
                }
            }
        }
    }
}

/**
 * 底部输入与多媒体附件面板（支持玻璃背景）
 */
@Composable
private fun SurfaceBottomInputBar(
    inputTextState: TextFieldState,
    selectedImageBytes: ByteArray?,
    isSending: Boolean,
    onClearSelectedImage: () -> Unit,
    onPickImage: () -> Unit,
    onSend: () -> Unit
) {
    val imeBottom = WindowInsets.ime.asPaddingValues().calculateBottomPadding()
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (imeBottom == 0.dp) Modifier.navigationBarsPadding() else Modifier)
            .padding(horizontal = 12.dp, vertical = 8.dp)
    ) {
        // 1. 待发图片预览条（支持上传中动态转圈与状态提示）
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
                        .size(56.dp)
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
                    if (isSending) {
                        Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .background(Color.Black.copy(alpha = 0.5f)),
                            contentAlignment = Alignment.Center
                        ) {
                            LoadingSpinner(
                                modifier = Modifier.size(22.dp),
                                color = Color.White,
                                strokeWidth = 2.dp
                            )
                        }
                    }
                }
                Spacer(Modifier.width(8.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = if (isSending) "正在上传并推送图片…" else "已选择图片 (${selectedImageBytes.size / 1024} KB)",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium,
                        color = if (isSending) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                    )
                    Text(
                        text = if (isSending) "正在向所选设备传输中…" else "点击右侧发送按钮立即推送",
                        fontSize = 11.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
                if (!isSending) {
                    IconButton(onClick = onClearSelectedImage) {
                        Icon(imageVector = LucideIcons.X, contentDescription = "移除图片", tint = MiuixTheme.colorScheme.error)
                    }
                }
            }
        }

        // 2. 输入框与操作按钮
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
 * 纯 Compose Canvas 实现的轻量平滑转圈
 * 加载微旋转指示器
 */
@Composable
internal fun LoadingSpinner(
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
