# Windows 正式包构建。不自动执行 git 提交、推送或发布。
param([switch]$RunWindowsSmoke)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $version = "1.1.0"
    $outDir = "publish/release-win-x64"
    $zip = "publish/MuseLauncher-win-x64-v$version.zip"
    Write-Host "== Core + loopback 协议回归 ==" -ForegroundColor Cyan
    dotnet test tests/MuseLauncher.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "回归测试失败，停止打包" }
    Write-Host "== 编译 Windows WebView2 冒烟入口 ==" -ForegroundColor Cyan
    dotnet build tests/MuseLauncher.WindowsSmoke -c Release
    if ($LASTEXITCODE -ne 0) { throw "Windows 冒烟入口编译失败" }
    if ($RunWindowsSmoke) {
        dotnet run --project tests/MuseLauncher.WindowsSmoke -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Windows 冒烟测试失败，停止打包" }
    } else {
        Write-Warning "未运行 Windows WebView2 冒烟测试"
    }
    Write-Host "== 正式包 ==" -ForegroundColor Cyan
    dotnet publish src/MuseLauncher -c Release -r win-x64 --self-contained true -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "发布构建失败" }
    if (Test-Path $zip) { throw "发布包已存在，请另存或确认后手动移除：$zip" }
    Compress-Archive -Path $outDir -DestinationPath $zip
    Write-Host "完成：$zip" -ForegroundColor Green
} finally {
    Pop-Location
}
