# NexClip 自研 Fluent 现代安装器自动化打包脚本
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$desktopDir = Join-Path $root "desktop"
$installerDir = Join-Path $desktopDir "NexClip.Installer.Native"
$resourcesDir = Join-Path $installerDir "Resources"
$releasesDir = "e:\Code\syncclipboard-releases"
$version = "20260828.01"

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
            if (!(Test-Path $destParent)) { New-Item -ItemType Directory -Path $destParent -Force | Out-Null }
            Copy-Item $_.FullName $dest -Force
        }
    }

    # 压缩为 payload.zip (使用最高压缩级别)
    Write-Host ">>> 正在压缩核心 Payload 数据包..." -ForegroundColor Cyan
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempStaging, $payloadZip, [System.IO.Compression.CompressionLevel]::SmallestSize, $false)
    $zipSize = (Get-Item $payloadZip).Length
    Write-Host ">>> Payload 压缩完成，大小: $([Math]::Round($zipSize / 1MB, 2)) MB" -ForegroundColor Green
}
finally {
    if (Test-Path $tempStaging) {
        Remove-Item $tempStaging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ">>> 3. Native AOT 纯机器码编译 NexClip.Installer.Native..." -ForegroundColor Cyan
$installerPublishDir = Join-Path $desktopDir "bin\Release\CustomInstallerNative"
dotnet publish "$installerDir\NexClip.Installer.Native.csproj" -c Release -r win-x64 -p:PublishAot=true -o $installerPublishDir

$installerExe = Join-Path $installerPublishDir "NexClip_Setup.exe"
if (!(Test-Path $installerExe)) {
    throw "安装器单文件输出未找到: $installerExe"
}

Write-Host ">>> 4. 归档发布安装包..." -ForegroundColor Cyan
Get-Process | Where-Object { $_.ProcessName -like "*NexClip_Setup*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200
if (!(Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

$finalInstallerName = "NexClip_Setup_v$($version)_x64.exe"
$finalInstallerDest = Join-Path $releasesDir $finalInstallerName
Copy-Item $installerExe $finalInstallerDest -Force

$installerLocalDir = Join-Path $desktopDir "bin\Release\Installer"
if (!(Test-Path $installerLocalDir)) { New-Item -ItemType Directory -Path $installerLocalDir | Out-Null }
Copy-Item $installerExe (Join-Path $installerLocalDir $finalInstallerName) -Force

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
