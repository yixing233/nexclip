# NexClip v20260825.01 - Windows & Android 双端首发 (Initial Release)

NexClip 是一套专为多端设备打造的现代化、轻量高效跨平台剪贴板同步与局域网文件流转系统。本次为 NexClip 的**首个正式发布版本**，专注于带来 **Windows 桌面端** 与 **Android 移动端** 的极致互联体验。

---

## 核心亮点与产品优势

- **双端无缝互联**：深度适配 Windows 10/11 (WinUI 3 原生界面) 与 Android (HyperOS / Miuix 视觉风格)。
- **毫秒级实时流转**：剪贴板变更双向无感实时推送，支持多设备在线状态感知与防回环同步。
- **极致轻量安装包**：
  - Windows 安装包仅 **8.96 MB**；
  - Android 安装包仅 **15.28 MB**。
- **开箱即用与极低资源**：极速冷启动，低内存占用，常驻后台省电无感。

---

## 客户端功能详解

### 1. Windows 桌面端 (WinUI 3 / .NET 9)
- **原生 Fluent 视觉**：基于 WinUI 3 原生打造，采用 Windows 11 Mica 背景材质，自适应深色/浅色主题。
- **托盘与全局热键**：支持自定义全局快捷键瞬间唤出历史浮窗，托盘常驻支持状态感知与快速管理。
- **智能动作引擎 (Smart Action)**：自动识别复制内容中的链接、短信验证码、手机号、IP 地址、邮箱及快递单号，提供一键打开、复制、拨打或查询。
- **来源应用感知**：自动探测并展示复制内容的源应用程序名称及高清图标。
- **多模态流转**：
  - 纯文本与多行代码高亮；
  - 高清图片缩略预览与一键快速保存；
  - 全屏文件拖拽覆盖层，支持将外部文件与截图一键直推流转。
- **拼音检索**：内置拼音全拼及首字母模糊搜索，毫秒级定位海量历史条目。

### 2. Android 移动端 (Jetpack Compose / Miuix)
- **Miuix 现代设计**：遵循小米 HyperOS / Miuix 视觉规范，配备 2:1 模块化仪表盘与分类胶囊。
- **HyperOS 灵动超级岛**：原生集成 Focus Notification V3 协议，在状态栏与灵动岛展示流光呼吸灯效与状态提醒。
- **多重后台存活方案**：
  - **Xposed / LSPosed 模式**：通过系统剪贴板服务底层 Hook 实现 100% 无感后台同步与防休眠；
  - **Shizuku 模式**：免 Root 授权，通过 ADB 权限实现后台剪贴板监听；
  - **前台常驻服务**：适配通用 Android 系统的常驻与自启策略。
- **扫码极速配对**：集成 CameraX 与本地扫码引擎，支持摄像头快速对焦扫描及系统相册识码，秒级完成设备授权。
- **高清大图查看器**：支持手势双指缩放、旋转、保存到相册及系统分享。
- **侧边悬浮窗**：支持贴边小窗快速查看最近同步记录与快捷动作。

---

## 安装与使用指引

### Windows 桌面端安装
1. 下载运行 [`NexClip_Setup_v20260825.01_x64.exe`](https://github.com/yixing233/nexclip/releases/download/v20260825.01/NexClip_Setup_v20260825.01_x64.exe)；
2. 安装向导将自动检测系统环境，按需在线静默安装缺失的 .NET 9 与 Windows App SDK 依赖；
3. 安装完成后即可使用全局热键或托盘唤出使用。

### Android 移动端安装
1. 下载安装 [`NexClip_v20260825.01_Android.apk`](https://github.com/yixing233/nexclip/releases/download/v20260825.01/NexClip_v20260825.01_Android.apk)；
2. 首次启动可选择使用 **Shizuku 授权**、**Xposed 模块激活** 或 **常驻服务**；
3. 扫码或输入配对码即可连接。

---

## 发布文件与 SHA256 校验

| 产物文件 | 适用平台 | 文件大小 | SHA256 校验码 |
| :--- | :--- | :--- | :--- |
| `NexClip_Setup_v20260825.01_x64.exe` | Windows 10 (1809+) / Windows 11 (x64) | 8.96 MB | `A2B5A57C66B4771163D7AF740A846AEEE81928BF0A23958030C8EE314DA11EE9` |
| `NexClip_v20260825.01_Android.apk` | Android 8.0+ (推荐 HyperOS / MIUI) | 15.28 MB | `F7E980505E831D765A52EEF84A8FC92DF7BC0B0C8F3CC9B04DDBD8F00CB09592` |

---

## 源码分支导航

- **Windows 桌面端源码**：`git clone -b windows https://github.com/yixing233/nexclip.git`
- **Android 客户端源码**：`git clone -b android https://github.com/yixing233/nexclip.git`
