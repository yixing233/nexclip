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
import clip.yixing.sync.root.AccessibilityGuard
import clip.yixing.sync.shizuku.ShizukuPermission
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.ui.PageShell
import clip.yixing.sync.ui.SectionBlock
import clip.yixing.sync.util.PasteMethod
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
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

    // ---- root 守护无障碍服务 ----
    var guardEnabled by remember { mutableStateOf(SyncSettings.accessibilityGuardEnabled(context)) }
    // root 可用性需要执行 su,不能在组合期同步调用 —— 先给 null 表示"检测中"
    var isRootReady by remember { mutableStateOf<Boolean?>(null) }
    var guardBusy by remember { mutableStateOf(false) }
    var guardLastResult by remember { mutableStateOf(AccessibilityGuard.lastResult(context)) }
    /** root 探测任务引用:用于防重入,并在页面销毁时取消。 */
    var rootProbeJob by remember { mutableStateOf<Job?>(null) }
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

    /**
     * 后台探测 root 可用性(会执行 su,不能在主线程做)。
     *
     * 注意:这里刻意【不在进入页面时自动执行】。
     * su 调用会 fork 进程并等待 root 管理器授权,期间若页面正在播放退场动画,
     * 回调里的状态回写会触发重组、打断 subPageAnimProgress 的动画,
     * 表现为"返回时先闪一下设置主页再进入下一页"。
     * 改为只在用户点击"Root 权限"或开启开关时按需探测。
     */
    fun probeRoot() {
        if (rootProbeJob?.isActive == true) return
        rootProbeJob = scope.launch(Dispatchers.IO) {
            val ready = AccessibilityGuard.isRootAvailable()
            // 任务被取消(页面已销毁)时协程会在此抛出 CancellationException,
            // 不会执行下面的状态写入 —— 这正是避免"退场时闪设置主页"的关键。
            ensureActive()
            withContext(Dispatchers.Main) { isRootReady = ready }
        }
    }

    fun refreshBackendStatus() {
        isAccessibilityOn = NexClipAccessibilityService.isEnabledInSystem(context)
        canDrawOverlays = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) Settings.canDrawOverlays(context) else true
        isShizukuReady = ShizukuPermission.isGranted()
        guardLastResult = AccessibilityGuard.lastResult(context)
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
            // 退场时取消仍在跑的 root 探测,避免它稍后回写状态触发重组。
            // 这正是"从本页返回时先闪一下设置主页"的直接成因。
            rootProbeJob?.cancel()
            rootProbeJob = null
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
                SectionBlock(title = "无障碍守护 (Root)", insideMargin = PaddingValues()) {
                    SwitchPreference(
                        title = "自动保持无障碍开启",
                        summary = "无障碍权限常被系统自动关闭。开启后,检测到被关会用 root 自动重新启用",
                        checked = guardEnabled,
                        onCheckedChange = { checked ->
                            guardEnabled = checked
                            SyncSettings.setAccessibilityGuardEnabled(context, checked)
                            scope.launch {
                                if (checked) {
                                    snackbarHostState.showAppSnack("已开启。首次运行会请求 root 授权", SnackType.Info)
                                    probeRoot()
                                } else {
                                    snackbarHostState.showAppSnack("已关闭无障碍守护", SnackType.Info)
                                }
                            }
                        }
                    )

                    // 仅在功能开启时展示状态与操作,避免干扰不用 root 的用户
                    if (guardEnabled) {
                        val rootText = when (isRootReady) {
                            null -> "正在检测…"
                            true -> "已授权"
                            false -> "未授权或设备未 root"
                        }
                        ArrowPreference(
                            title = "Root 权限",
                            summary = "当前状态:$rootText",
                            onClick = { probeRoot() }
                        )

                        val resultText = when (guardLastResult) {
                            AccessibilityGuard.LastResult.Never -> "尚未执行"
                            AccessibilityGuard.LastResult.Success -> "最近一次:已自动恢复无障碍"
                            AccessibilityGuard.LastResult.NotNeeded -> "最近一次:无需修复"
                            AccessibilityGuard.LastResult.NoRoot -> "最近一次:root 未授权"
                            AccessibilityGuard.LastResult.Failed -> "最近一次:写入后未生效(可能被 ROM 拦截)"
                            AccessibilityGuard.LastResult.Throttled -> "最近一次:已跳过(节流中)"
                        }
                        ArrowPreference(
                            title = "立即修复",
                            summary = if (guardBusy) "正在修复…" else resultText + " · 点击立即用 root 修复",
                            onClick = {
                                if (guardBusy) return@ArrowPreference
                                guardBusy = true
                                scope.launch {
                                    val result = withContext(Dispatchers.IO) {
                                        AccessibilityGuard.ensureEnabled(context, force = true)
                                    }
                                    guardBusy = false
                                    guardLastResult = result
                                    refreshBackendStatus()
                                    val (msg, type) = when (result) {
                                        AccessibilityGuard.LastResult.Success -> "无障碍服务已通过 root 恢复" to SnackType.Success
                                        AccessibilityGuard.LastResult.NotNeeded -> "无障碍服务本来就是开启的" to SnackType.Info
                                        AccessibilityGuard.LastResult.NoRoot -> "root 未授权,请在 root 管理器中允许本应用" to SnackType.Error
                                        AccessibilityGuard.LastResult.Failed -> "写入后未生效,可能被系统策略拦截" to SnackType.Error
                                        else -> "已跳过" to SnackType.Info
                                    }
                                    snackbarHostState.showAppSnack(msg, type)
                                }
                            }
                        )
                    }
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
