package clip.yixing.sync.sms.rules

enum class SmsCodeMatchedRuleSource {
    BUILTIN,
    OFFICIAL,
    CUSTOM,
}

data class SmsCodeMatchedRule(
    val source: SmsCodeMatchedRuleSource,
    val ordinal: Int,
)

data class SmsCodeParseResult(
    val code: String,
    val matchedRule: SmsCodeMatchedRule? = null,
)

enum class SmsCodeParseSourceKind {
    SMS,
    APP_NOTIFICATION,
    UNKNOWN,
}

data class SmsCodeParseSource(
    val packageName: String? = null,
    val sender: String? = null,
    val title: String? = null,
    val channelId: String? = null,
    val sourceKind: SmsCodeParseSourceKind = SmsCodeParseSourceKind.UNKNOWN,
)
