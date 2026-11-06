param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetRoot = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'The pinned .NET 10 SDK is missing from artifacts/engine-upgrade/dotnet.' }
$names = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'PATH', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:PATH = "$dotnetRoot;$($previous['PATH'])"
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts/engine-upgrade/dotnet-cli'
    $env:NUGET_PACKAGES = Join-Path $projectRoot 'artifacts/engine-upgrade/nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot 'artifacts/engine-upgrade/nuget-http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $arguments = @('build', (Join-Path $projectRoot 'prototypes/pda-voice/PdaVoicePrototype.csproj'), '-c', 'Release', '-o', (Join-Path $projectRoot 'prototypes/pda-voice/bin/qa-runtime'), '-p:NuGetAudit=false', '-v:minimal')
    if ($NoRestore) { $arguments += '--no-restore' }
    & $dotnet @arguments
    $result = $LASTEXITCODE
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
exit $result
