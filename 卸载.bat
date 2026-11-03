@echo off
chcp 65001 >nul
setlocal
title 卸载待办小助手

set "APPDIR=%LOCALAPPDATA%\Programs\TodoAssistant"

echo.
echo  卸载待办小助手
echo  （待办数据不会被删除，仍保存在 %LOCALAPPDATA%\TodoAssistant）
echo.

taskkill /IM TodoAssistant.exe /F >nul 2>&1
timeout /t 1 >nul
if exist "%APPDIR%\TodoAssistant.exe" "%APPDIR%\TodoAssistant.exe" --uninstall-startup >nul 2>&1

del /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\待办小助手.lnk" >nul 2>&1
del /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\待办小助手.lnk" >nul 2>&1
del /Q "%USERPROFILE%\Desktop\待办小助手.lnk" >nul 2>&1
netsh advfirewall firewall delete rule name="待办小助手 手机同步" >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "待办小助手" /f >nul 2>&1

timeout /t 2 >nul
rd /S /Q "%APPDIR%" >nul 2>&1

echo  已卸载（数据仍保留在 %LOCALAPPDATA%\TodoAssistant，可手动删除）
echo.
pause
exit /b 0
