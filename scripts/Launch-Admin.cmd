@echo off
cd /d "%~dp0"
start "" /b "%~dp0DeadlockTranslate.exe" --admin-compatible --launch
