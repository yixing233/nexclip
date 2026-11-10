package clip.yixing.sync.root

import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import android.util.Log
import clip.yixing.sync.paste.NexClipAccessibilityService
import clip.yixing.sync.util.SyncSettings

/**
 * 用 root 守护无障碍服务不被系统关闭。
 *
 * 背景:Android 会在若干场景下把无障碍服务"悄悄关掉"——
 * 应用被强制停止、系统省电策略、部分 ROM 的安全策略、
 * 以及用户手动清理后台。一旦被关,粘贴注入就失效,
 * 而所有常见的 ROM 里,"重新打开无障碍"要跳设置页手动操作。
 *
 * 本类把这件事交给 root 完成:检测到被关 → 直接改写 Settings.Secure → 复查。
 *
 * 安全性设计:
 * 1. 默认关闭,必须在设置页显式开启(见 SyncSettings.accessibilityGuardEnabled)。
 * 2. **绝不覆盖其它无障碍服务** —— 写入时把当前已启用的列表读出来,
 *    只追加/保留本服务,避免把用户的其它无障碍应用(如 TalkBack)踢掉。
 * 3. 节流:同一时间窗内只尝试一次,避免在持续失败时反复弹 root 授权。
 * 4. 失败一律静默记日志,由界面读取状态展示"root 未授权",不打扰用户。
 */
object AccessibilityGuard {

    private const val TAG = "NexClipA11yGuard"

    /** 两次自动修复的最小间隔。root 授权弹窗是打断性的,不能频繁触发。 */
    private const val MIN_REPAIR_INTERVAL_MS = 60_000L

    private const val PREFS = "accessibility_guard"
    private const val KEY_LAST_REPAIR_AT = "last_repair_at"
    private const val KEY_LAST_RESULT = "last_result"

    /** 最近一次自动修复的结果,供界面展示。 */
    enum class LastResult {
        /** 从未尝试过。 */
        Never,
        /** 最近一次修复成功。 */
        Success,
        /** 本轮无需修复(服务本来就是启用的)。 */
        NotNeeded,
        /** su 不可用或未授权。 */
        NoRoot,
        /** 命令执行了但服务仍未生效。 */
        Failed,
        /** 被节流跳过。 */
        Throttled,
    }

    /** 本应用的无障碍服务组件。 */
    fun serviceComponent(context: Context): ComponentName =
        ComponentName(context, NexClipAccessibilityService::class.java)

    /**
     * 本服务当前是否在系统的启用列表里。
     *
     * 直接读 Settings.Secure,不依赖服务进程是否活着 ——
     * 冷启动、服务被系统杀掉时本函数依然能给出正确结论。
     */
    fun isServiceEnabled(context: Context): Boolean {
        val raw = runCatching {
            Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
        }.getOrNull() ?: return false
        val expected = serviceComponent(context)
        return raw.split(':').any { entry ->
            entry.isNotBlank() && ComponentName.unflattenFromString(entry) == expected
        }
    }

    /**
     * 检查并在必要时用 root 重新启用无障碍服务。
     *
     * **必须在后台线程调用**(内部会 fork su 进程)。
     *
     * @param force 跳过节流立即执行(供设置页"立即修复"按钮使用)。
     * @return 本次调用的结果。
     */
    fun ensureEnabled(context: Context, force: Boolean = false): LastResult {
        if (!SyncSettings.accessibilityGuardEnabled(context)) return LastResult.Never

        // 已经启用:无需任何操作,也不消耗 root 授权
        if (isServiceEnabled(context)) {
            recordResult(context, LastResult.NotNeeded)
            return LastResult.NotNeeded
        }

        if (!force && isThrottled(context)) {
            return LastResult.Throttled
        }

        val result = repair(context)
        recordResult(context, result)
        markRepaired(context)
        return result
    }

    /**
     * 真正执行修复。
     *
     * 写入策略(关键):
     * 先读出当前列表,若其中已包含本服务则只确保 accessibility_enabled=1;
     * 否则在**保留原有条目**的前提下追加本服务,再写回。
     * 这样不会误伤其它无障碍应用。
     */
    private fun repair(context: Context): LastResult {
        val expected = serviceComponent(context)
        // 用系统标准短格式 "pkg/.Class":实测两种写法系统都接受,
        // 但短格式是 Settings 里最常见的形态,ComponentName.unflattenFromString 解析它最可靠。
        // flattenToString() 会展开成全限定名,反而与系统常见写法不一致,故显式构造。
        val expectedFlat = expected.packageName + "/." +
            expected.className.removePrefix(expected.packageName + ".")

        val current = RootShell.readOutput("settings get secure enabled_accessibility_services")
            ?: run {
                Log.d(TAG, "root 不可用,无法读取无障碍设置")
                return LastResult.NoRoot
            }

        val entries = current
            .split(':')
            .map { it.trim() }
            .filter { it.isNotEmpty() && it != "null" }

        val alreadyListed = entries.any { ComponentName.unflattenFromString(it) == expected }
        val newList = if (alreadyListed) entries else entries + expectedFlat

        val quoted = RootShell.shellQuote(newList.joinToString(":"))

        // 1) 写入服务列表(保留其它条目)
        val putServices = RootShell.exec("settings put secure enabled_accessibility_services $quoted")
        if (!putServices.suAvailable) {
            Log.d(TAG, "su 不可用: ${putServices.stderr.take(120)}")
            return LastResult.NoRoot
        }
        if (!putServices.ok) {
            Log.w(TAG, "写入无障碍服务列表失败: ${putServices.stderr.take(200)}")
            return LastResult.Failed
        }

        // 2) 打开总开关。部分 ROM 上只写列表不生效,必须同时置 1
        RootShell.exec("settings put secure accessibility_enabled 1")

        // 3) 复查:su 在未授权时可能静默返回 0,必须以系统实际状态为准
        val verified = isServiceEnabled(context)
        if (verified) {
            Log.i(TAG, "已通过 root 重新启用无障碍服务(原有 ${entries.size} 项已保留)")
            return LastResult.Success
        }

        Log.w(TAG, "root 写入后服务仍未生效,可能被 ROM 策略拦截")
        return LastResult.Failed
    }

    // ---- 节流与状态记录 ----

    private fun prefs(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun isThrottled(context: Context): Boolean {
        val last = prefs(context).getLong(KEY_LAST_REPAIR_AT, 0L)
        return System.currentTimeMillis() - last < MIN_REPAIR_INTERVAL_MS
    }

    private fun markRepaired(context: Context) {
        prefs(context).edit().putLong(KEY_LAST_REPAIR_AT, System.currentTimeMillis()).apply()
    }

    private fun recordResult(context: Context, result: LastResult) {
        prefs(context).edit().putString(KEY_LAST_RESULT, result.name).apply()
    }

    /** 最近一次自动修复结果(界面展示用)。 */
    fun lastResult(context: Context): LastResult {
        val name = prefs(context).getString(KEY_LAST_RESULT, null) ?: return LastResult.Never
        return runCatching { LastResult.valueOf(name) }.getOrDefault(LastResult.Never)
    }

    /** 最近一次自动修复的时间(毫秒时间戳,0 表示从未)。 */
    fun lastRepairAt(context: Context): Long = prefs(context).getLong(KEY_LAST_REPAIR_AT, 0L)

    /** 供界面判断能否使用本功能:su 文件存在 且 当前确实拿到 uid=0。 */
    fun isRootAvailable(): Boolean {
        val r = RootShell.exec("id", timeoutMs = 5_000)
        return r.ok && r.stdout.contains("uid=0")
    }
}
