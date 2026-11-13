package clip.yixing.sync

import android.app.ActivityManager
import android.content.Intent
import android.os.Bundle
import android.content.pm.PackageManager
import androidx.activity.BackEventCompat
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.PredictiveBackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.EaseInOut
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.add
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PagerState
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import kotlinx.coroutines.CancellationException
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigationevent.compose.LocalNavigationEventDispatcherOwner
import androidx.navigationevent.compose.rememberNavigationEventDispatcherOwner
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.service.SyncNotificationManager
import clip.yixing.sync.ui.AppUpdateDialog
import clip.yixing.sync.ui.BottomBarIcons
import clip.yixing.sync.ui.HomePage
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.ui.ManualPushPage
import clip.yixing.sync.ui.FilesPage
import clip.yixing.sync.ui.RecordsPage
import clip.yixing.sync.ui.SearchPage
import clip.yixing.sync.ui.BarBlurSurface
import clip.yixing.sync.ui.PageShell
import clip.yixing.sync.ui.nav.PredictiveNavigationBackHandler
import clip.yixing.sync.ui.nav.PredictiveNavigationLayer
import clip.yixing.sync.ui.nav.predictiveNavigationBackground
import clip.yixing.sync.ui.nav.rememberPredictiveNavigationLayerState
import clip.yixing.sync.ui.FloatingBottomBar
import clip.yixing.sync.ui.FloatingBottomBarItem
import clip.yixing.sync.ui.SettingsPage
import clip.yixing.sync.ui.scan.QrScanPage
import clip.yixing.sync.ui.theme.NexClipTheme
import clip.yixing.sync.util.SyncSettings
import clip.yixing.sync.util.UpdateInfo
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.DropdownEntry
import top.yukonga.miuix.kmp.basic.DropdownItem
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.MiuixScrollBehavior
import top.yukonga.miuix.kmp.basic.NavigationBar
import top.yukonga.miuix.kmp.basic.NavigationBarDisplayMode
import top.yukonga.miuix.kmp.basic.NavigationBarItem

import top.yukonga.miuix.kmp.basic.Scaffold
import top.yukonga.miuix.kmp.basic.Snackbar
import top.yukonga.miuix.kmp.basic.SnackbarDefaults
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.SnackbarHost
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.SnackbarResult
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.blur.BlendColorEntry
import top.yukonga.miuix.kmp.blur.BlurBlendMode
import top.yukonga.miuix.kmp.blur.BlurDefaults
import top.yukonga.miuix.kmp.blur.LayerBackdrop
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.blur.textureBlur
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.menu.WindowIconDropdownMenu
import top.yukonga.miuix.kmp.icon.basic.ArrowUpDown
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.Home
import top.yukonga.miuix.kmp.icon.extended.More
import top.yukonga.miuix.kmp.icon.extended.Search
import top.yukonga.miuix.kmp.icon.extended.Notes
import top.yukonga.miuix.kmp.icon.extended.Settings
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollHorizontal
import top.yukonga.miuix.kmp.utils.overScrollVertical
import android.os.Build
import androidx.compose.runtime.DisposableEffect
import androidx.navigationevent.OnBackInvokedOverlayInput
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        updateRecentsVisibility(SyncSettings.isHideFromRecents(this))
        // 恢复本地剪贴板记录;按设置自动恢复监听
        ClipboardMonitorService.loadCaptured(applicationContext)
        if (SyncSettings.bootStartEnabled(this)) {
            ClipboardMonitorService.start(this)
        }
        // 通知点按(open_files_page)或系统分享(SEND/SEND_MULTIPLE)进入时,
        // 提取 intent 信号传给 MainScreen 的初始 state —— compose 未就绪前
        // 不能命令式调 openFilesPage(),state 初值是唯一可靠路径。
        launchIntentSignal = extractIntentSignal(intent)
        setContent {
            // 气泡菜单/弹窗等组件依赖 NavigationEventDispatcher 处理返回手势,
            // 需在根节点提供 DispatcherOwner, 并挂载 OnBackInvokedOverlayInput 接收系统返回事件。
            val dispatcherOwner = rememberNavigationEventDispatcherOwner(
                enabled = true,
                parent = null,
            )
            DisposableEffect(dispatcherOwner) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    val input = OnBackInvokedOverlayInput(onBackInvokedDispatcher)
                    dispatcherOwner.navigationEventDispatcher.addInput(input)
                    onDispose {
                        dispatcherOwner.navigationEventDispatcher.removeInput(input)
                    }
                } else {
                    onDispose { }
                }
            }
            CompositionLocalProvider(
                LocalNavigationEventDispatcherOwner provides dispatcherOwner
            ) {
                NexClipTheme {
                    MainScreen(
                        initialOpenFiles = launchIntentSignal.openFiles,
                        initialShareUris = launchIntentSignal.shareUris,
                    )
                }
            }
        }
    }

    /** onCreate/onNewIntent 提取的启动信号(打开文件页 / 待上传分享 Uri) */
    data class LaunchIntentSignal(val openFiles: Boolean, val shareUris: List<android.net.Uri>)

    @Volatile
    private var launchIntentSignal: LaunchIntentSignal = LaunchIntentSignal(false, emptyList())

    /** 从 intent 提取文件页启动信号(通知点按 / 分享) */
    private fun extractIntentSignal(intent: Intent?): LaunchIntentSignal {
        if (intent == null) return LaunchIntentSignal(false, emptyList())
        val shareUris = when (intent.action) {
            Intent.ACTION_SEND ->
                @Suppress("DEPRECATION")
                listOfNotNull(intent.getParcelableExtra<android.net.Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE ->
                @Suppress("DEPRECATION")
                intent.getParcelableArrayListExtra<android.net.Uri>(Intent.EXTRA_STREAM)?.filterNotNull().orEmpty()
            else -> emptyList()
        }
        val openFiles = intent.getBooleanExtra(SyncNotificationManager.EXTRA_OPEN_FILES_PAGE, false) || shareUris.isNotEmpty()
        return LaunchIntentSignal(openFiles, shareUris)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        val signal = extractIntentSignal(intent)
        // 热路径:compose 树已在,直接把信号交给 MainScreen 的回调
        pendingIntentSignal?.invoke(signal)
    }

    /** MainScreen 组合后注册;onNewIntent 通过它把信号转发进 compose 状态 */
    @Volatile
    var pendingIntentSignal: ((LaunchIntentSignal) -> Unit)? = null

    override fun onResume() {
        super.onResume()
        updateRecentsVisibility(SyncSettings.isHideFromRecents(this))
        clip.yixing.sync.paste.FloatingBubbleService.sync(this)
    }

    fun updateRecentsVisibility(hideFromRecents: Boolean) {
        val activityManager = getSystemService(ActivityManager::class.java)
        activityManager?.appTasks
            ?.firstOrNull { it.taskInfo?.taskId == taskId }
            ?.setExcludeFromRecents(hideFromRecents)
    }
}

val cardContentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp)

/**
 * 底栏页面切换动画,与 KernelSU 一致:
 * 按目标页距离计算时长,用 EaseInOut 缓动滑动整个页面(底栏除外)。
 */
private class PagerNavigator(private val scope: CoroutineScope) {

    private var navJob: Job? = null
    var isProgrammaticScroll by mutableStateOf(false)
        private set

    fun animateTo(pagerState: PagerState, targetIndex: Int) {
        if (targetIndex == pagerState.currentPage) return

        navJob?.cancel()

        val distance = abs(targetIndex - pagerState.currentPage).coerceAtLeast(2)
        val duration = 100 * distance + 100

        isProgrammaticScroll = true
        navJob = scope.launch {
            try {
                pagerState.animateScrollToPage(
                    page = targetIndex,
                    animationSpec = tween(easing = EaseInOut, durationMillis = duration)
                )
            } finally {
                isProgrammaticScroll = false
            }
        }
    }
}

@Composable
private fun MainScreen(initialOpenFiles: Boolean = false, initialShareUris: List<android.net.Uri> = emptyList()) {
    val pagerState = rememberPagerState(pageCount = { 3 })
    val scope = rememberCoroutineScope()
    val pagerNavigator = remember { PagerNavigator(scope) }
    var searchOpen by remember { mutableStateOf(false) }
    var searchQuery by remember { mutableStateOf("") }
    val snackbarHostState = remember { SnackbarHostState() }
    // 底栏选中索引:点击立即驱动(指示器马上跟随目标页),用户手势滑动时实时同步当前页(屏蔽跨页跳转动画过程中的中间瞬态)
    var tabIndex by remember { mutableIntStateOf(pagerState.currentPage) }
    LaunchedEffect(pagerState, pagerNavigator) {
        snapshotFlow {
            if (pagerNavigator.isProgrammaticScroll) null else pagerState.currentPage
        }.collectLatest { page ->
            if (page != null) {
                tabIndex = page
            }
        }
    }
    val appContext = LocalContext.current
    var floatingBar by remember { mutableStateOf(SyncSettings.floatingBottomBarEnabled(appContext)) }
    var isOverlayActiveHome by remember { mutableStateOf(false) }
    var isOverlayActiveRecords by remember { mutableStateOf(false) }
    var isOverlayActiveSettings by remember { mutableStateOf(false) }
    var isScanOpen by remember { mutableStateOf(false) }
    // 覆盖层转场统一到 HyperIsland 的层级模型(见 ui/nav/PredictiveNavigation.kt):
    // 进入整屏右滑入 + 底层下沉,返回跟手并按释放速度结算。
    val scanNavState = rememberPredictiveNavigationLayerState()
    // 直接读,不用 remember 缓存 —— 否则在设置页改完开关回到主页仍是旧值。
    val predictiveBackOn = SyncSettings.predictiveBackEnabled(appContext)
    val predictiveBackMaxPercent = SyncSettings.predictiveBackMaxProgressPercent(appContext)

    var isManualPushOpen by remember { mutableStateOf(false) }
    val manualPushNavState = rememberPredictiveNavigationLayerState()

    // 文件互传覆盖页:初始值来自 intent(通知点按/系统分享),冷启动直接进入
    var isFilesOpen by remember { mutableStateOf(initialOpenFiles) }
    val filesNavState = rememberPredictiveNavigationLayerState()
    // 待上传的分享 Uri(系统分享进入时非空,FilesPage 消费后回调清空)
    var pendingShareUris = remember { mutableStateListOf<android.net.Uri>().apply { addAll(initialShareUris) } }

    // 覆盖层互斥打开,底层跟随"当前活着的那一层"下沉。
    val overlayDepth: () -> Float = {
        maxOf(
            scanNavState.backgroundDepth.value,
            manualPushNavState.backgroundDepth.value,
            filesNavState.backgroundDepth.value,
        )
    }
    val overlayBackdropIntensity = maxOf(
        scanNavState.backdropIntensity.value,
        manualPushNavState.backdropIntensity.value,
        filesNavState.backdropIntensity.value,
    )

    // 启动检查发现新版本后，点击 snackbar「更新」直接在应用内下载安装
    var updateDialogInfo by remember { mutableStateOf<UpdateInfo?>(null) }

    LaunchedEffect(Unit) {
        if (SyncSettings.autoCheckUpdate(appContext)) {
            delay(1500)
            val info = runCatching {
                val curVer = try {
                    val pInfo = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        appContext.packageManager.getPackageInfo(appContext.packageName, PackageManager.PackageInfoFlags.of(0L))
                    } else {
                        @Suppress("DEPRECATION")
                        appContext.packageManager.getPackageInfo(appContext.packageName, 0)
                    }
                    pInfo.versionName ?: "20261005.01"
                } catch (_: Exception) {
                    "20261005.01"
                }
                clip.yixing.sync.util.UpdateChecker.check(
                    currentVersion = curVer,
                    updateSource = SyncSettings.updateSource(appContext),
                    serverUrl = SyncSettings.serverUrl(appContext)
                ).getOrNull()
            }.getOrNull()
            if (info != null && info.hasUpdate) {
                val res = snackbarHostState.showAppSnack(
                    message = "发现新版本 v${info.latestVersion}",
                    type = SnackType.Info,
                    actionLabel = "更新"
                )
                if (res == SnackbarResult.ActionPerformed) {
                    updateDialogInfo = info
                }
            }
        }
    }

    val isGlobalOverlayActive = isScanOpen || isManualPushOpen || isFilesOpen || searchOpen
    val isAnyOverlayActive = isOverlayActiveHome || isOverlayActiveRecords || isOverlayActiveSettings || isGlobalOverlayActive

    // onNewIntent(通知点按/分享热路径) → compose 状态:
    // Activity 侧通过 pendingIntentSignal 回调转发;冷启动路径走 MainScreen 初始参数,无需补发。
    val activity = appContext as? MainActivity
    DisposableEffect(activity) {
        val forward: (MainActivity.LaunchIntentSignal) -> Unit = { signal ->
            if (signal.openFiles) isFilesOpen = true
            pendingShareUris.clear()
            pendingShareUris.addAll(signal.shareUris)
        }
        activity?.pendingIntentSignal = forward
        onDispose { activity?.pendingIntentSignal = null }
    }

    fun openScanner() {
        isScanOpen = true
    }

    fun closeScanner() {
        isScanOpen = false
    }

    fun openManualPush() {
        isManualPushOpen = true
    }

    fun closeManualPush() {
        isManualPushOpen = false
    }

    fun openFilesPage() {
        isFilesOpen = true
    }

    fun closeFilesPage() {
        isFilesOpen = false
        pendingShareUris.clear()
    }

    if (predictiveBackOn) {
        PredictiveNavigationBackHandler(
            enabled = isScanOpen,
            state = scanNavState,
            maxTranslationPercent = predictiveBackMaxPercent.toLong(),
            onDismiss = { isScanOpen = false },
        )
        PredictiveNavigationBackHandler(
            enabled = isManualPushOpen,
            state = manualPushNavState,
            maxTranslationPercent = predictiveBackMaxPercent.toLong(),
            onDismiss = { isManualPushOpen = false },
        )
        PredictiveNavigationBackHandler(
            enabled = isFilesOpen,
            state = filesNavState,
            maxTranslationPercent = predictiveBackMaxPercent.toLong(),
            onDismiss = { closeFilesPage() },
        )
    } else {
        BackHandler(enabled = isScanOpen) { isScanOpen = false }
        BackHandler(enabled = isManualPushOpen) { isManualPushOpen = false }
        BackHandler(enabled = isFilesOpen) { closeFilesPage() }
    }

    // 单个 backdrop:录制外层 content(Pager 整体,含各页大标题顶栏),底栏 blur
    // 表面读取它 —— 录制范围不含任何 blur 表面(顶栏在页面内无模糊,与 KernelSU
    // 一致),避免 GraphicsLayer 自引用导致的 hwui native 崩溃;且录制节点与底栏
    // 同属外层 Scaffold 坐标系,不存在 Pager 页面 graphicsLayer 平移导致的错位。
    val barSurface = MiuixTheme.colorScheme.surface
    val backdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )
    val currentPage = pagerState.currentPage

    Box(Modifier.fillMaxSize()) {
        // 内容全屏铺到屏幕底部(不预留底栏槽位),底栏以 overlay 悬浮其上:
        // 这样 backdrop 录制的内容覆盖底栏区域,液态玻璃才能折射/模糊到底栏下方的页面。
        Scaffold {
        padding ->
        val bottomInnerPadding = padding.calculateBottomPadding() + if (floatingBar) 88.dp else 64.dp
        // 整页(含大标题顶栏)在 Pager 内滑动切换,与 KernelSU 一致。
        // 覆盖层打开时整页下沉(缩到 96.5% + 左移 2.5%),由 PredictiveNavigation 驱动。
        Box(
            modifier = Modifier
                .fillMaxSize()
                .predictiveNavigationBackground(overlayDepth)
                .layerBackdrop(backdrop)
        ) {
            HorizontalPager(
                state = pagerState,
                modifier = Modifier
                    .fillMaxSize()
                    .overScrollHorizontal(),
                beyondViewportPageCount = 3,
                userScrollEnabled = !isAnyOverlayActive
            ) { page ->
                when (page) {
                    0 -> PageShell(
                        title = "NexClip",
                        bottomInnerPadding = bottomInnerPadding,
                        actions = {
                            IconButton(onClick = { openManualPush() }) {
                                Icon(
                                    imageVector = LucideIcons.Send,
                                    contentDescription = "即时互传",
                                    tint = MiuixTheme.colorScheme.onSurface
                                )
                            }
                            IconButton(onClick = { openFilesPage() }) {
                                Icon(
                                    imageVector = LucideIcons.FolderOpen,
                                    contentDescription = "文件互传",
                                    tint = MiuixTheme.colorScheme.onSurface
                                )
                            }
                            IconButton(onClick = { openScanner() }) {
                                Icon(
                                    imageVector = LucideIcons.ScanLine,
                                    contentDescription = "扫一扫",
                                    tint = MiuixTheme.colorScheme.onSurface
                                )
                            }
                        }
                    ) { scrollBehavior, topPadding ->
                        HomePage(
                            scrollBehavior = scrollBehavior,
                            topPadding = topPadding,
                            bottomInnerPadding = bottomInnerPadding,
                            snackbarHostState = snackbarHostState,
                            onNavigateToRecords = {
                                tabIndex = 1
                                pagerNavigator.animateTo(pagerState, 1)
                            },
                            onNavigateToSettings = {
                                tabIndex = 2
                                pagerNavigator.animateTo(pagerState, 2)
                            },
                            onOpenQrScanner = { openScanner() },
                            onOpenManualPush = { openManualPush() },
                            onOverlayActiveChanged = { isOverlayActiveHome = it }
                        )
                    }
                    1 -> RecordsPage(
                        bottomInnerPadding = bottomInnerPadding,
                        snackbarHostState = snackbarHostState,
                        onOpenSearch = {
                            searchQuery = ""
                            searchOpen = true
                        },
                        onOverlayActiveChanged = { isOverlayActiveRecords = it },
                        isGlobalOverlayActive = isGlobalOverlayActive
                    )
                    else -> SettingsPage(
                        bottomInnerPadding = bottomInnerPadding,
                        snackbarHostState = snackbarHostState,
                        floatingBarEnabled = floatingBar,
                        onFloatingBarChange = {
                            floatingBar = it
                            SyncSettings.setFloatingBottomBarEnabled(appContext, it)
                        },
                        onOverlayActiveChanged = { isOverlayActiveSettings = it },
                        onOpenQrScanner = { openScanner() },
                        isGlobalOverlayActive = isGlobalOverlayActive
                    )
                }
            }
        }

        // 覆盖层(扫码/即时互传)打开时,给底下的 Pager 压一层暗化。
        // 两层的强度取较大者 —— 两者互斥,同一时刻只有一层真正活着。
        if (overlayBackdropIntensity > 0.001f) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(
                        MiuixTheme.colorScheme.windowDimming.copy(alpha = 0.16f * overlayBackdropIntensity)
                    )
            )
        }

        // 全屏扫码配对页:整屏右滑入,底层下沉,返回跟手并按释放速度结算
        PredictiveNavigationLayer(
            visible = isScanOpen,
            state = scanNavState,
            maxTranslationPercent = predictiveBackMaxPercent.toLong(),
        ) {
            QrScanPage(
                snackbarHostState = snackbarHostState,
                onBack = { closeScanner() },
                onPairSuccess = {
                    closeScanner()
                    tabIndex = 0
                    pagerNavigator.animateTo(pagerState, 0)
                }
            )
        }

        // 全屏跨设备即时互传页:同上
        PredictiveNavigationLayer(
            visible = isManualPushOpen,
            state = manualPushNavState,
            maxTranslationPercent = predictiveBackMaxPercent.toLong(),
        ) {
            ManualPushPage(
                snackbarHostState = snackbarHostState,
                onBack = { closeManualPush() }
            )
        }

        // 全屏文件互传页:同上;系统分享进入时带 pendingShareUris 自动上传
        PredictiveNavigationLayer(
            visible = isFilesOpen,
            state = filesNavState,
            maxTranslationPercent = predictiveBackMaxPercent.toLong(),
        ) {
            FilesPage(
                snackbarHostState = snackbarHostState,
                pendingShareUris = pendingShareUris.toList(),
                onShareConsumed = { pendingShareUris.clear() },
                onBack = { closeFilesPage() }
            )
        }
    }

    // Snackbar: 修复配色与操作按钮文字可见性 (操作按钮蓝底白字, 错误提示红字, 成功/普通白底黑字)
    SnackbarHost(
        state = snackbarHostState,
        modifier = Modifier
            .align(Alignment.BottomCenter)
            // 避开底栏:悬浮条(108dp)或普通贴底条(80dp + 手势区)
            .padding(
                bottom = if (floatingBar) {
                    108.dp
                } else {
                    80.dp + WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
                },
                start = 16.dp,
                end = 16.dp
            )
    ) { data ->
        val scheme = MiuixTheme.colorScheme
        val type = SnackTypeStore.current
        val contentColor = when (type) {
            SnackType.Error -> scheme.error
            else -> scheme.onSurface
        }
        Snackbar(
            data = data,
            colors = SnackbarDefaults.snackbarColors(
                containerColor = scheme.surfaceContainerHigh,
                contentColor = contentColor,
                actionContainerColor = scheme.primary,
                actionContentColor = Color.White,
                dismissActionContentColor = scheme.onBackgroundVariant.copy(alpha = 0.8f),
            )
        )
    }

    // 启动检查发现新版本 → snackbar 点「更新」后弹出应用内下载安装弹窗
    updateDialogInfo?.let { info ->
        AppUpdateDialog(
            info = info,
            snackbarHostState = snackbarHostState,
            onDismiss = { updateDialogInfo = null }
        )
    }

    // 底栏:悬浮(液态玻璃)或普通贴底,由设置开关控制,默认开启
    // 当全屏搜索或任一弹层/对话框/多选操作/扫码激活时平滑隐藏,防止遮挡弹层内容与操作按钮
    AnimatedVisibility(
        visible = !searchOpen && !isAnyOverlayActive && !isScanOpen && !isManualPushOpen && !isFilesOpen,
        enter = fadeIn(tween(180)) + slideInVertically(initialOffsetY = { it }, animationSpec = tween(180)),
        exit = fadeOut(tween(180)) + slideOutVertically(targetOffsetY = { it }, animationSpec = tween(180)),
        modifier = Modifier.align(Alignment.BottomCenter)
    ) {
        if (floatingBar) {
            // 液态玻璃悬浮底栏:overlay 悬浮于内容之上(backdrop 覆盖全屏,底栏后方折射/模糊到页面内容)
            FloatingBottomBar(
                // 紧凑宽度:悬浮条只包裹三个 tab 的内容宽度,水平居中悬浮
                modifier = Modifier
                    .padding(bottom = 24.dp + WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()),
                selectedIndex = { tabIndex },
                onSelected = { index ->
                    tabIndex = index
                    pagerNavigator.animateTo(pagerState, index)
                },
                backdrop = backdrop,
                tabsCount = 3,
            ) {
                FloatingBottomBarItem(onClick = {
                    tabIndex = 0
                    pagerNavigator.animateTo(pagerState, 0)
                }) {
                    Icon(
                        imageVector = BottomBarIcons.forTab(0, tabIndex == 0),
                        contentDescription = "首页"
                    )
                    BarLabel("首页")
                }
                FloatingBottomBarItem(onClick = {
                    tabIndex = 1
                    pagerNavigator.animateTo(pagerState, 1)
                }) {
                    Icon(
                        imageVector = BottomBarIcons.forTab(1, tabIndex == 1),
                        contentDescription = "剪贴板"
                    )
                    BarLabel("剪贴板")
                }
                FloatingBottomBarItem(onClick = {
                    tabIndex = 2
                    pagerNavigator.animateTo(pagerState, 2)
                }) {
                    Icon(
                        imageVector = BottomBarIcons.forTab(2, tabIndex == 2),
                        contentDescription = "设置"
                    )
                    BarLabel("设置")
                }
            }
        } else {
            // 普通贴底导航栏(毛玻璃,非悬浮)
            // NavigationBar 内部已自动处理导航栏 windowInsets,此处不加额外 bottom padding,使背景铺满底部
            Box(
                modifier = Modifier.fillMaxWidth()
            ) {
                BarBlurSurface(backdrop = backdrop, refreshKey = currentPage) {
                    NavigationBar(
                        color = Color.Transparent,
                        mode = NavigationBarDisplayMode.IconAndText
                    ) {
                        NavigationBarItem(
                            selected = tabIndex == 0,
                            onClick = { tabIndex = 0; pagerNavigator.animateTo(pagerState, 0) },
                            icon = BottomBarIcons.forTab(0, tabIndex == 0),
                            label = "首页"
                        )
                        NavigationBarItem(
                            selected = tabIndex == 1,
                            onClick = { tabIndex = 1; pagerNavigator.animateTo(pagerState, 1) },
                            icon = BottomBarIcons.forTab(1, tabIndex == 1),
                            label = "剪贴板"
                        )
                        NavigationBarItem(
                            selected = tabIndex == 2,
                            onClick = { tabIndex = 2; pagerNavigator.animateTo(pagerState, 2) },
                            icon = BottomBarIcons.forTab(2, tabIndex == 2),
                            label = "设置"
                        )
                    }
                }
            }
        }
    }

    // 全屏搜索页(覆盖顶栏/底栏):进入淡入;退出时整体淡出并向上收起
    // (与进入时顶栏下滑方向相反,呈"折叠回搜索框"效果)
    AnimatedVisibility(
        visible = searchOpen,
        enter = fadeIn(tween(220)),
        exit = fadeOut(tween(200)) + slideOutVertically(
            targetOffsetY = { -it / 5 },
            animationSpec = tween(200)
        )
    ) {
        SearchPage(
            sortForward = true,
            query = searchQuery,
            onQueryChange = { searchQuery = it },
            onClose = { searchOpen = false },
            snackbarHostState = snackbarHostState,
        )
    }
}
}

/** 状态行:左标签右值(模块状态卡片用) */
@Composable
fun StatusRow(
    label: String,
    value: String,
    valueColor: Color = MiuixTheme.colorScheme.onBackgroundVariant,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(label, color = MiuixTheme.colorScheme.onBackgroundVariant)
        Text(value, color = valueColor)
    }
}

/** 记录时间格式化("HH:mm") */
fun formatTime(millis: Long): String {
    val fmt = SimpleDateFormat("HH:mm", Locale.getDefault())
    return fmt.format(Date(millis))
}

/** 液态玻璃底栏 tab 标签(小号,与 KernelSU 一致) */
@Composable
private fun BarLabel(text: String) {
    Text(
        text = text,
        fontSize = 11.sp,
        lineHeight = 14.sp,
        maxLines = 1,
        softWrap = false,
        overflow = TextOverflow.Visible
    )
}

/** Snackbar 消息类型:决定文字配色(成功=主题蓝,失败=红,普通=黑) */
enum class SnackType { Info, Success, Error }

/** 最近一次 snackbar 的消息类型(SnackbarHost 展示层读取决定配色) */
object SnackTypeStore {
    var current: SnackType = SnackType.Info
}

/** 类型化 snackbar:记录消息类型后弹出,展示层按类型着色;返回动作结果(如"撤销") */
suspend fun SnackbarHostState.showAppSnack(
    message: String,
    type: SnackType = SnackType.Info,
    actionLabel: String? = null,
): SnackbarResult {
    SnackTypeStore.current = type
    return showSnackbar(message, actionLabel = actionLabel, withDismissAction = true)
}
