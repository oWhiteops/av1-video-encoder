# Repeatable build: fetch FFmpeg if needed, run tests, publish a self-contained win-x64 build and zip it.
# Usage: scripts\build.ps1 [-SkipTests] [-Configuration Release]
param([switch]$SkipTests, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Prefer a dotnet that actually has an SDK (a per-user install from dotnet-install.ps1 counts).
$dotnet = @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe", 'dotnet') |
    Where-Object { (Get-Command $_ -ErrorAction SilentlyContinue) -and (& $_ --list-sdks 2>$null) } | Select-Object -First 1
if (-not $dotnet) { throw '.NET 10 SDK not found. Install it from https://dot.net or run dotnet-install.ps1 -Channel 10.0.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& "$PSScriptRoot\fetch-ffmpeg.ps1"

if (-not $SkipTests) {
    & $dotnet test "$root\tests\Av1Encoder.Engine.Tests" -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$version = ([xml](Get-Content "$root\src\Av1Encoder.App\Av1Encoder.App.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = "$root\artifacts\AV1VideoEncoder-$version-win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish "$root\src\Av1Encoder.App" -c $Configuration -r win-x64 --self-contained true -o $publish -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item "$root\README.md" $publish

$zip = "$publish.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish\*" -DestinationPath $zip
Write-Host "Published: $publish"
Write-Host "Package:   $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
