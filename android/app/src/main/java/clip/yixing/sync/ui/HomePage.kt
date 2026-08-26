package clip.yixing.sync.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.input.clearText
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
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.StatusRow
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.formatTime
import clip.yixing.sync.hook.ModuleStatusStore
import clip.yixing.sync.shizuku.ShizukuClipboardManager
import clip.yixing.sync.smartaction.SmartActionEngine
import clip.yixing.sync.smartaction.SmartActionChip
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.CaptureMethod
import clip.yixing.sync.util.ClipboardTest
import clip.yixing.sync.util.ImageLoader
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Switch
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.ChevronForward
import top.yukonga.miuix.kmp.icon.extended.Clear
import top.yukonga.miuix.kmp.icon.extended.Copy
import top.yukonga.miuix.kmp.icon.extended.UploadCloud
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical

@Composable
internal fun HomePage(
    scrollBehavior: ScrollBehavior,
    topPadding: Dp,
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState? = null,
    onNavigateToRecords: () -> Unit = {},
    onNavigateToSettings: () -> Unit = {},
    onOpenQrScanner: () -> Unit = {},
    onOpenManualPush: () -> Unit = {},
    onOverlayActiveChanged: (Boolean) -> Unit = {},
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val serviceRunning by ClipboardMonitorService.isRunning.collectAsState()
    val isServerConnected by ClipboardMonitorService.isServerConnected.collectAsState()
    val serverConnectionState by ClipboardMonitorService.serverConnectionState.collectAsState()
    val captured by ClipboardMonitorService.captured.collectAsState()

    val currentClip = captured.firstOrNull()
    val currentText = remember(captured) {
        currentClip?.text ?: ClipboardTest.readClipboard(context) ?: ""
    }

    var previewImageClip by remember { mutableStateOf<CapturedClip?>(null) }
    var isManualPushing by remember { mutableStateOf(false) }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

    val serverUrl = remember(isServerConnected) { SyncSettings.serverUrl(context) }
    var onlineDevicesCount by remember { mutableIntStateOf(0) }
    var otherOnlineDevicesCount by remember { mutableIntStateOf(0) }
    var totalDevicesCount by remember { mutableIntStateOf(0) }

    // 在线设备统计更新 (进入主页及连接就绪时拉取)
    val refreshDeviceCounts: suspend () -> Unit = {
        val selfDevId = SyncSettings.ensureDeviceId(context)
        if (serverUrl.isNotBlank() && SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
            try {
                val api = SyncApi(serverUrl, selfDevId, SyncSettings.deviceToken(context))
                val list = withContext(Dispatchers.IO) { api.getDevices() }
                onlineDevicesCount = list.count { it.online }
                otherOnlineDevicesCount = list.count { it.online && it.id != selfDevId }
                totalDevicesCount = list.size
            } catch (_: Exception) {
            }
        } else {
            onlineDevicesCount = 0
            otherOnlineDevicesCount = 0
            totalDevicesCount = 0
        }
    }

    LaunchedEffect(Unit) {
        refreshDeviceCounts()
    }

    LaunchedEffect(isServerConnected) {
        if (isServerConnected) {
            refreshDeviceCounts()
        }
    }

    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .overScrollVertical()
            .nestedScroll(scrollBehavior.nestedScrollConnection),
        contentPadding = PaddingValues(
            start = 16.dp,
            end = 16.dp,
            top = topPadding + 8.dp,
            bottom = 8.dp
        ),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        // 1. 统一状态与服务控制看板
        item {
            SyncStatusHeroCard(
                serviceRunning = serviceRunning,
                onToggleService = { checked ->
                    if (checked) {
                        requestNotificationPermissionIfNeeded(context, permissionLauncher)
                        ClipboardMonitorService.start(context)
                    } else {
                        ClipboardMonitorService.stop(context)
                    }
                },
                serverUrl = serverUrl,
                isServerConnected = isServerConnected,
                serverConnectionState = serverConnectionState,
                onlineDevicesCount = onlineDevicesCount,
                totalDevicesCount = totalDevicesCount,
                onNavigateToSettings = onNavigateToSettings,
                onOpenQrScanner = onOpenQrScanner
            )
        }

        // 2. 快捷功能入口卡片行 (即时互传 & 扫码配对)
        item {
            QuickActionsRow(
                onlineDevicesCount = otherOnlineDevicesCount,
                onOpenManualPush = onOpenManualPush,
                onOpenQrScanner = onOpenQrScanner
            )
        }

        // 3. 当前剪贴板卡片及快捷操作
        item {
            val hasContent = currentClip?.isImage == true || currentText.isNotBlank()
            if (hasContent) {
                SectionBlock(
                    title = "当前剪贴板",
                    trailing = {
                        val sourceLabel = if (!currentClip?.sourceDevice.isNullOrBlank() && currentClip?.sourceDevice != "本机") {
                            currentClip?.sourceDevice
                        } else {
                            currentClip?.sourceApp ?: "本机"
                        }
                        if (!sourceLabel.isNullOrBlank()) {
                            AppSourceBadge(
                                label = sourceLabel,
                                packageName = if (currentClip?.sourceDevice == null || currentClip?.sourceDevice == "本机") currentClip?.sourcePackage else null
                            )
                            Spacer(Modifier.width(4.dp))
                        }
                        IconButton(
                            onClick = {
                                scope.launch {
                                    if (currentClip?.isImage == true) {
                                        val ok = ImageLoader.copyImageToClipboard(context, currentClip.imageRef, currentClip.text)
                                        snackbarHostState?.showAppSnack(if (ok) "已复制图片到剪贴板" else "复制失败", if (ok) SnackType.Success else SnackType.Error)
                                    } else {
                                        copyToClipboard(context, currentText)
                                        snackbarHostState?.showAppSnack("已复制到剪贴板", SnackType.Success)
                                    }
                                }
                            }
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Copy,
                                contentDescription = "复制",
                                tint = MiuixTheme.colorScheme.primary
                            )
                        }
                        IconButton(
                            onClick = {
                                clearClipboard(context)
                                scope.launch {
                                    snackbarHostState?.showAppSnack("剪贴板已清空", SnackType.Info)
                                }
                            }
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Clear,
                                contentDescription = "清空",
                                tint = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    },
                ) {
                    if (currentClip?.isImage == true) {
                        ClipImageThumbnail(
                            imageRef = currentClip.imageRef,
                            rawText = currentClip.text,
                            maxHeight = 180.dp,
                            onClick = { previewImageClip = currentClip }
                        )
                    } else {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(8.dp))
                                .background(MiuixTheme.colorScheme.surfaceContainer.copy(alpha = 0.5f))
                                .padding(10.dp)
                        ) {
                            Text(
                                text = currentText,
                                color = MiuixTheme.colorScheme.onSurface,
                                maxLines = 5,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                    }
                    val currentSmartActions = remember(currentText, currentClip?.isImage) {
                        if (currentClip?.isImage != true) SmartActionEngine.detectActions(context, currentText) else emptyList()
                    }
                    if (currentSmartActions.isNotEmpty()) {
                        Spacer(Modifier.height(8.dp))
                        Row(
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier
                                .fillMaxWidth()
                                .horizontalScroll(rememberScrollState())
                        ) {
                            currentSmartActions.forEach { action ->
                                SmartActionChip(
                                    action = action,
                                    onClick = { action.action(context) }
                                )
                            }
                        }
                    }
                    Spacer(Modifier.height(10.dp))
                    Button(
                        onClick = {
                            val url = SyncSettings.serverUrl(context)
                            if (url.isBlank()) {
                                scope.launch {
                                    snackbarHostState?.showAppSnack("请先在设置中配置服务器地址", SnackType.Info)
                                }
                                return@Button
                            }
                            isManualPushing = true
                            scope.launch {
                                try {
                                    val api = SyncApi(url, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                    withContext(Dispatchers.IO) {
                                        if (currentClip?.isImage == true) {
                                            val bytes = ImageLoader.getImageBytes(context, currentClip.imageRef, currentClip.text)
                                            if (bytes != null) {
                                                api.uploadImage(bytes, SyncSettings.ensureDeviceId(context), SyncSettings.deviceName(context))
                                            } else {
                                                throw Exception("图片数据无法读取")
                                            }
                                        } else {
                                            api.putText(
                                                text = currentText,
                                                deviceId = SyncSettings.ensureDeviceId(context),
                                                deviceName = SyncSettings.deviceName(context)
                                            )
                                        }
                                    }
                                    snackbarHostState?.showAppSnack("已推送至所有设备", SnackType.Success)
                                } catch (e: Exception) {
                                    snackbarHostState?.showAppSnack(e.message ?: "推送失败", SnackType.Error)
                                } finally {
                                    isManualPushing = false
                                }
                            }
                        },
                        enabled = !isManualPushing,
                        colors = ButtonDefaults.buttonColorsPrimary(),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Icon(imageVector = LucideIcons.Upload, contentDescription = "立即推送")
                        Spacer(Modifier.width(6.dp))
                        Text(if (isManualPushing) "推送中…" else "一键推送当前剪贴板")
                    }
                }
            } else {
                // 剪贴板为空时的优雅通栏卡片与快速上手指引
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    insideMargin = PaddingValues(horizontal = 16.dp, vertical = 14.dp)
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Box(
                            modifier = Modifier
                                .size(40.dp)
                                .clip(RoundedCornerShape(10.dp))
                                .background(MiuixTheme.colorScheme.surfaceContainerHigh),
                            contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                imageVector = LucideIcons.ClipboardCheck,
                                contentDescription = "剪贴板为空",
                                tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.75f),
                                modifier = Modifier.size(20.dp)
                            )
                        }
                        Spacer(Modifier.width(12.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = "当前剪贴板为空",
                                fontSize = 14.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = MiuixTheme.colorScheme.onSurface
                            )
                            Spacer(Modifier.height(2.dp))
                            Text(
                                text = "在任意应用复制文字或截图，将自动在此捕获与多端同步",
                                fontSize = 12.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant,
                                lineHeight = 16.sp
                            )
                        }
                    }
                }
            }
        }

        // 4. 最近同步记录预览卡片
        item {
            RecentRecordsCard(
                records = captured.take(3),
                onCopy = { clip ->
                    scope.launch {
                        if (clip.isImage) {
                            val ok = ImageLoader.copyImageToClipboard(context, clip.imageRef, clip.text)
                            snackbarHostState?.showAppSnack(if (ok) "已复制图片" else "复制失败", if (ok) SnackType.Success else SnackType.Error)
                        } else {
                            copyToClipboard(context, clip.text)
                            snackbarHostState?.showAppSnack("已复制该条记录", SnackType.Success)
                        }
                    }
                },
                onPreviewImage = { clip -> previewImageClip = clip },
                onViewAll = onNavigateToRecords
            )
        }

        item {
            Spacer(Modifier.height(bottomInnerPadding))
        }
    }

    // 全屏沉浸式图片预览
    FullscreenImagePreviewDialog(
        show = previewImageClip != null,
        imageRef = previewImageClip?.imageRef,
        rawText = previewImageClip?.text,
        onDismissRequest = { previewImageClip = null }
    )
}

/** 统一状态与服务控制卡片 */
@Composable
private fun SyncStatusHeroCard(
    serviceRunning: Boolean,
    onToggleService: (Boolean) -> Unit,
    serverUrl: String,
    isServerConnected: Boolean,
    serverConnectionState: ClipboardMonitorService.ServerConnectionState,
    onlineDevicesCount: Int,
    totalDevicesCount: Int,
    onNavigateToSettings: () -> Unit,
    onOpenQrScanner: () -> Unit
) {
    val moduleStatus by ModuleStatusStore.moduleStatus.collectAsState()
    val isXposedActivated = moduleStatus.activated
    val shizukuStatus by ShizukuClipboardManager.status.collectAsState()
    val isShizukuActive = shizukuStatus == ShizukuClipboardManager.ShizukuStatus.AUTHORIZED_RUNNING

    Card(
        modifier = Modifier.fillMaxWidth(),
        insideMargin = PaddingValues(horizontal = 16.dp, vertical = 14.dp)
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            // 顶行: 服务主状态 + Switch 开关
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.weight(1f)
                ) {
                    Box(
                        modifier = Modifier
                            .size(38.dp)
                            .clip(CircleShape)
                            .background(
                                if (serviceRunning) MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                                else MiuixTheme.colorScheme.surfaceContainerHigh
                            ),
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(
                            imageVector = LucideIcons.Zap,
                            contentDescription = null,
                            tint = if (serviceRunning) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant,
                            modifier = Modifier.size(19.dp)
                        )
                    }
                    Spacer(Modifier.width(12.dp))
                    Column {
                        Text(
                            text = "剪贴板同步服务",
                            fontSize = 16.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = MiuixTheme.colorScheme.onSurface
                        )
                        Spacer(Modifier.height(2.dp))
                        Text(
                            text = if (serviceRunning) "后台监听中 · 实时捕获与双向同步" else "服务已停止 · 打开右侧开关开启",
                            fontSize = 12.sp,
                            color = if (serviceRunning) MiuixTheme.colorScheme.primary.copy(alpha = 0.85f) else MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                }
                Switch(
                    checked = serviceRunning,
                    onCheckedChange = onToggleService
                )
            }

            Spacer(Modifier.height(12.dp))
            HorizontalDivider(color = MiuixTheme.colorScheme.dividerLine, thickness = Dp.Hairline)
            Spacer(Modifier.height(10.dp))

            // 底行状态信息徽标流 (云端连接、LSPosed 模块、在线设备)
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                // 1. 云端连接状态
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable {
                            if (serverUrl.isBlank()) onOpenQrScanner() else onNavigateToSettings()
                        }
                        .padding(horizontal = 4.dp, vertical = 3.dp)
                ) {
                    val dotColor = when {
                        isServerConnected -> Color(0xFF34C759)
                        serverConnectionState == ClipboardMonitorService.ServerConnectionState.CONNECTING -> Color(0xFFFF9500)
                        else -> Color(0xFFFF3B30)
                    }
                    Box(
                        modifier = Modifier
                            .size(7.dp)
                            .clip(CircleShape)
                            .background(dotColor)
                    )
                    Spacer(Modifier.width(5.dp))
                    Text(
                        text = when {
                            serverUrl.isBlank() -> "未配对云端"
                            isServerConnected -> "云端已就绪"
                            serverConnectionState == ClipboardMonitorService.ServerConnectionState.CONNECTING -> "正在连接"
                            else -> "连接异常"
                        },
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium,
                        color = MiuixTheme.colorScheme.onSurface
                    )
                    Icon(
                        imageVector = MiuixIcons.Normal.ChevronForward,
                        contentDescription = null,
                        tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.5f),
                        modifier = Modifier.size(11.dp)
                    )
                }

                // 2. 增强监听状态 (LSPosed / Shizuku)
                val context = LocalContext.current
                val captureMethod = SyncSettings.captureMethod(context)
                val isEnhanced = when (captureMethod) {
                    CaptureMethod.AUTO -> isXposedActivated || isShizukuActive
                    CaptureMethod.LSPOSED -> isXposedActivated
                    CaptureMethod.SHIZUKU -> isShizukuActive
                }
                val badgeLabel = when (captureMethod) {
                    CaptureMethod.AUTO -> when {
                        isXposedActivated -> "模块已激活"
                        isShizukuActive -> "Shizuku 就绪"
                        else -> "未授权"
                    }
                    CaptureMethod.LSPOSED -> if (isXposedActivated) "模块已激活" else "模块未激活"
                    CaptureMethod.SHIZUKU -> if (isShizukuActive) "Shizuku 就绪" else "未授权"
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { onNavigateToSettings() }
                        .padding(horizontal = 4.dp, vertical = 3.dp)
                ) {
                    Box(
                        modifier = Modifier
                            .size(7.dp)
                            .clip(CircleShape)
                            .background(if (isEnhanced) Color(0xFF34C759) else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f))
                    )
                    Spacer(Modifier.width(5.dp))
                    Text(
                        text = badgeLabel,
                        fontSize = 12.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }

                // 3. 在线设备数
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.padding(horizontal = 4.dp, vertical = 3.dp)
                ) {
                    Icon(
                        imageVector = LucideIcons.Laptop,
                        contentDescription = null,
                        tint = if (onlineDevicesCount > 0) Color(0xFF34C759) else MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier.size(12.dp)
                    )
                    Spacer(Modifier.width(4.dp))
                    Text(
                        text = if (totalDevicesCount > 0) "$onlineDevicesCount/$totalDevicesCount 台在线"
                        else if (isServerConnected) "仅本机"
                        else "离线",
                        fontSize = 12.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            }
        }
    }
}

/** 快捷功能入口卡片行 (即时互传 & 扫码配对) */
@Composable
private fun QuickActionsRow(
    onlineDevicesCount: Int,
    onOpenManualPush: () -> Unit,
    onOpenQrScanner: () -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        // 左卡片: 跨设备即时互传
        Card(
            modifier = Modifier
                .weight(1f)
                .clickable(onClick = onOpenManualPush),
            insideMargin = PaddingValues(horizontal = 12.dp, vertical = 12.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Box(
                    modifier = Modifier
                        .size(34.dp)
                        .clip(CircleShape)
                        .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = LucideIcons.Send,
                        contentDescription = "即时互传",
                        tint = MiuixTheme.colorScheme.primary,
                        modifier = Modifier.size(16.dp)
                    )
                }
                Spacer(Modifier.width(8.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "即时互传",
                        fontSize = 14.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = MiuixTheme.colorScheme.onSurface,
                        maxLines = 1
                    )
                    Spacer(Modifier.height(1.dp))
                    Text(
                        text = if (onlineDevicesCount > 0) "$onlineDevicesCount 台在线" else "聊天流快传",
                        fontSize = 11.sp,
                        color = if (onlineDevicesCount > 0) Color(0xFF34C759) else MiuixTheme.colorScheme.onBackgroundVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Icon(
                    imageVector = MiuixIcons.Normal.ChevronForward,
                    contentDescription = null,
                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f),
                    modifier = Modifier.size(13.dp)
                )
            }
        }

        // 右卡片: 扫码配对
        Card(
            modifier = Modifier
                .weight(1f)
                .clickable(onClick = onOpenQrScanner),
            insideMargin = PaddingValues(horizontal = 12.dp, vertical = 12.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Box(
                    modifier = Modifier
                        .size(34.dp)
                        .clip(CircleShape)
                        .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = LucideIcons.ScanLine,
                        contentDescription = "开始配对",
                        tint = MiuixTheme.colorScheme.primary,
                        modifier = Modifier.size(16.dp)
                    )
                }
                Spacer(Modifier.width(8.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = "开始配对",
                        fontSize = 14.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = MiuixTheme.colorScheme.onSurface,
                        maxLines = 1
                    )
                    Spacer(Modifier.height(1.dp))
                    Text(
                        text = "扫码快速接入",
                        fontSize = 11.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Icon(
                    imageVector = MiuixIcons.Normal.ChevronForward,
                    contentDescription = null,
                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f),
                    modifier = Modifier.size(13.dp)
                )
            }
        }
    }
}

/** 最近同步记录预览卡片 */
@Composable
private fun RecentRecordsCard(
    records: List<clip.yixing.sync.service.CapturedClip>,
    onCopy: (clip.yixing.sync.service.CapturedClip) -> Unit,
    onPreviewImage: (clip.yixing.sync.service.CapturedClip) -> Unit,
    onViewAll: () -> Unit
) {
    val context = LocalContext.current
    SectionBlock(
        title = "最近记录",
        trailing = {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .clickable(onClick = onViewAll)
                    .padding(horizontal = 4.dp, vertical = 2.dp)
            ) {
                Text(
                    text = "查看全部",
                    color = MiuixTheme.colorScheme.primary
                )
                Icon(
                    imageVector = MiuixIcons.Normal.ChevronForward,
                    contentDescription = "查看全部",
                    tint = MiuixTheme.colorScheme.primary,
                    modifier = Modifier.size(16.dp)
                )
            }
        },
    ) {
        if (records.isEmpty()) {
            Text(
                text = "暂无剪贴板记录。复制文字或开启持续监听后将自动记录并同步。",
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        } else {
            records.forEachIndexed { index, clip ->
                if (index > 0) {
                    Spacer(Modifier.height(8.dp))
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(
                        modifier = Modifier
                            .weight(1f)
                            .clickable {
                                if (clip.isImage) onPreviewImage(clip) else onCopy(clip)
                            }
                    ) {
                        if (clip.isImage) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(
                                    text = "[图片]",
                                    color = MiuixTheme.colorScheme.primary,
                                    fontSize = 13.sp,
                                    fontWeight = FontWeight.Medium
                                )
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    text = "点击预览",
                                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                                    fontSize = 11.sp
                                )
                            }
                        } else {
                            Text(
                                text = clip.text,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                        val actions = remember(clip.text, clip.isImage) {
                            if (!clip.isImage) SmartActionEngine.detectActions(context, clip.text) else emptyList()
                        }
                        if (actions.isNotEmpty()) {
                            Spacer(Modifier.height(4.dp))
                            Row(
                                horizontalArrangement = Arrangement.spacedBy(4.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .horizontalScroll(rememberScrollState())
                            ) {
                                actions.forEach { action ->
                                    SmartActionChip(
                                        action = action,
                                        onClick = { action.action(context) }
                                    )
                                }
                            }
                        }
                        Spacer(Modifier.height(2.dp))
                        Text(
                            text = formatTime(clip.time),
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            fontSize = 11.sp
                        )
                    }
                    IconButton(onClick = { onCopy(clip) }) {
                        Icon(
                            imageVector = LucideIcons.Copy,
                            contentDescription = "复制",
                            tint = MiuixTheme.colorScheme.primary,
                            modifier = Modifier.size(16.dp)
                        )
                    }
                }
                if (index < records.size - 1) {
                    Spacer(Modifier.height(6.dp))
                    HorizontalDivider(color = MiuixTheme.colorScheme.dividerLine, thickness = Dp.Hairline)
                }
            }
        }
    }
}

private fun copyToClipboard(context: Context, text: String) {
    clip.yixing.sync.service.ClipboardMonitorService.copyToClipboardInternal(context, ClipData.newPlainText("NexClip", text))
}

private fun clearClipboard(context: Context) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
        cm.clearPrimaryClip()
    } else {
        cm.setPrimaryClip(ClipData.newPlainText("", ""))
    }
}

private fun requestNotificationPermissionIfNeeded(
    context: Context,
    launcher: androidx.activity.compose.ManagedActivityResultLauncher<String, Boolean>
) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
        context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
    ) {
        launcher.launch(Manifest.permission.POST_NOTIFICATIONS)
    }
}
