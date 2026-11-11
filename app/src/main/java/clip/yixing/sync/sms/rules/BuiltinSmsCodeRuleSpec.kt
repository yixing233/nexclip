package clip.yixing.sync.sms.rules

data class BuiltinSmsCodeRuleSpec(
    val id: String,
    val codeKeywordHint: String,
    val codeRegex: String,
)

object BuiltinSmsCodeRules {
    const val RULE_ID_ALPHANUMERIC = "default_alphanumeric"
    const val RULE_ID_DIGITS = "default_digits"
    const val KEYWORD_SETTING_HINT = "\$keyword_setting"
    const val DEFAULT_ALPHANUMERIC_CODE_REGEX = "(?<![a-zA-Z0-9])[a-zA-Z0-9]{4,8}(?![a-zA-Z0-9])"
    const val DEFAULT_DIGITS_CODE_REGEX = "(?<![a-zA-Z0-9])[0-9]{4,8}(?![a-zA-Z0-9])"

    val all: List<BuiltinSmsCodeRuleSpec> = listOf(
        BuiltinSmsCodeRuleSpec(
            id = RULE_ID_ALPHANUMERIC,
            codeKeywordHint = KEYWORD_SETTING_HINT,
            codeRegex = DEFAULT_ALPHANUMERIC_CODE_REGEX,
        ),
        BuiltinSmsCodeRuleSpec(
            id = RULE_ID_DIGITS,
            codeKeywordHint = KEYWORD_SETTING_HINT,
            codeRegex = DEFAULT_DIGITS_CODE_REGEX,
        ),
    )
}
