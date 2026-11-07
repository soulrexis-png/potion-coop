@echo off
chcp 65001 >nul
cd /d "%~dp0"
dotnet build -c Release
echo.
pause
