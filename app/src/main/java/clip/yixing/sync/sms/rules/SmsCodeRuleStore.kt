package clip.yixing.sync.sms.rules

import android.content.Context
import android.util.Log
import kotlinx.serialization.json.Json
import java.io.File

/**
 * 短信验证码规则库的加载与更新。
 *
 * 规则解释器(匹配算法)在 [SmsCodeUtils],本类只负责「从哪里拿到规则」:
 *
 * 1. **内置快照**(`assets/smscode-rules` 下的 json)—— 打包在 APK 里,保证离线可用;
 * 2. **远端更新** —— 从配置的规则源拉取,成功后落盘覆盖缓存;
 * 3. **本地缓存** —— 更新成功后的结果,优先于内置快照使用。
 *
 * 优先级:本地缓存 > 内置快照。远端拉取失败时静默回退,不影响发短信时提取验证码。
 *
 * ## 为什么规则要分这么多条而不是一个正则
 *
 * 中文短信的验证码模板千差万别("验证码是839201"、"校验码:194820"、
 * "动态密码 K9X2P4"),单条正则要覆盖所有这些形式会退化成"抓任意 4~8 位数字",
 * 从而把订单号、卡号、金额一起抓出来。规则库的形式是「关键字 + 取码正则」成对出现,
 * 每条规则只在自己那条短信模板命中时才产出候选,这是准确率的来源。
 */
object SmsCodeRuleStore {

    private const val TAG = "SmsCodeRules"

    /** 内置规则所在的 assets 目录 */
    private const val ASSET_DIR = "smscode-rules"

    /** 缓存目录名(放在应用私有 files 下,不占 assets) */
    private const val CACHE_DIR = "smscode-rules-cache"

    /** 缓存文件名:所有规则集合并后的产物 */
    private const val CACHE_FILE = "rules.json"

    /** 规则源默认地址(与参考实现同源,中文规则集) */
    const val DEFAULT_RULE_SOURCE_URL =
        "https://gitlab.com/magisk3171/shared/magisk-smscode-rules/-/raw/main/rules/cn/general/cn.general.default.json"

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        encodeDefaults = true
    }

    /** 内存缓存:规则解析是 IO 与反序列化开销,避免每来一条短信都重算 */
    @Volatile
    private var cachedRules: List<SmsCodeRuleSpec>? = null

    /**
     * 取得当前生效的规则列表(带内存缓存)。
     *
     * 失败一律回退到内置快照 —— 规则库缺失会让所有短信都提取不到验证码,
     * 这比"规则稍旧"严重得多,所以任何一步出错都不能让结果为空。
     */
    fun rules(context: Context): List<SmsCodeRuleSpec> {
        cachedRules?.let { return it }
        val loaded = runCatching { loadFromCache(context) }
            .getOrNull()
            ?.takeIf { it.isNotEmpty() }
            ?: runCatching { loadFromAssets(context) }
                .getOrElse { emptyList() }
        cachedRules = loaded
        return loaded
    }

    /** 清空内存缓存,下次 [rules] 重新读盘。规则更新成功或设置变更后调用。 */
    fun invalidate() {
        cachedRules = null
    }

    /** 读取远端更新后写入的缓存 */
    private fun loadFromCache(context: Context): List<SmsCodeRuleSpec> {
        val file = cacheFile(context)
        if (!file.exists() || file.length() == 0L) return emptyList()
        return parseRulesDocument(file.readText())
    }

    /** 读取打包在 APK 里的内置规则集 */
    private fun loadFromAssets(context: Context): List<SmsCodeRuleSpec> {
        val names = runCatching { context.assets.list(ASSET_DIR)?.toList() }
            .getOrNull()
            .orEmpty()
            .filter { it.endsWith(".json") }
        if (names.isEmpty()) {
            Log.w(TAG, "no bundled rule sets found in assets/$ASSET_DIR")
            return emptyList()
        }
        val all = mutableListOf<SmsCodeRuleSpec>()
        for (name in names) {
            val text = runCatching {
                context.assets.open("$ASSET_DIR/$name").bufferedReader().use { it.readText() }
            }.getOrElse {
                Log.w(TAG, "read bundled rule set failed: $name")
                continue
            }
            all += runCatching { parseRulesDocument(text) }
                .getOrElse {
                    Log.w(TAG, "parse bundled rule set failed: $name")
                    emptyList()
                }
        }
        Log.d(TAG, "loaded ${all.size} rules from ${names.size} bundled set(s)")
        return all
    }

    /** 解析规则集文档(兼容单集与数组两种形态) */
    fun parseRulesDocument(text: String): List<SmsCodeRuleSpec> {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return emptyList()
        // 规则集本身是对象;容错处理"多个集合并成数组"的情况
        if (trimmed.startsWith("[")) {
            val sets = json.decodeFromString<List<SmsCodeRuleSetDocument>>(trimmed)
            return sets.flatMap { doc -> doc.rules.map { it.toRuleSpec() } }
        }
        val doc = json.decodeFromString<SmsCodeRuleSetDocument>(trimmed)
        return doc.rules.map { it.toRuleSpec() }
    }

    /**
     * 从远端拉取规则并落盘。
     *
     * 返回拉取到的规则条数,失败返回 null。
     * 调用方(SmsCodeSettingsPage 的「更新规则库」入口)负责提示结果。
     *
     * 注意:这里刻意不做校验和/签名校验 —— 规则库只影响"从短信里抓哪几个数字",
     * 且只在本地生效,被投毒的后果是提取错误而非安全问题;而引入校验会让
     * 规则库升级比现在更难维护。若后续要收紧,应加的是 SHA-256 比对。
     */
    suspend fun updateFromRemote(context: Context, sourceUrl: String): Int? {
        if (sourceUrl.isBlank()) return null
        val text = runCatching {
            java.net.URL(sourceUrl).openConnection().apply {
                connectTimeout = 10_000
                readTimeout = 15_000
                setRequestProperty("User-Agent", "NexClip/SmsCodeRules")
            }.getInputStream().bufferedReader().use { it.readText() }
        }.onFailure { Log.w(TAG, "fetch rules failed: ${it.message}") }
            .getOrNull()
            ?: return null

        val parsed = runCatching { parseRulesDocument(text) }
            .onFailure { Log.w(TAG, "parse fetched rules failed: ${it.message}") }
            .getOrNull()
            ?: return null
        if (parsed.isEmpty()) return null

        val ok = runCatching {
            val file = cacheFile(context)
            file.parentFile?.mkdirs()
            file.writeText(text)
        }.onFailure { Log.w(TAG, "write rule cache failed: ${it.message}") }
            .isSuccess
        if (!ok) return null

        invalidate()
        Log.d(TAG, "rules updated from remote: ${parsed.size} rules")
        return parsed.size
    }

    /** 丢弃远端缓存,回到内置快照 */
    fun resetToBundled(context: Context) {
        runCatching { cacheFile(context).delete() }
        invalidate()
    }

    /** 当前是否有远端更新过的规则缓存(用于设置页展示来源) */
    fun hasRemoteCache(context: Context): Boolean =
        runCatching { cacheFile(context).let { it.exists() && it.length() > 0L } }
            .getOrDefault(false)

    private fun cacheFile(context: Context): File =
        File(File(context.filesDir, CACHE_DIR), CACHE_FILE)
}
