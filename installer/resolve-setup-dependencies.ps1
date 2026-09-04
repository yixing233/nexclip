<#
.SYNOPSIS
    校验并解析安装器运行环境依赖清单 setup-dependencies.json。
.DESCRIPTION
    构建安装器前调用；确保每个依赖都使用受信任的 Microsoft HTTPS 下载地址与合法的 SHA-256，
    并返回可直接展开为 dotnet publish -p:Key=Value 的属性集合。
#>
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot "setup-dependencies.json"),
    [string]$WindowsAppSdkPackageVersion = "",
    [switch]$VerifyRemoteSize
)

$ErrorActionPreference = "Stop"

$approvedHosts = @(
    "download.microsoft.com",
    "download.visualstudio.microsoft.com",
    "builds.dotnet.microsoft.com"
)
$approvedFallbackHosts = @("aka.ms", "dotnet.microsoft.com", "learn.microsoft.com")

$manifest = [System.IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "依赖清单未找到: $manifest"
}

$dependencies = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$dependencies.schemaVersion -ne 1) {
    throw "依赖清单 schemaVersion 不受支持: $($dependencies.schemaVersion)"
}

function Assert-Uri {
    param(
        [string]$Value,
        [string]$Name,
        [string[]]$AllowedHosts
    )
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne "https") {
        throw "$Name 必须是绝对 HTTPS 地址。"
    }
    if ($AllowedHosts -notcontains $uri.Host) {
        throw "$Name 的主机 $($uri.Host) 不在允许的 Microsoft 下载域名列表中。"
    }
    return $uri.AbsoluteUri
}

function Assert-Sha256 {
    param([string]$Value, [string]$Name)
    if ([string]$Value -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$Name 缺少合法的 SHA-256 校验值。"
    }
    return ([string]$Value).ToLowerInvariant()
}

function Assert-Version {
    param([string]$Value, [string]$Name)
    $parsed = $null
    if (-not [Version]::TryParse($Value, [ref]$parsed)) {
        throw "$Name 的版本号格式无效: $Value"
    }
    return $parsed
}

function Resolve-Dependency {
    param([object]$Dependency, [string]$Name)

    if ($null -eq $Dependency) {
        throw "依赖清单缺少 $Name 节点。"
    }
    if ([string]::IsNullOrWhiteSpace([string]$Dependency.fileName)) {
        throw "$Name 缺少 fileName。"
    }
    if ([string]::IsNullOrWhiteSpace([string]$Dependency.silentArguments)) {
        throw "$Name 缺少静默安装参数。"
    }
    if ([string]::IsNullOrWhiteSpace([string]$Dependency.repairArguments)) {
        throw "$Name 缺少修复安装参数。"
    }
    if ([long]$Dependency.sizeBytes -le 0) {
        throw "$Name 的 sizeBytes 必须为正数。"
    }

    $url = Assert-Uri -Value ([string]$Dependency.url) -Name "$Name.url" -AllowedHosts $approvedHosts
    $fallback = Assert-Uri -Value ([string]$Dependency.fallbackUrl) -Name "$Name.fallbackUrl" `
        -AllowedHosts ($approvedHosts + $approvedFallbackHosts)
    if ($url -eq $fallback) {
        throw "$Name 的备用下载源不能与主源相同。"
    }
    $manualPage = Assert-Uri -Value ([string]$Dependency.manualDownloadPage) -Name "$Name.manualDownloadPage" `
        -AllowedHosts ($approvedHosts + $approvedFallbackHosts)
    $sha256 = Assert-Sha256 -Value ([string]$Dependency.sha256) -Name "$Name.sha256"
    $minimum = Assert-Version -Value ([string]$Dependency.minimumVersion) -Name "$Name.minimumVersion"

    if ($VerifyRemoteSize) {
        $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 60
        $remoteLength = [long]($response.Headers['Content-Length'] | Select-Object -First 1)
        if ($remoteLength -gt 0 -and $remoteLength -ne [long]$Dependency.sizeBytes) {
            throw "$Name 的远端大小 $remoteLength 与清单声明的 $($Dependency.sizeBytes) 不一致，请重新固定版本与哈希。"
        }
    }

    return [pscustomobject]@{
        Url = $url
        FallbackUrl = $fallback
        ManualDownloadPage = $manualPage
        Sha256 = $sha256
        MinimumVersion = $minimum.ToString()
        SizeBytes = [long]$Dependency.sizeBytes
    }
}

$visualCpp = Resolve-Dependency -Dependency $dependencies.visualCppRuntime -Name "visualCppRuntime"
$dotNet = Resolve-Dependency -Dependency $dependencies.dotNetDesktopRuntime -Name "dotNetDesktopRuntime"
$windowsAppRuntime = Resolve-Dependency -Dependency $dependencies.windowsAppRuntime -Name "windowsAppRuntime"

if ([string]$dependencies.dotNetDesktopRuntime.version -notmatch '^9\.0\.\d+$') {
    throw "安装器必须固定到稳定的 .NET 9 Desktop Runtime 补丁版本。"
}
if ([int]$dependencies.dotNetDesktopRuntime.majorVersion -ne 9) {
    throw "dotNetDesktopRuntime.majorVersion 必须为 9。"
}
if ([string]$dependencies.windowsAppRuntime.packageName -ne "Microsoft.WindowsAppRuntime.1.8") {
    throw "Windows App Runtime 框架包名称无效。"
}
if ([string]$dependencies.windowsAppRuntime.mainPackageName -ne "MicrosoftCorporationII.WinAppRuntime.Main.1.8") {
    throw "Windows App Runtime Main 包名称无效（非打包应用需要它注册 DDLM）。"
}

$packageVersion = Assert-Version -Value ([string]$dependencies.windowsAppRuntime.packageVersion) -Name "windowsAppRuntime.packageVersion"
$minimumPackageVersion = [Version]$windowsAppRuntime.MinimumVersion
if ($packageVersion -lt $minimumPackageVersion) {
    throw "windowsAppRuntime.packageVersion $packageVersion 低于 minimumVersion $minimumPackageVersion。"
}

if (-not [string]::IsNullOrWhiteSpace($WindowsAppSdkPackageVersion) -and
    [string]$dependencies.windowsAppRuntime.sdkPackageVersion -ne $WindowsAppSdkPackageVersion) {
    throw "清单中的 Windows App SDK 版本 $($dependencies.windowsAppRuntime.sdkPackageVersion) 与项目引用的 $WindowsAppSdkPackageVersion 不一致。"
}

return [pscustomobject]@{
    VisualCppInstallerUrl = $visualCpp.Url
    VisualCppInstallerFallbackUrl = $visualCpp.FallbackUrl
    VisualCppInstallerSha256 = $visualCpp.Sha256
    VisualCppInstallerSizeBytes = $visualCpp.SizeBytes
    VisualCppManualDownloadPage = $visualCpp.ManualDownloadPage
    VisualCppMinimumVersion = $visualCpp.MinimumVersion
    DotNetDesktopInstallerUrl = $dotNet.Url
    DotNetDesktopInstallerFallbackUrl = $dotNet.FallbackUrl
    DotNetDesktopInstallerSha256 = $dotNet.Sha256
    DotNetDesktopInstallerSizeBytes = $dotNet.SizeBytes
    DotNetDesktopManualDownloadPage = $dotNet.ManualDownloadPage
    DotNetDesktopMinimumVersion = $dotNet.MinimumVersion
    WindowsAppRuntimeInstallerUrl = $windowsAppRuntime.Url
    WindowsAppRuntimeInstallerFallbackUrl = $windowsAppRuntime.FallbackUrl
    WindowsAppRuntimeInstallerSha256 = $windowsAppRuntime.Sha256
    WindowsAppRuntimeInstallerSizeBytes = $windowsAppRuntime.SizeBytes
    WindowsAppRuntimeManualDownloadPage = $windowsAppRuntime.ManualDownloadPage
    WindowsAppRuntimeMinimumVersion = $windowsAppRuntime.MinimumVersion
    WindowsAppRuntimePackageName = [string]$dependencies.windowsAppRuntime.packageName
    WindowsAppRuntimeMainPackageName = [string]$dependencies.windowsAppRuntime.mainPackageName
}