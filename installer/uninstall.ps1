<#
.SYNOPSIS
  Removes Chest Counter for the current user. Your statistics are kept unless -RemoveData is given.
#>
param([switch]$RemoveData)
$ErrorActionPreference = 'Continue'
$target = Join-Path $env:LOCALAPPDATA 'Programs\ChestCounter'
$exe = Join-Path $target 'ChestCounter.exe'

$running = Get-Process ChestCounter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    & $exe --exit
    foreach ($p in $running) { if (-not $p.WaitForExit(8000)) { $p | Stop-Process -Force } }
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Chest Counter.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Chest Counter.lnk') -ErrorAction SilentlyContinue
# "Start with Windows" entry (only exists if it was enabled in Settings)
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ChestCounter' -ErrorAction SilentlyContinue

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Write-Host 'Chest Counter was removed.'

$data = Join-Path $env:LOCALAPPDATA 'ChestCounter'
if ($RemoveData) {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Statistics and settings were deleted.'
} else {
    Write-Host "Your statistics were kept in $data (run with -RemoveData to delete them)."
}
