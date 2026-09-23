[CmdletBinding()]
param(
    [string]$Godot = '',
    [switch]$SkipBuild,
    [switch]$Smoke,
    [string]$Capture = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot

function Resolve-GodotExecutable {
    param([string]$Requested, [bool]$UseConsole)

    $candidate = $Requested
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        foreach ($name in @('godot', 'godot4', 'Godot_v4.7.2-stable_mono_win64')) {
            $command = Get-Command $name -ErrorAction SilentlyContinue
            if ($null -ne $command) { $candidate = $command.Source; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = Join-Path $env:USERPROFILE 'Downloads\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Godot executable was not found: $candidate. Pass -Godot <path-to-godot.exe>."
    }

    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    if ($UseConsole) {
        if ($resolved -notmatch '_console\.exe$') {
            $consoleSibling = $resolved -replace '\.exe$', '_console.exe'
            if (Test-Path -LiteralPath $consoleSibling -PathType Leaf) { return $consoleSibling }
        }
    } elseif ($resolved -match '_console\.exe$') {
        $guiSibling = $resolved -replace '_console\.exe$', '.exe'
        if (Test-Path -LiteralPath $guiSibling -PathType Leaf) { return $guiSibling }
    }
    return $resolved
}

if ($Smoke -and -not [string]::IsNullOrWhiteSpace($Capture)) {
    throw 'Use either -Smoke or -Capture, not both.'
}

if (-not $SkipBuild) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ([string]::IsNullOrWhiteSpace($dotnet)) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'dotnet was not found. Install .NET 8 SDK or add dotnet to PATH.' }
    & $dotnet build .\RingworldTechDemo.csproj --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$useConsole = $Smoke -or -not [string]::IsNullOrWhiteSpace($Capture)
$godotExe = Resolve-GodotExecutable $Godot $useConsole
$arguments = @('--path', $projectRoot, 'res://scenes/SunblockerDemo.tscn')
if ($Smoke) { $arguments = @('--headless') + $arguments + @('--', '--sunblocker-smoke') }
if (-not [string]::IsNullOrWhiteSpace($Capture)) { $arguments += @('--', "--sunblocker-capture=$Capture") }

& $godotExe @arguments
if ($LASTEXITCODE -ne 0) { throw "Godot exited with code $LASTEXITCODE." }
