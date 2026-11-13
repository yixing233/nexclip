package clip.yixing.sync.hook

import android.content.Context
import android.content.Intent
import android.provider.Telephony
import android.util.Log
import io.github.libxposed.api.XposedModule
import io.github.libxposed.api.XposedModuleInterface.ModuleLoadedParam
import io.github.libxposed.api.XposedModuleInterface.PackageLoadedParam

/**
 * 短信验证码 Hook:在 com.android.phone 进程内拦截 SMS_DELIVER。
 *
 * ## 为什么不用 SMS_RECEIVED 广播
 *
 * 实测(2026-11-13): HyperOS 上 SMS_RECEIVED 广播不投递给第三方接收器,
 * 即使 RECEIVE_SMS 已授权、appops allow、电池白名单、MIUI 管控未启用,
 * 广播入队后仍被队列吞掉(见 CHANGELOG 2026-11-13 节)。
 * 参考实现 tianma8023/XposedSmsCode 同样放弃了广播路径,改为 hook
 * com.android.internal.telephony.InboundSmsHandler.dispatchIntent ——
 * 这是短信进入系统的第一个分发点,先于 MIUI 的任何拦截。
 *
 * ## 数据流
 *
 *   com.android.phone (hook 进程)                NexClip 主进程
 *   ──────────────────────────                  ──────────────
 *   dispatchIntent(SMS_DELIVER)
 *     ↓ 提取 pdus → 组装正文
 *     sendBroadcast(ACTION_SMS_CODE_INTERCEPT,
 *       setPackage(MODULE_PACKAGE))  ────────→   SmsCodeReceiver
 *                                                  ↓
 *                                              SmsCodeHandler.handle()
 *                                              (去重/剪贴板/通知/自动填入)
 *
 * 广播用 setPackage 显式指向本模块,不经过系统解析。
 *
 * ## 隐私边界
 *
 * hook 进程只把正文放进广播 extra,不落盘;主进程 SmsCodeHandler 的
 * 原有隐私约定(不保存正文,只留验证码)继续适用。
 */
class SmsHandlerHook : XposedModule() {

    companion object {
        const val TAG = "NexClipSms"
        const val MODULE_PACKAGE = "clip.yixing.sync"

        /** 从 hook 进程发往主进程的验证码广播 action */
        const val ACTION_SMS_CODE_INTERCEPT = "clip.yixing.sync.action.SMS_CODE_INTERCEPT"
        const val EXTRA_BODY = "clip.yixing.sync.extra.BODY"
        const val EXTRA_SUBSCRIPTION = "clip.yixing.sync.extra.SUBSCRIPTION"

        private const val SMS_HANDLER_CLASS = "com.android.internal.telephony.InboundSmsHandler"
        private const val PHONE_PACKAGE = "com.android.phone"
    }

    /** phone 进程的 Context,从 dispatchIntent 的参数里捕获(存疑处见 [captureContext]) */
    @Volatile
    private var phoneContext: Context? = null

    override fun onModuleLoaded(param: ModuleLoadedParam) {
        log(Log.INFO, TAG, "module loaded in ${param.processName}")
    }

    override fun onPackageLoaded(param: PackageLoadedParam) {
        if (param.packageName != PHONE_PACKAGE) return
        log(Log.INFO, TAG, "phone process loading, hooking InboundSmsHandler")
        try {
            hookDispatchIntent(param.defaultClassLoader)
        } catch (t: Throwable) {
            log(Log.ERROR, TAG, "hook InboundSmsHandler failed", t)
        }
    }

    /**
     * Hook dispatchIntent(Intent, ...)。
     *
     * 该方法签名在 AOSP 各版本间有差异(有无 Bundle/UserHandle),参考实现用
     * "找 BroadcastReceiver 参数下标"的模糊匹配。我们不需要 receiver 下标
     * (只旁观,不阻断投递),所以只要找到同名方法全部 hook 即可。
     */
    private fun hookDispatchIntent(classLoader: ClassLoader) {
        val handlerClass = Class.forName(SMS_HANDLER_CLASS, false, classLoader)
        val targets = handlerClass.declaredMethods.filter { it.name == "dispatchIntent" }
        if (targets.isEmpty()) {
            log(Log.ERROR, TAG, "dispatchIntent not found on $SMS_HANDLER_CLASS")
            return
        }
        for (method in targets) {
            hook(method).intercept { chain ->
                try {
                    intercept(chain.args)
                } catch (t: Throwable) {
                    log(Log.WARN, TAG, "sms intercept failed: ${t.message}")
                }
                // 永远放行:我们只旁观提取验证码,不改变系统投递行为
                chain.proceed()
            }
            log(Log.INFO, TAG, "hooked ${method.name}(${method.parameterTypes.size} params)")
        }
    }

    /** 从 dispatchIntent 参数里拿 Intent,识别 SMS_DELIVER 并组装正文广播出去 */
    private fun intercept(args: List<Any?>) {
        val intent = args.filterIsInstance<Intent>().firstOrNull() ?: return
        if (intent.action != Telephony.Sms.Intents.SMS_DELIVER_ACTION) return

        val ctx = captureContext(args)
        if (ctx == null) {
            log(Log.WARN, TAG, "phone context not captured yet")
            return
        }

        val messages = runCatching {
            Telephony.Sms.Intents.getMessagesFromIntent(intent)
        }.getOrNull()
        if (messages.isNullOrEmpty()) {
            log(Log.WARN, TAG, "SMS_DELIVER but no messages parsed")
            return
        }
        val body = messages.joinToString(separator = "") {
            it.displayMessageBody ?: it.messageBody.orEmpty()
        }
        if (body.isBlank()) return

        // subscription id 需要 @Suppress 或隐藏 API,诊断价值不大,改从 intent 直接带
        val subscription = intent.getIntExtra("subscription", -1)

        val out = Intent(ACTION_SMS_CODE_INTERCEPT)
            .setPackage(MODULE_PACKAGE)
            .putExtra(EXTRA_BODY, body)
            .putExtra(EXTRA_SUBSCRIPTION, subscription)
        runCatching { ctx.sendBroadcast(out) }
            .onFailure { log(Log.WARN, TAG, "send intercept broadcast failed: ${it.message}") }
        log(Log.INFO, TAG, "sms body forwarded to module app (${body.length} chars)")
    }

    /**
     * dispatchIntent 的参数里带 Context(参考实现取 args[1])。
     * 版本间签名有差异,这里按类型模糊找第一个 Context 参数,并缓存复用。
     */
    private fun captureContext(args: List<Any?>): Context? {
        phoneContext?.let { return it }
        val ctx = args.filterIsInstance<Context>().firstOrNull() ?: return null
        phoneContext = ctx
        log(Log.INFO, TAG, "phone context captured from dispatchIntent args")
        return ctx
    }
}
