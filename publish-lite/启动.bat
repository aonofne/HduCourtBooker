@echo off
chcp 65001 >nul
title 杭电羽毛球自动预约 - 启动器
cd /d "%~dp0"

echo ============================================================
echo   杭电羽毛球自动预约 - 启动器
echo ============================================================
echo.

if not exist "HduCourtBooker.exe" (
    echo [错误] 当前目录没有 HduCourtBooker.exe
    echo        请把本文件放在与 HduCourtBooker.exe 同一个目录下。
    echo.
    pause
    exit /b 1
)

echo [1/3] 解除"来自网络"的锁定（需要时）...
powershell -NoProfile -Command "Get-ChildItem -Path '%~dp0' -Filter *.exe | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1
if errorlevel 1 echo       跳过（无 PowerShell 或无需解除）

echo [2/3] 启动主程序...
echo       若窗口一闪而过，下面会显示原因，并生成 crash-*.log
echo.

start "" /wait "HduCourtBooker.exe"
set RC=%ERRORLEVEL%

echo.
echo [3/3] 程序已退出，返回码 = %RC%
if not "%RC%"=="0" (
    echo.
    echo 返回码不是 0，说明启动/运行过程中出错了。请检查以下位置的 crash-*.log：
    echo     %%LOCALAPPDATA%%\HduCourtBooker\
    echo     %~dp0
    echo 把这个文件发给我即可定位。
)
echo.
echo 数据与日志目录：
echo     %%LOCALAPPDATA%%\HduCourtBooker\
echo.
pause
