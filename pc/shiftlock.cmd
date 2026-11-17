@echo off
rem usage: shiftlock.cmd status^|yasumi^|deru^|okita^|settings^|sync^|day ...  (no args = help)
chcp 65001 >nul
set "D=%~dp0"
set "D=%D:~0,-1%"
for /f "delims=" %%p in ('wsl.exe -e wslpath -a "%D%"') do set "W=%%p"
if "%~1"=="" (set "A=help") else (set "A=%*")
wsl.exe -e python3 "%W%/shiftlock.py" %A% --config "%W%/shifts.json"
echo.
pause
