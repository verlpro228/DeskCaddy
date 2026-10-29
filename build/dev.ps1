<#
.SYNOPSIS
    开发用构建脚本：关进程 → 编译 → 启动，避免 MSB3027 文件占用。

.DESCRIPTION
    DeskCaddy 运行时会锁住 bin 目录下的 exe/dll，MSBuild 无法覆盖，
    报错形如：
        MSB3027: 无法将 "...\DeskCaddy.dll" 复制到 "...\bin\..."，
        因为另一个程序正在使用此文件。

.PARAMETER Configuration
    Debug 或 Release，默认 Release。

.PARAMETER NoRun
    只编译，不启动。

.PARAMETER Shadow
    从 artifacts\run 的副本启动，而不是 bin 目录。
    这样编译产物不会被运行中的进程占用，可以「边跑边编」。

.EXAMPLE
    .\build\dev.ps1
    .\build\dev.ps1 -NoRun
    .\build\dev.ps1 -Shadow
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRun,

    [switch]$Shadow
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# PATH 上的 dotnet 可能只有运行时，优先用用户目录里装的 SDK
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = 'dotnet'
}

# 1. 关掉正在运行的实例，否则文件被占用
Get-Process DeskCaddy -ErrorAction SilentlyContinue | Stop-Process -Force

# 等待句柄真正释放，否则马上编译仍可能失败
for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-Process DeskCaddy -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 100
}

# 2. 编译
Write-Host "构建中（$Configuration）..." -ForegroundColor Cyan

& $dotnet build (Join-Path $repoRoot 'DeskCaddy.slnx') `
    -c $Configuration `
    -v q `
    -p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    Write-Host '构建失败' -ForegroundColor Red
    exit $LASTEXITCODE
}

if ($NoRun) {
    Write-Host '构建完成（未启动）' -ForegroundColor Green
    return
}

# 3. 启动
$binDir = Join-Path $repoRoot "src\DeskCaddy.App\bin\$Configuration\net10.0-windows10.0.19041.0"

if ($Shadow) {
    # 复制一份再运行：bin 目录保持可写，之后可以边跑边编
    $runDir = Join-Path $repoRoot 'artifacts\run'

    if (Test-Path $runDir) {
        Remove-Item $runDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $runDir | Out-Null
    Copy-Item -Path (Join-Path $binDir '*') -Destination $runDir -Recurse -Force

    $binDir = $runDir
}

Start-Process -FilePath (Join-Path $binDir 'DeskCaddy.exe') -WorkingDirectory $binDir

Write-Host "已启动：$binDir\DeskCaddy.exe" -ForegroundColor Green

if ($Shadow) {
    Write-Host '（影子模式：可以继续编译，不会冲突）' -ForegroundColor DarkGray
}
