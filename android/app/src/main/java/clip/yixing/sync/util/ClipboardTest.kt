package clip.yixing.sync.util

import android.content.ClipboardManager
import android.content.Context

object ClipboardTest {

    /** 读取当前系统剪贴板纯文本;任何异常(权限拒绝、非文本、图片 Uri 等)返回 null。 */
    fun readClipboard(context: Context): String? = runCatching {
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clip = cm.primaryClip?.takeIf { it.itemCount > 0 } ?: return@runCatching null
        val item = clip.getItemAt(0)

        // 关键防护: 仅读取纯文本类型, 严禁对 Uri / 二进制图片流使用 coerceToText 造成乱码
        if (item.text != null) {
            item.text.toString()
        } else if (item.uri == null && clip.description.hasMimeType("text/*")) {
            item.coerceToText(context)?.toString()
        } else {
            null
        }
    }.getOrNull()
}