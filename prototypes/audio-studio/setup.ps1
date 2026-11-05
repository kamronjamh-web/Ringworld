[CmdletBinding()]
param(
    [switch]$SkipModels,
    [ValidateSet('all', 'ace-step', 'stable-audio-open')][string]$Engine = 'all'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $root 'artifacts\audio-studio'
$revision = 'ca1e85fe9430179831e6bc6be790c332190a3866'
$source = Join-Path $artifacts "source\ACE-Step-1.5-$revision"
$uv = Join-Path $artifacts 'bootstrap\uv.exe'
$env:UV_CACHE_DIR = Join-Path $artifacts 'cache\uv'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $artifacts 'python'
$env:UV_PYTHON_BIN_DIR = Join-Path $artifacts 'python-bin'
$env:UV_PROJECT_ENVIRONMENT = Join-Path $artifacts 'venv'
$env:HF_HOME = Join-Path $artifacts 'cache\huggingface'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:MPLCONFIGDIR = Join-Path $artifacts 'cache\matplotlib'
$env:NUMBA_CACHE_DIR = Join-Path $artifacts 'cache\numba'
$env:TORCH_HOME = Join-Path $artifacts 'cache\torch'
$env:GRADIO_TEMP_DIR = Join-Path $artifacts 'cache\gradio'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
foreach ($folder in @('bootstrap', 'downloads', 'source', 'cache', 'python-bin', 'cache\matplotlib', 'cache\numba', 'cache\torch', 'cache\gradio')) {
    New-Item -ItemType Directory -Path (Join-Path $artifacts $folder) -Force | Out-Null
}
function Get-VerifiedArchive([string]$Uri, [string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $partial = "$Path.partial"
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) {
            throw "Archive checksum mismatch: $Path"
        }
        Move-Item -LiteralPath $partial -Destination $Path -Force
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        throw "Archive checksum mismatch: $Path"
    }
}
if (-not (Test-Path -LiteralPath $uv)) {
    $archive = Join-Path $artifacts 'downloads\uv.zip'
    Get-VerifiedArchive 'https://github.com/astral-sh/uv/releases/download/0.13.6/uv-x86_64-pc-windows-msvc.zip' $archive 'A2A8820D5EEA32F08EF467EF3B7F66883A9B076D5737A2FC2FBB5F458D1BB50F'
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $artifacts 'bootstrap') -Force
}
$uvVersion = & $uv --version
if ($LASTEXITCODE -ne 0 -or $uvVersion -notmatch '^uv 0\.13\.6(?:\s|$)') { throw 'Expected workspace uv 0.13.6.' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'uv.lock'))) {
    $archive = Join-Path $artifacts 'downloads\ace-step.zip'
    Get-VerifiedArchive "https://github.com/ace-step/ACE-Step-1.5/archive/$revision.zip" $archive '108E45DFFC7EB62AF98D781C4159AA63A2B7A241186734E3E065279DA7FC198D'
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $artifacts 'source') -Force
}
& $uv python install '3.12.16' --no-bin
if ($LASTEXITCODE -ne 0) { throw 'Workspace Python installation failed.' }
& $uv sync --frozen --no-dev --python '3.12.16' --managed-python --project $source
if ($LASTEXITCODE -ne 0) { throw 'Audio studio dependency installation failed.' }
& $uv pip install --python (Join-Path $artifacts 'venv\Scripts\python.exe') -r (Join-Path $PSScriptRoot 'requirements-studio.txt')
if ($LASTEXITCODE -ne 0) { throw 'Audio studio model-specific dependency installation failed.' }
Set-Content -LiteralPath (Join-Path $artifacts 'ace-source-revision.txt') -Value $revision -Encoding ASCII
if (-not $SkipModels) {
    & (Join-Path $artifacts 'venv\Scripts\python.exe') (Join-Path $PSScriptRoot 'download_models.py') --engine $Engine
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Model setup is incomplete. See the download instructions above; verified models and outputs were preserved.'
        exit 2
    }
}
Write-Host 'Audio studio setup complete. Start with prototypes\audio-studio\run.ps1.'
