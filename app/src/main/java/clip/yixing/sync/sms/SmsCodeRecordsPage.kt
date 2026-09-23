package clip.yixing.sync.sms

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import clip.yixing.sync.ui.PageShell
import clip.yixing.sync.ui.SectionBlock
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun SmsCodeRecordsPage(
    bottomInnerPadding: Dp,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var records by remember { mutableStateOf(SmsCodeRecordStore.getAll(context)) }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) records = SmsCodeRecordStore.getAll(context)
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    PageShell(
        title = "短信记录",
        bottomInnerPadding = bottomInnerPadding,
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(imageVector = MiuixIcons.Back, contentDescription = "返回")
            }
        },
        actions = {
            if (records.isNotEmpty()) {
                top.yukonga.miuix.kmp.basic.TextButton(
                    text = "清空",
                    onClick = {
                        SmsCodeRecordStore.clear(context)
                        records = emptyList()
                    }
                )
            }
        }
    ) { scrollBehavior, topPadding ->
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 16.dp),
            contentPadding = PaddingValues(top = topPadding + 10.dp, bottom = bottomInnerPadding + 24.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            item {
                SectionBlock(title = "最近记录（最多保留 100 条）") {
                    Text(
                        text = "记录仅保存在本机，包含接收时间、识别状态和提取出的验证码；不保存短信正文或发件人。",
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            }
            if (records.isEmpty()) {
                item {
                    SectionBlock(title = "暂无记录") {
                        ArrowPreference(
                            title = "尚未收到短信记录",
                            summary = "开启短信验证码接收后，收到的短信会显示在这里，包括未识别出验证码的短信。"
                        )
                    }
                }
            } else {
                items(records, key = { "${it.timestamp}_${it.status}_${it.code.orEmpty()}" }) { record ->
                    val date = remember(record.timestamp) {
                        SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault()).format(Date(record.timestamp))
                    }
                    SectionBlock(title = date) {
                        androidx.compose.foundation.layout.Column(
                            modifier = Modifier.fillMaxWidth(),
                            verticalArrangement = Arrangement.spacedBy(4.dp)
                        ) {
                            val (label, detail) = when (record.status) {
                                SmsCodeRecordStore.Status.EXTRACTED -> "已提取验证码" to (record.code ?: "")
                                SmsCodeRecordStore.Status.NOT_FOUND -> "已收到，但未识别出验证码" to "检查短信验证码提取规则"
                                SmsCodeRecordStore.Status.DUPLICATE -> "收到重复验证码" to (record.code ?: "")
                            }
                            Text(label, fontWeight = FontWeight.SemiBold, color = MiuixTheme.colorScheme.onBackground)
                            Text(detail, color = MiuixTheme.colorScheme.onBackgroundVariant)
                        }
                    }
                }
            }
        }
    }
}
