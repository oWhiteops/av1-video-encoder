# Encodes a sample with a grid of SVT-AV1 presets/CRFs (and optionally AMF) and reports
# size, speed and VMAF so preset defaults can be chosen from data rather than folklore.
# Usage: scripts\benchmark.ps1 -InputFile sample.mp4 [-Presets 4,6,8] [-Crfs 24,30,36,42] [-Amf]
param(
    [Parameter(Mandatory)][string]$InputFile,
    [int[]]$Presets = @(4, 6, 8),
    [int[]]$Crfs = @(24, 30, 36, 42),
    [int[]]$AmfQvbr = @(),
    [string]$SvtParams = "",
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'av1-bench')
)
$ErrorActionPreference = 'Continue'  # ffmpeg writes to stderr; check exit codes instead
$env:SVT_LOG = '1'  # errors only
$bin = Join-Path (Split-Path $PSScriptRoot -Parent) 'third_party\ffmpeg\bin'
$ffmpeg = Join-Path $bin 'ffmpeg.exe'
New-Item -ItemType Directory $OutDir -Force | Out-Null
$srcSize = (Get-Item $InputFile).Length

function Measure-Vmaf($distorted) {
    $log = & $ffmpeg -hide_banner -nostats -i $distorted -i $InputFile `
        -lavfi "[0:v]setpts=PTS-STARTPTS[d];[1:v]setpts=PTS-STARTPTS[r];[d][r]libvmaf=n_threads=12" -f null - 2>&1 | Out-String
    if ($log -match 'VMAF score: ([\d.]+)') { [double]$Matches[1] } else { $null }
}

function Run($name, [string[]]$codecArgs) {
    $out = Join-Path $OutDir "$name.mp4"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $ffmpeg -hide_banner -loglevel error -y -i $InputFile @codecArgs -an $out
    $sw.Stop()
    if ($LASTEXITCODE -ne 0) { throw "Encode failed: $name" }
    $size = (Get-Item $out).Length
    [pscustomobject]@{
        Config = $name; SizeMB = [math]::Round($size / 1MB, 2); PctOfSource = [math]::Round(100 * $size / $srcSize, 1)
        EncodeSec = [math]::Round($sw.Elapsed.TotalSeconds, 1); VMAF = Measure-Vmaf $out
    }
}

$results = foreach ($p in $Presets) { foreach ($c in $Crfs) {
    $extra = if ($SvtParams) { @('-svtav1-params', $SvtParams) } else { @() }
    Run "svt-p$p-crf$c$($SvtParams -replace '[^a-z0-9=]','_')" (@('-c:v', 'libsvtav1', '-preset', "$p", '-crf', "$c", '-pix_fmt', 'yuv420p10le') + $extra)
} }
$results += foreach ($q in $AmfQvbr) {
    Run "amf-qvbr$q" @('-c:v', 'av1_amf', '-usage', 'high_quality', '-quality', 'high_quality', '-rc', 'qvbr', '-qvbr_quality_level', "$q")
}
$results | Format-Table -AutoSize
$results | Export-Csv (Join-Path $OutDir 'results.csv') -NoTypeInformation
