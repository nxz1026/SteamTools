@echo off
REM Steam++ Accelerator launcher (system mode)
REM Double-click to run; no UAC needed (root cert already installed).
REM Black window auto-hides; tray icon appears at bottom-right.
REM To stop: right-click tray icon -> Exit.
REM For dns mode (deeper coverage, needs admin): change --mode system to --mode dns
set "EXE=C:\SteamToolsAccelerator\Steam++.Accelerator.exe"
if not exist "%EXE%" (
    echo Cannot find %EXE%
    pause
    exit /b 1
)
start "" "%EXE%" --mode system
exit /b 0
