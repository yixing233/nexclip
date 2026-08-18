package clip.yixing.sync.service

/** 本地捕获/接收的剪贴板记录 */
data class CapturedClip(
    val text: String,
    val time: Long,
    val isFavorite: Boolean = false,
    val imageRef: String? = null,
    val id: String = "$time-${text.hashCode()}"
) {
    val isLink: Boolean get() = text.contains(Regex("https?://\\S+", RegexOption.IGNORE_CASE))
    val isImage: Boolean get() = imageRef != null || text.startsWith("data:image/")
}
