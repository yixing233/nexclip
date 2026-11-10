# SettingsPage 结构清单（重写前记录）

生成时间：2026-10-23
源文件：`app/src/main/java/clip/yixing/sync/ui/SettingsPage.kt`（3386 行）

## 一、页面枚举

```kotlin
enum class SettingsSubPage(val title: String) {
    Basic("基础与界面"),
    Sync("同步服务"),
    Notifications("通知与隐私"),
    Storage("数据与存储"),
    Permissions("权限与保活"),
    Devices("设备列表与配对"),
    Filter("过滤规则"),
    SmartActions("智能动作与应用直达"),
    SmsCode("短信验证码"),
    SmsCodeRecords("短信记录"),
    Paste("一键粘贴与悬浮球"),
    About("关于")
}
```

## 二、真实层级（关键：这是多级树，不是两层）

```
一级 设置主页
├── Basic        基础与界面
├── Sync         同步服务
├── Notifications 通知与隐私
│   ├── SmartActions   智能动作与应用直达
│   ├── SmsCode        短信验证码
│   │   └── SmsCodeRecords  短信记录   ← 四级！
│   ├── Paste          一键粘贴与悬浮球
│   └── Filter         过滤规则
├── Storage      数据与存储
├── Permissions  权限与保活
├── About        关于
└── Devices      设备列表与配对（通过其它入口进入）
```

**旧实现的缺陷**：用单个 `parentSubPage` 记录父级，只能表达一层嵌套，
且返回时被清空。`SmsCode → SmsCodeRecords` 这条四级链路无法正确回退。

## 三、各页面内容区块

| 行号 | SectionBlock 标题 | 所属页面 |
|---|---|---|
| 662 | 设置分类 | 一级主页 |
| 747 | 基础与界面 | Basic |
| 856 | 同步服务 | Sync |
| 917 | 配对管理 | Sync |
| 1018 | 通知与隐私 | Notifications |
| 1168 | 数据与存储 | Storage |
| 1237 | 权限与保活 | Permissions |
| 1441 | 配对管理 | Devices |
| 1610 | 应用忽略黑名单 | Filter |
| 1740 | 内容关键词黑名单 | Filter |
| 2024 | 项目信息 | About |
| 2131 | 开源致谢与技术组件 | About |

## 四、跳转关系（openSubPage 调用点）

| 调用位置 | 来源页面 | 目标页面 |
|---|---|---|
| 663 | 一级主页 | Basic |
| 664 | 一级主页 | Sync |
| 665 | 一级主页 | Notifications |
| 666 | 一级主页 | Storage |
| 667 | 一级主页 | Permissions |
| 668 | 一级主页 | About |
| 1112 | Notifications | SmartActions |
| 1130 | Notifications | SmsCode |
| 1149 | Notifications | Paste |
| 1161 | Notifications | Filter |
| 1858 | SmsCode | SmsCodeRecords |

## 五、渲染结构（旧）

```
Box(fillMaxSize)
├── 底层：一级设置主页 PageShell("设置")
│   └── 视差：translationX / scale / alpha 随 subPageAnimProgress
│   └── 遮罩：Color.Black alpha (1-progress)*0.2
└── 顶层：displayedSubPage?.let { subPage ->    ← 单一渲染槽（问题所在）
    Box(graphicsLayer translationX = p*width ...)
    └── when(subPage) { 12 个分支，约 1458 行 }
```

**问题**：只有一个渲染槽。切换页面时内容整体替换，
旧页已消失、新页还在屏幕外 → 中间露出底层设置主页（3 → 1 → 2 闪烁）。

## 六、需要保留的能力

1. **多级返回**：任意深度都能正确回退
2. **对向滑动动画**：旧页右滑出 + 新页右滑入，任何一帧都被填满
3. **手势返回**：PredictiveBackHandler，跟手 + 取消可回弹
4. **底层视差**：一级主页在二级页展开时下沉（0.94~1.0 缩放）
5. **所有 12 个页面的内容完整保留**
6. **所有状态与副作用**：设备列表刷新、Shizuku 状态、配对码对话框、
   更新检查、导出导入、应用过滤名单等

## 七、关键状态变量（重写时必须保留）

导航相关：
- `subPageStack`（新增，替代 parentSubPage）
- `exitingSubPage`
- `subPageAnimProgress`

设备与配对：
- `deviceName`, `selfDeviceId`, `showNameDialog`, `nameDialogState`
- `devices`, `devicesLoading`, `devicesError`, `devicesReload`, `devicesManual`
- `showPairDialog`, `showCodeSheet`, `generatedCode`, `dialogCodeState`, `pairing`
- `deleteTargetDevice`

设置项：
- `bootStart`, `autoCheckUpdate`, `updateSourceIndex`, `notificationStyle`
- `outerGlowEnabled`, `glowColorIndex`, `islandExpandedTime`, `islandTimeout`
- `historyIndex`, `retentionDays`, `ignoreSensitive`
- `predictiveBackEnabled`, `predictiveBackMaxProgressPercent`, `hideFromRecents`
- `syncNotificationEnabled`, `captureNotificationEnabled`

权限状态：
- `isAccessibilityOn`, `canDrawOverlays`, `isShizukuReady`
- `showRestrictedSettingsGuide`, `accessibilitySettingsRequested`

数据管理：
- `storagePath`, `exporting`, `importing`, `dbPath` 等

更新：
- `checkingUpdate`, `updateDialogInfo`, `spinRotation`

## 八、重写方案

**新建文件 `SettingsPage.kt`（重写），保留 `SettingsSubPage` 枚举不变。**

核心改造：
1. 导航状态改为 `mutableStateListOf<SettingsSubPage>`（栈）
2. 渲染层：`RenderLayer(subPage, offsetFraction)` 被两个层各调用一次
3. 动画：`layerProgress` 0f（栈顶满屏）→ 1f（层交换完成）
4. 内容：12 个页面的 `when` 分支**原样搬过来**，不重新发明

**分文件组织（降低单文件复杂度）**：
- `SettingsPage.kt` —— 导航骨架 + 一级主页
- `SettingsPages.kt` —— 12 个子页面的内容 composable
