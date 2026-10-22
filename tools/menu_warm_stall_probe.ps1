<#
.SYNOPSIS
  Phase C WO C3 step 1 (diagnostics only): runs tools/menu_warm_stall_probe.gd against an ISOLATED APPDATA holding COPIES of the
  saved slots (never touches real saves) and optionally records a sampled managed-stack trace with dotnet-trace.
.EXAMPLE
  .\tools\menu_warm_stall_probe.ps1 -Label warm1 -Cache copy            # returning player: shader/pipeline/ring caches copied
  .\tools\menu_warm_stall_probe.ps1 -Label cold1 -Cache none            # first run: no caches
  .\tools\menu_warm_stall_probe.ps1 -Label warm2 -Cache reuse -Trace    # reuse the profile, add a dotnet-trace sample trace
  .\tools\menu_warm_stall_probe.ps1 -Label base -Cache reuse -NoWarm    # menu-only baseline (--no-warm-world)
.NOTES
  The isolated profile is .godot\phase_c_live\appdata_<Profile> (default: appdata_<Label>). Output: .godot\phase_c_live\<Label>\
  (console.log, stall_frames.csv, stall_detail.txt, summary.json, and trace.nettrace/.speedscope.json with -Trace).
  -Trace needs `dotnet tool install -g dotnet-trace`. Analyse with tools/warm_stall_report.py.
  Kill all Godot processes before a GPU run.
#>
param(
  [string]$Label = "stall",
  [string]$Profile = "",
  [int]$Primary = 1,
  [int]$Secondary = 2,
  [ValidateSet("none","copy","reuse")][string]$Cache = "copy",
  [switch]$Trace,
  [switch]$NoWarm,
  [double]$Tail = 8,
  [int]$TraceSeconds = 58,
  [string[]]$EnvVars = @(),
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\phase_c_live"
if ($Profile -eq "") { $Profile = $Label }
$isoRoot = Join-Path $base "appdata_$Profile"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$outDir = Join-Path $base $Label
New-Item -ItemType Directory -Force $outDir | Out-Null
if ($Cache -ne "reuse" -or -not (Test-Path $isoProfile)) {
  if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Primary") (Join-Path $isoProfile "worlds\slot_1")
  Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Secondary") (Join-Path $isoProfile "worlds\slot_2")
  foreach ($pair in @(@(1, $Primary), @(2, $Secondary))) {
    $meta = Join-Path $isoProfile "worlds\slot_$($pair[0])\world.json"
    $text = Get-Content -Raw $meta
    $text = $text -replace '"slot":\s*\d+', ('"slot": ' + $pair[0])
    Set-Content -NoNewline -Path $meta -Value $text
  }
  Get-ChildItem $realProfile -Filter "*.tres" | Copy-Item -Destination $isoProfile
  if ($Cache -eq "copy") {
    foreach ($dir in "shader_cache","vulkan","ring_cache","generated") {
      if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
    }
  }
}
$env:APPDATA = $isoRoot
foreach ($pair in $EnvVars) { $k, $v = $pair -split "=", 2; Set-Item -Path "env:$k" -Value $v }
$arguments = @("--path", $project, "--script", "res://tools/menu_warm_stall_probe.gd", "--", "--stall-probe-out=$Label", "--stall-probe-tail=$Tail")
# With -Trace the probe idles after its report so dotnet-trace stops (after -TraceSeconds) while the process is alive; the
# rundown then resolves managed method names. -TraceSeconds must end after STALL_PROBE_DONE and before the hold expires.
if ($Trace) { $arguments += "--stall-probe-hold=40" }
if ($NoWarm) { $arguments += "--no-warm-world" }
$stdout = Join-Path $outDir "console.log"
$watch = [Diagnostics.Stopwatch]::StartNew()
if ($Trace) {
  # The console wrapper spawns the real (GUI subsystem) Godot process; attach dotnet-trace to that child by PID. Startup
  # suspension through DOTNET_DiagnosticPorts does not connect under the Godot host, so the first ~1 s of boot is not traced.
  $traceTool = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-trace.exe"
  $nettrace = Join-Path $outDir "trace.nettrace"
  $childName = [IO.Path]::GetFileNameWithoutExtension($GodotExe) -replace "_console$", ""
  $proc = Start-Process -FilePath $GodotExe -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError (Join-Path $outDir "stderr.log") -PassThru -NoNewWindow
  $child = $null
  for ($i = 0; $i -lt 400 -and $child -eq $null; $i++) {
    Start-Sleep -Milliseconds 25
    $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($proc.Id)" | Where-Object { $_.Name -like "$childName*" } | Select-Object -First 1
  }
  if ($child -eq $null) { throw "Godot child process not found" }
  & $traceTool collect -p $child.ProcessId --profile dotnet-common,dotnet-sampled-thread-time --format Speedscope --duration ("00:00:{0:D2}:{1:D2}" -f [int][Math]::Floor($TraceSeconds / 60), ($TraceSeconds % 60)) -o $nettrace *> (Join-Path $outDir "trace_tool.log")
  $proc.WaitForExit()
  $code = $proc.ExitCode
} else {
  & $GodotExe @arguments *> $stdout
  $code = $LASTEXITCODE
}

"process wall seconds: {0:N1}  exit code: {1}" -f $watch.Elapsed.TotalSeconds, $code | Add-Content $stdout
Select-String -Path $stdout -Pattern "Using Device|STALL_PROBE_DONE|STALL_PROBE instrumented|SCRIPT ERROR|ERROR:|exit code" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length)) }
exit $code
