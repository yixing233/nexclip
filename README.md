# NexClip Windows 桌面端

NexClip Windows 桌面端基于 WinUI 3 (Windows App SDK) 与 .NET 9 打造，深度融合 Windows 11 Fluent Design 与 Mica 材质。

---

## 核心特性

- **原生 WinUI 3 体验**：Mica 背景材质、流畅动效、自适应暗色/亮色主题。
- **系统托盘与全局热键**：
  - 默认全局热键快速唤出主剪贴板面板与搜索框。
  - 托盘右键菜单支持快速退出、清空历史、切换同步状态。
- **多种剪贴板类型支持**：
  - 纯文本与多行代码。
  - 高清图片预览与无损同步。
  - 拖拽发送：拖入图片文件按图片发送，拖入文本内容按文本发送至已同步设备。
- **智能动作引擎 (SmartAction)**：
  - 自动识别链接（一键打开浏览器）、手机号（一键呼叫/复制）、验证码（一键复制）、IP/邮箱等。
- **拼音与全文检索**：支持拼音首字母与全拼模糊搜索历史记录。
- **免安装便携与自研 Native 安装器**：支持单文件运行，或由 `NexClip.Installer.Native` 生成的单文件安装包部署（自动补齐运行环境依赖）。

---

## 构建与开发

### 环境要求
- Windows 10 (1809+) 或 Windows 11
- .NET 9.0 SDK
- Visual Studio 2022 (包含 ".NET 桌面开发" 及 "通用 Windows 平台开发" 工作负荷)

### 编译指令

```powershell
# 编译 Release 版本
dotnet build NexClip.Desktop.csproj -c Release

# 独立发布 (Self-contained)
dotnet publish NexClip.Desktop.csproj -c Release -r win-x64 --self-contained false
```

---

## 安装器与运行环境依赖

安装器为 Native AOT 单文件程序（`NexClip.Installer.Native`），启动时并行检测并按需自动下载安装三项运行环境依赖：
Visual C++ x64 运行库、.NET 9 Desktop Runtime、Windows App SDK 1.8 Runtime（框架包 + Main 包）。

- 固定下载地址、SHA-256 与体积集中在 `installer/setup-dependencies.json` 维护，作为嵌入资源随安装器打包。
- `build-installer.ps1` 打包前调用 `installer/resolve-setup-dependencies.ps1` 校验域名白名单、哈希格式与 Windows App SDK 版本一致性。
- 下载支持主源（校验 SHA-256）→ evergreen 备用源（校验 Authenticode 签名）回退、断点续传与指数退避重试；
  安装包缓存在 `%TEMP%\NexClip-Setup\cache`，失败重试可直接复用已下载内容，超过 7 天的缓存自动清理。
- 诊断日志位于 `%TEMP%\NexClip-Setup\logs\dependency-setup.log`。

### 命令行参数

```powershell
NexClip_Setup.exe                       # 交互式安装
NexClip_Setup.exe /silent               # 无人值守安装（别名 /verysilent、/quiet）
NexClip_Setup.exe /silent /dir="D:\Apps\NexClip" /nodesktopicon /autostart
NexClip_Setup.exe /uninstall            # 卸载
NexClip_Setup.exe /diagnose=report.txt  # 生成运行环境诊断报告
```

退出码：`0` 成功、`1` 失败、`2` 取消、`3010` 成功但需重启系统。

---

## 独立分支

本项目已支持独立分支拉取与开发：
```bash
git clone -b windows https://github.com/yixing233/nexclip.git
```
拉取后根目录即为 WinUI 3 原生工程，可直接使用 Visual Studio 2022 打开。
