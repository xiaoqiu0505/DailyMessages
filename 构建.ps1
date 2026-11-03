# ============================================================
#  构建脚本：把源码发布成单个 exe（release\TodoAssistant.exe）
#  需要 .NET 10 SDK。用法：右键“使用 PowerShell 运行”
# ============================================================
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "[1/3] 生成图标..." -ForegroundColor Cyan
$py = "C:\Users\86135\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"
if (Test-Path $py) {
    & $py (Join-Path $root "tools\make_icon.py")
    & $py (Join-Path $root "tools\make_web_icons.py")
    Copy-Item (Join-Path $root "assets\app.ico") (Join-Path $root "src\assets\app.ico") -Force
} else {
    Write-Host "  未找到 Python/Pillow，跳过图标生成（沿用已有图标）" -ForegroundColor Yellow
}

Write-Host "[2/3] 停止正在运行的程序..." -ForegroundColor Cyan
Get-Process TodoAssistant -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "[3/3] 编译并打包..." -ForegroundColor Cyan
Push-Location (Join-Path $root "src")
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o (Join-Path $root "release") -v q --nologo
Pop-Location

$exe = Join-Path $root "release\TodoAssistant.exe"
if (Test-Path $exe) {
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "完成！ release\TodoAssistant.exe ($mb MB)" -ForegroundColor Green
    Write-Host "把这个 exe 和 安装.bat 放在同一个文件夹里，双击安装即可。" -ForegroundColor Green
} else {
    Write-Host "打包失败，请检查上面的错误信息。" -ForegroundColor Red
}
