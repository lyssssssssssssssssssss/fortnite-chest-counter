@echo off
rem Removes Chest Counter. Your statistics in %LOCALAPPDATA%\ChestCounter are kept.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
