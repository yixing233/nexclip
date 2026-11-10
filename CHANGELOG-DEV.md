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

## 2026-10-23 · 服务端部署目录纳入 git 管理

### 背景

承接待办 `ct_317eaff6`。当天排查「升级后连不上」时发现，生产 `/var/www/nexclip-server`
不是 git 仓库，靠手工拷贝 dist 部署，没有任何版本记录，线上跑的其实是 09-18 的代码。
临时修复只能直接 patch `dist/server.js` 并同步改 `src/server.ts`，src 与 dist 的一致性完全靠人工。

进一步核对发现比待办描述的**更严重**：

| 事实 | 说明 |
|---|---|
| `origin/server` 上没有凭证错误码的提交 | 服务器 `src/controllers.ts` 早已改好（`CredentialErrorCode`），但从未进过版本库 |
| 任何人重新 build 都会把它冲掉 | 10-23 那次 401 死循环的修复只存在于服务器磁盘上 |
| 被跟踪的 `config.json` 与生产布局不符 | 仓库版是单仓库结构 `../server/data`、`../web/dist`，生产目录是 `./data`、`./web` |
| 服务器没有 typescript | 所以只能在服务器上改产物，`npm run build` 根本跑不起来 |

### 方案

**先让仓库追上生产，再把生产变成 git 检出。**

1. **补齐仓库**（`origin/server` 前进 4 个提交）：
   - `e7b3adf` 把服务器上已有的凭证错误码改动落成正式提交；
   - `0664f08` 新增 `deploy.sh`，`config.json` 移出版本库并加 `config.example.json` 模板，
     `config.ts` 支持 `SC_WEB_DIST` 且按目录结构自动探测 `web/dist`；
   - `924b235` 用 `git rm --cached` 真正 untrack `config.json`（前一个提交只加 `.gitignore`，
     对已跟踪文件无效——迁移时真的踩到了，见下）；
   - `b6d356b` 加 `.gitattributes` 锁 `*.sh` 为 LF。

2. **生产目录原地转 git**：`git init` + `remote add` + `reset --hard origin/server`。
   选原地而不是「clone 到新目录再切」，因为 `data/`、`web/`、`releases/`、`.env`
   全程原地不动，不需要搬运任何敏感文件或 72MB 数据库。

3. **部署脚本 `deploy.sh`**：`git pull --ff-only` → `npm ci --include=dev` →
   `tsc --outDir dist.new` → 原子替换 `dist` → `pm2 restart` → 健康检查。
   构建失败不碰 `dist` 也不重启；健康检查失败自动回滚旧 `dist`。

### 迁移中踩到的坑（值得记）

`config.json` 被 `reset --hard` **覆盖了两次**，因为第一次只加 `.gitignore` 而没有 untrack 它：

1. 第一次 reset → 被换成仓库版 `../server/data` 路径；
2. 修好后再 reset 到 `924b235` → 该提交把文件移出版本库，而磁盘上那份仍被跟踪，
   `reset --hard` 直接**删除**了它。

两次都靠迁移前记录的 md5 还原，最终 `config.json` 与迁移前**逐字节一致**（`c8e8e224…`）。
教训：`git reset --hard` 之前，对「即将被 untrack 的已跟踪本地文件」必须先复制副本，
只记 md5 不够——文件可能被删，没有副本就还原不了。**本次是运气好**，
配置内容在对话里出现过才得以重建。

### 验证

| 项 | 结果 |
|---|---|
| 线上 commit 可查 | ✅ `git rev-parse --short HEAD` → `924b235`，工作区干净 |
| 服务器有 tsc | ✅ node_modules 含 typescript 5.9.3，`tsc -v` 正常 |
| 部署脚本真实跑通 | ✅ 一次完整 deploy 从 fetch 到健康检查全过 |
| 凭证错误码生效 | ✅ `GET /api/clipboard/history` 无凭证 → `{"error":"缺少设备凭证,请先完成配对","code":"credential_missing"}` |
| negotiate header 鉴权 | ✅ header 凭证路径返回 401（伪造凭证），未回退成 400 |
| 真实设备重连 | ✅ 重启后 10 秒内 XXCCBB / DESKTOP-IGJLCES / GIGA-XXCCBB / 小米14 四台 `LastSeenAt` 全部推进到 07:25:09 |
| 业务写入 | ✅ 新条目 #7063 正常落库 |
| 静态资源 | ✅ `/` 200、`/assets/*.js` 200，仍指向 `web/dist` |
| 公网链路 | ✅ `https://nexclip.157342.xyz/api/health` 200，`/releases/` 200 |
| 数据完好 | ✅ Entries 1000 条，Devices 5 台，库文件与迁移前同大小 |
| 失败路径：脏工作区 | ✅ 提示未提交改动并中止 |
| 失败路径：构建失败 | ✅ `TS2322` 报错后退出，`dist` md5 未变、无 `dist.new` 残留、未重启 |
| 失败路径：健康检查失败 | ✅ 自动回滚旧 dist，md5 与部署前一致 |

**未验证**：`pm2 save`（本次未执行，重启机器后进程列表依赖上次 save，与本改动无关）。

### 遗留

1. 服务器 `PATH` 上的 node 是 v20.20.2，pm2 用的是 nvm 里的 v22.22.2。
   构建用前者（tsc 能跑即可），运行用后者（`node:sqlite` 需要 ≥22.5）。
   脚本已检测并给出提示，但**两套 node 并存**本身是隐患，建议统一。
2. `/var/www/nexclip-server/releases/`（531MB 安装包）与 `backup/`（138MB）仍靠人工管理，
   会随部署无限增长，没有清理策略。
3. 这不是 CI：还是要人工在服务器上跑 `./deploy.sh`。真正的自动化需要自托管 runner 或
   部署密钥 + 推送式 hook，本次未做。

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

## 2026-10-23 · Android 图片上传：压缩 + 失败可见

### 背景

阅读 `origin/android` 代码时发现，Android 端的图片上传有三个叠加的缺陷（实测代码路径，非推测）：

```kotlin
// ClipboardMonitorService.kt:259
if (bytes != null && bytes.isNotEmpty() && bytes.size <= 30 * 1024 * 1024) {   // 放行 30MB
    ...
    api.uploadImage(bytes, ...)                                                // 原图直传，零压缩
}
```

而服务端 `config.json` 是 `maxImageSizeBytes: 10485760`（10MB）。手机长截图、相机原图很容易超过。

更糟的是失败被完全吞掉：

```kotlin
} catch (_: Exception) {
}
```

服务端返回 413 后，客户端既不重试也不提示，**用户以为已同步，直到换设备粘贴才发现内容不在**。

对比 Windows 端（已在 `ct_bf54a70c` 修复）：超限时降采样生成预览、本地照存、只跳过同步并明确提示。

### 方案

**1. 上传前压缩**（新增 `ImageLoader.compressForUpload`）

目标是压到服务端上限的 80%（8MB），留出 multipart 封装余量。

**关键设计：先降质量，再降尺寸。** 第一版实现是"按单边尺寸逐级缩小"，验证时发现长截图会被压成细条：

| 场景 | 第一版结果 | 问题 |
|---|---|---|
| 1080×12000 长截图 | 368×4096 | 宽度只剩 **34%** |
| 1080×30000 超长截图 | 147×4096 | 宽度只剩 **14%** |

根因是单边 4096 硬上限对长截图是错误约束——长截图的价值恰恰在于"长"。而实测表明，
截图与照片经 JPEG q=90 编码后通常已远小于 10MB（1080×12000 约 0.4MB），
**根本不需要缩尺寸**。

最终策略：
1. 原图已够小 → 原样上传，不解码不重编码（省 CPU、不损失画质）
2. 否则在**原尺寸**上降 JPEG 质量（q: 90→85→80→70→60→50）
3. 仍超限才按比例缩尺寸（1.0→0.75→0.5→0.35→0.25→0.15），每档同样从 q=90 试
4. 透明图优先保持 PNG；PNG 超限才退回 JPEG
5. 总像素上限 24MP（防解码 OOM），**不设单边硬上限**

验证结果（Python 等价模型，8 个场景）：

| 场景 | 原始 | 决策 | 输出 | 宽保留 |
|---|---|---|---|---|
| 微信小图 800×600 | 0.1MB | 原样 | 800×600 | 100% |
| 手机截图 1080×2400 | 0.9MB | 原样 | 1080×2400 | 100% |
| 长截图 1080×12000 | 9.1MB | q=90 | 1080×12000 | **100%** |
| 超长截图 1080×30000 | 23.8MB | q=90 | 929×25819 | 86% |
| 相机原图 6000×4000 | 13.4MB | q=90 | 6000×4000 | **100%** |
| 超宽图 8000×1000 | 2.9MB | 原样 | 8000×1000 | 100% |
| 极端 12000×9000 | 38.1MB | q=90 | 5656×4242 | 47% |
| 超极端 20000×15000 | 76.3MB | q=90 | 5656×4242 | 28% |

8/8 场景都能产出合规图片。

**2. 失败可见**

- 两处 `catch (_: Exception) {}` 改为记录日志（含 statusCode 与异常类型）
- 新增 `SyncNotificationManager.notifyUploadFailed()`：压缩失败/无法解码时在通知栏留一条，
  用独立通知 ID 3001，不覆盖前台常驻通知
- 压缩降级时记录 Info 日志（原尺寸 → 新尺寸、字节数、mimeType）

**3. 顺带修复**

- `SyncApi.uploadImage` 增加 `mimeType` 参数：压缩成 JPEG 后必须告知服务端，
  否则会以 `.png` 扩展名存下 JPEG 内容，下载端拿到错误类型
- **EXIF 方向丢失**：原 `applyExifRotation` 在没有 Uri 时直接返回原图，
  导致"从字节数组来的图片"方向信息全丢（手机竖拍上传后在其他设备上是横的）。
  改为无 Uri 时从 `ByteArrayInputStream` 读 EXIF
- 读入保护从"30MB 上传判据"改为"64MB 防 OOM 保护"，语义不再混淆

### 各端改动

| 端 | 文件 | 状态 |
|---|---|---|
| Android | `util/ImageLoader.kt` | ✅ 新增 `compressForUpload`（约 150 行） |
| Android | `data/SyncApi.kt` | ✅ `uploadImage` 支持 mimeType |
| Android | `service/ClipboardMonitorService.kt` | ✅ 上传前压缩 + 失败提示 |
| Android | `service/SyncNotificationManager.kt` | ✅ 新增 `notifyUploadFailed` |
| Android | `ui/RecordsPage.kt` | ✅ 手动推送路径同样压缩 |

> 编译器在改 `uploadImage` 参数名时抓出了 `RecordsPage.kt` 的调用点，
> 说明手动推送路径原本也会上传未压缩原图，已一并修复。

### 验证

| 项 | 结果 |
|---|---|
| `./gradlew :app:compileDebugKotlin` | ✅ BUILD SUCCESSFUL |
| 压缩策略（Python 等价模型，8 场景） | ✅ 8/8 产出合规图片 |
| **真机验证** | ❌ **未做** —— 无 Android 设备/模拟器 |

**必须补真机验证**：压缩后的图是否真的能被服务端接受、画质是否可接受、
EXIF 方向是否还正确。Python 模型只是逻辑等价推演，不是真实 JPEG 编码结果。

### 遗留

1. 压缩目标 8MB 是经验值，真实 JPEG 编码器输出与估算可能有偏差，需真机确认。
2. 超长截图（1080×30000）压缩后宽度保留 86%，仍属可接受范围但不完美。
3. `ct_7fdea874`（Android 令牌走 URL）是独立问题，本次未处理。

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
| 服务端线上实测 | ✅ 无凭证返回 `credential_missing`，错误令牌返回 `credential_invalid` |
| Windows `dotnet build`（Debug） | ✅ 0 错误 |
| **Android `./gradlew :app:compileDebugKotlin`** | ✅ **BUILD SUCCESSFUL**（编译通过，非运行验证） |

Android 编译环境是本次新搭的（首次编译前本机无 JDK / 无 Android SDK）：
- JDK：Microsoft OpenJDK 21.0.12.101（winget `Microsoft.OpenJDK.21`）
- Android SDK：`%LOCALAPPDATA%\Android\Sdk`，含
  `platforms;android-37.0`、`build-tools;37.0.0`、`build-tools;35.0.0`、`platform-tools`
- 注意：`compileSdk = 37` 对应的包名是 `platforms;android-37.0`，不是 `android-37`

> ⚠️ 编译通过 ≠ 行为正确。上述 Android 改动仍未在真机上验证
> "401/403 时到底清不清配对"这一实际行为。

### 遗留

1. ~~**服务端部署**：`/var/www/nexclip-server` 非 git 管理，本次通过 patch `dist` 上线。~~
   **已于同日修复**：目录已转为 git 检出，凭证错误码已正式入库，见下文
   「服务端部署目录纳入 git 管理」（待办 `ct_317eaff6` 已完成）。
2. **Android 未真机验证**：编译通过，但"401/403 时不清空配对"需要真机或集成测试确认。
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