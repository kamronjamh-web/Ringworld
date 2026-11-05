[CmdletBinding()]
param([switch]$SkipModels)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $root 'artifacts\audio-studio'
$uv = Join-Path $artifacts 'bootstrap\uv.exe'
$revision = '3241adba4fc2a85cf5b29d9eb68d42f40a28e820'
$source = Join-Path $artifacts "source\stable-audio-tools-$revision"
$archive = Join-Path $artifacts "downloads\stable-audio-tools-$revision.zip"
$archiveHash = '1D363D56C3B1C89689CE0DD2150E07B214DCD562984815F764240C25ED2C8743'
$nativeEnvironment = Join-Path $artifacts 'venv-stable-audio-tools'
$nativePython = Join-Path $nativeEnvironment 'Scripts\python.exe'
$env:UV_CACHE_DIR = Join-Path $artifacts 'cache\uv'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $artifacts 'python'
$env:UV_PYTHON_BIN_DIR = Join-Path $artifacts 'python-bin'
$env:HF_HOME = Join-Path $artifacts 'cache\huggingface'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
if (-not (Test-Path -LiteralPath $uv)) { throw 'Run setup.ps1 -SkipModels first to prepare the workspace package manager.' }
$uvVersion = & $uv --version
if ($LASTEXITCODE -ne 0 -or $uvVersion -notmatch '^uv 0\.13\.6(?:\s|$)') { throw 'Expected workspace uv 0.13.6.' }
if (-not (Test-Path -LiteralPath $archive)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $partial = "$archive.partial"
    Invoke-WebRequest -UseBasicParsing -Uri "https://codeload.github.com/Stability-AI/stable-audio-tools/zip/$revision" -OutFile $partial
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $archiveHash) { throw 'Native source archive checksum mismatch.' }
    Move-Item -LiteralPath $partial -Destination $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) { throw 'Native source archive checksum mismatch.' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'pyproject.toml'))) {
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $artifacts 'source') -Force
}
& $uv python install '3.10.22' --no-bin --no-registry
if ($LASTEXITCODE -ne 0) { throw 'Native Python installation failed.' }
if (-not (Test-Path -LiteralPath $nativePython)) {
    & $uv venv --python '3.10.22' --managed-python $nativeEnvironment
    if ($LASTEXITCODE -ne 0) { throw 'Native environment creation failed.' }
}
& $uv pip install --python $nativePython --index-url 'https://download.pytorch.org/whl/cu128' 'torch==2.7.1+cu128' 'torchaudio==2.7.1+cu128'
if ($LASTEXITCODE -ne 0) { throw 'Native CUDA PyTorch installation failed.' }
$requirements = Join-Path $PSScriptRoot 'requirements-native-lock.txt'
if (-not (Test-Path -LiteralPath $requirements)) { $requirements = Join-Path $PSScriptRoot 'requirements-native.txt' }
& $uv pip install --python $nativePython --extra-index-url 'https://download.pytorch.org/whl/cu128' --index-strategy unsafe-best-match -r $requirements $source
if ($LASTEXITCODE -ne 0) { throw 'Native inference dependency installation failed.' }
& $uv pip freeze --python $nativePython | Set-Content -LiteralPath (Join-Path $artifacts 'native-installed-packages.txt') -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw 'Native runtime package snapshot failed.' }
if (-not $SkipModels) {
    & (Join-Path $artifacts 'venv\Scripts\python.exe') (Join-Path $PSScriptRoot 'download_native.py')
    if ($LASTEXITCODE -ne 0) { throw 'Native model preparation failed.' }
}
Write-Host 'Native Stable Audio Tools runtime prepared in its separate environment.'
