package clip.yixing.sync.sms.rules

data class SmsCodeRuleSpec(
    val company: String?,
    val codeKeyword: String,
    val codeRegex: String,
    val senderRegex: String? = null,
    val packageNameHint: String? = null,
    val priority: Int = 0,
    val source: SmsCodeMatchedRuleSource = SmsCodeMatchedRuleSource.CUSTOM,
    val rejectedSuffixes: List<String> = emptyList(),
)
