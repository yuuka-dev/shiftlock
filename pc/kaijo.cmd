@echo off
rem kaijo.cmd : emergency release of enforce mode (creates STOP.txt, aborts pending shutdown)
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0guard.ps1" -Release
echo.
pause
