@echo off
chcp 65001 >nul
setlocal
title 待办小助手 · 自检

echo.
echo  正在检查：数据存储 / 通知 / 铃声 / 手机同步 / 二维码 ...
echo.

set "EXE=%~dp0release\TodoAssistant.exe"
if not exist "%EXE%" set "EXE=%~dp0TodoAssistant.exe"
if not exist "%EXE%" set "EXE=%LOCALAPPDATA%\Programs\TodoAssistant\TodoAssistant.exe"

if not exist "%EXE%" (
    echo  [X] 找不到 TodoAssistant.exe
    pause
    exit /b 1
)

"%EXE%" --selfcheck

echo.
echo  结果同时保存到: %LOCALAPPDATA%\TodoAssistant\selfcheck.txt
echo  日志文件:      %LOCALAPPDATA%\TodoAssistant\app.log
echo.
pause
exit /b 0
