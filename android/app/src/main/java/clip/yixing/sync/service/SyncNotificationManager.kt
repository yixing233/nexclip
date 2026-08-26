package clip.yixing.sync.service

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.drawable.Icon
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import clip.yixing.sync.MainActivity
import clip.yixing.sync.R
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.smartaction.SmartAction
import clip.yixing.sync.smartaction.SmartActionEngine
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import com.xzakota.hyper.notification.focus.FocusNotification

/**
 * 集中管理 NexClip 的所有通知渠道与通知构造
 * 适配三套展示样式：
 * 1. STANDARD: 原生普通通知 (通知栏折叠/静默提示，无悬浮)
 * 2. ANDROID_LIVE: 安卓实时通知 (Live Activity / 官方 Promoted Ongoing 规范，悬浮且提供快捷操作)
 * 3. HYPEROS_ISLAND: 小米澎湃 OS 灵动焦点与超级岛 (打孔灵动胶囊 / 桌面流光 / 超级岛卡片)
 */
object SyncNotificationManager {

    const val CHANNEL_MONITOR = "clipboard_monitor"
    const val CHANNEL_LIVE = "clipboard_live_activity"
    const val CHANNEL_PUSH = "clipboard_push"
    const val CHANNEL_ISLAND = "clipboard_hyperos_island"

    const val NOTIFICATION_ID_FOREGROUND = 1001
    const val NOTIFICATION_ID_PUSH = 2001

    /**
     * 初始化全部通知渠道
     */
    fun initChannels(context: Context) = createChannels(context)

    fun createChannels(context: Context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = context.getSystemService(NotificationManager::class.java) ?: return

            val monitorChannel = NotificationChannel(
                CHANNEL_MONITOR,
                "监听前台服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "保持后台剪贴板监听稳定运行所必需的静默前台通知"
                setShowBadge(false)
                enableLights(false)
                enableVibration(false)
                setSound(null, null)
            }

            val liveChannel = NotificationChannel(
                CHANNEL_LIVE,
                "实时动态与活动",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "Android 14+ 实时活动与剪贴板最新状态快捷悬浮展示"
                setShowBadge(true)
                enableLights(true)
                lightColor = Color.BLUE
                enableVibration(false)
            }

            val pushChannel = NotificationChannel(
                CHANNEL_PUSH,
                "新剪贴板推送",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "接收到来自其他设备或本地新捕获的剪贴板通知"
                setShowBadge(true)
                enableLights(true)
                enableVibration(false)
            }

            val islandChannel = NotificationChannel(
                CHANNEL_ISLAND,
                "HyperOS 灵动焦点通知",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "适配小米澎湃 OS 超级岛与状态栏灵动胶囊通知"
                setShowBadge(true)
            }

            nm.createNotificationChannels(listOf(monitorChannel, liveChannel, pushChannel, islandChannel))
        }
    }

    /**
     * 构建前台常驻服务通知 (常规通知标准样式，保证稳定常驻通知栏)
     */
    fun buildForegroundNotification(
        context: Context,
        latestClip: CapturedClip?,
        isServerConnected: Boolean,
        style: NotificationStyle
    ): Notification {
        val openAppPi = PendingIntent.getActivity(
            context,
            0,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val connStatus = if (isServerConnected) "已连接服务器 · 监听中" else "未连接服务器 · 本地监听中"
        val devName = latestClip?.sourceDevice
        val isRemote = devName != null && devName != "本机"
        val devPrefix = if (isRemote) "[来自 $devName] " else ""

        val preview = if (latestClip != null) {
            if (latestClip.isImage) "最新记录: ${devPrefix}[图片]" else "最新记录: ${devPrefix}${latestClip.text.replace("\n", " ").take(60)}"
        } else connStatus

        val subText = if (isRemote) "来自 $devName" else if (isServerConnected) "已连接" else "未连接"

        return NotificationCompat.Builder(context, CHANNEL_MONITOR)
            .setSmallIcon(R.drawable.ic_notification_nc)
            .setContentTitle("NexClip 剪贴板监听")
            .setContentText(preview)
            .setSubText(subText)
            .setContentIntent(openAppPi)
            .setOngoing(true)
            .setShowWhen(latestClip != null)
            .setWhen(latestClip?.time ?: System.currentTimeMillis())
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .build()
    }

    /**
     * 构建安卓原生实时活动通知 (Live Activity / Ongoing Notification)
     */
    private fun buildAndroidLiveNotification(
        context: Context,
        latestClip: CapturedClip?,
        isServerConnected: Boolean,
        openAppPi: PendingIntent
    ): Notification {
        val isImage = latestClip?.isImage == true
        val smartActions = if (!isImage && latestClip != null) SmartActionEngine.detectActions(context, latestClip.text) else emptyList()
        val topSmart = smartActions.firstOrNull()

        val preview = if (isImage) "[图片]" else (latestClip?.text?.replace("\n", " ")?.take(100) ?: "等待剪贴板变化 · 实时同步中")
        val title = if (latestClip != null) {
            if (isImage) "剪贴板已记录 (图片)" else "剪贴板已记录 (${latestClip.text.length} 字符)"
        } else "NexClip 实时同步中"
        val subText = if (isServerConnected) "云端同步在线" else "本地监听中"
        val tagText = if (latestClip != null) {
            if (isImage) "图片" else (resolveSmartActionTag(topSmart) ?: "${latestClip.text.length}字")
        } else "同步中"

        val builder = NotificationCompat.Builder(context, CHANNEL_LIVE)
            .setSmallIcon(R.drawable.ic_notification_nc)
            .setContentTitle(title)
            .setContentText(preview)
            .setSubText(subText)
            .setContentIntent(openAppPi)
            .setOngoing(true)
            .setShowWhen(true)
            .setWhen(latestClip?.time ?: System.currentTimeMillis())
            .setPriority(NotificationCompat.PRIORITY_MAX)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .setColorized(false)

        // 注入 Android 16+ 实时更新 (Live Update / Promoted Ongoing) 官方规范属性
        builder.extras.apply {
            putBoolean("android.requestPromotedOngoing", true)
            putBoolean("android.isLiveActivity", true)
            putBoolean("android.ongoingActivity", true)
            putString("android.substName", "NexClip")
            putBoolean("androidx.core.app.extra.COMPAT_TEMPLATE", true)
            putString("android.shortCriticalText", tagText)
        }

        // 大卡片展开视图 (标准 BigTextStyle, 无自定义 RemoteViews, 符合官方实时更新要求)
        if (latestClip != null) {
            val bigStyle = NotificationCompat.BigTextStyle()
                .setBigContentTitle(if (isImage) "最新剪贴板图片" else "最新剪贴板内容")
                .bigText(if (isImage) "[图片]" else latestClip.text.take(600))
                .setSummaryText(if (isServerConnected) "云端已连接" else "本地监听中")
            builder.setStyle(bigStyle)

            addQuickActions(context, builder, latestClip.text, smartActions)
        }

        return builder.build()
    }

    /**
     * 构建 HyperOS 灵动超级岛通知 (用于新剪贴板触发时的浮出卡片与打孔胶囊)
     */
    private fun buildHyperOsIslandNotification(
        context: Context,
        clip: CapturedClip,
        deviceLabel: String,
        isPush: Boolean,
        openAppPi: PendingIntent,
        smartActions: List<SmartAction>
    ): Notification {
        val isImage = clip.isImage
        val topSmart = smartActions.firstOrNull()

        val title = if (isImage) {
            if (isPush) "收到来自 $deviceLabel 的图片" else "已捕获图片"
        } else if (topSmart != null) {
            if (isPush) "来自 $deviceLabel · ${topSmart.title}" else topSmart.title
        } else {
            if (isPush) "收到来自 $deviceLabel 的内容" else "已捕获剪贴板"
        }

        val preview = if (isImage) {
            "[图片]"
        } else if (topSmart != null) {
            topSmart.summary ?: clip.text.replace("\n", " ").take(80)
        } else {
            clip.text.replace("\n", " ").take(80)
        }

        val tagText = if (isImage) "图片" else (resolveSmartActionTag(topSmart) ?: "直达")

        val builder = NotificationCompat.Builder(context, CHANNEL_ISLAND)
            .setSmallIcon(R.drawable.ic_notification_nc)
            .setContentTitle(title)
            .setContentText(preview)
            .setSubText(deviceLabel)
            .setStyle(NotificationCompat.BigTextStyle().bigText(if (isImage) "[图片]" else clip.text))
            .setContentIntent(openAppPi)
            .setAutoCancel(true)
            .setOngoing(false)
            .setShowWhen(true)
            .setWhen(clip.time)
            .setPriority(NotificationCompat.PRIORITY_MAX)
            .setCategory(NotificationCompat.CATEGORY_EVENT)
            .setVibrate(longArrayOf(0, 30))

        addQuickActions(context, builder, clip.text, smartActions)

        // 使用 FocusNotification.buildV3 构建原生 HyperOS 灵动胶囊 / 超级岛
        try {
            val lightLogoIcon = Icon.createWithResource(context, R.drawable.ic_notification_nc).setTint(Color.BLACK)
            val darkLogoIcon = Icon.createWithResource(context, R.drawable.ic_notification_nc).setTint(Color.WHITE)

            val islandExtras = FocusNotification.buildV3 {
                val lightLogoKey = createPicture("key_logo_light", lightLogoIcon)
                val darkLogoKey = createPicture("key_logo_dark", darkLogoIcon)

                val enableGlow = SyncSettings.isHyperOsOuterGlow(context)
                val glowColor = SyncSettings.hyperOsGlowColor(context)

                isShowNotification = true
                showSmallIcon = true
                islandFirstFloat = true
                enableFloat = true
                updatable = true
                ticker = title
                tickerPic = lightLogoKey
                if (enableGlow) {
                    outEffectSrc = "outer_glow"
                    outEffectColor = glowColor
                }
                business = "copytoservice"
                sequence = System.currentTimeMillis()

                // 小米超级岛 (打孔胶囊与大岛展开态)
                island {
                    islandProperty = 2
                    islandPriority = 2
                    business = "copytoservice"
                    expandedTime = 3
                    val timeoutSec = SyncSettings.hyperOsIslandTimeout(context)
                    if (timeoutSec < 3600) {
                        islandTimeout = timeoutSec
                    }

                    bigIslandArea {
                        imageTextInfoLeft {
                            type = 1
                            picInfo {
                                type = 1
                                pic = darkLogoKey
                            }
                        }
                        imageTextInfoRight {
                            type = 3
                            textInfo {
                                this.title = tagText
                            }
                        }
                    }

                    smallIslandArea {
                        picInfo {
                            type = 1
                            pic = darkLogoKey
                        }
                    }
                }

                // 焦点通知大卡片
                baseInfo {
                    type = 2
                    this.title = title
                    content = preview.take(50).ifEmpty { " " }
                }

                picInfo {
                    type = 1
                    pic = lightLogoKey
                    picDark = darkLogoKey
                }

                // 快捷操作按钮 (优先注入智能动作)
                val copyIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                    action = NotificationActionReceiver.ACTION_COPY_LATEST
                    putExtra(NotificationActionReceiver.EXTRA_CLIP_TEXT, clip.text)
                }
                val copyPi = PendingIntent.getBroadcast(
                    context,
                    201,
                    copyIntent,
                    PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
                )
                val favIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                    action = NotificationActionReceiver.ACTION_FAVORITE_LATEST
                }
                val favPi = PendingIntent.getBroadcast(
                    context,
                    202,
                    favIntent,
                    PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
                )

                textButton {
                    if (topSmart != null) {
                        val smartPi = topSmart.createPendingIntent(context, 200)
                        if (smartPi != null) {
                            addActionInfo {
                                val nativeAction = Notification.Action.Builder(
                                    Icon.createWithResource(context, R.drawable.ic_notification_nc),
                                    topSmart.title,
                                    smartPi
                                ).build()
                                action = createAction("miui_action_smart", nativeAction)
                                actionTitle = topSmart.title.take(12)
                                val btnColor = topSmart.hexColor ?: "#006EFF"
                                actionBgColor = btnColor
                                actionBgColorDark = btnColor
                                actionTitleColor = "#FFFFFF"
                                actionTitleColorDark = "#FFFFFF"
                            }
                        }
                        val isTopCopy = topSmart.id.startsWith("code_") || topSmart.id.startsWith("color_")
                        if (isTopCopy) {
                            addActionInfo {
                                val nativeAction = Notification.Action.Builder(
                                    Icon.createWithResource(context, R.drawable.ic_notification_nc),
                                    "收藏",
                                    favPi
                                ).build()
                                action = createAction("miui_action_fav", nativeAction)
                                actionTitle = "收藏"
                            }
                        } else {
                            addActionInfo {
                                val nativeAction = Notification.Action.Builder(
                                    Icon.createWithResource(context, R.drawable.ic_notification_nc),
                                    "复制",
                                    copyPi
                                ).build()
                                action = createAction("miui_action_copy", nativeAction)
                                actionTitle = "复制"
                            }
                        }
                    } else {
                        addActionInfo {
                            val nativeAction = Notification.Action.Builder(
                                Icon.createWithResource(context, R.drawable.ic_notification_nc),
                                "复制",
                                copyPi
                            ).build()
                            action = createAction("miui_action_copy", nativeAction)
                            actionTitle = "复制"
                            actionBgColor = "#006EFF"
                            actionBgColorDark = "#006EFF"
                            actionTitleColor = "#FFFFFF"
                            actionTitleColorDark = "#FFFFFF"
                        }
                        addActionInfo {
                            val nativeAction = Notification.Action.Builder(
                                Icon.createWithResource(context, R.drawable.ic_notification_nc),
                                "收藏",
                                favPi
                            ).build()
                            action = createAction("miui_action_fav", nativeAction)
                            actionTitle = "收藏"
                        }
                    }
                }
            }

            builder.addExtras(islandExtras)
        } catch (e: Throwable) {
            android.util.Log.e("SyncNotification", "buildHyperOsIslandNotification error", e)
        }

        return builder.build()
    }

    /**
     * 发送新剪贴板推送 / 捕获通知 (弹出实时胶囊 / 超级岛)
     */
    fun notifyNewClip(
        context: Context,
        clip: CapturedClip,
        sourceDevice: String?,
        isPush: Boolean
    ) {
        if (!SyncSettings.notificationEnabled(context)) {
            android.util.Log.w("SyncNotification", "notifyNewClip: notification is disabled in settings")
            return
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            android.util.Log.w("SyncNotification", "notifyNewClip: POST_NOTIFICATIONS permission not granted")
            return
        }

        val style = SyncSettings.notificationStyle(context)
        android.util.Log.i("SyncNotification", "notifyNewClip: style=$style, isPush=$isPush, textLen=${clip.text.length}")
        val openAppPi = PendingIntent.getActivity(
            context,
            (System.currentTimeMillis() % 10000).toInt(),
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val isImage = clip.isImage
        val deviceLabel = sourceDevice ?: "其他设备"
        val smartActions = if (!isImage) SmartActionEngine.detectActions(context, clip.text) else emptyList()
        val topSmart = smartActions.firstOrNull()

        // 核心规范：超级岛 / 实时通知只展示「智能动作与应用直达」或「图片」
        // 纯常规文本（未识别出智能动作）直接从超级岛/实时横幅中剥离，不弹岛打扰，统一静默存入历史并更新在下拉常驻通知中
        if (style == NotificationStyle.HYPEROS_ISLAND || style == NotificationStyle.ANDROID_LIVE) {
            if (!isImage && smartActions.isEmpty()) {
                android.util.Log.i("SyncNotification", "notifyNewClip: skipped island/live banner for plain text without smart actions")
                return
            }
        }

        val title = if (isPush) {
            if (isImage) "收到来自 $deviceLabel 的图片" else "收到来自 $deviceLabel 的内容"
        } else {
            if (isImage) "已捕获新图片" else "已捕获新剪贴板"
        }
        val preview = if (isImage) "[图片]" else clip.text.replace("\n", " ").take(100)
        val tagText = if (isImage) "图片" else (resolveSmartActionTag(topSmart) ?: "直达")

        when (style) {
            NotificationStyle.STANDARD -> {
                val builder = NotificationCompat.Builder(context, CHANNEL_PUSH)
                    .setSmallIcon(R.drawable.ic_notification_nc)
                    .setContentTitle(title)
                    .setContentText(preview)
                    .setStyle(NotificationCompat.BigTextStyle().bigText(if (isImage) "[图片]" else clip.text))
                    .setContentIntent(openAppPi)
                    .setAutoCancel(true)
                    .setShowWhen(true)
                    .setPriority(NotificationCompat.PRIORITY_HIGH)

                addQuickActions(context, builder, clip.text, smartActions)
                postNotification(context, NOTIFICATION_ID_PUSH, builder.build())
            }

            NotificationStyle.ANDROID_LIVE -> {
                val liveTitle = if (isImage) {
                    if (isPush) "收到来自 $deviceLabel 的图片" else "已捕获图片"
                } else if (topSmart != null) {
                    if (isPush) "来自 $deviceLabel · ${topSmart.title}" else topSmart.title
                } else {
                    title
                }
                val livePreview = if (isImage) "[图片]" else (topSmart?.summary ?: preview)

                val builder = NotificationCompat.Builder(context, CHANNEL_LIVE)
                    .setSmallIcon(R.drawable.ic_notification_nc)
                    .setContentTitle(liveTitle)
                    .setContentText(livePreview)
                    .setSubText(deviceLabel)
                    .setStyle(
                        NotificationCompat.BigTextStyle()
                            .setBigContentTitle(liveTitle)
                            .bigText(if (isImage) "[图片]" else (topSmart?.summary ?: clip.text))
                            .setSummaryText(deviceLabel)
                    )
                    .setContentIntent(openAppPi)
                    .setAutoCancel(true)
                    .setOngoing(false)
                    .setShowWhen(true)
                    .setWhen(clip.time)
                    .setPriority(NotificationCompat.PRIORITY_MAX)
                    .setCategory(NotificationCompat.CATEGORY_EVENT)
                    .setVibrate(longArrayOf(0, 30))
                    .setColorized(false)

                builder.extras.apply {
                    putBoolean("android.requestPromotedOngoing", true)
                    putBoolean("android.isLiveActivity", true)
                    putString("android.substName", "NexClip")
                    putBoolean("androidx.core.app.extra.COMPAT_TEMPLATE", true)
                    putString("android.shortCriticalText", tagText)
                }

                addQuickActions(context, builder, clip.text, smartActions)
                postNotification(context, NOTIFICATION_ID_PUSH, builder.build())
            }

            NotificationStyle.HYPEROS_ISLAND -> {
                val islandNotification = buildHyperOsIslandNotification(
                    context = context,
                    clip = clip,
                    deviceLabel = deviceLabel,
                    isPush = isPush,
                    openAppPi = openAppPi,
                    smartActions = smartActions
                )
                postNotification(context, NOTIFICATION_ID_PUSH, islandNotification)
            }
        }
    }

    private fun postNotification(context: Context, id: Int, notification: Notification) {
        runCatching {
            val nm = context.getSystemService(NotificationManager::class.java)
            if (id == NOTIFICATION_ID_PUSH) {
                // 关键：在发布新推送/超级岛前先 cancel 旧通知，确保 HyperOS 始终按全新通知触发灵动浮出动画与超级岛
                nm.cancel(id)
            }
            nm.notify(id, notification)
            android.util.Log.i("SyncNotification", "postNotification success: id=$id, channel=${notification.channelId}")
        }.onFailure {
            android.util.Log.e("SyncNotification", "postNotification failed: id=$id", it)
        }
    }

    /**
     * 根据智能动作提取灵动胶囊精简指示词
     */
    private fun resolveSmartActionTag(action: SmartAction?): String? {
        if (action == null) return null
        return when {
            action.id.startsWith("code_") -> "验证码"
            action.id.startsWith("bili_") -> "B站"
            action.id.startsWith("tb_") -> "淘宝"
            action.id.startsWith("jd_") -> "京东"
            action.id.startsWith("douyin_") -> "抖音"
            action.id.startsWith("xhs_") -> "小红书"
            action.id.startsWith("url_") -> "链接"
            action.id.startsWith("express_") -> "快递"
            action.id.startsWith("phone_") -> "电话"
            action.id.startsWith("email_") -> "邮件"
            action.id.startsWith("color_") -> "色值"
            action.id.startsWith("geo_") -> "地图"
            else -> action.title.take(4)
        }
    }

    /**
     * 为通知添加快捷操作按钮 (支持注入智能动作 + 复制、收藏)
     */
    private fun addQuickActions(
        context: Context,
        builder: NotificationCompat.Builder,
        text: String,
        smartActions: List<SmartAction> = emptyList()
    ) {
        // 1. 优先注入前 1~2 个智能识别动作
        smartActions.take(2).forEachIndexed { idx, action ->
            val smartPi = action.createPendingIntent(context, 300 + idx)
            if (smartPi != null) {
                builder.addAction(0, action.title, smartPi)
            }
        }

        // 2. 补充标准快捷操作
        val copyIntent = Intent(context, NotificationActionReceiver::class.java).apply {
            action = NotificationActionReceiver.ACTION_COPY_LATEST
            putExtra(NotificationActionReceiver.EXTRA_CLIP_TEXT, text)
        }
        val copyPi = PendingIntent.getBroadcast(
            context,
            101,
            copyIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        builder.addAction(0, "复制", copyPi)

        val favIntent = Intent(context, NotificationActionReceiver::class.java).apply {
            action = NotificationActionReceiver.ACTION_FAVORITE_LATEST
        }
        val favPi = PendingIntent.getBroadcast(
            context,
            102,
            favIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        builder.addAction(0, "收藏", favPi)
    }
}
