@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Trust-Local-Https.ps1" -Renew
pause
