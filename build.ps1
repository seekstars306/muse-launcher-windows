# Muse 登录器 (Windows) 构建脚本 —— 在 Windows 本机运行
# 用法: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"

$version = "1.0.1"
$outDir = "publish/win-x64"
$zip = "publish/MuseLauncher-win-x64-v$version.zip"

Write-Host "== 单元测试 ==" -ForegroundColor Cyan
dotnet test tests/MuseLauncher.Core.Tests
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== 发布 (self-contained win-x64) ==" -ForegroundColor Cyan
dotnet publish src/MuseLauncher -c Release -r win-x64 --self-contained true -o $outDir
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== 打包 ==" -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $outDir -DestinationPath $zip
Write-Host "完成: $zip" -ForegroundColor Green
