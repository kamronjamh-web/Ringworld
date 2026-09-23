param(
    [ValidateSet('all', 'scale', 'clouds', 'voxels')]
    [string]$Topic = 'all',
    [ValidateRange(24, 60)]
    [int]$FramesPerSecond = 30,
    [ValidateRange(6, 30)]
    [int]$DurationSeconds = 12,
    [ValidateRange(3, 30)]
    [int]$PreRollSeconds = 8,
    [string]$GodotExecutable = '',
    [string]$FfmpegExecutable = '',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $applicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    $OutputRoot = Join-Path $applicationData 'Godot\app_userdata\RingworldTechDemo\generated'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$projectPrefix = $projectRoot.TrimEnd('\') + '\'
if ($OutputRoot.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputRoot.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Shorts output must remain outside the OneDrive project checkout.'
}
$captureDirectory = Join-Path $OutputRoot 'captures\shorts'
New-Item -ItemType Directory -Force -Path $captureDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($GodotExecutable)) {
    if (-not [string]::IsNullOrWhiteSpace($env:GODOT4) -and (Test-Path -LiteralPath $env:GODOT4)) {
        $GodotExecutable = $env:GODOT4
    }
    else {
        $knownGodotCandidates = @(
            'C:\Users\kamronh\Downloads\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe',
            'C:\Users\Kamron\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
        )
        $knownGodot = $knownGodotCandidates | Where-Object {
            Test-Path -LiteralPath $_ -PathType Leaf
        } | Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($knownGodot)) {
            $GodotExecutable = $knownGodot
        }
    }
}

if ([string]::IsNullOrWhiteSpace($GodotExecutable) -or -not (Test-Path -LiteralPath $GodotExecutable)) {
    throw 'Godot 4 Mono was not found. Pass -GodotExecutable or set the GODOT4 environment variable.'
}

if ([string]::IsNullOrWhiteSpace($FfmpegExecutable)) {
    $knownFfmpeg = 'C:\Program Files\Wondershare\Recoverit - Data Recovery (CPC)\ffmpeg.exe'
    if (Test-Path -LiteralPath $knownFfmpeg) {
        $FfmpegExecutable = $knownFfmpeg
    }
}

if ([string]::IsNullOrWhiteSpace($FfmpegExecutable) -or -not (Test-Path -LiteralPath $FfmpegExecutable)) {
    throw 'FFmpeg was not found. Pass -FfmpegExecutable so the 16:9 engine master can be cropped and encoded to a 9:16 MP4.'
}

$topics = if ($Topic -eq 'all') { @('scale', 'clouds', 'voxels') } else { @($Topic) }
$frameCount = $FramesPerSecond * ($PreRollSeconds + $DurationSeconds)

foreach ($captureTopic in $topics) {
    $index = switch ($captureTopic) {
        'scale' { '01' }
        'clouds' { '02' }
        'voxels' { '03' }
    }
    $masterName = '{0}_halo_ring_{1}_certified_engine_master.avi' -f $index, $captureTopic
    $finalName = '{0}_halo_ring_{1}_certified_1080x1920_{2}fps.mp4' -f $index, $captureTopic, $FramesPerSecond
    $masterPath = Join-Path $captureDirectory $masterName
    $finalPath = Join-Path $captureDirectory $finalName
    Write-Host ('Loading {0} at maximum settings for {1}s, then recording -> {2}' -f $captureTopic, $PreRollSeconds, $masterPath)
    & $GodotExecutable `
        --path $projectRoot `
        --write-movie $masterPath `
        --fixed-fps $FramesPerSecond `
        --quit-after $frameCount `
        --disable-vsync `
        'res://scenes/VerticalShortCapture.tscn' `
        -- "--shorts-topic=$captureTopic" "--shorts-preroll=$PreRollSeconds" '--shorts-max-quality' '--certified-smoke-test'
    if ($LASTEXITCODE -ne 0) {
        throw "Godot capture failed for topic '$captureTopic' with exit code $LASTEXITCODE."
    }

    Write-Host ('Encoding true 9:16 MP4 -> {0}' -f $finalPath)
    & $FfmpegExecutable `
        -hide_banner `
        -loglevel warning `
        -y `
        -i $masterPath `
        -ss $PreRollSeconds `
        -t $DurationSeconds `
        -vf 'crop=540:960:(iw-540)/2:(ih-960)/2,scale=1080:1920:flags=lanczos' `
        -an `
        -c:v libx264 `
        -preset medium `
        -crf 18 `
        -pix_fmt yuv420p `
        -movflags +faststart `
        $finalPath
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg encoding failed for topic '$captureTopic' with exit code $LASTEXITCODE."
    }
}

Write-Host ('Capture set complete: {0}' -f $captureDirectory)
