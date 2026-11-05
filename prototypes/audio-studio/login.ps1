[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifacts = Join-Path $root 'artifacts\audio-studio'
$env:HF_HOME = Join-Path $artifacts 'cache\huggingface'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$hf = Join-Path $artifacts 'venv\Scripts\hf.exe'
if (-not (Test-Path -LiteralPath $hf)) { throw 'Run setup.ps1 -SkipModels first.' }
Write-Host 'Log in to Hugging Face using a read token. The interactive prompt hides the token.'
Write-Host 'Credentials are stored only in the workspace HF_HOME. Answer No to Git credential storage.'
& $hf auth login
if ($LASTEXITCODE -ne 0) { throw 'Hugging Face login failed.' }
