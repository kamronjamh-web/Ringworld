<#
.SYNOPSIS
  Keeps test output under .godot from growing without bound. Test runners copy a saved world plus the shader/vulkan/ring
  caches into an isolated APPDATA profile (up to ~2.5 GB each) and write captures next to it; nothing removed them, so .godot
  reached 160 GB. This deletes test output only: the project caches and the reference baselines below are never touched.
.EXAMPLE
  .\tools\prune_test_output.ps1                 # dry run: lists what the default policy would delete
  .\tools\prune_test_output.ps1 -Apply          # apply the default policy (the test runners call this before every run)
  .\tools\prune_test_output.ps1 -All -Apply     # delete ALL test output (keeps only the project caches and baselines)
.NOTES
  Policy, in order:
    1. isolated profiles (any directory named appdata_*) older than -ProfileMaxAgeHours;
    2. any other test output older than -MaxAgeDays;
    3. oldest-first until the remaining test output is under -MaxTotalGB.
  -Exclude names entries (top-level names or appdata_* directory names) to keep this time, e.g. the profile a runner reuses.
#>
param(
  [switch]$Apply,
  [switch]$All,
  [switch]$Quiet,
  [double]$ProfileMaxAgeHours = 24,
  [double]$MaxAgeDays = 7,
  [double]$MaxTotalGB = 20,
  [string[]]$Exclude = @()
)
$ErrorActionPreference = "Stop"
$godotDir = Join-Path (Split-Path -Parent $PSScriptRoot) ".godot"
if (-not (Test-Path $godotDir)) { return }

# Engine/editor caches and the reference images other tools compare against (docs/shadow-revamp-plan.md).
$keep = @(
  ".gdignore", "editor", "extension_list.cfg", "global_script_class_cache.cfg", "imported", "mono",
  "scene_groups_cache.cfg", "shader_cache", "uid_cache.bin",
  "shadow_baseline", "s5_before", "shadow_compose_identity_head_clouds", "shadow_compose_identity_storm_revamp_4070",
  "terrain3d-native-src", "terrain3d-native-tools"
) + $Exclude

function Get-EntrySize([IO.FileSystemInfo]$entry) {
  if ($entry -is [IO.FileInfo]) { return $entry.Length }
  $sum = (Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
  if ($sum) { return [long]$sum } else { return [long]0 }
}

function Get-Newest([IO.FileSystemInfo]$entry) {
  if ($entry -is [IO.FileInfo]) { return $entry.LastWriteTime }
  $newest = Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($newest) { return $newest.LastWriteTime } else { return $entry.LastWriteTime }
}

# Candidates: isolated profiles nested one level down (e.g. phase_c_live\appdata_x) count on their own, so a folder that
# holds both recent captures and stale profiles loses the profiles first.
$candidates = New-Object System.Collections.Generic.List[object]
foreach ($top in Get-ChildItem -LiteralPath $godotDir -Force) {
  if ($keep -contains $top.Name) { continue }
  if ($top -is [IO.DirectoryInfo] -and $top.Name -notlike "appdata_*") {
    foreach ($child in Get-ChildItem -LiteralPath $top.FullName -Force -Directory -Filter "appdata_*") {
      if ($keep -contains $child.Name) { continue }
      $candidates.Add([pscustomobject]@{ Entry = $child; Profile = $true; Size = (Get-EntrySize $child); Newest = (Get-Newest $child) })
    }
  }
  $candidates.Add([pscustomobject]@{ Entry = $top; Profile = ($top.Name -like "appdata_*"); Size = 0; Newest = (Get-Newest $top) })
}
# Size of a top-level folder excludes its nested profiles (they are separate candidates).
foreach ($c in $candidates) {
  if ($c.Profile) { continue }
  $nested = ($candidates | Where-Object { $_.Profile -and $_.Entry.FullName.StartsWith($c.Entry.FullName + [IO.Path]::DirectorySeparatorChar) } | Measure-Object Size -Sum).Sum
  $c.Size = [long]((Get-EntrySize $c.Entry) - [long]$nested)
}

$now = Get-Date
$doomed = New-Object System.Collections.Generic.List[object]
foreach ($c in $candidates) {
  $ageHours = ($now - $c.Newest).TotalHours
  if ($All -or ($c.Profile -and $ageHours -gt $ProfileMaxAgeHours) -or ($ageHours -gt $MaxAgeDays * 24)) { $doomed.Add($c) }
}
$remaining = $candidates | Where-Object { $doomed -notcontains $_ } | Sort-Object Newest
$remainingBytes = ($remaining | Measure-Object Size -Sum).Sum
$capBytes = $MaxTotalGB * 1GB
foreach ($c in $remaining) {
  if ($remainingBytes -le $capBytes) { break }
  $doomed.Add($c)
  $remainingBytes -= $c.Size
}

$freedBytes = ($doomed | Measure-Object Size -Sum).Sum
if (-not $Quiet) {
  foreach ($c in $doomed | Sort-Object Size -Descending) {
    "{0,9:N2} GB  {1:yyyy-MM-dd HH:mm}  {2}" -f ($c.Size / 1GB), $c.Newest, $c.Entry.FullName.Substring($godotDir.Length + 1)
  }
}
$verb = if ($Apply) { "Deleting" } else { "Would delete (dry run, pass -Apply)" }
"{0} {1} entries, {2:N2} GB; test output left: {3:N2} GB" -f $verb, $doomed.Count, ($freedBytes / 1GB), ([double]$remainingBytes / 1GB)
if (-not $Apply) { return }
# Nested profiles first, then their parents.
foreach ($c in $doomed | Sort-Object { $_.Entry.FullName.Length } -Descending) {
  $path = $c.Entry.FullName
  if (-not (Test-Path -LiteralPath $path)) { continue }
  # A folder holding a kept or not-doomed profile (e.g. the one this run reuses) loses everything else but that profile.
  $survivors = $candidates | Where-Object { $_.Profile -and $doomed -notcontains $_ -and $_.Entry.FullName.StartsWith($path + [IO.Path]::DirectorySeparatorChar) }
  $keptNested = Get-ChildItem -LiteralPath $path -Force -Directory -Filter "appdata_*" -ErrorAction SilentlyContinue | Where-Object { $keep -contains $_.Name }
  if ($c.Entry -is [IO.DirectoryInfo] -and ($survivors -or $keptNested)) {
    $spare = @($survivors | ForEach-Object { $_.Entry.Name }) + @($keptNested | ForEach-Object { $_.Name })
    Get-ChildItem -LiteralPath $path -Force | Where-Object { $spare -notcontains $_.Name } |
      ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Continue }
  } else {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Continue
  }
}
