<#
.SYNOPSIS
  One-click optimized play-test: builds the C# assembly with optimizations, launches the game, and puts the Debug assembly back afterwards.
.EXAMPLE
  .\tools\play.ps1                      # optimized build + launch (what you want for judging performance)
  .\tools\play.ps1 -Debug               # normal Debug build (use_godot_48.ps1 -Build) + launch; for stepping through problems
  .\tools\play.ps1 -SkipBuild           # reuse the last optimized build (bin\OptRelease) without rebuilding
  .\tools\play.ps1 -EngineArgs "--quit-after 300"  # extra engine arguments, space separated (here: smoke start and exit)
.NOTES
  The Godot mono engine binary always loads .godot\mono\temp\bin\Debug\RingworldTechDemo.dll, so "optimized" means the Debug
  configuration built with -p:Optimize=true into bin\OptRelease\ and temporarily copied into the Debug slot (same method as
  tools/quality_preset_capture.ps1 -CSharpConfig Release). The Debug dll/pdb are saved to bin\DebugPlaySaved\ and restored when the
  game exits, with their original timestamps, so tools/run_tests.ps1 still sees an
  up-to-date Debug assembly and rebuilds only when sources changed. If the script is killed before the restore, run
  .\tools\play.ps1 -Restore (or just run tools/run_tests.ps1 / use_godot_48.ps1 -Build, which rebuild Debug).
  The engine is the windowed (non-console) editor build; the Godot EDITOR holds ~2.6 GB VRAM, so close it first. This script only
  warns about a running editor and never kills any process.
#>
param(
    [switch]$Debug,
    [switch]$SkipBuild,
    [switch]$Restore,
    [string]$EngineArgs = ''
)
# Not an advanced function on purpose: [CmdletBinding]/[Parameter] would add the common -Debug parameter and clash with ours.
$EngineArguments = @($EngineArgs -split '\s+' | Where-Object { $_ })
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$name = 'RingworldTechDemo'
$engineRoot = Join-Path $project 'artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64'
$engineExe = Join-Path $engineRoot 'Godot_v4.8-dev7_mono_win64.exe'
$dotnetRoot = Join-Path $project 'artifacts\engine-upgrade\dotnet'
$monoTemp = Join-Path $project '.godot\mono\temp'
$slotDir = Join-Path $monoTemp 'bin\Debug'
$savedDir = Join-Path $monoTemp 'bin\DebugPlaySaved'
$optDir = Join-Path $monoTemp 'bin\OptRelease'
$slotDll = Join-Path $slotDir "$name.dll"; $slotPdb = Join-Path $slotDir "$name.pdb"
$savedDll = Join-Path $savedDir "$name.dll"; $savedPdb = Join-Path $savedDir "$name.pdb"
$optDll = Join-Path $optDir "$name.dll"; $optPdb = Join-Path $optDir "$name.pdb"

function Restore-DebugSlot {
    if (-not (Test-Path $savedDll)) { return }
    Copy-Item -Force $savedDll $slotDll
    if (Test-Path $savedPdb) { Copy-Item -Force $savedPdb $slotPdb }
    Remove-Item -Force $savedDll, $savedPdb -ErrorAction SilentlyContinue
    Write-Host "Debug assembly restored in $slotDir."
}
if ($Restore) { Restore-DebugSlot; return }

if (!(Test-Path $engineExe)) { throw "Engine not found at $engineExe (see tools/use_godot_48.ps1 for the pinned install)." }

# Warn about the editor (it holds ~2.6 GB VRAM). Never kill it.
$editors = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'Godot%'" | Where-Object { $_.CommandLine -match '(^|\s)(--editor|-e)(\s|$)' })
if ($editors.Count -gt 0) {
    Write-Warning ("The Godot editor is running (PID {0}); it holds about 2.6 GB VRAM and will slow or starve the game. Close it first if the game struggles. Not killing it." -f (($editors | ForEach-Object ProcessId) -join ', '))
}

# A previous run that died before its restore left the optimized assembly in the Debug slot; put Debug back before building.
Restore-DebugSlot

# Same pinned SDK environment as tools/use_godot_48.ps1.
$envNames = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'PATH', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')
$swapped = $false
try {
    if ($Debug) {
        if (-not $SkipBuild) {
            Write-Host 'Building Debug C# (tools/use_godot_48.ps1 -Build) ...'
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'use_godot_48.ps1') -Build
            if ($LASTEXITCODE -ne 0) { throw "Debug C# build failed (exit $LASTEXITCODE)." }
        }
    } else {
        if (-not $SkipBuild) {
            if (!(Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) { throw "Pinned .NET SDK missing at $dotnetRoot (see tools/use_godot_48.ps1)." }
            $oldEnv = @{}
            foreach ($n in $envNames) { $oldEnv[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }
            try {
                $env:DOTNET_ROOT = $dotnetRoot
                $env:DOTNET_ROOT_X64 = $dotnetRoot
                $env:PATH = "$dotnetRoot;$($oldEnv['PATH'])"
                $env:DOTNET_CLI_HOME = Join-Path $project 'artifacts\engine-upgrade\dotnet-cli'
                $env:NUGET_PACKAGES = Join-Path $project 'artifacts\engine-upgrade\nuget-packages'
                $env:NUGET_HTTP_CACHE_PATH = Join-Path $project 'artifacts\engine-upgrade\nuget-http-cache'
                $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
                $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
                $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
                Write-Host "Building optimized C# assembly into $optDir ..."
                # Godot.NET.Sdk sets OutputPath unconditionally; command-line properties override it. Own obj dir keeps the Debug build untouched.
                & (Join-Path $dotnetRoot 'dotnet.exe') build (Join-Path $project "$name.csproj") -c Debug -p:Optimize=true -p:NuGetAudit=false -v:minimal "-p:OutputPath=$optDir\" "-p:IntermediateOutputPath=$(Join-Path $monoTemp 'obj\OptRelease')\"
                $buildExit = $LASTEXITCODE
            } finally {
                foreach ($n in $envNames) { [Environment]::SetEnvironmentVariable($n, $oldEnv[$n], 'Process') }
            }
            if ($buildExit -ne 0) { throw "Optimized C# build failed (exit $buildExit)." }
        }
        if (!(Test-Path $optDll)) { throw "Missing $optDll (run without -SkipBuild first)." }
        New-Item -ItemType Directory -Force $savedDir, $slotDir | Out-Null
        if (Test-Path $slotDll) { Copy-Item -Force $slotDll $savedDll }
        if (Test-Path $slotPdb) { Copy-Item -Force $slotPdb $savedPdb }
        $swapped = $true
        Copy-Item -Force $optDll $slotDll
        if (Test-Path $optPdb) { Copy-Item -Force $optPdb $slotPdb }
        Write-Host "Optimized assembly in the Debug slot ($slotDll)."
    }
    Write-Host "Launching $engineExe"
    $p = Start-Process -PassThru -FilePath $engineExe -ArgumentList (@('--path', "`"$project`"") + @($EngineArguments))
    $p.WaitForExit()
    Write-Host "Game exited (code $($p.ExitCode))."
} finally {
    if ($swapped) { Restore-DebugSlot }
}
