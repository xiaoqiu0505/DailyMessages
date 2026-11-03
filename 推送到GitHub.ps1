# 一键推送到 GitHub：xiaoqiu0505/DailyMessages
#
# 用法（二选一）：
#   A. 已经用 gh 登录过：  powershell -ExecutionPolicy Bypass -File .\推送到GitHub.ps1
#   B. 没有 gh：先设置 token 环境变量，再运行本脚本
#        $env:GITHUB_TOKEN = "ghp_你的token"
#        powershell -ExecutionPolicy Bypass -File .\推送到GitHub.ps1
#
# token 生成：github.com -> Settings -> Developer settings ->
#            Personal access tokens -> Tokens (classic) -> Generate new token，勾选 repo

$ErrorActionPreference = 'Stop'
$owner = 'xiaoqiu0505'
$repo  = 'DailyMessages'
$url   = "https://github.com/$owner/$repo.git"

Set-Location $PSScriptRoot

Write-Host "==> 检查远程仓库是否存在 ..." -ForegroundColor Cyan
$exists = $false
try {
    $r = Invoke-WebRequest -Uri "https://api.github.com/repos/$owner/$repo" -UseBasicParsing -TimeoutSec 15
    $exists = ($r.StatusCode -eq 200)
} catch [System.Net.WebException] {
    if ($_.Exception.Response.StatusCode.value__ -eq 404) { $exists = $false }
    else { Write-Host "    无法确认（网络原因），继续尝试推送" -ForegroundColor Yellow }
}

if (-not $exists) {
    Write-Host "==> 仓库不存在，尝试创建 ..." -ForegroundColor Cyan
    $token = $env:GITHUB_TOKEN
    if (-not $token) { $token = $env:GH_TOKEN }

    if ($token) {
        $body = @{ name = $repo; description = 'Windows 托盘待办小助手：5天视图 + 定时提醒 + DDL + 大日历 + 时间表 + 手机同步'; private = $false } | ConvertTo-Json
        Invoke-RestMethod -Uri 'https://api.github.com/user/repos' -Method Post -Body $body `
            -Headers @{ Authorization = "Bearer $token"; 'User-Agent' = 'ps'; Accept = 'application/vnd.github+json' } `
            -ContentType 'application/json' | Out-Null
        Write-Host "    仓库已创建：$url" -ForegroundColor Green
    } elseif (Get-Command gh -ErrorAction SilentlyContinue) {
        gh repo create "$owner/$repo" --public --source=. --remote=origin `
            --description "Windows 托盘待办小助手：5天视图 + 定时提醒 + DDL + 大日历 + 时间表 + 手机同步" 2>&1 | Out-Host
        Write-Host "    已通过 gh 创建仓库" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "仓库还不存在，且没有可用的凭据。" -ForegroundColor Yellow
        Write-Host "请任选一种方式：" -ForegroundColor Yellow
        Write-Host "  1) 浏览器打开 https://github.com/new ，仓库名填 $repo，创建后重新运行本脚本"
        Write-Host "  2) 设置 token 后重新运行：`$env:GITHUB_TOKEN = 'ghp_xxx'"
        Write-Host "  3) 运行 gh auth login 登录后重新运行本脚本"
        exit 1
    }
} else {
    Write-Host "    仓库已存在" -ForegroundColor Green
}

Write-Host "==> 推送 main 分支 ..." -ForegroundColor Cyan
git remote remove origin 2>$null
git remote add origin $url
git push -u origin main

Write-Host ""
Write-Host "完成！仓库地址：$url" -ForegroundColor Green
