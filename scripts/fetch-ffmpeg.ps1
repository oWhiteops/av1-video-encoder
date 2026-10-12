# Downloads the LGPL shared FFmpeg build (BtbN/FFmpeg-Builds, release branch 8.1)
# into third_party/ffmpeg and verifies that libsvtav1 is available.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'third_party\ffmpeg'
$url  = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip'

if ((Test-Path "$dest\bin\ffmpeg.exe") -and -not $Force) { Write-Host "FFmpeg already present in $dest (use -Force to refresh)"; exit 0 }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'ffmpeg.zip'
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Expand-Archive $zip -DestinationPath $tmp
    $src = Get-ChildItem $tmp -Directory | Select-Object -First 1

    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory "$dest\bin" -Force | Out-Null
    Copy-Item "$($src.FullName)\bin\ffmpeg.exe", "$($src.FullName)\bin\ffprobe.exe", "$($src.FullName)\bin\*.dll" "$dest\bin"
    Copy-Item "$($src.FullName)\LICENSE.txt" "$dest\LICENSE.txt"

    $encoders = & "$dest\bin\ffmpeg.exe" -hide_banner -encoders 2>&1 | Out-String
    if ($encoders -notmatch 'libsvtav1') { throw 'Downloaded FFmpeg build does not include libsvtav1.' }
    $version = (& "$dest\bin\ffmpeg.exe" -hide_banner -version | Select-Object -First 1)
    @("Source: $url", "Archive: $($src.Name)", "SHA256: $hash", "Version: $version", "Fetched: $(Get-Date -Format s)") |
        Set-Content "$dest\VERSION.txt" -Encoding utf8
    Write-Host "Installed $version"
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
