<#
.SYNOPSIS
  T5-6: generates resources/rocks/prepared/rock_catalog.bin and rock_catalog.manifest.json (tools/PrepareRockAssets.cs), or with -Check regenerates in memory and compares with the committed files.
  Headless, isolated APPDATA under .godot; no import steps (the artifact is plain binary, not a Godot resource).
#>
param([switch]$SkipBuild, [switch]$Check, [int]$TimeoutSeconds = 300)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $SkipBuild) {
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'use_godot_48.ps1') -Build
  if ($LASTEXITCODE -ne 0) { throw 'Rock authoring build failed.' }
}
$engine = Join-Path $projectRoot 'artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/Godot_v4.8-dev7_mono_win64_console.exe'
$dotnet = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
$stage = Join-Path $projectRoot '.godot/prepare_rock_assets'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$savedAppData = $env:APPDATA; $savedDotnet = $env:DOTNET_ROOT; $savedDotnetX64 = $env:DOTNET_ROOT_X64; $savedPath = $env:PATH
try {
  $env:APPDATA = Join-Path $stage 'appdata'
  $env:DOTNET_ROOT = $dotnet; $env:DOTNET_ROOT_X64 = $dotnet; $env:PATH = "$dotnet;$savedPath"
  $stdout = Join-Path $stage 'run.log'; $stderr = Join-Path $stage 'run.stderr.log'
  $arguments = @('--headless', '--path', $projectRoot, '--script', 'res://tools/prepare_rock_assets.gd', '--')
  if ($Check) { $arguments += '--check' }
  $process = Start-Process -FilePath $engine -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $process.Id -Force; throw "Rock authoring timed out; see $stage" }
  if (-not (Select-String -LiteralPath $stdout -SimpleMatch 'PREPARE_ROCK_ASSETS=PASS' -Quiet)) { throw "Rock authoring failed; see $stdout and $stderr" }
  Write-Host (Select-String -LiteralPath $stdout -SimpleMatch 'PREPARE_ROCK_ASSETS=PASS' | Select-Object -First 1).Line
} finally {
  $env:APPDATA = $savedAppData; $env:DOTNET_ROOT = $savedDotnet; $env:DOTNET_ROOT_X64 = $savedDotnetX64; $env:PATH = $savedPath
}
