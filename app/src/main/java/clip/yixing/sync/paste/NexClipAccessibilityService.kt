package clip.yixing.sync.paste

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.AccessibilityServiceInfo
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.text.TextUtils
import android.util.Log
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow

/**
 * 一键粘贴的执行者 + 输入框焦点探测器。
 *
 * 隐私边界(实现与后续修改都必须守住):
 * - [onAccessibilityEvent] 只记录「当前前台包名」与「当前是否有可编辑控件聚焦」两个值,
 *   绝不读取、缓存或上报任何输入框内容;
 * - 只有用户主动点击粘贴时才 [findFocus] 查询节点树,平时不遍历窗口。
 */
class NexClipAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        runCatching {
            serviceInfo = (serviceInfo ?: AccessibilityServiceInfo()).apply {
                eventTypes = AccessibilityEvent.TYPE_VIEW_FOCUSED or
                    AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED
                feedbackType = AccessibilityServiceInfo.FEEDBACK_GENERIC
                flags = flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
                notificationTimeout = 100L
            }
        }
        instance = this
        isConnected.value = true
        Log.i(TAG, "accessibility service connected")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        event ?: return
        val pkg = event.packageName?.toString().orEmpty()
        // 自家窗口(含悬浮球面板)不参与焦点判定,否则面板一弹出就把自己藏起来
        if (pkg == packageName) return

        when (event.eventType) {
            AccessibilityEvent.TYPE_VIEW_FOCUSED -> {
                if (pkg.isNotBlank()) foregroundPackage.value = pkg
                val byClassName = event.className?.let { isEditableClassName(it) } == true
                val bySource = runCatching { event.source?.isEditable == true }.getOrDefault(false)
                editableFocused.value = byClassName || bySource
            }

            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED -> {
                if (pkg.isNotBlank()) foregroundPackage.value = pkg
                // 换窗口/换应用一律视为焦点丢失,由新窗口里的聚焦事件重新点亮
                editableFocused.value = false
            }
        }
    }

    override fun onInterrupt() = Unit

    override fun onUnbind(intent: Intent?): Boolean {
        markDisconnected()
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        markDisconnected()
        super.onDestroy()
    }

    private fun markDisconnected() {
        if (instance == this) instance = null
        isConnected.value = false
        editableFocused.value = false
    }

    /**
     * 把 [text] 插入当前聚焦输入框的光标处,完全不经过系统剪贴板。
     *
     * 通知栏收起、悬浮面板消失之后焦点回归目标输入框需要一点时间,所以做重试循环。
     */
    suspend fun injectText(text: String): Boolean {
        if (text.isEmpty()) return false
        val node = awaitFocusedEditable() ?: return false
        return try {
            val existing = node.text?.toString().orEmpty()
            val selStart = node.textSelectionStart
            val selEnd = node.textSelectionEnd
            // 选区无效(-1)时一律追加到末尾,避免把整段原文替换掉
            val start = if (selStart in 0..existing.length) selStart else existing.length
            val end = if (selEnd in 0..existing.length) selEnd else start
            val from = minOf(start, end)
            val to = maxOf(start, end)
            val merged = existing.substring(0, from) + text + existing.substring(to)
            val args = Bundle().apply {
                putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, merged)
            }
            val ok = node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
            if (ok) {
                val caret = from + text.length
                val selectionArgs = Bundle().apply {
                    putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, caret)
                    putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, caret)
                }
                node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selectionArgs)
            }
            Log.d(TAG, "injectText result=$ok")
            ok
        } catch (t: Throwable) {
            Log.d(TAG, "injectText failed: ${t.message}")
            false
        }
    }

    /** 对聚焦输入框执行系统粘贴动作(内容需已在剪贴板里),图片与注入失败时的回退路径 */
    suspend fun performPasteAction(): Boolean {
        val node = awaitFocusedEditable() ?: return false
        return try {
            val ok = node.performAction(AccessibilityNodeInfo.ACTION_PASTE)
            Log.d(TAG, "performPasteAction result=$ok")
            ok
        } catch (t: Throwable) {
            Log.d(TAG, "performPasteAction failed: ${t.message}")
            false
        }
    }

    /** 轮询等待可编辑焦点出现 */
    private suspend fun awaitFocusedEditable(): AccessibilityNodeInfo? {
        repeat(FOCUS_RETRY_TIMES) { attempt ->
            val node = runCatching { findFocus(AccessibilityNodeInfo.FOCUS_INPUT) }.getOrNull()
            if (node != null && node.isEditable) return node
            if (attempt < FOCUS_RETRY_TIMES - 1) delay(FOCUS_RETRY_INTERVAL_MILLIS)
        }
        Log.d(TAG, "no editable focus after ${FOCUS_RETRY_TIMES * FOCUS_RETRY_INTERVAL_MILLIS}ms")
        return null
    }

    private fun isEditableClassName(className: CharSequence): Boolean =
        TextUtils.indexOf(className, "EditText") >= 0 ||
            TextUtils.indexOf(className, "TextInputLayout") >= 0 ||
            TextUtils.indexOf(className, "SearchView") >= 0

    companion object {
        private const val TAG = "NexClipA11y"

        /** 100ms × 12 ≈ 1.2s,覆盖通知栏收起 / 悬浮面板消失后焦点回归目标输入框的延迟 */
        private const val FOCUS_RETRY_TIMES = 12
        private const val FOCUS_RETRY_INTERVAL_MILLIS = 100L

        @Volatile
        var instance: NexClipAccessibilityService? = null
            private set

        /** 无障碍服务是否已连接(设置页状态 + PasteEngine 调度依据) */
        val isConnected = MutableStateFlow(false)

        /** 当前是否有可编辑控件聚焦(悬浮球显隐依据) */
        val editableFocused = MutableStateFlow(false)

        /** 当前前台应用包名(仅包名,不含任何窗口内容) */
        val foregroundPackage = MutableStateFlow("")

        /**
         * 读系统设置判断本服务是否被启用。
         *
         * [isConnected] 只有在进程存活时才可信,设置页冷启动时得靠这个兜底。
         */
        fun isEnabledInSystem(context: Context): Boolean {
            val expected = ComponentName(context, NexClipAccessibilityService::class.java)
            val enabled = runCatching {
                Settings.Secure.getString(
                    context.contentResolver,
                    Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
                )
            }.getOrNull() ?: return false
            return enabled.split(':').any { entry ->
                ComponentName.unflattenFromString(entry) == expected
            }
        }

        /** 服务已连接且可以真正执行注入 */
        fun isUsable(): Boolean = instance != null && isConnected.value
    }
}
