package clip.yixing.sync.util

import android.content.Context
import android.content.SharedPreferences

/**
 * 应用设置项。与剪贴板捕获历史共用同一 SharedPreferences 文件。
 */
object SyncSettings {

    const val PREFS_NAME = "sync_clipboard"

    const val KEY_SERVER_URL = "server_url"
    const val KEY_PAIRED = "paired"
    const val KEY_DEVICE_ID = "device_id"
    const val KEY_DEVICE_TOKEN = "device_token"
    const val KEY_DEVICE_NAME = "device_name"
    const val KEY_BOOT_START_ENABLED = "boot_start_enabled"
    const val KEY_AUTO_CHECK_UPDATE = "auto_check_update"
    const val KEY_UPDATE_SOURCE = "update_source"
    const val UPDATE_SOURCE_GITHUB = 0
    const val UPDATE_SOURCE_DIRECT = 1
    const val KEY_FLOATING_BOTTOM_BAR = "floating_bottom_bar"
    const val KEY_PREDICTIVE_BACK = "predictive_back"
    const val KEY_HIDE_FROM_RECENTS = "hide_from_recents"
    const val KEY_NOTIFICATION_ENABLED = "notification_enabled"
    const val KEY_NOTIFICATION_STYLE = "notification_style"
    const val KEY_CAPTURE_METHOD = "capture_method"
    const val KEY_MAX_HISTORY = "max_history"
    const val KEY_SEARCH_HISTORY = "search_history"

    const val KEY_SMART_ACTION_MASTER = "smart_action_master"
    const val KEY_SMART_ACTION_CODE = "smart_action_code"
    const val KEY_SMART_ACTION_DEEPLINK = "smart_action_deeplink"
    const val KEY_SMART_ACTION_URL = "smart_action_url"
    const val KEY_SMART_ACTION_COMMAND = "smart_action_command"
    const val KEY_SMART_ACTION_PHONE = "smart_action_phone"
    const val KEY_SMART_ACTION_EMAIL = "smart_action_email"
    const val KEY_SMART_ACTION_EXPRESS = "smart_action_express"
    const val KEY_SMART_ACTION_COLOR = "smart_action_color"
    const val KEY_SMART_ACTION_MAP = "smart_action_map"
    const val KEY_SMART_ACTION_CUSTOM_RULES = "smart_action_custom_rules"

    const val KEY_FILTER_KEYWORDS = "filter_keywords"
    const val KEY_FILTER_PACKAGES = "filter_packages"
    const val KEY_IGNORE_SENSITIVE = "ignore_sensitive"

    const val KEY_HYPEROS_OUTER_GLOW = "hyperos_outer_glow"
    const val KEY_HYPEROS_GLOW_COLOR = "hyperos_glow_color"
    const val KEY_HYPEROS_ISLAND_TIMEOUT = "hyperos_island_timeout"

    val GLOW_COLORS = listOf(
        "#006EFF" to "经典科技蓝",
        "#10B981" to "灵动翡翠绿",
        "#8B5CF6" to "暗夜魅影紫",
        "#F59E0B" to "活力耀阳橙",
        "#EF4444" to "极光珊瑚红",
        "#EC4899" to "流光樱花粉"
    )

    val ISLAND_TIMEOUT_OPTIONS = intArrayOf(10, 30, 60, 180, 300, 3600)
    val ISLAND_TIMEOUT_LABELS = listOf("10 秒", "30 秒 (推荐)", "1 分钟", "3 分钟", "5 分钟", "常驻展示 (1小时)")

    const val DEFAULT_MAX_HISTORY = 50
    val MAX_HISTORY_OPTIONS = intArrayOf(20, 50, 100, 200)

    /** 检测当前系统是否为小米澎湃OS (HyperOS) */
    fun isHyperOs(): Boolean {
        val osVersion = getSystemProp("ro.mi.os.version.name")
        val miuiVersion = getSystemProp("ro.miui.ui.version.name")
        return osVersion.isNotBlank() || miuiVersion.isNotBlank()
    }

    fun getSystemProp(key: String, defaultValue: String = ""): String {
        return try {
            val clz = Class.forName("android.os.SystemProperties")
            val getMethod = clz.getMethod("get", String::class.java, String::class.java)
            getMethod.invoke(null, key, defaultValue) as String
        } catch (_: Exception) {
            defaultValue
        }
    }

    fun notificationStyle(context: Context): NotificationStyle {
        val defaultKey = if (isHyperOs()) NotificationStyle.HYPEROS_ISLAND.key else NotificationStyle.ANDROID_LIVE.key
        val key = prefs(context).getString(KEY_NOTIFICATION_STYLE, defaultKey)
        return NotificationStyle.fromKey(key)
    }

    fun setNotificationStyle(context: Context, style: NotificationStyle) {
        prefs(context).edit().putString(KEY_NOTIFICATION_STYLE, style.key).apply()
    }

    fun captureMethod(context: Context): CaptureMethod {
        val key = prefs(context).getString(KEY_CAPTURE_METHOD, CaptureMethod.AUTO.key)
        return CaptureMethod.fromKey(key)
    }

    fun setCaptureMethod(context: Context, method: CaptureMethod) {
        prefs(context).edit().putString(KEY_CAPTURE_METHOD, method.key).apply()
    }

    fun isHyperOsOuterGlow(context: Context): Boolean =
        prefs(context).getBoolean(KEY_HYPEROS_OUTER_GLOW, true)

    fun setHyperOsOuterGlow(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_HYPEROS_OUTER_GLOW, enabled).apply()
    }

    fun hyperOsGlowColor(context: Context): String =
        prefs(context).getString(KEY_HYPEROS_GLOW_COLOR, "#006EFF") ?: "#006EFF"

    fun setHyperOsGlowColor(context: Context, color: String) {
        prefs(context).edit().putString(KEY_HYPEROS_GLOW_COLOR, color).apply()
    }

    /** 小岛常驻展示有效时长（秒），默认 30 秒 */
    fun hyperOsIslandTimeout(context: Context): Int =
        prefs(context).getInt(KEY_HYPEROS_ISLAND_TIMEOUT, 30)

    fun setHyperOsIslandTimeout(context: Context, timeoutSeconds: Int) {
        prefs(context).edit().putInt(KEY_HYPEROS_ISLAND_TIMEOUT, timeoutSeconds).apply()
    }

    fun prefs(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** 服务器地址(默认本机联调地址,可在设置页修改) */
    fun serverUrl(context: Context): String =
        prefs(context).getString(KEY_SERVER_URL, "http://192.168.0.102:5033") ?: "http://192.168.0.102:5033"

    /** 是否已完成配对且拥有有效设备凭证。 */
    fun isPaired(context: Context): Boolean =
        prefs(context).getBoolean(KEY_PAIRED, false)

    fun setPaired(context: Context, paired: Boolean) {
        prefs(context).edit().putBoolean(KEY_PAIRED, paired).apply()
    }

    fun deviceToken(context: Context): String =
        prefs(context).getString(KEY_DEVICE_TOKEN, "") ?: ""

    fun setDeviceToken(context: Context, token: String?) {
        val edit = prefs(context).edit()
        if (token.isNullOrBlank()) edit.remove(KEY_DEVICE_TOKEN) else edit.putString(KEY_DEVICE_TOKEN, token)
        edit.apply()
    }

    /** 服务端撤销设备后统一清理本地凭证，防止后台服务继续重连。 */
    fun clearPairing(context: Context) {
        prefs(context).edit().putBoolean(KEY_PAIRED, false).remove(KEY_DEVICE_TOKEN).apply()
    }

    fun ensureDeviceId(context: Context): String {
        val p = prefs(context)
        var id = p.getString(KEY_DEVICE_ID, null)
        if (id.isNullOrBlank()) {
            id = java.util.UUID.randomUUID().toString()
            p.edit().putString(KEY_DEVICE_ID, id).apply()
        }
        return id
    }

    fun resetDeviceId(context: Context): String {
        val id = java.util.UUID.randomUUID().toString()
        prefs(context).edit().putString(KEY_DEVICE_ID, id).apply()
        return id
    }

    fun deviceName(context: Context): String {
        val custom = prefs(context).getString(KEY_DEVICE_NAME, null)
        if (!custom.isNullOrBlank()) return custom
        val model = android.os.Build.MODEL.orEmpty()
        val brand = android.os.Build.BRAND.orEmpty()
        return when {
            model.startsWith("23127") -> "小米 14"
            model.startsWith("23116") -> "小米 14 Pro"
            model.startsWith("24031") -> "小米 14 Ultra"
            model.startsWith("24129") -> "小米 15"
            model.startsWith("2210132") -> "小米 13"
            model.startsWith("2201123") -> "小米 12"
            brand.isNotBlank() && !model.startsWith(brand, ignoreCase = true) -> "$brand $model"
            model.isNotBlank() -> model
            else -> "Android 手机"
        }
    }

    fun setDeviceName(context: Context, name: String) {
        prefs(context).edit().putString(KEY_DEVICE_NAME, name.trim()).apply()
    }

    fun bootStartEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_BOOT_START_ENABLED, true)

    /** 启动时自动检查更新开关,默认开启 */
    fun autoCheckUpdate(context: Context): Boolean =
        prefs(context).getBoolean(KEY_AUTO_CHECK_UPDATE, true)

    fun isAutoCheckUpdate(context: Context): Boolean = autoCheckUpdate(context)

    fun setAutoCheckUpdate(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_AUTO_CHECK_UPDATE, enabled).apply()
    }

    /** 更新下载来源: 0=GitHub Releases (默认), 1=服务端直连加速 */
    fun updateSource(context: Context): Int =
        prefs(context).getInt(KEY_UPDATE_SOURCE, UPDATE_SOURCE_GITHUB)

    fun setUpdateSource(context: Context, source: Int) {
        prefs(context).edit().putInt(KEY_UPDATE_SOURCE, source).apply()
    }

    /** 悬浮底栏(液态玻璃)开关,默认开启 */
    fun floatingBottomBarEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_FLOATING_BOTTOM_BAR, true)

    fun setFloatingBottomBarEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_FLOATING_BOTTOM_BAR, enabled).apply()
    }

    /** 预测返回手势开关,默认开启 */
    fun predictiveBackEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_PREDICTIVE_BACK, true)

    fun setPredictiveBackEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_PREDICTIVE_BACK, enabled).apply()
    }

    /** 从最近任务列表隐藏开关, 默认关闭 */
    fun isHideFromRecents(context: Context): Boolean =
        prefs(context).getBoolean(KEY_HIDE_FROM_RECENTS, false)

    fun setHideFromRecents(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_HIDE_FROM_RECENTS, enabled).apply()
    }

    /** 动态应用从最近任务隐藏设置 */
    fun applyExcludeFromRecents(context: Context, exclude: Boolean) {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? android.app.ActivityManager
        am?.appTasks?.forEach { task ->
            runCatching {
                task.setExcludeFromRecents(exclude)
            }
        }
    }

    /** 同步与捕获通知展示开关,默认开启 */
    fun notificationEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_NOTIFICATION_ENABLED, true)

    fun setNotificationEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_NOTIFICATION_ENABLED, enabled).apply()
    }

    fun maxHistory(context: Context): Int =
        prefs(context).getInt(KEY_MAX_HISTORY, DEFAULT_MAX_HISTORY)

    // ---- 黑名单 / 关键词过滤 ----

    fun filterKeywords(context: Context): List<String> {
        val raw = prefs(context).getString(KEY_FILTER_KEYWORDS, null) ?: return emptyList()
        return raw.split('\n').filter { it.isNotBlank() }
    }

    fun addFilterKeyword(context: Context, keyword: String) {
        val k = keyword.trim()
        if (k.isEmpty()) return
        val current = filterKeywords(context).filter { it != k }
        val updated = listOf(k) + current
        prefs(context).edit().putString(KEY_FILTER_KEYWORDS, updated.joinToString("\n")).apply()
    }

    fun removeFilterKeyword(context: Context, keyword: String) {
        val updated = filterKeywords(context).filter { it != keyword.trim() }
        prefs(context).edit().putString(KEY_FILTER_KEYWORDS, updated.joinToString("\n")).apply()
    }

    fun clearFilterKeywords(context: Context) {
        prefs(context).edit().remove(KEY_FILTER_KEYWORDS).apply()
    }

    fun filterPackages(context: Context): List<String> {
        val raw = prefs(context).getString(KEY_FILTER_PACKAGES, null) ?: return emptyList()
        return raw.split('\n').filter { it.isNotBlank() }
    }

    fun addFilterPackage(context: Context, packageName: String) {
        val p = packageName.trim()
        if (p.isEmpty()) return
        val current = filterPackages(context).filter { it != p }
        val updated = listOf(p) + current
        prefs(context).edit().putString(KEY_FILTER_PACKAGES, updated.joinToString("\n")).apply()
    }

    fun removeFilterPackage(context: Context, packageName: String) {
        val updated = filterPackages(context).filter { it != packageName.trim() }
        prefs(context).edit().putString(KEY_FILTER_PACKAGES, updated.joinToString("\n")).apply()
    }

    fun clearFilterPackages(context: Context) {
        prefs(context).edit().remove(KEY_FILTER_PACKAGES).apply()
    }

    fun isPackageFiltered(context: Context, packageName: String?): Boolean {
        if (packageName.isNullOrBlank()) return false
        val packages = filterPackages(context)
        return packages.any { it.equals(packageName, ignoreCase = true) }
    }

    fun ignoreSensitive(context: Context): Boolean =
        prefs(context).getBoolean(KEY_IGNORE_SENSITIVE, false)

    fun setIgnoreSensitive(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_IGNORE_SENSITIVE, enabled).apply()
    }

    /** 检查内容是否命中黑名单规则 (包括关键词与来源包名) */
    fun isContentFiltered(context: Context, text: String, packageName: String? = null): Boolean {
        if (isPackageFiltered(context, packageName)) return true
        if (text.isBlank()) return false
        val keywords = filterKeywords(context)
        for (kw in keywords) {
            if (kw.isNotBlank() && text.contains(kw, ignoreCase = true)) {
                return true
            }
        }
        return false
    }

    // ---- 搜索历史(最近搜索词,最新在前,去重) ----

    private const val MAX_SEARCH_HISTORY = 20

    fun searchHistory(context: Context): List<String> {
        val raw = prefs(context).getString(KEY_SEARCH_HISTORY, null) ?: return emptyList()
        return raw.split('\n').filter { it.isNotBlank() }
    }

    fun addSearchHistory(context: Context, term: String) {
        val t = term.trim()
        if (t.isEmpty()) return
        val list = (listOf(t) + searchHistory(context).filter { it != t })
            .take(MAX_SEARCH_HISTORY)
        prefs(context).edit().putString(KEY_SEARCH_HISTORY, list.joinToString("\n")).apply()
    }

    fun clearSearchHistory(context: Context) {
        prefs(context).edit().remove(KEY_SEARCH_HISTORY).apply()
    }

    // ---- 智能动作识别与应用直达配置 ----

    fun isSmartActionMasterEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_SMART_ACTION_MASTER, true)

    fun setSmartActionMasterEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_SMART_ACTION_MASTER, enabled).apply()
    }

    fun isSmartActionTypeEnabled(context: Context, key: String, defaultVal: Boolean = true): Boolean =
        prefs(context).getBoolean(key, defaultVal)

    fun setSmartActionTypeEnabled(context: Context, key: String, enabled: Boolean) {
        prefs(context).edit().putBoolean(key, enabled).apply()
    }

    fun customSmartActionRules(context: Context): List<clip.yixing.sync.smartaction.CustomSmartActionRule> {
        val raw = prefs(context).getString(KEY_SMART_ACTION_CUSTOM_RULES, null)
        return clip.yixing.sync.smartaction.CustomSmartActionRule.listFromJson(raw)
    }

    fun setCustomSmartActionRules(context: Context, list: List<clip.yixing.sync.smartaction.CustomSmartActionRule>) {
        val json = clip.yixing.sync.smartaction.CustomSmartActionRule.listToJson(list)
        prefs(context).edit().putString(KEY_SMART_ACTION_CUSTOM_RULES, json).apply()
    }
}

/**
 * 通知展示样式枚举:
 * - STANDARD: 普通通知(传统标准通知栏提示，低干扰展示同步状态与内容摘要)
 * - ANDROID_LIVE: 安卓实时通知(Android 14+ 实时活动 Live Activity，悬浮横幅并提供快捷复制)
 * - HYPEROS_ISLAND: HyperOS 灵动焦点(适配小米澎湃 OS 超级岛与状态栏灵动胶囊，支持焦点流转)
 */
enum class NotificationStyle(val key: String, val label: String, val summary: String) {
    STANDARD("standard", "普通通知", "传统标准通知栏提示，低干扰展示同步状态与内容摘要"),
    ANDROID_LIVE("android_live", "安卓实时通知", "Android 14+ 实时活动（Live Activity），悬浮横幅并提供快捷复制"),
    HYPEROS_ISLAND("hyperos_island", "HyperOS 灵动焦点", "适配小米澎湃 OS 超级岛与状态栏灵动胶囊，支持焦点流转");

    companion object {
        fun fromKey(key: String?): NotificationStyle =
            entries.find { it.key == key } ?: (if (SyncSettings.isHyperOs()) HYPEROS_ISLAND else ANDROID_LIVE)
    }
}

/**
 * 剪贴板后台监听与授权模式:
 * - AUTO: 自动 (LSPosed 优先 / Shizuku 备用)
 * - LSPOSED: 仅 LSPosed 模块
 * - SHIZUKU: 仅 Shizuku
 */
enum class CaptureMethod(val key: String, val label: String, val summary: String) {
    AUTO("auto", "自动选择", "自动识别最佳方案"),
    LSPOSED("lsposed", "LSPosed 模块", "通过系统框架模块监听"),
    SHIZUKU("shizuku", "Shizuku 授权", "通过免 Root 服务监听");

    companion object {
        fun fromKey(key: String?): CaptureMethod =
            entries.find { it.key == key } ?: AUTO
    }
}

