@echo off
rem Installs or updates Chest Counter for this Windows user (no admin rights needed) and adds a desktop shortcut.
rem Uses the copy built in "dist" if there is one; otherwise offers to download the latest release from GitHub.
rem Your stats and settings are always kept.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1" -DesktopShortcut
pause
