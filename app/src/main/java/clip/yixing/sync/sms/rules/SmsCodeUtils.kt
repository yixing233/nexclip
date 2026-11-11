package clip.yixing.sync.sms.rules

import clip.yixing.sync.sms.rules.AppLabelResolver
import clip.yixing.sync.sms.rules.BuiltinSmsCodeRules
import clip.yixing.sync.sms.rules.SmsCodeMatchedRule
import clip.yixing.sync.sms.rules.SmsCodeMatchedRuleSource
import clip.yixing.sync.sms.rules.SmsCodeParseResult
import clip.yixing.sync.sms.rules.SmsCodeParseSource
import clip.yixing.sync.sms.rules.SmsCodeParseSourceKind
import clip.yixing.sync.sms.rules.SmsCodeRuleSpec
import java.util.Locale
import java.util.regex.Pattern

object SmsCodeUtils {
    private data class InternalRule(
        val spec: SmsCodeRuleSpec,
        val originalOrdinal: Int,
    )

    private data class CodeCandidate(
        val code: String,
        val start: Int,
    )

    private const val LEVEL_DIGITAL_6 = 4
    private const val LEVEL_DIGITAL_4 = 3
    private const val LEVEL_DIGITAL_OTHERS = 2
    private const val LEVEL_TEXT = 1
    private const val LEVEL_CHARACTER = 0
    private const val LEVEL_NONE = -1
    private const val KEYWORD_DISTANCE_THRESHOLD = 30
    private const val PRIORITY_TIER_CUSTOM = 2_000_000
    private const val PRIORITY_TIER_OFFICIAL = 1_000_000
    private val urlSchemeTokens = setOf("http", "https", "www")
    private val urlContextTokens = setOf('/', '?', '&', '=', '#', '%')
    private val dateTimeUnitTokens = setOf('年', '月', '日', '号', '时', '點', '点', '分', '秒')
    val DEFAULT_REJECTED_SUFFIXES = listOf(
        "mb", "gb", "kb", "tb", "pb", "byte", "bytes", "元", "角", "分", "块",
    )

    suspend fun parseSmsCodeIfExists(
        content: String,
        keywordsRegex: String,
        rules: List<SmsCodeRuleSpec> = emptyList(),
        source: SmsCodeParseSource? = null,
    ): String = parseSmsCodeResultIfExists(content, keywordsRegex, rules, source).code

    suspend fun parseSmsCodeResultIfExists(
        content: String,
        keywordsRegex: String,
        rules: List<SmsCodeRuleSpec> = emptyList(),
        source: SmsCodeParseSource? = null,
    ): SmsCodeParseResult = parseSmsCodeResult(
        content = content,
        keywordsRegex = keywordsRegex,
        rules = rules,
        source = source,
    )

    fun parseSmsCode(
        content: String,
        keywordsRegex: String,
        rules: List<SmsCodeRuleSpec> = emptyList(),
        source: SmsCodeParseSource? = null,
    ): String = parseSmsCodeResult(content, keywordsRegex, rules, source).code

    fun parseSmsCodeResult(
        content: String,
        keywordsRegex: String,
        rules: List<SmsCodeRuleSpec> = emptyList(),
        source: SmsCodeParseSource? = null,
    ): SmsCodeParseResult {
        val normalizedContent = normalizeDigits(content)
        val effectiveRules = buildEffectiveRules(rules, keywordsRegex)
        return executeRulePipeline(normalizedContent, effectiveRules, source)
    }

    /**
     * 数字归一化:把各种非 ASCII 数字转成 ASCII 数字。
     *
     * 中文短信里全角数字很常见(输入法全角状态、部分银行的模板化下发),
     * `１２３４５６` 与 `123456` 对用户是同一个验证码,但正则 [0-9] 匹配不到前者。
     * 这一步放在所有匹配之前,让后续规则无需各自处理。
     */
    private fun normalizeDigits(text: String): String {
        if (text.isEmpty()) return text
        val sb = StringBuilder(text.length)
        for (ch in text) {
            when (ch) {
                // Eastern Arabic-Indic digits (Persian/Urdu: ۰ ۱ ۲ ۳ ۴ ۵ ۶ ۷ ۸ ۹)
                in '\u06F0'..'\u06F9' -> sb.append((ch - '\u06F0' + '0'.code).toChar())
                // Arabic-Indic digits (Arabic: ٠ ١ ٢ ٣ ٤ ٥ ٦ ٧ ٨ ٩)
                in '\u0660'..'\u0669' -> sb.append((ch - '\u0660' + '0'.code).toChar())
                // Fullwidth digits (CJK: ０ １ ２ ３ ４ ５ ６ ７ ８ ９)
                in '\uFF10'..'\uFF19' -> sb.append((ch - '\uFF10' + '0'.code).toChar())
                else -> sb.append(ch)
            }
        }
        return sb.toString()
    }

    @JvmStatic
    fun parseCompany(content: String): String {
        val possibleCompanies = parseCompanyCandidates(content)
        if (possibleCompanies.isEmpty()) return ""
        return possibleCompanies.joinToString(" ")
    }

    @JvmStatic
    fun parseCompanyCandidates(content: String): List<String> {
        val regex = "((?<=【)(.*?)(?=】))|((?<=\\[)(.*?)(?=\\]))"
        val pattern = Pattern.compile(regex)
        val matcher = pattern.matcher(content)
        val possibleCompanies = mutableListOf<String>()
        while (matcher.find()) {
            possibleCompanies.add(matcher.group())
        }
        return possibleCompanies
    }

    @JvmStatic
    fun findPackageNameByLabel(label: String?, resolver: AppLabelResolver): String? {
        if (label.isNullOrBlank()) return null
        return resolver.findPackageNameByLabel(label)
    }

    private fun containsChinese(text: String): Boolean {
        val regex = "[一-龥]|。"
        val pattern = Pattern.compile(regex)
        val matcher = pattern.matcher(text)
        return matcher.find()
    }

    private fun buildEffectiveRules(
        rules: List<SmsCodeRuleSpec>,
        keywordsRegex: String,
    ): List<InternalRule> {
        val result = mutableListOf<InternalRule>()
        for ((idx, spec) in rules.withIndex()) {
            result.add(InternalRule(spec = spec, originalOrdinal = idx + 1))
        }

        // The catalog supplies the defaults; a nonblank preference is the user's
        // additional keyword regex and must remain usable alongside generic rules.
        if (keywordsRegex.isNotBlank()) {
            result.add(
                InternalRule(
                    spec = SmsCodeRuleSpec(
                        company = null,
                        codeKeyword = keywordsRegex,
                        codeRegex = BuiltinSmsCodeRules.DEFAULT_DIGITS_CODE_REGEX,
                        priority = 10,
                        source = SmsCodeMatchedRuleSource.BUILTIN,
                        rejectedSuffixes = DEFAULT_REJECTED_SUFFIXES,
                    ),
                    originalOrdinal = 1,
                ),
            )
            result.add(
                InternalRule(
                    spec = SmsCodeRuleSpec(
                        company = null,
                        codeKeyword = keywordsRegex,
                        codeRegex = BuiltinSmsCodeRules.DEFAULT_ALPHANUMERIC_CODE_REGEX,
                        priority = 5,
                        source = SmsCodeMatchedRuleSource.BUILTIN,
                        rejectedSuffixes = DEFAULT_REJECTED_SUFFIXES,
                    ),
                    originalOrdinal = 2,
                ),
            )
        }

        return result.sortedWith(
            compareByDescending<InternalRule> {
                when (it.spec.source) {
                    SmsCodeMatchedRuleSource.CUSTOM -> PRIORITY_TIER_CUSTOM + it.spec.priority
                    SmsCodeMatchedRuleSource.OFFICIAL -> PRIORITY_TIER_OFFICIAL + it.spec.priority
                    SmsCodeMatchedRuleSource.BUILTIN -> it.spec.priority
                }
            }.thenBy { it.originalOrdinal },
        )
    }

    private fun executeRulePipeline(
        content: String,
        rules: List<InternalRule>,
        source: SmsCodeParseSource?,
    ): SmsCodeParseResult {
        val lowerContent = content.lowercase(Locale.ROOT)
        var fallbackResult: SmsCodeParseResult? = null
        // Determine this before iterating: a generic rule may sort before the targeted
        // rule, so discovering the mismatch while iterating would let that fallback
        // bypass the notification identity check.
        val brandMismatchedSource = source?.sourceKind == SmsCodeParseSourceKind.APP_NOTIFICATION &&
            rules.any { item ->
                val rule = item.spec
                isTargetedBrandMismatch(rule, lowerContent, source)
            }

        for (item in rules) {
            val rule = item.spec
            val isBuiltin = rule.source == SmsCodeMatchedRuleSource.BUILTIN
            if (isBuiltin && fallbackResult != null && !brandMismatchedSource) {
                return fallbackResult
            }
            if (!matchesSource(rule, source)) {
                continue
            }

            val keywordPattern = runCatching {
                if (isBuiltin) {
                    Pattern.compile(rule.codeKeyword)
                } else {
                    // UNICODE_CASE keeps catalog keyword ranges working for cased
                    // non-ASCII scripts (Cyrillic, Greek); ASCII-only folding would
                    // silently empty them and skip the rule.
                    Pattern.compile(
                        Pattern.quote(rule.codeKeyword),
                        Pattern.CASE_INSENSITIVE or Pattern.UNICODE_CASE,
                    )
                }
            }.getOrNull() ?: continue

            if (!isBuiltin && !lowerContent.contains(rule.codeKeyword.lowercase(Locale.ROOT))) {
                continue
            }
            if (isBuiltin && !keywordPattern.matcher(content).find()) {
                continue
            }

            val effectiveSuffixes = if (rule.rejectedSuffixes.isNotEmpty()) {
                rule.rejectedSuffixes
            } else {
                DEFAULT_REJECTED_SUFFIXES
            }
            val extractedCode = extractCodeForRule(
                codeRegex = rule.codeRegex,
                keywordPattern = keywordPattern,
                content = content,
                rejectedSuffixes = effectiveSuffixes,
            )
            if (extractedCode.isBlank()) continue

            val company = rule.company?.trim().orEmpty()
            val companyMatched = company.isNotEmpty() && lowerContent.contains(company.lowercase(Locale.ROOT))
            val isTargeted = !rule.packageNameHint.isNullOrBlank() || !rule.senderRegex.isNullOrBlank()
            val isTargetedSourceMatched = isTargeted && isExplicitSourceMatch(rule, source)
            val isGeneric = company.isEmpty() && !isTargeted

            val candidateResult = SmsCodeParseResult(
                code = extractedCode,
                matchedRule = SmsCodeMatchedRule(
                    source = rule.source,
                    ordinal = item.originalOrdinal,
                ),
            )

            if (companyMatched || isTargetedSourceMatched || isGeneric) {
                if (isGeneric && brandMismatchedSource) continue
                return candidateResult
            }

            if (!isTargeted && fallbackResult == null) {
                fallbackResult = candidateResult
            }
        }

        if (brandMismatchedSource) return SmsCodeParseResult(code = "")
        return fallbackResult ?: SmsCodeParseResult(code = "")
    }

    /**
     * A notification from a known different package that presents a targeted app's
     * brand must not fall through to a generic code rule. SMS senders and missing
     * package metadata do not establish a notification package identity mismatch.
     */
    private fun isTargetedBrandMismatch(
        rule: SmsCodeRuleSpec,
        lowerContent: String,
        source: SmsCodeParseSource?,
    ): Boolean {
        if (source == null) return false
        val expectedPackage = rule.packageNameHint?.trim().orEmpty()
        val actualPackage = source.packageName?.trim().orEmpty()
        if (expectedPackage.isEmpty() || actualPackage.isEmpty() ||
            expectedPackage.equals(actualPackage, ignoreCase = true)
        ) return false
        val company = rule.company?.trim().orEmpty()
        val senderRegex = rule.senderRegex?.trim().orEmpty()
        if (company.isEmpty() && senderRegex.isEmpty()) return false
        if (company.isNotEmpty() && lowerContent.contains(company.lowercase(Locale.ROOT))) return true
        if (senderRegex.isEmpty()) return false
        val pattern = runCatching { Pattern.compile(senderRegex, Pattern.CASE_INSENSITIVE) }.getOrNull()
            ?: return false
        val sourceTexts = listOfNotNull(
            source.sender?.trim()?.takeIf { it.isNotEmpty() },
            source.title?.trim()?.takeIf { it.isNotEmpty() },
            source.channelId?.trim()?.takeIf { it.isNotEmpty() },
        )
        return sourceTexts.any { pattern.matcher(it).find() }
    }

    private fun extractCodeForRule(
        codeRegex: String,
        keywordPattern: Pattern,
        content: String,
        rejectedSuffixes: List<String>,
    ): String {
        var smsCode = getSmsCode(codeRegex, keywordPattern, content, rejectedSuffixes)
        if (smsCode.isEmpty() && containsChinese(content)) {
            val handledContent = removeAllWhiteSpaces(content)
            smsCode = getSmsCode(codeRegex, keywordPattern, handledContent, rejectedSuffixes)
        }
        return smsCode
    }

    private fun removeAllWhiteSpaces(content: String): String = content.replace("\\s*".toRegex(), "")

    private fun getSmsCode(
        codeRegex: String,
        keywordPattern: Pattern,
        content: String,
        rejectedSuffixes: List<String>,
    ): String {
        val matcher = runCatching {
            Pattern.compile(codeRegex).matcher(content)
        }.getOrNull() ?: return ""
        val possibleCodes = mutableListOf<CodeCandidate>()
        while (matcher.find()) {
            val candidate = matcher.group()
            if (isLikelyRejectedCodeToken(candidate, content, matcher.start(), rejectedSuffixes)) continue
            possibleCodes.add(CodeCandidate(code = candidate, start = matcher.start()))
        }
        if (possibleCodes.isEmpty()) return ""

        // Nearness is measured against every keyword match (every alternative of the
        // keyword regex, and every occurrence), not just the first match: locking onto
        // one long keyword occurrence can push the real code beyond the distance
        // threshold even though a shorter keyword sits right next to it (issue #267).
        val keywordRanges = findKeywordRanges(keywordPattern, content)
        val filteredCodes = possibleCodes.filter { isNearToKeyword(keywordRanges, it) }
        if (filteredCodes.isEmpty()) return ""

        // Proximity to a keyword wins over digit-length level: the code is the number
        // sitting next to the keyword, while a longer/shorter digit run elsewhere in
        // the message (card number, order id, date) is noise. Digit level only breaks
        // ties between candidates at the same distance.
        var bestDistance = Int.MAX_VALUE
        var bestLevel = LEVEL_NONE
        var smsCode = ""
        for (candidate in filteredCodes) {
            val curDistance = distanceToKeywordRanges(keywordRanges, candidate)
            val curLevel = getMatchLevel(candidate.code)
            val isBetter = curDistance < bestDistance ||
                (curDistance == bestDistance && curLevel > bestLevel)
            if (isBetter) {
                bestDistance = curDistance
                bestLevel = curLevel
                smsCode = candidate.code
            }
        }
        return smsCode
    }

    private fun findKeywordRanges(keywordPattern: Pattern, content: String): List<IntRange> {
        val ranges = mutableListOf<IntRange>()
        val matcher = keywordPattern.matcher(content)
        while (matcher.find()) {
            ranges.add(matcher.start() until matcher.end())
        }
        return ranges
    }

    private fun isNearToKeyword(keywordRanges: List<IntRange>, candidate: CodeCandidate): Boolean =
        distanceToKeywordRanges(keywordRanges, candidate) <= KEYWORD_DISTANCE_THRESHOLD

    private fun distanceToKeywordRanges(keywordRanges: List<IntRange>, candidate: CodeCandidate): Int {
        if (keywordRanges.isEmpty()) return Int.MAX_VALUE
        val codeStart = candidate.start
        val codeEnd = candidate.start + candidate.code.length - 1
        var minDistance = Int.MAX_VALUE
        for (range in keywordRanges) {
            val distance = when {
                codeStart in range -> 0
                codeEnd < range.first -> range.first - codeEnd - 1
                else -> codeStart - range.last - 1
            }
            if (distance < minDistance) {
                minDistance = distance
            }
        }
        return minDistance
    }

    private fun isLikelyDateTimeToken(
        candidate: String,
        content: String,
        candidateIndex: Int,
    ): Boolean {
        if (!candidate.all(Char::isDigit)) return false
        val candidateIdx = candidateIndex
        if (candidateIdx < 0) return false
        val prevChar = content.getOrNull(candidateIdx - 1)
        val nextChar = content.getOrNull(candidateIdx + candidate.length)
        return prevChar in dateTimeUnitTokens || nextChar in dateTimeUnitTokens
    }

    private fun isLikelyUrlToken(
        candidate: String,
        content: String,
        candidateIndex: Int,
    ): Boolean {
        val candidateIdx = candidateIndex
        if (candidateIdx < 0) return false
        val prevChar = content.getOrNull(candidateIdx - 1)
        val nextChar = content.getOrNull(candidateIdx + candidate.length)
        val contextStart = (candidateIdx - 8).coerceAtLeast(0)
        val contextEnd = (candidateIdx + candidate.length + 8).coerceAtMost(content.length)
        val nearby = content.substring(contextStart, contextEnd).lowercase(Locale.ROOT)
        if (nearby.contains("http://") || nearby.contains("https://") || nearby.contains("www.")) {
            return true
        }
        if (prevChar == ':' || nextChar == ':') {
            return nearby.contains("://")
        }
        val dotBeforeLooksLikeUrl =
            prevChar == '.' && content.getOrNull(candidateIdx - 2)?.isLetterOrDigit() == true
        val dotAfterLooksLikeUrl =
            nextChar == '.' && content.getOrNull(candidateIdx + candidate.length + 1)?.isLetterOrDigit() == true
        return prevChar in urlContextTokens ||
            nextChar in urlContextTokens ||
            dotBeforeLooksLikeUrl ||
            dotAfterLooksLikeUrl
    }

    private fun getMatchLevel(matchedStr: String): Int = when {
        matchedStr.matches("^[0-9]{6}$".toRegex()) -> LEVEL_DIGITAL_6
        matchedStr.matches("^[0-9]{4}$".toRegex()) -> LEVEL_DIGITAL_4
        matchedStr.matches("^[0-9]*$".toRegex()) -> LEVEL_DIGITAL_OTHERS
        matchedStr.matches("^[a-zA-Z]*$".toRegex()) -> LEVEL_CHARACTER
        else -> LEVEL_TEXT
    }


    private fun isLikelyRejectedCodeToken(
        candidate: String,
        content: String,
        candidateIndex: Int,
        rejectedSuffixes: List<String>,
    ): Boolean {
        if (candidate.none(Char::isDigit)) return true
        if (candidate.lowercase(Locale.ROOT) in urlSchemeTokens) return true
        return isLikelyDateTimeToken(candidate, content, candidateIndex) ||
            isLikelyUrlToken(candidate, content, candidateIndex) ||
            isLikelyDecimalAmountToken(candidate, content, candidateIndex) ||
            isLikelyQuantityUnitToken(candidate, content, candidateIndex, rejectedSuffixes)
    }

    private fun isLikelyQuantityUnitToken(
        candidate: String,
        content: String,
        candidateIndex: Int,
        rejectedSuffixes: List<String>,
    ): Boolean {
        if (rejectedSuffixes.isEmpty()) return false
        val cLower = candidate.lowercase(Locale.ROOT)
        for (suffix in rejectedSuffixes) {
            val sLower = suffix.lowercase(Locale.ROOT)
            if (cLower.endsWith(sLower) && cLower.dropLast(sLower.length).any(Char::isDigit)) {
                return true
            }
        }
        val nextIdx = candidateIndex + candidate.length
        val remaining = content.substring(nextIdx.coerceAtMost(content.length)).trimStart().lowercase(Locale.ROOT)
        for (suffix in rejectedSuffixes) {
            val sLower = suffix.lowercase(Locale.ROOT)
            if (remaining.startsWith(sLower)) {
                return true
            }
        }
        return false
    }

    private fun isLikelyDecimalAmountToken(
        candidate: String,
        content: String,
        candidateIndex: Int,
    ): Boolean {
        if (!candidate.all(Char::isDigit)) return false
        val candidateIdx = candidateIndex
        if (candidateIdx < 0) return false
        val prevChar = content.getOrNull(candidateIdx - 1)
        val nextChar = content.getOrNull(candidateIdx + candidate.length)
        if (prevChar != '.' && nextChar != '.') return false
        val beforeDecimal = prevChar == '.' && content.getOrNull(candidateIdx - 2)?.isDigit() == true
        val afterDecimal = nextChar == '.' && content.getOrNull(candidateIdx + candidate.length + 1)?.isDigit() == true
        return beforeDecimal || afterDecimal
    }

    private fun matchesSource(rule: SmsCodeRuleSpec, source: SmsCodeParseSource?): Boolean {
        if (source == null) return true
        val packageNameHint = rule.packageNameHint?.trim().orEmpty()
        if (packageNameHint.isNotEmpty()) {
            val sourcePackage = source.packageName?.trim().orEmpty()
            if (!sourcePackage.equals(packageNameHint, ignoreCase = true)) return false
        }

        val senderRegex = rule.senderRegex?.trim().orEmpty()
        if (senderRegex.isEmpty()) return true
        val pattern = runCatching { Pattern.compile(senderRegex, Pattern.CASE_INSENSITIVE) }.getOrNull()
            ?: return false
        val sourceTexts = listOfNotNull(
            source.sender?.trim()?.takeIf { it.isNotEmpty() },
            source.title?.trim()?.takeIf { it.isNotEmpty() },
            source.channelId?.trim()?.takeIf { it.isNotEmpty() },
        )
        if (sourceTexts.isEmpty()) return false
        return sourceTexts.any { pattern.matcher(it).find() }
    }

    private fun isExplicitSourceMatch(rule: SmsCodeRuleSpec, source: SmsCodeParseSource?): Boolean {
        if (source == null) return false
        val packageNameHint = rule.packageNameHint?.trim().orEmpty()
        val senderRegex = rule.senderRegex?.trim().orEmpty()
        if (packageNameHint.isEmpty() && senderRegex.isEmpty()) return false

        if (packageNameHint.isNotEmpty()) {
            val sourcePackage = source.packageName?.trim().orEmpty()
            if (!sourcePackage.equals(packageNameHint, ignoreCase = true)) {
                return false
            }
        }

        if (senderRegex.isNotEmpty()) {
            val pattern = runCatching { Pattern.compile(senderRegex, Pattern.CASE_INSENSITIVE) }.getOrNull()
                ?: return false
            val sourceTexts = listOfNotNull(
                source.sender?.trim()?.takeIf { it.isNotEmpty() },
                source.title?.trim()?.takeIf { it.isNotEmpty() },
                source.channelId?.trim()?.takeIf { it.isNotEmpty() },
            )
            if (sourceTexts.none { pattern.matcher(it).find() }) {
                return false
            }
        }

        return true
    }
}
