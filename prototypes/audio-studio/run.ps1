[CmdletBinding()]
param([switch]$OpenBrowser)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $root 'artifacts\audio-studio'
$python = Join-Path $artifacts 'venv\Scripts\python.exe'
$pidFile = Join-Path $artifacts 'pid.json'
$url = 'http://127.0.0.1:7861'
$identity = 'ringworld-audio-studio'
function Get-StudioHealth {
    try { return Invoke-RestMethod -Uri "$url/api/health" -TimeoutSec 2 } catch { return $null }
}
function Test-OwnedProcess($record) {
    if ($record.app -ne $identity -or $record.app_dir -ne $PSScriptRoot -or $record.executable -ne $python) { return $false }
    $owned = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if (-not $owned) { return $false }
    if ($owned.Path -ne $python -or $owned.StartTime.ToUniversalTime().ToString('o') -ne $record.start_time) { return $false }
    if (-not $record.server_pid) { return $false }
    $serverProcess = Get-Process -Id $record.server_pid -ErrorAction SilentlyContinue
    return ($serverProcess -and $serverProcess.Path -eq $record.server_executable -and $serverProcess.StartTime.ToUniversalTime().ToString('o') -eq $record.server_start_time)
}
if (-not (Test-Path -LiteralPath $python)) { throw 'Run setup.ps1 first.' }
if (Test-Path -LiteralPath $pidFile) {
    $record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
    if (Test-OwnedProcess $record) {
        $health = Get-StudioHealth
        if ($health -and $health.app -eq $identity -and $health.pid -eq $record.server_pid) {
            Write-Host "Audio studio is already running at $url ($($health.status))."
            if ($OpenBrowser) { Start-Process $url }
            return
        }
        throw 'The recorded studio process is running but its health check failed. Inspect server logs before restarting.'
    }
    if (Get-Process -Id $record.pid -ErrorAction SilentlyContinue) {
        throw 'The PID record refers to a different running process. Refusing to replace it.'
    }
}
$listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 7861)
try { $listener.Start() } catch { throw "Port 7861 is already occupied. Refusing to start another server. $($_.Exception.Message)" }
finally { $listener.Stop() }
$env:HF_HOME = Join-Path $artifacts 'cache\huggingface'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:MPLCONFIGDIR = Join-Path $artifacts 'cache\matplotlib'
$env:NUMBA_CACHE_DIR = Join-Path $artifacts 'cache\numba'
$env:TORCH_HOME = Join-Path $artifacts 'cache\torch'
$env:GRADIO_TEMP_DIR = Join-Path $artifacts 'cache\gradio'
foreach ($folder in @($env:MPLCONFIGDIR, $env:NUMBA_CACHE_DIR, $env:TORCH_HOME, $env:GRADIO_TEMP_DIR)) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}
$env:PYTHONNOUSERSITE = '1'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stdout = Join-Path $artifacts "server-$stamp.stdout.log"
$stderr = Join-Path $artifacts "server-$stamp.stderr.log"
$arguments = '-m uvicorn server:app --app-dir "{0}" --host 127.0.0.1 --port 7861' -f $PSScriptRoot
$process = Start-Process -FilePath $python -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$process.Refresh()
$record = [ordered]@{ app = $identity; app_dir = $PSScriptRoot; pid = $process.Id; start_time = $process.StartTime.ToUniversalTime().ToString('o'); executable = $python; stdout = $stdout; stderr = $stderr }
$temporary = "$pidFile.tmp"
[IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $temporary -Destination $pidFile -Force
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $process.Refresh()
    if ($process.HasExited) { throw "Audio studio exited with code $($process.ExitCode). See $stderr" }
    $health = Get-StudioHealth
    if ($health -and $health.app -eq $identity -and $health.pid) {
        $serverProcess = Get-Process -Id $health.pid -ErrorAction Stop
        $managedPythonRoot = [IO.Path]::GetFullPath((Join-Path $artifacts 'python')) + [IO.Path]::DirectorySeparatorChar
        if ($serverProcess.Path -ne $python -and -not $serverProcess.Path.StartsWith($managedPythonRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Health endpoint belongs to a Python executable outside this workspace environment.'
        }
        if ($serverProcess.StartTime.ToUniversalTime() -lt $process.StartTime.ToUniversalTime().AddSeconds(-1)) {
            throw 'Health endpoint belongs to a process older than the studio launch.'
        }
        $record.server_pid = $serverProcess.Id
        $record.server_executable = $serverProcess.Path
        $record.server_start_time = $serverProcess.StartTime.ToUniversalTime().ToString('o')
        [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $pidFile -Force
        Write-Host "Audio studio is running at $url ($($health.status))."
        Write-Host "Server logs: $stdout and $stderr"
        if ($OpenBrowser) { Start-Process $url }
        return
    }
    if ($health -and $health.app -ne $identity) {
        throw "A different server answered on port 7861. Inspect $stderr and pid.json."
    }
    Start-Sleep -Seconds 1
}
throw "Audio studio did not become ready within 60 seconds. The owned process was left running; inspect $stderr or use stop.ps1."
