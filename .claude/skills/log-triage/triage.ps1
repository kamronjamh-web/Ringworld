<#
Summarise a Godot run log without dumping it into context.
Usage: .\.claude\skills\log-triage\triage.ps1 <log> [-Top 8] [-Context 0] [-IncludeNoise]
Prints: device line, verdict markers (=PASS/=FAIL/=SKIP), error groups by kind with counts
and first occurrence, and the last lines of the log.
#>
param(
    [Parameter(Mandatory = $true)][string]$Log,
    [int]$Top = 8,
    [int]$Tail = 15,
    [switch]$IncludeNoise
)

if (-not (Test-Path $Log)) { Write-Error "log not found: $Log"; exit 2 }
$lines = Get-Content $Log
$err = "$Log.err"
if (Test-Path $err) { $lines += Get-Content $err }

# Known pre-existing noise; see godot-test-run skill.
$noise = 'ring_voxel_mesher'

$kinds = [ordered]@{
    'SHADER'  = 'SHADER ERROR|Shader compilation failed|shader.*error|Failed parse shader'
    'VULKAN'  = 'Vulkan|VK_ERROR|vkCreate|device lost|RenderingDevice.*(error|fail)'
    'SCRIPT'  = 'SCRIPT ERROR|Unhandled exception|System\.\w+Exception|NullReferenceException|   at |^\s+\[\d+\] \S+.*\(.*:\d+\)$'
    'ERROR'   = '^\s*ERROR:'
    'WARNING' = '^\s*WARNING:'
    'LEAK'    = 'leaked at exit|ObjectDB instances leaked|RID allocations .* leaked|Orphan StringName'
    'CRASH'   = 'CrashHandler|Program crashed|signal 11|Segmentation fault|Access violation'
}

"== $Log  ($($lines.Count) lines)"
$dev = $lines | Where-Object { $_ -match 'Using Device' } | Select-Object -First 1
if ($dev) { "device: $($dev.Trim())" } else { "device: (no 'Using Device' line - headless?)" }

"== verdicts"
$verdicts = $lines | Where-Object { $_ -match '[A-Z0-9_]+=(PASS|FAIL|SKIP)\b' }
if ($verdicts) { $verdicts | Select-Object -Last 20 | ForEach-Object { "  $($_.Trim())" } } else { "  (none)" }
$exitLine = $lines | Where-Object { $_ -match 'exit code|exit=' } | Select-Object -Last 1
if ($exitLine) { "  $($exitLine.Trim())" }

"== problems by kind"
foreach ($k in $kinds.Keys) {
    $hits = $lines | Where-Object { $_ -match $kinds[$k] }
    if (-not $IncludeNoise) { $hits = $hits | Where-Object { $_ -notmatch $noise } }
    if (-not $hits) { continue }
    $hits = @($hits)
    "-- $k : $($hits.Count)"
    # group by message with digits/hex normalised so repeats collapse
    $hits | Group-Object { ($_ -replace '0x[0-9a-fA-F]+', '#' -replace '\d+(\.\d+)?', 'N').Trim() } |
        Sort-Object Count -Descending | Select-Object -First $Top |
        ForEach-Object { "  x$($_.Count)  $($_.Group[0].Trim().Substring(0, [Math]::Min(220, $_.Group[0].Trim().Length)))" }
}

if (($lines -match "Unknown character #35: '#'") -and ($lines -match 'rendering/dummy/')) {
    "NOTE: '#include' tokenizer errors from the dummy (headless) renderer are usually spurious; confirm windowed."
}

"== last $Tail lines"
$lines | Select-Object -Last $Tail | ForEach-Object { "  $_" }
