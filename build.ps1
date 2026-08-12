# Build and optionally run WeChatMomentsAnalyzer
# Usage:
#   .\build.ps1              # restore + build (Debug x64)
#   .\build.ps1 -Run         # build and run
#   .\build.ps1 -Config Release -Arch x64

param(
    [ValidateSet('Debug','Release')]
    [string]$Config = 'Debug',

    [ValidateSet('x64','x86')]
    [string]$Arch = 'x64',

    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'WeChatMomentsAnalyzer\WeChatMomentsAnalyzer.csproj'

Write-Host "Project: $proj" -ForegroundColor Cyan
Write-Host "Configuration: $Config | Platform: $Arch" -ForegroundColor Cyan

# Prefer VS Build Tools MSBuild, which handles WinUI 3 XAML correctly
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { $msbuild = $cmd.Source }
}
if (-not $msbuild) {
    Write-Error "MSBuild not found. Install Visual Studio Build Tools 2022 or add MSBuild.exe to PATH."
}

# 先结束正在运行的实例，避免 exe 被锁导致 build/publish 复制失败（-Run 中断的常见原因）
Stop-Process -Name WeChatMomentsAnalyzer -Force -ErrorAction SilentlyContinue

Write-Host "`nRestore and build..." -ForegroundColor Cyan
& $msbuild $proj /p:Configuration=$Config /p:Platform=$Arch /restore
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed" }

# Publish self-contained win-x64 so the runtime folder is always fresh
# 非致命：publish 失败（如 exe 被运行实例锁定）不应阻断 -Run，构建产物同样可运行
Write-Host "`nPublishing framework-dependent package..." -ForegroundColor Cyan
& dotnet publish $proj -c $Config -r win-x64 -p:Platform=$Arch --self-contained false --no-build
if ($LASTEXITCODE -ne 0) { Write-Warning "Publish failed; will fall back to build output for -Run." }

if ($Run) {
    # 在 bin 下递归查找最新构建的 exe，兼容 build/publish 不同输出路径与 RID/Platform 子目录
    $binRoot = Join-Path $root "WeChatMomentsAnalyzer\bin"
    $candidates = @()
    if (Test-Path $binRoot) {
        $candidates = @(Get-ChildItem -Path $binRoot -Recurse -Filter 'WeChatMomentsAnalyzer.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\obj\\' })
    }
    if ($candidates.Count -eq 0) {
        Write-Error "未在 $binRoot 下找到 WeChatMomentsAnalyzer.exe，请确认构建成功。"
    }
    # 优先非 publish 产物（刚构建的最新），publish 目录仅作兜底；同组取最新写入时间
    $exe = $candidates | Where-Object { $_.FullName -notmatch '\\publish\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) {
        $exe = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    Write-Host "`nStart: $($exe.FullName)" -ForegroundColor Green
    # Start-Process 异步启动，脚本立即返回，不阻塞终端
    Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
}
