# NexClip Android 客户端

NexClip Android 端是基于 Jetpack Compose 与 Miuix 设计风格打造的高性能剪贴板同步工具。

---

## 核心特性

- **现代设计语言**：遵循 HyperOS / Miuix 视觉规范，提供精致的分类胶囊与筛选卡片设计。
- **HyperOS 灵动超级岛**：原生集成 FocusNotification API，支持流光呼吸灯效与大小超级岛实时状态通知。
- **全方位后台监听方案**：
  - **Xposed / LSPosed 模式**：通过 Hook 系统剪贴板服务实现 100% 无感后台同步与防休眠。
  - **Shizuku 模式**：免 Root 通过 ADB 权限提权获取系统剪贴板监听能力。
  - **前台常驻服务**：适配各品牌系统的后台存活策略。
- **智能动作引擎 (SmartAction)**：快速识别链接、电话、验证码、IP、邮箱、快递单号并提供上下文快捷操作。
- **拼音与多维度筛选**：支持汉字全拼、首字母快速搜索，支持按设备、日期、类型进行复合筛选。
- **双向实时同步**：基于 SignalR 协议保持长连接，剪贴板变更毫秒级双向流转。

---

## 构建与开发

### 环境要求
- JDK 17 或更高版本
- Android Studio Ladybug (2024.2+) 或更高版本
- Android SDK Platform 35+

### 编译指令

```powershell
# 编译 Debug APK
.\gradlew.bat assembleDebug

# 编译 Release APK
.\gradlew.bat assembleRelease
```

---

## 独立分支

本项目已支持独立分支拉取与开发：
```bash
git clone -b android https://github.com/yixing233/nexclip.git
```
拉取后根目录即为 Android Studio 标准工程，可直接打开导入。
