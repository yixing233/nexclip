# NexClip 自研 Fluent 现代安装器自动化打包脚本
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

# windows 分支的根目录本身就是桌面工程(早期脚本假定脚本位于仓库根、工程在 desktop/ 下,
# 但独立分支结构里 NexClip.Desktop.csproj 就在脚本同级)。
$desktopDir = $PSScriptRoot
$installerDir = Join-Path $desktopDir "NexClip.Installer.Native"
$resourcesDir = Join-Path $installerDir "Resources"
$releasesDir = Join-Path $desktopDir "release-out"
$version = "20261023.03"

# Native AOT 的链接步骤依赖 vswhere.exe 定位 MSVC link.exe。
# VS 开发者命令行会自带,普通 PowerShell 里不在 PATH 上,链接会以 exit 123 失败,
# 所以这里补一次探测,让脚本在任意 shell 下都能跑。
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $vsInstallerDir = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if (Test-Path (Join-Path $vsInstallerDir "vswhere.exe")) {
        $env:PATH = "$vsInstallerDir;$env:PATH"
    }
    else {
        Write-Warning "未找到 vswhere.exe,Native AOT 链接可能失败;请改用 VS 开发者 PowerShell 运行本脚本。"
    }
}

Write-Host ">>> 1. 编译 NexClip.Desktop 主程序 (Release win-x64, 轻量框架依赖)..." -ForegroundColor Cyan
$tempStaging = Join-Path ([System.IO.Path]::GetTempPath()) "NexClip_Staging_$([Guid]::NewGuid().ToString('N'))"
dotnet publish "$desktopDir\NexClip.Desktop.csproj" -c Release -r win-x64 -p:WindowsAppSDKSelfContained=false --self-contained false -p:PublishSingleFile=false -p:DebugType=none -o $tempStaging

Write-Host ">>> 2. 准备 Payload 压缩包..." -ForegroundColor Cyan
if (!(Test-Path $resourcesDir)) {
    New-Item -ItemType Directory -Path $resourcesDir | Out-Null
}

$payloadZip = Join-Path $resourcesDir "payload.zip"
if (Test-Path $payloadZip) {
    Remove-Item $payloadZip -Force
}

try {
    # 过滤拷贝主程序文件
    $excludePatterns = @("*.pdb", "publish*", "*onnxruntime*", "*DirectML*", "*NPUDetect*", "*NpuDetect*", "*Microsoft.Windows.Widgets*")
    
    Get-ChildItem -Path $tempStaging -Recurse | ForEach-Object {
        $item = $_
        foreach ($pat in $excludePatterns) {
            if ($item.Name -like $pat) {
                Remove-Item $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
                break
            }
        }
    }

    # 确保 XAML XBF 与 PRI 资源索引文件完整包含进 Staging
    $binDir = Join-Path $desktopDir "bin\Release\net9.0-windows10.0.19041.0\win-x64"
    if (Test-Path $binDir) {
        Get-ChildItem -Path $binDir -Filter "*.pri" | ForEach-Object {
            Copy-Item $_.FullName $tempStaging -Force
        }
        Get-ChildItem -Path $binDir -Filter "*.xbf" -Recurse | ForEach-Object {
            $rel = $_.FullName.Substring($binDir.Length).TrimStart('\', '/')
            $dest = Join-Path $tempStaging $rel
            $destParent = Split-Path $dest -Parent
            if (-not (Test-Path -LiteralPath $destParent)) {
                [System.IO.Directory]::CreateDirectory($destParent) | Out-Null
            }
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        }
    }

    $requiredPayloadFiles = @("NexClip.exe", "NexClip.Tray.dll", "Svg.dll")
    foreach ($requiredFile in $requiredPayloadFiles) {
        $requiredPath = Join-Path $tempStaging $requiredFile
        if (!(Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Payload 缺少必要运行时文件: $requiredFile"
        }
    }

    $priFiles = @(Get-ChildItem -Path $tempStaging -Filter "*.pri" -File)
    $xbfFiles = @(Get-ChildItem -Path $tempStaging -Filter "*.xbf" -File -Recurse)
    if ($priFiles.Count -eq 0 -or $xbfFiles.Count -eq 0) {
        throw "Payload 缺少 WinUI 资源索引文件 (.pri/.xbf)"
    }

    # 压缩为 payload.zip (使用最高压缩级别)
    Write-Host ">>> 正在压缩核心 Payload 数据包..." -ForegroundColor Cyan
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempStaging, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $zipSize = (Get-Item $payloadZip).Length
    Write-Host ">>> Payload 压缩完成，大小: $([Math]::Round($zipSize / 1MB, 2)) MB" -ForegroundColor Green
}
finally {
    if (Test-Path $tempStaging) {
        Remove-Item $tempStaging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ">>> 3. 校验运行环境依赖清单 (固定下载地址 + SHA-256)..." -ForegroundColor Cyan
$dependencyManifest = Join-Path $desktopDir "installer\setup-dependencies.json"
[xml]$desktopProject = Get-Content -LiteralPath (Join-Path $desktopDir "NexClip.Desktop.csproj") -Raw -Encoding UTF8
$sdkReference = @($desktopProject.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK" })[0]
if ($null -eq $sdkReference -or [string]::IsNullOrWhiteSpace([string]$sdkReference.Version)) {
    throw "NexClip.Desktop.csproj 中未找到 Microsoft.WindowsAppSDK 版本引用。"
}
$dependencies = & (Join-Path $desktopDir "installer\resolve-setup-dependencies.ps1") `
    -ManifestPath $dependencyManifest `
    -WindowsAppSdkPackageVersion ([string]$sdkReference.Version)
Write-Host ">>>   VC++       : $($dependencies.VisualCppMinimumVersion) ($([Math]::Round($dependencies.VisualCppInstallerSizeBytes / 1MB, 1)) MB)" -ForegroundColor DarkGray
Write-Host ">>>   .NET       : $($dependencies.DotNetDesktopMinimumVersion) ($([Math]::Round($dependencies.DotNetDesktopInstallerSizeBytes / 1MB, 1)) MB)" -ForegroundColor DarkGray
Write-Host ">>>   WinAppSDK  : $($dependencies.WindowsAppRuntimeMinimumVersion) ($([Math]::Round($dependencies.WindowsAppRuntimeInstallerSizeBytes / 1MB, 1)) MB)" -ForegroundColor DarkGray

# 4a. 先产出「卸载器变体」:同一程序集但不内嵌 payload.zip。
# 卸载路径完全不使用 payload(只结束进程 / 删快捷方式与自启动项 / 删注册表 / 删目录),
# 而安装器此前直接把自己整份复制成 Uninstall.exe —— 每台机器白占 12.2MB,
# 卸载时还要映射一个 19MB 的可执行文件。
#
# 顺序很重要:变体必须**先**编译并落到 installer\ 目录,
# 主安装器(4b)才能在 dotnet publish 时把它作为嵌入资源打进单文件 exe。
Write-Host ">>> 4a. 编译轻量卸载器变体 (不含 payload)..." -ForegroundColor Cyan

# 先清掉上一轮留下的变体文件:它会被 csproj 作为嵌入资源引用,
# 编译变体本身时若残留,会把自己嵌进自己(变体一轮比一轮大)。
# csproj 侧已用 EnablePayload=false 排除,这里是第二道保险。
$slimDestForCleanup = Join-Path $desktopDir "installer\NexClip_Uninstall_payload.exe"
if (Test-Path $slimDestForCleanup) {
    Remove-Item $slimDestForCleanup -Force -ErrorAction SilentlyContinue
}

$uninstallerPublishDir = Join-Path $desktopDir "bin\Release\CustomInstallerUninstaller"
dotnet publish "$installerDir\NexClip.Installer.Native.csproj" -c Release -r win-x64 -p:PublishAot=true -p:EnablePayload=false -o $uninstallerPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "卸载器变体 Native AOT 发布失败，退出码 $LASTEXITCODE。"
}

$slimUninstaller = Join-Path $uninstallerPublishDir "NexClip_Setup.exe"
if (!(Test-Path $slimUninstaller)) {
    throw "卸载器变体输出未找到: $slimUninstaller"
}

# 固定路径供 csproj 的 EmbeddedResource 引用,让主安装器把它嵌进 exe。
# 必须压缩存储:变体是原生 exe,直接嵌入会让安装包从 18.99MB 涨到 25.48MB,
# 抵消瘦身收益(安装目录只省 12.5MB,下载却多 6.5MB)。Deflate 实测压缩率约 52%。
$slimDest = Join-Path $desktopDir "installer\NexClip_Uninstall_payload.exe"
$slimRawBytes = [System.IO.File]::ReadAllBytes($slimUninstaller)
$slimCompressedPath = "$slimDest.deflate"
$fs = [System.IO.File]::Create($slimCompressedPath)
try {
    $deflate = New-Object System.IO.Compression.DeflateStream($fs, [System.IO.Compression.CompressionLevel]::SmallestSize, $true)
    try { $deflate.Write($slimRawBytes, 0, $slimRawBytes.Length) } finally { $deflate.Dispose() }
} finally { $fs.Dispose() }
Move-Item $slimCompressedPath $slimDest -Force

$slimRawMB = [Math]::Round($slimRawBytes.Length / 1MB, 2)
$slimStoredMB = [Math]::Round((Get-Item $slimDest).Length / 1MB, 2)
Write-Host ">>>   变体已就位: $slimRawMB MB → 压缩存储 $slimStoredMB MB" -ForegroundColor DarkGray

Write-Host ">>> 4b. Native AOT 纯机器码编译 NexClip.Installer.Native..." -ForegroundColor Cyan
$installerPublishDir = Join-Path $desktopDir "bin\Release\CustomInstallerNative"
dotnet publish "$installerDir\NexClip.Installer.Native.csproj" -c Release -r win-x64 -p:PublishAot=true -o $installerPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "安装器 Native AOT 发布失败，退出码 $LASTEXITCODE。"
}

$installerExe = Join-Path $installerPublishDir "NexClip_Setup.exe"
if (!(Test-Path $installerExe)) {
    throw "安装器单文件输出未找到: $installerExe"
}

$installerSizeMB = [Math]::Round((Get-Item $installerExe).Length / 1MB, 2)
$slimSizeMB = [Math]::Round((Get-Item $slimDest).Length / 1MB, 2)
if ($slimSizeMB -ge $installerSizeMB) {
    throw "卸载器变体($slimSizeMB MB)不小于安装器($installerSizeMB MB)，构建配置可能有误。"
}
Write-Host ">>>   安装器 $installerSizeMB MB / 卸载器 $slimSizeMB MB (安装目录节省 $([Math]::Round($installerSizeMB - $slimSizeMB, 2)) MB)" -ForegroundColor DarkGray

Write-Host ">>> 5. 归档发布安装包..." -ForegroundColor Cyan
Get-Process | Where-Object { $_.ProcessName -like "*NexClip_Setup*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200
if (!(Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

$finalInstallerName = "NexClip_Setup_v$($version)_x64.exe"
$finalInstallerDest = Join-Path $releasesDir $finalInstallerName
try {
    Copy-Item $installerExe $finalInstallerDest -Force -ErrorAction Stop
} catch {
    $timestamp = Get-Date -Format "yyyyMMddHHmmss"
    $finalInstallerName = "NexClip_Setup_v$($version)_$($timestamp)_x64.exe"
    $finalInstallerDest = Join-Path $releasesDir $finalInstallerName
    Copy-Item $installerExe $finalInstallerDest -Force
}

$installerLocalDir = Join-Path $desktopDir "bin\Release\Installer"
if (!(Test-Path $installerLocalDir)) { New-Item -ItemType Directory -Path $installerLocalDir | Out-Null }
try {
    Copy-Item $installerExe (Join-Path $installerLocalDir $finalInstallerName) -Force -ErrorAction SilentlyContinue
} catch {}

$hash = (Get-FileHash $finalInstallerDest -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $finalInstallerDest).Length

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " NexClip 自研 Fluent 现代安装程序构建成功！" -ForegroundColor Green
Write-Host " 产物文件: $finalInstallerName" -ForegroundColor Yellow
Write-Host " 文件大小: $([Math]::Round($size / 1MB, 2)) MB ($size 字节)" -ForegroundColor Yellow
Write-Host " SHA256  : $hash" -ForegroundColor Yellow
Write-Host " 归档路径: $finalInstallerDest" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Green
