package clip.yixing.sync.util

import android.content.ClipboardManager
import android.content.Context

object ClipboardTest {

    /** 读取当前剪贴板文本;任何异常(权限拒绝等)返回 null。 */
    fun readClipboard(context: Context): String? = runCatching {
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.primaryClip
            ?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)
            ?.coerceToText(context)
            ?.toString()
    }.getOrNull()
}