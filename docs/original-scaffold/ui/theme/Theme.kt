package clip.yixing.sync.ui.theme

import android.app.Activity
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import androidx.core.view.WindowCompat
import top.yukonga.miuix.kmp.theme.ColorSchemeMode
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.theme.ThemeController

@Composable
fun SyncClipboardTheme(content: @Composable () -> Unit) {
    val controller = remember { ThemeController(ColorSchemeMode.System) }
    val context = LocalContext.current
    // 在组合上下文中读取实际配色,主题切换时随重组自动更新
    val isLightBackground = MiuixTheme.colorScheme.background.luminance() > 0.5f

    // 根据当前实际配色(浅色/深色)同步系统状态栏与导航栏的图标颜色,
    // 避免浅色背景下白色图标不可见的问题;主题切换时也会自动更新。
    SideEffect {
        val activity = context as? Activity ?: return@SideEffect
        val insetsController = WindowCompat.getInsetsController(
            activity.window,
            activity.window.decorView
        )
        insetsController.isAppearanceLightStatusBars = isLightBackground
        insetsController.isAppearanceLightNavigationBars = isLightBackground
    }

    MiuixTheme(controller = controller) { content() }
}
