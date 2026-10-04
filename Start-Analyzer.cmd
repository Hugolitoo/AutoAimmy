@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Analyzer.ps1" %*
exit /b %errorlevel%
