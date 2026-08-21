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
    const val KEY_FLOATING_BOTTOM_BAR = "floating_bottom_bar"
    const val KEY_PREDICTIVE_BACK = "predictive_back"
    const val KEY_NOTIFICATION_ENABLED = "notification_enabled"
    const val KEY_NOTIFICATION_STYLE = "notification_style"
    const val KEY_MAX_HISTORY = "max_history"
    const val KEY_SEARCH_HISTORY = "search_history"

    const val KEY_FILTER_KEYWORDS = "filter_keywords"
    const val KEY_IGNORE_SENSITIVE = "ignore_sensitive"

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

    fun deviceName(context: Context): String =
        prefs(context).getString(KEY_DEVICE_NAME, null) ?: android.os.Build.MODEL

    fun setDeviceName(context: Context, name: String) {
        prefs(context).edit().putString(KEY_DEVICE_NAME, name.trim()).apply()
    }

    fun bootStartEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_BOOT_START_ENABLED, true)

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

    fun ignoreSensitive(context: Context): Boolean =
        prefs(context).getBoolean(KEY_IGNORE_SENSITIVE, false)

    fun setIgnoreSensitive(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_IGNORE_SENSITIVE, enabled).apply()
    }

    /** 检查内容是否命中黑名单规则 */
    fun isContentFiltered(context: Context, text: String): Boolean {
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
