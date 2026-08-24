package clip.yixing.sync.util

import android.app.ActivityManager
import android.app.AppOpsManager
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.ClipData
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.os.Build
import android.os.Process
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import java.util.concurrent.ConcurrentHashMap

/**
 * 剪贴板来源应用解析器:
 * 1. 从 ClipData.description.extras 中读取 (Xposed 注入或系统提供)
 * 2. 从 UsageStatsManager (应用使用情况) 中回溯最近前台活动
 * 3. 从 ActivityManager 前台进程列表中获取
 * 4. 解析包名对应的应用名称与高清图标 (带内存多级缓存与主流应用字典兜底)
 */
object AppSourceHelper {

    private val nameCache = ConcurrentHashMap<String, String>()
    private val iconCache = ConcurrentHashMap<String, ImageBitmap>()

    private val KNOWN_APPS = mapOf(
        "clip.yixing.sync" to "NexClip",
        "com.tencent.mm" to "微信",
        "com.tencent.mobileqq" to "QQ",
        "com.tencent.tim" to "TIM",
        "com.coolapk.market" to "酷安",
        "com.zhihu.android" to "知乎",
        "com.eg.android.AlipayGphone" to "支付宝",
        "com.android.chrome" to "Chrome",
        "com.microsoft.emmx" to "Edge",
        "com.xingin.xhs" to "小红书",
        "com.sina.weibo" to "微博",
        "com.taobao.taobao" to "淘宝",
        "com.jingdong.app.mall" to "京东",
        "com.xunmeng.pinduoduo" to "拼多多",
        "com.bilibili.app.in" to "哔哩哔哩",
        "tv.danmaku.bili" to "哔哩哔哩",
        "com.ss.android.ugc.aweme" to "抖音",
        "com.smile.gifmaker" to "快手",
        "com.netease.cloudmusic" to "网易云音乐",
        "com.kugou.android" to "酷狗音乐",
        "com.autonavi.minimap" to "高德地图",
        "com.baidu.BaiduMap" to "百度地图",
        "com.baidu.searchbox" to "百度",
        "com.tencent.mtt" to "QQ浏览器",
        "org.mozilla.firefox" to "Firefox",
        "com.sec.android.app.sbrowser" to "三星浏览器",
        "com.android.mms" to "信息",
        "com.android.contacts" to "通讯录",
        "com.miui.notes" to "便签",
        "com.huawei.notepad" to "备忘录",
        "com.coloros.note" to "便签",
        "com.vivo.notes" to "原子便签",
        "com.oppo.market" to "软件商店",
        "com.bbk.appstore" to "应用商店",
        "com.huawei.appmarket" to "华为应用市场",
        "com.xiaomi.market" to "小米应用商店",
        "com.github.android" to "GitHub",
        "org.telegram.messenger" to "Telegram",
        "org.thunderdog.challegram" to "Telegram X",
        "com.twitter.android" to "X (Twitter)",
        "com.instagram.android" to "Instagram",
        "com.facebook.katana" to "Facebook",
        "com.whatsapp" to "WhatsApp"
    )

    /**
     * 从 ClipData 或系统运行状态中解析剪贴板产生的来源包名
     */
    fun resolvePackageName(context: Context, clipData: ClipData?): String? {
        // 1. 尝试从 ClipData 描述信息的 extras 读取 (Xposed 模块 / 系统自带)
        if (clipData != null) {
            val desc = clipData.description
            if (desc != null) {
                val extras = desc.extras
                if (extras != null) {
                    val candidateKeys = listOf(
                        "source_package",
                        "sourcePackage",
                        "android.intent.extra.PACKAGE_NAME",
                        "calling_package",
                        "package_name",
                        "com.android.browser.application_id"
                    )
                    for (key in candidateKeys) {
                        val pkg = extras.getString(key)
                        if (!pkg.isNullOrBlank()) {
                            return pkg
                        }
                    }
                }
            }
        }

        // 2. 尝试从 UsageStatsManager 查询最近前台应用 (需有查看使用情况权限)
        val usagePkg = getRecentForegroundPackageFromUsage(context)
        if (!usagePkg.isNullOrBlank() && usagePkg != context.packageName) {
            return usagePkg
        }

        // 3. 尝试从 ActivityManager 查询前台进程
        val amPkg = getForegroundPackageFromActivityManager(context)
        if (!amPkg.isNullOrBlank() && amPkg != context.packageName) {
            return amPkg
        }

        return usagePkg ?: amPkg
    }

    /**
     * 根据包名解析友好的应用名称 (如 "com.tencent.mm" -> "微信")
     */
    fun resolveAppName(context: Context, packageName: String?): String? {
        if (packageName.isNullOrBlank()) return null

        // 命中内存缓存
        nameCache[packageName]?.let { return it }

        // 命中常用应用字典
        KNOWN_APPS[packageName]?.let {
            nameCache[packageName] = it
            return it
        }

        // 查询 PackageManager
        val pm = context.packageManager
        try {
            val appInfo = pm.getApplicationInfo(packageName, 0)
            val label = pm.getApplicationLabel(appInfo).toString()
            if (label.isNotBlank()) {
                nameCache[packageName] = label
                return label
            }
        } catch (_: Exception) {
        }

        // 格式化末尾包名
        val fallback = packageName.substringAfterLast('.').replaceFirstChar { it.uppercase() }
        nameCache[packageName] = fallback
        return fallback
    }

    /**
     * 根据包名获取应用图标并转为 Compose ImageBitmap (带内存缓存)
     */
    fun getAppIconBitmap(context: Context, packageName: String?): ImageBitmap? {
        if (packageName.isNullOrBlank()) return null
        iconCache[packageName]?.let { return it }

        try {
            val pm = context.packageManager
            val drawable = pm.getApplicationIcon(packageName)
            val bitmap = drawableToBitmap(drawable)
            if (bitmap != null) {
                val imgBitmap = bitmap.asImageBitmap()
                iconCache[packageName] = imgBitmap
                return imgBitmap
            }
        } catch (_: Exception) {
        }
        return null
    }

    private fun drawableToBitmap(drawable: Drawable): Bitmap? {
        if (drawable is BitmapDrawable && drawable.bitmap != null) {
            return drawable.bitmap
        }
        val width = if (drawable.intrinsicWidth > 0) drawable.intrinsicWidth.coerceAtMost(96) else 48
        val height = if (drawable.intrinsicHeight > 0) drawable.intrinsicHeight.coerceAtMost(96) else 48
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        drawable.setBounds(0, 0, canvas.width, canvas.height)
        drawable.draw(canvas)
        return bitmap
    }

    private fun getRecentForegroundPackageFromUsage(context: Context): String? {
        try {
            val usm = context.getSystemService(Context.USAGE_STATS_SERVICE) as? UsageStatsManager ?: return null
            val now = System.currentTimeMillis()
            val events = usm.queryEvents(now - 5000, now)
            val event = UsageEvents.Event()
            var lastPkg: String? = null
            while (events.hasNextEvent()) {
                events.getNextEvent(event)
                if (event.eventType == UsageEvents.Event.ACTIVITY_RESUMED ||
                    event.eventType == UsageEvents.Event.MOVE_TO_FOREGROUND) {
                    val pkg = event.packageName
                    if (pkg != null && pkg != context.packageName) {
                        lastPkg = pkg
                    }
                }
            }
            return lastPkg
        } catch (_: Exception) {
            return null
        }
    }

    private fun getForegroundPackageFromActivityManager(context: Context): String? {
        try {
            val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return null
            val processes = am.runningAppProcesses ?: return null
            for (p in processes) {
                if (p.importance == ActivityManager.RunningAppProcessInfo.IMPORTANCE_FOREGROUND &&
                    p.processName != context.packageName) {
                    return p.pkgList?.firstOrNull() ?: p.processName
                }
            }
        } catch (_: Exception) {
        }
        return null
    }
}
