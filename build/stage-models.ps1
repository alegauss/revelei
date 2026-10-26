# PB51: the models the installer carries, so a fresh install works offline. For every model the
# app runs, copies its meta.json and its fp16.onnx from the exported models folder into <out>\<name>\.
# A model with no FP16 variant brings its fp32.onnx instead: YuNet, which is only ever FP32, and
# ViTMatte, whose graph does not survive the FP16 converter. A model missing, or a file that differs
# from its meta.json, fails the build rather than shipping an installer that cannot run an operation.
#
# Why FP16, the owner's choice of 2026-10-20: half of FP32's size, 82 to 85 dB PSNR against it
# (PB10) and as fast on DirectML (SCUNet 132 against 126 ms per 256 px tile). INT8 was slower than
# FP32 on the GPU and lost 29 to 33 dB, so it is not shipped. On the CPU FP16 takes about twice
# FP32's time (Real-ESRGAN x4 2404 against 1141 ms at 128 px), which the FP32 download (PB13) answers.
# Staged on 2026-10-20: 446 MB of models, a 475 MB installer; 608 MB of models once GFPGAN went FP32 (PB54).
#
#   powershell -NoProfile -File build\stage-models.ps1 -Out artifacts\publish\models

param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Models = ''
)

$ErrorActionPreference = 'Stop'
if (-not $Models) { $Models = Join-Path $PSScriptRoot '..\.cache\models' }

# PB58, the owner's choice of 2026-10-20: upscaling, denoising and the face detector. GFPGAN
# (325 MB), RMBG-1.4 (168 MB) and ViTMatte (99 MB) were 84% of v0.1.0's models, for two operations
# most photos never use, so their first use downloads them (FirstUse in Revelei.Inference).
$required = @(
    'realesrgan-x4plus', 'realesrgan-x2plus', 'yunet-2023mar', 'scunet-color-real-psnr'
)

# Models staged as FP32 although they have an FP16. GFPGAN was one (PB54) until PB58 left it out.
$fp32Only = @()

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null

$total = 0
foreach ($name in $required) {
    $folder = Join-Path $Models $name
    $metaPath = Join-Path $folder 'meta.json'
    if (-not (Test-Path $metaPath)) {
        throw "$name has no meta.json in $Models; run scripts\download_models.py and scripts\export-and-quantize.py"
    }
    $meta = Get-Content -Raw -Encoding UTF8 $metaPath | ConvertFrom-Json
    $variant = if ($fp32Only -notcontains $name -and $meta.variants.PSObject.Properties.Name -contains 'fp16') { 'fp16' } else { 'fp32' }
    $described = $meta.variants.$variant
    $source = Join-Path $folder $described.file
    if (-not (Test-Path $source)) { throw "$name lists $($described.file) in meta.json but $source is missing" }
    $file = Get-Item $source
    if ($file.Length -ne $described.bytes -or (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $described.sha256) {
        throw "$name\$($described.file) differs from its meta.json; re-run scripts\export-and-quantize.py"
    }

    $target = Join-Path $Out $name
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $metaPath, $source -Destination $target
    $total += $file.Length
    Write-Host ('{0}: {1} ({2:N1} MB)' -f $name, $described.file, ($file.Length / 1MB))
}

Write-Host ('{0} models, {1:N0} MB -> {2}' -f $required.Count, ($total / 1MB), $Out)
