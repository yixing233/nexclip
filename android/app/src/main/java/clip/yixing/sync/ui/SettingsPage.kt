package clip.yixing.sync.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ComponentName
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import clip.yixing.sync.MainActivity
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
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.LinearOutSlowInEasing
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import kotlinx.coroutines.CancellationException
import clip.yixing.sync.util.AppSourceHelper
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.input.clearText
import androidx.compose.foundation.text.input.setTextAndPlaceCursorAtEnd
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import clip.yixing.sync.hook.ModuleStatusStore
import clip.yixing.sync.shizuku.ShizukuClipboardManager
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
import androidx.compose.ui.graphics.vector.ImageVector
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
import clip.yixing.sync.util.CaptureMethod
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import clip.yixing.sync.smartaction.SmartActionSettingsPage
import kotlinx.coroutines.isActive
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.BasicComponent
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.window.WindowDialog
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.preference.WindowDropdownPreference
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
enum class SettingsSubPage(val title: String) {
    Devices("设备列表与配对"),
    Filter("过滤规则"),
    SmartActions("智能动作与应用直达"),
    About("关于")
}

@Composable
internal fun SettingsPage(
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    floatingBarEnabled: Boolean,
    onFloatingBarChange: (Boolean) -> Unit,
    onOverlayActiveChanged: (Boolean) -> Unit = {},
    onOpenQrScanner: () -> Unit = {},
    isGlobalOverlayActive: Boolean = false,
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
    var hideFromRecents by remember { mutableStateOf(SyncSettings.isHideFromRecents(context)) }
    var notificationEnabled by remember { mutableStateOf(SyncSettings.notificationEnabled(context)) }

    // ---- 1. 基础设置与设备状态 ----
    var deviceName by remember { mutableStateOf(SyncSettings.deviceName(context)) }
    var selfDeviceId by remember { mutableStateOf(SyncSettings.ensureDeviceId(context)) }
    var showNameDialog by remember { mutableStateOf(false) }
    val nameDialogState = remember { TextFieldState(deviceName) }

    var bootStart by remember { mutableStateOf(SyncSettings.bootStartEnabled(context)) }
    var autoCheckUpdate by remember { mutableStateOf(SyncSettings.autoCheckUpdate(context)) }
    var notificationStyle by remember { mutableStateOf(SyncSettings.notificationStyle(context)) }
    val notificationStyles = remember { NotificationStyle.entries }
    val notificationStyleLabels = remember { notificationStyles.map { it.label } }
    var outerGlowEnabled by remember { mutableStateOf(SyncSettings.isHyperOsOuterGlow(context)) }
    val glowColors = remember { SyncSettings.GLOW_COLORS }
    val glowColorLabels = remember { glowColors.map { it.second } }
    var glowColorIndex by remember {
        mutableStateOf(
            glowColors.indexOfFirst { it.first.equals(SyncSettings.hyperOsGlowColor(context), ignoreCase = true) }.coerceAtLeast(0)
        )
    }
    val islandTimeoutOptions = remember { SyncSettings.ISLAND_TIMEOUT_OPTIONS }
    val islandTimeoutLabels = remember { SyncSettings.ISLAND_TIMEOUT_LABELS }
    var islandTimeoutIndex by remember {
        mutableStateOf(
            islandTimeoutOptions.indexOf(SyncSettings.hyperOsIslandTimeout(context)).let { if (it >= 0) it else 1 }
        )
    }
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
    var showAppPickerDialog by remember { mutableStateOf(false) }
    var isDropdownExpanded by remember { mutableStateOf(false) }
    var checkingUpdate by remember { mutableStateOf(false) }
    var updateDialogInfo by remember { mutableStateOf<clip.yixing.sync.util.UpdateInfo?>(null) }

    // 是否有任何弹窗、Bottom Sheet 或选择器处于打开状态
    val isAnyOverlayOpen = showNameDialog ||
        showPairDialog ||
        showCodeSheet ||
        deleteTargetDevice != null ||
        showAppPickerDialog ||
        isDropdownExpanded ||
        updateDialogInfo != null

    val dispatcherOwner = androidx.navigationevent.compose.LocalNavigationEventDispatcherOwner.current
    val directInput = remember { androidx.navigationevent.DirectNavigationEventInput() }
    androidx.compose.runtime.DisposableEffect(dispatcherOwner, directInput) {
        dispatcherOwner?.navigationEventDispatcher?.addInput(directInput)
        onDispose {
            dispatcherOwner?.navigationEventDispatcher?.removeInput(directInput)
        }
    }

    // 0. 气泡下拉菜单展开时优先拦截返回手势，仅关闭气泡菜单
    BackHandler(enabled = isDropdownExpanded && !isGlobalOverlayActive) {
        directInput.backCompleted()
        isDropdownExpanded = false
    }

    // 1. 弹层开启时优先拦截返回键/手势，仅关闭当前弹窗
    PredictiveBackHandler(enabled = isAnyOverlayOpen && !isDropdownExpanded && !isGlobalOverlayActive) { progress ->
        try {
            progress.collect { }
        } finally {
            if (showNameDialog) {
                showNameDialog = false
            } else if (showPairDialog) {
                showPairDialog = false
            } else if (showCodeSheet) {
                showCodeSheet = false
            } else if (deleteTargetDevice != null) {
                deleteTargetDevice = null
            } else if (showAppPickerDialog) {
                showAppPickerDialog = false
            }
        }
    }

    // 2. 预测返回手势监听（无缝连贯跟手，二级页面退出）
    PredictiveBackHandler(enabled = currentSubPage != null && !isAnyOverlayOpen && !isGlobalOverlayActive) { progress ->
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

    // ① 设备列表基础加载与进入设备列表二级页面时即时自动拉取
    LaunchedEffect(devicesReload, currentSubPage) {
        if (currentSubPage != null && currentSubPage != SettingsSubPage.Devices) return@LaunchedEffect
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
    var filterPackages by remember { mutableStateOf(SyncSettings.filterPackages(context)) }
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

    var isCameraGranted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }
    val cameraPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { isGranted ->
        isCameraGranted = isGranted
        if (isGranted) {
            scope.launch { snackbarHostState.showAppSnack("已授予相机扫描权限", SnackType.Success) }
        } else {
            scope.launch { snackbarHostState.showAppSnack("未授予相机权限", SnackType.Info) }
        }
    }

    val captureMethods = remember { CaptureMethod.entries }
    val captureMethodLabels = remember { captureMethods.map { it.label } }
    var captureMethod by remember { mutableStateOf(SyncSettings.captureMethod(context)) }

    // 页面 Resume 时自动刷新所有权限与系统状态
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                isNotificationGranted = if (Build.VERSION.SDK_INT >= 33) {
                    ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
                } else true
                isBatteryOptIgnored = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    powerManager.isIgnoringBatteryOptimizations(context.packageName)
                } else true
                isCameraGranted = ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
                ShizukuClipboardManager.updateStatus(context)
                captureMethod = SyncSettings.captureMethod(context)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
        }
    }

    // 二级子页面全屏滑入时通知底栏收起避让（对话框和气泡卡片不隐藏底栏，由系统 Window 遮罩自然覆盖）
    LaunchedEffect(currentSubPage) {
        onOverlayActiveChanged(currentSubPage != null)
    }

    val screenCornerRadius = rememberScreenCornerRadius()

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
                        clip = true
                        shape = RoundedCornerShape(screenCornerRadius)
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
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    // 1. 基础与界面
                    item {
                        SectionBlock(title = "基础与界面", insideMargin = PaddingValues()) {
                            ArrowPreference(
                                title = "设备名称",
                                endActions = {
                                    Text(
                                        text = deviceName,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                        fontSize = 14.sp
                                    )
                                },
                                onClick = {
                                    nameDialogState.setTextAndPlaceCursorAtEnd(deviceName)
                                    showNameDialog = true
                                }
                            )
                            SwitchPreference(
                                checked = bootStart,
                                onCheckedChange = { checked ->
                                    bootStart = checked
                                    prefs.edit()
                                        .putBoolean(SyncSettings.KEY_BOOT_START_ENABLED, checked)
                                        .apply()
                                },
                                title = "开机自启"
                            )
                            SwitchPreference(
                                checked = autoCheckUpdate,
                                onCheckedChange = { checked ->
                                    autoCheckUpdate = checked
                                    SyncSettings.setAutoCheckUpdate(context, checked)
                                },
                                title = "启动检查新版本"
                            )
                            SwitchPreference(
                                checked = floatingBarEnabled,
                                onCheckedChange = onFloatingBarChange,
                                title = "悬浮底栏"
                            )
                            SwitchPreference(
                                checked = predictiveBackEnabled,
                                onCheckedChange = { checked ->
                                    predictiveBackEnabled = checked
                                    SyncSettings.setPredictiveBackEnabled(context, checked)
                                },
                                title = "预测返回手势"
                            )
                            SwitchPreference(
                                checked = hideFromRecents,
                                onCheckedChange = { checked ->
                                    hideFromRecents = checked
                                    SyncSettings.setHideFromRecents(context, checked)
                                    context.findMainActivity()?.updateRecentsVisibility(checked)
                                },
                                title = "从最近任务隐藏"
                            )
                        }
                    }

                    // 2. 同步服务
                    item {
                        SectionBlock(title = "同步服务") {
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
                                    colors = ButtonDefaults.buttonColorsPrimary(),
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text("保存配置")
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
                            Spacer(Modifier.height(6.dp))
                            ArrowPreference(
                                title = "扫码加入设备组",
                                onClick = onOpenQrScanner
                            )
                            ArrowPreference(
                                title = "设备列表与配对",
                                endActions = {
                                    Text(
                                        text = if (!SyncSettings.isPaired(context) || SyncSettings.deviceToken(context).isBlank()) "未加入"
                                        else if (devicesLoading && devices.isEmpty()) "加载中…"
                                        else "${devices.count { it.online }} / ${devices.size} 在线",
                                        color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                        fontSize = 14.sp
                                    )
                                },
                                onClick = { openSubPage(SettingsSubPage.Devices) }
                            )
                        }
                    }

                    // 3. 通知与隐私
                    item {
                        SectionBlock(title = "通知与隐私", insideMargin = PaddingValues()) {
                            SwitchPreference(
                                checked = notificationEnabled,
                                onCheckedChange = { checked ->
                                    notificationEnabled = checked
                                    SyncSettings.setNotificationEnabled(context, checked)
                                    ClipboardMonitorService.updateNotification(context)
                                },
                                title = "同步与捕获通知"
                            )
                            if (notificationEnabled) {
                                val notificationStyleIndex = notificationStyles.indexOf(notificationStyle).coerceAtLeast(0)
                                WindowDropdownPreference(
                                    items = notificationStyleLabels,
                                    selectedIndex = notificationStyleIndex,
                                    onSelectedIndexChange = { index ->
                                        val style = notificationStyles[index]
                                        notificationStyle = style
                                        SyncSettings.setNotificationStyle(context, style)
                                        ClipboardMonitorService.updateNotification(context)
                                    },
                                    onExpandedChange = { isDropdownExpanded = it },
                                    title = "通知展示样式"
                                )
                                if (notificationStyle == NotificationStyle.HYPEROS_ISLAND || SyncSettings.isHyperOs()) {
                                    SwitchPreference(
                                        checked = outerGlowEnabled,
                                        onCheckedChange = { checked ->
                                            outerGlowEnabled = checked
                                            SyncSettings.setHyperOsOuterGlow(context, checked)
                                            ClipboardMonitorService.updateNotification(context)
                                        },
                                        title = "超级岛流光呼吸灯效"
                                    )
                                    if (outerGlowEnabled) {
                                        GlowColorPaletteChips(
                                            selectedColor = glowColors.getOrNull(glowColorIndex)?.first ?: "#006EFF",
                                            onColorSelected = { colorHex ->
                                                val newIndex = glowColors.indexOfFirst { it.first.equals(colorHex, ignoreCase = true) }.coerceAtLeast(0)
                                                glowColorIndex = newIndex
                                                SyncSettings.setHyperOsGlowColor(context, colorHex)
                                                ClipboardMonitorService.updateNotification(context)
                                            }
                                        )
                                    }
                                    WindowDropdownPreference(
                                        items = islandTimeoutLabels,
                                        selectedIndex = islandTimeoutIndex,
                                        onSelectedIndexChange = { index ->
                                            islandTimeoutIndex = index
                                            val timeoutSec = islandTimeoutOptions[index]
                                            SyncSettings.setHyperOsIslandTimeout(context, timeoutSec)
                                            ClipboardMonitorService.updateNotification(context)
                                        },
                                        onExpandedChange = { isDropdownExpanded = it },
                                        title = "小岛常驻有效时长"
                                    )
                                }
                            }
                            SwitchPreference(
                                checked = ignoreSensitive,
                                onCheckedChange = { checked ->
                                    ignoreSensitive = checked
                                    SyncSettings.setIgnoreSensitive(context, checked)
                                },
                                title = "忽略敏感与密码标记"
                            )
                            ArrowPreference(
                                title = "智能动作与应用直达",
                                endActions = {
                                    val isSmartEnabled = SyncSettings.isSmartActionMasterEnabled(context)
                                    Text(
                                        text = if (isSmartEnabled) "已开启" else "已关闭",
                                        color = if (isSmartEnabled) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                        fontSize = 14.sp
                                    )
                                },
                                onClick = { openSubPage(SettingsSubPage.SmartActions) }
                            )
                            val totalFilterRules = filterKeywords.size + filterPackages.size
                            ArrowPreference(
                                title = "过滤与忽略黑名单",
                                endActions = {
                                    Text(
                                        text = if (totalFilterRules == 0) "未设置" else "${totalFilterRules} 条规则",
                                        color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                        fontSize = 14.sp
                                    )
                                },
                                onClick = { openSubPage(SettingsSubPage.Filter) }
                            )
                        }
                    }

                    // 4. 数据与存储
                    item {
                        SectionBlock(title = "数据与存储", insideMargin = PaddingValues()) {
                            WindowDropdownPreference(
                                items = historyLabels,
                                selectedIndex = historyIndex,
                                onSelectedIndexChange = { index ->
                                    historyIndex = index
                                    prefs.edit()
                                        .putInt(SyncSettings.KEY_MAX_HISTORY, historyOptions[index])
                                        .apply()
                                },
                                onExpandedChange = { isDropdownExpanded = it },
                                title = "记录上限"
                            )
                            BasicComponent(
                                title = "导出记录备份",
                                endActions = {
                                    Icon(
                                        imageVector = LucideIcons.Upload,
                                        contentDescription = null,
                                        tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f),
                                        modifier = Modifier.size(18.dp)
                                    )
                                },
                                onClick = {
                                    val timeStr = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.getDefault()).format(Date())
                                    exportLauncher.launch("sync_clipboard_backup_$timeStr.json")
                                }
                            )
                            BasicComponent(
                                title = "导入记录备份",
                                endActions = {
                                    Icon(
                                        imageVector = LucideIcons.Download,
                                        contentDescription = null,
                                        tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f),
                                        modifier = Modifier.size(18.dp)
                                    )
                                },
                                onClick = {
                                    importLauncher.launch(arrayOf("application/json", "text/*", "*/*"))
                                }
                            )
                            ArrowPreference(
                                title = "应用缓存",
                                endActions = {
                                    Text(
                                        text = cacheSizeText,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                        fontSize = 14.sp
                                    )
                                },
                                onClick = {
                                    scope.launch(Dispatchers.IO) {
                                        deleteFolderContents(context.cacheDir)
                                        deleteFolderContents(context.codeCacheDir)
                                        refreshCacheSize()
                                        snackbarHostState.showAppSnack("缓存已清理", SnackType.Success)
                                    }
                                }
                            )
                        }
                    }

                    // 5. 权限与保活
                    item {
                        val moduleStatus by ModuleStatusStore.moduleStatus.collectAsState()
                        val isModuleActivated = moduleStatus.activated
                        val shizukuStatus by ShizukuClipboardManager.status.collectAsState()

                        SectionBlock(title = "权限与保活", insideMargin = PaddingValues()) {
                            val captureMethodIndex = captureMethods.indexOf(captureMethod).coerceAtLeast(0)
                            WindowDropdownPreference(
                                items = captureMethodLabels,
                                selectedIndex = captureMethodIndex,
                                onSelectedIndexChange = { index ->
                                    val method = captureMethods[index]
                                    captureMethod = method
                                    SyncSettings.setCaptureMethod(context, method)
                                    ClipboardMonitorService.updateMonitoringState(context)
                                },
                                onExpandedChange = { isDropdownExpanded = it },
                                title = "后台监听与授权模式"
                            )

                            if (captureMethod == CaptureMethod.AUTO || captureMethod == CaptureMethod.LSPOSED) {
                                ArrowPreference(
                                    title = "LSPosed 系统框架模块",
                                    endActions = {
                                        Text(
                                            text = if (isModuleActivated) (moduleStatus.frameworkVersion?.let { "v$it 已激活" } ?: "已激活") else "未激活",
                                            color = if (isModuleActivated) Color(0xFF34C759) else Color(0xFFFF9500),
                                            fontSize = 14.sp,
                                            fontWeight = FontWeight.Medium
                                        )
                                    },
                                    onClick = {
                                        val opened = openLsposedManager(context)
                                        if (!opened) {
                                            scope.launch {
                                                snackbarHostState.showAppSnack("未检测到 LSPosed 管理器应用，请手动打开", SnackType.Info)
                                            }
                                        }
                                    }
                                )
                            }

                            if (captureMethod == CaptureMethod.AUTO || captureMethod == CaptureMethod.SHIZUKU) {
                                ArrowPreference(
                                    title = "Shizuku 授权 (免 Root 监听)",
                                    endActions = {
                                        val (label, color) = when (shizukuStatus) {
                                            ShizukuClipboardManager.ShizukuStatus.AUTHORIZED_RUNNING -> "已授权" to Color(0xFF34C759)
                                            ShizukuClipboardManager.ShizukuStatus.UNAUTHORIZED -> "去授权" to MiuixTheme.colorScheme.primary
                                            ShizukuClipboardManager.ShizukuStatus.DEAD_OR_STOPPED -> "未运行" to Color(0xFFFF9500)
                                            ShizukuClipboardManager.ShizukuStatus.NOT_INSTALLED -> "未安装" to MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f)
                                        }
                                        Text(
                                            text = label,
                                            color = color,
                                            fontSize = 14.sp,
                                            fontWeight = FontWeight.Medium
                                        )
                                    },
                                    onClick = {
                                        when (shizukuStatus) {
                                            ShizukuClipboardManager.ShizukuStatus.AUTHORIZED_RUNNING -> {
                                                scope.launch {
                                                    snackbarHostState.showAppSnack("Shizuku 剪贴板服务正常运行中", SnackType.Success)
                                                }
                                            }
                                            ShizukuClipboardManager.ShizukuStatus.UNAUTHORIZED -> {
                                                ShizukuClipboardManager.requestPermission()
                                            }
                                            ShizukuClipboardManager.ShizukuStatus.DEAD_OR_STOPPED,
                                            ShizukuClipboardManager.ShizukuStatus.NOT_INSTALLED -> {
                                                val opened = openShizukuManager(context)
                                                if (!opened) {
                                                    val browserIntent = Intent(Intent.ACTION_VIEW, Uri.parse("https://shizuku.rikka.app/download/")).apply {
                                                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                                                    }
                                                    runCatching { context.startActivity(browserIntent) }.onFailure {
                                                        scope.launch {
                                                            snackbarHostState.showAppSnack("未检测到 Shizuku 应用，请先安装并启动", SnackType.Info)
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                )
                            }

                            ArrowPreference(
                                title = "通知与前台服务权限",
                                endActions = {
                                    Text(
                                        text = if (isNotificationGranted) "已开启" else "去开启",
                                        color = if (isNotificationGranted) Color(0xFF34C759) else MiuixTheme.colorScheme.primary,
                                        fontSize = 14.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                },
                                onClick = {
                                    if (Build.VERSION.SDK_INT >= 33 && !isNotificationGranted) {
                                        notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                                    } else {
                                        val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                                            putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                                        }
                                        runCatching { context.startActivity(intent) }.onFailure {
                                            context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                                data = Uri.parse("package:${context.packageName}")
                                            })
                                        }
                                    }
                                }
                            )
                            ArrowPreference(
                                title = "忽略电池优化 (防杀后台)",
                                endActions = {
                                    Text(
                                        text = if (isBatteryOptIgnored) "已加白" else "去加白",
                                        color = if (isBatteryOptIgnored) Color(0xFF34C759) else MiuixTheme.colorScheme.primary,
                                        fontSize = 14.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                },
                                onClick = {
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
                            )
                            ArrowPreference(
                                title = "相机扫描权限",
                                endActions = {
                                    Text(
                                        text = if (isCameraGranted) "已授权" else "去授权",
                                        color = if (isCameraGranted) Color(0xFF34C759) else MiuixTheme.colorScheme.primary,
                                        fontSize = 14.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                },
                                onClick = {
                                    if (!isCameraGranted) {
                                        cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
                                    } else {
                                        val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                            data = Uri.parse("package:${context.packageName}")
                                        }
                                        runCatching { context.startActivity(intent) }
                                    }
                                }
                            )
                            ArrowPreference(
                                title = "系统自启动与运行配置",
                                endActions = {
                                    Text(
                                        text = "前往",
                                        color = MiuixTheme.colorScheme.primary,
                                        fontSize = 14.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                },
                                onClick = {
                                    val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                        data = Uri.parse("package:${context.packageName}")
                                    }
                                    runCatching { context.startActivity(intent) }
                                }
                            )
                        }
                    }

                    // 6. 关于
                    item {
                        SectionBlock(title = "关于", insideMargin = PaddingValues()) {
                            ArrowPreference(
                                title = "关于 NexClip",
                                endActions = {
                                    Text(
                                        text = "v" + appVersion(context),
                                        color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                        fontSize = 14.sp
                                    )
                                },
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
                        val corner = screenCornerRadius + (p * 4).dp
                        shape = RoundedCornerShape(corner)
                        shadowElevation = (1f - p) * 24f
                    }
            ) {
                when (subPage) {
                    SettingsSubPage.Devices -> {
                        // ---- 二级页面 1: 设备列表与配对 ----
                        PageShell(
                            title = "设备列表与配对",
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
                                    SectionBlock(title = "配对管理") {
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
                                                Icon(
                                                    imageVector = MiuixIcons.Normal.Refresh,
                                                    contentDescription = "生成配对码",
                                                    modifier = Modifier.size(16.dp)
                                                )
                                                Spacer(Modifier.width(6.dp))
                                                Text(if (generatingCode) "生成中…" else "生成配对码")
                                            }
                                            Button(
                                                onClick = { showPairDialog = true },
                                                enabled = !pairing,
                                                colors = ButtonDefaults.buttonColorsPrimary(),
                                                modifier = Modifier.weight(1f)
                                            ) {
                                                Icon(
                                                    imageVector = LucideIcons.ScanLine,
                                                    contentDescription = "开始配对",
                                                    modifier = Modifier.size(16.dp)
                                                )
                                                Spacer(Modifier.width(6.dp))
                                                Text(if (pairing) "配对中…" else "开始配对")
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
                                                isServerConnected = isServerConnected,
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
                        // ---- 二级页面 2: 过滤规则 ----
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
                                // 1. 应用忽略黑名单
                                item {
                                    SectionBlock(title = "应用忽略黑名单") {
                                        Text(
                                            text = "选中的应用在产生剪贴板复制行为时，应用将自动跳过捕获与多端同步（适合密码管理器、手机银行等敏感应用）。",
                                            fontSize = 13.sp,
                                            lineHeight = 18.sp,
                                            color = MiuixTheme.colorScheme.onBackgroundVariant
                                        )
                                        Spacer(Modifier.height(12.dp))
                                        Button(
                                            onClick = { showAppPickerDialog = true },
                                            colors = ButtonDefaults.buttonColorsPrimary(),
                                            modifier = Modifier.fillMaxWidth()
                                        ) {
                                            Icon(
                                                imageVector = LucideIcons.Smartphone,
                                                contentDescription = "选择应用",
                                                modifier = Modifier.size(16.dp)
                                            )
                                            Spacer(Modifier.width(6.dp))
                                            Text("从已安装应用中挑选…")
                                        }
                                        Spacer(Modifier.height(10.dp))

                                        if (filterPackages.isEmpty()) {
                                            Box(
                                                modifier = Modifier
                                                    .fillMaxWidth()
                                                    .padding(vertical = 12.dp),
                                                contentAlignment = Alignment.Center
                                            ) {
                                                Text(
                                                    text = "暂无忽略应用",
                                                    fontSize = 13.sp,
                                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                                )
                                            }
                                        } else {
                                            Column(
                                                modifier = Modifier.fillMaxWidth(),
                                                verticalArrangement = Arrangement.spacedBy(6.dp)
                                            ) {
                                                filterPackages.forEach { pkg ->
                                                    val appName = remember(pkg) { AppSourceHelper.resolveAppName(context, pkg) ?: pkg }
                                                    val appIcon = remember(pkg) { AppSourceHelper.getAppIconBitmap(context, pkg) }
                                                    Row(
                                                        modifier = Modifier
                                                            .fillMaxWidth()
                                                            .clip(RoundedCornerShape(8.dp))
                                                            .background(MiuixTheme.colorScheme.surfaceContainer)
                                                            .padding(horizontal = 10.dp, vertical = 6.dp),
                                                        verticalAlignment = Alignment.CenterVertically
                                                    ) {
                                                        if (appIcon != null) {
                                                            Image(
                                                                bitmap = appIcon,
                                                                contentDescription = appName,
                                                                modifier = Modifier
                                                                    .size(28.dp)
                                                                    .clip(RoundedCornerShape(6.dp))
                                                            )
                                                        } else {
                                                            Box(
                                                                modifier = Modifier
                                                                    .size(28.dp)
                                                                    .clip(RoundedCornerShape(6.dp))
                                                                    .background(MiuixTheme.colorScheme.surfaceContainerHigh),
                                                                contentAlignment = Alignment.Center
                                                            ) {
                                                                Icon(
                                                                    imageVector = LucideIcons.Smartphone,
                                                                    contentDescription = appName,
                                                                    modifier = Modifier.size(16.dp),
                                                                    tint = MiuixTheme.colorScheme.primary
                                                                )
                                                            }
                                                        }
                                                        Spacer(Modifier.width(10.dp))
                                                        Column(modifier = Modifier.weight(1f)) {
                                                            Text(
                                                                text = appName,
                                                                fontSize = 13.sp,
                                                                fontWeight = FontWeight.Medium,
                                                                color = MiuixTheme.colorScheme.onSurface,
                                                                maxLines = 1,
                                                                overflow = TextOverflow.Ellipsis
                                                            )
                                                            Text(
                                                                text = pkg,
                                                                fontSize = 11.sp,
                                                                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                                                maxLines = 1,
                                                                overflow = TextOverflow.Ellipsis
                                                            )
                                                        }
                                                        IconButton(
                                                            onClick = {
                                                                SyncSettings.removeFilterPackage(context, pkg)
                                                                filterPackages = SyncSettings.filterPackages(context)
                                                            }
                                                        ) {
                                                            Icon(
                                                                imageVector = MiuixIcons.Normal.Delete,
                                                                contentDescription = "移除",
                                                                tint = MiuixTheme.colorScheme.error
                                                            )
                                                        }
                                                    }
                                                }
                                                Spacer(Modifier.height(6.dp))
                                                Button(
                                                    onClick = {
                                                        SyncSettings.clearFilterPackages(context)
                                                        filterPackages = emptyList()
                                                        scope.launch { snackbarHostState.showAppSnack("已清空应用黑名单", SnackType.Info) }
                                                    },
                                                    colors = ButtonDefaults.buttonColors(
                                                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                                        contentColor = MiuixTheme.colorScheme.error
                                                    ),
                                                    modifier = Modifier.fillMaxWidth()
                                                ) {
                                                    Text("清空全部忽略应用")
                                                }
                                            }
                                        }
                                    }
                                }

                                // 2. 内容关键词黑名单
                                item {
                                    SectionBlock(title = "内容关键词黑名单") {
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
                                                        addKeywordState.clearText()
                                                        scope.launch { snackbarHostState.showAppSnack("已添加规则", SnackType.Success) }
                                                    }
                                                },
                                                colors = ButtonDefaults.buttonColorsPrimary()
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
                                                    text = "暂无关键词规则",
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
                                                        scope.launch { snackbarHostState.showAppSnack("已清空关键词规则", SnackType.Info) }
                                                    },
                                                    colors = ButtonDefaults.buttonColors(
                                                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                                        contentColor = MiuixTheme.colorScheme.error
                                                    ),
                                                    modifier = Modifier.fillMaxWidth()
                                                ) {
                                                    Text("清空全部关键词")
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    SettingsSubPage.SmartActions -> {
                        SmartActionSettingsPage(
                            bottomInnerPadding = bottomInnerPadding,
                            snackbarHostState = snackbarHostState,
                            onBack = { closeSubPage() }
                        )
                    }

                    SettingsSubPage.About -> {
                        // ---- 二级页面 3: 关于与开源致谢 ----
                        val openUrl: (String) -> Unit = { targetUrl ->
                            runCatching {
                                val intent = Intent(Intent.ACTION_VIEW, Uri.parse(targetUrl)).apply {
                                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                                }
                                context.startActivity(intent)
                            }.onFailure {
                                val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                cm.setPrimaryClip(ClipData.newPlainText("Link", targetUrl))
                                scope.launch { snackbarHostState.showAppSnack("链接已复制", SnackType.Success) }
                            }
                        }

                        val infiniteTransition = rememberInfiniteTransition(label = "update_spin")
                        val spinRotation by infiniteTransition.animateFloat(
                            initialValue = 0f,
                            targetValue = 360f,
                            animationSpec = infiniteRepeatable(
                                animation = tween(900, easing = LinearEasing)
                            ),
                            label = "spin_angle"
                        )

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
                                // 1. 应用品牌头部
                                item {
                                    Column(
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .padding(vertical = 12.dp),
                                        horizontalAlignment = Alignment.CenterHorizontally
                                    ) {
                                        val icon = remember { appIconBitmap(context) }
                                        Image(
                                            bitmap = icon,
                                            contentDescription = "NexClip 图标",
                                            modifier = Modifier
                                                .size(68.dp)
                                                .clip(RoundedCornerShape(18.dp))
                                        )
                                        Spacer(Modifier.height(10.dp))
                                        Text(
                                            text = "NexClip",
                                            fontSize = 22.sp,
                                            fontWeight = FontWeight.Bold,
                                            color = MiuixTheme.colorScheme.onSurface
                                        )
                                        Spacer(Modifier.height(4.dp))
                                        Text(
                                            text = "轻量高效的跨设备剪贴板同步与管理",
                                            fontSize = 13.sp,
                                            color = MiuixTheme.colorScheme.onBackgroundVariant
                                        )
                                        Spacer(Modifier.height(8.dp))
                                        Row(
                                            verticalAlignment = Alignment.CenterVertically,
                                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                                        ) {
                                            Row(
                                                verticalAlignment = Alignment.CenterVertically,
                                                modifier = Modifier
                                                    .clip(RoundedCornerShape(8.dp))
                                                    .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
                                                    .padding(horizontal = 10.dp, vertical = 3.dp)
                                            ) {
                                                Text(
                                                    text = "版本 v" + appVersion(context),
                                                    fontSize = 12.sp,
                                                    fontWeight = FontWeight.SemiBold,
                                                    color = MiuixTheme.colorScheme.primary
                                                )
                                            }

                                            Row(
                                                verticalAlignment = Alignment.CenterVertically,
                                                modifier = Modifier
                                                    .clip(RoundedCornerShape(8.dp))
                                                    .background(MiuixTheme.colorScheme.surfaceContainerHigh)
                                                    .clickable(enabled = !checkingUpdate) {
                                                        scope.launch {
                                                            checkingUpdate = true
                                                            val curVer = appVersion(context)
                                                            val res = clip.yixing.sync.util.UpdateChecker.check(curVer)
                                                            checkingUpdate = false
                                                            res.onSuccess { info ->
                                                                if (info.hasUpdate) {
                                                                    updateDialogInfo = info
                                                                } else {
                                                                    snackbarHostState.showAppSnack("当前已是最新版本 (v$curVer)", SnackType.Success)
                                                                }
                                                            }.onFailure { err ->
                                                                snackbarHostState.showAppSnack("检查更新失败: ${err.message ?: "网络错误"}", SnackType.Error)
                                                            }
                                                        }
                                                    }
                                                    .padding(horizontal = 10.dp, vertical = 3.dp)
                                            ) {
                                                Icon(
                                                    imageVector = LucideIcons.RefreshCw,
                                                    contentDescription = "检查更新",
                                                    tint = if (checkingUpdate) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant,
                                                    modifier = Modifier
                                                        .size(12.dp)
                                                        .rotate(if (checkingUpdate) spinRotation else 0f)
                                                )
                                                Spacer(Modifier.width(4.dp))
                                                Text(
                                                    text = if (checkingUpdate) "检查中..." else "检查更新",
                                                    fontSize = 12.sp,
                                                    color = if (checkingUpdate) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                                                )
                                            }
                                        }
                                    }
                                }

                                // 2. 项目信息
                                item {
                                    SectionBlock(title = "项目信息", insideMargin = PaddingValues()) {
                                        SwitchPreference(
                                            checked = autoCheckUpdate,
                                            onCheckedChange = { checked ->
                                                autoCheckUpdate = checked
                                                SyncSettings.setAutoCheckUpdate(context, checked)
                                            },
                                            title = "启动检查新版本"
                                        )
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .clickable(enabled = !checkingUpdate) {
                                                    scope.launch {
                                                        checkingUpdate = true
                                                        val curVer = appVersion(context)
                                                        val res = clip.yixing.sync.util.UpdateChecker.check(curVer)
                                                        checkingUpdate = false
                                                        res.onSuccess { info ->
                                                            if (info.hasUpdate) {
                                                                updateDialogInfo = info
                                                            } else {
                                                                snackbarHostState.showAppSnack("当前已是最新版本 (v$curVer)", SnackType.Success)
                                                            }
                                                        }.onFailure { err ->
                                                            snackbarHostState.showAppSnack("检查更新失败: ${err.message ?: "网络错误"}", SnackType.Error)
                                                        }
                                                    }
                                                }
                                                .padding(horizontal = 16.dp, vertical = 12.dp),
                                            horizontalArrangement = Arrangement.SpaceBetween,
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text("检查新版本", fontSize = 15.sp, color = MiuixTheme.colorScheme.onSurface)
                                            Row(verticalAlignment = Alignment.CenterVertically) {
                                                Text(
                                                    text = if (checkingUpdate) "正在检查更新..." else "v" + appVersion(context),
                                                    color = if (checkingUpdate) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant,
                                                    fontSize = 13.sp
                                                )
                                                Spacer(Modifier.width(4.dp))
                                                Icon(
                                                    imageVector = LucideIcons.RefreshCw,
                                                    contentDescription = "检查更新",
                                                    tint = if (checkingUpdate) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f),
                                                    modifier = Modifier
                                                        .size(13.dp)
                                                        .rotate(if (checkingUpdate) spinRotation else 0f)
                                                )
                                            }
                                        }
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .clickable { openUrl("https://github.com/yixing233/easy-clip") }
                                                .padding(horizontal = 16.dp, vertical = 12.dp),
                                            horizontalArrangement = Arrangement.SpaceBetween,
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text("开源项目仓库", fontSize = 15.sp, color = MiuixTheme.colorScheme.onSurface)
                                            Row(verticalAlignment = Alignment.CenterVertically) {
                                                Text(
                                                    text = "yixing233/easy-clip",
                                                    color = MiuixTheme.colorScheme.primary,
                                                    fontSize = 13.sp,
                                                    fontWeight = FontWeight.Medium
                                                )
                                                Spacer(Modifier.width(4.dp))
                                                Icon(
                                                    imageVector = LucideIcons.ExternalLink,
                                                    contentDescription = "前往",
                                                    tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.6f),
                                                    modifier = Modifier.size(13.dp)
                                                )
                                            }
                                        }
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .padding(horizontal = 16.dp, vertical = 12.dp),
                                            horizontalArrangement = Arrangement.SpaceBetween,
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text("开源许可证", fontSize = 15.sp, color = MiuixTheme.colorScheme.onSurface)
                                            Text("MIT License", fontSize = 13.sp, color = MiuixTheme.colorScheme.onBackgroundVariant)
                                        }
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .padding(horizontal = 16.dp, vertical = 12.dp),
                                            horizontalArrangement = Arrangement.SpaceBetween,
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text("应用包名", fontSize = 15.sp, color = MiuixTheme.colorScheme.onSurface)
                                            Text(context.packageName ?: "-", fontSize = 13.sp, color = MiuixTheme.colorScheme.onBackgroundVariant)
                                        }
                                    }
                                }

                                // 3. 开源依赖致谢
                                item {
                                    SectionBlock(title = "开源致谢与技术组件", insideMargin = PaddingValues()) {
                                        val libs = listOf(
                                            OpenSourceLib(
                                                name = "Miuix-KMP",
                                                license = "Apache-2.0",
                                                desc = "现代优雅的 MIUI / HyperOS 风格跨平台组件库",
                                                url = "https://github.com/miuix-kmp/miuix"
                                            ),
                                            OpenSourceLib(
                                                name = "LSPosed Framework",
                                                license = "GPL-3.0",
                                                desc = "Android 系统级 Xposed 模块框架与运行时 Hook",
                                                url = "https://github.com/LSPosed/LSPosed"
                                            ),
                                            OpenSourceLib(
                                                name = "Google ML Kit",
                                                license = "Apache-2.0",
                                                desc = "端侧离线高精度二维码与条形码识别套件",
                                                url = "https://developers.google.com/ml-kit/vision/barcode-scanning"
                                            ),
                                            OpenSourceLib(
                                                name = "Ktor Client",
                                                license = "Apache-2.0",
                                                desc = "JetBrains 异步协程网络通信与 WebSocket 客户端",
                                                url = "https://github.com/ktorio/ktor"
                                            ),
                                            OpenSourceLib(
                                                name = "Lucide Icons",
                                                license = "ISC",
                                                desc = "清晰规整的现代开源矢量图标规范与图形集",
                                                url = "https://github.com/lucide-icons/lucide"
                                            ),
                                            OpenSourceLib(
                                                name = "SyncClipboard",
                                                license = "MIT",
                                                desc = "跨设备多平台剪贴板数据同步协议与生态灵感",
                                                url = "https://github.com/Dupdate/SyncClipboard"
                                            )
                                        )

                                        libs.forEach { lib ->
                                            OpenSourceCreditRow(
                                                lib = lib,
                                                onClick = { openUrl(lib.url) }
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // ---- 对话框与弹层 ----

    // 发现新版本更新弹窗
    updateDialogInfo?.let { info ->
        AppUpdateDialog(
            info = info,
            snackbarHostState = snackbarHostState,
            onDismiss = { updateDialogInfo = null }
        )
    }

    // 配对对话框 (6 位纯数字配对码或扫码接入)
    WindowDialog(
        show = showPairDialog,
        title = "开始配对",
        summary = "输入 6 位配对码或点击下方「扫一扫」快速接入设备组",
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
                            dialogCodeState.clearText()
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
                colors = ButtonDefaults.buttonColorsPrimary(),
                modifier = Modifier.weight(1f)
            ) {
                Text(if (pairing) "连接中…" else "立即连接")
            }
        }
    }

    // 生成的配对码用对话框展示(关闭后立即失效)
    WindowDialog(
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
    WindowDialog(
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
                    colors = ButtonDefaults.buttonColorsPrimary(),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("保存")
                }
            }
        }
    }

    // 移除其他设备确认对话框
    val targetDev = deleteTargetDevice
    WindowDialog(
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

    // 应用忽略黑名单选择弹窗
    InstalledAppPickerDialog(
        show = showAppPickerDialog,
        blacklistedPackages = filterPackages,
        onTogglePackage = { pkg ->
            if (filterPackages.contains(pkg)) {
                SyncSettings.removeFilterPackage(context, pkg)
            } else {
                SyncSettings.addFilterPackage(context, pkg)
            }
            filterPackages = SyncSettings.filterPackages(context)
        },
        onDismissRequest = { showAppPickerDialog = false }
    )
}

private fun platformIcon(name: String, platform: String): androidx.compose.ui.graphics.vector.ImageVector {
    return resolveDeviceIcon(name, platform)
}

@Composable
private fun DeviceCard(
    device: DeviceInfo,
    isSelf: Boolean,
    isServerConnected: Boolean = false,
    onDeleteClick: (DeviceInfo) -> Unit,
    onCopyId: (String) -> Unit
) {
    val isActuallyOnline = if (isSelf) (isServerConnected || device.online) else device.online
    val lastSeen = relativeTime(device.lastSeenAt)
    val statusText = if (isActuallyOnline) "在线" else if (lastSeen.isNotEmpty()) lastSeen else "离线"
    val statusColor = if (isActuallyOnline) Color(0xFF10B981) else MiuixTheme.colorScheme.onBackgroundVariant

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
                imageVector = resolveDeviceIcon(device.name, device.platform),
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

/** 尝试拉起 LSPosed / Xposed / 模块管理器与 Root 管理器应用 */
private fun openLsposedManager(context: Context): Boolean {
    val pm = context.packageManager

    // 1. 优先尝试显式组件调用（可绕过部分 ROM 的隐式过滤器）
    val explicitComponents = listOf(
        ComponentName("org.lsposed.manager", "org.lsposed.manager.ui.activity.MainActivity"),
        ComponentName("org.lsposed.manager", "org.lsposed.manager.ui.activity.ComposeActivity"),
        ComponentName("org.lsposed.manager.v1", "org.lsposed.manager.ui.activity.MainActivity"),
        ComponentName("io.github.lsposed.manager", "io.github.lsposed.manager.ui.activity.MainActivity"),
        ComponentName("org.lsposed.manager.zygisk", "org.lsposed.manager.ui.activity.MainActivity"),
        ComponentName("org.meowcat.edxposed.manager", "org.meowcat.edxposed.manager.ui.MainActivity"),
        ComponentName("de.robv.android.xposed.installer", "de.robv.android.xposed.installer.WelcomeActivity"),
        ComponentName("top.canyie.dreamland.manager", "top.canyie.dreamland.manager.ui.MainActivity"),
        ComponentName("me.weishu.kernelsu", "me.weishu.kernelsu.ui.MainActivity"),
        ComponentName("com.rifsxd.ksu", "me.weishu.kernelsu.ui.MainActivity"),
        ComponentName("me.bmax.apatch", "me.bmax.apatch.ui.MainActivity"),
        ComponentName("com.topjohnwu.magisk", "com.topjohnwu.magisk.ui.MainActivity"),
        ComponentName("io.github.huskydg.magisk", "com.topjohnwu.magisk.ui.MainActivity")
    )

    for (comp in explicitComponents) {
        try {
            val intent = Intent(Intent.ACTION_MAIN).apply {
                component = comp
                addCategory(Intent.CATEGORY_LAUNCHER)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
            return true
        } catch (_: Exception) {
            // 继续尝试下一个
        }
    }

    // 2. 尝试根据包名获取 Launch Intent
    val candidatePackages = listOf(
        "org.lsposed.manager",
        "org.lsposed.manager.v1",
        "io.github.lsposed.manager",
        "org.lsposed.manager.zygisk",
        "org.meowcat.edxposed.manager",
        "de.robv.android.xposed.installer",
        "top.canyie.dreamland.manager",
        "me.weishu.kernelsu",
        "com.rifsxd.ksu",
        "me.bmax.apatch",
        "com.topjohnwu.magisk",
        "io.github.huskydg.magisk"
    )

    for (pkg in candidatePackages) {
        val launchIntent = pm.getLaunchIntentForPackage(pkg)
        if (launchIntent != null) {
            launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            val result = runCatching { context.startActivity(launchIntent) }
            if (result.isSuccess) return true
        }
    }

    // 3. 尝试通用 LSPosed 动作 / URI
    val actionIntents = listOf(
        Intent("org.lsposed.manager.LAUNCH").apply { addFlags(Intent.FLAG_ACTIVITY_NEW_TASK) },
        Intent("android.intent.action.APPLICATION_PREFERENCES").apply {
            `package` = "org.lsposed.manager"
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        },
        Intent(Intent.ACTION_VIEW, Uri.parse("lsposed://manager")).apply { addFlags(Intent.FLAG_ACTIVITY_NEW_TASK) },
        Intent(Intent.ACTION_VIEW, Uri.parse("lsposed://module/${context.packageName}")).apply { addFlags(Intent.FLAG_ACTIVITY_NEW_TASK) }
    )

    for (intent in actionIntents) {
        val result = runCatching { context.startActivity(intent) }
        if (result.isSuccess) return true
    }

    return false
}

/** 尝试拉起 Shizuku 管理器应用 */
private fun openShizukuManager(context: Context): Boolean {
    val pm = context.packageManager
    val explicitComponents = listOf(
        ComponentName("moe.shizuku.privileged.api", "moe.shizuku.manager.MainActivity"),
        ComponentName("moe.shizuku.privileged.api", "moe.shizuku.privileged.api.MainActivity")
    )
    for (comp in explicitComponents) {
        try {
            val intent = Intent(Intent.ACTION_MAIN).apply {
                component = comp
                addCategory(Intent.CATEGORY_LAUNCHER)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
            return true
        } catch (_: Exception) {
        }
    }
    val launchIntent = pm.getLaunchIntentForPackage("moe.shizuku.privileged.api")
    if (launchIntent != null) {
        launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return runCatching { context.startActivity(launchIntent); true }.getOrDefault(false)
    }
    return false
}

/** 开源依赖组件实体模型 */
private data class OpenSourceLib(
    val name: String,
    val license: String,
    val desc: String,
    val url: String
)

/** 开源致谢单行列表条目组件 */
@Composable
private fun OpenSourceCreditRow(
    lib: OpenSourceLib,
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
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Text(
                    text = lib.name,
                    fontSize = 15.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = MiuixTheme.colorScheme.onSurface
                )
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainerHigh)
                        .padding(horizontal = 6.dp, vertical = 1.dp)
                ) {
                    Text(
                        text = lib.license,
                        fontSize = 10.sp,
                        fontWeight = FontWeight.Medium,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            }
            Spacer(Modifier.height(3.dp))
            Text(
                text = lib.desc,
                fontSize = 12.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                lineHeight = 16.sp
            )
        }
        Spacer(Modifier.width(8.dp))
        Icon(
            imageVector = LucideIcons.ExternalLink,
            contentDescription = "查看项目主页",
            tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f),
            modifier = Modifier.size(14.dp)
        )
    }
}

/**
 * 超级岛流光呼吸灯横向彩色小色块选择胶囊栏
 */
@Composable
private fun GlowColorPaletteChips(
    selectedColor: String,
    onColorSelected: (String) -> Unit
) {
    val glowColors = remember { SyncSettings.GLOW_COLORS }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 10.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "流光呼吸灯颜色",
                fontSize = 15.sp,
                fontWeight = FontWeight.Medium,
                color = MiuixTheme.colorScheme.onSurface
            )
            val currentLabel = glowColors.firstOrNull { it.first.equals(selectedColor, ignoreCase = true) }?.second ?: "自定义"
            Text(
                text = currentLabel,
                fontSize = 13.sp,
                color = MiuixTheme.colorScheme.primary,
                fontWeight = FontWeight.SemiBold
            )
        }
        Spacer(Modifier.height(10.dp))
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            glowColors.forEach { (colorHex, colorName) ->
                val parsedColor = remember(colorHex) {
                    try {
                        Color(android.graphics.Color.parseColor(colorHex))
                    } catch (_: Exception) {
                        Color(0xFF006EFF)
                    }
                }
                val isSelected = selectedColor.equals(colorHex, ignoreCase = true)
                Box(
                    modifier = Modifier
                        .size(40.dp)
                        .clip(CircleShape)
                        .background(parsedColor)
                        .border(
                            width = if (isSelected) 3.dp else 1.5.dp,
                            color = if (isSelected) MiuixTheme.colorScheme.onSurface.copy(alpha = 0.85f) else Color.White.copy(alpha = 0.35f),
                            shape = CircleShape
                        )
                        .clickable { onColorSelected(colorHex) },
                    contentAlignment = Alignment.Center
                ) {
                    if (isSelected) {
                        Icon(
                            imageVector = LucideIcons.Check,
                            contentDescription = colorName,
                            tint = Color.White,
                            modifier = Modifier.size(20.dp)
                        )
                    }
                }
            }
        }
    }
}

/** 已安装应用条目模型 */
private data class InstalledAppItem(
    val name: String,
    val packageName: String,
    val iconBitmap: androidx.compose.ui.graphics.ImageBitmap?
)

/** 加载已安装桌面启动应用列表 */
private fun loadInstalledApps(context: Context): List<InstalledAppItem> {
    val pm = context.packageManager
    val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
    val list = pm.queryIntentActivities(intent, 0)
    return list.mapNotNull { resolveInfo ->
        try {
            val pkg = resolveInfo.activityInfo.packageName
            val label = resolveInfo.loadLabel(pm).toString()
            val drawable = resolveInfo.loadIcon(pm)
            val bmp = if (drawable != null) {
                val width = drawable.intrinsicWidth.coerceAtLeast(1).coerceAtMost(96)
                val height = drawable.intrinsicHeight.coerceAtLeast(1).coerceAtMost(96)
                val bitmap = android.graphics.Bitmap.createBitmap(width, height, android.graphics.Bitmap.Config.ARGB_8888)
                val canvas = android.graphics.Canvas(bitmap)
                drawable.setBounds(0, 0, canvas.width, canvas.height)
                drawable.draw(canvas)
                bitmap.asImageBitmap()
            } else null
            InstalledAppItem(name = label, packageName = pkg, iconBitmap = bmp)
        } catch (_: Exception) {
            null
        }
    }.distinctBy { it.packageName }.sortedBy { it.name.lowercase() }
}

/** 从已安装应用中挑选忽略黑名单弹窗 */
@Composable
private fun InstalledAppPickerDialog(
    show: Boolean,
    blacklistedPackages: List<String>,
    onTogglePackage: (String) -> Unit,
    onDismissRequest: () -> Unit
) {
    if (!show) return
    val context = LocalContext.current
    var searchQuery by remember { mutableStateOf("") }
    var installedApps by remember { mutableStateOf<List<InstalledAppItem>>(emptyList()) }
    var isLoading by remember { mutableStateOf(true) }

    LaunchedEffect(Unit) {
        withContext(Dispatchers.IO) {
            installedApps = loadInstalledApps(context)
            isLoading = false
        }
    }

    val filteredApps = remember(installedApps, searchQuery) {
        if (searchQuery.isBlank()) installedApps
        else installedApps.filter {
            it.name.contains(searchQuery, ignoreCase = true) ||
            it.packageName.contains(searchQuery, ignoreCase = true)
        }
    }

    WindowDialog(
        show = show,
        title = "选择要忽略的应用",
        onDismissRequest = onDismissRequest
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp)
        ) {
            Text(
                text = "已勾选的应用在产生剪贴板复制时，将被自动跳过捕获与多端同步。",
                fontSize = 12.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                lineHeight = 16.sp
            )
            Spacer(Modifier.height(10.dp))

            // 搜索框
            val searchState = remember { TextFieldState() }
            LaunchedEffect(searchState.text) {
                searchQuery = searchState.text.toString().trim()
            }
            TextField(
                state = searchState,
                label = "搜索应用名称或包名…",
                useLabelAsPlaceholder = true,
                modifier = Modifier.fillMaxWidth()
            )

            Spacer(Modifier.height(10.dp))

            if (isLoading) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(200.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = "正在读取已安装应用列表…",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            } else if (filteredApps.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(160.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = if (searchQuery.isBlank()) "未检测到已安装应用" else "未找到匹配应用",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = 300.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp)
                ) {
                    items(filteredApps, key = { it.packageName }) { app ->
                        val isChecked = blacklistedPackages.contains(app.packageName)
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .background(
                                    if (isChecked) MiuixTheme.colorScheme.primary.copy(alpha = 0.10f)
                                    else MiuixTheme.colorScheme.surfaceContainer
                                )
                                .clickable { onTogglePackage(app.packageName) }
                                .padding(horizontal = 12.dp, vertical = 8.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            if (app.iconBitmap != null) {
                                Image(
                                    bitmap = app.iconBitmap,
                                    contentDescription = app.name,
                                    modifier = Modifier
                                        .size(36.dp)
                                        .clip(RoundedCornerShape(8.dp))
                                )
                            } else {
                                Box(
                                    modifier = Modifier
                                        .size(36.dp)
                                        .clip(RoundedCornerShape(8.dp))
                                        .background(MiuixTheme.colorScheme.surfaceContainerHigh),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Icon(
                                        imageVector = LucideIcons.Smartphone,
                                        contentDescription = app.name,
                                        modifier = Modifier.size(20.dp),
                                        tint = MiuixTheme.colorScheme.primary
                                    )
                                }
                            }
                            Spacer(Modifier.width(10.dp))
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = app.name,
                                    fontSize = 14.sp,
                                    fontWeight = FontWeight.Medium,
                                    color = MiuixTheme.colorScheme.onSurface,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                                Text(
                                    text = app.packageName,
                                    fontSize = 11.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                            }
                            Spacer(Modifier.width(8.dp))
                            Box(
                                modifier = Modifier
                                    .size(24.dp)
                                    .clip(CircleShape)
                                    .background(
                                        if (isChecked) MiuixTheme.colorScheme.primary
                                        else MiuixTheme.colorScheme.surfaceContainerHigh
                                    ),
                                contentAlignment = Alignment.Center
                            ) {
                                if (isChecked) {
                                    Icon(
                                        imageVector = LucideIcons.Check,
                                        contentDescription = "已勾选",
                                        tint = Color.White,
                                        modifier = Modifier.size(14.dp)
                                    )
                                }
                            }
                        }
                    }
                }
            }

            Spacer(Modifier.height(14.dp))
            Button(
                onClick = onDismissRequest,
                colors = ButtonDefaults.buttonColorsPrimary(),
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("完成")
            }
        }
    }
}

private fun Context.findMainActivity(): MainActivity? = generateSequence(this) {
    (it as? ContextWrapper)?.baseContext
}.filterIsInstance<MainActivity>().firstOrNull()
