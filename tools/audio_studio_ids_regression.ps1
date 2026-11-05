<#
.SYNOPSIS
  CPU regression for permanent audio generation/take IDs. No models or GPU.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_tests.ps1 -Tests audio_studio_ids_regression -NoBuild -Label audio-studio-ids
#>
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$python = Join-Path $projectRoot 'artifacts\audio-studio\venv\Scripts\python.exe'
$regression = Join-Path $projectRoot 'prototypes\audio-studio\generation_ids_regression.py'
$code = 1
try {
    if (-not (Test-Path -LiteralPath $python)) {
        throw 'Local audio runtime missing; run prototypes/audio-studio/setup.ps1 -SkipModels first.'
    }
    & $python $regression
    $code = $LASTEXITCODE
} catch {
    Write-Output "AUDIO_STUDIO_IDS=FAIL $($_.Exception.Message)"
}
exit $code
