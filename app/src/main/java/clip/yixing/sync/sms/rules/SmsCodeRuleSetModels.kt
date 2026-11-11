package clip.yixing.sync.sms.rules

data class SmsCodeRuleSetSpec(
    val id: String,
    val version: String,
    val rules: List<SmsCodeRuleSpec>,
    val source: String? = null,
)

data class SmsCodeRuleSetIndexEntry(
    val id: String,
    val version: String,
    val title: String,
    val summary: String? = null,
    val source: String? = null,
)

data class SmsCodeRuleSetIndex(
    val generatedAt: String? = null,
    val entries: List<SmsCodeRuleSetIndexEntry>,
)
