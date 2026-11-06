<#
.SYNOPSIS
Runs one standalone resident-helper PDA model comparison. Does not build or download anything.
.EXAMPLE
.\tools\run_pda_model_comparison.ps1 -ModelKey qwen3-8b-q4 -MemoryMode SystemRam
.NOTES
GPU invocations through run_tests.ps1 already hold the shared GPU queue; this runner never acquires it again.
Direct GPU invocations require the caller to own that queue. Results are synthetic-fixture measurements, with human quality review required.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('qwen3-8b-q4', 'granite-3.3-8b-q4', 'llama-3.1-8b-q4', 'gemma-3-4b-q4')][string]$ModelKey,
    [ValidateSet('SystemRam', 'GpuVram')][string]$MemoryMode = 'SystemRam',
    [ValidateRange(1, 99)][int]$GpuLayers = 8,
    [ValidateRange(1, 16)][int]$Threads = 4,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$Label = 'pda_8b'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetRoot = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
$assembly = Join-Path $projectRoot 'prototypes/pda-voice/bin/qa-comparison/PdaVoicePrototype.dll'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Pinned .NET 10 SDK is missing from artifacts/engine-upgrade/dotnet.' }
if (!(Test-Path -LiteralPath $assembly)) { throw 'Comparison helper is missing. Build it explicitly with tools/build_pda_worker.ps1 -Comparison before running comparisons.' }
$comparisonAppDataPath = Join-Path $projectRoot '.godot/test_runs/appdata_pda_8b'
[IO.Directory]::CreateDirectory($comparisonAppDataPath) | Out-Null
$names = @('APPDATA', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'PATH', 'DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:APPDATA = $comparisonAppDataPath
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:PATH = "$dotnetRoot;$($previous['PATH'])"
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet-cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $mode = if ($MemoryMode -eq 'GpuVram') { 'vram' } else { 'ram' }
    & $dotnet $assembly qa-compare --model $ModelKey --memory $mode --gpu-layers $GpuLayers --threads $Threads --label $Label
    $result = $LASTEXITCODE
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
exit $result
