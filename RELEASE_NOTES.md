# NexClip v20260825.01 - 首个正式版本发布 (Initial Release)

NexClip 是一套专为多端设备打造的现代化、轻量高效跨平台剪贴板同步与局域网文件流转系统。本次为 NexClip 的**首个正式发布版本**，带来全平台客户端（Windows / Android / Web）以及双技术栈自建服务端的完整功能体系。

---

## 产品定位与核心优势

- **跨端全覆盖**：深度适配 Windows 10/11 (WinUI 3)、Android (Miuix / HyperOS) 及 Web 浏览器。
- **毫秒级实时流转**：基于 SignalR / WebSocket 长连接协议，实现双向无感实时推送与多设备在线状态感知。
- **极致轻量化**：Windows 安装包仅 **8.96 MB**，Android 安装包仅 **15.28 MB**，极低内存与系统资源占用。
- **隐私与自建优先**：支持自建服务端部署，内置轻量 SQLite 数据库，数据完全保存在自建服务器或本地，无第三方数据泄露风险。

---

## 全平台功能概览

### 1. Windows 桌面端 (WinUI 3 / .NET 9)
- **现代 Fluent 视觉**：基于 WinUI 3 原生打造，深度融合 Mica 背景材质与自适应深色/浅色主题。
- **托盘与全局热键**：支持快捷键一键唤出剪贴板历史浮窗，托盘常驻支持快速暂停同步与状态监控。
- **智能动作引擎 (Smart Action)**：自动识别复制内容中的链接、短信验证码、手机号、IP 地址、邮箱及快递单号，提供一键打开、复制、拨打或查询。
- **来源应用感知**：自动探测并展示复制内容的产生来源软件名称及高精应用图标。
- **多模态流转**：
  - 纯文本与多行代码高亮；
  - 高清图片缩略图与快速保存；
  - 全屏文件拖拽覆盖层，支持将外部文件一键推送到其他设备。
- **拼音检索**：内置拼音全拼及拼音首字母模糊搜索，毫秒级定位历史记录。

### 2. Android 移动端 (Jetpack Compose / Miuix)
- **Miuix 设计语言**：遵循小米 HyperOS / Miuix 视觉规范，配备 2:1 模块化仪表盘与分类胶囊。
- **HyperOS 灵动超级岛**：原生集成 Focus Notification V3 协议，在状态栏与灵动岛展示呼吸灯效与剪贴板状态提醒。
- **全方位后台存活方案**：
  - **Xposed / LSPosed 模式**：通过系统剪贴板服务底层 Hook 实现 100% 无感后台同步与防休眠；
  - **Shizuku 模式**：免 Root 授权，通过 ADB 权限实现后台剪贴板监听；
  - **前台常驻服务**：适配通用 Android 系统的常驻与自启策略。
- **扫码配对**：集成 CameraX 与本地扫码引擎，支持摄像头对焦扫描及系统相册识码，秒级完成设备授权。
- **大图查看与流转**：支持手势缩放、全屏旋转、保存相册及系统分享。
- **侧边悬浮窗**：支持贴边小窗快速查看最近同步记录与常用动作。

### 3. 服务端 (Node.js & .NET 9 双实现)
- **Node.js (TypeScript) 服务端 [推荐]**：
  - 超轻量设计，内置使用 `node:sqlite` 原生数据库，运行时内存仅约 30~50MB；
  - 完整实现 SignalR JSON 线协议，支持多设备心跳监测与定向推送；
  - 支持直接托管 Web 管理端静态资源。
- **.NET 9 ASP.NET Core 服务端**：
  - 采用 Kestrel 高性能服务器与 Entity Framework Core，支持单文件无依赖发布。

### 4. Web 管理控制台 (React 19 / Vite)
- 现代化响应式控制台，支持在任意浏览器中管理多端设备、检索剪贴板历史与在线发送文本。

---

## 快速安装与使用

### Windows 桌面端
1. 下载并运行 [`NexClip_Setup_v20260825.01_x64.exe`](https://github.com/yixing233/nexclip/releases/download/v20260825.01/NexClip_Setup_v20260825.01_x64.exe)；
2. 安装向导将自动检测系统环境，并按需在线静默安装所需的 .NET 9 与 Windows App SDK 依赖；
3. 安装完成后即可在系统托盘及快捷键中使用。

### Android 移动端
1. 下载并安装 [`NexClip_v20260825.01_Android.apk`](https://github.com/yixing233/nexclip/releases/download/v20260825.01/NexClip_v20260825.01_Android.apk)；
2. 首次启动可选择使用 **Shizuku 授权**、**Xposed 模块激活** 或 **常驻服务**；
3. 扫码或输入配对码即可连接自建服务端。

### 服务端自建 (极简 3 步)
```bash
# 克隆服务端分支
git clone -b server https://github.com/yixing233/nexclip.git nexclip-server
cd nexclip-server

# 安装并启动
npm install && npm run build && npm start
```
*(服务端默认运行于 `http://0.0.0.0:5033`，可在 `config.json` 或环境变量中配置端口及 AuthToken)*

---

## 发布文件与 SHA256 校验

| 产物文件 | 适用平台 | 文件大小 | SHA256 校验码 |
| :--- | :--- | :--- | :--- |
| `NexClip_Setup_v20260825.01_x64.exe` | Windows 10 (1809+) / Windows 11 (x64) | 8.96 MB | `A2B5A57C66B4771163D7AF740A846AEEE81928BF0A23958030C8EE314DA11EE9` |
| `NexClip_v20260825.01_Android.apk` | Android 8.0+ (推荐 HyperOS / MIUI) | 15.28 MB | `F7E980505E831D765A52EEF84A8FC92DF7BC0B0C8F3CC9B04DDBD8F00CB09592` |

---

## 源码分支导航

本项目采用单平台独立分支架构，拉取特定分支即可使用对应 IDE 独立打开：

- **Windows 桌面端工程**：`git clone -b windows https://github.com/yixing233/nexclip.git`
- **Android 移动端工程**：`git clone -b android https://github.com/yixing233/nexclip.git`
- **Node.js 服务端工程**：`git clone -b server https://github.com/yixing233/nexclip.git`
- **.NET C# 服务端工程**：`git clone -b server-csharp https://github.com/yixing233/nexclip.git`
- **Web 控制台工程**：`git clone -b web https://github.com/yixing233/nexclip.git`
