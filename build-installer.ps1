# NexClip 自研 Fluent 现代安装器自动化打包脚本
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$desktopDir = Join-Path $root "desktop"
$installerDir = Join-Path $desktopDir "NexClip.Installer.Native"
$resourcesDir = Join-Path $installerDir "Resources"
$releasesDir = "e:\Code\syncclipboard-releases"
$version = "20260904.01"

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

Write-Host ">>> 4. Native AOT 纯机器码编译 NexClip.Installer.Native..." -ForegroundColor Cyan
$installerPublishDir = Join-Path $desktopDir "bin\Release\CustomInstallerNative"
dotnet publish "$installerDir\NexClip.Installer.Native.csproj" -c Release -r win-x64 -p:PublishAot=true -o $installerPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "安装器 Native AOT 发布失败，退出码 $LASTEXITCODE。"
}

$installerExe = Join-Path $installerPublishDir "NexClip_Setup.exe"
if (!(Test-Path $installerExe)) {
    throw "安装器单文件输出未找到: $installerExe"
}

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
