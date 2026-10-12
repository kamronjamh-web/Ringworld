param([switch]$Build, [Parameter(ValueFromRemainingArguments = $true)][string[]]$EngineArguments)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$engineRoot = Join-Path $projectRoot 'artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64'
$dotnetRoot = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
$engineExecutable = Join-Path $engineRoot 'Godot_v4.8-dev7_mono_win64_console.exe'
if (!(Test-Path $engineExecutable) -or !(Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
    throw 'Install the pinned Godot 4.8 dev7 .NET archive and .NET 10 SDK in artifacts/engine-upgrade first. See docs/godot-48-native-texture-streaming.md.'
}
$oldRoot = $env:DOTNET_ROOT
$oldRootX64 = $env:DOTNET_ROOT_X64
$oldPath = $env:PATH
$localVariables = @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')
$oldVariables = @{}
foreach ($name in $localVariables) { $oldVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:PATH = "$dotnetRoot;$oldPath"
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet-cli'
    $env:NUGET_PACKAGES = Join-Path $projectRoot 'artifacts/engine-upgrade/nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot 'artifacts/engine-upgrade/nuget-http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    if ($Build) {
        # Resolve the engine SDK and its dependencies from the official archive.
        & (Join-Path $dotnetRoot 'dotnet.exe') build (Join-Path $projectRoot 'RingworldTechDemo.csproj') -p:NuGetAudit=false -v:minimal
    } else {
        & $engineExecutable --path $projectRoot @EngineArguments
    }
    $result = $LASTEXITCODE
} finally {
    $env:DOTNET_ROOT = $oldRoot
    $env:DOTNET_ROOT_X64 = $oldRootX64
    $env:PATH = $oldPath
    foreach ($name in $localVariables) { [Environment]::SetEnvironmentVariable($name, $oldVariables[$name], 'Process') }
}
exit $result
