<#
.SYNOPSIS
  Builds, tests and packages Chest Counter into .\dist (Windows PowerShell 5.1+ or PowerShell 7).

  Produces two shareable zips:
    dist\ChestCounter-Standalone-win-x64.zip  everything included (no .NET install needed) - best for friends
    dist\ChestCounter-Small-win-x64.zip       small download; needs the free .NET 10 Desktop Runtime
                                              (the installer offers to install it from Microsoft)
.PARAMETER SkipTests
  Skip the automated tests.
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Use the .NET SDK on PATH, or the per-user one installed by dotnet-install.ps1.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (Test-Path $userDotnet) { $dotnet = $userDotnet }
if (-not $dotnet) { throw 'The .NET 10 SDK is required to build. Install it from https://dotnet.microsoft.com/download/dotnet/10.0' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    & $dotnet test tests\ChestCounter.Tests\ChestCounter.Tests.csproj -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

function Publish-Package([string]$name, [string[]]$extraArgs) {
    $out = Join-Path $PSScriptRoot "dist\$name"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "Publishing $name..." -ForegroundColor Cyan
    & $dotnet publish src\ChestCounter.App\ChestCounter.App.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:DebugType=none -o $out -nologo @extraArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish of $name failed." }

    Copy-Item installer\install.ps1, installer\uninstall.ps1, 'installer\Install Chest Counter.cmd', 'installer\Uninstall Chest Counter.cmd', 'installer\READ ME FIRST.txt' $out
    Copy-Item README.md $out
    Copy-Item LICENSE (Join-Path $out 'LICENSE.txt')
    Copy-Item lib\LICENSE-FortniteReplayDecompressor.txt $out

    $zip = Join-Path $PSScriptRoot "dist\$name-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host ("  {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
    return $out
}

# Standalone: includes the .NET runtime, one exe, nothing else to install.
Publish-Package 'ChestCounter-Standalone' @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true') | Out-Null

# Small: uses the shared .NET 10 Desktop Runtime.
$small = Publish-Package 'ChestCounter-Small' @('--self-contained', 'false', '-p:IncludeNativeLibrariesForSelfExtract=false')

# Keep the old dist\ChestCounter path working for local installs.
$legacy = Join-Path $PSScriptRoot 'dist\ChestCounter'
if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force }
Copy-Item $small $legacy -Recurse
Write-Host 'Done.' -ForegroundColor Green
