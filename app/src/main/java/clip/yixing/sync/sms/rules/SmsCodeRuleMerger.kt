package clip.yixing.sync.sms.rules

import clip.yixing.sync.sms.rules.SmsCodeRuleSpec
import java.util.regex.Pattern

object SmsCodeRuleMerger {
    fun merge(
        userRules: List<SmsCodeRuleSpec>,
        officialRules: List<OfficialSmsCodeRule>,
    ): List<SmsCodeRuleSpec> {
        val sortedOfficial = officialRules
            .mapIndexed { index, rule -> index to rule }
            .sortedWith(
                compareByDescending<Pair<Int, OfficialSmsCodeRule>> { it.second.priority }
                    .thenBy { it.first },
            )
            .mapNotNull { (_, rule) ->
                rule.toRuleSpec().takeIf(::isValidRule)
            }
        return userRules + sortedOfficial
    }

    private fun isValidRule(rule: SmsCodeRuleSpec): Boolean {
        if (rule.codeKeyword.isBlank() || rule.codeRegex.isBlank()) return false
        return runCatching { Pattern.compile(rule.codeRegex) }.isSuccess
    }
}
