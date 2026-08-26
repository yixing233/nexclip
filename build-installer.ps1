# NexClip 自研 Fluent 现代安装器自动化打包脚本
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$desktopDir = Join-Path $root "desktop"
$installerDir = Join-Path $desktopDir "NexClip.Installer"
$resourcesDir = Join-Path $installerDir "Resources"
$releasesDir = "e:\Code\syncclipboard-releases"
$version = "20260825.02"

Write-Host ">>> 1. 编译 NexClip.Desktop 主程序 (Release win-x64)..." -ForegroundColor Cyan
$mainAppOutput = Join-Path $desktopDir "bin\Release\net9.0-windows10.0.19041.0\win-x64"
dotnet build "$desktopDir\NexClip.Desktop.csproj" -c Release

if (!(Test-Path $mainAppOutput)) {
    throw "主程序编译产物目录未找到: $mainAppOutput"
}

Write-Host ">>> 2. 准备 Payload 压缩包..." -ForegroundColor Cyan
if (!(Test-Path $resourcesDir)) {
    New-Item -ItemType Directory -Path $resourcesDir | Out-Null
}

$payloadZip = Join-Path $resourcesDir "payload.zip"
if (Test-Path $payloadZip) {
    Remove-Item $payloadZip -Force
}

$tempStaging = Join-Path ([System.IO.Path]::GetTempPath()) "NexClip_Staging_$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempStaging | Out-Null

try {
    # 过滤拷贝主程序文件
    $excludePatterns = @("*.pdb", "publish*", "*onnxruntime*", "*DirectML*", "*NPUDetect*", "*NpuDetect*", "*Microsoft.Windows.Widgets*")
    
    Get-ChildItem -Path $mainAppOutput -Recurse | ForEach-Object {
        $relPath = $_.FullName.Substring($mainAppOutput.Length).TrimStart('\', '/')
        
        $shouldExclude = $false
        foreach ($pat in $excludePatterns) {
            if ($relPath -like $pat -or $_.Name -like $pat) {
                $shouldExclude = $true
                break
            }
        }
        
        if (!$shouldExclude) {
            $destPath = Join-Path $tempStaging $relPath
            if ($_.PSIsContainer) {
                if (!(Test-Path $destPath)) { New-Item -ItemType Directory -Path $destPath | Out-Null }
            } else {
                $destParent = Split-Path -Parent $destPath
                if (!(Test-Path $destParent)) { New-Item -ItemType Directory -Path $destParent | Out-Null }
                Copy-Item $_.FullName $destPath -Force
            }
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

Write-Host ">>> 3. 单文件自包含发布编译 NexClip.Installer..." -ForegroundColor Cyan
$installerPublishDir = Join-Path $desktopDir "bin\Release\CustomInstaller"
dotnet publish "$installerDir\NexClip.Installer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $installerPublishDir

$installerExe = Join-Path $installerPublishDir "NexClip_Setup.exe"
if (!(Test-Path $installerExe)) {
    throw "安装器单文件输出未找到: $installerExe"
}

Write-Host ">>> 4. 归档发布安装包..." -ForegroundColor Cyan
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
