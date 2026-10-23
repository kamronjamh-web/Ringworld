<#
.SYNOPSIS
  ffmpeg helpers for reviewing captured test runs: contact sheets, frame comparison, flicker scans and MP4 export.

.DESCRIPTION
  -Path is a video file or a Godot Movie Maker PNG sequence. Record a sequence with
    godot --path . --write-movie .godot/captures/<label>/frame.png --fixed-fps 30 <scene or --script ...>
  which writes frame00000000.png, frame00000001.png, ... (pass the same "...\frame.png" path as -Path).
  An .avi path to --write-movie records MJPEG video instead.

  Modes:
    sheet    Evenly spaced frames tiled into one PNG (readable by agents that view images, not video).
    compare  SSIM and PSNR between two images or videos (-Path, -Reference), plus an amplified difference heatmap PNG.
    flicker  Mean frame-to-frame luma change per frame; prints the largest spikes (pops, flashes, black frames).
    mp4      Encodes the input as H.264 MP4 for a human to watch.

.EXAMPLE
  .\tools\media\ffmpeg_tools.ps1 sheet -Path .godot\captures\c6\frame.png -Columns 6 -Rows 4
  .\tools\media\ffmpeg_tools.ps1 compare -Path after.png -Reference before.png
  .\tools\media\ffmpeg_tools.ps1 flicker -Path .godot\captures\c6\frame.png -Top 10
  .\tools\media\ffmpeg_tools.ps1 mp4 -Path .godot\captures\c6\frame.png -Fps 30
#>
param(
  [Parameter(Mandatory, Position = 0)][ValidateSet("sheet", "compare", "flicker", "mp4")][string]$Mode,
  [Parameter(Mandatory)][string]$Path,
  [string]$Reference = "",
  [string]$Output = "",
  [int]$Columns = 6,
  [int]$Rows = 4,
  [int]$TileWidth = 480,
  [int]$Fps = 30,
  [int]$Top = 10,
  [double]$Amplify = 8.0
)
$ErrorActionPreference = "Stop"

# winget adds ffmpeg to PATH only for new shells; fall back to the package folder so already-open shells and agents still find it.
function Resolve-Tool([string]$name) {
  $command = Get-Command $name -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }
  $found = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg*" -Recurse -Filter "$name.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($found) { return $found.FullName }
  throw "$name not found. Install it with: winget install Gyan.FFmpeg"
}
$ffmpeg = Resolve-Tool "ffmpeg"
$ffprobe = Resolve-Tool "ffprobe"

# Returns the ffmpeg input arguments and the frame count. A missing "...\name.png" is read as a Godot sequence name00000000.png, ...
function Resolve-Source([string]$file) {
  if (Test-Path -LiteralPath $file -PathType Leaf) {
    if ($file -match '\.(png|jpg|jpeg|bmp|exr)$') { return @{ Args = @("-i", $file); Frames = 1 } }
    $count = & $ffprobe -v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 $file
    return @{ Args = @("-i", $file); Frames = [int]$count }
  }
  if ($file -match '\.png$') {
    $base = $file -replace '\.png$', ''
    $frames = @(Get-ChildItem -Path "$base*.png" -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\d{8}\.png$' })
    if ($frames.Count -gt 0) { return @{ Args = @("-framerate", "$Fps", "-start_number", "0", "-i", "$base%08d.png"); Frames = $frames.Count } }
  }
  throw "Input not found: $file (a video, an image, or a Godot --write-movie PNG sequence base path)"
}

function Default-Output([string]$suffix) {
  if ($Output) { return $Output }
  $base = ($Path -replace '\.[^.\\/]+$', '')
  return "$base$suffix"
}

$source = Resolve-Source $Path
switch ($Mode) {
  "sheet" {
    $out = Default-Output "_sheet.png"
    $tiles = [Math]::Max(1, $Columns * $Rows)
    $step = [Math]::Max(1, [Math]::Floor($source.Frames / $tiles))
    $filter = "select='not(mod(n\,$step))',scale=${TileWidth}:-2,drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{eif\:n*$step\:d}':x=8:y=8:fontsize=20:fontcolor=yellow:box=1:boxcolor=black@0.6,tile=${Columns}x${Rows}"
    & $ffmpeg -hide_banner -loglevel error -y @($source.Args) -vf $filter -frames:v 1 -update 1 $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed" }
    "SHEET frames=$($source.Frames) step=$step tiles=$tiles out=$out (tile labels are source frame numbers)"
  }
  "compare" {
    if (-not $Reference) { throw "compare needs -Reference" }
    $referenceSource = Resolve-Source $Reference
    $log = & $ffmpeg -hide_banner @($source.Args) @($referenceSource.Args) -lavfi "[0:v][1:v]ssim;[0:v][1:v]psnr" -f null - 2>&1 | Out-String
    $ssim = [regex]::Match($log, 'SSIM .*All:([\d.]+)').Groups[1].Value
    $psnr = [regex]::Match($log, 'PSNR .*average:([\w.]+)').Groups[1].Value
    $out = Default-Output "_diff.png"
    & $ffmpeg -hide_banner -loglevel error -y @($source.Args) @($referenceSource.Args) -lavfi "[0:v][1:v]blend=all_mode=difference,format=gray,lutyuv=y='clip(val*$Amplify,0,255)'" -frames:v 1 -update 1 $out
    "COMPARE ssim=$ssim psnr_db=$psnr heatmap=$out (difference x$Amplify; 1.0 / inf = identical)"
  }
  "flicker" {
    $stats = [System.IO.Path]::GetTempFileName()
    $statsPath = $stats -replace '\\', '/' -replace ':', '\:'
    & $ffmpeg -hide_banner -loglevel error @($source.Args) -vf "tblend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file='$statsPath'" -f null -
    $values = @()
    $frame = -1
    foreach ($line in Get-Content $stats) {
      if ($line -match '^frame:(\d+)') { $frame = [int]$Matches[1] + 1 }
      elseif ($line -match 'YAVG=([\d.]+)') { $values += [pscustomobject]@{ Frame = $frame; Change = [double]$Matches[1] } }
    }
    Remove-Item $stats -ErrorAction SilentlyContinue
    if ($values.Count -eq 0) { throw "no frame pairs to compare" }
    $mean = ($values | Measure-Object Change -Average).Average
    "FLICKER pairs=$($values.Count) mean_change=$([Math]::Round($mean, 3)) (0-255 luma; frame N = change from N-1)"
    $values | Sort-Object Change -Descending | Select-Object -First $Top | ForEach-Object { "  frame {0,6}  change {1,7:N3}  x{2:N1} mean" -f $_.Frame, $_.Change, ($_.Change / [Math]::Max(0.001, $mean)) }
  }
  "mp4" {
    $out = Default-Output ".mp4"
    & $ffmpeg -hide_banner -loglevel error -y @($source.Args) -c:v libx264 -pix_fmt yuv420p -crf 20 -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed" }
    "MP4 frames=$($source.Frames) out=$out"
  }
}
