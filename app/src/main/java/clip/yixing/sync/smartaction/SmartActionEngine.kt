package clip.yixing.sync.smartaction

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.widget.Toast
import androidx.compose.ui.graphics.Color
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.service.NotificationActionReceiver
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.util.SyncSettings

/**
 * 智能动作识别与应用直达引擎 (支持多客户端/应用变体版本适配与直达)
 */
object SmartActionEngine {

    /**
     * 文本扫描长度上限。所有提取器共用此上限: 若某一提取器使用更严格的上限,
     * 长短信/长推送会在该提取器上被静默丢弃, 而调用方无法察觉。
     */
    private const val MAX_SCAN_LENGTH = 10000

    /**
     * 弱特征(距离启发式)路径允许的候选码与关键字最大字符距离。实测真实下发短信为 2~10,
     * 广告类噪声数字可达 29; 取 16 可容纳真实用例并排除远距离噪声。
     */
    private const val MAX_DISTANCE_CANDIDATE = 16

    // ---------------------------------------------------------------------
    // 正则常量
    //
    // 以下正则在改造前都是「每次调用现场构造」: extractVerificationCode 单次调用要编译约 30 条,
    // 而它同时位于剪贴板捕获、通知构建与记录列表逐卡片组合三条热路径上。Kotlin 的 Regex 在
    // 模式相同的前提下构造成本主要是 java.util.regex.Pattern.compile 的一次性开销, 提升为
    // 对象级常量后仅在类初始化时编译一次。正则文本与语义保持与改造前逐一对应, 不做修改。
    // ---------------------------------------------------------------------

    /** 整段文本即一个纯数字/G-码 (如 "839201", "G-123456") */
    private val PURE_CODE_REGEX = Regex("^(?:G-)?([0-9]{4,8})$", RegexOption.IGNORE_CASE)

    /** Google 专属验证码格式 (G-123456 / G - 123456) */
    private val GOOGLE_CODE_REGEX = Regex("""(?i)\b(?:G\s*-\s*)([0-9]{6})\b""")

    /** 验证码类关键字(用于判定文本是否含关键字, 以及弱特征分支的距离计算) */
    private val CODE_KEYWORD_PATTERNS = listOf(
        Regex("""验证码"""),
        Regex("""动态码"""),
        Regex("""校验码"""),
        Regex("""安全码"""),
        Regex("""确认码"""),
        Regex("""动态密码"""),
        Regex("""授权码"""),
        Regex("""随机码"""),
        Regex("""短信验证码"""),
        Regex("""(?i)\bverification\s*code\b"""),
        Regex("""(?i)\bsecurity\s*code\b"""),
        Regex("""(?i)\bauth(?:entication)?\s*code\b"""),
        Regex("""(?i)\b(?:login|confirm(?:ation)?|access|pin)\s*code\b"""),
        Regex("""(?i)\bone-time\s*(?:passcode|password|code)\b"""),
        Regex("""(?i)\bpasscode\b"""),
        Regex("""(?i)\botp\b"""),
        Regex("""(?i)\b2fa\b""")
    )

    /** 验证码关键字的正则片段(供前后缀匹配复用) */
    private const val PREFIX_KEYWORD_FRAGMENT =
        """(?:验证码|动态码|校验码|安全码|确认码|动态密码|授权码|随机码|短信验证码|verification\s*code|security\s*code|auth(?:entication)?\s*code|login\s*code|confirm(?:ation)?\s*code|access\s*code|one-time\s*(?:passcode|password|code)|passcode|otp|2fa)"""

    /**
     * 强特征前置匹配的连接片段。重复次数设上限是为封顶回溯深度:
     * 无界量词在数千连续分隔符上会触发 StackOverflowError。
     */
    private const val CODE_SEPARATOR_FRAGMENT =
        """(?:\s+(?:is|was|be)|\s*[为是]|\s*[:：,\-，【\[\(（〔\)\]】]){0,8}"""

    /** 强特征前置匹配: 关键字紧跟验证码 (如 "验证码为: 123456", "OTP: 883920") */
    private val PREFIX_CODE_REGEX = Regex(
        """(?i)(?<![0-9A-Za-z])$PREFIX_KEYWORD_FRAGMENT(?![0-9A-Za-z])$CODE_SEPARATOR_FRAGMENT\s*([0-9a-zA-Z]{4,8})(?![0-9a-zA-Z])"""
    )

    /** 强特征后置匹配: 验证码在关键字前面 (如 "123456 为您的登录验证码") */
    private val SUFFIX_CODE_REGEX = Regex(
        """(?<![0-9a-zA-Z])([0-9a-zA-Z]{4,8})\s*(?:(?:为|是|，|,)\s*){0,16}[（\(]?(?:您的|本次|您本次)?(?:短信)?(?:登录|注册|支付|动态|身份)?$PREFIX_KEYWORD_FRAGMENT"""
    )

    /** 括号/特殊符号包裹的 4~8 位纯数字 (如 "【123456】") */
    private val BRACKET_CODE_REGEX = Regex("""[【\[〔（(]([0-9]{4,8})[】\]〕）)]""")

    /** 候选数字提取(排除版本号/路径/时间/尾号等邻接噪声) */
    private val ALL_NUMBERS_REGEX = Regex("""(?<![\d._\-/\\a-zA-Z])(\d{4,8})(?![\d._\-/\\a-zA-Z])""")

    /** 编程代码/路径判定所用的 URL 剔除正则 */
    private val URL_SCRUB_REGEX = Regex("""(?i)\bhttps?://\S+""")

    /** 编程代码特征正则集 */
    private val PROGRAMMING_CODE_PATTERNS = listOf(
        Regex("""(?i)::"""), // C++ / Rust / PowerShell 作用域解析符
        Regex("""(?i)\b(?:const|let|var|val|fun|def|function|class|import|package|namespace|public|private|protected)\b"""),
        Regex("""(?i)\b(?:System\.Environment|SetEnvironmentVariable|console\.log|println|return|SELECT\s+.*FROM)\b"""),
        Regex("""(?:[\\/][a-zA-Z0-9_.-]+){2,}[\\/]?"""), // 多层文件路径(至少两级目录)
        Regex("""(?i)\b[a-zA-Z]:[\\/]"""), // Windows 盘符路径, 如 C:\
        Regex("""(?i)\.(?:exe|dll|apk|jar|sh|bat|ps1|py|kt|java|js|ts|cpp|rs|json|xml|yaml|yml)\b"""),
        Regex("""[{}\[\];=]{3,}""") // 密集代码符号
    )

    private val URL_REGEX = Regex("(https?://[\\w\\-._~:/?#\\[\\]@!$&'()*+,;=%]+)", RegexOption.IGNORE_CASE)
    private val PHONE_REGEX = Regex("(?:\\+?86)?(1[3-9]\\d{9})|\\b(\\d{3,4}-\\d{7,8})\\b")
    private val EMAIL_REGEX = Regex("""(?i)\b([a-zA-Z0-9_.+-]+@([a-zA-Z0-9-]+\.[a-zA-Z0-9-.]+))\b""")
    private val DOMAIN_LABEL_REGEX = Regex("^[a-zA-Z0-9-]+$")
    private val EXPRESS_SF_REGEX = Regex("SF\\d{13}", RegexOption.IGNORE_CASE)
    private val EXPRESS_COMMON_REGEX = Regex("(?:单号|快递)[:：\\s]*([a-zA-Z0-9]{10,24})")
    private val COLOR_HEX_REGEX = Regex("^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")

    /**
     * 应用变体定义模型 (标准版、极速版、概念版、HD版等)
     */
    data class AppVariant(
        val packageName: String,
        val appName: String,
        val scheme: String? = null
    )

    // 1. 抖音族系 (标准版 / 极速版 / 火山版 / TikTok)
    private val DOUYIN_VARIANTS = listOf(
        AppVariant("com.ss.android.ugc.aweme", "抖音", "snssdk1128://"),
        AppVariant("com.ss.android.ugc.aweme.lite", "抖音极速版", "snssdk2329://"),
        AppVariant("com.ss.android.ugc.live", "抖音火山版", "snssdk1112://"),
        AppVariant("com.zhiliaoapp.musically", "TikTok", "snssdk1233://")
    )

    // 2. 快手族系 (标准版 / 极速版 / 概念版)
    private val KUAISHOU_VARIANTS = listOf(
        AppVariant("com.smile.gifmaker", "快手", "kwai://"),
        AppVariant("com.kuaishou.nebula", "快手极速版", "ksnebula://"),
        AppVariant("com.kwai.video", "快手概念版", "kwai://")
    )

    // 3. 哔哩哔哩族系 (标准版 / 概念版 / HD版 / 国际版)
    private val BILIBILI_VARIANTS = listOf(
        AppVariant("tv.danmaku.bili", "哔哩哔哩", "bilibili://"),
        AppVariant("com.bilibili.app.in", "哔哩哔哩概念版", "bilibili://"),
        AppVariant("tv.danmaku.bilibilihd", "哔哩哔哩 HD", "bilibili://"),
        AppVariant("com.bilibili.app.blue", "哔哩哔哩国际版", "bilibili://")
    )

    // 4. 淘宝族系 (手机淘宝 / 淘特极速版 / 淘宝HD)
    private val TAOBAO_VARIANTS = listOf(
        AppVariant("com.taobao.taobao", "手机淘宝", "taobao://"),
        AppVariant("com.taobao.litetao", "淘特 (特价版)", "litetao://"),
        AppVariant("com.taobao.pad", "淘宝 HD", "taobao://")
    )

    // 5. 京东族系 (京东 / 京东极速版 / 京喜)
    private val JD_VARIANTS = listOf(
        AppVariant("com.jingdong.app.mall", "京东", "openapp.jdmobile://"),
        AppVariant("com.jd.jdlite", "京东极速版", "openapp.jdlite://"),
        AppVariant("com.jingdong.pdj", "京喜", "openapp.jdmobile://")
    )

    // 6. 微博族系 (微博 / 微博轻享版国际版 / 微博极速版)
    private val WEIBO_VARIANTS = listOf(
        AppVariant("com.sina.weibo", "微博", "sinaweibo://"),
        AppVariant("com.weico.international", "微博轻享版", "weico://"),
        AppVariant("com.sina.weibolite", "微博极速版", "sinaweibo://")
    )

    // 7. 小红书族系
    private val XIAOHONGSHU_VARIANTS = listOf(
        AppVariant("com.xingin.xhs", "小红书", "xhsdiscover://")
    )

    // 8. 百度族系 (百度 / 百度极速版)
    private val BAIDU_VARIANTS = listOf(
        AppVariant("com.baidu.searchbox", "百度", "baiduboxapp://"),
        AppVariant("com.baidu.searchbox.lite", "百度极速版", "baiduboxliteapp://")
    )

    // 9. 知乎族系 (知乎 / 知乎极速版)
    private val ZHIHU_VARIANTS = listOf(
        AppVariant("com.zhihu.android", "知乎", "zhihu://"),
        AppVariant("com.zhihu.android.lite", "知乎极速版", "zhihu://")
    )

    // 10. 网易云音乐族系 (网易云音乐 / 极速版)
    private val CLOUDMUSIC_VARIANTS = listOf(
        AppVariant("com.netease.cloudmusic", "网易云音乐", "orpheus://"),
        AppVariant("com.netease.cloudmusic.lite", "网易云音乐极速版", "orpheus://")
    )

    // 11. QQ音乐族系 (QQ音乐 / 简洁版极速版)
    private val QQMUSIC_VARIANTS = listOf(
        AppVariant("com.tencent.qqmusic", "QQ音乐", "qqmusic://"),
        AppVariant("com.tencent.qqmusiclite", "QQ音乐简洁版", "qqmusic://")
    )

    /**
     * 包名 → 是否已安装 的进程级缓存。
     *
     * [findInstalledVariants] 位于「记录列表逐卡片组合」这一热路径上: 每条含链接/口令的记录
     * 都会枚举其应用族系的全部变体, 而每次 getPackageInfo 都是一次 PackageManager 跨进程
     * 调用。同一次滚动里同一批变体会被重复查询数十次且结果恒定, 故在此缓存。
     * 安装状态变化由 [invalidatePackageCache] 主动失效(见 SyncApp 的包变更广播)。
     */
    private val installedPackageCache = java.util.concurrent.ConcurrentHashMap<String, Boolean>()

    /**
     * 检测设备上已安装的目标应用变体列表
     */
    private fun findInstalledVariants(context: Context, variants: List<AppVariant>): List<AppVariant> {
        return variants.filter { isPackageInstalled(context, it.packageName) }
    }

    private fun isPackageInstalled(context: Context, packageName: String): Boolean {
        installedPackageCache[packageName]?.let { return it }
        val installed = runCatching {
            val pm = context.packageManager
            if (Build.VERSION.SDK_INT >= 33) {
                pm.getPackageInfo(packageName, PackageManager.PackageInfoFlags.of(0))
            } else {
                pm.getPackageInfo(packageName, 0)
            }
            true
        }.getOrDefault(false)
        installedPackageCache[packageName] = installed
        return installed
    }

    /**
     * 应用安装/卸载/更新后清空「已安装状态」缓存。
     * 不做失效会让动作列表长期停留在旧结果(如新装的极速版客户端一直不出现)。
     */
    fun invalidatePackageCache() {
        installedPackageCache.clear()
        // 计数器参与缓存指纹: 若仅靠缓存 size 判变化, 空缓存时清空前后 size 都是 0,
        // 指纹不变会导致旧的动作结果被继续命中, 覆盖不了新装的客户端。
        packageCacheGeneration++
    }

    /** 安装状态缓存的失效代数(见 [invalidatePackageCache]) */
    @Volatile
    private var packageCacheGeneration = 0

    /**
     * 智能识别文本中的操作意图并生成动作列表 (结合用户开关与自定义规则)
     *
     * 结果带缓存: 本方法位于记录列表「逐卡片组合」的热路径上, 每次调用要跑约 20 条正则、
     * 解析一遍自定义规则 JSON 并可能发起 PackageManager 查询。同一条文本在滚动/重组的
     * 过程中会被反复求值且结果恒定, 因此按「文本 + 设置指纹」缓存。
     * 设置变更(开关/自定义规则)会自然改变指纹而使旧条目失效, 无需手动清理。
     */
    fun detectActions(context: Context, text: String): List<SmartAction> {
        if (text.isBlank() || text.length > MAX_SCAN_LENGTH) return emptyList()

        val fingerprint = settingsFingerprint(context)
        val cacheKey = fingerprint + '#' + text.length + '#' + text.hashCode()
        detectionCache.get(cacheKey)?.let { cached ->
            // 命中哈希后仍需比对原文: hashCode 碰撞会返回另一条记录的动作(错误链接),
            // 代价远高于一次字符串比较, 故以原文相等为准。
            if (cached.text == text) return cached.actions
        }

        val actions = computeActions(context, text)

        // 仅缓存占位上限内的条目, 避免长会话下无界增长
        if (text.length <= MAX_CACHEABLE_TEXT_LENGTH) {
            detectionCache.put(cacheKey, CachedActions(text, actions))
        }
        return actions
    }

    /** 缓存条目: 保留原文以便命中时做精确比对, 避免 hashCode 碰撞返回错误动作 */
    private class CachedActions(val text: String, val actions: List<SmartAction>)

    /**
     * 文本级缓存上限。记录文本最多可到 500k, 但可缓存的只取较短者:
     * 长文本本身已超出真实剪贴板场景, 缓存它们只会白占内存。
     */
    private const val MAX_CACHEABLE_TEXT_LENGTH = 4096

    /** 动作识别结果缓存(键内已含设置指纹与文本哈希) */
    private val detectionCache = android.util.LruCache<String, CachedActions>(64)

    /** 自定义规则正则缓存(键为模式串) */
    private val ruleRegexCache = android.util.LruCache<String, Regex>(32)

    /** 取(并缓存)自定义规则的正则; 非法模式返回 null 由调用方 runCatching 语义承接 */
    private fun compiledRuleRegex(pattern: String): Regex {
        ruleRegexCache.get(pattern)?.let { return it }
        val regex = Regex(pattern, RegexOption.IGNORE_CASE)
        ruleRegexCache.put(pattern, regex)
        return regex
    }

    /**
     * 缓存 `SharedPreferences` 句柄。
     *
     * 指纹在每次 [detectActions] 调用时都要读一遍设置, 而 `Context.getSharedPreferences`
     * 内部是加锁的名字查表。同一进程内同名 prefs 恒为同一实例, 故缓存句柄以避免在
     * 「逐卡片组合」的循环里反复加锁。
     */
    @Volatile
    private var prefsHolder: android.content.SharedPreferences? = null

    private fun cachedPrefs(context: Context): android.content.SharedPreferences {
        prefsHolder?.let { return it }
        return SyncSettings.prefs(context.applicationContext).also { prefsHolder = it }
    }

    /**
     * 计算当前智能动作相关设置与安装状态的指纹。
     *
     * 之所以用指纹而不是监听设置变更: 设置写入分散在 SyncSettings 的多个 setter 中,
     * 逐个挂钩既易漏又引入耦合; 而读取内存态的 SharedPreferences 是廉价的,
     * 相较命中缓存所省下的正则与 IPC 开销可以忽略。
     */
    private fun settingsFingerprint(context: Context): String {
        val prefs = cachedPrefs(context)
        return buildString(64) {
            append(prefs.getBoolean(SyncSettings.KEY_SMART_ACTION_MASTER, true)).append('|')
            for (key in SMART_ACTION_TYPE_KEYS) {
                append(prefs.getBoolean(key, true)).append('|')
            }
            // 自定义规则原文即为指纹, 规则增删改都会改变它, 无需解析 JSON
            append(prefs.getString(SyncSettings.KEY_SMART_ACTION_CUSTOM_RULES, "") ?: "")
            append('|')
            // 安装状态变化(如新装极速版客户端)也要让缓存失效
            append(packageCacheGeneration)
        }
    }

    private val SMART_ACTION_TYPE_KEYS = arrayOf(
        SyncSettings.KEY_SMART_ACTION_CODE,
        SyncSettings.KEY_SMART_ACTION_DEEPLINK,
        SyncSettings.KEY_SMART_ACTION_URL,
        SyncSettings.KEY_SMART_ACTION_COMMAND,
        SyncSettings.KEY_SMART_ACTION_PHONE,
        SyncSettings.KEY_SMART_ACTION_EMAIL,
        SyncSettings.KEY_SMART_ACTION_EXPRESS,
        SyncSettings.KEY_SMART_ACTION_COLOR,
        SyncSettings.KEY_SMART_ACTION_MAP
    )

    private fun computeActions(context: Context, text: String): List<SmartAction> {
        if (!SyncSettings.isSmartActionMasterEnabled(context)) return emptyList()

        val actions = mutableListOf<SmartAction>()
        val trimmed = text.trim()

        // 1. 验证码智能提取 (优先)
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_CODE)) {
            val codeResult = extractVerificationCode(trimmed)
            if (codeResult != null) {
                val copyIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                    action = NotificationActionReceiver.ACTION_COPY_TEXT
                    putExtra(NotificationActionReceiver.EXTRA_CLIP_TEXT, codeResult)
                    putExtra(NotificationActionReceiver.EXTRA_TOAST_MSG, "已复制验证码: $codeResult")
                }
                actions.add(
                    SmartAction(
                        id = "code_$codeResult",
                        title = "复制验证码: $codeResult",
                        buttonLabel = "复制验证码",
                        summary = "提取短信纯数字验证码",
                        icon = LucideIcons.Key,
                        color = Color(0xFF10B981),
                        hexColor = "#10B981",
                        targetIntent = copyIntent,
                        isBroadcast = true
                    ) { ctx ->
                        ClipboardMonitorService.copyToClipboardInternal(ctx, ClipData.newPlainText("Code", codeResult), rawText = codeResult)
                        Toast.makeText(ctx, "已复制验证码: $codeResult", Toast.LENGTH_SHORT).show()
                    }
                )
            }
        }

        // 2. URL / 深度应用链接直达 (支持多客户端/极速版变体适配)
        val allowDeepLink = SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_DEEPLINK)
        val allowUrl = SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_URL)
        if (allowDeepLink || allowUrl) {
            val urls = extractUrls(trimmed)
            for (url in urls.take(2)) {
                if (allowDeepLink) {
                    val deepLinkActions = resolveDeepLinkActions(context, url)
                    if (deepLinkActions.isNotEmpty()) {
                        actions.addAll(deepLinkActions)
                        continue
                    }
                }
                if (allowUrl) {
                    val host = runCatching { Uri.parse(url).host }.getOrNull() ?: ""
                    val finalUrl = if (!url.startsWith("http://", ignoreCase = true) && !url.startsWith("https://", ignoreCase = true)) "https://$url" else url
                    val viewIntent = Intent(Intent.ACTION_VIEW, Uri.parse(finalUrl)).apply {
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    actions.add(
                        SmartAction(
                            id = "url_$url",
                            title = if (host.isNotBlank()) "访问 $host" else "打开网页",
                            buttonLabel = "打开链接",
                            summary = url,
                            icon = LucideIcons.ExternalLink,
                            color = Color(0xFF006EFF),
                            hexColor = "#006EFF",
                            targetIntent = viewIntent,
                            isBroadcast = false
                        ) { ctx ->
                            openUrl(ctx, url)
                        }
                    )
                }
            }
        }

        // 3. 电商/短视频/社交口令直达 (支持极速版等全变体检测，自动过滤已匹配的 DeepLink 重复项)
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_COMMAND)) {
            val existingTargetPackages = actions.mapNotNull { it.targetIntent?.`package` }.toSet()
            val commandActions = detectAppCommands(context, trimmed, existingTargetPackages)
            actions.addAll(commandActions)
        }

        // 4. 电话号码
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_PHONE)) {
            val phones = extractPhones(trimmed)
            for (phone in phones.take(1)) {
                val dialIntent = Intent(Intent.ACTION_DIAL, Uri.parse("tel:$phone")).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
                actions.add(
                    SmartAction(
                        id = "phone_$phone",
                        title = "呼叫 $phone",
                        buttonLabel = "呼叫",
                        icon = LucideIcons.Phone,
                        color = Color(0xFF006EFF),
                        hexColor = "#006EFF",
                        targetIntent = dialIntent,
                        isBroadcast = false
                    ) { ctx ->
                        runCatching { ctx.startActivity(dialIntent) }
                    }
                )
            }
        }

        // 5. 电子邮箱
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_EMAIL)) {
            val emails = extractEmails(trimmed)
            for (email in emails.take(1)) {
                val mailIntent = Intent(Intent.ACTION_SENDTO, Uri.parse("mailto:$email")).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
                actions.add(
                    SmartAction(
                        id = "email_$email",
                        title = "发邮件给 $email",
                        buttonLabel = "发邮件",
                        icon = LucideIcons.Mail,
                        color = Color(0xFF6366F1),
                        hexColor = "#6366F1",
                        targetIntent = mailIntent,
                        isBroadcast = false
                    ) { ctx ->
                        runCatching { ctx.startActivity(mailIntent) }
                    }
                )
            }
        }

        // 6. 快递单号
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_EXPRESS)) {
            val expressNo = extractExpressNumber(trimmed)
            if (expressNo != null) {
                val expressUrl = "https://www.baidu.com/s?wd=${Uri.encode(expressNo)}"
                val viewIntent = Intent(Intent.ACTION_VIEW, Uri.parse(expressUrl)).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
                actions.add(
                    SmartAction(
                        id = "express_$expressNo",
                        title = "查快递: $expressNo",
                        buttonLabel = "查快递",
                        icon = LucideIcons.Truck,
                        color = Color(0xFFF59E0B),
                        hexColor = "#F59E0B",
                        targetIntent = viewIntent,
                        isBroadcast = false
                    ) { ctx ->
                        openUrl(ctx, expressUrl)
                    }
                )
            }
        }

        // 7. 色彩代码识别
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_COLOR)) {
            val colorHex = extractColorHex(trimmed)
            if (colorHex != null) {
                val parsedColor = runCatching { Color(android.graphics.Color.parseColor(colorHex)) }.getOrNull()
                val copyIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                    action = NotificationActionReceiver.ACTION_COPY_TEXT
                    putExtra(NotificationActionReceiver.EXTRA_CLIP_TEXT, colorHex)
                    putExtra(NotificationActionReceiver.EXTRA_TOAST_MSG, "已复制色值: $colorHex")
                }
                actions.add(
                    SmartAction(
                        id = "color_$colorHex",
                        title = "色值 $colorHex",
                        buttonLabel = "复制色值",
                        icon = LucideIcons.Palette,
                        color = parsedColor,
                        hexColor = colorHex,
                        targetIntent = copyIntent,
                        isBroadcast = true
                    ) { ctx ->
                        ClipboardMonitorService.copyToClipboardInternal(ctx, ClipData.newPlainText("Color", colorHex), rawText = colorHex)
                        Toast.makeText(ctx, "已复制色值: $colorHex", Toast.LENGTH_SHORT).show()
                    }
                )
            }
        }

        // 8. 中文地址导航识别
        if (SyncSettings.isSmartActionTypeEnabled(context, SyncSettings.KEY_SMART_ACTION_MAP)) {
            val address = extractAddress(trimmed)
            if (address != null) {
                val geoIntent = Intent(Intent.ACTION_VIEW, Uri.parse("geo:0,0?q=${Uri.encode(address)}")).apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
                actions.add(
                    SmartAction(
                        id = "geo_$address",
                        title = "地图导航",
                        summary = address,
                        icon = LucideIcons.MapPin,
                        color = Color(0xFF10B981),
                        hexColor = "#10B981",
                        targetIntent = geoIntent,
                        isBroadcast = false
                    ) { ctx ->
                        runCatching { ctx.startActivity(geoIntent) }.onFailure {
                            openUrl(ctx, "https://uri.amap.com/search?keyword=${Uri.encode(address)}")
                        }
                    }
                )
            }
        }

        // 9. 用户自定义规则匹配
        val customRules = runCatching { SyncSettings.customSmartActionRules(context) }.getOrDefault(emptyList())
        for (rule in customRules) {
            if (!rule.enabled || rule.pattern.isBlank()) continue
            // 规则正则在热路径上按条重复编译, 且用户规则通常只有少数几条却会被每条记录各编译一次;
            // 按模式串缓存编译结果。模式串变更(用户改规则)会产生新键, 旧键由 LRU 自然淘汰。
            val matchResult = runCatching { compiledRuleRegex(rule.pattern).find(trimmed) }.getOrNull()
            if (matchResult != null) {
                val matchedValue = matchResult.value
                val group1 = matchResult.groupValues.getOrNull(1) ?: matchedValue
                val target = rule.targetTemplate
                    .replace("{match}", Uri.encode(matchedValue))
                    .replace("{1}", Uri.encode(group1))

                val customAction = when (rule.type) {
                    SmartActionType.URL -> {
                        val viewIntent = Intent(Intent.ACTION_VIEW, Uri.parse(target)).apply {
                            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        }
                        SmartAction(
                            id = "custom_${rule.id}",
                            title = rule.name,
                            summary = target,
                            icon = LucideIcons.Zap,
                            color = Color(0xFF8B5CF6),
                            hexColor = "#8B5CF6",
                            targetIntent = viewIntent,
                            isBroadcast = false
                        ) { ctx -> openUrl(ctx, target) }
                    }

                    SmartActionType.SCHEME -> {
                        val viewIntent = Intent(Intent.ACTION_VIEW, Uri.parse(target)).apply {
                            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        }
                        SmartAction(
                            id = "custom_${rule.id}",
                            title = rule.name,
                            summary = target,
                            icon = LucideIcons.Zap,
                            color = Color(0xFF8B5CF6),
                            hexColor = "#8B5CF6",
                            targetIntent = viewIntent,
                            isBroadcast = false
                        ) { ctx ->
                            runCatching { ctx.startActivity(viewIntent) }.onFailure {
                                Toast.makeText(ctx, "无法唤起目标应用", Toast.LENGTH_SHORT).show()
                            }
                        }
                    }

                    SmartActionType.COPY -> {
                        val copyIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                            action = NotificationActionReceiver.ACTION_COPY_TEXT
                            putExtra(NotificationActionReceiver.EXTRA_CLIP_TEXT, group1)
                            putExtra(NotificationActionReceiver.EXTRA_TOAST_MSG, "已复制: $group1")
                        }
                        SmartAction(
                            id = "custom_${rule.id}",
                            title = "${rule.name}: $group1",
                            buttonLabel = rule.name,
                            summary = "复制提取内容",
                            icon = LucideIcons.Copy,
                            color = Color(0xFF8B5CF6),
                            hexColor = "#8B5CF6",
                            targetIntent = copyIntent,
                            isBroadcast = true
                        ) { ctx ->
                            ClipboardMonitorService.copyToClipboardInternal(ctx, ClipData.newPlainText(rule.name, group1), rawText = group1)
                            Toast.makeText(ctx, "已复制: $group1", Toast.LENGTH_SHORT).show()
                        }
                    }
                }
                actions.add(customAction)
            }
        }

        return actions
    }

    private fun openUrl(context: Context, url: String) {
        val finalUrl = if (!url.startsWith("http://", ignoreCase = true) && !url.startsWith("https://", ignoreCase = true)) "https://$url" else url
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse(finalUrl)).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        runCatching { context.startActivity(intent) }.onFailure {
            Toast.makeText(context, "无法打开链接", Toast.LENGTH_SHORT).show()
        }
    }

    /**
     * 解析 DeepLink 并支持设备上安装的多版本/极速版客户端适配 (精简按钮文字避免折行截断)
     */
    private fun resolveDeepLinkActions(context: Context, url: String): List<SmartAction> {
        val lower = url.lowercase()

        // 1. 哔哩哔哩
        if (lower.contains("bilibili.com") || lower.contains("b23.tv")) {
            val installed = findInstalledVariants(context, BILIBILI_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "在${v.appName}打开", LucideIcons.Tv, Color(0xFFFB7299), "#FB7299")
                }
            } else {
                listOf(createFallbackUrlAction(context, "bili_$url", "在哔哩哔哩打开", url, LucideIcons.Tv, Color(0xFFFB7299), "#FB7299"))
            }
        }

        // 2. 抖音 / TikTok
        if (lower.contains("douyin.com") || lower.contains("iesdouyin.com")) {
            val installed = findInstalledVariants(context, DOUYIN_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.Tv, Color(0xFF111111), "#111111")
                }
            } else {
                listOf(createFallbackUrlAction(context, "douyin_$url", "打开抖音", url, LucideIcons.Tv, Color(0xFF111111), "#111111"))
            }
        }

        // 3. 快手
        if (lower.contains("kuaishou.com") || lower.contains("kwai.com")) {
            val installed = findInstalledVariants(context, KUAISHOU_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.Tv, Color(0xFFFF5000), "#FF5000")
                }
            } else {
                listOf(createFallbackUrlAction(context, "kuaishou_$url", "打开快手", url, LucideIcons.Tv, Color(0xFFFF5000), "#FF5000"))
            }
        }

        // 4. 淘宝 / 淘特
        if (lower.contains("taobao.com") || lower.contains("tb.cn")) {
            val installed = findInstalledVariants(context, TAOBAO_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.ShoppingBag, Color(0xFFFF5000), "#FF5000")
                }
            } else {
                listOf(createFallbackUrlAction(context, "tb_$url", "打开手机淘宝", url, LucideIcons.ShoppingBag, Color(0xFFFF5000), "#FF5000"))
            }
        }

        // 5. 京东 / 京东极速版 / 京喜
        if (lower.contains("jd.com") || lower.contains("3.cn")) {
            val installed = findInstalledVariants(context, JD_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.ShoppingBag, Color(0xFFE1251B), "#E1251B")
                }
            } else {
                listOf(createFallbackUrlAction(context, "jd_$url", "打开京东", url, LucideIcons.ShoppingBag, Color(0xFFE1251B), "#E1251B"))
            }
        }

        // 6. 小红书
        if (lower.contains("xhslink.com") || lower.contains("xiaohongshu.com")) {
            val installed = findInstalledVariants(context, XIAOHONGSHU_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(1).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.Sparkles, Color(0xFFFF2442), "#FF2442")
                }
            } else {
                listOf(createFallbackUrlAction(context, "xhs_$url", "打开小红书", url, LucideIcons.Sparkles, Color(0xFFFF2442), "#FF2442"))
            }
        }

        // 7. 微博 / 微博轻享版
        if (lower.contains("weibo.com") || lower.contains("weibo.cn") || lower.contains("t.cn")) {
            val installed = findInstalledVariants(context, WEIBO_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.Share2, Color(0xFFE6162D), "#E6162D")
                }
            } else {
                listOf(createFallbackUrlAction(context, "weibo_$url", "打开微博", url, LucideIcons.Share2, Color(0xFFE6162D), "#E6162D"))
            }
        }

        // 8. 知乎 / 知乎极速版
        if (lower.contains("zhihu.com")) {
            val installed = findInstalledVariants(context, ZHIHU_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "打开${v.appName}", LucideIcons.ExternalLink, Color(0xFF0066FF), "#0066FF")
                }
            } else {
                listOf(createFallbackUrlAction(context, "zhihu_$url", "打开知乎", url, LucideIcons.ExternalLink, Color(0xFF0066FF), "#0066FF"))
            }
        }

        // 9. 网易云音乐
        if (lower.contains("music.163.com") || lower.contains("163cn.tv")) {
            val installed = findInstalledVariants(context, CLOUDMUSIC_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "在${v.appName}播放", LucideIcons.Sparkles, Color(0xFFC20C0C), "#C20C0C")
                }
            } else {
                listOf(createFallbackUrlAction(context, "cloudmusic_$url", "在网易云音乐播放", url, LucideIcons.Sparkles, Color(0xFFC20C0C), "#C20C0C"))
            }
        }

        // 10. QQ音乐
        if (lower.contains("y.qq.com") || lower.contains("c6.y.qq.com")) {
            val installed = findInstalledVariants(context, QQMUSIC_VARIANTS)
            return if (installed.isNotEmpty()) {
                installed.take(2).map { v ->
                    createVariantUrlAction(context, v, url, "在${v.appName}播放", LucideIcons.Sparkles, Color(0xFF31C27C), "#31C27C")
                }
            } else {
                listOf(createFallbackUrlAction(context, "qqmusic_$url", "在QQ音乐播放", url, LucideIcons.Sparkles, Color(0xFF31C27C), "#31C27C"))
            }
        }

        // 11. GitHub
        if (lower.contains("github.com")) {
            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(url)).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            return listOf(
                SmartAction(
                    id = "github_$url",
                    title = "查看 GitHub 仓库",
                    icon = LucideIcons.Code,
                    color = Color(0xFF24292F),
                    hexColor = "#24292F",
                    targetIntent = intent,
                    isBroadcast = false
                ) { ctx -> openUrl(ctx, url) }
            )
        }

        return emptyList()
    }

    private fun createVariantUrlAction(
        context: Context,
        variant: AppVariant,
        url: String,
        title: String,
        icon: androidx.compose.ui.graphics.vector.ImageVector,
        color: Color,
        hexColor: String
    ): SmartAction {
        val targetIntent = Intent(Intent.ACTION_VIEW, Uri.parse(url)).apply {
            setPackage(variant.packageName)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        return SmartAction(
            id = "${variant.packageName}_$url",
            title = title,
            icon = icon,
            color = color,
            hexColor = hexColor,
            targetPackage = variant.packageName,
            targetIntent = targetIntent,
            isBroadcast = false
        ) { ctx ->
            runCatching { ctx.startActivity(targetIntent) }.onFailure { openUrl(ctx, url) }
        }
    }

    private fun createFallbackUrlAction(
        context: Context,
        id: String,
        title: String,
        url: String,
        icon: androidx.compose.ui.graphics.vector.ImageVector,
        color: Color,
        hexColor: String,
        targetPackage: String? = null
    ): SmartAction {
        val targetIntent = Intent(Intent.ACTION_VIEW, Uri.parse(url)).apply {
            if (targetPackage != null) setPackage(targetPackage)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        return SmartAction(
            id = id,
            title = title,
            icon = icon,
            color = color,
            hexColor = hexColor,
            targetPackage = targetPackage,
            targetIntent = targetIntent,
            isBroadcast = false
        ) { ctx -> openUrl(ctx, url) }
    }

    /**
     * 智能识别口令格式并唤起已安装的客户端 (支持标准版、极速版等变体，自动跳过已存在的包名)
     */
    private fun detectAppCommands(context: Context, text: String, existingTargetPackages: Set<String>): List<SmartAction> {
        val actions = mutableListOf<SmartAction>()

        // 1. 淘宝口令
        val isTaobao = text.contains("【淘宝】") || text.contains("￥") || text.contains("復製这条信息") ||
                (text.length in 8..30 && text.startsWith("￥") && text.endsWith("￥")) || text.contains("淘口令")
        if (isTaobao) {
            val installed = findInstalledVariants(context, TAOBAO_VARIANTS).filter { !existingTargetPackages.contains(it.packageName) }
            if (installed.isNotEmpty()) {
                installed.take(2).forEach { v ->
                    val intent = (context.packageManager.getLaunchIntentForPackage(v.packageName) ?: Intent(Intent.ACTION_VIEW, Uri.parse(v.scheme ?: "taobao://"))).apply {
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    actions.add(
                        SmartAction(
                            id = "${v.packageName}_cmd",
                            title = "打开${v.appName}",
                            icon = LucideIcons.ShoppingBag,
                            color = Color(0xFFFF5000),
                            hexColor = "#FF5000",
                            targetPackage = v.packageName,
                            targetIntent = intent,
                            isBroadcast = false
                        ) { ctx ->
                            runCatching { ctx.startActivity(intent) }.onFailure {
                                Toast.makeText(ctx, "未安装${v.appName}客户端", Toast.LENGTH_SHORT).show()
                            }
                        }
                    )
                }
            } else if (existingTargetPackages.none { it.contains("taobao") || it.contains("litetao") }) {
                val intent = Intent(Intent.ACTION_VIEW, Uri.parse("taobao://")).apply { addFlags(Intent.FLAG_ACTIVITY_NEW_TASK) }
                actions.add(
                    SmartAction(
                        id = "tb_cmd",
                        title = "打开手机淘宝",
                        icon = LucideIcons.ShoppingBag,
                        color = Color(0xFFFF5000),
                        hexColor = "#FF5000",
                        targetPackage = "com.taobao.taobao",
                        targetIntent = intent,
                        isBroadcast = false
                    ) { ctx ->
                        runCatching { ctx.startActivity(intent) }.onFailure {
                            Toast.makeText(ctx, "未安装淘宝相关客户端", Toast.LENGTH_SHORT).show()
                        }
                    }
                )
            }
        }

        // 2. 抖音口令
        val isDouyin = text.contains("【抖音】") || text.contains("%抖音") || text.contains("v.douyin.com") || text.contains("抖音口令")
        if (isDouyin) {
            val installed = findInstalledVariants(context, DOUYIN_VARIANTS).filter { !existingTargetPackages.contains(it.packageName) }
            if (installed.isNotEmpty()) {
                installed.take(2).forEach { v ->
                    val intent = (context.packageManager.getLaunchIntentForPackage(v.packageName) ?: Intent(Intent.ACTION_VIEW, Uri.parse(v.scheme ?: "snssdk1128://"))).apply {
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    actions.add(
                        SmartAction(
                            id = "${v.packageName}_cmd",
                            title = "打开${v.appName}",
                            icon = LucideIcons.Tv,
                            color = Color(0xFF111111),
                            hexColor = "#111111",
                            targetPackage = v.packageName,
                            targetIntent = intent,
                            isBroadcast = false
                        ) { ctx ->
                            runCatching { ctx.startActivity(intent) }.onFailure {
                                Toast.makeText(ctx, "未安装${v.appName}客户端", Toast.LENGTH_SHORT).show()
                            }
                        }
                    )
                }
            } else if (existingTargetPackages.none { it.contains("aweme") || it.contains("live") || it.contains("musically") }) {
                val intent = Intent(Intent.ACTION_VIEW, Uri.parse("snssdk1128://")).apply { addFlags(Intent.FLAG_ACTIVITY_NEW_TASK) }
                actions.add(
                    SmartAction(
                        id = "douyin_cmd",
                        title = "打开抖音",
                        icon = LucideIcons.Tv,
                        color = Color(0xFF111111),
                        hexColor = "#111111",
                        targetPackage = "com.ss.android.ugc.aweme",
                        targetIntent = intent,
                        isBroadcast = false
                    ) { ctx ->
                        runCatching { ctx.startActivity(intent) }.onFailure {
                            Toast.makeText(ctx, "未安装抖音相关客户端", Toast.LENGTH_SHORT).show()
                        }
                    }
                )
            }
        }

        // 3. 快手口令
        val isKuaishou = text.contains("【快手】") || text.contains("快手口令") || text.contains("v.kuaishou.com")
        if (isKuaishou) {
            val installed = findInstalledVariants(context, KUAISHOU_VARIANTS).filter { !existingTargetPackages.contains(it.packageName) }
            if (installed.isNotEmpty()) {
                installed.take(2).forEach { v ->
                    val intent = (context.packageManager.getLaunchIntentForPackage(v.packageName) ?: Intent(Intent.ACTION_VIEW, Uri.parse(v.scheme ?: "kwai://"))).apply {
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    actions.add(
                        SmartAction(
                            id = "${v.packageName}_cmd",
                            title = "打开${v.appName}",
                            icon = LucideIcons.Tv,
                            color = Color(0xFFFF5000),
                            hexColor = "#FF5000",
                            targetPackage = v.packageName,
                            targetIntent = intent,
                            isBroadcast = false
                        ) { ctx ->
                            runCatching { ctx.startActivity(intent) }.onFailure {
                                Toast.makeText(ctx, "未安装${v.appName}客户端", Toast.LENGTH_SHORT).show()
                            }
                        }
                    )
                }
            }
        }

        // 4. 京东口令
        val isJd = text.contains("【京东】") || text.contains("京口令") || text.contains("3.cn")
        if (isJd) {
            val installed = findInstalledVariants(context, JD_VARIANTS).filter { !existingTargetPackages.contains(it.packageName) }
            if (installed.isNotEmpty()) {
                installed.take(2).forEach { v ->
                    val intent = (context.packageManager.getLaunchIntentForPackage(v.packageName) ?: Intent(Intent.ACTION_VIEW, Uri.parse(v.scheme ?: "openapp.jdmobile://"))).apply {
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    actions.add(
                        SmartAction(
                            id = "${v.packageName}_cmd",
                            title = "打开${v.appName}",
                            icon = LucideIcons.ShoppingBag,
                            color = Color(0xFFE1251B),
                            hexColor = "#E1251B",
                            targetPackage = v.packageName,
                            targetIntent = intent,
                            isBroadcast = false
                        ) { ctx ->
                            runCatching { ctx.startActivity(intent) }.onFailure {
                                Toast.makeText(ctx, "未安装${v.appName}客户端", Toast.LENGTH_SHORT).show()
                            }
                        }
                    )
                }
            }
        }

        return actions
    }

    /**
     * 判断文本是否明显属于编程代码、脚本、路径或 SQL，避免误将代码/版本号识别为短信验证码
     */
    private fun isLikelyProgrammingCode(text: String): Boolean {
        // URL 不是编程代码: 其中的 "https://host.tld/" 会被下面的路径规则命中(域名恰好呈现为
        // 斜杠包裹的片段), 而短信常同时携带验证码与链接, 若不剔除会整条跳过提取。故先移除 URL。
        val scrubbed = text.replace(URL_SCRUB_REGEX, " ")
        return PROGRAMMING_CODE_PATTERNS.any { it.containsMatchIn(scrubbed) }
    }

    /**
     * 工业级短信验证码智能提取引擎
     *
     * 除剪贴板文本路径外, 短信接收路径也直接调用本方法 (见 sms/SmsCodeHandler),
     * 两处共用同一套判定, 避免出现「剪贴板能提取、短信不能」这类规则漂移。
     */
    internal fun extractVerificationCode(text: String): String? {
        val trimmed = text.trim()
        if (trimmed.length > MAX_SCAN_LENGTH) return null

        // 0. 纯数字/G-码快速提取 (如 "839201", "G-123456")
        // 整段文本只有一个 4~8 位数字串时不存在周边上下文, isValidCodeCandidate 的判定
        // (前缀/后缀上下文、边界字符)全部无法生效, 编程代码防御对此类输入亦恒为 false。
        // 因此该分支不再追加过滤: 单数字串一律视为验证码, 以保住 "123456" 这类只含验证码的短信。
        PURE_CODE_REGEX.find(trimmed)?.groupValues?.getOrNull(1)?.let { return it }

        // 关键防御: 若内容属于明显的编程代码/脚本命令/文件路径，直接跳过，杜绝误识别
        if (isLikelyProgrammingCode(text)) return null

        // 1. Google 专属验证码格式 (G-123456 / G - 123456)
        GOOGLE_CODE_REGEX.find(text)?.groupValues?.getOrNull(1)?.let { return it }

        val codeKeywordPatterns = CODE_KEYWORD_PATTERNS

        val hasCodeKeyword = codeKeywordPatterns.any { it.containsMatchIn(text) }
        if (!hasCodeKeyword) return null

        // 2. 强特征前置匹配: 关键字紧跟验证码 (如: "验证码为: 123456", "Code is 492018", "OTP: 883920")
        // 边界断言用 (?<![0-9A-Za-z]) 而非 \b: Java/Kotlin 的 \b 基于 ASCII \w, 而中文汉字属非单词字符,
        // 汉字与汉字之间不存在单词边界, 故 \b验证码\b 对中文文本永不匹配 —— 而中文短信正是主要场景。
        // 分隔符里同时收 为/是 的无空格写法 (如 "验证码为550875"), 这是中文短信最常见的形式。
        // 重复次数设上限是为封顶回溯深度: 无界量词在数千连续分隔符上会触发 StackOverflowError。
        val prefixRegex = PREFIX_CODE_REGEX
        for (match in prefixRegex.findAll(text)) {
            val candidate = match.groupValues.getOrNull(1) ?: continue
            val start = match.range.first + match.value.lastIndexOf(candidate)
            val end = start + candidate.length
            if (isValidCodeCandidate(text, candidate, start, end, strongMatch = true)) {
                return candidate
            }
        }

        // 3. 强特征后置匹配: 验证码在关键字前面 (如: "123456 为您的登录验证码", "9527 是本次动态码")
        // 同上, 连接词组的重复次数设上限以封顶回溯深度; 上限同样远高于真实短信用量。
        val suffixRegex = SUFFIX_CODE_REGEX
        for (match in suffixRegex.findAll(text)) {
            val candidate = match.groupValues.getOrNull(1) ?: continue
            val start = match.range.first
            val end = start + candidate.length
            if (isValidCodeCandidate(text, candidate, start, end)) {
                return candidate
            }
        }

        // 4. 括号/特殊符号包裹的 4~8 位纯数字 (如 "【123456】", "[892014]")
        val bracketRegex = BRACKET_CODE_REGEX
        for (match in bracketRegex.findAll(text)) {
            val candidate = match.groupValues.getOrNull(1) ?: continue
            val start = match.range.first + 1
            val end = start + candidate.length
            if (isValidCodeCandidate(text, candidate, start, end)) {
                return candidate
            }
        }

        // 5. 候选数字距离加权提取 (排除版本号/路径/时间/尾号后，选取与"验证码"关键字距离最近的纯数字)
        val allNumbersRegex = ALL_NUMBERS_REGEX
        val matches = allNumbersRegex.findAll(text).toList()
        if (matches.isEmpty()) return null

        val keywordIndices = mutableListOf<Int>()
        for (kwPattern in codeKeywordPatterns) {
            for (m in kwPattern.findAll(text)) {
                keywordIndices.add(m.range.first)
            }
        }

        if (keywordIndices.isEmpty()) return null

        var bestCandidate: String? = null
        var minDistance = Int.MAX_VALUE

        for (m in matches) {
            val candidate = m.groupValues[1]
            val start = m.range.first
            val end = m.range.last + 1
            if (!isValidCodeCandidate(text, candidate, start, end)) continue

            val distance = keywordIndices.minOf { kwIdx ->
                if (start >= kwIdx) start - kwIdx else kwIdx - end
            }
            // 距离上限: 本分支纯靠"离关键字近"推断, 无相邻性证据。实测真实下发短信的码与关键字
            // 距离为 2~10 字符; 而广告类文本(如"前1000名...验证码戳")的噪声数字距离可达 29。
            // 不设上限会把这类无关数字当成验证码, 因此限定为紧邻窗口内的候选。
            if (distance > MAX_DISTANCE_CANDIDATE) continue
            if (distance < minDistance) {
                minDistance = distance
                bestCandidate = candidate
            }
        }

        return bestCandidate
    }

    /**
     * 校验候选验证码是否有效（过滤常见干扰场景：版本号、变量标识符、文件路径、金额、时间、卡号）
     *
     * @param strongMatch 候选是否来自「关键字在前、码紧随其后」的前置强特征分支。该分支下码与
     *   关键字相邻本身就是下发证据, 故不再套用身份类过滤: 真实短信常写成「您正在进行账号注册，
     *   验证码为550875」, 码前 12 字窗口内含「账号」等词, 但码确实由关键字直接引出, 属正常下发,
     *   若一并无差别过滤会漏报。身份类过滤仅用于弱特征与后缀分支, 防止误取尾号/卡号等数字。
     */
    private fun isValidCodeCandidate(
        fullText: String,
        candidate: String,
        startIndex: Int,
        endIndex: Int,
        strongMatch: Boolean = false
    ): Boolean {
        if (candidate.all { it.isLetter() }) return false

        // 边界字符校验：避免作为版本号(如 26.820.7780.0)、变量标识符、路径的一部分
        if (startIndex > 0) {
            val prevChar = fullText[startIndex - 1]
            if (prevChar in "._-/\\$@%#=") return false
        }
        if (endIndex < fullText.length) {
            val nextChar = fullText[endIndex]
            if (nextChar in "._-/\\$@%#=") return false
        }

        val prefixContext = fullText.substring(maxOf(0, startIndex - 12), startIndex)
        val suffixContext = fullText.substring(minOf(fullText.length, endIndex + 1), minOf(fullText.length, endIndex + 12))

        if (!strongMatch) {
            if (prefixContext.contains("尾号") || prefixContext.contains("卡号") || prefixContext.contains("账号") || prefixContext.contains("户名")) {
                return false
            }
        }
        if (prefixContext.contains("致电") || prefixContext.contains("客服") || prefixContext.contains("电话") || prefixContext.contains("热线") || prefixContext.contains("拨打")) {
            return false
        }
        // 日期单位可跟 4 位年份(2024年), 故 年月日 对任意长度都要判; 而 点/时/分/秒 只能跟在
        // 1~2 位时刻后(20点/9时), 不可能跟在 4~8 位验证码后, 其检查对本候选范围无意义, 反而会
        // 误杀 点击/点评/分钟/分秒 等复合词开头的正常短信。故时刻类单位仅对短候选生效。
        val suffixStartsDateUnit = suffixContext.startsWith("年") || suffixContext.startsWith("月") || suffixContext.startsWith("日")
        val suffixStartsClockUnit = suffixContext.startsWith("点") || suffixContext.startsWith("时") ||
            suffixContext.startsWith("分") || suffixContext.startsWith("秒")
        if (suffixStartsDateUnit || (suffixStartsClockUnit && candidate.length <= 2)) {
            return false
        }
        if (prefixContext.endsWith("年") || prefixContext.endsWith("月") || prefixContext.endsWith("日") ||
            prefixContext.endsWith("时") || prefixContext.endsWith("点")) {
            return false
        }
        if (suffixContext.startsWith("元") || suffixContext.startsWith("块") || suffixContext.startsWith("角") || suffixContext.startsWith("分钱") || suffixContext.startsWith("USD")) {
            return false
        }
        if (prefixContext.endsWith("¥") || prefixContext.endsWith("￥") || prefixContext.endsWith("$") || prefixContext.endsWith("金额")) {
            return false
        }
        if (prefixContext.contains("订单") || prefixContext.contains("运单") || prefixContext.contains("快递")) {
            return false
        }
        val asInt = candidate.toIntOrNull()
        if (asInt != null && asInt in 1990..2035) {
            if (suffixContext.contains("年") || suffixContext.startsWith("-") || suffixContext.startsWith("/")) {
                return false
            }
        }

        return true
    }

    private fun extractUrls(text: String): List<String> {
        return URL_REGEX.findAll(text).map { it.value }.distinct().toList()
    }

    private fun extractPhones(text: String): List<String> {
        return PHONE_REGEX.findAll(text).map { it.value }.distinct().toList()
    }

    /**
     * 内置各大邮箱服务商与教育/科研机构后缀精准识别
     */
    private fun extractEmails(text: String): List<String> {
        val results = mutableListOf<String>()

        for (match in EMAIL_REGEX.findAll(text)) {
            val fullEmail = match.groupValues[1].trim()
            val domain = match.groupValues[2].lowercase().trim('.')
            if (isValidEmailDomain(domain)) {
                results.add(fullEmail)
            }
        }
        return results.distinct()
    }

    /**
     * 校验邮箱域名是否为合法的主流服务商、教育机构、学术/政务后缀或标准顶级域
     */
    private fun isValidEmailDomain(domain: String): Boolean {
        if (domain.length !in 3..100 || !domain.contains('.')) return false

        // 1. 国内与国际主流邮箱服务商白名单
        val majorEmailDomains = setOf(
            // 国内主流服务商
            "qq.com", "foxmail.com", "vip.qq.com",
            "163.com", "126.com", "yeah.net", "188.com",
            "sina.com", "sina.cn", "vip.sina.com",
            "sohu.com", "139.com", "189.cn", "wo.cn",
            "aliyun.com", "dingtalk.com", "feishu.cn", "bytedance.com",
            "tom.com", "21cn.com", "netease.com", "huawei.com", "xiaomi.com",

            // 国际主流服务商
            "gmail.com", "googlemail.com",
            "outlook.com", "hotmail.com", "live.com", "msn.com",
            "icloud.com", "me.com", "mac.com",
            "yahoo.com", "yahoo.com.cn", "yahoo.co.jp", "yahoo.com.hk",
            "proton.me", "protonmail.com", "pm.me",
            "zoho.com", "zoho.com.cn", "gmx.com", "gmx.net", "mail.com",
            "yandex.com", "yandex.ru", "fastmail.com", "aol.com", "tutanota.com", "tutamail.com"
        )
        if (majorEmailDomains.contains(domain)) return true

        // 2. 全球高校教育机构后缀 (Edu)
        val isEdu = domain.endsWith(".edu.cn") ||
                domain.endsWith(".edu") ||
                domain.endsWith(".edu.hk") ||
                domain.endsWith(".edu.tw") ||
                domain.endsWith(".edu.mo") ||
                domain.endsWith(".edu.sg") ||
                domain.endsWith(".edu.au") ||
                domain.endsWith(".edu.my") ||
                domain.endsWith(".edu.uk")
        if (isEdu) return true

        // 3. 学术科研机构后缀 (Academic)
        val isAcademic = domain.endsWith(".ac.cn") ||
                domain.endsWith(".ac.uk") ||
                domain.endsWith(".ac.jp") ||
                domain.endsWith(".ac.kr") ||
                domain.endsWith(".ac.in") ||
                domain.endsWith(".cas.cn")
        if (isAcademic) return true

        // 4. 政务与非盈利组织后缀 (Gov / Org)
        val isGovOrOrg = domain.endsWith(".gov.cn") ||
                domain.endsWith(".gov") ||
                domain.endsWith(".org.cn") ||
                domain.endsWith(".org")
        if (isGovOrOrg) return true

        // 5. 标准顶级域名 (TLD) 合法性校验 (支持企业自定义域名企业邮箱)
        val tld = domain.substringAfterLast('.')
        val validTlds = setOf(
            "com", "cn", "net", "org", "io", "cc", "co", "vip", "xyz",
            "top", "tech", "me", "info", "biz", "dev", "app", "ai",
            "hk", "tw", "jp", "kr", "uk", "de", "fr", "ca", "au", "sg", "ru"
        )
        if (validTlds.contains(tld)) {
            val domainParts = domain.split('.')
            if (domainParts.all { it.isNotBlank() && DOMAIN_LABEL_REGEX.matches(it) && !it.startsWith("-") && !it.endsWith("-") }) {
                return true
            }
        }

        return false
    }

    private fun extractExpressNumber(text: String): String? {
        if (!text.contains("快递") && !text.contains("单号") && !text.contains("运单") && !text.startsWith("SF")) {
            return null
        }
        val sfRegex = EXPRESS_SF_REGEX
        sfRegex.find(text)?.value?.let { return it }

        val commonRegex = EXPRESS_COMMON_REGEX
        commonRegex.find(text)?.groupValues?.getOrNull(1)?.let { return it }

        return null
    }

    private fun extractColorHex(text: String): String? {
        val trimmed = text.trim()
        val hexRegex = COLOR_HEX_REGEX
        if (hexRegex.matches(trimmed)) return trimmed
        return null
    }

    private fun extractAddress(text: String): String? {
        if (text.length !in 6..60) return null
        val keywords = listOf("省", "市", "区", "县", "路", "街", "号", "大厦", "广场", "小区", "村", "道", "栋", "层", "室", "酒店", "大学")
        var matchCount = 0
        for (kw in keywords) {
            if (text.contains(kw)) matchCount++
        }
        if (matchCount >= 2) return text
        return null
    }
}
