param(
  [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
  [string]$ImplementationIdentity = '',
  [string]$ArtifactPath = 'resources/trees/prepared/lolipop_catalog.res',
  [string]$ManifestPath = 'resources/trees/prepared/lolipop_catalog.manifest.json'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'prepared_tree_assets_common.ps1')
try {
  $ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
  foreach ($required in @($ArtifactPath, $ManifestPath)) {
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $required) -PathType Leaf)) { throw "Prepared tree file missing: $required" }
  }
  $source = Get-PreparedTreeFingerprint $ProjectRoot (Get-PreparedTreeInputPaths $ProjectRoot)
  $implementation = Get-PreparedTreeFingerprint $ProjectRoot (Get-PreparedTreeImplementationPaths $ProjectRoot)
  if ($ImplementationIdentity -and $ImplementationIdentity -cne $implementation.fingerprint) { throw 'Compiled implementation identity differs from current authoring sources.' }
  $manifest = Get-Content -LiteralPath (Join-Path $ProjectRoot $ManifestPath) -Raw | ConvertFrom-Json
  if ($manifest.schema_version -ne 1 -or $manifest.bake_version -ne 1 -or $manifest.catalog_version -ne 1) { throw 'Prepared tree schema/bake/catalog version mismatch.' }
  if ($manifest.artifact_path -cne 'resources/trees/prepared/lolipop_catalog.res') { throw 'Prepared tree artifact destination mismatch.' }
  if ($manifest.parity_verified -isnot [bool] -or -not $manifest.parity_verified) { throw 'Prepared tree parity was not verified.' }
  # Compatibility identity uses the runtime version string plus the pinned nine-character commit prefix.
  if ($manifest.engine_identity -cne '4.8-dev7 (official)|c971f93e7') { throw 'Prepared tree engine identity mismatch.' }
  if ($manifest.implementation_identity -cne $implementation.fingerprint) { throw 'Prepared tree implementation is stale; run tools/prepare_tree_assets.ps1.' }
  if ($manifest.artifact_sha256 -cne (Get-PreparedTreeHash (Join-Path $ProjectRoot $ArtifactPath))) { throw 'Prepared tree artifact hash mismatch.' }
  $recorded = @($manifest.inputs)
  if ($recorded.Count -ne $source.inputs.Count) {
    $changedPaths = Compare-Object -ReferenceObject @($recorded | ForEach-Object { $_.path }) -DifferenceObject @($source.inputs | ForEach-Object { $_.path }) -CaseSensitive | ForEach-Object { $_.InputObject }
    throw "Prepared tree input set changed: $($changedPaths -join ', ')"
  }
  for ($i = 0; $i -lt $recorded.Count; $i++) {
    if ($recorded[$i].path -cne $source.inputs[$i].path -or $recorded[$i].sha256 -cne $source.inputs[$i].sha256) { throw "Prepared tree input mismatch: $($source.inputs[$i].path)" }
  }
  if ($manifest.source_fingerprint -cne $source.fingerprint) { throw 'Prepared tree authoring fingerprint is stale despite matching input records.' }
  Write-Host 'PREPARED_TREE_ASSETS=PASS'
  exit 0
} catch {
  Write-Error "PREPARED_TREE_ASSETS=FAIL $($_.Exception.Message) Rebuild with: powershell -NoProfile -ExecutionPolicy Bypass -File tools/prepare_tree_assets.ps1" -ErrorAction Continue
  exit 1
}
