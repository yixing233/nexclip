package clip.yixing.sync.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.BackEventCompat
import androidx.activity.compose.BackHandler
import androidx.activity.compose.PredictiveBackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearOutSlowInEasing
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import kotlinx.coroutines.CancellationException
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import clip.yixing.sync.SnackType
import clip.yixing.sync.StatusRow
import clip.yixing.sync.data.ApiException
import clip.yixing.sync.data.DeviceInfo
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.data.PairingCode
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.isActive
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayBottomSheet
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.ChevronForward
import top.yukonga.miuix.kmp.icon.extended.Copy
import top.yukonga.miuix.kmp.icon.extended.Delete
import top.yukonga.miuix.kmp.icon.extended.Refresh
import top.yukonga.miuix.kmp.icon.extended.Settings
import top.yukonga.miuix.kmp.icon.extended.UploadCloud
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 设置二级页面枚举
 */
enum class SettingsSubPage(val title: String, val subtitle: String) {
    Basic("基础设置", "设备名称、开机自启、悬浮底栏、记录上限"),
    Sync("同步设置", "服务器配置、设备配对、在线设备列表"),
    Filter("过滤规则", "内容过滤黑名单、敏感内容保护"),
    Data("数据管理", "备份导出与导入、应用缓存清理"),
    Permission("权限管理", "通知权限、电池优化白名单、自启动"),
    About("关于", "版本信息、项目仓库与开源致谢")
}

@Composable
internal fun SettingsPage(
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    floatingBarEnabled: Boolean,
    onFloatingBarChange: (Boolean) -> Unit,
    onOverlayActiveChanged: (Boolean) -> Unit = {},
    onOpenQrScanner: () -> Unit = {},
) {
    val context = LocalContext.current
    val prefs = remember { SyncSettings.prefs(context) }
    val scope = rememberCoroutineScope()

    // 当前所处二级子页面（null 为一级设置主页）
    var currentSubPage by remember { mutableStateOf<SettingsSubPage?>(null) }
    var displayedSubPage by remember { mutableStateOf<SettingsSubPage?>(null) }
    val subPageAnimProgress = remember { Animatable(1f) } // 0f: 完全展开展示, 1f: 退出到屏幕右侧

    fun openSubPage(page: SettingsSubPage) {
        displayedSubPage = page
        currentSubPage = page
        scope.launch {
            subPageAnimProgress.snapTo(1f)
            subPageAnimProgress.animateTo(0f, animationSpec = tween(280, easing = FastOutSlowInEasing))
        }
    }

    fun closeSubPage() {
        scope.launch {
            subPageAnimProgress.animateTo(1f, animationSpec = tween(240, easing = FastOutSlowInEasing))
            currentSubPage = null
            displayedSubPage = null
        }
    }

    var predictiveBackEnabled by remember { mutableStateOf(SyncSettings.predictiveBackEnabled(context)) }
    var notificationEnabled by remember { mutableStateOf(SyncSettings.notificationEnabled(context)) }

    // ---- 1. 基础设置与设备状态 ----
    var deviceName by remember { mutableStateOf(SyncSettings.deviceName(context)) }
    var selfDeviceId by remember { mutableStateOf(SyncSettings.ensureDeviceId(context)) }
    var showNameDialog by remember { mutableStateOf(false) }
    val nameDialogState = remember { TextFieldState(deviceName) }
    var showResetIdDialog by remember { mutableStateOf(false) }

    var bootStart by remember { mutableStateOf(SyncSettings.bootStartEnabled(context)) }
    var notificationStyle by remember { mutableStateOf(SyncSettings.notificationStyle(context)) }
    var showNotificationStyleDialog by remember { mutableStateOf(false) }
    val historyOptions = SyncSettings.MAX_HISTORY_OPTIONS.toList()
    val historyLabels = historyOptions.map { "$it 条" }
    var historyIndex by remember {
        mutableStateOf(
            historyOptions.indexOf(SyncSettings.maxHistory(context)).coerceAtLeast(0)
        )
    }

    // ---- 2. 同步设置状态 (6 位纯数字单向即入) ----
    val urlState = remember { TextFieldState(SyncSettings.serverUrl(context)) }
    var testing by remember { mutableStateOf(false) }
    var pairing by remember { mutableStateOf(false) }

    var showPairDialog by remember { mutableStateOf(false) }
    val dialogCodeState = remember { TextFieldState("") }

    var generatingCode by remember { mutableStateOf(false) }
    var generatedCode by remember { mutableStateOf<PairingCode?>(null) }
    var showCodeSheet by remember { mutableStateOf(false) }
    var deleteTargetDevice by remember { mutableStateOf<DeviceInfo?>(null) }

    // 是否有任何弹窗、Bottom Sheet 或选择器处于打开状态
    val isAnyOverlayOpen = showNotificationStyleDialog ||
        showNameDialog ||
        showResetIdDialog ||
        showPairDialog ||
        showCodeSheet ||
        deleteTargetDevice != null

    // 1. 弹层开启时优先拦截返回键/手势，仅关闭当前弹窗
    PredictiveBackHandler(enabled = isAnyOverlayOpen) { progress ->
        try {
            progress.collect { }
        } finally {
            if (showNotificationStyleDialog) {
                showNotificationStyleDialog = false
            } else if (showNameDialog) {
                showNameDialog = false
            } else if (showResetIdDialog) {
                showResetIdDialog = false
            } else if (showPairDialog) {
                showPairDialog = false
            } else if (showCodeSheet) {
                showCodeSheet = false
            } else if (deleteTargetDevice != null) {
                deleteTargetDevice = null
            }
        }
    }

    // 2. 预测返回手势监听（无缝连贯跟手，二级页面退出）
    PredictiveBackHandler(enabled = currentSubPage != null && !isAnyOverlayOpen) { progress ->
        if (!predictiveBackEnabled) {
            closeSubPage()
            return@PredictiveBackHandler
        }
        try {
            progress.collect { event ->
                val p = FastOutSlowInEasing.transform(event.progress)
                subPageAnimProgress.snapTo(p)
            }
            // 手势正常完成（松手）：从当前手势位移继续平滑滑动退出
            subPageAnimProgress.animateTo(1f, animationSpec = tween(200, easing = LinearOutSlowInEasing))
            currentSubPage = null
            displayedSubPage = null
        } catch (e: CancellationException) {
            // 用户取消手势（滑回边缘）：从当前位置平滑弹回复原
            subPageAnimProgress.animateTo(0f, animationSpec = spring(stiffness = Spring.StiffnessMediumLow))
        }
    }

    var devices by remember { mutableStateOf<List<DeviceInfo>>(emptyList()) }
    var devicesLoading by remember { mutableStateOf(true) }
    var devicesError by remember { mutableStateOf<String?>(null) }
    var devicesReload by remember { mutableIntStateOf(0) }
    var devicesManual by remember { mutableStateOf(false) }
    val isServerConnected by ClipboardMonitorService.isServerConnected.collectAsState()
    var autoRefreshUntil by remember { mutableLongStateOf(0L) }

    // ① 设备列表基础加载
    LaunchedEffect(devicesReload) {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isBlank() || !SyncSettings.isPaired(context) || SyncSettings.deviceToken(context).isBlank()) {
            devices = emptyList()
            devicesLoading = false
            return@LaunchedEffect
        }
        devicesLoading = devices.isEmpty()
        devicesError = null
        try {
            val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
            devices = withContext(Dispatchers.IO) { api.getDevices() }
        } catch (e: Exception) {
            devicesError = e.message ?: "加载失败"
            if (e is ApiException && (e.statusCode == 401 || e.statusCode == 403 || e.statusCode == 410)) {
                SyncSettings.clearPairing(context)
                devices = emptyList()
                if (ClipboardMonitorService.isRunning.value) {
                    ClipboardMonitorService.stop(context)
                }
            }
            if (devicesManual || devices.isEmpty()) {
                snackbarHostState.showAppSnack(devicesError ?: "设备列表加载失败", SnackType.Error)
            }
        } finally {
            devicesManual = false
            devicesLoading = false
        }
    }

    // ② 配对期间高频自动刷新设备列表
    LaunchedEffect(showCodeSheet, autoRefreshUntil) {
        val isCodeSheetActive = showCodeSheet && generatedCode != null
        val isPostPairActive = System.currentTimeMillis() < autoRefreshUntil
        if (isCodeSheetActive || isPostPairActive) {
            while (true) {
                delay(3000)
                val codeActive = showCodeSheet && generatedCode != null
                val postActive = System.currentTimeMillis() < autoRefreshUntil
                if (!codeActive && !postActive) break

                val serverUrl = SyncSettings.serverUrl(context)
                if (serverUrl.isNotBlank() && SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
                    try {
                        val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                        val list = withContext(Dispatchers.IO) { api.getDevices() }
                        devices = list
                    } catch (e: Exception) {
                        if (e is ApiException && (e.statusCode == 401 || e.statusCode == 403 || e.statusCode == 410)) {
                            SyncSettings.clearPairing(context)
                            devices = emptyList()
                            break
                        }
                    }
                }
            }
        }
    }

    // ③ 服务器已连接后低频更新设备列表
    LaunchedEffect(isServerConnected) {
        if (isServerConnected) {
            while (true) {
                delay(20_000)
                val codeActive = showCodeSheet && generatedCode != null
                val postActive = System.currentTimeMillis() < autoRefreshUntil
                if (codeActive || postActive) continue

                val serverUrl = SyncSettings.serverUrl(context)
                if (serverUrl.isNotBlank() && SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
                    try {
                        val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                        val list = withContext(Dispatchers.IO) { api.getDevices() }
                        devices = list
                    } catch (e: Exception) {
                        if (e is ApiException && (e.statusCode == 401 || e.statusCode == 403 || e.statusCode == 410)) {
                            SyncSettings.clearPairing(context)
                            devices = emptyList()
                            break
                        }
                    }
                }
            }
        }
    }

    // ---- 3. 过滤规则状态 ----
    var filterKeywords by remember { mutableStateOf(SyncSettings.filterKeywords(context)) }
    var ignoreSensitive by remember { mutableStateOf(SyncSettings.ignoreSensitive(context)) }
    val addKeywordState = remember { TextFieldState("") }

    // ---- 4. 缓存与数据管理 ----
    var cacheSizeText by remember { mutableStateOf("计算中…") }
    fun refreshCacheSize() {
        scope.launch(Dispatchers.IO) {
            val cacheSize = getFolderSize(context.cacheDir) + getFolderSize(context.codeCacheDir)
            val text = formatSize(cacheSize)
            withContext(Dispatchers.Main) {
                cacheSizeText = text
            }
        }
    }
    LaunchedEffect(Unit) {
        refreshCacheSize()
    }

    val exportLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.CreateDocument("application/json")
    ) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                try {
                    val json = withContext(Dispatchers.IO) {
                        ClipboardMonitorService.exportBackup(context)
                    }
                    withContext(Dispatchers.IO) {
                        context.contentResolver.openOutputStream(uri)?.use { os ->
                            os.write(json.toByteArray(Charsets.UTF_8))
                            os.flush()
                        }
                    }
                    snackbarHostState.showAppSnack("备份已成功导出", SnackType.Success)
                } catch (e: Exception) {
                    snackbarHostState.showAppSnack("导出失败: ${e.message}", SnackType.Error)
                }
            }
        }
    }

    val importLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                try {
                    val content = withContext(Dispatchers.IO) {
                        context.contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }
                    }
                    if (content.isNullOrBlank()) {
                        snackbarHostState.showAppSnack("读取备份文件为空", SnackType.Error)
                        return@launch
                    }
                    val count = withContext(Dispatchers.IO) {
                        ClipboardMonitorService.importBackup(context, content)
                    }
                    snackbarHostState.showAppSnack("成功导入 $count 条记录", SnackType.Success)
                } catch (e: Exception) {
                    snackbarHostState.showAppSnack("导入失败: ${e.message}", SnackType.Error)
                }
            }
        }
    }

    // ---- 5. 权限管理状态 ----
    var isNotificationGranted by remember {
        mutableStateOf(
            if (Build.VERSION.SDK_INT >= 33) {
                ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
            } else {
                true
            }
        )
    }
    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { isGranted ->
        isNotificationGranted = isGranted
        if (isGranted) {
            scope.launch { snackbarHostState.showAppSnack("已授予通知权限", SnackType.Success) }
        } else {
            scope.launch { snackbarHostState.showAppSnack("未授予通知权限", SnackType.Info) }
        }
    }

    val powerManager = remember(context) { context.getSystemService(Context.POWER_SERVICE) as PowerManager }
    var isBatteryOptIgnored by remember {
        mutableStateOf(
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                powerManager.isIgnoringBatteryOptimizations(context.packageName)
            } else {
                true
            }
        )
    }

    // 弹层或二级页面开启时通知底栏收起避让
    LaunchedEffect(currentSubPage, isAnyOverlayOpen) {
        onOverlayActiveChanged(currentSubPage != null || isAnyOverlayOpen)
    }

    Box(modifier = Modifier.fillMaxSize()) {
        // ---- 1. 底层：一级设置主页 ----
        val baseProgress = subPageAnimProgress.value // 0f: 二级页打开时底层下沉, 1f: 二级页关闭时底层复原
        Box(
            modifier = Modifier
                .fillMaxSize()
                .graphicsLayer {
                    if (displayedSubPage != null) {
                        translationX = -(1f - baseProgress) * size.width * 0.15f
                        val s = 0.94f + 0.06f * baseProgress
                        scaleX = s
                        scaleY = s
                        alpha = 0.82f + 0.18f * baseProgress
                    }
                }
        ) {
            PageShell(
                title = "设置",
                bottomInnerPadding = bottomInnerPadding
            ) { scrollBehavior, topPadding ->
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
                    // 5 大二级设置入口卡片（无分隔线）
                    item {
                        SectionBlock(title = "设置分类", insideMargin = PaddingValues()) {
                            SettingsNavRow(
                                title = "基础设置",
                                summary = "设备名称、开机自启、悬浮底栏、记录上限",
                                onClick = { openSubPage(SettingsSubPage.Basic) }
                            )
                            SettingsNavRow(
                                title = "同步设置",
                                summary = "服务器配置、设备配对、在线设备列表",
                                onClick = { openSubPage(SettingsSubPage.Sync) }
                            )
                            SettingsNavRow(
                                title = "过滤规则",
                                summary = "内容过滤黑名单、敏感内容保护",
                                onClick = { openSubPage(SettingsSubPage.Filter) }
                            )
                            SettingsNavRow(
                                title = "数据管理",
                                summary = "备份导出与导入、应用缓存清理",
                                onClick = { openSubPage(SettingsSubPage.Data) }
                            )
                            SettingsNavRow(
                                title = "权限管理",
                                summary = "通知权限、电池优化白名单、自启动",
                                onClick = { openSubPage(SettingsSubPage.Permission) }
                            )
                        }
                    }

                    // 关于（独立二级页面入口）
                    item {
                        SectionBlock(title = "关于", insideMargin = PaddingValues()) {
                            SettingsNavRow(
                                title = "关于 NexClip",
                                summary = "版本 v${appVersion(context)} · 开源仓库与项目致谢",
                                onClick = { openSubPage(SettingsSubPage.About) }
                            )
                        }
                    }

                    item {
                        Spacer(Modifier.height(bottomInnerPadding))
                    }
                }
            }

            // 黑色半透明遮罩层（视差下沉感）
            if (displayedSubPage != null && (1f - baseProgress) > 0.001f) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color.Black.copy(alpha = (1f - baseProgress) * 0.2f))
                )
            }
        }

        // ---- 2. 顶层：当前激活的二级子页面 ----
        displayedSubPage?.let { subPage ->
            val p = subPageAnimProgress.value // 0f (显示) -> 1f (退出到右侧)
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer {
                        translationX = p * size.width
                        val s = 1f - p * 0.05f
                        scaleX = s
                        scaleY = s
                        transformOrigin = TransformOrigin(0f, 0.5f)
                        clip = true
                        shape = RoundedCornerShape((p * 24).dp)
                        shadowElevation = (1f - p) * 24f
                    }
            ) {
                when (subPage) {
                    SettingsSubPage.Basic -> {
                        // ---- 二级页面 1: 基础设置 ----
                        PageShell(
                            title = "基础设置",
                            bottomInnerPadding = bottomInnerPadding,
                            navigationIcon = {
                                IconButton(onClick = { closeSubPage() }) {
                                    Icon(
                                        imageVector = MiuixIcons.Normal.Back,
                                        contentDescription = "返回"
                                    )
                                }
                            }
                        ) { scrollBehavior, topPadding ->
                            LazyColumn(
                        modifier = Modifier
                            .fillMaxSize()
                            .overScrollVertical()
                            .nestedScroll(scrollBehavior.nestedScrollConnection),
                        contentPadding = PaddingValues(
                            start = 16.dp,
                            end = 16.dp,
                            top = topPadding + 8.dp,
                            bottom = bottomInnerPadding + 16.dp
                        ),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                    item {
                        SectionBlock(title = "设备信息", insideMargin = PaddingValues()) {
                            // 修改设备名称
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        nameDialogState.edit { replace(0, length, deviceName) }
                                        showNameDialog = true
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "设备名称",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = deviceName,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                                        fontSize = 13.sp
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "修改",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }

                            // 设备标识 Device ID
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "设备标识 (Device ID)",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = selfDeviceId,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                                        fontSize = 13.sp,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis
                                    )
                                }
                                IconButton(
                                    onClick = {
                                        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                        cm.setPrimaryClip(ClipData.newPlainText("Device ID", selfDeviceId))
                                        scope.launch { snackbarHostState.showAppSnack("已复制 Device ID", SnackType.Success) }
                                    }
                                ) {
                                    Icon(imageVector = MiuixIcons.Normal.Copy, contentDescription = "复制")
                                }
                                IconButton(
                                    onClick = { showResetIdDialog = true }
                                ) {
                                    Icon(imageVector = MiuixIcons.Normal.Refresh, contentDescription = "重置")
                                }
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "界面与交互", insideMargin = PaddingValues()) {
                            SwitchPreference(
                                checked = predictiveBackEnabled,
                                onCheckedChange = { checked ->
                                    predictiveBackEnabled = checked
                                    SyncSettings.setPredictiveBackEnabled(context, checked)
                                },
                                title = "预测返回手势",
                                summary = "边缘侧滑返回时支持实时跟手视差与缩放预览"
                            )
                            SwitchPreference(
                                checked = floatingBarEnabled,
                                onCheckedChange = onFloatingBarChange,
                                title = "悬浮底栏",
                                summary = "使用液态玻璃悬浮导航栏"
                            )
                            SwitchPreference(
                                checked = bootStart,
                                onCheckedChange = { checked ->
                                    bootStart = checked
                                    prefs.edit()
                                        .putBoolean(SyncSettings.KEY_BOOT_START_ENABLED, checked)
                                        .apply()
                                },
                                title = "开机自启",
                                summary = "开机后自动恢复剪贴板监听"
                            )
                        }
                    }

                    item {
                        SectionBlock(title = "通知与记录", insideMargin = PaddingValues()) {
                            SwitchPreference(
                                checked = notificationEnabled,
                                onCheckedChange = { checked ->
                                    notificationEnabled = checked
                                    SyncSettings.setNotificationEnabled(context, checked)
                                    ClipboardMonitorService.updateNotification(context)
                                },
                                title = "同步与捕获通知",
                                summary = "在收到新同步内容或本地复制时展示系统通知"
                            )
                            if (notificationEnabled) {
                                // 通知展示样式 (普通通知 / 实时通知 / HyperOS 超级岛)
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable { showNotificationStyleDialog = true }
                                        .padding(horizontal = 16.dp, vertical = 12.dp),
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(
                                            text = "通知展示样式",
                                            fontSize = 16.sp,
                                            fontWeight = FontWeight.Medium
                                        )
                                        Spacer(Modifier.height(2.dp))
                                        Text(
                                            text = notificationStyle.label + " · " + notificationStyle.summary,
                                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                                            fontSize = 13.sp
                                        )
                                    }
                                    Icon(
                                        imageVector = MiuixIcons.Normal.ChevronForward,
                                        contentDescription = "选择样式",
                                        tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                    )
                                }
                            }
                            OverlayDropdownPreference(
                                items = historyLabels,
                                selectedIndex = historyIndex,
                                onSelectedIndexChange = { index ->
                                    historyIndex = index
                                    prefs.edit()
                                        .putInt(SyncSettings.KEY_MAX_HISTORY, historyOptions[index])
                                        .apply()
                                },
                                title = "记录上限",
                                summary = "本地最多保留 ${historyOptions[historyIndex]} 条捕获记录"
                            )
                        }
                    }
                }
            }
        }

        SettingsSubPage.Sync -> {
                // ---- 二级页面 2: 同步设置 ----
                PageShell(
                    title = "同步设置",
                    bottomInnerPadding = bottomInnerPadding,
                    navigationIcon = {
                        IconButton(onClick = { closeSubPage() }) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Back,
                                contentDescription = "返回"
                            )
                        }
                    }
                ) { scrollBehavior, topPadding ->
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxSize()
                            .overScrollVertical()
                            .nestedScroll(scrollBehavior.nestedScrollConnection),
                        contentPadding = PaddingValues(
                            start = 16.dp,
                            end = 16.dp,
                            top = topPadding + 8.dp,
                            bottom = bottomInnerPadding + 16.dp
                        ),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                    item {
                        SectionBlock(title = "服务器设置") {
                            TextField(
                                state = urlState,
                                label = "服务器地址",
                                useLabelAsPlaceholder = true,
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                                modifier = Modifier.fillMaxWidth()
                            )
                            Spacer(Modifier.height(8.dp))
                            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                Button(
                                    onClick = {
                                        val newUrl = urlState.text.toString().trim()
                                        prefs.edit()
                                            .putString(SyncSettings.KEY_SERVER_URL, newUrl)
                                            .apply()
                                        scope.launch { snackbarHostState.showAppSnack("已保存", SnackType.Success) }
                                        devicesReload++
                                        if (ClipboardMonitorService.isRunning.value) {
                                            ClipboardMonitorService.stop(context)
                                            ClipboardMonitorService.start(context)
                                        }
                                    },
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text("保存")
                                }
                                Button(
                                    onClick = {
                                        val url = urlState.text.toString().trim()
                                            .ifEmpty { SyncSettings.serverUrl(context) }
                                        if (url.isEmpty()) {
                                            scope.launch { snackbarHostState.showAppSnack("请先填写服务器地址", SnackType.Info) }
                                            return@Button
                                        }
                                        testing = true
                                        val api = SyncApi(url, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                        scope.launch {
                                            val (ok, msg) = withContext(Dispatchers.IO) {
                                                api.testConnection()
                                            }
                                            testing = false
                                            snackbarHostState.showAppSnack(msg, if (ok) SnackType.Success else SnackType.Error)
                                        }
                                    },
                                    enabled = !testing,
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text(if (testing) "测试中…" else "连通性测试")
                                }
                            }
                            if (testing) {
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    text = "正在连接服务器…",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "配对管理") {
                            // 1. 扫码配对主按钮
                            Button(
                                onClick = onOpenQrScanner,
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Icon(
                                    imageVector = LucideIcons.ScanLine,
                                    contentDescription = "扫码配对",
                                    modifier = Modifier.size(18.dp)
                                )
                                Spacer(Modifier.width(8.dp))
                                Text("扫码配对 (推荐)")
                            }
                            Spacer(Modifier.height(8.dp))
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(10.dp)
                            ) {
                                Button(
                                    onClick = {
                                        val url = urlState.text.toString().trim()
                                            .ifEmpty { SyncSettings.serverUrl(context) }
                                        if (url.isEmpty()) {
                                            scope.launch { snackbarHostState.showAppSnack("请先填写服务器地址", SnackType.Info) }
                                            return@Button
                                        }
                                        generatingCode = true
                                        val genDeviceId = SyncSettings.ensureDeviceId(context)
                                        val genDeviceName = SyncSettings.deviceName(context)
                                        val api = SyncApi(url, genDeviceId, SyncSettings.deviceToken(context))
                                        scope.launch {
                                            val r = withContext(Dispatchers.IO) {
                                                runCatching { api.createPairingCode(genDeviceId, genDeviceName) }
                                            }
                                            generatingCode = false
                                            r.onSuccess {
                                                it.deviceToken?.let { token ->
                                                    SyncSettings.setDeviceToken(context, token)
                                                    SyncSettings.setPaired(context, true)
                                                }
                                                generatedCode = it
                                                showCodeSheet = true
                                                snackbarHostState.showAppSnack("配对码已生成", SnackType.Success)
                                            }.onFailure { e ->
                                                snackbarHostState.showAppSnack(e.message ?: "生成失败", SnackType.Error)
                                            }
                                        }
                                    },
                                    enabled = !generatingCode,
                                    colors = ButtonDefaults.buttonColors(
                                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                        contentColor = MiuixTheme.colorScheme.onSurface
                                    ),
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text(if (generatingCode) "生成中…" else "生成配对码")
                                }
                                Button(
                                    onClick = { showPairDialog = true },
                                    enabled = !pairing,
                                    colors = ButtonDefaults.buttonColors(
                                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                        contentColor = MiuixTheme.colorScheme.onSurface
                                    ),
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text(if (pairing) "配对中…" else "输入配对码")
                                }
                            }
                            if (pairing) {
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    text = "等待对方确认…",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(
                            title = "设备列表",
                            trailing = {
                                if (SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
                                    Text(
                                        text = if (devicesLoading && devices.isEmpty()) "加载中…"
                                        else "${devices.count { it.online }} / ${devices.size} 在线",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                    Spacer(Modifier.width(6.dp))
                                    IconButton(onClick = { devicesManual = true; devicesReload++ }) {
                                        Icon(
                                            imageVector = LucideIcons.RefreshCw,
                                            contentDescription = "刷新",
                                            tint = MiuixTheme.colorScheme.onBackgroundVariant,
                                            modifier = Modifier.size(16.dp)
                                        )
                                    }
                                }
                            },
                        ) {
                            if (!SyncSettings.isPaired(context) || SyncSettings.deviceToken(context).isBlank()) {
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    text = "当前未加入任何设备组。请点击上方「生成配对码」或「输入配对码」接入。",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            } else if (devices.isEmpty() && !devicesLoading && devicesError == null) {
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    text = "设备组中暂无其他设备。",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                            val sortedDevices = remember(devices, selfDeviceId) {
                                devices.sortedWith(
                                    compareByDescending<DeviceInfo> { it.id == selfDeviceId }
                                        .thenByDescending { it.online }
                                        .thenByDescending { it.lastSeenAt }
                                )
                            }
                            sortedDevices.forEach { device ->
                                Spacer(Modifier.height(10.dp))
                                DeviceCard(
                                    device = device,
                                    isSelf = device.id == selfDeviceId,
                                    onDeleteClick = { deleteTargetDevice = it },
                                    onCopyId = { id ->
                                        copyPairingCode(context, id)
                                        scope.launch { snackbarHostState.showAppSnack("设备 ID 已复制", SnackType.Success) }
                                    }
                                )
                            }
                        }
                    }
                }
            }
        }

        SettingsSubPage.Filter -> {
                // ---- 二级页面 3: 过滤规则 ----
                PageShell(
                    title = "过滤规则",
                    bottomInnerPadding = bottomInnerPadding,
                    navigationIcon = {
                        IconButton(onClick = { closeSubPage() }) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Back,
                                contentDescription = "返回"
                            )
                        }
                    }
                ) { scrollBehavior, topPadding ->
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxSize()
                            .overScrollVertical()
                            .nestedScroll(scrollBehavior.nestedScrollConnection),
                        contentPadding = PaddingValues(
                            start = 16.dp,
                            end = 16.dp,
                            top = topPadding + 8.dp,
                            bottom = bottomInnerPadding + 16.dp
                        ),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                    item {
                        SectionBlock(title = "内容过滤黑名单") {
                            Text(
                                text = "复制包含以下关键词的内容时，应用将自动跳过记录与同步。",
                                fontSize = 13.sp,
                                lineHeight = 18.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                            Spacer(Modifier.height(12.dp))
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                TextField(
                                    state = addKeywordState,
                                    label = "输入要忽略的关键词",
                                    useLabelAsPlaceholder = true,
                                    modifier = Modifier.weight(1f)
                                )
                                Button(
                                    onClick = {
                                        val text = addKeywordState.text.toString().trim()
                                        if (text.isNotBlank()) {
                                            SyncSettings.addFilterKeyword(context, text)
                                            filterKeywords = SyncSettings.filterKeywords(context)
                                            addKeywordState.edit { replace(0, length, "") }
                                            scope.launch { snackbarHostState.showAppSnack("已添加规则", SnackType.Success) }
                                        }
                                    }
                                ) {
                                    Text("添加")
                                }
                            }
                            Spacer(Modifier.height(12.dp))

                            if (filterKeywords.isEmpty()) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(vertical = 16.dp),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Text(
                                        text = "暂无黑名单规则",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                            } else {
                                Column(
                                    modifier = Modifier.fillMaxWidth(),
                                    verticalArrangement = Arrangement.spacedBy(6.dp)
                                ) {
                                    filterKeywords.forEach { kw ->
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .clip(RoundedCornerShape(8.dp))
                                                .background(MiuixTheme.colorScheme.surfaceContainer)
                                                .padding(horizontal = 12.dp, vertical = 6.dp),
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text(
                                                text = kw,
                                                modifier = Modifier.weight(1f),
                                                fontSize = 14.sp
                                            )
                                            IconButton(
                                                onClick = {
                                                    SyncSettings.removeFilterKeyword(context, kw)
                                                    filterKeywords = SyncSettings.filterKeywords(context)
                                                }
                                            ) {
                                                Icon(
                                                    imageVector = MiuixIcons.Normal.Delete,
                                                    contentDescription = "删除",
                                                    tint = MiuixTheme.colorScheme.error
                                                )
                                            }
                                        }
                                    }
                                    Spacer(Modifier.height(6.dp))
                                    Button(
                                        onClick = {
                                            SyncSettings.clearFilterKeywords(context)
                                            filterKeywords = emptyList()
                                            scope.launch { snackbarHostState.showAppSnack("已清空黑名单规则", SnackType.Info) }
                                        },
                                        colors = ButtonDefaults.buttonColors(
                                            color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                            contentColor = MiuixTheme.colorScheme.error
                                        ),
                                        modifier = Modifier.fillMaxWidth()
                                    ) {
                                        Text("清空全部规则")
                                    }
                                }
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "敏感内容保护", insideMargin = PaddingValues()) {
                            SwitchPreference(
                                checked = ignoreSensitive,
                                onCheckedChange = { checked ->
                                    ignoreSensitive = checked
                                    SyncSettings.setIgnoreSensitive(context, checked)
                                },
                                title = "忽略敏感/密码标记",
                                summary = "自动跳过密码管理器或标记为 sensitive 的剪贴板"
                            )
                        }
                    }
                }
            }
        }

        SettingsSubPage.Data -> {
                // ---- 二级页面 4: 数据管理 ----
                PageShell(
                    title = "数据管理",
                    bottomInnerPadding = bottomInnerPadding,
                    navigationIcon = {
                        IconButton(onClick = { closeSubPage() }) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Back,
                                contentDescription = "返回"
                            )
                        }
                    }
                ) { scrollBehavior, topPadding ->
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxSize()
                            .overScrollVertical()
                            .nestedScroll(scrollBehavior.nestedScrollConnection),
                        contentPadding = PaddingValues(
                            start = 16.dp,
                            end = 16.dp,
                            top = topPadding + 8.dp,
                            bottom = bottomInnerPadding + 16.dp
                        ),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                    item {
                        SectionBlock(title = "备份与迁移", insideMargin = PaddingValues()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        val timeStr = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.getDefault()).format(Date())
                                        exportLauncher.launch("sync_clipboard_backup_$timeStr.json")
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "导出记录备份",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "将全部捕获历史导出为 JSON 备份文件",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Icon(
                                    imageVector = LucideIcons.Upload,
                                    contentDescription = "导出",
                                    tint = MiuixTheme.colorScheme.primary
                                )
                            }

                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        importLauncher.launch(arrayOf("application/json", "text/*", "*/*"))
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "导入记录备份",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "从 JSON 文件导入并合并历史记录",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Icon(
                                    imageVector = LucideIcons.Download,
                                    contentDescription = "导入",
                                    tint = MiuixTheme.colorScheme.primary
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "存储空间", insideMargin = PaddingValues()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "应用缓存与临时存储",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "已占用 $cacheSizeText",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Button(
                                    onClick = {
                                        scope.launch(Dispatchers.IO) {
                                            deleteFolderContents(context.cacheDir)
                                            deleteFolderContents(context.codeCacheDir)
                                            refreshCacheSize()
                                            snackbarHostState.showAppSnack("缓存已清理", SnackType.Success)
                                        }
                                    }
                                ) {
                                    Text("一键清理")
                                }
                            }
                        }
                    }
                }
            }
        }

        SettingsSubPage.Permission -> {
                // ---- 二级页面 5: 权限管理 ----
                PageShell(
                    title = "权限管理",
                    bottomInnerPadding = bottomInnerPadding,
                    navigationIcon = {
                        IconButton(onClick = { closeSubPage() }) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Back,
                                contentDescription = "返回"
                            )
                        }
                    }
                ) { scrollBehavior, topPadding ->
                    LazyColumn(
                        modifier = Modifier
                            .fillMaxSize()
                            .overScrollVertical()
                            .nestedScroll(scrollBehavior.nestedScrollConnection),
                        contentPadding = PaddingValues(
                            start = 16.dp,
                            end = 16.dp,
                            top = topPadding + 8.dp,
                            bottom = bottomInnerPadding + 16.dp
                        ),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                    item {
                        SectionBlock(title = "系统权限", insideMargin = PaddingValues()) {
                            // 通知权限
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (Build.VERSION.SDK_INT >= 33 && !isNotificationGranted) {
                                            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                                        } else {
                                            val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                                                putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                                            }
                                            runCatching { context.startActivity(intent) }
                                        }
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "通知权限",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = if (isNotificationGranted) "已授权（用于前台常驻与推送提示）" else "未授权，点击前往开启",
                                        fontSize = 13.sp,
                                        color = if (isNotificationGranted) Color(0xFF34C759) else MiuixTheme.colorScheme.error
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "前往设置",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }

                            // 电池优化白名单
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                                            val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                                                data = Uri.parse("package:${context.packageName}")
                                            }
                                            runCatching { context.startActivity(intent) }.onFailure {
                                                runCatching {
                                                    context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
                                                }
                                            }
                                        }
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "忽略电池优化 (防杀后台)",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = if (isBatteryOptIgnored) "已加入白名单（后台保活更稳定）" else "未加入白名单，点击前往开启",
                                        fontSize = 13.sp,
                                        color = if (isBatteryOptIgnored) Color(0xFF34C759) else MiuixTheme.colorScheme.error
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "前往设置",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }

                            // 系统应用详情与自启动
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                            data = Uri.parse("package:${context.packageName}")
                                        }
                                        runCatching { context.startActivity(intent) }
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "系统应用详情与自启动",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "配置系统自启动权限、省电策略及后台弹出界面",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "前往设置",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "LSPosed 模块配置说明") {
                            Text(
                                text = "1. 打开 LSPosed 管理器并勾选「NexClip」\n" +
                                    "2. 作用域必须包含「系统框架」和「NexClip」应用本身\n" +
                                    "3. 完成配置后重启系统即可生效",
                                fontSize = 13.sp,
                                lineHeight = 19.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                }
            }
        }

        SettingsSubPage.About -> {
            // ---- 二级页面 6: 关于 ----
            PageShell(
                title = "关于",
                bottomInnerPadding = bottomInnerPadding,
                navigationIcon = {
                    IconButton(onClick = { closeSubPage() }) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Back,
                            contentDescription = "返回"
                        )
                    }
                }
            ) { scrollBehavior, topPadding ->
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item {
                        SectionBlock(title = "应用信息") {
                            Column(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalAlignment = Alignment.CenterHorizontally
                            ) {
                                val icon = remember { appIconBitmap(context) }
                                Image(
                                    bitmap = icon,
                                    contentDescription = "NexClip 图标",
                                    modifier = Modifier
                                        .size(64.dp)
                                        .clip(RoundedCornerShape(16.dp))
                                )
                                Spacer(Modifier.height(10.dp))
                                Text(
                                    text = "NexClip",
                                    fontSize = 20.sp,
                                    fontWeight = FontWeight.Bold
                                )
                                Spacer(Modifier.height(4.dp))
                                Text(
                                    text = "轻量高效的跨设备剪贴板同步与管理",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                                Spacer(Modifier.height(4.dp))
                                Text(
                                    text = "版本 v" + appVersion(context),
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.primary,
                                    fontWeight = FontWeight.Medium
                                )
                            }
                            Spacer(Modifier.height(14.dp))
                            StatusRow(label = "应用包名", value = context.packageName ?: "-")
                            Spacer(Modifier.height(8.dp))
                            StatusRow(label = "开源协议", value = "MIT License")
                            Spacer(Modifier.height(8.dp))
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        val repoUrl = "https://github.com/yixing233/easy-clip"
                                        runCatching {
                                            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(repoUrl))
                                            context.startActivity(intent)
                                        }.onFailure {
                                            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                            cm.setPrimaryClip(ClipData.newPlainText("GitHub Repository", repoUrl))
                                            scope.launch { snackbarHostState.showAppSnack("仓库链接已复制", SnackType.Success) }
                                        }
                                    }
                                    .padding(vertical = 4.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text("项目仓库", color = MiuixTheme.colorScheme.onBackgroundVariant, fontSize = 14.sp)
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text(
                                        text = "github.com/yixing233/easy-clip",
                                        color = MiuixTheme.colorScheme.primary,
                                        fontSize = 13.sp
                                    )
                                    Spacer(Modifier.width(4.dp))
                                    Icon(
                                        imageVector = MiuixIcons.Normal.ChevronForward,
                                        contentDescription = "前往",
                                        tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.6f),
                                        modifier = Modifier.size(14.dp)
                                    )
                                }
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "开源致谢") {
                            Text(
                                text = "NexClip 依托并致谢以下优秀开源项目与技术规范：",
                                fontSize = 13.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                            Spacer(Modifier.height(10.dp))
                            StatusRow(label = "Miuix-KMP", value = "现代优雅的跨平台设计组件库")
                            Spacer(Modifier.height(8.dp))
                            StatusRow(label = "LSPosed", value = "Android 系统级剪贴板感知框架")
                            Spacer(Modifier.height(8.dp))
                            StatusRow(label = "ML Kit", value = "Google 离线高精度二维码识别套件")
                            Spacer(Modifier.height(8.dp))
                            StatusRow(label = "Lucide Icons", value = "清晰规整的矢量图标集")
                            Spacer(Modifier.height(8.dp))
                            StatusRow(label = "SyncClipboard", value = "跨设备多端剪贴板同步协议启发")
                        }
                    }
                }
            }
        }
    }
}
}

    // ---- 对话框与弹层 ----

    // 配对对话框 (6 位纯数字配对码单向即入)
    OverlayDialog(
        show = showPairDialog,
        title = "加入设备组",
        summary = "输入其他设备屏幕上显示的 6 位配对码即可直接接入",
        onDismissRequest = { showPairDialog = false }
    ) {
        TextField(
            state = dialogCodeState,
            label = "6 位数字配对码",
            useLabelAsPlaceholder = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            modifier = Modifier.fillMaxWidth()
        )
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = {
                    showPairDialog = false
                    onOpenQrScanner()
                },
                enabled = !pairing,
                colors = ButtonDefaults.buttonColors(
                    color = MiuixTheme.colorScheme.surfaceContainerHigh,
                    contentColor = MiuixTheme.colorScheme.primary
                ),
                modifier = Modifier.weight(1f)
            ) {
                Icon(
                    imageVector = LucideIcons.ScanLine,
                    contentDescription = "扫码",
                    tint = MiuixTheme.colorScheme.primary,
                    modifier = Modifier.size(16.dp)
                )
                Spacer(Modifier.width(4.dp))
                Text("扫一扫")
            }
            Button(
                onClick = { showPairDialog = false },
                enabled = !pairing,
                colors = ButtonDefaults.buttonColors(
                    color = MiuixTheme.colorScheme.surfaceContainerHigh,
                    contentColor = MiuixTheme.colorScheme.onSurface
                ),
                modifier = Modifier.weight(1f)
            ) {
                Text("取消")
            }
            Button(
                onClick = {
                    val url = urlState.text.toString().trim()
                        .ifEmpty { SyncSettings.serverUrl(context) }
                    val code = dialogCodeState.text.toString().trim().uppercase()
                    if (url.isEmpty()) {
                        scope.launch { snackbarHostState.showAppSnack("请先填写服务器地址", SnackType.Info) }
                        return@Button
                    }
                    if (code.isEmpty()) {
                        scope.launch { snackbarHostState.showAppSnack("请输入 6 位配对码", SnackType.Info) }
                        return@Button
                    }
                    pairing = true
                    showPairDialog = false
                    val deviceId = SyncSettings.ensureDeviceId(context)
                    val genDevName = SyncSettings.deviceName(context)
                    val api = SyncApi(url, deviceId, SyncSettings.deviceToken(context))
                    scope.launch {
                        val directRes = withContext(Dispatchers.IO) {
                            runCatching { api.pair(code, deviceId, genDevName) }
                        }
                        pairing = false
                        if (directRes.isSuccess && !directRes.getOrNull()?.deviceToken.isNullOrBlank()) {
                            val token = directRes.getOrNull()!!.deviceToken!!
                            prefs.edit().putString(SyncSettings.KEY_SERVER_URL, url).apply()
                            SyncSettings.setDeviceToken(context, token)
                            SyncSettings.setPaired(context, true)
                            dialogCodeState.edit { replace(0, length, "") }
                            snackbarHostState.showAppSnack("配对成功！已加入设备组", SnackType.Success)
                            devicesReload++
                            autoRefreshUntil = System.currentTimeMillis() + 60_000L
                            if (ClipboardMonitorService.isRunning.value) {
                                ClipboardMonitorService.stop(context)
                                ClipboardMonitorService.start(context)
                            }
                        } else {
                            val errMsg = directRes.exceptionOrNull()?.message ?: "配对失败，请检查配对码"
                            snackbarHostState.showAppSnack(errMsg, SnackType.Error)
                        }
                    }
                },
                enabled = !pairing,
                modifier = Modifier.weight(1f)
            ) {
                Text(if (pairing) "连接中…" else "立即连接")
            }
        }
    }

    // 生成的配对码用对话框展示(关闭后立即失效)
    OverlayDialog(
        show = showCodeSheet,
        title = "设备配对码",
        onDismissRequest = {
            showCodeSheet = false
            val revokeCode = generatedCode?.code
            generatedCode = null
            if (revokeCode != null) {
                scope.launch {
                    withContext(Dispatchers.IO) {
                        runCatching { SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context)).revokePairingCode(revokeCode) }
                    }
                    devicesReload++
                }
            }
        }
    ) {
        generatedCode?.let { code ->
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 8.dp, vertical = 6.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                // 1. 6 位数字卡片
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(14.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer)
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Column {
                        Text(
                            text = "6 位数字配对码",
                            fontSize = 12.sp,
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                        Text(
                            text = code.code,
                            fontSize = 26.sp,
                            fontWeight = FontWeight.Bold,
                            color = MiuixTheme.colorScheme.primary
                        )
                    }
                    Button(
                        onClick = {
                            copyPairingCode(context, code.code)
                            scope.launch { snackbarHostState.showAppSnack("配对码已复制", SnackType.Success) }
                        }
                    ) {
                        Text("复制配对码")
                    }
                }

                Spacer(Modifier.height(14.dp))
                Text(
                    text = "在另一台设备上输入上述 6 位数字配对码即可直接连接。\n关闭对话框后配对码立即失效。",
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    fontSize = 12.sp,
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center
                )

                Spacer(Modifier.height(16.dp))
                Button(
                    onClick = {
                        showCodeSheet = false
                        val revokeCode = generatedCode?.code
                        generatedCode = null
                        if (revokeCode != null) {
                            scope.launch {
                                withContext(Dispatchers.IO) {
                                    runCatching { SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context)).revokePairingCode(revokeCode) }
                                }
                                devicesReload++
                            }
                        }
                    },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("完成并关闭")
                }
            }
        }
    }

    // 修改设备名称对话框
    OverlayDialog(
        show = showNameDialog,
        title = "修改设备名称",
        onDismissRequest = { showNameDialog = false }
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
            TextField(
                state = nameDialogState,
                label = "设备名称",
                useLabelAsPlaceholder = true,
                modifier = Modifier.fillMaxWidth()
            )
            Spacer(Modifier.height(16.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Button(
                    onClick = { showNameDialog = false },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                        contentColor = MiuixTheme.colorScheme.onSurface
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        val newName = nameDialogState.text.toString().trim()
                        if (newName.isNotBlank()) {
                            SyncSettings.setDeviceName(context, newName)
                            deviceName = newName
                            showNameDialog = false
                            scope.launch { snackbarHostState.showAppSnack("已保存设备名称", SnackType.Success) }
                        }
                    },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("保存")
                }
            }
        }
    }

    // 重置设备 ID 对话框
    OverlayDialog(
        show = showResetIdDialog,
        title = "重置设备标识",
        onDismissRequest = { showResetIdDialog = false }
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
            Text(
                text = "确定要重新生成本机 Device ID 吗？\n重置后该设备在服务端将被视为新设备，需要重新登记或配对。",
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                fontSize = 13.sp,
                lineHeight = 18.sp
            )
            Spacer(Modifier.height(16.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Button(
                    onClick = { showResetIdDialog = false },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                        contentColor = MiuixTheme.colorScheme.onSurface
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        val newId = SyncSettings.resetDeviceId(context)
                        selfDeviceId = newId
                        showResetIdDialog = false
                        devicesReload++
                        scope.launch { snackbarHostState.showAppSnack("已重置 Device ID", SnackType.Success) }
                    },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.error,
                        contentColor = Color.White
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("确定重置")
                }
            }
        }
    }

    // 移除其他设备确认对话框
    val targetDev = deleteTargetDevice
    OverlayDialog(
        show = targetDev != null,
        title = "移除设备",
        onDismissRequest = { deleteTargetDevice = null }
    ) {
        if (targetDev != null) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp)
            ) {
                Text(
                    text = "确定要从设备组中移除「${targetDev.name}」吗？",
                    fontSize = 14.sp,
                    lineHeight = 20.sp,
                    color = MiuixTheme.colorScheme.onBackground
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    text = "移除后该设备将无法接收同步内容，如需恢复需重新配对接入。",
                    fontSize = 12.sp,
                    lineHeight = 16.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                Spacer(Modifier.height(16.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Button(
                        onClick = { deleteTargetDevice = null },
                        colors = ButtonDefaults.buttonColors(
                            color = MiuixTheme.colorScheme.surfaceContainerHigh,
                            contentColor = MiuixTheme.colorScheme.onSurface
                        ),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("取消")
                    }
                    Button(
                        onClick = {
                            val targetId = targetDev.id
                            val targetName = targetDev.name
                            deleteTargetDevice = null
                            scope.launch {
                                val result = withContext(Dispatchers.IO) {
                                    runCatching {
                                        SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context)).deleteDevice(targetId)
                                    }
                                }
                                if (result.isSuccess && result.getOrNull() == true) {
                                    devices = devices.filterNot { it.id == targetId }
                                    if (targetId == SyncSettings.ensureDeviceId(context)) {
                                        SyncSettings.clearPairing(context)
                                        ClipboardMonitorService.stop(context)
                                    }
                                    snackbarHostState.showAppSnack("设备「$targetName」已成功移除", SnackType.Success)
                                    devicesReload++
                                } else {
                                    val err = result.exceptionOrNull()?.message ?: "移除设备失败，请稍后重试"
                                    snackbarHostState.showAppSnack(err, SnackType.Error)
                                }
                            }
                        },
                        colors = ButtonDefaults.buttonColors(
                            color = MiuixTheme.colorScheme.error,
                            contentColor = Color.White
                        ),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("确认移除")
                    }
                }
            }
        }
    }

    // 通知展示样式选择弹窗
    OverlayDialog(
        show = showNotificationStyleDialog,
        title = "选择通知展示样式",
        summary = if (SyncSettings.isHyperOs()) "已检测到小米澎湃OS (HyperOS)，推荐使用超级岛" else "根据系统支持选择合适的通知交互模式",
        onDismissRequest = { showNotificationStyleDialog = false }
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
            NotificationStyle.entries.forEach { style ->
                val isSelected = notificationStyle == style
                val isRecommended = style == NotificationStyle.HYPEROS_ISLAND && SyncSettings.isHyperOs()
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(
                            if (isSelected) MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                            else Color.Transparent
                        )
                        .clickable {
                            notificationStyle = style
                            SyncSettings.setNotificationStyle(context, style)
                            ClipboardMonitorService.updateNotification(context)
                            showNotificationStyleDialog = false
                            scope.launch {
                                snackbarHostState.showAppSnack("已切换为 ${style.label}", SnackType.Success)
                            }
                        }
                        .padding(horizontal = 12.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                text = style.label,
                                fontSize = 16.sp,
                                fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Medium,
                                color = if (isSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                            )
                            if (isRecommended) {
                                Spacer(Modifier.width(6.dp))
                                Box(
                                    modifier = Modifier
                                        .clip(RoundedCornerShape(4.dp))
                                        .background(MiuixTheme.colorScheme.primary)
                                        .padding(horizontal = 6.dp, vertical = 2.dp)
                                ) {
                                    Text(
                                        text = "推荐",
                                        color = Color.White,
                                        fontSize = 10.sp,
                                        fontWeight = FontWeight.Bold
                                    )
                                }
                            }
                        }
                        Spacer(Modifier.height(3.dp))
                        Text(
                            text = style.summary,
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            fontSize = 12.sp,
                            lineHeight = 16.sp
                        )
                    }
                    if (isSelected) {
                        Spacer(Modifier.width(8.dp))
                        Text(
                            text = "✓",
                            color = MiuixTheme.colorScheme.primary,
                            fontWeight = FontWeight.Bold,
                            fontSize = 16.sp
                        )
                    }
                }
                Spacer(Modifier.height(4.dp))
            }
        }
    }
}
}

/**
 * 一级设置分类导航行
 */
@Composable
private fun SettingsNavRow(
    title: String,
    summary: String,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                fontSize = 16.sp,
                fontWeight = FontWeight.Medium
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = summary,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                fontSize = 13.sp,
                lineHeight = 17.sp
            )
        }
        Spacer(Modifier.width(8.dp))
        Icon(
            imageVector = MiuixIcons.Normal.ChevronForward,
            contentDescription = title,
            tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
        )
    }
}

private fun platformIcon(platform: String): androidx.compose.ui.graphics.vector.ImageVector {
    val p = platform.lowercase()
    return when {
        p.contains("android") || p.contains("phone") || p.contains("ios") -> LucideIcons.Smartphone
        p.contains("win") || p.contains("windows") || p.contains("desktop") -> LucideIcons.Laptop
        p.contains("mac") || p.contains("linux") -> LucideIcons.Monitor
        p.contains("web") || p.contains("browser") -> LucideIcons.Globe
        else -> LucideIcons.Monitor
    }
}

@Composable
private fun DeviceCard(
    device: DeviceInfo,
    isSelf: Boolean,
    onDeleteClick: (DeviceInfo) -> Unit,
    onCopyId: (String) -> Unit
) {
    val lastSeen = relativeTime(device.lastSeenAt)
    val statusText = if (device.online) "在线" else if (lastSeen.isNotEmpty()) lastSeen else "离线"
    val statusColor = if (device.online) Color(0xFF10B981) else MiuixTheme.colorScheme.onBackgroundVariant

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(MiuixTheme.colorScheme.surfaceContainer)
            .clickable { onCopyId(device.id) }
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        // 左侧：平台图标容器 (38x38dp)
        Box(
            modifier = Modifier
                .size(38.dp)
                .clip(RoundedCornerShape(10.dp))
                .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.08f)),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = platformIcon(device.platform),
                contentDescription = device.platform,
                tint = MiuixTheme.colorScheme.primary,
                modifier = Modifier.size(20.dp)
            )
        }

        Spacer(Modifier.width(12.dp))

        // 中间：两行核心信息区 (自适应填充)
        Column(
            modifier = Modifier.weight(1f)
        ) {
            // 第一行：设备名称 + 本机徽章 + 状态胶囊
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text(
                    text = device.name,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = MiuixTheme.colorScheme.onBackground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false)
                )

                if (isSelf) {
                    Spacer(Modifier.width(6.dp))
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(4.dp))
                            .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
                            .padding(horizontal = 5.dp, vertical = 1.5.dp)
                    ) {
                        Text(
                            text = "本机",
                            fontSize = 10.sp,
                            fontWeight = FontWeight.Bold,
                            color = MiuixTheme.colorScheme.primary
                        )
                    }
                }

                Spacer(Modifier.width(6.dp))

                // 状态小胶囊 (绿色/灰色圆点 + 文字)
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .background(statusColor.copy(alpha = 0.1f))
                        .padding(horizontal = 5.dp, vertical = 1.5.dp)
                ) {
                    Box(
                        modifier = Modifier
                            .size(5.dp)
                            .background(statusColor, CircleShape)
                    )
                    Spacer(Modifier.width(4.dp))
                    Text(
                        text = statusText,
                        fontSize = 10.sp,
                        fontWeight = FontWeight.Medium,
                        color = statusColor
                    )
                }
            }

            Spacer(Modifier.height(3.dp))

            // 第二行：平台 · IP · 版本
            val subText = buildString {
                append(device.platform)
                if (!device.ip.isNullOrBlank()) {
                    append(" · ")
                    append(device.ip)
                }
                if (!device.version.isNullOrBlank()) {
                    append(" · v")
                    append(device.version)
                }
            }
            Text(
                text = subText,
                fontSize = 11.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.75f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }

        // 右侧：非本机显示删除按钮
        if (!isSelf) {
            Spacer(Modifier.width(8.dp))
            Box(
                modifier = Modifier
                    .size(32.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(Color(0xFFEF4444).copy(alpha = 0.1f))
                    .clickable { onDeleteClick(device) },
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = LucideIcons.Trash2,
                    contentDescription = "移除设备",
                    tint = Color(0xFFEF4444),
                    modifier = Modifier.size(15.dp)
                )
            }
        }
    }
}

/** 服务端 lastSeenAt(可能带 Z 也可能无时区后缀)→ 相对时间文本 */
private fun relativeTime(iso: String): String = try {
    val t = try {
        java.time.OffsetDateTime.parse(iso).toInstant()
    } catch (_: Exception) {
        java.time.LocalDateTime.parse(iso).toInstant(java.time.ZoneOffset.UTC)
    }
    val sec = java.time.Duration.between(t, java.time.Instant.now()).seconds
    when {
        sec < 60 -> "刚刚"
        sec < 3600 -> "${sec / 60} 分钟前"
        sec < 86400 -> "${sec / 3600} 小时前"
        else -> "${sec / 86400} 天前"
    }
} catch (_: Exception) {
    ""
}

private fun copyPairingCode(context: android.content.Context, code: String) {
    val cm = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
    cm.setPrimaryClip(android.content.ClipData.newPlainText("PairingCode", code))
    android.widget.Toast.makeText(context, "已复制", android.widget.Toast.LENGTH_SHORT).show()
}

/** 取应用图标(关于页居中圆角头像用) */
private fun appIconBitmap(context: android.content.Context): androidx.compose.ui.graphics.ImageBitmap {
    val drawable = context.packageManager.getApplicationIcon(context.applicationInfo)
    val bmp = if (drawable is android.graphics.drawable.BitmapDrawable && drawable.bitmap != null) {
        drawable.bitmap!!
    } else {
        android.graphics.Bitmap.createBitmap(
            drawable.intrinsicWidth.coerceAtLeast(1),
            drawable.intrinsicHeight.coerceAtLeast(1),
            android.graphics.Bitmap.Config.ARGB_8888
        ).also {
            drawable.setBounds(0, 0, it.width, it.height)
            drawable.draw(android.graphics.Canvas(it))
        }
    }
    return bmp.asImageBitmap()
}

private fun appVersion(context: android.content.Context): String {
    return runCatching {
        val info = context.packageManager.getPackageInfo(
            context.packageName,
            android.content.pm.PackageManager.PackageInfoFlags.of(0L)
        )
        buildString {
            append(info.versionName ?: "?")
            append(" (")
            append(info.longVersionCode)
            append(")")
        }
    }.getOrDefault("?")
}

private fun getFolderSize(file: File?): Long {
    if (file == null || !file.exists()) return 0L
    var size = 0L
    if (file.isDirectory) {
        file.listFiles()?.forEach { size += getFolderSize(it) }
    } else {
        size += file.length()
    }
    return size
}

private fun deleteFolderContents(file: File?) {
    if (file == null || !file.exists()) return
    if (file.isDirectory) {
        file.listFiles()?.forEach {
            deleteFolderContents(it)
            it.delete()
        }
    } else {
        file.delete()
    }
}

private fun formatSize(bytes: Long): String {
    if (bytes <= 0) return "0 B"
    if (bytes < 1024) return "$bytes B"
    val kb = bytes / 1024.0
    if (kb < 1024) return "%.1f KB".format(kb)
    val mb = kb / 1024.0
    return "%.1f MB".format(mb)
}
