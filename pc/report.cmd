@echo off
rem report.cmd     : closing-period reports (current + previous), HTML+PDF, then open
rem report.cmd 14  : last 14 days
chcp 65001 >nul
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-report.ps1" -Open
  goto :eof
)
set "D=%~dp0"
set "D=%D:~0,-1%"
for /f "delims=" %%p in ('wsl.exe -e wslpath -a "%D%"') do set "W=%%p"
wsl.exe -e python3 "%W%/shiftlock.py" report --config "%W%/shifts.json" --days %1
start "" "%~dp0report.html"
