<#
.SYNOPSIS
  Tag-driven test runner (tools/testing/TestRunner.psm1). Selects tests from tools/test_manifest.json, runs them in lanes
  and writes .godot/test_runs/<label>/<run id>/{results.json,results.md,logs/}. Exit 0 = no fail/timeout/crash/error, 1 = failures, 2 = runner error.
.EXAMPLE
  .\tools\run_tests.ps1 -Label mytask                       # tests for the files changed vs main (default: -Changed)
  .\tools\run_tests.ps1 -Label mytask -DryRun               # selection, reasons and a time estimate; runs nothing
  .\tools\run_tests.ps1 -Label mytask -Tags clouds,weather
  .\tools\run_tests.ps1 -Label mytask -Tests cloud_layers_gpu_test,storm_lifecycle_code_test
  .\tools\run_tests.ps1 -Label mytask -Suite smoke          # smoke | gate | full | extended
  .\tools\run_tests.ps1 -Label mytask -Resume latest        # finish only the tests that did not complete (add -RetryFailed for fail)
  .\tools\run_tests.ps1 -Files scripts/ui/GameplayGrassDefaults.cs -DryRun   # explicit changed paths instead of git
.NOTES
  Smoke is always part of -Changed and -Suite runs. -Since <rev|green> diffs against a revision (green = last green run of the label).
  -UnmappedPolicy smoke|full: what a changed file that no glob maps selects (default smoke = smoke set only, with a warning; full = every headless test).
  -GpuClass: '' (default) = each entry's gpu_class from the manifest (exclusive unless the entry or defaults.gpu_class says shared); shared = every GPU entry runs in the shared
  GPU group except those pinned exclusive (gpu_class exclusive or tag perf-telemetry); exclusive = all in one exclusive ticket. The shared group runs first, then the exclusive one.
  .ps1 runner entries run inside the run's ticket (RINGWORLD_GPU_QUEUE_HELD, see gpu_queue.ps1 nested mode).
  Profile: .godot\test_runs\appdata_<Label> (reused; first run copies the warm caches). -ManifestPath is for scratch manifests.
#>
[CmdletBinding()]
param(
    [switch]$Changed,
    [string]$Base = 'main',
    [string]$Since = '',
    [string[]]$Files = @(),
    [string[]]$Tags = @(),
    [string[]]$Tests = @(),
    [ValidateSet('', 'smoke', 'gate', 'full', 'extended')][string]$Suite = '',
    [switch]$DryRun,
    [switch]$NoBuild,
    [string]$Label = 'default',
    [ValidateSet('copy', 'reuse', 'none')][string]$Cache = 'reuse',
    [int]$Parallel = 0,
    [int]$LocalLanes = 0,
    [double]$YieldMinutes = 0,
    [string]$Resume = '',
    [switch]$RetryFailed,
    [string]$ManifestPath = '',
    [string]$KnownFailuresPath = '',
    [ValidateSet('smoke', 'full')][string]$UnmappedPolicy = 'smoke',
    [ValidateSet('', 'shared', 'exclusive')][string]$GpuClass = ''
)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'testing\TestRunner.psm1') -Force -DisableNameChecking
    $code = Invoke-TestRun @PSBoundParameters
    exit ([int]($code | Select-Object -Last 1))
} catch {
    Write-Host "run_tests: ERROR $($_.Exception.Message)"
    exit 2
}
