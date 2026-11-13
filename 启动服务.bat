@echo off
chcp 65001 >nul
title Transcode LAN Server
cd /d "%~dp0"
if not exist "data" mkdir "data"
echo ==================================================
echo   Transcode LAN  -  local video transcode server
echo.
echo   Local:  http://127.0.0.1:8099
echo   LAN  :  check the URL printed below
echo.
echo   Keep this window open. Close it to stop.
echo ==================================================
echo.
TranscodeServer.exe
echo.
echo Server stopped.
pause
