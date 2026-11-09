@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ReplayOobe.ps1" %*
echo.
pause
