package clip.yixing.sync.smartaction

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.ui.PageShell
import clip.yixing.sync.ui.SectionBlock
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Switch
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.Clear
import top.yukonga.miuix.kmp.icon.extended.Delete
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.window.WindowDialog
import java.util.UUID

/**
 * 智能动作与应用直达设置二级子页面
 */
@Composable
fun SmartActionSettingsPage(
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var masterEnabled by remember { mutableStateOf(SyncSettings.isSmartActionMasterEnabled(context)) }
    var codeEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_CODE)) }
    var deeplinkEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_DEEPLINK)) }
    var urlEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_URL)) }
    var commandEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_COMMAND)) }
    var phoneEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_PHONE)) }
    var emailEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_EMAIL)) }
    var expressEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_EXPRESS)) }
    var colorEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_COLOR)) }
    var mapEnabled by remember { mutableStateOf(SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_MAP)) }

    var customRules by remember { mutableStateOf(SyncSettings.customSmartActionRules(context)) }

    // 自定义规则添加/编辑弹窗状态
    var isRuleDialogOpen by remember { mutableStateOf(false) }
    var editingRule by remember { mutableStateOf<CustomSmartActionRule?>(null) }
    var dialogName by remember { mutableStateOf("") }
    var dialogPattern by remember { mutableStateOf("") }
    var dialogType by remember { mutableStateOf(SmartActionType.URL) }
    var dialogTemplate by remember { mutableStateOf("") }

    val openAddDialog = {
        editingRule = null
        dialogName = ""
        dialogPattern = ""
        dialogType = SmartActionType.URL
        dialogTemplate = ""
        isRuleDialogOpen = true
    }

    val openEditDialog = { rule: CustomSmartActionRule ->
        editingRule = rule
        dialogName = rule.name
        dialogPattern = rule.pattern
        dialogType = rule.type
        dialogTemplate = rule.targetTemplate
        isRuleDialogOpen = true
    }

    PageShell(
        title = "智能动作与应用直达",
        bottomInnerPadding = bottomInnerPadding,
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(
                    imageVector = MiuixIcons.Normal.Back,
                    contentDescription = "返回"
                )
            }
        },
        actions = {
            IconButton(onClick = openAddDialog) {
                Icon(
                    imageVector = LucideIcons.Plus,
                    contentDescription = "添加规则",
                    tint = MiuixTheme.colorScheme.primary
                )
            }
        }
    ) { scrollBehavior, topPadding ->
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 16.dp)
                .nestedScroll(scrollBehavior.nestedScrollConnection),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(
                top = topPadding + 10.dp,
                bottom = bottomInnerPadding + 24.dp
            )
        ) {
            // 1. 总开关
            item {
                SectionBlock(title = "全局开关", insideMargin = androidx.compose.foundation.layout.PaddingValues()) {
                    SwitchPreference(
                        title = "启用智能动作识别",
                        summary = "自动分析剪贴板文本并提供一键直达小胶囊",
                        checked = masterEnabled,
                        onCheckedChange = { checked ->
                            masterEnabled = checked
                            SyncSettings.setSmartActionMasterEnabled(context, checked)
                        }
                    )
                }
            }

            // 2. 内置智能识别开关组
            if (masterEnabled) {
                item {
                    Spacer(Modifier.height(16.dp))
                    SectionBlock(title = "内置智能动作", insideMargin = androidx.compose.foundation.layout.PaddingValues()) {
                        SwitchPreference(
                            title = "短信验证码提取",
                            summary = "自动提取 4~8 位验证码并提供快捷复制",
                            checked = codeEnabled,
                            onCheckedChange = {
                                codeEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_CODE, it)
                            }
                        )
                        SwitchPreference(
                            title = "深度链接与主流应用直达",
                            summary = "识别哔哩哔哩、GitHub、淘宝、京东、抖音、小红书等",
                            checked = deeplinkEnabled,
                            onCheckedChange = {
                                deeplinkEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_DEEPLINK, it)
                            }
                        )
                        SwitchPreference(
                            title = "网页链接访问",
                            summary = "识别通用 HTTP/HTTPS 网址并在浏览器中打开",
                            checked = urlEnabled,
                            onCheckedChange = {
                                urlEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_URL, it)
                            }
                        )
                        SwitchPreference(
                            title = "口令识别与唤醒",
                            summary = "识别淘口令、抖音口令并一键启动对应 App",
                            checked = commandEnabled,
                            onCheckedChange = {
                                commandEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_COMMAND, it)
                            }
                        )
                        SwitchPreference(
                            title = "电话拨打与短信",
                            summary = "识别手机与固定电话号码",
                            checked = phoneEnabled,
                            onCheckedChange = {
                                phoneEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_PHONE, it)
                            }
                        )
                        SwitchPreference(
                            title = "电子邮箱发送",
                            summary = "识别 Email 地址并调起邮件应用",
                            checked = emailEnabled,
                            onCheckedChange = {
                                emailEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_EMAIL, it)
                            }
                        )
                        SwitchPreference(
                            title = "快递单号查询",
                            summary = "识别顺丰及主流物流运单号",
                            checked = expressEnabled,
                            onCheckedChange = {
                                expressEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_EXPRESS, it)
                            }
                        )
                        SwitchPreference(
                            title = "色彩代码预览",
                            summary = "实时解析 Hex 颜色代码并在卡片上呈现色彩圆点",
                            checked = colorEnabled,
                            onCheckedChange = {
                                colorEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_COLOR, it)
                            }
                        )
                        SwitchPreference(
                            title = "地理位置与地图导航",
                            summary = "识别中文详细地址并一键唤起地图导航",
                            checked = mapEnabled,
                            onCheckedChange = {
                                mapEnabled = it
                                SyncSettings.setSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_MAP, it)
                            }
                        )
                    }
                }

                // 3. 自定义规则与应用直达
                item {
                    Spacer(Modifier.height(16.dp))
                    SectionBlock(
                        title = "自定义规则与应用直达",
                        insideMargin = androidx.compose.foundation.layout.PaddingValues(),
                        trailing = {
                            Text(
                                text = "新建",
                                color = MiuixTheme.colorScheme.primary,
                                fontSize = 14.sp,
                                modifier = Modifier
                                    .clickable { openAddDialog() }
                                    .padding(horizontal = 6.dp, vertical = 2.dp)
                            )
                        }
                    ) {
                        if (customRules.isEmpty()) {
                            Text(
                                text = "暂无自定义规则。点击下方按钮添加自定义正则匹配、目标 URL 或 App Scheme 唤起规则。",
                                color = MiuixTheme.colorScheme.onBackgroundVariant,
                                fontSize = 13.sp,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp)
                            )
                        } else {
                            Column {
                                customRules.forEachIndexed { index, rule ->
                                    CustomRuleItemCard(
                                        rule = rule,
                                        onToggleEnabled = { enabled ->
                                            val updated = customRules.map {
                                                if (it.id == rule.id) it.copy(enabled = enabled) else it
                                            }
                                            customRules = updated
                                            SyncSettings.setCustomSmartActionRules(context, updated)
                                        },
                                        onEdit = { openEditDialog(rule) },
                                        onDelete = {
                                            val updated = customRules.filter { it.id != rule.id }
                                            customRules = updated
                                            SyncSettings.setCustomSmartActionRules(context, updated)
                                            scope.launch { snackbarHostState.showAppSnack("已删除规则: ${rule.name}", SnackType.Info) }
                                        }
                                    )
                                    if (index < customRules.size - 1) {
                                        HorizontalDivider(color = MiuixTheme.colorScheme.dividerLine, thickness = Dp.Hairline)
                                    }
                                }
                            }
                        }

                        Box(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp)) {
                            Button(
                                onClick = openAddDialog,
                                colors = ButtonDefaults.buttonColorsPrimary(),
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Icon(
                                    imageVector = LucideIcons.Plus,
                                    contentDescription = null,
                                    modifier = Modifier.size(16.dp)
                                )
                                Spacer(Modifier.width(6.dp))
                                Text(text = "添加自定义智能动作")
                            }
                        }
                    }
                }
            }
        }
    }

    // 4. 自定义规则添加/编辑对话框
    if (isRuleDialogOpen) {
        WindowDialog(
            show = isRuleDialogOpen,
            title = if (editingRule != null) "编辑自定义动作" else "新建自定义动作",
            summary = "配置正则表达式与执行模板",
            onDismissRequest = { isRuleDialogOpen = false }
        ) {
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                TextField(
                    value = dialogName,
                    onValueChange = { dialogName = it },
                    label = "动作名称 (例如: 知乎搜索)",
                    modifier = Modifier.fillMaxWidth()
                )

                TextField(
                    value = dialogPattern,
                    onValueChange = { dialogPattern = it },
                    label = "匹配正则 (例如: .* 或 (BV\\w+))",
                    modifier = Modifier.fillMaxWidth()
                )

                // 快捷预设提示
                Row(
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    PresetChip(label = "匹配任意 (.*)") {
                        dialogPattern = ".*"
                    }
                    PresetChip(label = "匹配纯数字 (\\d+)") {
                        dialogPattern = "\\d+"
                    }
                }

                Text(
                    text = "动作类型",
                    style = MiuixTheme.textStyles.footnote1,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    SmartActionType.entries.forEach { type ->
                        val isSelected = dialogType == type
                        Box(
                            modifier = Modifier
                                .weight(1f)
                                .clip(RoundedCornerShape(8.dp))
                                .background(if (isSelected) MiuixTheme.colorScheme.primary.copy(alpha = 0.15f) else MiuixTheme.colorScheme.surfaceContainer)
                                .clickable { dialogType = type }
                                .padding(vertical = 8.dp, horizontal = 4.dp),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                text = when (type) {
                                    SmartActionType.URL -> "网页链接"
                                    SmartActionType.SCHEME -> "App Scheme"
                                    SmartActionType.COPY -> "提取复制"
                                },
                                color = if (isSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface,
                                fontSize = 12.sp,
                                fontWeight = if (isSelected) FontWeight.SemiBold else FontWeight.Normal,
                                maxLines = 1
                            )
                        }
                    }
                }

                if (dialogType != SmartActionType.COPY) {
                    TextField(
                        value = dialogTemplate,
                        onValueChange = { dialogTemplate = it },
                        label = if (dialogType == SmartActionType.URL) "目标 URL (支持 {match} / {1})" else "目标 Scheme (支持 {match} / {1})",
                        modifier = Modifier.fillMaxWidth()
                    )
                    Text(
                        text = "提示: {match} 会自动替换为匹配文本，并进行 URL 编码。",
                        style = MiuixTheme.textStyles.footnote2,
                        color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f)
                    )
                }

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 10.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Button(
                        onClick = { isRuleDialogOpen = false },
                        colors = ButtonDefaults.buttonColors(
                            color = MiuixTheme.colorScheme.surfaceContainerHigh,
                            contentColor = MiuixTheme.colorScheme.onSurface
                        ),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("取消")
                    }
                    Button(
                        onClick = {
                            val name = dialogName.trim()
                            val pattern = dialogPattern.trim()
                            if (name.isBlank()) {
                                scope.launch { snackbarHostState.showAppSnack("请输入动作名称", SnackType.Error) }
                                return@Button
                            }
                            if (pattern.isBlank()) {
                                scope.launch { snackbarHostState.showAppSnack("请输入匹配正则表达式", SnackType.Error) }
                                return@Button
                            }
                            val regexValid = runCatching { Regex(pattern) }.isSuccess
                            if (!regexValid) {
                                scope.launch { snackbarHostState.showAppSnack("正则表达式格式有误", SnackType.Error) }
                                return@Button
                            }

                            val newRule = CustomSmartActionRule(
                                id = editingRule?.id ?: UUID.randomUUID().toString(),
                                name = name,
                                pattern = pattern,
                                type = dialogType,
                                targetTemplate = dialogTemplate.trim(),
                                enabled = editingRule?.enabled ?: true
                            )

                            val updated = if (editingRule != null) {
                                customRules.map { if (it.id == newRule.id) newRule else it }
                            } else {
                                customRules + newRule
                            }
                            customRules = updated
                            SyncSettings.setCustomSmartActionRules(context, updated)
                            isRuleDialogOpen = false
                            scope.launch { snackbarHostState.showAppSnack(if (editingRule != null) "已更新规则" else "已添加规则", SnackType.Success) }
                        },
                        colors = ButtonDefaults.buttonColorsPrimary(),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("保存")
                    }
                }
            }
        }
    }
}

@Composable
private fun PresetChip(label: String, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(MiuixTheme.colorScheme.surfaceContainer)
            .clickable(onClick = onClick)
            .padding(horizontal = 8.dp, vertical = 4.dp)
    ) {
        Text(
            text = label,
            fontSize = 11.sp,
            color = MiuixTheme.colorScheme.primary
        )
    }
}

@Composable
private fun CustomRuleItemCard(
    rule: CustomSmartActionRule,
    onToggleEnabled: (Boolean) -> Unit,
    onEdit: () -> Unit,
    onDelete: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onEdit)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = rule.name,
                    fontWeight = FontWeight.Medium,
                    fontSize = 15.sp,
                    color = MiuixTheme.colorScheme.onSurface
                )
                Spacer(Modifier.width(6.dp))
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .background(Color(0xFF8B5CF6).copy(alpha = 0.12f))
                        .padding(horizontal = 6.dp, vertical = 2.dp)
                ) {
                    Text(
                        text = rule.type.label,
                        color = Color(0xFF8B5CF6),
                        fontSize = 10.sp,
                        fontWeight = FontWeight.Medium
                    )
                }
            }
            Spacer(Modifier.height(2.dp))
            Text(
                text = "正则: ${rule.pattern}",
                style = MiuixTheme.textStyles.footnote2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
            if (rule.targetTemplate.isNotBlank()) {
                Text(
                    text = "目标: ${rule.targetTemplate}",
                    style = MiuixTheme.textStyles.footnote2,
                    color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }

        IconButton(onClick = onDelete) {
            Icon(
                imageVector = LucideIcons.Trash2,
                contentDescription = "删除",
                tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.5f),
                modifier = Modifier.size(16.dp)
            )
        }

        Switch(
            checked = rule.enabled,
            onCheckedChange = onToggleEnabled
        )
    }
}
