param([switch]$SkipBuild, [int]$TimeoutSeconds = 600)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'prepared_tree_assets_common.ps1')
$stageRelative = '.godot/prepared_tree_assets/' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fffffff')
$stageDirectory = Join-Path $projectRoot $stageRelative
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'prune_test_output.ps1') -Apply -Quiet -Exclude prepared_tree_assets
if (-not $SkipBuild) {
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'use_godot_48.ps1') -Build
  if ($LASTEXITCODE -ne 0) { throw 'Prepared tree authoring build failed.' }
}
$engine = Join-Path $projectRoot 'artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/Godot_v4.8-dev7_mono_win64_console.exe'
$dotnet = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
$savedAppData = $env:APPDATA
$savedDotnet = $env:DOTNET_ROOT
$savedDotnetX64 = $env:DOTNET_ROOT_X64
$savedPath = $env:PATH
$terrainReloadDll = Join-Path $projectRoot 'addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll'
$terrainReloadInitiallyPresent = Test-Path -LiteralPath $terrainReloadDll
$terrainReloadInitialBytes = if ($terrainReloadInitiallyPresent) { [IO.File]::ReadAllBytes($terrainReloadDll) } else { $null }

function Invoke-TreeAuthoringStep([string]$Name, [string[]]$Arguments, [string]$Marker) {
  $stdout = Join-Path $stageDirectory "$Name.log"
  $stderr = Join-Path $stageDirectory "$Name.stderr.log"
  $process = Start-Process -FilePath $engine -ArgumentList (@('--headless', '--path', $projectRoot) + $Arguments) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $markerAt = $null
  while (-not $process.WaitForExit(1000)) {
    if ($null -eq $markerAt -and (Select-String -LiteralPath $stdout -SimpleMatch $Marker -Quiet)) { $markerAt = $watch.Elapsed.TotalSeconds }
    if ($null -ne $markerAt -and $watch.Elapsed.TotalSeconds - $markerAt -ge 10) { Stop-Process -Id $process.Id -Force; break }
    if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds) { Stop-Process -Id $process.Id -Force; throw "Tree authoring $Name timed out; see $stageDirectory" }
  }
  [void]$process.WaitForExit(5000)
  if (-not (Select-String -LiteralPath $stdout -SimpleMatch $Marker -Quiet)) { throw "Tree authoring $Name failed; see $stageDirectory" }
  if (Select-String -LiteralPath $stderr -Pattern 'SCRIPT ERROR|Parse Error' -Quiet) { throw "Tree authoring $Name script error; see $stderr" }
  if ($Name -eq 'import' -and (Select-String -LiteralPath $stderr -Pattern '(?i)(Error importing|Cannot open file from path|Failed.*import).*lolipop/(fir|maple|pine)/scene\.gltf' -Quiet)) { throw "Tree source import failed; see $stderr" }
}

try {
  $env:APPDATA = Join-Path $projectRoot '.godot/prepared_tree_assets/appdata'
  $env:DOTNET_ROOT = $dotnet
  $env:DOTNET_ROOT_X64 = $dotnet
  $env:PATH = "$dotnet;$savedPath"
  $beforeImport = Get-PreparedTreeFingerprint $projectRoot (Get-PreparedTreeInputPaths $projectRoot)
  $beforeImport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stageDirectory 'inputs_before_import.json') -Encoding UTF8
  Invoke-TreeAuthoringStep 'import' @('--editor', '--script', 'res://tools/prepare_tree_assets_editor.gd') 'PREPARE_TREE_IMPORTS=PASS'
  $beforeBake = Get-PreparedTreeFingerprint $projectRoot (Get-PreparedTreeInputPaths $projectRoot)
  $beforeBake | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stageDirectory 'inputs_before_bake.json') -Encoding UTF8
  $stageArtifact = "$stageRelative/lolipop_catalog.res"
  $stageManifest = "$stageRelative/lolipop_catalog.manifest.json"
  Invoke-TreeAuthoringStep 'bake' @('--script', 'res://tools/prepare_tree_assets.gd', '--', "--out=res://$stageArtifact", "--manifest=res://$stageManifest") 'PREPARE_TREE_ASSETS=PASS'
  $afterBake = Get-PreparedTreeFingerprint $projectRoot (Get-PreparedTreeInputPaths $projectRoot)
  if ($beforeBake.fingerprint -cne $afterBake.fingerprint) { throw 'Tree authoring inputs changed during baking; staged output was not promoted.' }
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check_prepared_tree_assets.ps1') -ProjectRoot $projectRoot -ArtifactPath $stageArtifact -ManifestPath $stageManifest
  if ($LASTEXITCODE -ne 0) { throw 'Staged tree artifact validation failed; existing artifact was not changed.' }
  $manifest = Get-Content -LiteralPath (Join-Path $projectRoot $stageManifest) -Raw | ConvertFrom-Json
  $manifest.artifact_path = 'resources/trees/prepared/lolipop_catalog.res'
  $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $projectRoot $stageManifest) -Encoding UTF8
  $destination = Join-Path $projectRoot 'resources/trees/prepared'
  New-Item -ItemType Directory -Force -Path $destination | Out-Null
  $backups = @{}
  try {
    foreach ($file in @('lolipop_catalog.res', 'lolipop_catalog.manifest.json')) {
      $target = Join-Path $destination $file
      if (Test-Path -LiteralPath $target) {
        $backup = Join-Path $stageDirectory "$file.previous"
        Copy-Item -LiteralPath $target -Destination $backup
        $backups[$file] = $backup
      } else { $backups[$file] = $null }
      Copy-Item -LiteralPath (Join-Path $stageDirectory $file) -Destination $target -Force
    }
  } catch {
    foreach ($file in $backups.Keys) {
      $target = Join-Path $destination $file
      if ($backups[$file]) { Copy-Item -LiteralPath $backups[$file] -Destination $target -Force }
      elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    throw
  }
  Write-Host "PREPARE_TREE_ASSETS_TOOL=PASS evidence=$stageDirectory"
} finally {
  if ($terrainReloadInitiallyPresent -and -not (Test-Path -LiteralPath $terrainReloadDll)) { [IO.File]::WriteAllBytes($terrainReloadDll, $terrainReloadInitialBytes) }
  $env:APPDATA = $savedAppData
  $env:DOTNET_ROOT = $savedDotnet
  $env:DOTNET_ROOT_X64 = $savedDotnetX64
  $env:PATH = $savedPath
}
