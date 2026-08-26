# NexClip v20260825.02 - 双端更新通道切换与平滑覆盖安装升级

NexClip 是一套专为多端设备打造的现代化、轻量高效跨平台剪贴板同步与局域网文件/消息流转系统。本次 **v20260825.02** 重点增强了多端更新通道调度、网络容灾降级机制，并彻底解决了 Windows 端覆盖安装时的进程占用卡死问题。

---

## 本次更新核心亮点 (Changelog)

### 1. 双端更新来源自由切换 (Update Channels)
- **支持多通道下拉选择**：在 Windows 端与 Android 端设置中新增“更新下载来源”选项（提供 `GitHub Releases (默认)` 与 `服务端直连加速` 两档下拉切换）。
- **国内高速直连**：选中直连加速后，客户端优先向服务端直连节点探测最新版本并下发直连高速安装包，免去大陆网络下 GitHub 访问受限困扰。
- **智能网络容灾降级**：若 GitHub 官方源发生连接超时或不可达，客户端将自动无缝回退至服务端直连源探测版本，保障更新链路高可用。

### 2. Windows 桌面端平滑覆盖安装优化
- **进程自动平滑终止**：修复旧版安装程序在应用运行时覆盖升级被系统 Restart Manager 阻塞或文件锁死卡住的问题。
- **无感生命周期接管**：安装向导在初始化与解包前自动安全终止旧版进程并释放句柄，升级后即可无缝重启启动。

### 3. Web 门户与直连分发升级
- **双通道组合按钮**：网页门户下载区域支持主按钮直达 GitHub 官方源，下拉扩展菜单直连高速通道、版本日志与 SHA256 校验。

---

## 发布文件与 SHA256 校验

| 产物文件 | 适用平台 | 文件大小 | SHA256 校验码 |
| :--- | :--- | :--- | :--- |
| **`NexClip_Setup_v20260825.02_x64.exe`** | Windows 10 (1809+) / Windows 11 (x64) | 22.03 MB | `1a586a4873bedd3318a43f99ce7d138c5c4e0aa3bb3ebe1db02f7cabbfcfd264` |
| **`NexClip_v20260825.02_Android.apk`** | Android 8.0+ (推荐 HyperOS / MIUI) | 15.28 MB | `94c23181737b8056fe2187302476e6ac82a620c18810643921ef50a7b2a63391` |

---

## 下载通道说明

- **GitHub 官方源**：[GitHub Releases](https://github.com/yixing233/nexclip/releases)
- **服务端直连加速**：
  - Windows 安装包：`https://nexclip.157342.xyz/releases/NexClip_Setup_v20260825.02_x64.exe`
  - Android 安装包：`https://nexclip.157342.xyz/releases/NexClip_v20260825.02_Android.apk`
  - Web 控制台与门户：[https://nexclip.157342.xyz](https://nexclip.157342.xyz)

---

## 源码分支导航

- **Windows 桌面端源码**：`git clone -b windows https://github.com/yixing233/nexclip.git`
- **Android 移动端源码**：`git clone -b android https://github.com/yixing233/nexclip.git`
- **Web 端源码**：`git clone -b web https://github.com/yixing233/nexclip.git`
- **服务端源码**：`git clone -b server https://github.com/yixing233/nexclip.git`
