@echo off
chcp 65001 >nul
set "SCRIPT=%~dp0Install-Requirements.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
if errorlevel 1 pause
