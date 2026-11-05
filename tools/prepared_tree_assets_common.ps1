function Get-PreparedTreeHash([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Prepared tree input missing: $Path" }
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-PreparedTreeInputPaths([string]$ProjectRoot) {
  $paths = New-Object System.Collections.Generic.List[string]
  foreach ($species in @('fir', 'maple', 'pine')) {
    $prefix = "resources/trees/lolipop/$species"
    foreach ($file in @('scene.bin', 'scene.gltf', 'scene.gltf.import')) { $paths.Add("$prefix/$file") }
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "$prefix/textures") -Filter '*.png.import' -File -Recurse | ForEach-Object { $paths.Add($_.FullName.Substring($ProjectRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')) }
  }
  $array = $paths.ToArray()
  [Array]::Sort($array, [StringComparer]::Ordinal)
  return $array
}

function Get-PreparedTreeImplementationPaths([string]$ProjectRoot) {
  [xml]$targets = Get-Content -LiteralPath (Join-Path $ProjectRoot 'tools/PreparedTreeAssets.targets') -Raw
  return [string[]]$targets.Project.ItemGroup.PreparedTreeImplementationInput.Include.Split(';')
}

function Get-PreparedTreeFingerprint([string]$ProjectRoot, [string[]]$Paths) {
  $ordered = [string[]]$Paths.Clone()
  [Array]::Sort($ordered, [StringComparer]::Ordinal)
  $entries = @($ordered | ForEach-Object { [pscustomobject]@{ path = $_; sha256 = (Get-PreparedTreeHash (Join-Path $ProjectRoot $_)) } })
  $canonical = (($entries | ForEach-Object { $_.path + '=' + $_.sha256 }) -join "`r`n") + "`r`n"
  $sha = [Security.Cryptography.SHA256]::Create()
  try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-', '') } finally { $sha.Dispose() }
  return [pscustomobject]@{ fingerprint = $hash; inputs = $entries }
}
