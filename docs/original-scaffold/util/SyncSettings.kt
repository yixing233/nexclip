package clip.yixing.sync.util

import android.content.Context
import android.content.SharedPreferences

/**
 * 应用设置项。与剪贴板捕获历史共用同一 SharedPreferences 文件。
 */
object SyncSettings {

    const val PREFS_NAME = "sync_clipboard"

    const val KEY_SERVER_URL = "server_url"
    const val KEY_SERVER_USERNAME = "server_username"
    const val KEY_SERVER_PASSWORD = "server_password"
    const val KEY_BOOT_START_ENABLED = "boot_start_enabled"
    const val KEY_MAX_HISTORY = "max_history"
    const val KEY_SEARCH_HISTORY = "search_history"

    const val DEFAULT_MAX_HISTORY = 50
    val MAX_HISTORY_OPTIONS = intArrayOf(20, 50, 100, 200)

    fun prefs(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    fun serverUrl(context: Context): String =
        prefs(context).getString(KEY_SERVER_URL, "") ?: ""

    fun serverUsername(context: Context): String =
        prefs(context).getString(KEY_SERVER_USERNAME, "") ?: ""

    fun serverPassword(context: Context): String =
        prefs(context).getString(KEY_SERVER_PASSWORD, "") ?: ""

    fun bootStartEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_BOOT_START_ENABLED, true)

    fun maxHistory(context: Context): Int =
        prefs(context).getInt(KEY_MAX_HISTORY, DEFAULT_MAX_HISTORY)

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
