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
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.StatusRow
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.formatTime
import clip.yixing.sync.hook.ModuleStatusStore
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.ClipboardTest
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
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
    onOverlayActiveChanged: (Boolean) -> Unit = {},
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val serviceRunning by ClipboardMonitorService.isRunning.collectAsState()
    val isServerConnected by ClipboardMonitorService.isServerConnected.collectAsState()
    val serverConnectionState by ClipboardMonitorService.serverConnectionState.collectAsState()
    val captured by ClipboardMonitorService.captured.collectAsState()

    val currentText = remember(captured) {
        captured.firstOrNull()?.text ?: ClipboardTest.readClipboard(context) ?: ""
    }

    var isManualPushing by remember { mutableStateOf(false) }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

    val serverUrl = remember(isServerConnected) { SyncSettings.serverUrl(context) }
    var onlineDevicesCount by remember { mutableIntStateOf(0) }
    var totalDevicesCount by remember { mutableIntStateOf(0) }

    // 在线设备统计更新
    LaunchedEffect(isServerConnected) {
        if (isServerConnected && serverUrl.isNotBlank()) {
            try {
                val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                val list = withContext(Dispatchers.IO) { api.getDevices() }
                onlineDevicesCount = list.count { it.online }
                totalDevicesCount = list.size
            } catch (_: Exception) {
            }
        } else {
            onlineDevicesCount = 0
            totalDevicesCount = 0
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
        // 1. 同步状态看板
        item {
            SyncOverviewCard(
                serverUrl = serverUrl,
                isServerConnected = isServerConnected,
                serverConnectionState = serverConnectionState,
                serviceRunning = serviceRunning,
                onlineDevicesCount = onlineDevicesCount,
                totalDevicesCount = totalDevicesCount,
                onNavigateToSettings = onNavigateToSettings
            )
        }

        // 2. 模块激活状态卡片
        item {
            ModuleStatusCard()
        }

        // 3. 当前剪贴板卡片及快捷操作
        item {
            SectionBlock(
                title = "当前剪贴板",
                trailing = {
                    if (currentText.isNotBlank()) {
                        IconButton(
                            onClick = {
                                copyToClipboard(context, currentText)
                                scope.launch {
                                    snackbarHostState?.showAppSnack("已复制到剪贴板", SnackType.Success)
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
                    }
                },
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer.copy(alpha = 0.5f))
                        .padding(10.dp)
                ) {
                    Text(
                        text = if (currentText.isNotBlank()) currentText else "(剪贴板为空)",
                        color = if (currentText.isNotBlank()) MiuixTheme.colorScheme.onSurface
                        else MiuixTheme.colorScheme.onBackgroundVariant,
                        maxLines = 5,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Spacer(Modifier.height(10.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Button(
                        onClick = {
                            if (currentText.isBlank()) {
                                scope.launch {
                                    snackbarHostState?.showAppSnack("当前剪贴板为空，无法推送", SnackType.Info)
                                }
                                return@Button
                            }
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
                                        api.putText(
                                            text = currentText,
                                            deviceId = SyncSettings.ensureDeviceId(context),
                                            deviceName = SyncSettings.deviceName(context)
                                        )
                                    }
                                    snackbarHostState?.showAppSnack("已推送至所有设备", SnackType.Success)
                                } catch (e: Exception) {
                                    snackbarHostState?.showAppSnack(e.message ?: "推送失败", SnackType.Error)
                                } finally {
                                    isManualPushing = false
                                }
                            }
                        },
                        enabled = !isManualPushing && currentText.isNotBlank(),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Icon(imageVector = MiuixIcons.Normal.UploadCloud, contentDescription = "立即推送")
                        Spacer(Modifier.width(6.dp))
                        Text(if (isManualPushing) "推送中…" else "立即推送当前剪贴板")
                    }
                }
            }
        }

        // 4. 持续监听剪贴板开关卡片
        item {
            SectionBlock(title = "持续监听") {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            if (serviceRunning) {
                                when (serverConnectionState) {
                                    ClipboardMonitorService.ServerConnectionState.CONNECTED -> "运行中 · 服务器已连接"
                                    ClipboardMonitorService.ServerConnectionState.CONNECTING -> "运行中 · 正在连接服务器"
                                    ClipboardMonitorService.ServerConnectionState.DISCONNECTED -> "运行中 · 服务器未连接"
                                }
                            } else "未运行",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                    Switch(
                        checked = serviceRunning,
                        onCheckedChange = { checked ->
                            if (checked) {
                                requestNotificationPermissionIfNeeded(context, permissionLauncher)
                                ClipboardMonitorService.start(context)
                            } else {
                                ClipboardMonitorService.stop(context)
                            }
                        }
                    )
                }
            }
        }

        // 5. 最近同步记录预览卡片
        item {
            RecentRecordsCard(
                records = captured.take(3),
                onCopy = { text ->
                    copyToClipboard(context, text)
                    scope.launch {
                        snackbarHostState?.showAppSnack("已复制该条记录", SnackType.Success)
                    }
                },
                onViewAll = onNavigateToRecords
            )
        }

        item {
            Spacer(Modifier.height(bottomInnerPadding))
        }
    }
}

/** 同步状态与设备概览看板 */
@Composable
private fun SyncOverviewCard(
    serverUrl: String,
    isServerConnected: Boolean,
    serverConnectionState: ClipboardMonitorService.ServerConnectionState,
    serviceRunning: Boolean,
    onlineDevicesCount: Int,
    totalDevicesCount: Int,
    onNavigateToSettings: () -> Unit
) {
    SectionBlock(
        title = "同步状态",
        trailing = {
            if (serverUrl.isNotBlank()) {
                Text(
                    text = "设置",
                    color = MiuixTheme.colorScheme.primary,
                    modifier = Modifier
                        .clickable(onClick = onNavigateToSettings)
                        .padding(horizontal = 4.dp, vertical = 2.dp)
                )
            }
        },
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(10.dp)
                    .background(
                        color = when {
                            isServerConnected -> Color(0xFF34C759)
                            serverConnectionState == ClipboardMonitorService.ServerConnectionState.CONNECTING -> Color(0xFFFF9500)
                            else -> Color(0xFFFF3B30)
                        },
                        shape = CircleShape
                    )
            )
            Spacer(Modifier.width(8.dp))
            Text(
                text = if (serverUrl.isBlank()) "未配置服务器"
                else if (isServerConnected) "服务器连接正常"
                else if (serverConnectionState == ClipboardMonitorService.ServerConnectionState.CONNECTING) "连接中…"
                else if (serviceRunning) "服务器连接失败"
                else "服务未开启",
                style = MiuixTheme.textStyles.title3,
                modifier = Modifier.weight(1f)
            )
        }
        if (serverUrl.isNotBlank()) {
            Spacer(Modifier.height(4.dp))
            Text(
                text = serverUrl,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                fontSize = 12.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        Spacer(Modifier.height(10.dp))
        HorizontalDivider(color = MiuixTheme.colorScheme.dividerLine, thickness = Dp.Hairline)
        Spacer(Modifier.height(8.dp))
        StatusRow(
            label = "在线设备",
            value = if (totalDevicesCount > 0) "$onlineDevicesCount / $totalDevicesCount 台在线"
            else if (isServerConnected) "暂无其他设备"
            else "离线",
            valueColor = if (onlineDevicesCount > 0) Color(0xFF34C759)
            else MiuixTheme.colorScheme.onBackgroundVariant
        )
        Spacer(Modifier.height(6.dp))
        StatusRow(
            label = "实时推送通道",
            value = when {
                isServerConnected -> "SignalR 已就绪"
                serverConnectionState == ClipboardMonitorService.ServerConnectionState.CONNECTING -> "正在连接"
                serviceRunning -> "连接失败"
                else -> "未连接"
            },
            valueColor = when {
                isServerConnected -> Color(0xFF34C759)
                serverConnectionState == ClipboardMonitorService.ServerConnectionState.CONNECTING -> Color(0xFFFF9500)
                else -> MiuixTheme.colorScheme.onBackgroundVariant
            }
        )
    }
}

/** 最近同步记录预览卡片 */
@Composable
private fun RecentRecordsCard(
    records: List<clip.yixing.sync.service.CapturedClip>,
    onCopy: (String) -> Unit,
    onViewAll: () -> Unit
) {
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
                text = "暂无捕获记录。复制文字或开启持续监听后将自动记录并同步。",
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
                            .clickable { onCopy(clip.text) }
                    ) {
                        Text(
                            text = clip.text,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis
                        )
                        Spacer(Modifier.height(2.dp))
                        Text(
                            text = formatTime(clip.time),
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            fontSize = 11.sp
                        )
                    }
                    IconButton(onClick = { onCopy(clip.text) }) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Copy,
                            contentDescription = "复制",
                            tint = MiuixTheme.colorScheme.onBackgroundVariant
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

@Composable
internal fun ModuleStatusCard() {
    val status by ModuleStatusStore.moduleStatus.collectAsState()
    SectionBlock(title = "模块状态") {
        StatusRow(
            label = "状态",
            value = if (status.activated) "已激活" else "未激活",
            valueColor = if (status.activated) {
                MiuixTheme.colorScheme.primary
            } else {
                MiuixTheme.colorScheme.onBackgroundVariant
            }
        )
        if (status.activated) {
            Spacer(Modifier.height(6.dp))
            StatusRow(
                label = "框架版本",
                value = buildString {
                    append(status.frameworkName ?: "未知框架")
                    status.frameworkVersion?.let { append(" v$it") }
                    status.frameworkVersionCode?.let { append(" ($it)") }
                }
            )
            Spacer(Modifier.height(6.dp))
            StatusRow(
                label = "Xposed API",
                value = status.apiVersion?.toString() ?: "未知"
            )
        } else {
            Spacer(Modifier.height(8.dp))
            Text(
                text = "请依次确认:\n1. LSPosed 中已启用「NexClip」\n2. 作用域同时勾选「系统框架」和「NexClip」应用本身\n3. 重启手机后,模块才会注入本应用进程,此处才能实时显示激活状态",
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
    }
}

private fun copyToClipboard(context: Context, text: String) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    cm.setPrimaryClip(ClipData.newPlainText("SyncClipboard", text))
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
