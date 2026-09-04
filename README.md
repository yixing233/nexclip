# NexClip

NexClip 是一套现代化、轻量高效的多端跨设备剪贴板同步与文本 / 图片实时互传系统。支持 Windows 桌面端、Android 移动端、Web 管理面板以及轻量化自建服务端，提供毫秒级实时双向推送、历史记录管理、智能动作识别及跨端互联体验。

---

## 客户端下载与版本发布 (Releases)

您可以在 [GitHub Releases 页面](https://github.com/yixing233/nexclip/releases) 下载最新版本的客户端安装包与发行产物：

| 平台 / 组件 | 产物类型 | 适用系统 | 下载直链 |
| :--- | :--- | :--- | :--- |
| **Windows 桌面端** | 安装包 (`.exe`) / 便携绿色版 | Windows 10 (1809+) / Windows 11 (x64) | [下载 Windows 最新版](https://github.com/yixing233/nexclip/releases/latest) |
| **Android 移动端** | 安装包 (`.apk`) | Android 8.0+ (推荐 HyperOS / MIUI) | [下载 Android 最新版](https://github.com/yixing233/nexclip/releases/latest) |
| **Node.js 服务端** | 源码包 / 容器镜像 | 全平台 (Node.js >= 22.5) | [查看 Release](https://github.com/yixing233/nexclip/releases) |
| **.NET C# 服务端** | 单文件独立发布包 | Linux / Windows / macOS (.NET 9) | [查看 Release](https://github.com/yixing233/nexclip/releases) |

---

## 源码分支导航 (Source Code Branches)

本项目采用多分支独立代码架构，主分支 (`master`) 专注于 Release 发布与聚合文档导航，各平台代码托管于独立分支：

| 分支名称 | 平台 / 工程 | 技术栈 | 推荐 IDE / 环境 | 克隆指令 |
| :--- | :--- | :--- | :--- | :--- |
| **[`windows`](https://github.com/yixing233/nexclip/tree/windows)** | Windows 桌面端 | WinUI 3 + .NET 9 + Mica | Visual Studio 2022 | `git clone -b windows https://github.com/yixing233/nexclip.git` |
| **[`android`](https://github.com/yixing233/nexclip/tree/android)** | Android 移动端 | Compose + Miuix + HyperOS | Android Studio | `git clone -b android https://github.com/yixing233/nexclip.git` |
| **[`server`](https://github.com/yixing233/nexclip/tree/server)** | Node 服务端 | Node.js + TypeScript + ws | VSCode / Node.js | `git clone -b server https://github.com/yixing233/nexclip.git` |
| **[`server-csharp`](https://github.com/yixing233/nexclip/tree/server-csharp)** | .NET 服务端 | ASP.NET Core 9 + EF Core | VS / Rider / .NET 9 | `git clone -b server-csharp https://github.com/yixing233/nexclip.git` |
| **[`web`](https://github.com/yixing233/nexclip/tree/web)** | Web 控制台 | React 19 + Vite + Tailwind | VSCode / WebStorm | `git clone -b web https://github.com/yixing233/nexclip.git` |

---

## 核心特性矩阵

- **多端全覆盖**：
  - **Android 端**：基于 Jetpack Compose + Miuix 设计风格，深度适配 HyperOS 灵动超级岛通知，支持 Xposed 模块注入、Shizuku 免 Root 授权及前台常驻等多种后台监听机制。
  - **Windows 桌面端**：基于 WinUI 3 + .NET 9 原生构建，深度集成 Windows 系统托盘与全局快捷键呼出，支持纯文本、富文本（HTML）与高清图片同步。
  - **服务端 (Server)**：提供 Node.js (TypeScript) 与 .NET 9 ASP.NET Core 双版本实现，基于 SignalR / WebSocket 实现毫秒级双向实时同步，内置 SQLite 数据库，开箱即用。
  - **Web 管理端**：现代响应式 React + Vite 控制台，支持免客户端快速查看、发送与管理剪贴板数据。
- **智能动作引擎 (SmartAction)**：自动识别剪贴板中的链接、手机号、IP 地址、邮箱、验证码、快递单号等内容，提供一键快捷操作。
- **毫秒级实时推送**：基于 SignalR 协议进行长连接保活，支持多设备在线状态感知与按设备定向推送。
- **拼音首字母检索**：桌面端与移动端均支持全拼与首字母快速搜索历史条目。
- **安全与私密性**：支持统一 Token 身份验证，数据完全保存在自建服务器或本地 SQLite 中，保护个人隐私。

---

## 服务端快速部署

### 方案 A：Node.js 服务端 (推荐)
```bash
# 克隆服务端分支
git clone -b server https://github.com/yixing233/nexclip.git nexclip-server
cd nexclip-server

# 安装依赖并启动
npm install
npm run build
npm start
```

### 方案 B：.NET C# 服务端
```bash
# 克隆 .NET 服务端分支
git clone -b server-csharp https://github.com/yixing233/nexclip.git nexclip-server-csharp
cd nexclip-server-csharp

# 运行
dotnet run --configuration Release
```

### 常用环境变量配置

| 配置字段 | 环境变量名称 | 默认值 | 描述 |
| :--- | :--- | :--- | :--- |
| `Port` | `SC_PORT` | `5033` | 服务端监听端口 |
| `AuthToken` | `SC_AUTH_TOKEN` | (空) | 客户端连接认证令牌，留空则无需认证 |
| `DatabasePath` | `SC_DB_PATH` | `data/syncclipboard.db` | SQLite 数据库存储路径 |
| `ImagePath` | `SC_IMAGE_PATH` | `data/images` | 剪贴板图片文件保存目录 |
| `MaxHistoryItems` | `SC_MAX_HISTORY` | `200` | 剪贴板历史最大保留条数 |
| `OnlineThresholdSeconds` | `SC_ONLINE_THRESHOLD_SECONDS` | `120` | 设备在线判定心跳超时时间 (秒) |

---

## 开源协议

本项目采用 [MIT License](LICENSE) 开源协议。
