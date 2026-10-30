# Validates tools/test_manifest.json and tools/test_known_failures.json against the tools/ folder.
# Works on Windows PowerShell 5.1 and PowerShell 7. Exit 0 = pass, 1 = validation errors, 2 = could not run.
#   pwsh -File tools/testing/validate_manifest.ps1 [-Quiet]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolsDir = Join-Path $root 'tools'
$manifestPath = Join-Path $toolsDir 'test_manifest.json'
$knownPath = Join-Path $toolsDir 'test_known_failures.json'

$errors = New-Object System.Collections.Generic.List[string]
$markerFix = New-Object System.Collections.Generic.List[string]
function Fail([string]$file, [string]$msg) { $script:errors.Add("${file}: $msg") }

function Read-Json([string]$path) {
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { Write-Host "validate_manifest: cannot read $path : $($_.Exception.Message)"; exit 2 }
}
function Has($obj, [string]$name) { return ($null -ne $obj) -and ($obj.PSObject.Properties.Name -contains $name) }
function Get-Val($obj, [string]$name) { if (Has $obj $name) { return $obj.$name } return $null }

# Glob -> regex: ** spans directories, * and ? stay inside one path segment, [..] classes pass through.
function ConvertTo-GlobRegex([string]$glob) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('^')
    $i = 0
    while ($i -lt $glob.Length) {
        $c = $glob[$i]
        if ($c -eq '*') {
            if (($i + 1) -lt $glob.Length -and $glob[$i + 1] -eq '*') {
                if (($i + 2) -lt $glob.Length -and $glob[$i + 2] -eq '/') { [void]$sb.Append('(?:.*/)?'); $i += 3 }
                else { [void]$sb.Append('.*'); $i += 2 }
            } else { [void]$sb.Append('[^/]*'); $i++ }
        }
        elseif ($c -eq '?') { [void]$sb.Append('[^/]'); $i++ }
        elseif ($c -eq '[') {
            $end = $glob.IndexOf(']', $i + 1)
            if ($end -lt 0) { throw "unclosed '[' in glob" }
            [void]$sb.Append($glob.Substring($i, $end - $i + 1)); $i = $end + 1
        }
        else { [void]$sb.Append([regex]::Escape([string]$c)); $i++ }
    }
    [void]$sb.Append('$')
    return $sb.ToString()
}
function Test-Globs($globs, [string]$where) {
    if ($null -eq $globs -or @($globs).Count -eq 0) { Fail 'tools/test_manifest.json' "$where has no globs"; return }
    foreach ($g in @($globs)) {
        if ([string]::IsNullOrWhiteSpace([string]$g)) { Fail 'tools/test_manifest.json' "$where has an empty glob"; continue }
        try { [void][regex]::new((ConvertTo-GlobRegex ([string]$g))) }
        catch { Fail 'tools/test_manifest.json' "$where glob '$g' does not parse: $($_.Exception.Message)" }
    }
}

$m = Read-Json $manifestPath
$kf = Read-Json $knownPath
$mf = 'tools/test_manifest.json'

# --- taxonomy, globs ---
$tagNames = @{}
if (-not (Has $m 'tags')) { Fail $mf "missing 'tags'" } else {
    foreach ($p in $m.tags.PSObject.Properties) { $tagNames[$p.Name] = $true; Test-Globs (Get-Val $p.Value 'globs') "tag '$($p.Name)'" }
}
Test-Globs (Get-Val $m 'core_globs') 'core_globs'
Test-Globs (Get-Val $m 'ignore_globs') 'ignore_globs'

# --- every SceneTree tools/*.gd has an entry ---
$entries = @{}
if (-not (Has $m 'tests')) { Fail $mf "missing 'tests'" } else { foreach ($p in $m.tests.PSObject.Properties) { $entries[$p.Name] = $p.Value } }
$rx = New-Object System.Text.RegularExpressions.Regex('^\s*extends\s+SceneTree\b', 'Multiline')
foreach ($f in Get-ChildItem -LiteralPath $toolsDir -Filter '*.gd' -File) {
    if ($rx.IsMatch([System.IO.File]::ReadAllText($f.FullName)) -and -not $entries.ContainsKey($f.BaseName)) {
        Fail "tools/$($f.Name)" "SceneTree script has no entry in $mf"
    }
}

# --- per-entry checks ---
$kinds = 'test', 'tool'; $modes = 'headless', 'gpu', 'gpu-local'
$tiers = 'smoke', 'gate', 'slow', 'extended', 'manual'; $gateTiers = 'smoke', 'gate', 'slow'
$profiles = 'shared', 'fresh', 'own-runner'
foreach ($name in @($entries.Keys | Sort-Object)) {
    $e = $entries[$name]; $where = "tools/$name.gd"
    $runner = Get-Val $e 'runner'
    if ($runner) {
        if (-not (Test-Path -LiteralPath (Join-Path $root ([string]$runner)))) { Fail $where "runner '$runner' does not exist" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDir "$name.gd")) -and -not $runner) { Fail $where 'entry has no tools/<name>.gd and no runner' }
    if ($kinds -notcontains (Get-Val $e 'kind')) { Fail $where "bad kind '$(Get-Val $e 'kind')'" }
    if ($modes -notcontains (Get-Val $e 'mode')) { Fail $where "bad mode '$(Get-Val $e 'mode')'" }
    $tier = Get-Val $e 'tier'
    if ($tiers -notcontains $tier) { Fail $where "bad tier '$tier'" }
    $prof = Get-Val $e 'profile'
    if ($prof -and $profiles -notcontains $prof) { Fail $where "bad profile '$prof'" }
    $tags = Get-Val $e 'tags'
    if ($null -eq $tags -or @($tags).Count -eq 0) { Fail $where 'no tags' }
    foreach ($t in @($tags)) { if (-not $tagNames.ContainsKey([string]$t)) { Fail $where "tag '$t' is not in the taxonomy" } }
    foreach ($num in 'expected_s', 'timeout_s') {
        $v = Get-Val $e $num
        if ($num -eq 'timeout_s' -and ($null -eq $v -or $v -le 0)) { Fail $where 'timeout_s missing or not positive' }
        if ($num -eq 'expected_s' -and $null -ne $v -and $v -lt 0) { Fail $where 'expected_s negative' }
    }
    $batch = Get-Val $e 'batch'
    if ($batch) {
        $w = Get-Val $e 'world'
        if (-not $w) { Fail $where "batch group '$batch' but no world" }
        elseif (([string]$batch).Split('@')[0] -ne $w) { Fail $where "batch group '$batch' does not match world '$w'" }
        if ((Get-Val $e 'mode') -ne 'gpu') { Fail $where "batch group '$batch' requires mode gpu" }
    }
    if ($gateTiers -contains $tier -and (Get-Val $e 'kind') -eq 'test') {
        $mk = Get-Val $e 'marker'
        $fix = Get-Val $e 'marker_fix'
        if ([string]::IsNullOrWhiteSpace([string]$mk) -and -not $fix) { Fail $where "tier '$tier' needs a marker (or a marker_fix entry)" }
        elseif ($fix) { $markerFix.Add($name) }
    }
}

# --- smoke set ---
if (-not (Has $m 'smoke')) { Fail $mf "missing 'smoke'" } else {
    foreach ($lane in 'headless', 'gpu') {
        foreach ($n in @(Get-Val $m.smoke $lane)) {
            if (-not $entries.ContainsKey($n)) { Fail $mf "smoke.$lane '$n' has no entry"; continue }
            $r = Get-Val $entries[$n] 'runner'
            if (-not $r -and -not (Test-Path -LiteralPath (Join-Path $toolsDir "$n.gd"))) { Fail "tools/$n.gd" "smoke.$lane file does not exist" }
        }
    }
}

# --- known failures ---
$statuses = 'known-fail', 'quarantined', 'flaky', 'unverified'
$kfile = 'tools/test_known_failures.json'
if (-not (Has $kf 'failures')) { Fail $kfile "missing 'failures'" } else {
    foreach ($p in $kf.failures.PSObject.Properties) {
        $v = $p.Value
        if (-not $entries.ContainsKey($p.Name)) { Fail $kfile "'$($p.Name)' has no manifest entry" }
        if ($statuses -notcontains (Get-Val $v 'status')) { Fail $kfile "'$($p.Name)' bad status '$(Get-Val $v 'status')'" }
        $mt = Get-Val $v 'match'
        if ($mt) { try { [void][regex]::new([string]$mt) } catch { Fail $kfile "'$($p.Name)' match regex does not parse" } }
        $mach = Get-Val $v 'machines'
        if ($null -ne $mach) { foreach ($mm in @($mach)) { if ('desktop_mid_high', 'laptop_a1000' -notcontains $mm) { Fail $kfile "'$($p.Name)' unknown machine '$mm'" } } }
        if (-not (Get-Val $v 'evidence')) { Fail $kfile "'$($p.Name)' has no evidence" }
    }
}

# --- report ---
if (-not $Quiet) {
    Write-Host ("validate_manifest: {0} entries, {1} tags, marker_fix pending: {2}" -f $entries.Count, $tagNames.Count, $markerFix.Count)
    if ($markerFix.Count -gt 0) { Write-Host ("  marker_fix (WO5): " + (($markerFix | Sort-Object) -join ', ')) }
}
if ($errors.Count -gt 0) {
    foreach ($err in $errors) { Write-Host "FAIL $err" }
    Write-Host "VALIDATE_MANIFEST=FAIL ($($errors.Count) errors)"
    exit 1
}
Write-Host 'VALIDATE_MANIFEST=PASS'
exit 0
