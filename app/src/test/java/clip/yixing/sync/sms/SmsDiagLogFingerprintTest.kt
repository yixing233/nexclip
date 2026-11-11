package clip.yixing.sync.sms

import clip.yixing.sync.sms.rules.SmsCodeRuleStore
import clip.yixing.sync.sms.rules.SmsCodeUtils
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * 诊断日志与规则引擎的纯逻辑验证(不依赖 Android 运行时)。
 *
 * 诊断日志是这次「日用几天后回看」方案的地基:如果它记录的字段
 * 不足以定位问题,攒再多样本也没用。所以这里固定两件事:
 * 1. [SmsDiagLog.fingerprint] 的统计口径正确;
 * 2. **它绝不泄露正文** —— 指纹里不能出现原文的任何片段。
 */
class SmsDiagLogFingerprintTest {

    @Test
    fun `指纹应统计出关键字链接与数字串`() {
        val fp = SmsDiagLog.fingerprint("【支付宝】验证码 839201，详见 https://a.cn/x", segments = 2)
        assertTrue("应识别出关键字: $fp", fp.contains("kw=true"))
        assertTrue("应识别出链接: $fp", fp.contains("url=true"))
        assertTrue("应统计出数字串: $fp", fp.contains("num="))
        assertTrue("应带上分段数: $fp", fp.contains("seg=2"))
        assertTrue("应带长度: $fp", fp.contains("len="))
    }

    @Test
    fun `无关键字无链接时应如实标记`() {
        val fp = SmsDiagLog.fingerprint("您的快递已到驿站，取件码 3-2-1024", segments = 1)
        assertTrue("不应误判关键字: $fp", fp.contains("kw=false"))
        assertTrue("不应误判链接: $fp", fp.contains("url=false"))
    }

    /** 隐私边界:指纹里不能出现原文内容 */
    @Test
    fun `指纹不得包含短信正文片段`() {
        val body = "【支付宝】验证码 839201，请勿泄露。"
        val fp = SmsDiagLog.fingerprint(body, segments = 1)
        assertFalse("指纹泄露了正文: $fp", fp.contains("支付宝"))
        assertFalse("指纹泄露了验证码: $fp", fp.contains("839201"))
        assertFalse("指纹泄露了正文: $fp", fp.contains("请勿泄露"))
    }

    /** 英文关键字也要能识别,否则英文短信的日志会误报 kw=false */
    @Test
    fun `英文字符串也应识别关键字`() {
        val fp = SmsDiagLog.fingerprint("Your verification code is 556677")
        assertTrue("英文关键字未识别: $fp", fp.contains("kw=true"))
    }
}

/**
 * 规则库引擎回归(与 SmsCodeRuleEngineTest 共用同一批 assets)。
 * 独立成一个类是为了让"日志"与"引擎"失败时能一眼区分。
 */
class SmsCodeRuleRegressionTest {

    private val ruleDir = File("src/main/assets/smscode-rules")

    private val rules by lazy {
        ruleDir.listFiles { f -> f.name.endsWith(".json") }!!
            .sortedBy { it.name }
            .flatMap { SmsCodeRuleStore.parseRulesDocument(it.readText()) }
    }

    @Test
    fun `规则总量应包含中文与英文补充集`() {
        assertTrue("规则过少: ${rules.size}", rules.size >= 90)
        val keywords = rules.map { it.codeKeyword }.toSet()
        assertTrue("缺少英文补充关键字", keywords.any { it.contains("verification code") })
        assertTrue("缺少中文关键字", keywords.any { it == "验证码" })
    }

    /** 全角数字归一化后应能命中 */
    @Test
    fun `全角数字应被归一化`() {
        assertEquals("123456", SmsCodeUtils.parseSmsCode("验证码为１２３４５６", "", rules))
        assertEquals("482913", SmsCodeUtils.parseSmsCode("验证码 48２913", "", rules))
    }
}
