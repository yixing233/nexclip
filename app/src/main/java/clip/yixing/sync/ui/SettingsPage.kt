package clip.yixing.sync.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.util.SyncSettings
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
internal fun SettingsPage(scrollBehavior: ScrollBehavior, topPadding: Dp, bottomInnerPadding: Dp) {
    val context = LocalContext.current
    val prefs = remember { SyncSettings.prefs(context) }

    // 服务器配置输入
    val urlState = remember { TextFieldState(SyncSettings.serverUrl(context)) }
    val tokenState = remember { TextFieldState(SyncSettings.serverToken(context)) }
    var saved by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    var testing by remember { mutableStateOf(false) }
    var testResult by remember { mutableStateOf<Pair<Boolean, String>?>(null) }

    // 监听与历史
    var bootStart by remember { mutableStateOf(SyncSettings.bootStartEnabled(context)) }
    val historyOptions = SyncSettings.MAX_HISTORY_OPTIONS.toList()
    val historyLabels = historyOptions.map { "$it 条" }
    var historyIndex by remember {
        mutableStateOf(
            historyOptions.indexOf(SyncSettings.maxHistory(context)).coerceAtLeast(0)
        )
    }

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
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("服务器设置")
                    Spacer(Modifier.height(10.dp))
                    TextField(
                        state = urlState,
                        label = "服务器地址",
                        useLabelAsPlaceholder = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(Modifier.height(8.dp))
                    TextField(
                        state = tokenState,
                        label = "访问令牌",
                        useLabelAsPlaceholder = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(Modifier.height(10.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(
                            onClick = {
                                prefs.edit()
                                    .putString(SyncSettings.KEY_SERVER_URL, urlState.text.toString())
                                    .putString(SyncSettings.KEY_SERVER_TOKEN, tokenState.text.toString())
                                    .apply()
                                saved = true
                            },
                            modifier = Modifier.weight(1f)
                        ) {
                            Text("保存")
                        }
                        Button(
                            onClick = {
                                val url = urlState.text.toString().trim()
                                    .ifEmpty { SyncSettings.serverUrl(context) }
                                val token = tokenState.text.toString()
                                    .ifEmpty { SyncSettings.serverToken(context) }
                                if (url.isEmpty()) {
                                    testResult = false to "请先填写服务器地址"
                                    return@Button
                                }
                                testing = true
                                testResult = null
                                val api = SyncApi(url, token)
                                scope.launch {
                                    val (ok, msg) = withContext(Dispatchers.IO) {
                                        api.testConnection()
                                    }
                                    testing = false
                                    testResult = ok to msg
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
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                    testResult?.let { (ok, msg) ->
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = msg,
                            color = if (ok) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.error
                        )
                    }
                    if (saved) {
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = "已保存",
                            color = MiuixTheme.colorScheme.primary
                        )
                    }
                    Spacer(Modifier.height(6.dp))
                    Text(
                        text = "保存后,回到首页重新开启「持续监听剪贴板」即可生效",
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth()) {
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
                    HorizontalDivider(
                        color = MiuixTheme.colorScheme.dividerLine,
                        thickness = Dp.Hairline,
                        modifier = Modifier.padding(horizontal = 16.dp)
                    )
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
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("使用说明")
                    Spacer(Modifier.height(8.dp))
                    Text(
                        text = "1. 在 LSPosed 中启用「剪贴板同步」\n" +
                            "2. 作用域勾选「系统框架」和「剪贴板同步」应用本身\n" +
                            "3. 重启手机后生效\n" +
                            "4. 返回首页开启「持续监听剪贴板」"
                    )
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("关于")
                    Spacer(Modifier.height(8.dp))
                    Text(
                        text = "剪贴板同步 " + appVersion(context) + "\n基于 Miuix + LSPosed 构建"
                    )
                }
            }
            item {
                Spacer(Modifier.height(bottomInnerPadding))
            }
        }
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
