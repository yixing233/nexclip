package clip.yixing.sync.sms.rules

fun interface AppLabelResolver {
    fun findPackageNameByLabel(label: String): String?
}
