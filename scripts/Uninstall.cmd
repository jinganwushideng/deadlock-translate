@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Manage-Mod.ps1" -Action Uninstall
pause
