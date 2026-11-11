package clip.yixing.sync.sms.rules

import clip.yixing.sync.sms.rules.SmsCodeMatchedRuleSource
import clip.yixing.sync.sms.rules.SmsCodeRuleSpec
import kotlinx.serialization.Serializable

@Serializable
data class SmsCodeRuleCatalogIndex(
    val sourceRepo: String,
    val branch: String,
    val generatedAt: String,
    val files: List<SmsCodeRuleCatalogFile>,
)

@Serializable
data class SmsCodeRuleCatalogFile(
    val path: String,
    val id: String,
    val name: String,
    val locale: String,
    val updatedAt: String,
    val ruleCount: Int,
    val sha256: String,
    val size: Int,
)

@Serializable
data class SmsCodeRuleSetDocument(
    val specVersion: Int,
    val id: String,
    val name: String,
    val description: String? = null,
    val locale: String,
    val updatedAt: String,
    val rules: List<SmsCodeRuleDocument>,
)

@Serializable
data class SmsCodeRuleDocument(
    val id: String,
    val company: String? = null,
    val codeKeyword: String,
    val codeRegex: String,
    val senderRegex: String? = null,
    val packageNameHint: String? = null,
    val priority: Int = 0,
    val rejectedSuffixes: List<String> = emptyList(),
    val notes: String? = null,
    val examples: List<SmsCodeRuleExample> = emptyList(),
) {
    fun toRuleSpec(): SmsCodeRuleSpec = SmsCodeRuleSpec(
        company = company?.takeIf { it.isNotBlank() },
        codeKeyword = codeKeyword,
        codeRegex = codeRegex,
        senderRegex = senderRegex?.takeIf { it.isNotBlank() },
        packageNameHint = packageNameHint?.takeIf { it.isNotBlank() },
        priority = priority,
        source = SmsCodeMatchedRuleSource.OFFICIAL,
        rejectedSuffixes = rejectedSuffixes,
    )
}

@Serializable
data class SmsCodeRuleExample(
    val body: String,
    val expectedCode: String,
    val notes: String? = null,
)

data class OfficialSmsCodeRule(
    val setId: String,
    val setName: String,
    val setLocale: String,
    val setUpdatedAt: String,
    val sourcePath: String,
    val id: String,
    val company: String?,
    val codeKeyword: String,
    val codeRegex: String,
    val senderRegex: String? = null,
    val packageNameHint: String? = null,
    val priority: Int = 0,
    val rejectedSuffixes: List<String> = emptyList(),
    val notes: String? = null,
) {
    fun toRuleSpec(): SmsCodeRuleSpec = SmsCodeRuleSpec(
        company = company?.takeIf { it.isNotBlank() },
        codeKeyword = codeKeyword,
        codeRegex = codeRegex,
        senderRegex = senderRegex?.takeIf { it.isNotBlank() },
        packageNameHint = packageNameHint?.takeIf { it.isNotBlank() },
        priority = priority,
        source = SmsCodeMatchedRuleSource.OFFICIAL,
        rejectedSuffixes = rejectedSuffixes,
    )
}
