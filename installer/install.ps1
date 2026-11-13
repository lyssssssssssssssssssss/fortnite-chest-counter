<#
.SYNOPSIS
  Installs Chest Counter for the current Windows user (no administrator rights needed).
  Copies the app to %LOCALAPPDATA%\Programs\ChestCounter and adds a Start Menu shortcut.
  Your statistics live separately in %LOCALAPPDATA%\ChestCounter and are never touched by (re)installing.
.PARAMETER DesktopShortcut
  Also create a desktop shortcut.
.PARAMETER NoLaunch
  Do not start the app after installing.
#>
param([switch]$DesktopShortcut, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\ChestCounter'
$exe = Join-Path $target 'ChestCounter.exe'

if (-not (Test-Path (Join-Path $source 'ChestCounter.exe'))) { throw "ChestCounter.exe not found next to this script ($source)." }

# The "Small" package needs the .NET 10 Desktop Runtime (x64); the "Standalone" package includes it.
$selfContained = (Get-Item (Join-Path $source 'ChestCounter.exe')).Length -gt 20MB
function Test-DesktopRuntime {
    foreach ($root in @("$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App", "$env:LOCALAPPDATA\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App")) {
        if (Test-Path $root) { if (Get-ChildItem $root -Directory | Where-Object { $_.Name -like '10.*' }) { return $true } }
    }
    return $false
}
if (-not $selfContained -and -not (Test-DesktopRuntime)) {
    Write-Host ''
    Write-Host 'Chest Counter needs the free Microsoft .NET 10 Desktop Runtime, which is not installed on this PC.' -ForegroundColor Yellow
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $answer = Read-Host 'Install it now from Microsoft (winget)? Windows may ask for permission. [Y/N]'
        if ($answer -match '^[Yy]') {
            winget install --id Microsoft.DotNet.DesktopRuntime.10 --exact --accept-source-agreements --accept-package-agreements
        }
    }
    if (-not (Test-DesktopRuntime)) {
        Write-Warning 'Please install the ".NET Desktop Runtime 10" (x64) from https://dotnet.microsoft.com/download/dotnet/10.0 and run this installer again - or use the Standalone version of Chest Counter, which needs nothing else.'
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0'
        exit 1
    }
}

# Close a running copy cleanly so files can be replaced (updates). Statistics are saved continuously.
$running = Get-Process ChestCounter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host 'Closing the running Chest Counter...'
    & $exe --exit
    foreach ($p in $running) { if (-not $p.WaitForExit(8000)) { $p | Stop-Process -Force } }
}

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source '*') $target -Recurse -Force -Exclude '*.zip'
Write-Host "Installed to $target"

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Fortnite chest counter (reads replay files only)'
    $lnk.Save()
}
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Chest Counter.lnk'
New-Shortcut $startMenu
Write-Host "Start Menu shortcut: $startMenu"
if ($DesktopShortcut) {
    $desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Chest Counter.lnk'
    New-Shortcut $desk
    Write-Host "Desktop shortcut: $desk"
}

if (-not $NoLaunch) {
    Start-Process $exe
    Write-Host 'Chest Counter is starting. Dashboard: http://localhost:48231/'
}
