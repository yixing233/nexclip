# NexClip 改动记录

> 本文件记录每一次非平凡改动的**动机、方案、验证方式与遗留问题**。
> 目的是避免"改了什么、为什么这么改、验证到哪一步"散落在会话记录里。
>
> 记录规则：
> - 每次改动追加一节，按时间倒序排列（最新在最前）。
> - 必须写明**验证边界**：哪些验证过、哪些没验证、为什么没验证。
> - 涉及多端的改动，标注每端的完成状态。
> - 被推翻的判断要保留，写清"原来怎么想、实测后怎么变"。

---

## 2026-10-23 · 安装器:统一开机自启动并修复卸载残留

### 背景

排查安装器整体质量时发现一个**真实影响用户的卸载残留 bug**：卸载后每次开机，Windows
仍在尝试启动一个已被删除的程序。

根因是自启动在本项目里有**两条互不相通的实现**：

| 写入方 | 位置 | 卸载时是否清理 |
|---|---|---|
| 安装器 | 「启动」文件夹快捷方式（`ShortcutHelper.SetStartupShortcut`） | ✅ `RemoveAllShortcuts` 会删 |
| 主程序设置页 | 注册表 `HKCU\...\Run` 的 `NexClip` 值（`Services/StartupService`） | ❌ **从不清理** |

`Uninstall.exe` 只调 `RemoveAllShortcuts()`，完全不认识 Run 键。全项目 `grep` 确认
`NexClip.Installer.Native/` 下**没有任何 Run 键处理代码**。

### 实测复现（本机，改动前）

卸载前状态，两份自启动项同时存在：

```
Run 键      NexClip = "C:\Users\...\NexClip.exe" --autostart
启动文件夹   NexClip.lnk
```

用旧卸载器（`/silent /uninstall`，退出码 0）卸载后：

```
启动文件夹 lnk = False      ← 清了
卸载注册项     = False      ← 清了
安装目录       = False      ← 清了
Run[NexClip]   = "C:\...\NexClip.exe" --autostart   ← 仍残留
```

双份的成因：安装时勾选「开机自启动」写快捷方式，而主程序
`SettingsStore.BootStartEnabled` 默认 `true`，设置页初始化时又写 Run 键。

### 方案

**统一为只写 Run 键**，与主程序设置页保持单一实现路径。

新增 `NexClip.Installer.Native/Services/StartupRegistration.cs`：

- `RunKeyPath` / `ValueName` / `LegacyValueNames` 与桌面端 `StartupService` 完全一致
- `BuildValue` 始终给 exe 路径加引号（安装目录可能含空格）
- `Enable` 写入主值并清除遗留值名（`SyncClipboard.Desktop` / `NexClip.Desktop`）
- `Disable` **幂等**：键不存在、值不存在都返回成功（「本来就干净」与「刚清理完」对调用方等价）
- `Root` 属性可替换注册表根键，让测试指向一次性子键，不污染真实自启动项

`ShortcutHelper` 的调整：

- `SetStartupShortcut` 改为委托 `StartupRegistration`，**不再写快捷方式**，
  同时清掉历史遗留的启动文件夹快捷方式，让老版本升级上来的机器收敛
- `RemoveAllShortcuts` 先清理自启动项再删快捷方式；三处删除各自独立 `catch`，
  失败记日志而不是被同一个 `catch` 静默吞掉

**安装侧改为双向处理**（交互式与 `/silent` 两条路径都改了）：勾选时写入，
未勾选时主动清理。此前只写不清，导致「取消勾选后重装」仍然自启动 —— 用户明确表达了不要，
旧 Run 键却还在生效。

**顺带修复**：`Program.cs` 有两段连续的 `<summary>`，前一段没有对应方法
（"输出运行环境体检报告"错挂在 `TryLoadDependencies` 上），已归位到 `RunDiagnostics`。

**诊断增强**：`RunDiagnostics` 现在输出自启动注册状态、实际值内容，
以及是否还残留旧式启动文件夹快捷方式 —— 这个字段历史上是残留高发点。

### 验证

- **端到端实测（真实安装/卸载，非模拟）**：
  1. 旧卸载器卸载 → `Run[NexClip]` 残留，**bug 复现** ✔
  2. 新安装器 `/silent`（不带 `/autostart`）→ 残留值被主动清空 ✔
  3. 新安装器 `/silent /autostart` → Run 键正确写入，启动文件夹**不再**产生快捷方式 ✔
  4. 新卸载器 `/silent /uninstall` → `Run[NexClip]` / `NexClip.Desktop` /
     `SyncClipboard.Desktop` 全部清空，快捷方式与目录同步清理 ✔
  5. `/diagnose` 报告正确输出「开机自启动: 已注册」及值内容 ✔
- **单元测试**：新增 `StartupRegistrationTests` 9 项，全部通过
  （覆盖引号、遗留值清理、幂等、Run 键缺失、`IsEnabled` 识别遗留名、路径常量断言）
- **回归**：安装器测试 118 项全过；托盘测试 18 项全过；桌面端 Release 构建 0 错误
- **恢复现场**：测试后已重装 `/autostart` 并启动应用，状态与本机原先一致

### 遗留

1. 主程序 `SettingsStore.BootStartEnabled` 默认 `true`，但设置页初始化时用
   `StartupService.IsEnabled()` 回读真实状态（`SettingsViewModel:328`），
   所以默认值不会覆盖用户选择 —— 这点是对的，但**默认值与实际注册状态脱钩**容易误读，
   后续可考虑把默认值改成"跟随实际状态"。
2. 安装器仍需管理员权限（`requireAdministrator`），而自启动写的是 HKCU 用户级键 ——
   两者不对称的问题单独记录在待办（`ct_73c1d730`）。
3. 未在**非管理员账户**下验证。本机是管理员账户，`HKCU` 写入无权限问题；
   受限账户下 `Enable` 会返回 false 并记日志（有代码路径，但未实测）。

---

## 2026-10-23 · 三端统一凭证失效判定

### 背景

当天线上发生了一次真实故障：Windows 客户端升级到 v20261023.02 后持续离线、反复 401，
最终本地配对凭证被清空，只能人工重新配对。

排查确认根因是**服务端部署的是 09-18 的旧代码**：全新客户端把 `deviceToken` 放进
`X-Device-Token` 请求头，旧服务端 negotiate 只读 URL 查询参数，于是 401；
客户端把这当成"凭证失效"清空了 `IsPaired` 与 token，之后无凭证重连再 401，形成死循环。

这次故障暴露了一个更普遍的问题：**"什么情况下该清空本地凭证"这个判定，三端都写得很粗**
——只看 HTTP 状态码，把 403 也算作凭证失效，且 401 的两种语义（缺少凭证 / 凭证无效）完全混淆。

### 问题分析

服务端其实已经区分了三种设备凭证状态，但**用同一批状态码表达**：

| 服务端情况 | 状态码 | 原客户端处理 | 正确做法 |
|---|---|---|---|
| `credential_missing` 请求没带凭证 | 401 | 清空配对 ❌ | **不清空**（属客户端自身 bug） |
| `credential_invalid` 凭证无效 | 401 | 清空配对 ✅ | 清空 |
| `credential_revoked` 设备被移除 | 410 | 清空配对 ✅ | 清空 |
| `device_identity_mismatch` 身份不匹配 | 403 | 清空配对 ❌ | **不清空**（凭证本身有效） |

403 的三种来源全部与凭证有效性无关：
`上传设备身份与凭证不匹配`、`无权删除该条目`、`无权操作该设备`。

典型受害场景：设备 ID 因 `resetDeviceId` 与 token 不再匹配 → 服务端返回 403 →
客户端清掉仍然有效的 token → 用户被迫重新配对。

### 方案

**服务端给出机器可读错误码，客户端按 code 判断，不再猜状态码。**

服务端（`src/controllers.ts`）新增 `code` 字段，保持向后兼容（老客户端只读 `error`）：

```ts
export const CredentialErrorCode = {
  Missing: 'credential_missing',          // 请求没带凭证
  Invalid: 'credential_invalid',          // 凭证无效
  Revoked: 'credential_revoked',          // 设备已被移除
  IdentityMismatch: 'device_identity_mismatch',  // 403 身份不匹配
} as const
```

判定规则（三端一致）：

```
code 存在时以 code 为准:
  credential_invalid / credential_revoked  -> 清空配对
  credential_missing / device_identity_mismatch -> 不清空
code 不存在（老服务端）时退回状态码:
  401 / 410 -> 清空
  403       -> 不清空
  其它/null -> 不清空（网络异常时 statusCode 为 null）
```

### 各端改动

| 端 | 文件 | 状态 |
|---|---|---|
| 服务端 | `src/controllers.ts` | ✅ 已改，`tsc --noEmit` 通过 |
| Windows | `Services/ServerApi.cs`、`Services/SyncEngine.cs` | ✅ 已改，`dotnet build` 0 错误 |
| Android | `data/SyncApi.kt`、`data/PushClient.kt`、`util/SyncSettings.kt`、`service/ClipboardMonitorService.kt` | ⚠️ 已改，**未编译验证** |

**服务端**：`sendApiError` 增加可选 `code` 参数；`rejectDeviceCredential` 三分支带上对应 code；
4 处身份不匹配 403 带上 `device_identity_mismatch`。

**Windows**：
- `ApiException` 增加 `Code` 属性与 `IsCredentialMissing` / `IsCredentialInvalid` /
  `IsCredentialRevoked` / `IsDeviceIdentityMismatch` 四个语义化判断。
- 修了一个**顺带发现的 bug**：`ReadErrorDetailAsync` 在 401 分支直接 `return` 固定文案，
  根本读不到响应体里的 `code`。已重排为先解析 body 再决定文案。
- `IsDeviceAuthFailure` 重写：code 优先，无 code 时 403 不再算凭证失效。
- `ShouldAttemptLegacyMigration` 同步收敛，避免两个函数语义分叉。

**Android**：
- `ApiException` 增加 `code` 参数与同名语义判断（Kotlin 属性）。
- `execute()` 解析响应体的 `code` 字段。
- `SyncSettings` 新增统一判定函数 `shouldClearPairingOn(ApiException)`。
- 替换 3 处重复的 `if (401 || 403 || 410) clearPairing()`：
  图片上传、文本上传、`PushClient.onAuthFailure`。
- `PushClient.isAuthFailure` 收紧：先排除 `Forbidden` / `device_identity_mismatch`，再匹配 401/410。
- 顺带把两处 `catch (_: Exception) {}` 改为记录日志——原来服务端 413（体积超限）
  之类的失败被完全吞掉，用户毫无感知。

### 验证

| 项 | 结果 |
|---|---|
| 服务端 `npx tsc --noEmit` | ✅ 通过 |
| Windows `dotnet build`（Debug） | ✅ 0 错误 |
| Android 编译 | ❌ **未做** —— 本机无 JDK、无 Android SDK，`gradlew` 无法运行 |
| Android 结构完整性 | ⚠️ 仅做了括号/字符串配对的粗略检查（4 文件均平衡），不等于编译通过 |

**Android 必须补编译验证**。改动涉及 `val code: String?` 参数位置与 Kotlin 属性语法，
建议在有 SDK 的环境跑一次 `./gradlew :app:compileDebugKotlin`。

### 遗留

1. **服务端部署**：`/var/www/nexclip-server` 非 git 管理，本次通过 patch `dist` 上线。
   该目录与仓库代码会持续漂移，见待办 `ct_317eaff6`。
2. **Android 编译验证**缺失，见上。
3. 本次只统一了"是否清空凭证"的判定；`ct_7fdea874`（Android 令牌仍走 URL query）
   是另一个独立问题，方案见该待办（推荐短期一次性 ticket）。

---

## 2026-10-23 · Windows v20261023.01 / v20261023.02 发布

### v20261023.01（本次会话完成）

5 个功能提交，均已合并到 `origin/windows`：

| 提交 | 内容 | 关键实测数据 |
|---|---|---|
| `034d2d0` | 同步状态事件回 UI 线程；上传并发 1→4；超限图降采样 | 并发 1 时最后一条文本 37.4s 才同步，并发 2 为 2.7s |
| `c36e840` | 超限图片降采样保存本地预览，不再静默丢弃 | 4096 噪声图 PNG 实测 64MB，2048 为 16MB |
| `d5a4224` | 日志按天分卷 + 5MB 滚动 + 7 天保留 + 脱敏 | 42MB/55 文件 → 3.8MB/8 文件；写日志 262µs → 0.7µs |
| `1fa79e2` | 设备令牌改走请求头 | 端到端：header 200 / query 200 / 无凭证 401 / 伪造 401 |
| `943b30a` | HistoryStore 启用 WAL + 多语句事务 | UPDATE 3418µs → 62µs；事务再省 23.2% |

另有服务端配套提交 `b61c7c5`（negotiate 支持 header 鉴权）。

### v20261023.02（另一会话完成，本会话仅记录）

`bc90b18` 剪贴板写入移出 UI 线程，新增 `ClipboardWriter.cs`（常驻 STA 线程 + Win32 消息泵）。
改动 16 个文件 +1193/-200。

**待观察**：用"硬超时后放弃写入"替代重试，需确认超时后是否会静默丢弃（用户感知为"复制没反应"）。
见待办 `ct_3ab40e2d`。

### 发布过程中修复的打包脚本缺陷

`build-installer.ps1` 在独立分支结构下**完全无法运行**，修了三处（`ce672ba`）：

1. `$desktopDir = Join-Path $root "desktop"` —— windows 分支根目录就是桌面工程，无 `desktop/` 子目录。
2. `$releasesDir = "e:\Code\syncclipboard-releases"` —— 该盘符不存在，改为工程内 `release-out/`。
3. `installer/resolve-setup-dependencies.ps1` **缺 UTF-8 BOM** —— 内容是 UTF-8 但无 BOM，
   Windows PowerShell 5.1 按 ANSI(GBK) 解析中文注释，报大量语法错误，
   打包在第 3 步中断。**该脚本在中文 Windows 上此前从未跑通过。**

> ⚠️ 教训：用文件编辑工具修改 `.ps1` 会把 BOM 去掉，导致同样问题复现。
> 后续改 PowerShell 脚本应确认首 3 字节为 `ef bb bf`，或用 Python 以 `utf-8-sig` 读写。

---

## 2026-10-23 · 人工回归结果（7 项中 5 项通过）

| # | 项目 | 结果 | 证据 |
|---|---|---|---|
| 1 | 搜索中文/拼音/英文 | ✅ | 真实库 204 条，12 个查询词新旧逻辑命中 id 集合完全一致 |
| 2 | 断网再恢复 | ✅ | pm2 restart 后 6 次采样 lastSeen 全程推进，无中断 |
| 3 | 日志轮转与级别 | ✅ | 10-16 及更早已清理；DEBUG 从 307 条降到 0 |
| 4 | 大图+文本并发 | ⚠️ 条件通过 | 代码确认并发度 4 与计数逻辑；当前网速太快，时序差异无法体现 |
| 5 | 双向推送 | ✅ | 本机 #4809/#4810 的 origin=1，对应服务端 #7039/#7040 来自 XXCCBB |
| 6 | 改储存位置迁移 | ✅ | WAL 已启用，204 条 / 203 hash / 4 收藏与升级前一致 |
| 7 | 整机剪贴板卡死 | ⏸ 待人工 | 需多窗口快速粘贴复现 |

### 附带发现：服务端 `online` 字段有误导性

```ts
online: isLive || d.LastSeenAt >= threshold,   // threshold = 120 秒
lastSeenAt: isLive ? toIso(dbNow()) : toIso(d.LastSeenAt),
```

"在线"可能只是配对那一刻写入的时间戳在撑 2 分钟缓冲期。
**判断是否真实连接要看 `lastSeenAt` 是否持续前进**，不要看 `online`。
本次故障就是靠"lastSeenAt 固定在 06:33:30 不动"才识破的。

---

## 记录模板

```markdown
## YYYY-MM-DD · 一句话标题

### 背景
为什么现在要做这件事（问题现象、触发来源、是否来自实测）。

### 问题分析
根因是什么，排除了哪些错误猜测。

### 方案
怎么改的，为什么选这个方案，放弃了什么备选。

### 各端改动
| 端 | 文件 | 状态 |

### 验证
哪些验证过（附数据）、哪些没验证、为什么。**不允许把"编译通过"写成"功能验证通过"。**

### 遗留
未完成的部分、已知风险、后续待办编号。
```