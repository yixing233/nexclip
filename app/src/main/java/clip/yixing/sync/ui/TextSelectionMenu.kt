package clip.yixing.sync.ui

import android.app.SearchManager
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.util.TypedValue
import android.view.ActionMode
import android.view.Gravity
import android.view.Menu
import android.view.MenuItem
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.EditText
import android.widget.TextView
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.viewinterop.AndroidView
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/*
 * 记录详情的正文用原生 EditText / TextView 承载,只为一件事:文本选中菜单。
 *
 * Compose 的 appendTextContextMenuComponents 只能把应用项追加到 components 末尾,
 * 并且一律以 SHOW_AS_ACTION_IF_ROOM 添加,于是可编辑态的浮动条被 剪切/复制/粘贴/全选
 * 占满,自定义项全被挤进 ⋮ 里;原生 ActionMode.Callback 才能指定 order 和
 * SHOW_AS_ACTION_ALWAYS,也能直接看到系统(含 HyperOS)自己填充的菜单项。
 */

// 自定义项 id,取一段不会和 android.R.id.* 相撞的常量
private const val ITEM_ID_PUSH = 0x4E43_0001
private const val ITEM_ID_SEARCH = 0x4E43_0002
private const val ITEM_ID_SHARE = 0x4E43_0003

// AOSP Editor 的 order 约定:4 剪切 / 5 复制 / 6 粘贴 / 7 分享 / 8 全选 / 9 替换 /
// 10 自动填充 / 11 粘贴为纯文本 / 50+ 次级智能操作 / 100+ 第三方 PROCESS_TEXT
private const val ORDER_PUSH = 1
private const val ORDER_SEARCH = 12
private const val ORDER_SHARE = 13

private const val MAX_SEARCH_QUERY_LENGTH = 200

/** 防止 EditText 与 TextFieldState 双向同步时互相触发 */
private class SyncGuard {
    var busy = false
}

/** 可编辑正文:与 TextFieldState 双向同步,进入编辑态时自动聚焦并拉起输入法 */
@Composable
fun NexClipEditableText(
    state: TextFieldState,
    textColor: Color,
    highlightColor: Color,
    fontSize: TextUnit,
    lineHeight: TextUnit,
    onPushText: ((String) -> Unit)?,
    modifier: Modifier = Modifier
) {
    val density = LocalDensity.current
    val sync = remember { SyncGuard() }
    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            EditText(ctx).apply {
                applyBodyLook(density, fontSize, lineHeight, textColor, highlightColor)
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
                inputType = InputType.TYPE_CLASS_TEXT or
                    InputType.TYPE_TEXT_FLAG_MULTI_LINE
                isSingleLine = false
                setHorizontallyScrolling(false)
                isVerticalScrollBarEnabled = false
                customSelectionActionModeCallback = NexClipSelectionCallback(
                    context = ctx,
                    selectedText = { selectedTextOrEmpty() },
                    onPush = onPushText
                )
                val initial = state.text.toString()
                setText(initial)
                setSelection(initial.length)
                addTextChangedListener(object : TextWatcher {
                    override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit

                    override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit

                    override fun afterTextChanged(s: Editable?) {
                        if (sync.busy) return
                        val typed = s?.toString() ?: ""
                        if (state.text.toString() == typed) return
                        sync.busy = true
                        state.edit { replace(0, length, typed) }
                        sync.busy = false
                    }
                })
                post {
                    requestFocus()
                    windowInsetsController?.show(WindowInsets.Type.ime())
                }
            }
        },
        update = { view ->
            // 外部改写(例如“取消”还原原文)时反向同步回 EditText
            val incoming = state.text.toString()
            if (!sync.busy && view.text.toString() != incoming) {
                sync.busy = true
                val caret = view.selectionStart.coerceIn(0, incoming.length)
                view.setText(incoming)
                view.setSelection(caret)
                sync.busy = false
            }
            view.applyBodyLook(density, fontSize, lineHeight, textColor, highlightColor)
        }
    )
}

/** 只读正文:textIsSelectable 的 TextView,能选中、能弹菜单,但不弹输入法、不画光标 */
@Composable
fun NexClipSelectableText(
    text: String,
    textColor: Color,
    highlightColor: Color,
    fontSize: TextUnit,
    lineHeight: TextUnit,
    onPushText: ((String) -> Unit)?,
    modifier: Modifier = Modifier
) {
    val density = LocalDensity.current
    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            TextView(ctx).apply {
                applyBodyLook(density, fontSize, lineHeight, textColor, highlightColor)
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
                setTextIsSelectable(true)
                customSelectionActionModeCallback = NexClipSelectionCallback(
                    context = ctx,
                    selectedText = { selectedTextOrEmpty() },
                    onPush = onPushText
                )
                setText(text)
            }
        },
        update = { view ->
            if (view.text.toString() != text) {
                view.text = text
            }
            view.applyBodyLook(density, fontSize, lineHeight, textColor, highlightColor)
        }
    )
}

/** 把 Miuix 的正文样式搬到原生 View 上,让它看起来还是同一段文字 */
private fun TextView.applyBodyLook(
    density: Density,
    fontSize: TextUnit,
    lineHeight: TextUnit,
    textColor: Color,
    highlightColor: Color
) {
    background = null
    setPadding(0, 0, 0, 0)
    gravity = Gravity.TOP or Gravity.START
    includeFontPadding = false
    setTextColor(textColor.toArgb())
    this.highlightColor = highlightColor.toArgb()
    setTextSize(TypedValue.COMPLEX_UNIT_PX, with(density) { fontSize.toPx() })
    val linePx = with(density) { lineHeight.toPx() }.roundToInt()
    if (linePx > 0) {
        setLineHeight(linePx)
    }
}

/** 取当前选区文本;选区为空返回空串 */
private fun TextView.selectedTextOrEmpty(): String {
    val content = text ?: return ""
    val start = min(selectionStart, selectionEnd).coerceIn(0, content.length)
    val end = max(selectionStart, selectionEnd).coerceIn(0, content.length)
    return if (end > start) content.subSequence(start, end).toString() else ""
}

/**
 * 选中菜单:系统项照旧,自定义项按 order 插到前面,「推送到设备」常驻浮动条。
 */
private class NexClipSelectionCallback(
    private val context: Context,
    private val selectedText: () -> String,
    private val onPush: ((String) -> Unit)?
) : ActionMode.Callback {

    override fun onCreateActionMode(mode: ActionMode, menu: Menu): Boolean = true

    override fun onPrepareActionMode(mode: ActionMode, menu: Menu): Boolean {
        // onPrepare 每次弹出都会重新走一遍,先清掉上一轮的自定义项,避免重复
        menu.removeItem(ITEM_ID_PUSH)
        menu.removeItem(ITEM_ID_SEARCH)
        menu.removeItem(ITEM_ID_SHARE)
        if (onPush != null) {
            menu.add(Menu.NONE, ITEM_ID_PUSH, ORDER_PUSH, "推送到设备")
                .setShowAsActionFlags(MenuItem.SHOW_AS_ACTION_ALWAYS)
        }
        menu.add(Menu.NONE, ITEM_ID_SEARCH, ORDER_SEARCH, "搜索")
            .setShowAsActionFlags(MenuItem.SHOW_AS_ACTION_IF_ROOM)
        // 系统自己给了“分享”就不再加一个重复项
        if (menu.findItem(android.R.id.shareText) == null) {
            menu.add(Menu.NONE, ITEM_ID_SHARE, ORDER_SHARE, "分享")
                .setShowAsActionFlags(MenuItem.SHOW_AS_ACTION_IF_ROOM)
        }
        return true
    }

    override fun onActionItemClicked(mode: ActionMode, item: MenuItem): Boolean {
        // finish() 会清掉选区,先把文本取出来
        val text = selectedText()
        return when (item.itemId) {
            ITEM_ID_PUSH -> {
                mode.finish()
                if (text.isNotBlank()) onPush?.invoke(text)
                true
            }
            ITEM_ID_SEARCH -> {
                mode.finish()
                if (text.isNotBlank()) searchWeb(context, text)
                true
            }
            ITEM_ID_SHARE -> {
                mode.finish()
                if (text.isNotBlank()) shareSelectedText(context, text)
                true
            }
            else -> false
        }
    }

    override fun onDestroyActionMode(mode: ActionMode) = Unit
}

/** 用系统搜索;没有能处理 ACTION_WEB_SEARCH 的应用时退回浏览器打开 Bing */
private fun searchWeb(context: Context, rawText: String) {
    val query = rawText.trim().take(MAX_SEARCH_QUERY_LENGTH)
    if (query.isEmpty()) return
    val searchIntent = Intent(Intent.ACTION_WEB_SEARCH).apply {
        putExtra(SearchManager.QUERY, query)
        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    try {
        context.startActivity(searchIntent)
    } catch (_: ActivityNotFoundException) {
        val fallback = Intent(
            Intent.ACTION_VIEW,
            Uri.parse("https://www.bing.com/search?q=" + Uri.encode(query))
        ).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        try {
            context.startActivity(fallback)
        } catch (_: ActivityNotFoundException) {
            // 连浏览器都没有,静默放弃
        }
    }
}

/** 系统分享面板 */
private fun shareSelectedText(context: Context, text: String) {
    val sendIntent = Intent(Intent.ACTION_SEND).apply {
        type = "text/plain"
        putExtra(Intent.EXTRA_TEXT, text)
    }
    val chooser = Intent.createChooser(sendIntent, "分享选中文本").apply {
        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    try {
        context.startActivity(chooser)
    } catch (_: ActivityNotFoundException) {
        // 没有可分享的目标,静默放弃
    }
}
