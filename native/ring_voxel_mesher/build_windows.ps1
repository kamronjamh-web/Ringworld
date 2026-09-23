[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [ValidatePattern("^[A-Za-z0-9_-]+$")]
    [string]$RuntimeBinaryName = "ring_voxel_mesher_runtime_v7"
)

$ErrorActionPreference = "Stop"
$nativeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Resolve-Path (Join-Path $nativeRoot "..\..")
$sourcePath = Join-Path $nativeRoot "ring_voxel_mesher.cpp"
$outputDirectory = Join-Path $nativeRoot "bin\windows"
$outputPath = Join-Path $outputDirectory "$RuntimeBinaryName.dll"
$descriptorTemplate = Join-Path $nativeRoot "ring_voxel_mesher.gdextension.template"
$descriptorPath = Join-Path $nativeRoot "ring_voxel_mesher.gdextension"
$vswherePath = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path -LiteralPath $vswherePath)) {
    throw "Visual Studio Installer (vswhere.exe) was not found. Install Visual Studio Build Tools with the C++ workload."
}

$vcvarsPath = & $vswherePath -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "VC\Auxiliary\Build\vcvars64.bat"
if (-not $vcvarsPath) {
    throw "MSVC x64 tools were not found. Install the Visual Studio C++ Build Tools workload."
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$optimizationFlags = if ($Configuration -eq "Release") {
    "/O2 /Ob3 /GL /DNDEBUG"
} else {
    "/Od /Zi"
}
$compileCommand = "call `"$vcvarsPath`" && cl /nologo /std:c++20 /permissive- /W4 /EHsc /MD /LD $optimizationFlags /Fe:`"$outputPath`" `"$sourcePath`" /link /INCREMENTAL:NO"

Push-Location $outputDirectory
try {
    & $env:ComSpec /d /s /c $compileCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Native voxel mesher compilation failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}

Copy-Item -LiteralPath $descriptorTemplate -Destination $descriptorPath -Force
foreach ($intermediateName in @("ring_voxel_mesher.obj", "ring_voxel_mesher.lib", "ring_voxel_mesher.exp", "$RuntimeBinaryName.lib", "$RuntimeBinaryName.exp")) {
    $intermediatePath = Join-Path $outputDirectory $intermediateName
    if (Test-Path -LiteralPath $intermediatePath) {
        Remove-Item -LiteralPath $intermediatePath -Force
    }
}
Write-Output "Built $outputPath"
Write-Output "Activated $descriptorPath"
