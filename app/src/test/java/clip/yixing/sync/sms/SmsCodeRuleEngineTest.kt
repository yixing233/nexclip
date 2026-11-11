package clip.yixing.sync.sms

import clip.yixing.sync.sms.rules.SmsCodeRuleSpec
import clip.yixing.sync.sms.rules.SmsCodeRuleStore
import clip.yixing.sync.sms.rules.SmsCodeUtils
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * 规则库引擎的行为验证。
 *
 * 为什么值得单独写:规则库的准确率是这次改动的全部意义,而 UI 上的
 * 「更新成功 / 提取失败」都不会告诉你是哪条规则失配。这里直接跑仓库里
 * 内置的规则集,任何一条规则被改坏都会立刻暴露。
 *
 * 这些用例不依赖 Android 运行时 —— [SmsCodeRuleStore.parseRulesDocument]
 * 与 [SmsCodeUtils.parseSmsCode] 都是纯 Kotlin/JVM 逻辑。
 */
class SmsCodeRuleEngineTest {

    private val ruleDir = File("src/main/assets/smscode-rules")

    private val rules: List<SmsCodeRuleSpec> by lazy {
        assertTrue("内置规则目录不存在: ${ruleDir.absolutePath}", ruleDir.isDirectory)
        ruleDir.listFiles { f -> f.name.endsWith(".json") }!!
            .sortedBy { it.name }
            .flatMap { SmsCodeRuleStore.parseRulesDocument(it.readText()) }
    }

    @Test
    fun `规则集应被正确加载`() {
        assertTrue("规则集为空", rules.isNotEmpty())
        println("已加载规则条数: ${rules.size}")
    }

    /** 规则集自带的官方样例:规则与引擎语义必须逐条对齐 */
    @Test
    fun `所有内置样例都应提取正确`() {
        val failures = mutableListOf<String>()
        var total = 0
        for (file in ruleDir.listFiles { f -> f.name.endsWith(".json") }!!.sortedBy { it.name }) {
            for ((body, expected) in examplesOf(file.readText())) {
                total++
                val got = SmsCodeUtils.parseSmsCode(body, "", rules)
                if (got != expected) {
                    failures += "期望[$expected] 实际[$got] : ${body.take(60)}"
                }
            }
        }
        println("样例总数: $total")
        assertTrue("样例数量异常: $total", total > 50)
        assertEquals("失败用例:\n" + failures.joinToString("\n"), 0, failures.size)
    }

    /** 真实短信场景,含必须被拒绝的噪声 */
    @Test
    fun `真实短信与噪声场景`() {
        val cases = listOf(
            "【支付宝】验证码 839201，请勿泄露。" to "839201",
            "您的验证码为194820，5分钟内有效。" to "194820",
            "验证码：550875，用于登录。" to "550875",
            "【美团】动态密码 K9X2P4，请勿告知他人。" to "K9X2P4",
            "校验码是 716243，10分钟内有效。" to "716243",
            "【京东】您的订单金额 2000元，验证码为 883921。" to "883921",
            // 以下都应拒绝:尾号/订单号/带链接营销短信/快递单号
            "您尾号1234的储蓄卡消费128.50元。" to "",
            "订单号2024111100123已发货。" to "",
            "【拼多多】百亿补贴12.12大促，点击 m.pinduoduo.com/abc 验证码戳这里退订" to "",
            "您的快递单号SF1234567890123已签收。" to "",
        )
        val failures = mutableListOf<String>()
        for ((body, expected) in cases) {
            val got = SmsCodeUtils.parseSmsCode(body, "", rules)
            if (got != expected) failures += "期望[$expected] 实际[${got.ifEmpty { "空" }}] : $body"
        }
        assertEquals("失败用例:\n" + failures.joinToString("\n"), 0, failures.size)
    }

    /**
     * 从规则集 JSON 中抽出 body/expectedCode 对。
     *
     * 用正则而非反序列化:样例只用于验证,不值得为它再引入一套模型。
     * body 与 expectedCode 在文件中成对出现且顺序一致,故可 zip。
     */
    private fun examplesOf(json: String): List<Pair<String, String>> {
        val bodies = Regex("\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
            .findAll(json).map { unescape(it.groupValues[1]) }.toList()
        val codes = Regex("\"expectedCode\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
            .findAll(json).map { unescape(it.groupValues[1]) }.toList()
        return bodies.zip(codes)
    }

    private fun unescape(s: String): String = s
        .replace("\\n", "\n")
        .replace("\\t", "\t")
        .replace("\\\"", "\"")
        .replace("\\\\", "\\")
}
