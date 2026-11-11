param(
    [int]$Generation,
    [long]$Seed = 1337,
    [Parameter(Mandatory = $true)][string]$Step,
    [string]$Review,
    [int]$Slot,
    [switch]$Replace,
    [switch]$DeleteReview,
    [string]$AppData,
    [switch]$DryRun,
    [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
)

$ErrorActionPreference = "Stop"

function Get-GodotEngineArguments([string]$CommandLine) {
    # The executable is argv[0]. Quoted spaces belong to their argument.
    $tokens = [regex]::Matches($CommandLine, '(?:"[^"]*"|[^\s"])+')
    for ($index = 1; $index -lt $tokens.Count; $index++) {
        $argument = $tokens[$index].Value.Replace('"', '')
        if ($argument -ceq "--") { break }
        $argument
    }
}

function Get-WorldsMutexName([string]$WorldsRoot) {
    $normalized = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($WorldsRoot)).ToUpperInvariant()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized))
        return "Global\Ringworld.NewGenerationWorld." + [BitConverter]::ToString($digest).Replace("-", "")
    } finally {
        $hasher.Dispose()
    }
}

$project = Split-Path -Parent $PSScriptRoot
$previousAppData = $env:APPDATA
$dryProfile = $null
$worldsMutex = $null
$ownsWorldsMutex = $false
$exitCode = 1
try {
    if ($DeleteReview) {
        foreach ($argument in @("Slot", "Generation", "Seed", "Review", "Replace")) {
            if ($PSBoundParameters.ContainsKey($argument)) { throw "-DeleteReview does not take -$argument." }
        }
    }
    $profile = if ($PSBoundParameters.ContainsKey("AppData")) {
        [IO.Path]::GetFullPath($AppData)
    } else {
        [IO.Path]::GetFullPath($previousAppData)
    }
    $worldsRoot = Join-Path $profile "Godot\app_userdata\RingworldTechDemo\worlds"
    if (-not (Test-Path -LiteralPath $GodotExe -PathType Leaf)) {
        if ($PSBoundParameters.ContainsKey("GodotExe")) { throw "Godot executable not found: $GodotExe" }
        $GodotExe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
        if (-not (Test-Path -LiteralPath $GodotExe -PathType Leaf)) { throw "Pinned Godot executable not found." }
    }
    if (-not $DryRun) {
        $worldsMutex = [Threading.Mutex]::new($false, (Get-WorldsMutexName $worldsRoot))
        try {
            $ownsWorldsMutex = $worldsMutex.WaitOne(0)
        } catch [Threading.AbandonedMutexException] {
            $ownsWorldsMutex = $true
            throw "A previous world-slot command was interrupted. Inspect $worldsRoot for staging directories before retrying."
        }
        if (-not $ownsWorldsMutex) { throw "Another world-slot command is active for $worldsRoot." }
        foreach ($process in (Get-CimInstance Win32_Process -Filter "Name LIKE 'Godot%'")) {
            $commandLine = $process.CommandLine
            if ([string]::IsNullOrWhiteSpace($commandLine)) {
                throw "Cannot identify the project or mode of Godot process $($process.ProcessId)."
            }
            $engineArgs = @(Get-GodotEngineArguments $commandLine)
            if ($engineArgs -ccontains "--headless") { continue }
            $pathIndex = [Array]::IndexOf($engineArgs, "--path")
            if ($pathIndex -ge 0 -and ($pathIndex + 1) -lt $engineArgs.Count -and
                $pathIndex -eq [Array]::LastIndexOf($engineArgs, "--path")) {
                $otherPath = $engineArgs[$pathIndex + 1]
                if ([IO.Path]::IsPathFullyQualified($otherPath) -and
                    [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($otherPath)) -ne $project) {
                    continue
                }
            }
            throw "Close the non-headless Godot process $($process.ProcessId) before changing world slots."
        }
    }

    $godotArgs = @("--headless", "--path", $project, "--script", "res://tools/new_generation_world.gd")
    $toolArgs = @("--step=$Step")
    if ($DeleteReview) { $toolArgs += "--delete-review" }
    else {
        $toolArgs += "--seed=$Seed"
        if ($PSBoundParameters.ContainsKey("Slot")) { $toolArgs += "--slot=$Slot" }
        if ($PSBoundParameters.ContainsKey("Generation")) { $toolArgs += "--generation=$Generation" }
        if ($PSBoundParameters.ContainsKey("Review")) { $toolArgs += "--review=$Review" }
        if ($Replace) { $toolArgs += "--replace" }
    }
    if ($DryRun) {
        # Godot can initialize log and application-data files before the script runs.
        $dryProfile = Join-Path ([IO.Path]::GetTempPath()) ("ringworld-generation-dry-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $dryProfile | Out-Null
        $env:APPDATA = $dryProfile
        $toolArgs += @("--dry-run", "--worlds-root=$worldsRoot")
    } elseif ($PSBoundParameters.ContainsKey("AppData")) {
        $godotData = (Join-Path $project ".godot") + [IO.Path]::DirectorySeparatorChar
        if ($profile.StartsWith($godotData, [StringComparison]::OrdinalIgnoreCase)) {
            & (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply
        }
        $env:APPDATA = $profile
    }
    & $GodotExe @godotArgs "--" @toolArgs
    $exitCode = $LASTEXITCODE
} catch {
    Write-Output "NEW_GENERATION_WORLD=FAIL $($_.Exception.Message)"
} finally {
    $env:APPDATA = $previousAppData
    if ($null -ne $worldsMutex) {
        try { if ($ownsWorldsMutex) { $worldsMutex.ReleaseMutex() } }
        finally { $worldsMutex.Dispose() }
    }
    if ($null -ne $dryProfile -and (Test-Path -LiteralPath $dryProfile)) {
        $resolved = (Resolve-Path -LiteralPath $dryProfile).ProviderPath
        $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
        if ($resolved -eq $dryProfile -and [IO.Path]::GetDirectoryName($resolved) -eq $tempRoot) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
exit $exitCode
