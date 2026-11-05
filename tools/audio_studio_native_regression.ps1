<#
.SYNOPSIS
  CPU-only readiness/dispatch regression for the native Stable Audio comparison.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/run_tests.ps1 -Tests audio_studio_native_regression -NoBuild -Label audio-studio-native
#>
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$python = Join-Path $projectRoot 'artifacts\audio-studio\venv\Scripts\python.exe'
$regression = Join-Path $projectRoot 'prototypes\audio-studio\native_backend_regression.py'
$code = 1
try {
    if (-not (Test-Path -LiteralPath $python)) { throw 'Prepare the local audio runtime before running this regression.' }
    & $python $regression
    $code = $LASTEXITCODE
} catch {
    Write-Output "AUDIO_STUDIO_NATIVE=FAIL $($_.Exception.Message)"
}
exit $code
