package clip.yixing.sync

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.ui.HomePage
import clip.yixing.sync.ui.RecordsPage
import clip.yixing.sync.ui.SettingsPage
import clip.yixing.sync.ui.theme.SyncClipboardTheme
import clip.yixing.sync.util.SyncSettings
import top.yukonga.miuix.kmp.basic.MiuixScrollBehavior
import top.yukonga.miuix.kmp.basic.NavigationBar
import top.yukonga.miuix.kmp.basic.NavigationBarItem
import top.yukonga.miuix.kmp.basic.SnackbarHost
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.basic.rememberTopAppBarState
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Home
import top.yukonga.miuix.kmp.icon.extended.ListView
import top.yukonga.miuix.kmp.icon.extended.Settings
import top.yukonga.miuix.kmp.theme.MiuixTheme
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
                MainShell()
            }
        }
    }
}

/** Miuix 外壳:顶部栏(滚动折叠)+ 三页 + 底部导航 */
@Composable
fun MainShell() {
    val context = LocalContext.current
    val scrollBehavior = MiuixScrollBehavior(rememberTopAppBarState())
    var tab by remember { mutableIntStateOf(0) }
    var sortDesc by remember { mutableIntStateOf(1) }
    val snackbarHostState = remember { SnackbarHostState() }
    val titles = listOf("剪贴板同步", "同步记录", "设置")

    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = titles[tab],
            scrollBehavior = scrollBehavior,
        )
        Box(Modifier.weight(1f).fillMaxWidth()) {
            when (tab) {
                0 -> HomePage(scrollBehavior, topPadding = 0.dp, bottomInnerPadding = 0.dp)
                1 -> RecordsPage(
                    scrollBehavior,
                    topPadding = 0.dp,
                    bottomInnerPadding = 0.dp,
                    sortDesc = sortDesc == 1,
                    snackbarHostState = snackbarHostState,
                )
                else -> SettingsPage(scrollBehavior, topPadding = 0.dp, bottomInnerPadding = 0.dp)
            }
        }
        NavigationBar {
            NavigationBarItem(
                selected = tab == 0,
                onClick = { tab = 0 },
                icon = MiuixIcons.Home,
                label = "首页",
            )
            NavigationBarItem(
                selected = tab == 1,
                onClick = { tab = 1 },
                icon = MiuixIcons.ListView,
                label = "记录",
            )
            NavigationBarItem(
                selected = tab == 2,
                onClick = { tab = 2 },
                icon = MiuixIcons.Settings,
                label = "设置",
            )
        }
    }
}

/** 卡片内边距(各页 Card 统一使用) */
val cardContentPadding = PaddingValues(16.dp, 12.dp)

/** 状态行:左标签右值(首页模块状态用) */
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
