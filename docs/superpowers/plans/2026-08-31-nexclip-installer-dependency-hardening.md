# NexClip Installer Dependency Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 NexClip 原生安装器可靠检测、下载、验证并安装必需运行时，同时提供连续、可解释的安装动画与重启状态。

**Architecture:** 将依赖定义、检测策略、下载校验、安装进程策略从 GDI 窗口中分离到 `Services`，窗口只维护可视状态并调用同一条依赖执行管线。下载使用安全的单流临时文件和重试策略，安装完成后以目标机实际状态复检；所有进度先写目标值，再由 60 FPS 定时器平滑显示。

**Tech Stack:** .NET 9、Win32/GDI+、Native AOT、HttpClient、Windows Registry、WinTrust、xUnit

---

### Task 1: 可测试的安装策略

**Files:**
- Create: `NexClip.Installer.Native/Services/SetupPolicy.cs`
- Create: `NexClip.Installer.Native/Services/DependencyModels.cs`
- Create: `NexClip.Installer.Native.Tests/NexClip.Installer.Native.Tests.csproj`
- Create: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`
- Modify: `NexClip.Installer.Native/NexClip.Installer.Native.csproj`

- [x] **Step 1: 写退出码、重试、运行时目录、版本和磁盘空间策略测试**

测试覆盖 `0/1638/1641/3010`，指数退避，稳定版 `.NET 9` 目录，同时存在 Core/Desktop 框架，Windows App Runtime 版本，以及带依赖下载余量的空间计算。

- [x] **Step 2: 运行测试并确认因类型缺失而失败**

Run: `dotnet test NexClip.Installer.Native.Tests/NexClip.Installer.Native.Tests.csproj -c Debug`

Expected: FAIL，提示 `SetupPolicy`、`DependencyDefinition` 或检测辅助方法尚不存在。

- [x] **Step 3: 实现最小策略与依赖定义**

定义 `DependencyKind`、`DependencyDefinition`、`DependencyInstallStage`、`DependencyProgress`、`DependencyInstallResult`；实现成功退出码、重启码、三次重试退避、20 分钟安装超时、HTTPS URI 和磁盘空间判断。

- [x] **Step 4: 运行策略测试**

Run: `dotnet test NexClip.Installer.Native.Tests/NexClip.Installer.Native.Tests.csproj -c Debug`

Expected: PASS。

### Task 2: 安全下载与依赖检测

**Files:**
- Create: `NexClip.Installer.Native/Services/DownloadVerifier.cs`
- Modify: `NexClip.Installer.Native/Services/DependencyService.cs`
- Modify: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] **Step 1: 增加下载异常分类与双框架检测测试**

覆盖无状态网络错误、HTTP 408/429/5xx 可重试、HTTP 404 不重试；只有 `Microsoft.NETCore.App` 与 `Microsoft.WindowsDesktop.App` 都满足 `9.0.0` 时 `.NET 9` 才通过。

- [x] **Step 2: 实现安全下载**

只允许 HTTPS；启用系统证书校验；写入 `.partial`；限制 512 MB；失败清理并按 1/2 秒退避重试；成功后原子移动；使用 WinTrust 和签名证书确认 Microsoft Corporation 签名。

- [x] **Step 3: 实现准确检测**

`.NET 9` 检查注册表安装根和 Program Files 根中的 Core/Desktop；VC++ 检查 x64 安装标记与版本；Windows App Runtime 仅接受 `Microsoft.WindowsAppRuntime.1.8` 的 x64/Neutral 包且版本满足下限。

- [x] **Step 4: 运行测试**

Run: `dotnet test NexClip.Installer.Native.Tests/NexClip.Installer.Native.Tests.csproj -c Debug`

Expected: PASS。

### Task 3: 统一依赖安装管线

**Files:**
- Modify: `NexClip.Installer.Native/Services/DependencyService.cs`
- Modify: `NexClip.Installer.Native.Tests/SetupPolicyTests.cs`

- [x] **Step 1: 增加安装结果策略测试**

断言失败退出码被拒绝，`1641/3010` 被记录为需要重启，依赖元数据均为 HTTPS 且含静默参数。

- [x] **Step 2: 实现 `InstallDependencyAsync`**

下载、签名验证、静默启动、超时、退出码判断、重启记录、安装后复检依次执行；临时文件放在每次安装唯一目录并在 `finally` 清理。

- [x] **Step 3: 实现 `EnsureDependenciesAsync`**

按当前目标机检测结果仅安装缺失项，聚合总进度和重启结果；没有缺失项时报告环境已就绪。

- [x] **Step 4: 运行测试**

Run: `dotnet test NexClip.Installer.Native.Tests/NexClip.Installer.Native.Tests.csproj -c Debug`

Expected: PASS。

### Task 4: UI 状态与动画

**Files:**
- Modify: `NexClip.Installer.Native/UI/FluentInstallerWindow.cs`
- Modify: `NexClip.Installer.Native/Rendering/LucideGdiPlus.cs`
- Modify: `NexClip.Installer.Native/Services/PayloadService.cs`

- [x] **Step 1: 收敛窗口依赖状态**

欢迎页三行从 `DependencyService.Dependencies` 生成；单项按钮和主安装按钮均调用统一服务；检测或安装异常显示失败状态和可操作错误信息。

- [x] **Step 2: 实现连续动画**

定时器在检测、下载、安装和部署期间运行；旋转 Lucide loader；依赖行显示进度向目标值平滑插值；总进度采用帧率无关、只前进的缓动并稳定到 100%。

- [x] **Step 3: 增加空间与重启状态**

根据嵌入载荷、缺失依赖和安全余量计算空间；安装前分别阻止目标盘或临时盘空间不足；依赖返回重启码时完成页禁用立即启动并明确提示重启后使用。

- [x] **Step 4: 检查 UI 文案与图标**

界面不使用 Emoji；所有状态使用 `LucideGdiPlus` 矢量图标；文本在 620x470 基准窗口和 DPI 缩放下保持既有边界。

### Task 5: 验证

**Files:**
- Modify: `docs/superpowers/plans/2026-08-31-nexclip-installer-dependency-hardening.md`

- [x] **Step 1: 运行测试**

Run: `dotnet test NexClip.Installer.Native.Tests/NexClip.Installer.Native.Tests.csproj -c Release`

Expected: PASS。

- [x] **Step 2: 运行普通构建**

Run: `dotnet build NexClip.Installer.Native/NexClip.Installer.Native.csproj -c Release`

Expected: PASS，无新增警告。

- [x] **Step 3: 运行 Native AOT 发布**

Run: `dotnet publish NexClip.Installer.Native/NexClip.Installer.Native.csproj -c Release -r win-x64 -p:PublishAot=true -o .artifacts/installer-validation`

Expected: `NexClip_Setup.exe` 生成成功；不启动安装器或 `NexClip.exe`。

- [x] **Step 4: 检查差异**

Run: `git diff --check`

Expected: 无新增空白错误；保留任务开始前已有的 `PayloadService.cs`、`payload.zip` 与 `build-installer.ps1` 修改。

> 本计划按用户要求在当前会话内执行；不创建提交、不推送。
