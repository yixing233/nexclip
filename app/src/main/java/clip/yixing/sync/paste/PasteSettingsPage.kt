package clip.yixing.sync.paste

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import clip.yixing.sync.SnackType
import clip.yixing.sync.shizuku.ShizukuPermission
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.ui.PageShell
import clip.yixing.sync.ui.SectionBlock
import clip.yixing.sync.util.PasteMethod
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.preference.WindowDropdownPreference
import top.yukonga.miuix.kmp.window.WindowDialog

@Composable
fun PasteSettingsPage(
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val lifecycleOwner = LocalLifecycleOwner.current

    var pasteMethod by remember { mutableStateOf(SyncSettings.pasteMethod(context)) }
    var keepClipboard by remember { mutableStateOf(SyncSettings.pasteKeepClipboard(context)) }
    var bubbleEnabled by remember { mutableStateOf(SyncSettings.floatingBubbleEnabled(context)) }
    var notifAction by remember { mutableStateOf(SyncSettings.pasteNotificationAction(context)) }

    var isAccessibilityOn by remember { mutableStateOf(NexClipAccessibilityService.isEnabledInSystem(context)) }
    var canDrawOverlays by remember {
        mutableStateOf(if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) Settings.canDrawOverlays(context) else true)
    }
    var isShizukuReady by remember { mutableStateOf(ShizukuPermission.isGranted()) }
    var showRestrictedSettingsGuide by remember { mutableStateOf(false) }
    var accessibilitySettingsRequested by remember { mutableStateOf(false) }

    fun openAccessibilitySettings() {
        accessibilitySettingsRequested = true
        runCatching {
            context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }.onFailure { e ->
            scope.launch {
                snackbarHostState.showAppSnack("无法打开无障碍设置: ${e.message}", SnackType.Error)
            }
        }
    }

    fun openAppDetails() {
        runCatching {
            context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:${context.packageName}")
            })
        }.onFailure { e ->
            scope.launch {
                snackbarHostState.showAppSnack("无法打开应用信息: ${e.message}", SnackType.Error)
            }
        }
    }

    fun refreshBackendStatus() {
        isAccessibilityOn = NexClipAccessibilityService.isEnabledInSystem(context)
        canDrawOverlays = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) Settings.canDrawOverlays(context) else true
        isShizukuReady = ShizukuPermission.isGranted()
    }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                val wasAccessibilityOn = isAccessibilityOn
                refreshBackendStatus()
                if (canDrawOverlays && bubbleEnabled) {
                    FloatingBubbleService.sync(context)
                }
                if (accessibilitySettingsRequested) {
                    accessibilitySettingsRequested = false
                    if (!isAccessibilityOn && !wasAccessibilityOn && Build.VERSION.SDK_INT >= 33) {
                        showRestrictedSettingsGuide = true
                    }
                }
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
        }
    }

    val methodOptions = listOf(
        PasteMethod.AUTO,
        PasteMethod.ACCESSIBILITY,
        PasteMethod.SHIZUKU
    )
    val methodLabels = listOf(
        "自动选择 (${PasteMethod.AUTO.summary})",
        "无障碍注入 (${PasteMethod.ACCESSIBILITY.summary})",
        "Shizuku 按键 (${PasteMethod.SHIZUKU.summary})"
    )
    val initialIndex = methodOptions.indexOf(pasteMethod).let { if (it >= 0) it else 0 }
    var selectedMethodIndex by remember(pasteMethod) {
        mutableIntStateOf(initialIndex)
    }

    PageShell(
        title = "一键粘贴与悬浮球",
        bottomInnerPadding = bottomInnerPadding,
        navigationIcon = {
            IconButton(onClick = onBack) {
                top.yukonga.miuix.kmp.basic.Icon(
                    imageVector = MiuixIcons.Back,
                    contentDescription = "返回"
                )
            }
        }
    ) { scrollBehavior, topPadding ->
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 16.dp)
                .nestedScroll(scrollBehavior.nestedScrollConnection),
            contentPadding = PaddingValues(
                top = topPadding + 10.dp,
                bottom = bottomInnerPadding + 24.dp
            )
        ) {
            item {
                SectionBlock(title = "核心功能", insideMargin = PaddingValues()) {
                    WindowDropdownPreference(
                        items = methodLabels,
                        selectedIndex = selectedMethodIndex,
                        onSelectedIndexChange = { index ->
                            selectedMethodIndex = index
                            val targetMethod = methodOptions[index]
                            pasteMethod = targetMethod
                            SyncSettings.setPasteMethod(context, targetMethod)
                            if (targetMethod == PasteMethod.ACCESSIBILITY && !isAccessibilityOn) {
                                scope.launch {
                                    snackbarHostState.showAppSnack("提示: 请开启无障碍服务以生效", SnackType.Info)
                                }
                            } else if (targetMethod == PasteMethod.SHIZUKU && !isShizukuReady) {
                                scope.launch {
                                    snackbarHostState.showAppSnack("提示: 请为 NexClip 授予 Shizuku 权限", SnackType.Info)
                                }
                            }
                        },
                        title = "粘贴实现方式"
                    )

                    SwitchPreference(
                        title = "直接注入并保留剪贴板",
                        summary = "使用无障碍输入时，直接将文字填入光标位置，不破坏系统剪贴板中原有的内容",
                        checked = keepClipboard,
                        onCheckedChange = { checked ->
                            keepClipboard = checked
                            SyncSettings.setPasteKeepClipboard(context, checked)
                        }
                    )

                    SwitchPreference(
                        title = "通知栏 / 灵动岛快捷粘贴",
                        summary = "收到远端新内容时，在通知栏提供快捷一键粘贴动作",
                        checked = notifAction,
                        onCheckedChange = { checked ->
                            notifAction = checked
                            SyncSettings.setPasteNotificationAction(context, checked)
                        }
                    )
                }
            }

            item {
                Spacer(Modifier.height(16.dp))
                SectionBlock(title = "焦点智能悬浮球", insideMargin = PaddingValues()) {
                    SwitchPreference(
                        title = "启用悬浮球与最近记录面板",
                        summary = "在输入框获得焦点时智能弹出轻量悬浮球，点击可查看最近记录并直接粘贴",
                        checked = bubbleEnabled,
                        onCheckedChange = { checked ->
                            if (checked && !canDrawOverlays) {
                                scope.launch {
                                    snackbarHostState.showAppSnack("请先授予显示在其他应用上层的权限", SnackType.Info)
                                }
                                try {
                                    val intent = Intent(
                                        Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                                        Uri.parse("package:${context.packageName}")
                                    )
                                    context.startActivity(intent)
                                } catch (_: Exception) {}
                                return@SwitchPreference
                            }
                            bubbleEnabled = checked
                            SyncSettings.setFloatingBubbleEnabled(context, checked)
                            FloatingBubbleService.sync(context)
                        }
                    )
                }
            }

            item {
                Spacer(Modifier.height(16.dp))
                SectionBlock(title = "后端服务授权状态", insideMargin = PaddingValues()) {
                    ArrowPreference(
                        title = "无障碍服务 (推荐)",
                        summary = if (isAccessibilityOn) "已开启 · 支持直接注入光标与输入框焦点感知" else "未开启 · 点击前往系统无障碍设置页启用",
                        onClick = { openAccessibilitySettings() }
                    )

                    ArrowPreference(
                        title = "悬浮窗权限 (显示在上层)",
                        summary = if (canDrawOverlays) "已授权 · 悬浮球可在任意应用上方显示" else "未授权 · 点击前往系统权限设置页授予",
                        onClick = {
                            try {
                                val intent = Intent(
                                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                                    Uri.parse("package:${context.packageName}")
                                )
                                context.startActivity(intent)
                            } catch (e: Exception) {
                                scope.launch {
                                    snackbarHostState.showAppSnack("无法打开权限设置: ${e.message}", SnackType.Error)
                                }
                            }
                        }
                    )

                    ArrowPreference(
                        title = "Shizuku 授权 (备选方案)",
                        summary = if (isShizukuReady) "已就绪 · 支持模拟粘贴快捷键注入" else "未授权或未运行 · 点击申请授权",
                        onClick = {
                            if (!ShizukuPermission.isAvailable()) {
                                scope.launch {
                                    snackbarHostState.showAppSnack("Shizuku 服务未运行，请先启动 Shizuku", SnackType.Error)
                                }
                            } else {
                                ShizukuPermission.requestIfNeeded { granted ->
                                    isShizukuReady = granted
                                    scope.launch {
                                        if (granted) {
                                            snackbarHostState.showAppSnack("Shizuku 权限授权成功", SnackType.Success)
                                        } else {
                                            snackbarHostState.showAppSnack("Shizuku 权限未被允许", SnackType.Error)
                                        }
                                    }
                                }
                            }
                        }
                    )
                }
            }
        }
    }

    WindowDialog(
        show = showRestrictedSettingsGuide,
        title = "需要允许受限设置",
        summary = "Android 13 及以上系统可能会阻止侧载应用启用无障碍服务。请先进入应用信息，点击右上角菜单并选择「允许受限设置」，再返回这里开启无障碍服务。",
        onDismissRequest = { showRestrictedSettingsGuide = false }
    ) {
        Text(
            text = "此权限仅用于判断输入框是否聚焦，并在你主动点击粘贴时写入内容；不会读取、保存或上传输入框中的文字。"
        )
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = { showRestrictedSettingsGuide = false },
                colors = ButtonDefaults.buttonColors(
                    color = top.yukonga.miuix.kmp.theme.MiuixTheme.colorScheme.surfaceContainerHigh,
                    contentColor = top.yukonga.miuix.kmp.theme.MiuixTheme.colorScheme.onSurface
                ),
                modifier = Modifier.weight(1f)
            ) {
                Text("稍后")
            }
            Button(
                onClick = {
                    showRestrictedSettingsGuide = false
                    openAppDetails()
                },
                colors = ButtonDefaults.buttonColorsPrimary(),
                modifier = Modifier.weight(1f)
            ) {
                Text("打开应用信息")
            }
        }
    }
}
