param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArguments)
$ErrorActionPreference = 'Stop'
# Pinned repo SDK + package locations, mirroring tools/use_godot_48.ps1.
$projectRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$dotnetRoot = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
if (!(Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) { throw 'Install the pinned .NET 10 SDK in artifacts/engine-upgrade first.' }
$names = 'DOTNET_ROOT','DOTNET_ROOT_X64','PATH','DOTNET_CLI_HOME','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'
$old = @{}; foreach ($n in $names) { $old[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }
try {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:PATH = "$dotnetRoot;$($old['PATH'])"
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet-cli'
    $env:NUGET_PACKAGES = Join-Path $projectRoot 'artifacts/engine-upgrade/nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot 'artifacts/engine-upgrade/nuget-http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $project = Join-Path $PSScriptRoot 'PdaVoicePrototype.csproj'
    & (Join-Path $dotnetRoot 'dotnet.exe') run --project $project -c Release -v:q -- @AppArguments
    $result = $LASTEXITCODE
} finally {
    foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $old[$n], 'Process') }
}
exit $result
