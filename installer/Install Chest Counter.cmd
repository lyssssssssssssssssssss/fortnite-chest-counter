@echo off
rem Double-click to install Chest Counter for this Windows user (no admin rights needed).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -DesktopShortcut
pause
