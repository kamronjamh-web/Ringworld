<#
.SYNOPSIS
Runs focused-context, native-vision or debug-panel capture without building or downloading.
.NOTES
GPU runs through run_tests.ps1 already own the shared GPU queue; do not acquire it twice.
Direct GPU callers must own that queue. Answers require human review.
#>
[CmdletBinding()]
param(
    [ValidateSet('Focused', 'Vision', 'CaptureUI')][string]$Scenario = 'Focused',
    [ValidateSet('Qwen', 'Gemma', 'qwen3-8b-q4', 'gemma-3-4b-q4')][string]$ModelKey = 'Qwen',
    [ValidateSet('SystemRam', 'GpuVram')][string]$MemoryMode = 'SystemRam',
    [ValidateRange(1, 99)][int]$GpuLayers = 16,
    [ValidateRange(1, 16)][int]$Threads = 4,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$Label = 'pda_memory'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetRoot = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
$assembly = Join-Path $projectRoot 'prototypes/pda-voice/bin/qa-comparison/PdaVoicePrototype.dll'
$engine = Join-Path $projectRoot 'artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/Godot_v4.8-dev7_mono_win64_console.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Pinned .NET 10 SDK is missing.' }
if ($Scenario -eq 'CaptureUI') {
    if (!(Test-Path -LiteralPath $engine)) { throw 'Pinned Godot console executable is missing.' }
} elseif (!(Test-Path -LiteralPath $assembly)) { throw 'Build tools/build_pda_worker.ps1 -Comparison explicitly before evaluation.' }
if ($Scenario -eq 'Vision' -and !$PSBoundParameters.ContainsKey('ModelKey')) { $ModelKey = 'Gemma' }
$resolvedModel = switch ($ModelKey) { 'Qwen' { 'qwen3-8b-q4' }; 'Gemma' { 'gemma-3-4b-q4' }; default { $ModelKey } }
if ($Scenario -eq 'Vision' -and $resolvedModel -ne 'gemma-3-4b-q4') { throw 'Native vision requires Gemma.' }
$profile = Join-Path $projectRoot '.godot/test_runs/appdata_pda_memory'
[IO.Directory]::CreateDirectory($profile) | Out-Null
$names = @('APPDATA', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'PATH', 'DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:APPDATA = $profile
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:PATH = "$dotnetRoot;$($previous['PATH'])"
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet-cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if ($Scenario -eq 'CaptureUI') {
        & $engine --path $projectRoot --script 'res://tools/pda_debug_ui_test.gd' -- --capture --capture-dir 'res://.godot/pda_memory/ui_capture'
    } else {
        $mode = if ($MemoryMode -eq 'GpuVram') { 'vram' } else { 'ram' }
        $command = if ($Scenario -eq 'Vision') { 'qa-vision-smoke' } else { 'qa-compare' }
        $arguments = @($assembly, $command, '--model', $resolvedModel, '--memory', $mode, '--gpu-layers', "$GpuLayers", '--threads', "$Threads", '--label', $Label)
        if ($Scenario -eq 'Focused') { $arguments += @('--context', 'focused', '--guardrails', 'true') }
        & $dotnet @arguments
    }
    $result = $LASTEXITCODE
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
exit $result
