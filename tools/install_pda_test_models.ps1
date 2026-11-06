<#
.SYNOPSIS
  Opt-in installation of the two retained PDA test models, using tracked revision/size/SHA-256 pins.
.EXAMPLE
  .\tools\install_pda_test_models.ps1 -VerifyOnly -IncludeVisionProjector
  .\tools\install_pda_test_models.ps1 -ModelKey gemma-3-4b-q4 -IncludeVisionProjector
.NOTES
  Requires curl.exe for downloads. Does not install an SDK, build a helper, or start inference.
  Existing final files are verified and preserved; mismatches are refused. Downloads resume only owned .part files.
#>
[CmdletBinding()]
param(
    [Alias('ModelKey')][ValidateSet('qwen3-8b-q4', 'gemma-3-4b-q4')][ValidateNotNullOrEmpty()]
    [string[]]$ModelKeys = @('qwen3-8b-q4', 'gemma-3-4b-q4'),
    [switch]$IncludeVisionProjector,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$modelsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'prototypes/pda-voice/models'))
$comparisonRoot = [IO.Path]::GetFullPath((Join-Path $modelsRoot '_comparison'))

function Assert-NoReparse([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a model path through a reparse point: $current"
            }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
    }
}

function Get-ArtifactPath($Artifact) {
    $name = [string]$Artifact.FileName
    if (!$name -or [IO.Path]::GetFileName($name) -ne $name -or $name -notmatch '^[A-Za-z0-9_.-]+\.gguf$' -or
        [string]$Artifact.RelativePath -cne "_comparison/$name") { throw 'Artifact metadata contains an invalid model filename/path.' }
    $destination = [IO.Path]::GetFullPath((Join-Path $modelsRoot ([string]$Artifact.RelativePath)))
    if (!$destination.StartsWith($comparisonRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Artifact path escapes the canonical comparison-model directory.'
    }
    Assert-NoReparse $destination
    return $destination
}

function Assert-ArtifactPin($Artifact) {
    if ([long]$Artifact.Bytes -le 0 -or [string]$Artifact.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$Artifact.Revision -notmatch '^[0-9a-f]{40}$' -or [string]$Artifact.Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'Artifact metadata is missing its immutable size/SHA-256/revision pin.'
    }
    $source = if ($Artifact.PSObject.Properties.Name -contains 'SourceFile') { [string]$Artifact.SourceFile } else { [string]$Artifact.FileName }
    if (!$source -or [IO.Path]::GetFileName($source) -ne $source) { throw 'Artifact source filename is invalid.' }
    $expected = "https://huggingface.co/$($Artifact.Repo)/resolve/$($Artifact.Revision)/$($source)?download=true"
    if ([string]$Artifact.Url -cne $expected) { throw 'Artifact URL differs from its pinned HTTPS Hugging Face revision/file.' }
}

function Test-ArtifactFile([string]$Path, $Artifact) {
    Assert-NoReparse $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$Artifact.Bytes) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq [string]$Artifact.Sha256
}

$catalog = @(Get-Content -LiteralPath (Join-Path $projectRoot 'resources/pda/model_artifacts.json') -Raw | ConvertFrom-Json)
$selected = @()
foreach ($key in ($ModelKeys | Select-Object -Unique)) {
    $matchedArtifacts = @($catalog | Where-Object Key -EQ $key)
    if ($matchedArtifacts.Count -ne 1) { throw "Tracked metadata must contain exactly one entry for $key." }
    $selected += $matchedArtifacts[0]
}
if ($IncludeVisionProjector) {
    $selected += Get-Content -LiteralPath (Join-Path $projectRoot 'resources/pda/gemma_vision_projector.json') -Raw | ConvertFrom-Json
}
Assert-NoReparse $comparisonRoot
$curl = $null
$failed = $false
foreach ($artifact in $selected) {
    Assert-ArtifactPin $artifact
    $destination = Get-ArtifactPath $artifact
    if (Test-Path -LiteralPath $destination) {
        if (!(Test-ArtifactFile $destination $artifact)) {
            if (!$VerifyOnly) { throw "Installed artifact differs from its pin; refusing to overwrite: $destination" }
            [pscustomobject]@{ File = $artifact.FileName; Status = 'MISMATCH'; Bytes = $artifact.Bytes; Sha256 = $artifact.Sha256 }
            $failed = $true
            continue
        }
        [pscustomobject]@{ File = $artifact.FileName; Status = 'VERIFIED'; Bytes = $artifact.Bytes; Sha256 = $artifact.Sha256 }
        continue
    }
    if ($VerifyOnly) {
        [pscustomobject]@{ File = $artifact.FileName; Status = 'MISSING'; Bytes = $artifact.Bytes; Sha256 = $artifact.Sha256 }
        $failed = $true
        continue
    }
    if ($null -eq $curl) { $curl = (Get-Command curl.exe -CommandType Application -ErrorAction Stop).Source }
    [void](New-Item -ItemType Directory -Path $comparisonRoot -Force)
    Assert-NoReparse $comparisonRoot
    $partial = $destination + '.part'
    Assert-NoReparse $partial
    if (Test-Path -LiteralPath $partial) {
        $partialItem = Get-Item -LiteralPath $partial
        if ($partialItem.PSIsContainer -or $partialItem.Length -gt [long]$artifact.Bytes) { throw "Refusing an invalid partial artifact: $partial" }
    }
    if (!(Test-Path -LiteralPath $partial) -or (Get-Item -LiteralPath $partial).Length -lt [long]$artifact.Bytes) {
        & $curl --fail --location --proto '=https' --proto-redir '=https' --max-filesize ([long]$artifact.Bytes) --continue-at - --output $partial --url ([string]$artifact.Url)
        if ($LASTEXITCODE -ne 0) { throw "Artifact download failed (curl exit $LASTEXITCODE); the partial file was retained for resuming." }
    }
    if (!(Test-ArtifactFile $partial $artifact)) { throw "Downloaded artifact differs from its pin; partial retained and final file untouched: $partial" }
    Assert-NoReparse $destination
    [IO.File]::Move($partial, $destination) # Two-argument Move refuses to replace a concurrently installed final file.
    [pscustomobject]@{ File = $artifact.FileName; Status = 'INSTALLED'; Bytes = $artifact.Bytes; Sha256 = $artifact.Sha256 }
}
if ($failed) { exit 1 }
