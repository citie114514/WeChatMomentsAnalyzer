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

Write-Host "`nRestore and build..." -ForegroundColor Cyan
& $msbuild $proj /p:Configuration=$Config /p:Platform=$Arch /restore
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed" }

# Publish self-contained win-x64 so the runtime folder is always fresh
# 非致命：publish 失败（如 exe 被运行实例锁定）不应阻断 -Run，构建产物同样可运行
Write-Host "`nPublishing self-contained package..." -ForegroundColor Cyan
& dotnet publish $proj -c $Config -r win-x64 -p:Platform=$Arch --self-contained true --no-build
if ($LASTEXITCODE -ne 0) { Write-Warning "Publish failed; will fall back to build output for -Run." }

if ($Run) {
    $base = Join-Path $root "WeChatMomentsAnalyzer\bin\$Arch\$Config\net8.0-windows10.0.19041.0\win-x64"
    $exe = Join-Path $base 'publish\WeChatMomentsAnalyzer.exe'
    if (-not (Test-Path $exe)) { $exe = Join-Path $base 'WeChatMomentsAnalyzer.exe' }
    if (-not (Test-Path $exe)) {
        Write-Error "Executable not found under: $base"
    }
    Write-Host "`nStart: $exe" -ForegroundColor Green
    # Start-Process 异步启动，脚本立即返回，不阻塞终端
    Start-Process -FilePath $exe
}
