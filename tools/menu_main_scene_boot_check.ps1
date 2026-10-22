# Boots the project exactly like F5 (StartMenu as the main scene, no --script) against an isolated APPDATA, and checks that
# the live world warm-loads and the menu unlocks for "quiet" (not "no_world"). Script-driven tests add the menu after the tree is
# ready, so they cannot catch _ready-time failures such as adding nodes to a root that is still setting up its children.
#
# Usage (pwsh):  .\tools\menu_main_scene_boot_check.ps1 -AppData <isolated APPDATA dir containing Godot\app_userdata\RingworldTechDemo>
#                [-Seconds 120]
# The isolated profile needs an eligible (runtime-generation Standard) most-recent slot. Never point it at the real %APPDATA%.
param(
  [Parameter(Mandatory = $true)][string]$AppData,
  [int]$Seconds = 120,
  [string]$Godot = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
)
$ErrorActionPreference = "Stop"
if ($AppData.TrimEnd('\') -ieq $env:APPDATA.TrimEnd('\')) { throw "Refusing to run against the real APPDATA." }
if (-not (Test-Path (Join-Path $AppData "Godot\app_userdata\RingworldTechDemo"))) { throw "No RingworldTechDemo profile under $AppData." }
$project = Split-Path -Parent $PSScriptRoot
$out = Join-Path $AppData "main_scene_boot.log"; $err = Join-Path $AppData "main_scene_boot.err.log"
$old = $env:APPDATA
$env:APPDATA = $AppData
try {
  $proc = Start-Process -FilePath $Godot -ArgumentList @("--path", "`"$project`"") -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
} finally { $env:APPDATA = $old }
$deadline = (Get-Date).AddSeconds($Seconds)
$unlock = $null
while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
  Start-Sleep -Seconds 2
  $unlock = Select-String -Path $out -Pattern "MENU_UNLOCK .*reason=(\w+)" -ErrorAction SilentlyContinue | Select-Object -Last 1
  if ($unlock -and $unlock.Matches[0].Groups[1].Value -ne "no_world") { break }
}
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
$busy = Select-String -Path $err -Pattern "Parent node is busy setting up children" -ErrorAction SilentlyContinue
$reason = if ($unlock) { $unlock.Matches[0].Groups[1].Value } else { "none" }
if ($busy) { "MENU_MAIN_SCENE_BOOT=FAIL add_child during root setup"; exit 1 }
if ($reason -ne "quiet" -and $reason -ne "cap") { "MENU_MAIN_SCENE_BOOT=FAIL unlock_reason=$reason"; exit 1 }
"MENU_MAIN_SCENE_BOOT=PASS unlock_reason=$reason"
exit 0
