package clip.yixing.sync

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
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
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
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
import clip.yixing.sync.ui.BottomBarIcons
import clip.yixing.sync.ui.HomePage
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.ui.RecordsPage
import clip.yixing.sync.ui.SearchPage
import clip.yixing.sync.ui.BarBlurSurface
import clip.yixing.sync.ui.PageShell
import clip.yixing.sync.ui.FloatingBottomBar
import clip.yixing.sync.ui.FloatingBottomBarItem
import clip.yixing.sync.ui.SettingsPage
import clip.yixing.sync.ui.scan.QrScanPage
import clip.yixing.sync.ui.theme.SyncClipboardTheme
import clip.yixing.sync.util.SyncSettings
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
import top.yukonga.miuix.kmp.menu.OverlayIconDropdownMenu
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
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // 恢复本地剪贴板记录;按设置自动恢复监听
        ClipboardMonitorService.loadCaptured(applicationContext)
        if (SyncSettings.bootStartEnabled(this)) {
            ClipboardMonitorService.start(this)
        }
        setContent {
            // 气泡菜单/弹窗等组件依赖 NavigationEventDispatcher 处理返回手势,
            // 需在根节点提供 DispatcherOwner,否则点击下拉菜单会闪退。
            val dispatcherOwner = rememberNavigationEventDispatcherOwner(
                enabled = true,
                parent = null,
            )
            CompositionLocalProvider(
                LocalNavigationEventDispatcherOwner provides dispatcherOwner
            ) {
                SyncClipboardTheme {
                    MainScreen()
                }
            }
        }
    }
}

val cardContentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp)

/**
 * 底栏页面切换动画,与 KernelSU 一致:
 * 按目标页距离计算时长,用 EaseInOut 缓动滑动整个页面(底栏除外)。
 */
private class PagerNavigator(private val scope: CoroutineScope) {

    private var navJob: Job? = null

    fun animateTo(pagerState: PagerState, targetIndex: Int) {
        if (targetIndex == pagerState.currentPage) return

        navJob?.cancel()

        val distance = abs(targetIndex - pagerState.currentPage).coerceAtLeast(2)
        val duration = 100 * distance + 100
        val layoutInfo = pagerState.layoutInfo
        val pageSize = layoutInfo.pageSize + layoutInfo.pageSpacing
        val currentDistanceInPages = targetIndex - pagerState.currentPage - pagerState.currentPageOffsetFraction
        val scrollPixels = currentDistanceInPages * pageSize

        navJob = scope.launch {
            pagerState.animateScrollToPage(
                page = targetIndex,
                animationSpec = tween(easing = EaseInOut, durationMillis = duration)
            )
        }
    }
}

@Composable
private fun MainScreen() {
    val pagerState = rememberPagerState(pageCount = { 3 })
    val scope = rememberCoroutineScope()
    val pagerNavigator = remember { PagerNavigator(scope) }
    var isTimeForward by remember { mutableStateOf(true) } // true = 时间正序(最新在前, 默认), false = 时间倒序(最早在前)
    var isRecordsMultiSelect by remember { mutableStateOf(false) }
    var recordsSelectedCount by remember { mutableIntStateOf(0) }
    var recordsTotalFilteredCount by remember { mutableIntStateOf(0) }
    var onToggleSelectAllRecords by remember { mutableStateOf<(() -> Unit)?>(null) }
    var onExitRecordsMultiSelect by remember { mutableStateOf<(() -> Unit)?>(null) }
    var searchOpen by remember { mutableStateOf(false) }
    var searchQuery by remember { mutableStateOf("") }
    val snackbarHostState = remember { SnackbarHostState() }
    // 底栏选中索引:点击立即驱动(指示器马上跟随),页面手势滑动停稳后同步
    var tabIndex by remember { mutableIntStateOf(pagerState.currentPage) }
    LaunchedEffect(pagerState) {
        snapshotFlow { pagerState.settledPage }.collectLatest { tabIndex = it }
    }
    // 悬浮底栏开关(设置页可切,默认开启)
    val appContext = LocalContext.current
    var floatingBar by remember { mutableStateOf(SyncSettings.floatingBottomBarEnabled(appContext)) }
    var isOverlayActive by remember { mutableStateOf(false) }
    var isScanOpen by remember { mutableStateOf(false) }
    var enterMultiSelectTrigger by remember { mutableIntStateOf(0) }
    var clearDialogTrigger by remember { mutableIntStateOf(0) }

    BackHandler(enabled = isScanOpen) {
        isScanOpen = false
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
        // 整页(含大标题顶栏)在 Pager 内滑动切换,与 KernelSU 一致
        Box(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
        ) {
            HorizontalPager(
                state = pagerState,
                modifier = Modifier
                    .fillMaxSize()
                    .overScrollHorizontal(),
                beyondViewportPageCount = 3
            ) { page ->
                when (page) {
                    0 -> PageShell(
                        title = "NexClip",
                        bottomInnerPadding = bottomInnerPadding,
                        actions = {
                            IconButton(onClick = { isScanOpen = true }) {
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
                            onOpenQrScanner = { isScanOpen = true },
                            onOverlayActiveChanged = { isOverlayActive = it }
                        )
                    }
                    1 -> PageShell(
                        title = if (isRecordsMultiSelect) "已选择 ${recordsSelectedCount} 项" else "捕获记录",
                        bottomInnerPadding = bottomInnerPadding,
                        navigationIcon = if (isRecordsMultiSelect) {
                            {
                                Text(
                                    text = "取消",
                                    color = MiuixTheme.colorScheme.primary,
                                    fontSize = 16.sp,
                                    modifier = Modifier
                                        .clickable { onExitRecordsMultiSelect?.invoke() }
                                        .padding(horizontal = 14.dp, vertical = 8.dp)
                                )
                            }
                        } else { {} },
                        actions = {
                            if (isRecordsMultiSelect) {
                                val isAllSelected = recordsSelectedCount > 0 && recordsSelectedCount == recordsTotalFilteredCount
                                Text(
                                    text = if (isAllSelected) "取消全选" else "全选",
                                    color = MiuixTheme.colorScheme.primary,
                                    fontSize = 16.sp,
                                    modifier = Modifier
                                        .clickable { onToggleSelectAllRecords?.invoke() }
                                        .padding(horizontal = 14.dp, vertical = 8.dp)
                                )
                            } else {
                                // 整合菜单: 排序(正序/倒序) + 多选管理 + 清空全部记录
                                OverlayIconDropdownMenu(
                                    entry = DropdownEntry(
                                        items = listOf(
                                            DropdownItem(
                                                text = "时间正序",
                                                selected = isTimeForward,
                                                onClick = { isTimeForward = true },
                                            ),
                                            DropdownItem(
                                                text = "时间倒序",
                                                selected = !isTimeForward,
                                                onClick = { isTimeForward = false },
                                            ),
                                            DropdownItem(
                                                text = "多选管理",
                                                onClick = { enterMultiSelectTrigger++ },
                                            ),
                                            DropdownItem(
                                                text = "清空全部记录",
                                                onClick = { clearDialogTrigger++ },
                                            ),
                                        ),
                                    ),
                                    onExpandedChange = { isOverlayActive = it }
                                ) {
                                    Icon(
                                        imageVector = MiuixIcons.Normal.More,
                                        contentDescription = "更多操作",
                                    )
                                }
                                // 搜索:打开全屏搜索页
                                IconButton(
                                    onClick = {
                                        searchQuery = ""
                                        searchOpen = true
                                    },
                                ) {
                                    Icon(
                                        imageVector = MiuixIcons.Normal.Search,
                                        contentDescription = "搜索"
                                    )
                                }
                            }
                        }
                    ) { scrollBehavior, topPadding ->
                        RecordsPage(
                            scrollBehavior, topPadding, bottomInnerPadding,
                            sortForward = isTimeForward,
                            snackbarHostState = snackbarHostState,
                            enterMultiSelectTrigger = enterMultiSelectTrigger,
                            clearDialogTrigger = clearDialogTrigger,
                            onMultiSelectStateChanged = { inMultiSelect, selectedCount, totalCount, toggleSelectAll, exitMultiSelect ->
                                isRecordsMultiSelect = inMultiSelect
                                recordsSelectedCount = selectedCount
                                recordsTotalFilteredCount = totalCount
                                onToggleSelectAllRecords = toggleSelectAll
                                onExitRecordsMultiSelect = exitMultiSelect
                            },
                            onOverlayActiveChanged = { isOverlayActive = it }
                        )
                    }
                    else -> SettingsPage(
                        bottomInnerPadding = bottomInnerPadding,
                        snackbarHostState = snackbarHostState,
                        floatingBarEnabled = floatingBar,
                        onFloatingBarChange = {
                            floatingBar = it
                            SyncSettings.setFloatingBottomBarEnabled(appContext, it)
                        },
                        onOverlayActiveChanged = { isOverlayActive = it },
                        onOpenQrScanner = { isScanOpen = true }
                    )
                }
            }
        }
    }

    // Snackbar:按消息类型着色 —— 成功=主题蓝,失败=红,普通=黑(浅色模式足够深)
    SnackbarHost(
        state = snackbarHostState,
        modifier = Modifier
            .align(Alignment.BottomCenter)
            // 避开底栏:悬浮条(132dp)或普通贴底条(80dp + 手势区)
            .padding(
                bottom = if (floatingBar) {
                    132.dp
                } else {
                    80.dp + WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
                },
                start = 16.dp,
                end = 16.dp
            )
    ) { data ->
        val scheme = MiuixTheme.colorScheme
        val type = SnackTypeStore.current
        val textColor = when (type) {
            SnackType.Success -> scheme.primary
            SnackType.Error -> scheme.error
            SnackType.Info -> scheme.onBackground
        }
        Snackbar(
            data = data,
            colors = SnackbarDefaults.snackbarColors(
                containerColor = scheme.surfaceContainerHigh,
                contentColor = textColor,
                actionContentColor = textColor,
                dismissActionContentColor = textColor,
            )
        )
    }

    // 底栏:悬浮(液态玻璃)或普通贴底,由设置开关控制,默认开启
    // 当全屏搜索或任一弹层/对话框/多选操作/扫码激活时平滑隐藏,防止遮挡弹层内容与操作按钮
    AnimatedVisibility(
        visible = !searchOpen && !isOverlayActive && !isScanOpen,
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
                        contentDescription = "记录"
                    )
                    BarLabel("记录")
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
                            label = "记录"
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
            sortForward = isTimeForward,
            query = searchQuery,
            onQueryChange = { searchQuery = it },
            onClose = { searchOpen = false },
            snackbarHostState = snackbarHostState,
        )
    }

    // 全屏扫码配对页(全屏覆盖,独立返回手势)
    AnimatedVisibility(
        visible = isScanOpen,
        enter = fadeIn(tween(220)) + slideInVertically(initialOffsetY = { it }, animationSpec = tween(220)),
        exit = fadeOut(tween(200)) + slideOutVertically(targetOffsetY = { it }, animationSpec = tween(200))
    ) {
        QrScanPage(
            snackbarHostState = snackbarHostState,
            onBack = { isScanOpen = false },
            onPairSuccess = {
                isScanOpen = false
                tabIndex = 0
                pagerNavigator.animateTo(pagerState, 0)
            }
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
