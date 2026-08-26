# libxposed 模块入口类与 service binder
-dontwarn io.github.libxposed.annotation.**
-adaptresourcefilecontents META-INF/xposed/java_init.list
-keep class clip.yixing.sync.hook.ClipboardHook { *; }
-keep class io.github.libxposed.** { *; }
-keep,allowoptimization,allowobfuscation public class * extends io.github.libxposed.api.XposedModule {
    public <init>();
}

# Shizuku API
-keep class rikka.shizuku.** { *; }
-dontwarn rikka.shizuku.**

# SignalR & 网络数据模型 (反射及 JSON 序列化需要保留字段)
-keep class com.microsoft.signalr.** { *; }
-dontwarn com.microsoft.signalr.**
-keep class clip.yixing.sync.model.** { *; }
-keepclassmembers class clip.yixing.sync.model.** { *; }
-keep class clip.yixing.sync.smartaction.** { *; }
-keepclassmembers class clip.yixing.sync.smartaction.** { *; }
-keep class clip.yixing.sync.util.UpdateInfo { *; }

# Miuix UI & Compose
-keep class top.yukonga.miuix.kmp.** { *; }
-dontwarn top.yukonga.miuix.kmp.**

# HyperOS 灵动焦点通知 (FocusNotification)
-keep class com.xzakota.hyper.notification.** { *; }
-dontwarn com.xzakota.hyper.notification.**

# SLF4J / OkHttp / Coroutines
-dontwarn org.slf4j.**
-dontwarn okhttp3.**
-dontwarn kotlinx.coroutines.**
-keepattributes *Annotation*,Signature,InnerClasses,EnclosingMethod
