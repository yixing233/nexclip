# NexClip v20260901.01 - Native AOT 原生极速安装器与全端流式更新发布

NexClip 是一套专为多端设备打造的现代化、轻量高效跨平台剪贴板同步与局域网文件/消息流转系统。本次 **v20260901.01** 正式引入全新 Native AOT 原生安装向导、双端应用内一键下载更新、智能远控应用过滤（防剪贴板死循环）、Fluent 2 现代托盘右键菜单，并增强了后台网络心跳自愈与收藏记录保护机制。

---

## 本次更新核心亮点 (Changelog)

### 1. Windows 桌面端
- **全新 Native AOT 原生极速安装向导**：采用 C# Native AOT 纯机器码与 Win32/GDI+ 原生渲染，毫秒级冷启动；支持 Visual C++、.NET 9 与 Windows App SDK 缺失依赖的独立分步安装与签名校验；具备磁盘空间预检与事务回滚保护。
- **应用内流式下载升级**：关于卡片与主窗口支持直接流式下载升级安装包，实时展示下载进度与网络速率，自动校验 SHA256 完整性并覆盖安装。
- **智能远程控制应用过滤**：内置 AnyDesk、TeamViewer、RustDesk、ToDesk、向日葵 (Sunlogin)、Windows 远程桌面 (mstsc)、Chrome 远程桌面等数十款主流远控工具过滤规则；支持自定义进程扫描与流式标签管理，防止远控时剪贴板循环污染。
- **现代 Fluent 2 系统托盘菜单**：重构上下文菜单渲染引擎，适配 Windows 11 Fluent 沉浸式圆角与系统深浅主题，全面采用 Lucide 矢量图标，热键动态联动。
- **剪贴板管理优化**：清空历史操作始终自动保留已收藏记录；新增“保持上次浏览位置”开关；优化大文本查看弹窗尺寸。
- **后台心跳稳定性**：解耦 UI 定时器为纯后台心跳机制，断线后自动重连并重新拉取对齐数据。

### 2. Android 移动端
- **应用内流式下载与自动安装**：版本更新弹窗支持直接流式下载 APK，带进度条与速率展示，下载并核验 SHA256 后自动调用系统安装器。
- **通知栏与超级岛图片复制支持**：快捷动作全面支持图片条目，点击异步写入系统剪贴板，支持异常降级纯文本。
- **来源设备与包名精准解析**：移除远程数据对本地包名的误识别，独立展示来源设备标识。
- **焦点与输入法体验优化**：悬浮窗优化输入法焦点配置，杜绝后台抓取剪贴板时意外隐藏软键盘；复制过程智能拦截多余焦点抢占。

---

## 发布文件与 SHA256 校验

| 产物文件 | 适用平台 | 文件大小 | SHA256 校验码 |
| :--- | :--- | :--- | :--- |
| **`NexClip_Setup_v20260901.01_x64.exe`** | Windows 10 (1809+) / Windows 11 (x64) | 18.27 MB | `8fb9e32c586a7538a7bad5d93b386afa9f25f0795f77d236b55cbfb1930bb4ad` |
| **`NexClip_v20260901.01_Android.apk`** | Android 8.0+ (推荐 HyperOS / MIUI) | 15.29 MB | `a8b42c9776c4129dca043c641fcbd5631d3af0c5257f5d6d7811a075c2289083` |

---

## 下载通道说明

- **GitHub 官方源**：[GitHub Releases](https://github.com/yixing233/nexclip/releases/tag/v20260901.01)
- **服务端直连加速**：
  - Windows 安装包：`https://nexclip.157342.xyz/releases/NexClip_Setup_v20260901.01_x64.exe`
  - Android 安装包：`https://nexclip.157342.xyz/releases/NexClip_v20260901.01_Android.apk`
  - 检查更新配置：`https://nexclip.157342.xyz/releases/version.json`
  - Web 控制台与门户：[https://nexclip.157342.xyz](https://nexclip.157342.xyz)

---

## 源码分支导航

- **Windows 桌面端源码**：`git clone -b windows https://github.com/yixing233/nexclip.git`
- **Android 移动端源码**：`git clone -b android https://github.com/yixing233/nexclip.git`
- **Web 端源码**：`git clone -b web https://github.com/yixing233/nexclip.git`
- **服务端源码**：`git clone -b server https://github.com/yixing233/nexclip.git`
