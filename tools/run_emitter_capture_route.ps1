param(
    [string]$Out = (Join-Path (Get-Location) 'artifacts\emitter_capture_route'),
    [string]$Sites = 'land,island,mountain,main',
    [switch]$Quick
)

# Runs the M1-11 capture route WINDOWED (needs a GPU window; headless cannot save frames).
# Engine output goes to <Out>\godot.log; SHADER ERROR / ERROR: lines (minus the known ring_voxel_mesher noise) are counted into report.json and report.md.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$godot = Join-Path $repo 'artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64.exe'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$arguments = @('--path', $repo, '--script', 'res://tools/emitter_capture_route.gd', '--', "--out=$Out", "--sites=$Sites")
if ($Quick) { $arguments += '--quick' }
$log = Join-Path $Out 'godot.log'
$process = Start-Process -FilePath $godot -ArgumentList $arguments -WorkingDirectory $repo -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
Get-Content "$log.err" -ErrorAction SilentlyContinue | Add-Content $log
Remove-Item "$log.err" -ErrorAction SilentlyContinue

$lines = Get-Content $log -ErrorAction SilentlyContinue
$errors = @($lines | Where-Object { ($_ -match 'SHADER ERROR' -or $_ -match 'ERROR:') -and $_ -notmatch 'ring_voxel_mesher' })
$shader = @($errors | Where-Object { $_ -match 'SHADER ERROR' }).Count
$summary = "$($errors.Count) error lines ($shader SHADER ERROR), excluding ring_voxel_mesher noise; see godot.log"
$jsonPath = Join-Path $Out 'report.json'
if (Test-Path $jsonPath) {
    $report = Get-Content $jsonPath -Raw | ConvertFrom-Json
    $report.log_errors = $summary
    $report | ConvertTo-Json -Depth 32 | Set-Content $jsonPath -Encoding utf8
    Add-Content (Join-Path $Out 'report.md') "`nLog errors (counted by the wrapper): $summary`n"
}
$lines | Select-String 'EMITTER_CAPTURE_ROUTE=' | ForEach-Object { $_.Line }
exit $process.ExitCode
