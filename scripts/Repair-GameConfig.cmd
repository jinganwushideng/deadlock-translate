@echo off
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Manage-Mod.ps1" -Action Repair %*
set "DLT_REPAIR_EXIT=%ERRORLEVEL%"
pause
exit /b %DLT_REPAIR_EXIT%
