package clip.yixing.sync

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.core.EaseInOut
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PagerState
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.activity.enableEdgeToEdge
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.ui.HomePage
import clip.yixing.sync.ui.RecordsPage
import clip.yixing.sync.ui.SettingsPage
import clip.yixing.sync.ui.theme.SyncClipboardTheme
import clip.yixing.sync.util.SyncSettings
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.MiuixScrollBehavior
import top.yukonga.miuix.kmp.basic.NavigationBar
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.NavigationBarDisplayMode
import top.yukonga.miuix.kmp.basic.NavigationBarItem
import top.yukonga.miuix.kmp.basic.Scaffold
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Home
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
            SyncClipboardTheme {
                MainScreen()
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
    var sortDesc by remember { mutableIntStateOf(1) }
    val snackbarHostState = remember { SnackbarHostState() }
    val titles = listOf("剪贴板同步", "捕获记录", "设置")

    Scaffold(
        bottomBar = {
            NavigationBar(mode = NavigationBarDisplayMode.IconAndText) {
                NavigationBarItem(
                    selected = pagerState.currentPage == 0,
                    onClick = { pagerNavigator.animateTo(pagerState, 0) },
                    icon = MiuixIcons.Normal.Home,
                    label = "首页"
                )
                NavigationBarItem(
                    selected = pagerState.currentPage == 1,
                    onClick = { pagerNavigator.animateTo(pagerState, 1) },
                    icon = MiuixIcons.Normal.Notes,
                    label = "记录"
                )
                NavigationBarItem(
                    selected = pagerState.currentPage == 2,
                    onClick = { pagerNavigator.animateTo(pagerState, 2) },
                    icon = MiuixIcons.Normal.Settings,
                    label = "设置"
                )
            }
        }
    ) { padding ->
        val bottomInnerPadding = padding.calculateBottomPadding()
        HorizontalPager(
            state = pagerState,
            modifier = Modifier
                .fillMaxSize()
                .overScrollHorizontal(),
            beyondViewportPageCount = 3
        ) { page ->
            PageShell(title = titles[page], bottomInnerPadding = bottomInnerPadding) { scrollBehavior, topPadding ->
                when (page) {
                    0 -> HomePage(scrollBehavior, topPadding, bottomInnerPadding)
                    1 -> RecordsPage(
                        scrollBehavior, topPadding, bottomInnerPadding,
                        sortDesc = sortDesc == 1,
                        snackbarHostState = snackbarHostState,
                    )
                    else -> SettingsPage(scrollBehavior, topPadding, bottomInnerPadding)
                }
            }
        }
    }
}

/**
 * KernelSU 风格整页结构:每页独立大标题 TopAppBar 与滚动状态,页面间互不共享。
 */
@Composable
private fun PageShell(
    title: String,
    bottomInnerPadding: Dp,
    content: @Composable (ScrollBehavior, Dp) -> Unit
) {
    val scrollBehavior = MiuixScrollBehavior()
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = title,
            largeTitle = title,
            scrollBehavior = scrollBehavior
        )
        Box(Modifier.weight(1f).fillMaxWidth()) {
            content(scrollBehavior, 0.dp)
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
