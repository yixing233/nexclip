package clip.yixing.sync.sms

import android.content.ClipData
import android.content.Context
import android.util.Log
import clip.yixing.sync.paste.NexClipAccessibilityService
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.service.SyncNotificationManager
import clip.yixing.sync.smartaction.SmartActionEngine
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 短信验证码的处理中枢：提取 → 去重 → 写剪贴板 → 通知 → 可选自动填入。
 *
 * 隐私边界(实现与后续修改都必须守住):
 * - 本类**不保存、不上报、不落盘**短信正文。正文只在 [handle] 的调用栈里存在,
 *   方法返回后即不可达; 进入剪贴板与历史的只有提取出来的验证码本身;
 * - 不匹配验证码的短信会被直接丢弃, 连日志都不打印正文。
 *
 * 与 [SmsCodeReceiver] 分离, 是为了让「接收系统广播」与「提取与副作用」互不耦合,
 * 后者可以在无 Android 运行时的环境下单独验证。
 */
object SmsCodeHandler {

    private const val TAG = "SmsCode"

    /**
     * 同一验证码在此窗口内重复出现时只处理一次。
     * 运营商补发、系统重投都会导致同一条短信被投递多次, 不去重会重复弹通知并打断用户输入。
     */
    private const val DEDUP_WINDOW_MILLIS = 60_000L

    /** 写剪贴板与自动填入之间的间隔, 让系统剪贴板服务先完成同步 */
    private const val AUTOFILL_DELAY_MILLIS = 300L

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    /**
     * 链接识别, 含裸域名。
     *
     * 只认 `http(s)://` 是不够的: 短信里的链接几乎都用短域名或省略协议
     * (实测语料中的 `3.cn/1w5M-ML2`、`l.6tgbl.cn/J87g9X`、`smzdm.com/a/n4GeraS1`),
     * 只匹配协议头会让这些促销短信绕过护栏。
     */
    private val LINK_REGEX = Regex(
        """(?i)(?:\bhttps?://|\bwww\.|\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:cn|com|net|org|io|me|cc|top|xyz)/\S*)"""
    )

    /** 最近处理过的验证码 -> 处理时间, 仅内存驻留, 进程结束即清空 */
    private val recentCodes = mutableMapOf<String, Long>()

    /**
     * 处理一条短信正文。返回提取到的验证码, 未提取到则返回 null。
     *
     * 调用方(广播接收器)不要求返回值, 返回值存在是为了便于测试与复用。
     */
    fun handle(context: Context, body: String): String? {
        val appContext = context.applicationContext
        if (!SyncSettings.smsCodeEnabled(appContext)) return null
        if (body.isBlank()) return null

        // 复用剪贴板路径的同一套提取逻辑, 保证两条入口判定一致
        val code = SmartActionEngine.extractVerificationCode(body) ?: run {
            Log.d(TAG, "sms received but no verification code matched")
            return null
        }

        if (isDuplicate(code)) {
            Log.d(TAG, "duplicate code within dedup window, ignored")
            return null
        }

        writeToClipboard(appContext, code)
        notifyCode(appContext, code)

        if (SyncSettings.smsCodeAutofill(appContext)) {
            scope.launch { autofill(appContext, code, body) }
        }

        return code
    }

    private fun isDuplicate(code: String): Boolean {
        val now = System.currentTimeMillis()
        recentCodes.entries.removeIf { now - it.value > DEDUP_WINDOW_MILLIS }
        val last = recentCodes[code]
        if (last != null && now - last <= DEDUP_WINDOW_MILLIS) return true
        recentCodes[code] = now
        return false
    }

    /**
     * 把验证码写进系统剪贴板。
     *
     * 走 [ClipboardMonitorService.copyToClipboardInternal] 而非直接 setPrimaryClip:
     * 前者会登记内部复制标记, 否则监听服务会把这次写入当成用户的新复制,
     * 既产生重复历史条目, 又会把验证码上传到其他设备 —— 与「仅本机」的设计相悖。
     */
    private fun writeToClipboard(context: Context, code: String) {
        runCatching {
            ClipboardMonitorService.copyToClipboardInternal(
                context,
                ClipData.newPlainText("Code", code),
                rawText = code
            )
        }.onFailure { Log.w(TAG, "write code to clipboard failed: ${it.message}") }
    }

    /**
     * 复用剪贴板推送的通知链路展示验证码。
     *
     * 传入的正文只有验证码本身, 因此通知与超级岛上显示的是验证码而非短信原文,
     * 不会把短信内容暴露到通知栏。
     */
    private fun notifyCode(context: Context, code: String) {
        runCatching {
            ClipboardMonitorService.addCaptured(
                context,
                text = code,
                sourceDevice = "本机",
                sourceApp = "短信"
            )
            val clip = CapturedClip(
                text = code,
                time = System.currentTimeMillis(),
                sourceDevice = "本机",
                sourceApp = "短信"
            )
            SyncNotificationManager.notifyNewClip(context, clip, "短信", isPush = false)
        }.onFailure { Log.w(TAG, "notify code failed: ${it.message}") }
    }

    /**
     * 把验证码直接写进当前聚焦的输入框。
     *
     * 连续的前置校验缺一不可, 任一条不满足都宁可放弃填入 —— 填错地方的代价
     * (把验证码写进聊天框、搜索框或别的表单) 远高于少填一次:
     * 1. 无障碍服务须已连接, 否则没有注入能力;
     * 2. 前台不能是 NexClip 自己, 否则会填进本应用的搜索框/输入区;
     * 3. 聚焦输入框必须为空, 已有内容时几乎必然是无关字段(如账号、金额);
     * 4. 短信正文不含链接。
     *
     * 关于第 4 条: 实测 5166 条真实短信中, 同时含「关键字紧邻验证码」与 URL 的为 0 条,
     * 而带链接的短信清一色是促销: 其"验证码戳 3.cn/xxx"实为退订/跳转话术, 附近的数字
     * 多为「前1000名」这类噪声。距离启发式在短促销串上可能取到该噪声(见测试用例),
     * 剪贴板路径下最多是多一个无用条目, 但自动填入会把它写进用户输入框。故带链接时
     * 放弃自动填入 —— 用户仍可从剪贴板手动粘贴。
     */
    private suspend fun autofill(context: Context, code: String, body: String) {
        delay(AUTOFILL_DELAY_MILLIS)

        if (containsUrl(body)) {
            // 只影响自动填入, 剪贴板与通知照常 —— 少填一次用户仍可手动粘贴
            Log.d(TAG, "autofill skipped: sms contains a link")
            return
        }

        val service = NexClipAccessibilityService.instance
        if (service == null || !NexClipAccessibilityService.isUsable()) {
            Log.d(TAG, "autofill skipped: accessibility service not usable")
            return
        }

        val foreground = NexClipAccessibilityService.foregroundPackage.value
        if (foreground.isBlank() || foreground == context.packageName) {
            Log.d(TAG, "autofill skipped: foreground is self or unknown")
            return
        }
        if (!NexClipAccessibilityService.editableFocused.value) {
            Log.d(TAG, "autofill skipped: no editable focus")
            return
        }

        val ok = withContext(Dispatchers.Default) { service.injectTextIfTargetEmpty(code) }
        Log.d(TAG, "autofill result=$ok pkg=$foreground")
    }

    private fun containsUrl(body: String): Boolean = LINK_REGEX.containsMatchIn(body)
}
