[CmdletBinding()]
param(
    [string]$Godot = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot

function Resolve-GodotExecutable {
    param([string]$Requested)

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        if (-not (Test-Path -LiteralPath $Requested -PathType Leaf)) {
            throw "Godot executable was not found: $Requested"
        }
        return (Resolve-Path -LiteralPath $Requested).Path
    }

    foreach ($candidate in @('godot', 'godot4', 'Godot_v4.7.2-stable_mono_win64')) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $command) { return $command.Source }
    }

    throw 'Godot .NET executable was not found. Pass -Godot <absolute-path-to-godot.exe>.'
}

function Invoke-CertificationStage {
    param([string]$Name, [string[]]$Arguments)

    Write-Host "[$Name]"
    & $script:godotExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipBuild) {
    Write-Host '[clean-build]'
    dotnet build .\RingworldTechDemo.csproj -t:Rebuild --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "clean build failed with exit code $LASTEXITCODE" }
}

$script:godotExe = Resolve-GodotExecutable $Godot

# Parses imported project resources before the targeted headless contracts. The
# runtime smoke includes journal save/reload and the focus-residency traversal.
Invoke-CertificationStage 'project-parse' @('--headless', '--path', $projectRoot, '--editor', '--quit')
Invoke-CertificationStage 'water-regression' @('--headless', '--path', $projectRoot, 'res://scenes/WaterColumnRegressionSmoke.tscn')
Invoke-CertificationStage 'underground-environment' @('--headless', '--path', $projectRoot, 'res://scenes/UndergroundEnvironmentRuntimeSmoke.tscn')
Invoke-CertificationStage 'smooth-cave-runtime-save-reload-traversal' @('--headless', '--path', $projectRoot, 'res://scenes/SmoothTerrainRuntimeSmoke.tscn', '--', '--smooth-terrain-runtime-smoke')

Write-Host 'CAVE_CERTIFICATION_ROUTE=PASS'
