@echo off
REM ============================================================
REM  Steam++ Accelerator (proxy-only build) launcher
REM
REM  Usage:
REM    launch.bat              dns mode (default, needs admin)
REM    launch.bat system       system proxy mode
REM    launch.bat proxyonly    local proxy only, no system change
REM    launch.bat console      any mode, but keep the window visible
REM
REM  Once running, the program hides its console and moves to the
REM  system tray. Right-click the tray icon to show the console or
REM  to exit. Ctrl+C is no longer the exit path (the window is gone).
REM
REM  Edit accelerate-rules.json in %LOCALAPPDATA%\Steam++Accelerator
REM  to change which rules are enabled.
REM ============================================================
setlocal

REM  first arg "console" keeps the window visible (for debugging)
set "KEEP="
if /i "%~1"=="console" (
    set "KEEP=--console"
    shift
)

set "MODE=%~1"
if "%MODE%"=="" set "MODE=dns"

set "EXE=%~dp0Steam++.Accelerator.exe"
if not exist "%EXE%" (
    echo Cannot find %EXE%
    pause
    exit /b 1
)

if /i "%MODE%"=="proxyonly" goto :launch

REM dns / system / pac / hosts need administrator rights
net session >nul 2>&1
if errorlevel 1 (
    echo Administrator rights required, requesting elevation...
    powershell -NoProfile -Command "Start-Process -FilePath '%EXE%' -ArgumentList '--mode','%MODE%','%KEEP%' -Verb RunAs"
    exit /b 0
)

:launch
REM IMPORTANT: launch detached via "start" so the program gets its own
REM console window. Running it synchronously (the previous way) shared
REM this window with cmd.exe, so GetConsoleProcessList returned 2, and
REM the program deliberately refuses to hide a console it does not own -
REM the window stayed on screen and Ctrl+C was the only way out.
start "" "%EXE%" --mode %MODE% %KEEP%
exit /b 0
