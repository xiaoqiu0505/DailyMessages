@echo off
chcp 65001 >nul
setlocal
title 安装待办小助手

set "SRC=%~dp0%EXENAME%"
if not defined EXENAME set "EXENAME=TodoAssistant.exe"
set "SRC=%~dp0release\TodoAssistant.exe"
if not exist "%SRC%" set "SRC=%~dp0TodoAssistant.exe"
set "APPDIR=%LOCALAPPDATA%\Programs\TodoAssistant"
set "EXE=%APPDIR%\TodoAssistant.exe"

echo.
echo  ============================================
echo    待办小助手 · 安装到本机
echo  ============================================
echo.

if not exist "%SRC%" (
    echo  [X] 找不到 TodoAssistant.exe
    echo      请把 exe 放在 release 文件夹里，或和本脚本放在同一目录。
    pause
    exit /b 1
)

echo  [1/5] 正在关闭正在运行的程序...
taskkill /IM TodoAssistant.exe /F >nul 2>&1
timeout /t 1 >nul

echo  [2/5] 复制程序到: %APPDIR%
if not exist "%APPDIR%" mkdir "%APPDIR%"
copy /Y "%SRC%" "%EXE%" >nul
if errorlevel 1 (
    echo  [X] 复制失败，请检查权限。
    pause
    exit /b 1
)

echo  [3/5] 注册 Windows 通知...
"%EXE%" --repair >nul 2>&1

echo  [4/5] 添加手机访问的防火墙规则（需要管理员确认）...
netsh advfirewall firewall delete rule name="待办小助手 手机同步" >nul 2>&1
netsh advfirewall firewall add rule name="待办小助手 手机同步" dir=in action=allow program="%EXE%" protocol=TCP localport=8974 profile=private,domain enable=yes >nul 2>&1
if errorlevel 1 (
    echo       [跳过] 未获得管理员权限（手机访问时如弹窗请选"允许"）
) else (
    echo       完成
)

echo  [5/5] 启动程序（自动开启开机自启）...
start "" "%EXE%"

echo.
echo  ============================================
echo    安装完成！
echo    - 程序位置：%APPDIR%
echo    - 托盘图标：屏幕右下角，左键单击打开待办窗口
echo  ============================================

powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell);$p=Join-Path ([Environment]::GetFolderPath('Desktop')) '待办小助手.lnk';$l=$s.CreateShortcut($p);$l.TargetPath='%EXE%';$l.WorkingDirectory='%APPDIR%';$l.IconLocation='%EXE%,0';$l.Description='待办与提醒';$l.Save()" >nul 2>&1

timeout /t 3 >nul
exit /b 0
