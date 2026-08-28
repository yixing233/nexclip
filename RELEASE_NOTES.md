# NexClip v20260828.02 - Android 去重防自环优化与即时同步策略升级

NexClip 是一套专为多端设备打造的现代化、轻量高效跨平台剪贴板同步与局域网文件/消息流转系统。本次 **v20260828.02** 重点解决了 Android 移动端应用内点击复制导致的自环重复建档、来源篡改与时间覆写问题，并移除了设备上线覆写本地剪贴板的逻辑，全面回归纯净秒级即时同步。

---

## 本次更新核心亮点 (Changelog)

### 1. Android 端内容特征去重与应用内防自环
- **全局内容特征白名单池 (`internalCopyHashes`)**：建立线程安全的内部复制 Hash 注册与校验机制，有效覆盖 30 秒监听窗口。
- **杜绝应用内复制重复建档**：凡在 Android 软件内发起的复制操作（列表项复制、聊天室消息复制、验证码/色值/自定义规则直达、图片预览复制等），绝不生成重复新条目，绝不篡改条目原始来源设备，绝不更新条目时间戳，绝不上报服务端自环广播。
- **ClipData 隐式指纹标记**：在复制写入时注入应用内 PersistableBundle 标记，双重保险规避系统跳板异步捕获导致的误判。

### 2. 即时同步策略纯净化
- **移除上线拉取覆写逻辑**：移除了后台服务启动与重连时的 `pullAndApply` 强制覆写操作，设备上线或手机解锁时不再拉取服务器历史覆盖本地剪贴板，仅在两端同时在线时执行实时的 WebSocket 广播同步。

### 3. Windows 桌面端平滑覆盖安装与 Native AOT 架构
- **Native AOT 现代安装向导**：安装包精简至 17.71 MB，60 FPS Win32 缓动插值引擎，解决进度跳变与覆盖升级句柄锁死。

---

## 发布文件与 SHA256 校验

| 产物文件 | 适用平台 | 文件大小 | SHA256 校验码 |
| :--- | :--- | :--- | :--- |
| **`NexClip_Setup_v20260828.02_x64.exe`** | Windows 10 (1809+) / Windows 11 (x64) | 17.71 MB | `2a58a0ed720497867061c2313b3a720739e63637c61cd41b6de52b338c9d2c58` |
| **`NexClip_v20260828.02_Android.apk`** | Android 8.0+ (推荐 HyperOS / MIUI) | 15.28 MB | `efbe40868516980fa202a08c25e26f5a582319b6f6b2a051b8226a13b337302d` |

---

## 下载通道说明

- **GitHub 官方源**：[GitHub Releases](https://github.com/yixing233/nexclip/releases)
- **服务端直连加速**：
  - Windows 安装包：`https://nexclip.157342.xyz/releases/NexClip_Setup_v20260828.02_x64.exe`
  - Android 安装包：`https://nexclip.157342.xyz/releases/NexClip_v20260828.02_Android.apk`
  - Web 控制台与门户：[https://nexclip.157342.xyz](https://nexclip.157342.xyz)

---

## 源码分支导航

- **Windows 桌面端源码**：`git clone -b windows https://github.com/yixing233/nexclip.git`
- **Android 移动端源码**：`git clone -b android https://github.com/yixing233/nexclip.git`
- **Web 端源码**：`git clone -b web https://github.com/yixing233/nexclip.git`
- **服务端源码**：`git clone -b server https://github.com/yixing233/nexclip.git`
