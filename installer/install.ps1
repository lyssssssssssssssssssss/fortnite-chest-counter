<#
.SYNOPSIS
  Installs Chest Counter for the current Windows user (no administrator rights needed).
  Copies the app to %LOCALAPPDATA%\Programs\ChestCounter and adds a Start Menu shortcut.
  Your statistics live separately in %LOCALAPPDATA%\ChestCounter and are never touched by (re)installing.
.PARAMETER DesktopShortcut
  Also create a desktop shortcut.
.PARAMETER NoLaunch
  Do not start the app after installing.
.PARAMETER Yes
  When run from the source-code folder, download the latest release without asking.
#>
param([switch]$DesktopShortcut, [switch]$NoLaunch, [switch]$Yes)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar makes downloads very slow
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\ChestCounter'
$exe = Join-Path $target 'ChestCounter.exe'
$releasesPage = 'https://github.com/lyssssssssssssssssssss/fortnite-chest-counter/releases/latest'
$releasesApi = 'https://api.github.com/repos/lyssssssssssssssssssss/fortnite-chest-counter/releases/latest'

if (-not (Test-Path (Join-Path $source 'ChestCounter.exe'))) {
    # Run from the source-code folder (installer\) instead of an extracted release zip:
    # use a copy built with build.ps1 if there is one, otherwise offer the latest GitHub release.
    $repoRoot = Split-Path $source -Parent
    $built = @('dist\ChestCounter-Standalone', 'dist\ChestCounter-Small', 'dist\ChestCounter') |
        ForEach-Object { Join-Path $repoRoot $_ } |
        Where-Object { Test-Path (Join-Path $_ 'ChestCounter.exe') } |
        Select-Object -First 1

    if ($built) {
        Write-Host "Installing the copy built in $built"
        $source = $built
    } else {
        Write-Host ''
        Write-Host 'This is the Chest Counter source-code folder, so there is no ChestCounter.exe here to install.' -ForegroundColor Yellow
        $answer = if ($Yes) { 'Y' } else { Read-Host 'Download the latest Chest Counter release from GitHub and install it? [Y/N]' }
        if ($answer -notmatch '^[Yy]') {
            Write-Host "No problem. You can download it yourself from $releasesPage"
            exit 1
        }

        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $release = Invoke-RestMethod -UseBasicParsing $releasesApi
        $asset = $release.assets | Where-Object { $_.name -like 'ChestCounter-Standalone-*.zip' } | Select-Object -First 1
        if (-not $asset) { throw "The latest release has no Standalone zip. Download it from $releasesPage" }

        $work = Join-Path $env:TEMP "ChestCounter-$($release.tag_name)"
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        New-Item -ItemType Directory -Force $work | Out-Null
        $zip = Join-Path $work $asset.name
        Write-Host ("Downloading {0} ({1:N0} MB) from GitHub..." -f $asset.name, ($asset.size / 1MB))
        Invoke-WebRequest -UseBasicParsing $asset.browser_download_url -OutFile $zip
        if ((Get-Item $zip).Length -ne $asset.size) { throw 'The download was incomplete. Please run the installer again.' }
        Expand-Archive $zip -DestinationPath (Join-Path $work 'app') -Force
        $source = Join-Path $work 'app'
        if (-not (Test-Path (Join-Path $source 'ChestCounter.exe'))) { throw "The downloaded release does not contain ChestCounter.exe. Download it from $releasesPage" }
        Write-Host "Downloaded Chest Counter $($release.tag_name)."
    }
}

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
