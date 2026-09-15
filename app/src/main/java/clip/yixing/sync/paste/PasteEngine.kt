package clip.yixing.sync.paste

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.PersistableBundle
import android.util.Log
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.shizuku.ShizukuClipboardManager
import clip.yixing.sync.shizuku.ShizukuPermission
import clip.yixing.sync.util.ImageLoader
import clip.yixing.sync.util.PasteMethod
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

/** 一次粘贴的结果,[message] 直接拿去弹 Toast / Snackbar,避免各调用方各写一套文案 */
enum class PasteOutcome(val message: String) {
    /** 无障碍直接写进了输入框,用户自己的剪贴板没被动过 */
    INJECTED("已粘贴到输入框"),

    /** 内容进了剪贴板,再由无障碍 / 按键触发系统粘贴 */
    PASTED("已粘贴"),

    /** 没有可用的粘贴后端,只把内容放进了剪贴板 */
    CLIPBOARD_ONLY("已复制,请长按输入框粘贴"),

    /** 连剪贴板都没写进去 */
    FAILED("粘贴失败,请重试")
}

/** 当前可用的粘贴后端,用于通知栏按钮显隐与设置页状态文案 */
enum class PasteBackend { ACCESSIBILITY, SHIZUKU, NONE }

/**
 * 一键粘贴的统一调度入口,是 [NexClipAccessibilityService] 与 [ShizukuKeyInjector] 唯一的对外门面。
 *
 * AUTO 模式的优先级:
 * 1. 无障碍可用 —— 纯文本且用户要求「保留我的剪贴板」时走 [NexClipAccessibilityService.injectText],
 *    完全不碰系统剪贴板;图片或注入失败则退到「写剪贴板 + ACTION_PASTE」;
 * 2. 否则 Shizuku 已授权 —— 写剪贴板后模拟粘贴按键;
 * 3. 都不可用 —— 只写剪贴板,由调用方提示用户手动粘贴。
 */
object PasteEngine {
    private const val TAG = "PasteEngine"

    /** 写完剪贴板到按下粘贴键之间的等待,给系统剪贴板服务同步留出时间 */
    private const val CLIPBOARD_SETTLE_MILLIS = 400L

    /** 当前设置下真正能用的后端 */
    fun availableBackend(context: Context): PasteBackend {
        val method = SyncSettings.pasteMethod(context)
        val allowAccessibility = method == PasteMethod.AUTO || method == PasteMethod.ACCESSIBILITY
        val allowShizuku = method == PasteMethod.AUTO || method == PasteMethod.SHIZUKU
        return when {
            allowAccessibility && NexClipAccessibilityService.isUsable() -> PasteBackend.ACCESSIBILITY
            allowShizuku && ShizukuPermission.isGranted() -> PasteBackend.SHIZUKU
            else -> PasteBackend.NONE
        }
    }

    fun isAvailable(context: Context): Boolean = availableBackend(context) != PasteBackend.NONE

    /**
     * 把 [clip] 送进当前聚焦的输入框。
     *
     * 无论走哪条路,写剪贴板前都会先登记内部复制标记 —— 这是防回环的关键,
     * 少了它监听器会把粘贴写入的内容当成一次新捕获重新上传。
     */
    suspend fun paste(context: Context, clip: CapturedClip): PasteOutcome {
        val isImage = clip.isImage
        if (!isImage && clip.text.isBlank()) return PasteOutcome.FAILED

        return when (availableBackend(context)) {
            PasteBackend.ACCESSIBILITY -> pasteByAccessibility(context, clip, isImage)
            PasteBackend.SHIZUKU -> pasteByShizukuKey(context, clip)
            PasteBackend.NONE -> if (writeToClipboard(context, clip)) {
                PasteOutcome.CLIPBOARD_ONLY
            } else {
                PasteOutcome.FAILED
            }
        }
    }

    /** 只写剪贴板不触发粘贴,供悬浮面板的「复制」小动作使用 */
    suspend fun copyOnly(context: Context, clip: CapturedClip): Boolean =
        writeToClipboard(context, clip)

    private suspend fun pasteByAccessibility(
        context: Context,
        clip: CapturedClip,
        isImage: Boolean
    ): PasteOutcome {
        val service = NexClipAccessibilityService.instance
        // 纯文本 + 用户要求保留剪贴板 -> 直接注入,不动系统剪贴板
        if (service != null && !isImage && SyncSettings.pasteKeepClipboard(context)) {
            if (service.injectText(clip.text)) return PasteOutcome.INJECTED
            Log.d(TAG, "injectText failed, falling back to clipboard + ACTION_PASTE")
        }

        if (!writeToClipboard(context, clip)) return PasteOutcome.FAILED
        if (service != null && service.performPasteAction()) return PasteOutcome.PASTED
        return PasteOutcome.CLIPBOARD_ONLY
    }

    private suspend fun pasteByShizukuKey(context: Context, clip: CapturedClip): PasteOutcome {
        if (!writeToClipboard(context, clip)) return PasteOutcome.FAILED
        delay(CLIPBOARD_SETTLE_MILLIS)
        val ok = withContext(Dispatchers.IO) { ShizukuKeyInjector.pasteKey() }
        return if (ok) PasteOutcome.PASTED else PasteOutcome.CLIPBOARD_ONLY
    }

    /**
     * 写系统剪贴板,顺序与 [ClipboardMonitorService] 收到远端推送时保持一致:Shizuku 静默写入优先,
     * 失败再退回普通 [ClipboardManager]。
     */
    private suspend fun writeToClipboard(context: Context, clip: CapturedClip): Boolean {
        if (clip.isImage) {
            // copyImageToClipboard 内部已经走 copyToClipboardInternal,内部复制标记已登记
            return ImageLoader.copyImageToClipboard(context, clip.imageRef, clip.text)
        }

        val text = clip.text
        if (text.isBlank()) return false
        ClipboardMonitorService.registerInternalCopy(text)

        val data = ClipData.newPlainText("NexClip", text)
        runCatching {
            val extras = data.description?.extras ?: PersistableBundle()
            extras.putBoolean("is_nexclip_internal", true)
            extras.putString("source_pkg", context.packageName)
            data.description?.extras = extras
        }

        if (withContext(Dispatchers.IO) { ShizukuClipboardManager.setPrimaryClip(data) }) return true

        return withContext(Dispatchers.Main) {
            runCatching {
                ClipboardMonitorService.copyToClipboardInternal(context, data, rawText = text)
            }.isSuccess
        }
    }
}
