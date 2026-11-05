[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $root 'artifacts\audio-studio'
$pidFile = Join-Path $artifacts 'pid.json'
$python = Join-Path $artifacts 'venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-Host 'No managed audio studio process is recorded.'
    return
}
$record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
if ($record.app -ne 'ringworld-audio-studio' -or $record.app_dir -ne $PSScriptRoot -or $record.executable -ne $python) {
    throw 'PID record identity does not match this audio studio. No process was stopped.'
}
$process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
if (-not $process) {
    Remove-Item -LiteralPath $pidFile
    Write-Host 'The recorded audio studio process has already exited.'
    return
}
if ($process.Path -ne $python -or $process.StartTime.ToUniversalTime().ToString('o') -ne $record.start_time) {
    throw 'PID has been reused or process identity changed. No process was stopped.'
}
if (-not $record.server_pid) { throw 'Server PID was never verified. Inspect the startup logs; no process was stopped.' }
$serverProcess = Get-Process -Id $record.server_pid -ErrorAction SilentlyContinue
if ($serverProcess) {
    $managedPythonRoot = [IO.Path]::GetFullPath((Join-Path $artifacts 'python')) + [IO.Path]::DirectorySeparatorChar
    if (($record.server_executable -ne $python -and -not $record.server_executable.StartsWith($managedPythonRoot, [StringComparison]::OrdinalIgnoreCase)) -or $serverProcess.Path -ne $record.server_executable -or $serverProcess.StartTime.ToUniversalTime().ToString('o') -ne $record.server_start_time) {
        throw 'Server PID has been reused or executable identity changed. No process was stopped.'
    }
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:7861/api/health' -TimeoutSec 5
        if ($health.app -ne 'ringworld-audio-studio' -or $health.pid -ne $record.server_pid) {
            throw 'The responding server identity does not match the recorded studio.'
        }
        $prepared = Invoke-RestMethod -Uri 'http://127.0.0.1:7861/api/prepare-stop' -Method Post -TimeoutSec 5
        if (-not $prepared.ready -or $prepared.pid -ne $record.server_pid) {
            throw 'The studio did not confirm it is ready to stop.'
        }
    } catch {
        throw "Studio shutdown was refused or could not be verified; no process was stopped. Wait for active jobs to finish and retry. $($_.Exception.Message)"
    }
    Stop-Process -InputObject $serverProcess
    $serverProcess.WaitForExit(10000) | Out-Null
    if (-not $serverProcess.HasExited) { throw 'The server process did not exit. PID record was preserved.' }
}
$process.Refresh()
if (-not $process.HasExited) {
    Stop-Process -InputObject $process
    $process.WaitForExit(10000) | Out-Null
}
if (-not $process.HasExited) { throw 'The audio studio process did not exit. PID record was preserved.' }
Remove-Item -LiteralPath $pidFile
Write-Host 'Audio studio stopped. Models, logs and generated sounds were preserved.'
