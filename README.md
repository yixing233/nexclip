# NexClip

NexClip 是一套现代化、轻量高效的多端跨设备剪贴板同步与局域网文件流转系统。支持 Windows 桌面端、Android 移动端、Web 管理面板以及轻量化自建服务端，提供毫秒级实时双向推送、历史记录管理、智能动作识别及跨端互联体验。

---

## 核心特性

- **多端全覆盖**：
  - **Android 端**：基于 Jetpack Compose + Miuix 设计风格，深度适配 HyperOS 灵动超级岛通知，支持 Xposed 框架注入、Shizuku 免 Root 授权及前台常驻等多种后台监听机制。
  - **Windows 桌面端**：基于 WinUI 3 + .NET 9 原生构建，深度集成 Windows 系统托盘与全局快捷键呼出，支持富文本、高清图片及文件传输。
  - **服务端 (Server)**：提供 Node.js (TypeScript) 与 .NET 9 ASP.NET Core 双版本实现，基于 SignalR / WebSocket 实现毫秒级双向实时同步，内置 SQLite 数据库，开箱即用。
  - **Web 管理端**：现代响应式 React + Vite 控制台，支持免客户端快速查看、发送与管理剪贴板数据。
- **智能动作引擎 (SmartAction)**：自动识别剪贴板中的链接、手机号、IP 地址、邮箱、验证码、快递单号等内容，提供一键快捷操作。
- **毫秒级实时推送**：基于 SignalR 协议进行长连接保活，支持多设备在线状态感知与按设备定向推送。
- **拼音首字母检索**：桌面端与移动端均支持全拼与首字母快速搜索历史条目。
- **安全与私密性**：支持统一 Token 身份验证，数据完全保存在自建服务器或本地 SQLite 中，保护个人隐私。

---

## 仓库分支架构

为了便于各端开发者使用专属 IDE（Android Studio / Visual Studio / VSCode）进行独立开发与编译，本项目提供了单工程独立分支：

| 分支名 | 适用平台 | 说明与建议 IDE |
| :--- | :--- | :--- |
| **`master`** | 全平台 Monorepo | 包含所有平台源码（Android、Desktop、Server、Web） |
| **`android`** | Android 移动端 | 根目录为 Android 项目工程，推荐使用 **Android Studio** 直接打开 |
| **`windows`** | Windows 桌面端 | 根目录为 WinUI 3 原生工程，推荐使用 **Visual Studio 2022** 打开 |
| **`server`** | Node.js 服务端 | 根目录为 TypeScript 服务端，推荐使用 **VSCode / Node.js** 运行 |
| **`server-csharp`** | .NET C# 服务端 | 根目录为 ASP.NET Core 服务端，推荐使用 **Visual Studio / Rider** 运行 |

克隆特定分支示例：
```bash
# 仅克隆 Android 端
git clone -b android https://github.com/yixing233/nexclip.git nexclip-android

# 仅克隆 Windows 端
git clone -b windows https://github.com/yixing233/nexclip.git nexclip-windows

# 仅克隆服务端
git clone -b server https://github.com/yixing233/nexclip.git nexclip-server
```

---

## 快速开始

### 1. 部署服务端

#### 方案 A：Node.js / TypeScript 服务端 (推荐)
```bash
cd server-node
npm install
npm run build
npm start
```
默认服务端口为 `5033`，可在 `server-node/config.json` 或通过环境变量自定义。

#### 方案 B：.NET 9 ASP.NET Core 服务端
```bash
cd server
dotnet run --configuration Release
```

---

### 2. 编译与运行 Windows 桌面端

- **环境要求**：Windows 10/11, .NET 9 SDK, Visual Studio 2022 (包含 Windows App SDK 工作负荷)。
- **编译命令**：
```powershell
cd desktop
dotnet build NexClip.Desktop.csproj -c Release
```
*(注：编译产物位于 `desktop/bin/x64/Release/net9.0-windows10.0.19041.0/`)*

---

### 3. 编译与安装 Android 端

- **环境要求**：JDK 17+, Android SDK (API 35+), Android Studio Ladybug 或更高版本。
- **编译 Debug APK**：
```powershell
cd android
.\gradlew.bat assembleDebug
```
*(注：安装包生成于 `android/app/build/outputs/apk/debug/`)*

---

### 4. 运行 Web 管理面板

```bash
cd web
npm install
npm run dev
```

---

## 服务端配置说明

通过 `config.json` 或环境变量进行配置：

| 配置字段 | 环境变量名称 | 默认值 | 描述 |
| :--- | :--- | :--- | :--- |
| `Port` | `SC_PORT` | `5033` | 服务端监听端口 |
| `AuthToken` | `SC_AUTH_TOKEN` | (空) | 客户端连接认证令牌，留空则无需认证 |
| `DatabasePath` | `SC_DB_PATH` | `data/syncclipboard.db` | SQLite 数据库存储路径 |
| `ImagePath` | `SC_IMAGE_PATH` | `data/images` | 剪贴板图片文件保存目录 |
| `MaxHistoryItems` | `SC_MAX_HISTORY` | `200` | 剪贴板历史最大保留条数 |
| `OnlineThresholdSeconds` | `SC_ONLINE_THRESHOLD_SECONDS` | `120` | 设备在线判定心跳超时时间 (秒) |

---

## 项目规范与技术约定

- **图标规范**：严格使用 Lucide / FontAwesome 矢量图标体系，Android 端使用 Compose ImageVector，桌面端使用 SVG 资源。
- **UI 风格**：Android 端统一接入 Miuix 与 HyperOS 设计语言；Windows 端采用 WinUI 3 Mica 材质与 Fluent Design 规范。
- **通信协议**：采用 ASP.NET Core SignalR 协议规范实现实时双向消息通信。

---

## 开源协议

本项目采用 [MIT License](LICENSE) 开源协议。
