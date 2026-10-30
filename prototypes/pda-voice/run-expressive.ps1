param(
    [switch]$Smoke,
    [ValidateSet('cuda', 'cpu')][string]$Device = 'cuda',
    [switch]$ReportOnly,
    [switch]$DialogueR1
)
$ErrorActionPreference = 'Stop'
if ($Smoke -and $ReportOnly) { throw '-Smoke and -ReportOnly cannot be combined.' }
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runtimeRoot = Join-Path $projectRoot 'artifacts/pda-expressive'
$python = Join-Path $runtimeRoot 'venv/Scripts/python.exe'
$names = 'HF_HOME','HUGGINGFACE_HUB_CACHE','HF_ASSETS_CACHE','TORCH_HOME','TORCHINDUCTOR_CACHE_DIR','TRITON_CACHE_DIR','XDG_CACHE_HOME','NUMBA_CACHE_DIR','HF_HUB_OFFLINE','TRANSFORMERS_OFFLINE','HF_HUB_DISABLE_TELEMETRY','PYTHONUNBUFFERED'
$old = @{}
foreach ($name in $names) { $old[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$result = 0
try {
    if (!$ReportOnly) {
        if (!(Test-Path -LiteralPath $python -PathType Leaf)) { throw "Missing isolated Python environment: $python" }
        $model = Join-Path $PSScriptRoot 'models/qwen3-tts-1.7b-voice-design/download_manifest.json'
        if (!(Test-Path -LiteralPath $model -PathType Leaf)) { throw "Missing local model snapshot manifest: $model" }
        $env:HF_HOME = Join-Path $runtimeRoot 'hf'
        $env:HUGGINGFACE_HUB_CACHE = Join-Path $runtimeRoot 'hf/hub'
        $env:HF_ASSETS_CACHE = Join-Path $runtimeRoot 'hf/assets'
        $env:TORCH_HOME = Join-Path $runtimeRoot 'torch'
        $env:TORCHINDUCTOR_CACHE_DIR = Join-Path $runtimeRoot 'inductor'
        $env:TRITON_CACHE_DIR = Join-Path $runtimeRoot 'triton'
        $env:XDG_CACHE_HOME = Join-Path $runtimeRoot 'cache'
        $env:NUMBA_CACHE_DIR = Join-Path $runtimeRoot 'numba'
        $env:HF_HUB_OFFLINE = '1'
        $env:TRANSFORMERS_OFFLINE = '1'
        $env:HF_HUB_DISABLE_TELEMETRY = '1'
        $env:PYTHONUNBUFFERED = '1'
        $pythonArguments = @('-u', (Join-Path $PSScriptRoot 'expressive_audition.py'), '--device', $Device)
        if ($Smoke) { $pythonArguments += '--smoke' }
        if ($DialogueR1) { $pythonArguments += '--dialogue-r1' }
        & $python @pythonArguments
        $result = $LASTEXITCODE
    }
    if ($result -eq 0 -and !$Smoke) {
        # Child PowerShell keeps run.ps1's explicit exit from terminating this wrapper before finally restores the environment.
        $shellExe = (Get-Process -Id $PID).Path
        $reportArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'run.ps1'), 'audition-expressive-report')
        if ($DialogueR1) { $reportArguments += '--dialogue-r1' }
        & $shellExe @reportArguments
        $result = $LASTEXITCODE
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $old[$name], 'Process') }
}
exit $result
