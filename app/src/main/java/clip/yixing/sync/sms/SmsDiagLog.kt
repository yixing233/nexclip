package clip.yixing.sync.sms

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 短信处理诊断日志:按天追加到应用私有目录,供用户日用几天后回看。
 *
 * ## 为什么不用 logcat
 *
 * logcat 有固定环形缓冲,几分钟到几十分钟就会被系统滚掉。而"某类短信为什么没提取到"
 * 这类问题必须攒够真实样本才有结论 —— 用户日用几天后再回看,logcat 早就没了。
 *
 * ## 隐私边界(必须守住,与 [SmsCodeHandler] 一致)
 *
 * **绝不写短信正文。** 只记录结构化元数据:
 * - 时间、处理阶段、结果状态
 * - 短信分段数、正文字符数(用于区分"没收到"和"收到了但解析不出")
 * - 正文里是否出现关键字、是否含链接、含几个数字串
 * - 命中的规则 id(不命中则记 none)
 *
 * 这样能回答"为什么没提取到",但无法还原短信内容 —— 字符数 + 关键字布尔值
 * 不足以重建原文。日志文件上限 200KB,超出后自动滚动删除最旧的。
 */
object SmsDiagLog {

    private const val TAG = "SmsDiag"
    private const val DIR_NAME = "sms_diag"
    private const val FILE_NAME = "log.txt"
    private const val MAX_BYTES = 200 * 1024L
    private const val MAX_DAYS_KEPT = 14

    private val timeFormat = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)

    /** 处理阶段,用于区分链路在哪一步断掉 */
    enum class Stage {
        /** 收到广播并解析出正文 */
        RECEIVED,
        /** 广播到了但没解析出正文(PDU 异常/空短信) */
        EMPTY_BODY,
        /** 广播 action 不匹配 */
        IGNORED_ACTION,
        /** 提取成功 */
        EXTRACTED,
        /** 规则库与启发式都没命中 */
        NOT_FOUND,
        /** 命中但处于去重窗口内 */
        DUPLICATE,
        /** 解析过程抛异常 */
        ERROR,
    }

    /**
     * 记录一条诊断。
     *
     * @param summary 结构化摘要,由调用方构造。**不得包含短信正文**。
     */
    fun log(context: Context, stage: Stage, summary: String = "") {
        val line = buildString {
            append(timeFormat.format(Date()))
            append(' ')
            append(stage.name.padEnd(14))
            if (summary.isNotEmpty()) {
                append(' ')
                append(summary)
            }
        }
        Log.d(TAG, line)
        runCatching { appendToFile(context.applicationContext, line) }
            .onFailure { Log.w(TAG, "write diag log failed: ${it.message}") }
    }

    /**
     * 构造正文的"元数据指纹"。
     *
     * 只暴露统计特征,不含原文:
     * 长度 [len]、分段 [seg]、含关键字 [kw]、含链接 [url]、数字串数 [num]、通道 [ch]。
     * `kw`/`url` 是布尔值,`num` 是计数 —— 三者都没法用来还原短信。
     */
    fun fingerprint(
        body: String,
        segments: Int = 1,
        channel: String = "system",
        keywords: List<String> = DEFAULT_KEYWORDS,
    ): String {
        val hasKeyword = keywords.any { body.contains(it, ignoreCase = true) }
        val hasUrl = body.contains("http://") || body.contains("https://") || body.contains("www.")
        val numberRuns = Regex("""\d+""").findAll(body).count()
        return "ch=$channel len=${body.length} seg=$segments kw=$hasKeyword url=$hasUrl num=$numberRuns"
    }

    /** 关键字清单,仅用于统计"这条短信到底有没有出现验证码关键字" */
    private val DEFAULT_KEYWORDS = listOf(
        "验证码", "校验码", "动态码", "动态密码", "确认码", "安全码", "授权码", "口令",
        "verification code", "security code", "otp", "passcode", "code",
    )

    /** 读取全部日志(供设置页「查看诊断日志」展示) */
    fun readAll(context: Context): String {
        val file = logFile(context)
        if (!file.exists()) return ""
        return runCatching { file.readText() }.getOrDefault("")
    }

    /** 清空日志 */
    fun clear(context: Context) {
        runCatching { logFile(context).delete() }
    }

    private fun appendToFile(context: Context, line: String) {
        val file = logFile(context)
        file.parentFile?.mkdirs()
        // 超过上限时先把旧的截断一半,避免日用几天后无限增长
        if (file.exists() && file.length() > MAX_BYTES) {
            val kept = runCatching {
                val text = file.readText()
                text.substring(text.length / 2)
            }.getOrDefault("")
            file.writeText(kept)
        }
        file.appendText(line + "\n")
        pruneOldDays(context)
    }

    /** 只保留最近若干天的日志目录内容 */
    private fun pruneOldDays(context: Context) {
        val dir = File(context.filesDir, DIR_NAME)
        val cutoff = System.currentTimeMillis() - MAX_DAYS_KEPT * 24L * 3600_000L
        dir.listFiles()?.forEach { f ->
            if (f.isFile && f.lastModified() < cutoff) runCatching { f.delete() }
        }
    }

    private fun logFile(context: Context): File =
        File(File(context.filesDir, DIR_NAME), FILE_NAME)
}
