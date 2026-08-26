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
  - 文件与文件夹拖拽传输。
- **智能动作引擎 (SmartAction)**：
  - 自动识别链接（一键打开浏览器）、手机号（一键呼叫/复制）、验证码（一键复制）、IP/邮箱等。
- **拼音与全文检索**：支持拼音首字母与全拼模糊搜索历史记录。
- **免安装便携与 Inno Setup 安装包**：支持单文件运行与标准安装包制作。

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

## 独立分支

本项目已支持独立分支拉取与开发：
```bash
git clone -b windows https://github.com/yixing233/nexclip.git
```
拉取后根目录即为 WinUI 3 原生工程，可直接使用 Visual Studio 2022 打开。
