param(
    [ValidateSet('template_debug', 'template_release')]
    [string]$Target = 'template_release',
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$work = Join-Path $root '.godot/terrain3d-native-src'
$tools = Join-Path $root '.godot/terrain3d-native-tools'
$llvm = Join-Path $tools 'llvm-mingw-20260922-ucrt-x86_64'
$pythonPackages = Join-Path $tools 'python'
$expectedLlvmHash = 'e3ad77d117a4bea19a7a3b333341824d79a5a371004a10e25b8504e7b3047666'

if (!(Test-Path (Join-Path $work '.git'))) { throw 'Clone Terrain3D v1.0.2-stable with --recurse-submodules into .godot/terrain3d-native-src first.' }
$sourceCommit = (git -C $work rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Terrain3D source commit.' }
if ($sourceCommit -ne '0077405b52e353c5e5dc3a094e7ede49833ba6fe') { throw 'Unexpected Terrain3D source commit.' }
$submoduleStatus = git -C $work submodule status -- godot-cpp
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Terrain3D godot-cpp submodule.' }
if ($submoduleStatus -notmatch '6388e26dd8a42071f65f764a3ef3d9523dda3d6e') { throw 'Unexpected godot-cpp submodule commit.' }
if ((Get-FileHash (Join-Path $tools 'llvm-mingw-20260922-ucrt-x86_64.zip') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedLlvmHash) { throw 'LLVM-MinGW archive hash mismatch.' }

foreach ($patchName in @('all_axis_culling.patch', 'standard_grid_diagonal.patch')) {
    $patch = Join-Path $root (Join-Path 'third_party/Terrain3D/patches' $patchName)
    $savedErrorAction = $ErrorActionPreference
    try {
        # PowerShell 5.1 can promote the expected nonzero reverse-check status to
        # a terminating native-command error under Stop, even when stderr is null.
        $ErrorActionPreference = 'Continue'
        git -C $work apply --reverse --check $patch 2>$null
        $reverseCheckExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $savedErrorAction }
    if ($reverseCheckExit -ne 0) {
        git -C $work apply --check $patch
        if ($LASTEXITCODE -ne 0) { throw "Terrain3D patch '$patchName' is neither clean nor already applied." }
        git -C $work apply $patch
        if ($LASTEXITCODE -ne 0) { throw "Terrain3D patch '$patchName' apply failed." }
    }
}

$savedPath = $env:PATH; $savedPythonPath = $env:PYTHONPATH
$savedCc = $env:CC; $savedCxx = $env:CXX; $savedAr = $env:AR; $savedRanlib = $env:RANLIB
try {
    $env:PATH = "$(Join-Path $llvm 'bin');$env:PATH"
    $env:PYTHONPATH = $pythonPackages
    $env:CC = 'x86_64-w64-mingw32-clang.exe'; $env:CXX = 'x86_64-w64-mingw32-clang++.exe'
    $env:AR = 'x86_64-w64-mingw32-ar.exe'; $env:RANLIB = 'x86_64-w64-mingw32-ranlib.exe'
    python -c "import os, SCons; expected=os.path.abspath(r'$pythonPackages'); actual=os.path.abspath(SCons.__file__); assert actual.startswith(expected), (actual, expected); assert getattr(SCons, '__version__', None) == '4.10.1', getattr(SCons, '__version__', None)"
    if ($LASTEXITCODE -ne 0) { throw 'Pinned SCons 4.10.1 is unavailable.' }
    Push-Location $work
    try { python -c "from SCons.Script.Main import main; main()" platform=windows target=$Target arch=x86_64 use_mingw=yes -j 8 }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Terrain3D SCons build failed.' }
}
finally {
    $env:PATH = $savedPath; $env:PYTHONPATH = $savedPythonPath
    $env:CC = $savedCc; $env:CXX = $savedCxx; $env:AR = $savedAr; $env:RANLIB = $savedRanlib
}

$flavor = if ($Target -eq 'template_debug') { 'debug' } else { 'release' }
$source = Join-Path $work "project/addons/terrain_3d/bin/libterrain.windows.$flavor.x86_64.dll"
$destination = Join-Path $root "addons/terrain_3d/bin/libterrain.ringworld.windows.$flavor.x86_64.dll"
if (!(Test-Path $source)) { throw "Expected build artifact is missing: $source" }
if ($BuildOnly) {
    Get-FileHash -Algorithm SHA256 $source
    return
}
Copy-Item -LiteralPath $source -Destination $destination -Force
& (Join-Path $llvm 'bin/llvm-objdump.exe') -p $destination | Select-String 'DLL Name'
if ($LASTEXITCODE -ne 0) { throw 'llvm-objdump dependency inspection failed.' }
Get-FileHash -Algorithm SHA256 $destination
