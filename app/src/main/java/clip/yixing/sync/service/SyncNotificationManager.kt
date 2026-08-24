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
import android.os.Bundle
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import clip.yixing.sync.MainActivity
import clip.yixing.sync.R
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import com.xzakota.hyper.notification.focus.FocusNotification

/**
 * 集中管理应用通知构建与分发:
 * 深度实现:
 * 1. 普通通知 (Standard Notification)
 * 2. 安卓实时活动通知 (Android Live Notification / Ongoing Activity)
 * 3. 小米澎湃OS (HyperOS) 超级岛 / 焦点通知 (Focus Notification)
 */
object SyncNotificationManager {

    const val CHANNEL_MONITOR = "clipboard_monitor"
    const val CHANNEL_LIVE = "clipboard_live_activity"
    const val CHANNEL_PUSH = "clipboard_sync_push"
    const val CHANNEL_ISLAND = "clipboard_sync_island"

    const val NOTIFICATION_ID_FOREGROUND = 1
    const val NOTIFICATION_ID_PUSH = 1001

    /** 初始化所有通知渠道 */
    fun initChannels(context: Context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = context.getSystemService(NotificationManager::class.java) ?: return

            // 1. 普通前台常驻服务渠道 (低打扰)
            val monitorChannel = NotificationChannel(
                CHANNEL_MONITOR,
                "剪贴板基础监听服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "保持后台剪贴板监听的常驻基础服务通知"
                setShowBadge(false)
            }

            // 2. 安卓实时活动通知渠道 (高优先级常驻, 静音无振动, 支持置顶实时卡片)
            val liveChannel = NotificationChannel(
                CHANNEL_LIVE,
                "剪贴板实时活动通知",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "实时卡片展示最新剪贴板内容与操作按钮"
                setSound(null, null)
                enableVibration(false)
                enableLights(false)
                setShowBadge(true)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    setAllowBubbles(true)
                }
            }

            // 3. 消息推送提示渠道
            val pushChannel = NotificationChannel(
                CHANNEL_PUSH,
                "剪贴板新内容提示",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "从其他设备同步到本机时的即时提示"
                setShowBadge(true)
            }

            // 4. 小米澎湃OS 超级岛 / 焦点通知渠道 (HIGH 级别才能触发灵动胶囊动画)
            val islandChannel = NotificationChannel(
                CHANNEL_ISLAND,
                "小米超级岛焦点通知",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "在小米澎湃OS状态栏/打孔屏超级岛展示实时胶囊与卡片"
                setSound(null, null)
                enableVibration(false)
                setShowBadge(true)
            }

            nm.createNotificationChannels(listOf(monitorChannel, liveChannel, pushChannel, islandChannel))
        }
    }

    /**
     * 构建前台常驻服务通知 (根据当前设置的样式动态生成)
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
        return NotificationCompat.Builder(context, CHANNEL_MONITOR)
            .setSmallIcon(R.drawable.ic_notification_nc)
            .setContentTitle("NexClip 监听中")
            .setContentText(connStatus)
            .setContentIntent(openAppPi)
            .setOngoing(true)
            .setShowWhen(false)
            .setPriority(NotificationCompat.PRIORITY_LOW)
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
        val preview = latestClip?.text?.replace("\n", " ")?.take(100) ?: "等待剪贴板变化 · 实时同步中"
        val title = if (latestClip != null) "剪贴板已记录 (${latestClip.text.length} 字符)" else "NexClip 实时同步中"
        val subText = if (isServerConnected) "云端同步在线" else "本地监听中"

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
            putString("android.shortCriticalText", if (latestClip != null) "${latestClip.text.length}字" else "同步中")
        }

        // 大卡片展开视图 (标准 BigTextStyle, 无自定义 RemoteViews, 符合官方实时更新要求)
        if (latestClip != null) {
            val bigStyle = NotificationCompat.BigTextStyle()
                .setBigContentTitle("最新剪贴板内容")
                .bigText(latestClip.text.take(600))
                .setSummaryText(if (isServerConnected) "云端已连接" else "本地监听中")
            builder.setStyle(bigStyle)

            addQuickActions(context, builder, latestClip.text)
        }

        return builder.build()
    }

    /**
     * 构建 HyperOS 超级岛前台常驻通知
     */
    private fun buildHyperOsIslandForegroundNotification(
        context: Context,
        latestClip: CapturedClip?,
        isServerConnected: Boolean,
        openAppPi: PendingIntent
    ): Notification {
        val preview = latestClip?.text?.replace("\n", " ")?.take(80) ?: "剪贴板实时监听中"
        val title = if (latestClip != null) "剪贴板最新记录" else "NexClip"
        val status = if (isServerConnected) "已连接" else "监听中"

        val builder = NotificationCompat.Builder(context, CHANNEL_ISLAND)
            .setSmallIcon(R.drawable.ic_notification_nc)
            .setContentTitle(title)
            .setContentText(preview)
            .setSubText(status)
            .setContentIntent(openAppPi)
            .setOngoing(true)
            .setShowWhen(true)
            .setWhen(latestClip?.time ?: System.currentTimeMillis())
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)

        if (latestClip != null) {
            builder.setStyle(NotificationCompat.BigTextStyle().bigText(latestClip.text.take(400)))
            addQuickActions(context, builder, latestClip.text)
        }

        // 1. 使用 FocusNotification.buildV3 构建原生 HyperOS 超级岛 / 焦点通知
        try {
            val lightLogoIcon = Icon.createWithResource(context, R.drawable.ic_notification_nc).setTint(Color.BLACK)
            val darkLogoIcon = Icon.createWithResource(context, R.drawable.ic_notification_nc).setTint(Color.WHITE)

            val islandExtras = FocusNotification.buildV3 {
                val lightLogoKey = createPicture("key_logo_light", lightLogoIcon)
                val darkLogoKey = createPicture("key_logo_dark", darkLogoIcon)

                val enableGlow = SyncSettings.isHyperOsOuterGlow(context)
                val glowColor = SyncSettings.hyperOsGlowColor(context)

                islandFirstFloat = false
                enableFloat = false
                updatable = true
                ticker = title
                tickerPic = lightLogoKey
                if (enableGlow) {
                    outEffectSrc = "outer_glow"
                    outEffectColor = glowColor
                }

                // 小米超级岛 (打孔胶囊与大岛展开态)
                island {
                    islandProperty = 1

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
                                this.title = if (latestClip != null) "${latestClip.text.length}字" else "0字"
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

                // 快捷操作按钮
                if (latestClip != null) {
                    val copyIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                        action = NotificationActionReceiver.ACTION_COPY_LATEST
                        putExtra(NotificationActionReceiver.EXTRA_CLIP_TEXT, latestClip.text)
                    }
                    val copyPi = PendingIntent.getBroadcast(
                        context,
                        101,
                        copyIntent,
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
                    )
                    val favIntent = Intent(context, NotificationActionReceiver::class.java).apply {
                        action = NotificationActionReceiver.ACTION_FAVORITE_LATEST
                    }
                    val favPi = PendingIntent.getBroadcast(
                        context,
                        102,
                        favIntent,
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
                    )

                    textButton {
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
            android.util.Log.e("SyncNotification", "buildHyperOsIslandForegroundNotification error", e)
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
            return
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }

        val style = SyncSettings.notificationStyle(context)
        val openAppPi = PendingIntent.getActivity(
            context,
            (System.currentTimeMillis() % 10000).toInt(),
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val deviceLabel = sourceDevice ?: "其他设备"
        val title = if (isPush) "收到来自 $deviceLabel 的内容" else "已捕获新剪贴板"
        val preview = clip.text.replace("\n", " ").take(100)

        when (style) {
            NotificationStyle.STANDARD -> {
                val builder = NotificationCompat.Builder(context, CHANNEL_PUSH)
                    .setSmallIcon(R.drawable.ic_notification_nc)
                    .setContentTitle(title)
                    .setContentText(preview)
                    .setStyle(NotificationCompat.BigTextStyle().bigText(clip.text))
                    .setContentIntent(openAppPi)
                    .setAutoCancel(true)
                    .setShowWhen(true)
                    .setPriority(NotificationCompat.PRIORITY_DEFAULT)

                postNotification(context, NOTIFICATION_ID_PUSH, builder.build())
            }

            NotificationStyle.ANDROID_LIVE -> {
                val builder = NotificationCompat.Builder(context, CHANNEL_LIVE)
                    .setSmallIcon(R.drawable.ic_notification_nc)
                    .setContentTitle(title)
                    .setContentText(preview)
                    .setSubText(deviceLabel)
                    .setStyle(
                        NotificationCompat.BigTextStyle()
                            .setBigContentTitle(title)
                            .bigText(clip.text)
                            .setSummaryText(deviceLabel)
                    )
                    .setContentIntent(openAppPi)
                    .setOngoing(true) // 官方硬性要求: 实时更新必须设置 ongoing
                    .setShowWhen(true)
                    .setWhen(clip.time)
                    .setPriority(NotificationCompat.PRIORITY_MAX)
                    .setCategory(NotificationCompat.CATEGORY_STATUS)
                    .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
                    .setColorized(false)
                    .setTimeoutAfter(45_000) // 45秒后自动静默退出实时展示

                builder.extras.apply {
                    putBoolean("android.requestPromotedOngoing", true)
                    putBoolean("android.isLiveActivity", true)
                    putBoolean("android.ongoingActivity", true)
                    putString("android.substName", "NexClip")
                    putBoolean("androidx.core.app.extra.COMPAT_TEMPLATE", true)
                    putString("android.shortCriticalText", "${clip.text.length}字")
                }

                addQuickActions(context, builder, clip.text)
                postNotification(context, NOTIFICATION_ID_PUSH, builder.build())
            }

            NotificationStyle.HYPEROS_ISLAND -> {
                val builder = NotificationCompat.Builder(context, CHANNEL_ISLAND)
                    .setSmallIcon(R.drawable.ic_notification_nc)
                    .setContentTitle(title)
                    .setContentText(preview)
                    .setSubText(deviceLabel)
                    .setStyle(NotificationCompat.BigTextStyle().bigText(clip.text))
                    .setContentIntent(openAppPi)
                    .setOngoing(false)
                    .setShowWhen(true)
                    .setWhen(clip.time)
                    .setPriority(NotificationCompat.PRIORITY_HIGH)
                    .setCategory(NotificationCompat.CATEGORY_STATUS)
                    .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
                    .setTimeoutAfter(30_000) // 30秒后小岛恢复

                addQuickActions(context, builder, clip.text)

                // 1. 使用 FocusNotification.buildV3 构建原生 HyperOS 灵动胶囊 / 超级岛
                try {
                    val lightLogoIcon = Icon.createWithResource(context, R.drawable.ic_notification_nc).setTint(Color.BLACK)
                    val darkLogoIcon = Icon.createWithResource(context, R.drawable.ic_notification_nc).setTint(Color.WHITE)

                    val islandExtras = FocusNotification.buildV3 {
                        val lightLogoKey = createPicture("key_logo_light", lightLogoIcon)
                        val darkLogoKey = createPicture("key_logo_dark", darkLogoIcon)

                        val enableGlow = SyncSettings.isHyperOsOuterGlow(context)
                        val glowColor = SyncSettings.hyperOsGlowColor(context)

                        islandFirstFloat = true   // 触发顶部灵动胶囊浮出与流光动画
                        enableFloat = true
                        updatable = true
                        ticker = title
                        tickerPic = lightLogoKey
                        if (enableGlow) {
                            outEffectSrc = "outer_glow"
                            outEffectColor = glowColor
                        }

                        // 小米超级岛 (打孔胶囊与大岛展开态)
                        island {
                            islandProperty = 1

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
                                        this.title = "${clip.text.length}字"
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

                        // 快捷操作按钮
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

                    builder.addExtras(islandExtras)
                } catch (e: Throwable) {
                    android.util.Log.e("SyncNotification", "notifyNewClip hyperos island error", e)
                }

                postNotification(context, NOTIFICATION_ID_PUSH, builder.build())
            }
        }
    }

    private fun postNotification(context: Context, id: Int, notification: Notification) {
        runCatching {
            val nm = context.getSystemService(NotificationManager::class.java)
            nm.notify(id, notification)
        }
    }

    /**
     * 为通知添加快捷操作按钮 (复制、收藏)
     */
    private fun addQuickActions(context: Context, builder: NotificationCompat.Builder, text: String) {
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
