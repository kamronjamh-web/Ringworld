# Downloads prototype models/voices into ./models (skips existing, fails on empty files). Data only; nothing is executed from here except the Piper binary used by the TTS test.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$models = Join-Path $PSScriptRoot 'models'
New-Item -ItemType Directory -Force $models | Out-Null
$hf = 'https://huggingface.co'
$items = @(
    @{ Name = 'qwen2-0_5b-instruct-q4_k_m.gguf';   Url = "$hf/Qwen/Qwen2-0.5B-Instruct-GGUF/resolve/main/qwen2-0_5b-instruct-q4_k_m.gguf" },
    @{ Name = 'qwen2.5-0.5b-instruct-q4_k_m.gguf'; Url = "$hf/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf" },
    @{ Name = 'Qwen3-0.6B-Q8_0.gguf';              Url = "$hf/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q8_0.gguf" },
    # Kokoro-82M ONNX (fp32) in the format KokoroSharp expects; voices ship inside the KokoroSharp.CPU NuGet package.
    @{ Name = 'kokoro.onnx';                       Url = 'https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx' },
    @{ Name = 'piper_windows_amd64.zip';           Url = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip' },
    @{ Name = 'piper/en_US-lessac-medium.onnx';      Url = "$hf/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx" },
    @{ Name = 'piper/en_US-lessac-medium.onnx.json'; Url = "$hf/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json" },
)
foreach ($item in $items) {
    $dest = Join-Path $models $item.Name
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { Write-Host "skip  $($item.Name)"; continue }
    Write-Host "fetch $($item.Name)"
    Invoke-WebRequest -Uri $item.Url -OutFile "$dest.part" -UseBasicParsing
    if ((Get-Item "$dest.part").Length -le 0) { Remove-Item "$dest.part"; throw "Empty download: $($item.Url)" }
    Move-Item "$dest.part" $dest -Force
}
# Unpack Piper into its own directory (it is a prebuilt binary; treat as untrusted until you trust the source).
$piperDir = Join-Path $models 'piper-bin'
if (!(Test-Path (Join-Path $piperDir 'piper/piper.exe'))) {
    New-Item -ItemType Directory -Force $piperDir | Out-Null
    Expand-Archive (Join-Path $models 'piper_windows_amd64.zip') $piperDir -Force
}
Get-ChildItem $models -File | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }

