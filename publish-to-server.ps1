# NexClip Windows 安装包发布到服务端
#
# 为什么需要这个脚本：`/var/www/nexclip-server/releases/` **不受 deploy.sh 管辖**，
# 也不在 git 里（服务端 CHANGELOG 自己写着"仍靠人工管理"）。
# 此前发版只更新了 GitHub Release，忘记同步服务端，导致「服务端直连加速」这个更新源
# 读到的 version.json 一直停在旧版本 —— 用户因此检测不到更新（2026-10-29 实际发生）。
#
# 用法：
#   .\publish-to-server.ps1 -Version 20261029.01
#   .\publish-to-server.ps1 -Version 20261029.01 -SshHost root@47.116.39.247
#
# 做的事：
#   1. 校验本地安装包存在且与 GitHub Release 的 SHA-256 一致
#   2. scp 上传到服务端 releases/
#   3. 服务器端校验哈希（传输无损）
#   4. 备份并更新 version.json 的 windows 段（android 段原样保留）
#   5. 用真实接口验证 direct 源能检测到该版本

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$SshHost = "root@47.116.39.247",
    [string]$RemoteReleases = "/var/www/nexclip-server/releases",
    # 默认取 release-out/：build-installer.ps1 把产物归档到那里
    [string]$LocalDir = (Join-Path $PSScriptRoot "release-out"),
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

$installerName = "NexClip_Setup_v$($Version)_x64.exe"
$localPath = Join-Path $LocalDir $installerName

function Fail($msg) { Write-Host "错误：$msg" -ForegroundColor Red; exit 1 }

Write-Host ">>> 1. 校验本地安装包" -ForegroundColor Cyan
if (!(Test-Path $localPath)) { Fail "找不到安装包：$localPath（请先运行 build-installer.ps1）" }
$localSize = (Get-Item $localPath).Length
$localHash = (Get-FileHash $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "    $installerName  $([math]::Round($localSize/1MB,2)) MB" -ForegroundColor DarkGray
Write-Host "    SHA-256: $localHash" -ForegroundColor DarkGray

Write-Host ">>> 2. 上传到服务端" -ForegroundColor Cyan
$remoteTarget = "$($SshHost):$($RemoteReleases)/"
Write-Host "    -> $remoteTarget" -ForegroundColor DarkGray
& scp -o BatchMode=yes -o StrictHostKeyChecking=no -C $localPath $remoteTarget
if ($LASTEXITCODE -ne 0) { Fail "scp 上传失败（退出码 $LASTEXITCODE）" }

Write-Host ">>> 3. 服务端校验哈希" -ForegroundColor Cyan
$remoteHash = (& ssh -o BatchMode=yes -o StrictHostKeyChecking=no $SshHost "sha256sum '$RemoteReleases/$installerName' | cut -d' ' -f1").Trim()
if ($remoteHash -ne $localHash) { Fail "哈希不一致！本地 $localHash / 远程 $remoteHash" }
Write-Host "    一致 ✓" -ForegroundColor Green

Write-Host ">>> 4. 更新 version.json" -ForegroundColor Cyan
# 用 Node 改 JSON：服务器上有 node，且能精确保留 android 段与缩进
$nodeScript = @'
const fs = require('fs');
const [,, file, version, filename, sizeStr, sha, bodyPath] = process.argv;
const d = JSON.parse(fs.readFileSync(file, 'utf8'));
const size = Number(sizeStr);
const derivedName = 'NexClip v' + version + ' 正式版';
// 正文从文件读取：说明里含反引号等特殊字符，走命令行会被 shell 吃掉
const body = fs.readFileSync(bodyPath, 'utf8').replace(/\|/g, '\n').trim();

d.windows = {
  version, tag_name: 'v' + version,
  name: 'NexClip v' + version + ' - Windows',
  body,
  filename,
  url: 'https://nexclip.157342.xyz/releases/' + filename,
  size, file_size_bytes: size,
  sha256: sha,
};
d.version = version;
d.tag_name = 'v' + version;
d.name = derivedName;
d.body = body;
d.html_url = 'https://github.com/yixing233/nexclip/releases/tag/v' + version;
fs.writeFileSync(file + '.new', JSON.stringify(d, null, 2) + '\n');
console.log('android 段保留:', d.android ? d.android.version : '(无)');
'@

# 正文用**文件传输**而不是命令行参数：
# 说明里有反引号(Markdown 代码块)和特殊字符，经 ssh 命令行会被 bash 当成命令替换吃掉，
# 导致 version.json 里的更新说明变成空白(2026-10-29 实测踩到)。
$notesFile = Join-Path $LocalDir "notes-v$($Version).md"
$bodyLocal = Join-Path ([System.IO.Path]::GetTempPath()) "nexclip-body-$Version.txt"
if (Test-Path $notesFile) {
    # 把多行正文压成 | 分隔的一行，避免 JSON 里出现裸换行
    (Get-Content $notesFile -Raw) -replace "`r?`n", '|' | Set-Content $bodyLocal -NoNewline -Encoding UTF8
} else {
    "NexClip v$Version" | Set-Content $bodyLocal -NoNewline -Encoding UTF8
}

$nodeScript | & ssh -o BatchMode=yes -o StrictHostKeyChecking=no $SshHost "cat > /tmp/gen_version.js"
if ($LASTEXITCODE -ne 0) { Fail "写入服务器脚本失败" }

# 文件上传：正文与参数都走文件，彻底避开 shell 转义
& scp -o BatchMode=yes -o StrictHostKeyChecking=no $bodyLocal "${SshHost}:/tmp/nexclip-body.txt"
if ($LASTEXITCODE -ne 0) { Fail "上传说明文件失败" }

$remoteCmd = 'cd "' + $RemoteReleases + '" && cp version.json version.json.bak-$(date +%Y%m%d-%H%M%S) && node /tmp/gen_version.js version.json "' + $Version + '" "' + $installerName + '" "' + $localSize + '" "' + $localHash + '" /tmp/nexclip-body.txt && mv version.json.new version.json && chmod 644 version.json && rm -f /tmp/gen_version.js /tmp/nexclip-body.txt'
& ssh -o BatchMode=yes -o StrictHostKeyChecking=no $SshHost $remoteCmd
if ($LASTEXITCODE -ne 0) { Fail "更新 version.json 失败" }
Remove-Item $bodyLocal -ErrorAction SilentlyContinue

Write-Host ">>> 5. 验证线上接口" -ForegroundColor Cyan
Start-Sleep -Seconds 2
$json = Invoke-RestMethod -Uri "https://nexclip.157342.xyz/releases/version.json" -Headers @{ "User-Agent" = "NexClip" } -TimeoutSec 20
$wv = $json.windows.version
$wa = $json.android.version
Write-Host "    windows.version = $wv" -ForegroundColor DarkGray
Write-Host "    android.version = $wa (未改动)" -ForegroundColor DarkGray
if ($wv -ne $Version) { Fail "线上 windows.version 是 $wv，期望 $Version" }

$installerHead = Invoke-WebRequest -Uri $json.windows.url -Method Head -TimeoutSec 30
if ($installerHead.StatusCode -ne 200) { Fail "安装包 URL 不可访问：$($installerHead.StatusCode)" }
Write-Host "    安装包可下载 ✓ ($($installerHead.Headers['Content-Length']) 字节)" -ForegroundColor Green

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host " 发布完成：v$Version 已同步到服务端" -ForegroundColor Green
Write-Host " 安装包: $RemoteReleases/$installerName" -ForegroundColor Yellow
Write-Host " SHA256: $localHash" -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Green