@echo off
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Manage-Mod.ps1" -Action Install %*
pause
