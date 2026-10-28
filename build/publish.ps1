<#
.SYNOPSIS
    发布 DeskCaddy 并（可选）生成安装包。

.PARAMETER Mode
    selfcontained  自带运行时，安装包约 60-80MB，用户无需装 .NET
    framework      依赖系统 .NET 10 桌面运行时，约 5MB，但用户必须先装运行时

.PARAMETER SkipInstaller
    只发布，不打安装包（没装 Inno Setup 时用这个）

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -Mode framework -SkipInstaller
#>
param(
    [ValidateSet('selfcontained', 'framework')]
    [string]$Mode = 'selfcontained',

    [string]$Runtime = 'win-x64',

    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DeskCaddy.App\DeskCaddy.App.csproj'
$outDir = Join-Path $repoRoot 'artifacts\publish'

# PATH 上的 dotnet 可能只有运行时，优先用用户目录里装的 SDK
$script:dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $script:dotnet)) {
    $script:dotnet = 'dotnet'
}

# 运行中的程序会锁住 exe，导致发布失败
Get-Process DeskCaddy -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $outDir,
    '-p:PublishSingleFile=false',   # 单文件会在运行时解压到临时目录，容易被杀软当成释放器
    '-p:PublishReadyToRun=true',
    '-v', 'minimal'
)

if ($Mode -eq 'selfcontained') {
    $arguments += '--self-contained'
    $arguments += 'true'
} else {
    $arguments += '--self-contained'
    $arguments += 'false'
}

Write-Host "发布中（$Mode）..." -ForegroundColor Cyan
& $dotnet @arguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

$exe = Join-Path $outDir 'DeskCaddy.exe'
Write-Host "已发布：$exe"

if ($SkipInstaller) {
    Write-Host '跳过安装包（-SkipInstaller）'
    return
}

# 找 Inno Setup 编译器（兼容按用户安装到 %LocalAppData% 的情况）
$isccCandidates = @(@(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ })

if ($isccCandidates.Count -eq 0) {
    Write-Warning '未找到 ISCC.exe（Inno Setup）。安装后可重新运行本脚本生成安装包。'
    Write-Warning '下载：https://jrsoftware.org/isdl.php'
    return
}

$iscc = $isccCandidates[0]
$script = Join-Path $PSScriptRoot 'DeskCaddy.iss'

# 安装包版本必须跟 csproj 一致，避免手改 .iss 时漏掉
$version = (Select-String -Path $project -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value

Write-Host "打包安装程序：$iscc（版本 $version）" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" $script

if ($LASTEXITCODE -ne 0) {
    throw "ISCC 失败，退出码 $LASTEXITCODE"
}

Write-Host '完成。安装包在 artifacts\installer\' -ForegroundColor Green
