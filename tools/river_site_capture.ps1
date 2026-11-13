<#
.SYNOPSIS
  Runner of tools/river_site_capture.gd: stills of river and lake places in a NEW rivers world (T2R-6c, T2R-17).
.EXAMPLE
  .\tools\river_site_capture.ps1 -Places .godot\captures\landscape\t2r\dead_ends\places.json -Out .godot\captures\landscape\t2r\dead_ends
.NOTES
  Profile: one isolated profile, .godot\test_runs\appdata_<Label>, reused across runs (the user's preference files and, on first use, a copy of the shader and Vulkan caches).
  The world folder is emptied before every run. Takes the GPU queue itself (tools/gpu_queue.ps1 -Wrap). The stills and the index (-Index, default index.json) go to -Out; the console log to -Out\console_<index name>.log.
  Exit code: 0 when the script printed RIVER_SITE_CAPTURE=PASS.
#>
param(
  [Parameter(Mandatory = $true)][string]$Places,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Seed = "876374626",
  [string]$Size = "1600x900",
  [string]$Label = "t2r_capture",
  [string]$Index = "index.json",
  [int]$ArrivalTimeout = 240,
  [int]$ViewTimeout = 150,
  [int]$TimeoutSeconds = 3300,
  [string]$ProjectName = "RingworldTechDemo",
  [switch]$NoPrune,
  [switch]$InQueue
)
$ErrorActionPreference = "Stop"
$project = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$Places = (Resolve-Path $Places).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not $InQueue) {
  $inner = "& '$PSCommandPath' -InQueue -Places '$Places' -Out '$Out' -Seed $Seed -Size $Size -Label $Label -Index $Index -ArrivalTimeout $ArrivalTimeout -ViewTimeout $ViewTimeout -TimeoutSeconds $TimeoutSeconds -ProjectName $ProjectName" + $(if ($NoPrune) { " -NoPrune" } else { "" })
  & (Join-Path $project "tools\gpu_queue.ps1") -Label $Label -Wrap $inner
  exit $LASTEXITCODE
}

$manifest = Get-Content (Join-Path $project "tools\test_manifest.json") -Raw | ConvertFrom-Json
$engine = Join-Path $project ([string]$manifest.engine)
$script = Join-Path $PSScriptRoot "river_site_capture.gd"
$realRoaming = if ($env:APPDATA -and $env:APPDATA -notlike "*\.godot\*") { $env:APPDATA } else { Join-Path $env:USERPROFILE "AppData\Roaming" }
$realProfile = Join-Path $realRoaming "Godot\app_userdata\$ProjectName"
$isoRoot = Join-Path $project ".godot\test_runs\appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
if (-not $NoPrune) { & (Join-Path $project "tools\prune_test_output.ps1") -Apply -Quiet -Exclude (Split-Path -Leaf $isoRoot) | Out-Host }
if (-not (Test-Path $isoProfile)) {
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  foreach ($name in @("shader_cache", "vulkan")) {
    $source = Join-Path $realProfile $name
    if (Test-Path $source) { Copy-Item -Recurse -Force $source (Join-Path $isoProfile $name) }
  }
}
Get-ChildItem $realProfile -Filter "*.tres" -File | Copy-Item -Destination $isoProfile -Force
$worlds = Join-Path $isoProfile "worlds"
if (Test-Path $worlds) { Get-ChildItem $worlds -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $worlds | Out-Null
Set-Content (Join-Path $isoRoot ".keep_alive") (Get-Date -Format o)

if (-not ("RiverSiteCapture.Power" -as [type])) {
  Add-Type -Namespace RiverSiteCapture -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
}
$log = Join-Path $Out ("console_" + [IO.Path]::GetFileNameWithoutExtension($Index) + ".log")
$godotArgs = @("--path", "`"$project`"", "--resolution", "2560x1440", "--position", "0,0", "--script", "`"$script`"", "--",
  "--seed=$Seed", "--out=`"$Out`"", "--places=`"$Places`"", "--size=$Size", "--index=$Index", "--arrival-timeout=$ArrivalTimeout", "--view-timeout=$ViewTimeout")
$oldAppData = $env:APPDATA
$oldPreload = $env:RINGWORLD_PDA_DISABLE_PRELOAD
$env:APPDATA = $isoRoot
$env:RINGWORLD_PDA_DISABLE_PRELOAD = "1"
$exit = -1
try {
  [void][RiverSiteCapture.Power]::SetThreadExecutionState([uint32]"0x80000003")
  $proc = Start-Process -FilePath $engine -ArgumentList $godotArgs -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
  if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) { $proc.Kill(); "timed out after $TimeoutSeconds s (this process only)" | Add-Content $log } else { $exit = $proc.ExitCode }
} finally {
  [void][RiverSiteCapture.Power]::SetThreadExecutionState([uint32]"0x80000000")
  $env:APPDATA = $oldAppData; $env:RINGWORLD_PDA_DISABLE_PRELOAD = $oldPreload
}
if (Test-Path "$log.err") { Get-Content "$log.err" | Add-Content $log; Remove-Item "$log.err" }
"exit=$exit" | Add-Content $log
Select-String -Path $log -Pattern "RIVER_SITE_CAPTURE" | ForEach-Object { $_.Line } | Out-Host
if (Select-String -Path $log -Pattern "RIVER_SITE_CAPTURE=PASS" -Quiet) { exit 0 }
exit 1
