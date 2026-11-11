package clip.yixing.sync.sms

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import clip.yixing.sync.SnackType
import clip.yixing.sync.paste.NexClipAccessibilityService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.sms.rules.SmsCodeRuleStore
import clip.yixing.sync.ui.PageShell
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.window.WindowDialog
import clip.yixing.sync.ui.SectionBlock
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference

/**
 * 短信验证码设置页。
 *
 * 两个开关是递进关系: 先要「接收短信」拿到验证码, 「自动填入」才有意义。
 * 因此自动填入的开关在接收未开启时不可用, 且需要无障碍服务就绪。
 */
@Composable
fun SmsCodeSettingsPage(
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    onBack: () -> Unit,
    onOpenRecords: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val lifecycleOwner = LocalLifecycleOwner.current

    var smsEnabled by remember { mutableStateOf(SyncSettings.smsCodeEnabled(context)) }
    var autofillEnabled by remember { mutableStateOf(SyncSettings.smsCodeAutofill(context)) }

    // 规则库:条数摘要 + 更新中状态
    var updatingRules by remember { mutableStateOf(false) }
    var ruleSummary by remember { mutableStateOf(buildRuleSummary(context)) }

    // 诊断日志弹窗
    var showDiagLog by remember { mutableStateOf(false) }
    var diagLogText by remember { mutableStateOf("") }

    // 每次打开弹窗时重新读取(日志是追加写的,不在组合期缓存)
    LaunchedEffect(showDiagLog) {
        if (showDiagLog) diagLogText = SmsDiagLog.readAll(context)
    }

    fun hasSmsPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.RECEIVE_SMS) ==
            PackageManager.PERMISSION_GRANTED

    var isSmsGranted by remember { mutableStateOf(hasSmsPermission()) }
    var isAccessibilityOn by remember {
        mutableStateOf(NexClipAccessibilityService.isEnabledInSystem(context))
    }

    val smsPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { granted ->
        isSmsGranted = granted
        scope.launch {
            if (granted) {
                smsEnabled = true
                SyncSettings.setSmsCodeEnabled(context, true)
                snackbarHostState.showAppSnack("已开启短信验证码识别", SnackType.Success)
            } else {
                smsEnabled = false
                SyncSettings.setSmsCodeEnabled(context, false)
                snackbarHostState.showAppSnack("未授予短信权限，无法识别验证码", SnackType.Info)
            }
        }
    }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                // 用户可能在系统设置里改过权限/无障碍，回到页面时重新读取
                isSmsGranted = hasSmsPermission()
                isAccessibilityOn = NexClipAccessibilityService.isEnabledInSystem(context)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    PageShell(
        title = "短信验证码",
        bottomInnerPadding = bottomInnerPadding,
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(imageVector = MiuixIcons.Back, contentDescription = "返回")
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
                SectionBlock(title = "验证码识别", insideMargin = PaddingValues()) {
                    SwitchPreference(
                        title = "接收短信并提取验证码",
                        summary = "收到含验证码的短信时自动提取并写入剪贴板。仅提取验证码，" +
                            "短信正文不会保存或上传到任何设备",
                        checked = smsEnabled,
                        onCheckedChange = { checked ->
                            if (checked && !isSmsGranted) {
                                smsPermissionLauncher.launch(Manifest.permission.RECEIVE_SMS)
                                return@SwitchPreference
                            }
                            smsEnabled = checked
                            SyncSettings.setSmsCodeEnabled(context, checked)
                            if (!checked) {
                                // 关闭接收时一并关掉自动填入，避免留下一个永远不触发的开关
                                autofillEnabled = false
                                SyncSettings.setSmsCodeAutofill(context, false)
                            }
                        }
                    )

                    SwitchPreference(
                        title = "自动填入验证码",
                        summary = when {
                            !smsEnabled -> "需先开启上方「接收短信并提取验证码」"
                            !isAccessibilityOn -> "需先开启无障碍服务，点击前往系统设置启用"
                            else -> "收到验证码时直接填入当前输入框（仅当输入框为空）。" +
                                "不填聊天框、搜索框等非验证场景，避免误填"
                        },
                        checked = autofillEnabled,
                        enabled = smsEnabled,
                        onCheckedChange = { checked ->
                            if (checked && !isAccessibilityOn) {
                                scope.launch {
                                    snackbarHostState.showAppSnack("请先开启无障碍服务", SnackType.Info)
                                }
                                runCatching {
                                    context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                                }
                                return@SwitchPreference
                            }
                            autofillEnabled = checked
                            SyncSettings.setSmsCodeAutofill(context, checked)
                        }
                    )
                }
            }

            item {
                Spacer(Modifier.height(16.dp))
                SectionBlock(title = "识别规则库", insideMargin = PaddingValues()) {
                    ArrowPreference(
                        title = if (updatingRules) "正在更新规则库…" else "更新识别规则库",
                        summary = ruleSummary,
                        onClick = {
                            if (updatingRules) return@ArrowPreference
                            updatingRules = true
                            scope.launch {
                                val count = SmsCodeRuleStore.updateFromRemote(
                                    context,
                                    SyncSettings.smsCodeRuleSourceUrl(context)
                                )
                                updatingRules = false
                                snackbarHostState.showAppSnack(
                                    if (count != null) "规则库已更新: $count 条规则"
                                    else "更新失败,继续使用当前规则",
                                    if (count != null) SnackType.Success else SnackType.Error
                                )
                                ruleSummary = buildRuleSummary(context)
                            }
                        }
                    )
                }
            }

            item {
                Spacer(Modifier.height(16.dp))
                SectionBlock(title = "短信记录", insideMargin = PaddingValues()) {
                    ArrowPreference(
                        title = "查看短信识别记录",
                        summary = "确认短信接收与验证码提取是否正常",
                        onClick = onOpenRecords
                    )
                    ArrowPreference(
                        title = "查看诊断日志",
                        summary = "日用几天后回看：每条短信走到了哪一步、命中哪条规则。" +
                            "只记录统计特征（长度/关键字有无/数字个数），不含短信正文",
                        onClick = { showDiagLog = true }
                    )
                }
            }

            item {
                Spacer(Modifier.height(16.dp))
                SectionBlock(title = "权限状态", insideMargin = PaddingValues()) {
                    ArrowPreference(
                        title = "短信权限",
                        summary = if (isSmsGranted) {
                            "已授权 · 可接收短信并提取验证码"
                        } else {
                            "未授权 · 点击前往系统权限设置授予"
                        },
                        onClick = {
                            if (!isSmsGranted) {
                                smsPermissionLauncher.launch(Manifest.permission.RECEIVE_SMS)
                            } else {
                                runCatching {
                                    context.startActivity(
                                        Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                            data = android.net.Uri.parse("package:${context.packageName}")
                                        }
                                    )
                                }
                            }
                        }
                    )

                    ArrowPreference(
                        title = "无障碍服务",
                        summary = if (isAccessibilityOn) {
                            "已开启 · 可自动填入验证码"
                        } else {
                            "未开启 · 自动填入需要此服务，点击前往启用"
                        },
                        onClick = {
                            runCatching {
                                context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                            }
                        }
                    )
                }
            }

            item {
                Spacer(Modifier.height(16.dp))
                SectionBlock(title = "说明", insideMargin = PaddingValues()) {
                    ArrowPreference(
                        title = "隐私边界",
                        summary = "NexClip 只在收到验证码短信时读取该条短信，仅提取其中的验证码写入剪贴板。" +
                            "短信正文既不落盘也不上传，未识别出验证码的短信会被直接丢弃",
                        onClick = {}
                    )
                }
            }
        }
    }

    // 诊断日志弹窗:日用几天后回看用
    if (showDiagLog) {
        WindowDialog(
            show = true,
            title = "短信诊断日志",
            summary = "只记录结构化特征，不含短信正文。可复制后反馈给开发者。",
            onDismissRequest = { showDiagLog = false },
        ) {
            Column(modifier = Modifier.padding(horizontal = 16.dp)) {
                val scrollState = rememberScrollState()
                Text(
                    text = diagLogText.ifBlank { "暂无日志。收到短信后这里会出现记录。" },
                    fontSize = 11.sp,
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = 360.dp)
                        .verticalScroll(scrollState),
                )
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = {
                            runCatching {
                                val cm = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE)
                                    as android.content.ClipboardManager
                                cm.setPrimaryClip(
                                    android.content.ClipData.newPlainText("diag", diagLogText)
                                )
                            }
                            showDiagLog = false
                            scope.launch {
                                snackbarHostState.showAppSnack("日志已复制", SnackType.Success)
                            }
                        },
                        modifier = Modifier.weight(1f),
                    ) { Text("复制") }
                    Button(
                        onClick = {
                            SmsDiagLog.clear(context)
                            diagLogText = ""
                        },
                        modifier = Modifier.weight(1f),
                    ) { Text("清空") }
                }
            }
        }
    }
}

/**
 * 规则库状态摘要:让用户知道当前是内置快照还是已更新过。
 *
 * 规则条数能直接反映"更新是否真的生效" —— 只看一条"更新成功"的提示
 * 无法区分"拉到的是同一个版本"和"换了一份新规则"。
 */
private fun buildRuleSummary(context: android.content.Context): String {
    val count = runCatching { SmsCodeRuleStore.rules(context).size }.getOrDefault(0)
    val source = if (SmsCodeRuleStore.hasRemoteCache(context)) "已更新的远端规则" else "内置快照"
    return "当前 $count 条规则 · $source。规则库决定能从短信中提取出哪些验证码模板"
}
