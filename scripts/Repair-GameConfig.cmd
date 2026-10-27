@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Manage-Mod.ps1" -Action Repair %*
set "DLT_REPAIR_EXIT=%ERRORLEVEL%"
pause
exit /b %DLT_REPAIR_EXIT%
