<#
Compare two PNGs (or two folders of same-named PNGs) the same way the repo's GDScript goldens do
(per pixel: max abs diff over RGBA channels), and write a diff heatmap plus an A|B|diff side-by-side
you can open with the Read tool.

Usage:
  .\.claude\skills\visual-golden-check\imgdiff.ps1 -A golden.png -B actual.png [-Out dir] [-MaxDiff 0] [-Gain 8]
  .\.claude\skills\visual-golden-check\imgdiff.ps1 -A goldenDir -B actualDir [-Out dir]
Exit code: 0 all within MaxDiff, 1 any over, 2 bad input.
#>
param(
    [Parameter(Mandatory = $true)][string]$A,
    [Parameter(Mandatory = $true)][string]$B,
    [string]$Out = "",
    [int]$MaxDiff = 0,
    [int]$Gain = 8
)
Add-Type -AssemblyName System.Drawing

function Read-Pixels([string]$path) {
    $bmp = [System.Drawing.Bitmap]::new($path)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $r = @{ W = $bmp.Width; H = $bmp.Height; Stride = $data.Stride; Bytes = $bytes; Bmp = $bmp }
    return $r
}

function Compare-Pair([string]$pa, [string]$pb, [string]$outDir) {
    $ia = Read-Pixels $pa; $ib = Read-Pixels $pb
    $name = [IO.Path]::GetFileNameWithoutExtension($pb)
    if ($ia.W -ne $ib.W -or $ia.H -ne $ib.H) {
        "$name size_mismatch $($ia.W)x$($ia.H) vs $($ib.W)x$($ib.H)"
        $ia.Bmp.Dispose(); $ib.Bmp.Dispose(); return 255
    }
    $w = $ia.W; $h = $ia.H; $s = $ia.Stride
    $heat = New-Object byte[] ($s * $h)
    $hist = New-Object int[] 256
    $maxD = 0; $count = 0
    $ba = $ia.Bytes; $bb = $ib.Bytes
    for ($y = 0; $y -lt $h; $y++) {
        $row = $y * $s
        for ($x = 0; $x -lt $w; $x++) {
            $i = $row + $x * 4
            $d = 0
            for ($c = 0; $c -lt 4; $c++) { $v = [Math]::Abs([int]$ba[$i + $c] - [int]$bb[$i + $c]); if ($v -gt $d) { $d = $v } }
            $hist[$d]++
            if ($d -gt 0) { $count++ }
            if ($d -gt $maxD) { $maxD = $d }
            $g = [Math]::Min(255, $d * $Gain)
            # BGRA: diff in red over dimmed grey of B
            $lum = [byte](([int]$bb[$i] + [int]$bb[$i + 1] + [int]$bb[$i + 2]) / 12)
            $heat[$i] = $lum; $heat[$i + 1] = $lum; $heat[$i + 2] = [byte][Math]::Max($lum, $g); $heat[$i + 3] = 255
        }
    }
    $total = $w * $h
    $p99 = 0; $acc = 0
    for ($k = 0; $k -lt 256; $k++) { $acc += $hist[$k]; if ($acc -ge 0.99 * $total) { $p99 = $k; break } }
    "{0} differing_pixels={1} ({2:P3}) max_channel_diff={3} p99={4}" -f $name, $count, ($count / $total), $maxD, $p99

    if ($outDir -and $count -gt 0) {
        $heatBmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
        $hd = $heatBmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        [System.Runtime.InteropServices.Marshal]::Copy($heat, 0, $hd.Scan0, $heat.Length)
        $heatBmp.UnlockBits($hd)
        $side = [System.Drawing.Bitmap]::new($w * 3, $h)
        $gfx = [System.Drawing.Graphics]::FromImage($side)
        $gfx.DrawImage($ia.Bmp, 0, 0, $w, $h); $gfx.DrawImage($ib.Bmp, $w, 0, $w, $h); $gfx.DrawImage($heatBmp, $w * 2, 0, $w, $h)
        $gfx.Dispose()
        $heatBmp.Save((Join-Path $outDir "${name}_diff.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $side.Save((Join-Path $outDir "${name}_side_by_side.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $heatBmp.Dispose(); $side.Dispose()
    }
    $ia.Bmp.Dispose(); $ib.Bmp.Dispose()
    return $maxD
}

if (-not (Test-Path $A) -or -not (Test-Path $B)) { Write-Error "missing input"; exit 2 }
if ($Out) { New-Item -ItemType Directory -Force $Out | Out-Null }

$pairs = @()
if ((Get-Item $A).PSIsContainer) {
    foreach ($f in Get-ChildItem $A -Filter *.png) {
        $other = Join-Path $B $f.Name
        if (Test-Path $other) { $pairs += , @($f.FullName, $other) } else { "$($f.BaseName) missing_in_B" }
    }
} else { $pairs += , @((Resolve-Path $A).Path, (Resolve-Path $B).Path) }

$worst = 0
foreach ($p in $pairs) {
    $res = Compare-Pair $p[0] $p[1] $Out
    $lines = @($res); $m = $lines[-1]; $lines[0..($lines.Count - 2)] | ForEach-Object { $_ }
    if ($m -gt $worst) { $worst = $m }
}
$verdict = if ($worst -le $MaxDiff) { "PASS" } else { "FAIL" }
"IMGDIFF=$verdict pairs=$($pairs.Count) worst_max_channel_diff=$worst max_allowed=$MaxDiff$(if ($Out) { " out=$Out" })"
if ($verdict -eq "PASS") { exit 0 } else { exit 1 }
