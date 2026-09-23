[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Report', 'Open', 'Clean')]
    [string]$Action = 'Report',
    [switch]$IncludeGodotCache,
    [switch]$IncludeLegacyRepoOutput
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectFile = Join-Path $projectRoot 'project.godot'
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Expected RingworldTechDemo project.godot at '$projectFile'."
}

$applicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
if ([string]::IsNullOrWhiteSpace($applicationData)) {
    throw 'Windows ApplicationData could not be resolved.'
}

$godotUserRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $applicationData 'Godot\app_userdata\RingworldTechDemo'))
$generatedRoot = [System.IO.Path]::GetFullPath((Join-Path $godotUserRoot 'generated'))
$expectedPrefix = $godotUserRoot.TrimEnd('\') + '\'
if (-not $generatedRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to manage a generated path outside '$godotUserRoot'."
}

function Get-FolderSummary([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [pscustomobject]@{ Name = $Label; Files = 0; MiB = 0.0; Path = $Path }
    }
    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction SilentlyContinue)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    return [pscustomobject]@{
        Name = $Label
        Files = $files.Count
        MiB = [Math]::Round($bytes / 1MB, 1)
        Path = $Path
    }
}

function Get-RootLogSummary([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Force -Filter '*.log')
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    return [pscustomobject]@{
        Name = 'Legacy project-root logs'
        Files = $files.Count
        MiB = [Math]::Round($bytes / 1MB, 1)
        Path = $Root
    }
}

function Assert-NotFilesystemLink([System.IO.FileSystemInfo]$Item, [string]$Purpose) {
    $isReparsePoint = ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    if (-not $isReparsePoint) { return }
    # OneDrive Files On-Demand marks ordinary in-place files and directories as
    # reparse points with no link type or target. Permit those, but never follow
    # a symbolic link or junction during recursive cleanup.
    $hasLinkType = -not [string]::IsNullOrWhiteSpace([string]$Item.LinkType)
    $hasTarget = $null -ne $Item.Target -and @($Item.Target).Count -gt 0
    if ($hasLinkType -or $hasTarget) {
        throw "Refusing to $Purpose filesystem link '$($Item.FullName)'."
    }
}

function Assert-NoFilesystemLinksBelow([System.IO.FileSystemInfo]$Item, [string]$Purpose) {
    Assert-NotFilesystemLink -Item $Item -Purpose $Purpose
    if (-not $Item.PSIsContainer) { return }
    foreach ($descendant in Get-ChildItem -LiteralPath $Item.FullName -Force -Recurse) {
        Assert-NotFilesystemLink -Item $descendant -Purpose $Purpose
    }
}

function Clear-FolderContents([string]$Path, [string[]]$PreserveNames = @()) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $rootItem = Get-Item -LiteralPath $Path -Force
    Assert-NotFilesystemLink -Item $rootItem -Purpose 'recursively clean'
    $resolvedRoot = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $targets = @()
    foreach ($item in Get-ChildItem -LiteralPath $resolvedRoot -Force) {
        if ($PreserveNames -contains $item.Name) { continue }
        $resolvedTarget = [System.IO.Path]::GetFullPath($item.FullName)
        $targetParent = [System.IO.Path]::GetDirectoryName($resolvedTarget).TrimEnd('\')
        if (-not $targetParent.Equals($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected target '$resolvedTarget'."
        }
        Assert-NoFilesystemLinksBelow -Item $item -Purpose 'recursively remove'
        $targets += $resolvedTarget
    }
    if ($targets.Count -eq 0) { return }
    if ($PSCmdlet.ShouldProcess($resolvedRoot, "Remove $($targets.Count) generated item(s) below this exact folder")) {
        foreach ($resolvedTarget in $targets) {
            Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
        }
    }
}

function Clear-RootLogFiles([string]$Root) {
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $targets = @(Get-ChildItem -LiteralPath $resolvedRoot -File -Force -Filter '*.log')
    foreach ($target in $targets) {
        $targetParent = [System.IO.Path]::GetDirectoryName(
            [System.IO.Path]::GetFullPath($target.FullName)).TrimEnd('\')
        if (-not $targetParent.Equals($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected log '$($target.FullName)'."
        }
        Assert-NotFilesystemLink -Item $target -Purpose 'remove'
    }
    if ($targets.Count -gt 0 -and $PSCmdlet.ShouldProcess(
        $resolvedRoot, "Remove $($targets.Count) project-root log file(s)")) {
        foreach ($target in $targets) {
            Remove-Item -LiteralPath $target.FullName -Force
        }
    }
}

if ($Action -eq 'Open') {
    New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
    # A visible Explorer window is the purpose of this explicit action.
    Start-Process -FilePath 'explorer.exe' -ArgumentList @($generatedRoot)
    return
}

if ($Action -eq 'Clean') {
    Clear-FolderContents -Path $generatedRoot

    if ($IncludeGodotCache) {
        $godotCache = [System.IO.Path]::GetFullPath((Join-Path $projectRoot '.godot'))
        if (-not ([System.IO.Path]::GetDirectoryName($godotCache)).Equals(
            $projectRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected Godot cache '$godotCache'."
        }
        if (Test-Path -LiteralPath $godotCache) {
            Assert-NoFilesystemLinksBelow -Item (Get-Item -LiteralPath $godotCache -Force) `
                -Purpose 'recursively remove'
        }
        if ((Test-Path -LiteralPath $godotCache) -and $PSCmdlet.ShouldProcess(
            $godotCache, 'Remove reconstructible Godot project cache')) {
            Remove-Item -LiteralPath $godotCache -Recurse -Force
        }
    }

    if ($IncludeLegacyRepoOutput) {
        Clear-FolderContents -Path (Join-Path $projectRoot 'artifacts') -PreserveNames @('.gdignore')
        Clear-FolderContents -Path (Join-Path $projectRoot 'captures') -PreserveNames @('.gdignore')
        Clear-FolderContents -Path (Join-Path $projectRoot '.test-appdata')
        Clear-RootLogFiles -Root $projectRoot
    }
}

$summaries = @(
    Get-FolderSummary -Path $generatedRoot -Label 'Local generated output'
    Get-FolderSummary -Path (Join-Path $projectRoot '.godot') -Label 'Godot project cache'
    Get-FolderSummary -Path (Join-Path $projectRoot 'artifacts') -Label 'Legacy repo artifacts'
    Get-FolderSummary -Path (Join-Path $projectRoot 'captures') -Label 'Legacy repo captures'
    Get-FolderSummary -Path (Join-Path $projectRoot '.test-appdata') -Label 'Legacy test data'
    Get-RootLogSummary -Root $projectRoot
)
$summaries | Format-Table -AutoSize
Write-Host "Disposable local output root: $generatedRoot"
Write-Host 'World saves are outside generated/ and are never touched by this tool.'
