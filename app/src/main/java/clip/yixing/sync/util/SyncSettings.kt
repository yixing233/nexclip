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
    const val KEY_DEVICE_NAME = "device_name"
    const val KEY_BOOT_START_ENABLED = "boot_start_enabled"
    const val KEY_FLOATING_BOTTOM_BAR = "floating_bottom_bar"
    const val KEY_MAX_HISTORY = "max_history"
    const val KEY_SEARCH_HISTORY = "search_history"

    const val KEY_FILTER_KEYWORDS = "filter_keywords"
    const val KEY_IGNORE_SENSITIVE = "ignore_sensitive"

    const val DEFAULT_MAX_HISTORY = 50
    val MAX_HISTORY_OPTIONS = intArrayOf(20, 50, 100, 200)

    fun prefs(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** 服务器地址(默认本机联调地址,可在设置页修改) */
    fun serverUrl(context: Context): String =
        prefs(context).getString(KEY_SERVER_URL, "http://192.168.0.102:5033") ?: "http://192.168.0.102:5033"

    /** 是否已完成配对(配对码配对;设备同步免令牌,配对仅用于登记) */
    fun isPaired(context: Context): Boolean =
        prefs(context).getBoolean(KEY_PAIRED, false)

    fun setPaired(context: Context, paired: Boolean) {
        prefs(context).edit().putBoolean(KEY_PAIRED, paired).apply()
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
