@echo off
chcp 65001 >nul
setlocal
net session >nul 2>&1
if %errorlevel%==0 goto run
echo Administrator rights are required - asking for elevation...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:run
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup.ps1" -FirewallOnly
echo.
pause
