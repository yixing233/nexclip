# AI Agent 规范与项目开发指南 (SyncClipboard / NexClip)

本项目为多端剪贴板同步与文本 / 图片互传工具（包含 Android 端、Windows WinUI 3 桌面端、Server 服务端）。所有协助本项目的 AI 编程助手与开发者必须严格遵守以下核心规范：

---

## 🎨 1. 图标与 UI 呈现规范 (Icon & UI Design Standards)

> [!IMPORTANT]
> **绝对禁止使用 Emoji 表情符号作为 UI 图标或文案修饰！**

1. **严禁使用 Emoji**：
   - 界面所有组件（包括但不限于：Tab 标签、FilterChip 筛选胶囊、按钮、列表项标签、空状态提示、对话框标题与说明）**严禁混入或使用 Emoji 表情符号**（如 🗓️, 💻, 📱, 🖥️, ✕, ❌, ⭐, 🔗, 🖼️, 📝, ✨ 等）。
2. **强制统一使用 Lucide / FontAwesome 矢量图标**：
   - **Android 端 (ndroid/)**：
     - 统一使用 clip.yixing.sync.ui.LucideIcons 原生矢量图标集（基于 Compose ImageVector，24x24 视口，2.0 笔画粗细）；
     - 以及 	op.yukonga.miuix.kmp.icon.MiuixIcons 中的标准矢量图标；
     - 涉及清除/关闭操作统一使用 LucideIcons.X；涉及设备标识统一使用 LucideIcons.forDevice(name)。
   - **Windows 桌面端 (desktop/)**：
     - 统一使用 NexClip.Desktop.Services.Lucide 提供的矢量 SVG 图标资源（如 Lucide.Copy, Lucide.Trash2, Lucide.Pin 等）。

---

## 🛠️ 2. 构建与运行规则 (Build & Runtime Rules)

1. **开发与构建系统**：系统环境为 Windows，使用 PowerShell 执行编译与构建脚本（如 .\gradlew.bat assembleDebug、dotnet build）。
2. **桌面端启动约束**：Windows 桌面端编译完成后，**不要自动运行或启动桌面端进程 (NexClip.exe)**，由用户手动运行测试。
3. **语言交流**：始终使用中文（Chinese）进行沟通与汇报。

---

## 📱 3. Android Miuix 架构与设计规范

1. **顶栏气泡卡片与页面胶囊结合**：
   - 主列表页顶部仅保留单行紧凑轻量的主要分类胶囊；
   - 复合维度筛选（如日期范围筛选、来源设备筛选）统一收纳在顶栏右侧的筛选漏斗（WindowIconDropdownMenu）气泡卡片菜单中；
   - 激活过滤条件时，在主分类栏右侧呈现带 LucideIcons.X 的高亮清除小胶囊。
2. **HyperOS 灵动超级岛规范**：
   - 接入原生 com.xzakota.hyper.notification:focus-api:1.4；
   - 遵循 FocusNotification.buildV3 标准协议构建流光呼吸灯效与大小岛模板。
