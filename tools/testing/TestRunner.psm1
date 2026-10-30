# Test runner core (WO2 of docs/test-suite-tooling-design.md). Use tools/run_tests.ps1; this module holds the logic.
# Works on Windows PowerShell 5.1 and PowerShell 7. No Python. Windows only (taskkill, Win32 process tree).
#
# Elapsed/duration use unbiased interrupt time (no sleep); the system is kept awake during a run. .ps1 runner entries always get
# the REAL APPDATA in their environment (they isolate themselves); -AppData carries the runner's profile where declared.
# Sections: process helper (C#) | manifest + globs | selection (4.5) | profiles (8.2) | launch + verdict (5.4/5.5)
#           | reporting (6) | Invoke-TestRun.

$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

if (-not ('RingTestProcV2' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

// Launches one child process, merges stdout and stderr into one timestamped log (flushed per line, so a killed runner
// still leaves logs behind) and keeps the raw lines in memory for verdicts. Real exit codes via System.Diagnostics.Process.
public class RingTestProcV2
{
    Process p;
    StreamWriter w;
    readonly object lk = new object();
    readonly List<string> lines = new List<string>();
    bool completed;
    public string StartError = "";
    public int Pid = 0;
    // Unbiased interrupt time (100 ns units) excludes sleep/hibernate, so timeouts and durations count active time only.
    [DllImport("kernel32.dll")]
    static extern bool QueryUnbiasedInterruptTime(out ulong t);
    ulong startU;
    ulong exitU;

    static ulong NowU()
    {
        ulong t;
        QueryUnbiasedInterruptTime(out t);
        return t;
    }

    public bool Start(string exe, string args, string workDir, string logPath, string[] envPairs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (envPairs != null)
            {
                foreach (string kv in envPairs)
                {
                    int i = kv.IndexOf('=');
                    if (i > 0) psi.EnvironmentVariables[kv.Substring(0, i)] = kv.Substring(i + 1);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            w = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            w.AutoFlush = true;
            p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { Add(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { Add(e.Data); };
            w.WriteLine("# " + exe + " " + args);
            p.Start();
            startU = NowU();
            Pid = p.Id;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
            return false;
        }
    }

    void Add(string line)
    {
        if (line == null) return;
        lock (lk)
        {
            lines.Add(line);
            if (w != null)
            {
                double t = (DateTime.Now - p.StartTime).TotalSeconds;
                w.WriteLine("[" + t.ToString("F3").PadLeft(8) + "] " + line);
            }
        }
    }

    public void Note(string text)
    {
        lock (lk) { if (w != null) w.WriteLine("# " + text); }
    }

    public bool HasExited
    {
        get
        {
            if (p == null) return true;
            if (!p.HasExited) return false;
            MarkExit();
            return true;
        }
    }
    public int ExitCode { get { return p.ExitCode; } }

    void MarkExit()
    {
        lock (lk) { if (exitU == 0) exitU = NowU(); }
    }

    // Active (sleep-free) seconds since start; frozen once the exit has been observed.
    public double Elapsed
    {
        get
        {
            ulong end = exitU != 0 ? exitU : NowU();
            return (end - startU) / 10000000.0;
        }
    }
    public double Duration { get { return Elapsed; } }

    // Wall-clock seconds minus active seconds: time the machine spent asleep during the run.
    public double SleptSeconds
    {
        get
        {
            double wall = p.HasExited ? (p.ExitTime - p.StartTime).TotalSeconds : (DateTime.Now - p.StartTime).TotalSeconds;
            double s = wall - Elapsed;
            return s > 0 ? s : 0;
        }
    }

    public string[] Lines()
    {
        lock (lk) { return lines.ToArray(); }
    }

    // Kills only this process and its descendants (taskkill /T /F on our own PID, never by image name).
    public void KillTree()
    {
        try
        {
            if (!p.HasExited)
            {
                ProcessStartInfo k = new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id);
                k.CreateNoWindow = true;
                k.UseShellExecute = false;
                k.RedirectStandardOutput = true;
                k.RedirectStandardError = true;
                Process kp = Process.Start(k);
                kp.WaitForExit(10000);
            }
        }
        catch (Exception) { }
        try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
    }

    // Wait for the async readers to drain (bounded: a grandchild holding the pipe must not hang the runner), then close the log.
    public void Complete(string footer)
    {
        if (completed) return;
        completed = true;
        try { if (p.HasExited) MarkExit(); } catch (Exception) { }
        try { Task t = Task.Factory.StartNew(delegate() { p.WaitForExit(); }); t.Wait(5000); } catch (Exception) { }
        lock (lk)
        {
            if (w != null) { w.WriteLine("# " + footer); w.Dispose(); w = null; }
        }
    }
}
'@
}

# Keeps Windows from going to sleep while a run is in progress (a sleep used to count towards test timeouts).
if (-not ('RingTestPower' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class RingTestPower
{
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    public static void KeepAwake(bool on)
    {
        // ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1
        SetThreadExecutionState(on ? 0x80000001u : 0x80000000u);
    }
}
'@
}

# ---------------------------------------------------------------- small utilities

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}
# Atomic: write a temp file next to the target, then replace.
function Write-AtomicText([string]$Path, [string]$Text) {
    $tmp = "$Path.tmp"
    Write-Utf8NoBom $tmp $Text
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}
function Has($obj, [string]$name) { return ($null -ne $obj) -and ($obj.PSObject.Properties.Name -contains $name) }
function Get-Val($obj, [string]$name) { if (Has $obj $name) { return $obj.$name } return $null }
function Format-Duration([double]$s) {
    if ($s -ge 3600) { return ('{0}h{1:D2}m' -f [int][math]::Floor($s / 3600), [int][math]::Floor(($s % 3600) / 60)) }
    if ($s -ge 60) { return ('{0}m{1:D2}s' -f [int][math]::Floor($s / 60), [int][math]::Floor($s % 60)) }
    return ('{0:N1}s' -f $s)
}
function Split-ArgString([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return @() }
    return @($s.Trim() -split '\s+')
}
function Join-ProcessArgs([string[]]$a) {
    $out = foreach ($x in $a) {
        if ($x -eq '' -or $x -match '[\s"]') { '"' + ($x -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' } else { $x }
    }
    return ($out -join ' ')
}
function Invoke-Git([string[]]$GitArgs) {
    $ErrorActionPreference = 'Continue'   # 5.1 turns native stderr (CRLF warnings) into terminating errors otherwise
    $o = & git -C $script:Root -c core.quotepath=false @GitArgs 2>$null
    $script:GitOk = ($LASTEXITCODE -eq 0)
    return @($o | Where-Object { $_ })
}

# ---------------------------------------------------------------- manifest + globs

# Same semantics as tools/testing/validate_manifest.ps1: ** spans directories, * and ? stay inside one segment.
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
function New-GlobSet($globs) {
    $set = New-Object System.Collections.Generic.List[regex]
    foreach ($g in @($globs)) { if ($g) { $set.Add([regex]::new((ConvertTo-GlobRegex ([string]$g)))) } }
    return , $set
}
function Test-GlobSet($set, [string]$path) {
    foreach ($r in $set) { if ($r.IsMatch($path)) { return $true } }
    return $false
}

function Read-JsonFile([string]$path) {
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# Loads the manifest and known failures into one context object. $Ctx.Known maps test name -> entry.
function Import-TestManifest([string]$ManifestPath, [string]$KnownPath) {
    $m = Read-JsonFile $ManifestPath
    $known = @{}
    if (Test-Path -LiteralPath $KnownPath) {
        $kf = Read-JsonFile $KnownPath
        if (Has $kf 'failures') { foreach ($p in $kf.failures.PSObject.Properties) { $known[$p.Name] = $p.Value } }
    }
    $tagGlobs = [ordered]@{}
    foreach ($p in $m.tags.PSObject.Properties) { $tagGlobs[$p.Name] = New-GlobSet $p.Value.globs }
    $tests = [ordered]@{}
    foreach ($p in ($m.tests.PSObject.Properties | Sort-Object Name)) { $tests[$p.Name] = $p.Value }
    return [pscustomobject]@{
        Manifest = $m; Tests = $tests; Known = $known; TagGlobs = $tagGlobs
        CoreSet = (New-GlobSet $m.core_globs); IgnoreSet = (New-GlobSet $m.ignore_globs)
    }
}

# A runnable spec from one manifest entry. Defaults: unknown timeout 300 gpu / 120 headless (manifest note).
function New-TestSpec([string]$Name, $E, $Known) {
    $mode = [string](Get-Val $E 'mode')
    $timeout = Get-Val $E 'timeout_s'
    if (-not $timeout) { $timeout = if ($mode -eq 'headless') { 120 } else { 300 } }
    $expected = Get-Val $E 'expected_s'
    if ($null -eq $expected) { $expected = if ($mode -eq 'headless') { 10 } else { 60 } }
    $profile = [string](Get-Val $E 'profile')
    if (-not $profile) { $profile = 'shared' }
    $ps = Get-Val $E 'parallel_safe'
    $parallelSafe = ($ps -ne $false) -and ($profile -ne 'fresh')   # a recreated fresh profile cannot be shared with parallel tests
    $kstatus = [string](Get-Val $Known 'status')
    return [pscustomobject]@{
        Name = $Name; Kind = [string](Get-Val $E 'kind'); Mode = $mode; Tier = [string](Get-Val $E 'tier')
        Tags = @(Get-Val $E 'tags'); Marker = [string](Get-Val $E 'marker'); MarkerFix = [string](Get-Val $E 'marker_fix')
        Args = (Split-ArgString ([string](Get-Val $E 'args'))); UserArgs = (Split-ArgString ([string](Get-Val $E 'user_args')))
        Runner = [string](Get-Val $E 'runner'); TimeoutS = [double]$timeout; ExpectedS = [double]$expected
        Profile = $profile; ParallelSafe = $parallelSafe; Batch = [string](Get-Val $E 'batch'); Synthetic = $false
        KnownStatus = $kstatus; KnownMatch = [string](Get-Val $Known 'match'); KnownRetries = [int](Get-Val $Known 'retries')
        KnownTimeout = (Get-Val $Known 'timeout_s')
    }
}

# ---------------------------------------------------------------- selection (design 4.5)

function Get-ChangedFiles([string]$Base, [string]$Since, [string]$Label) {
    $files = New-Object System.Collections.Generic.HashSet[string]
    $rev = $null
    if ($Since) {
        if ($Since -in @('green', 'last', 'last-green')) {
            $lg = Join-Path $script:Root ".godot\test_runs\$Label\last_green.json"
            if (-not (Test-Path -LiteralPath $lg)) { throw "-Since $Since : no $lg yet (no green run of label '$Label')" }
            $rev = [string](Read-JsonFile $lg).head
        } else { $rev = $Since }
        $d = Invoke-Git @('diff', '--name-only', $rev)
        if (-not $script:GitOk) { throw "git diff --name-only $rev failed" }
        foreach ($f in $d) { [void]$files.Add($f) }
    } else {
        $d = Invoke-Git @('diff', '--name-only', "$Base...HEAD")
        if (-not $script:GitOk) { Write-Warning "git diff $Base...HEAD failed (no '$Base' ref?); using the working tree only"; $d = @() }
        foreach ($f in $d) { [void]$files.Add($f) }
        $u = Invoke-Git @('diff', '--name-only')
        foreach ($f in $u) { [void]$files.Add($f) }
    }
    $o = Invoke-Git @('ls-files', '--others', '--exclude-standard')
    foreach ($f in $o) { [void]$files.Add($f) }
    return @($files | Sort-Object)
}

# Meta files of the test tooling itself: change = run smoke (which includes the manifest validator), nothing more.
$script:MetaSet = New-GlobSet @('tools/test_manifest.json', 'tools/test_known_failures.json', 'tools/testing/**', 'tools/run_tests.ps1')

function Get-WrapperDependants([string]$helperFile) {
    $wrappers = New-Object System.Collections.Generic.List[string]
    foreach ($f in Get-ChildItem -LiteralPath (Join-Path $script:Root 'tools') -Filter '*.gd' -File) {
        if ((Get-Content -LiteralPath $f.FullName -Raw) -match [regex]::Escape($helperFile)) { $wrappers.Add($f.BaseName) }
    }
    return $wrappers
}

# Returns Items (name -> list of reasons), Skipped (name -> why not), plus the tag/core/unmapped summary.
function Get-Selection {
    param($M, [string[]]$Files, [string[]]$Tags, [string[]]$Tests, [string]$Suite, [bool]$UseFiles)

    $items = [ordered]@{}; $skipped = [ordered]@{}
    $tagHit = [ordered]@{}   # tag -> first file (or '-Tags')
    $direct = @{}            # test name -> reason
    $core = $null; $unmapped = New-Object System.Collections.Generic.List[string]; $ignored = 0; $meta = $false

    if ($UseFiles) {
        foreach ($f in $Files) {
            $path = $f -replace '\\', '/'
            if (Test-GlobSet $M.IgnoreSet $path) { $ignored++; continue }
            $mapped = $false
            if (Test-GlobSet $M.CoreSet $path) { if (-not $core) { $core = $path }; $mapped = $true }
            foreach ($t in $M.TagGlobs.Keys) {
                if (Test-GlobSet $M.TagGlobs[$t] $path) { if (-not $tagHit.Contains($t)) { $tagHit[$t] = $path }; $mapped = $true }
            }
            if ($path -match '^tools/([^/]+)\.gd$' -and $M.Tests.Contains($Matches[1])) {
                if (-not $direct.ContainsKey($Matches[1])) { $direct[$Matches[1]] = "direct <- $path" }; $mapped = $true
            }
            elseif ($path -match '^tools/([^/]+)\.cs$') {
                foreach ($w in (Get-WrapperDependants "$($Matches[1]).cs")) {
                    if ($M.Tests.Contains($w) -and -not $direct.ContainsKey($w)) { $direct[$w] = "helper $path <- wrapper $w.gd" }
                    $mapped = $true
                }
            }
            elseif ($path -match '^tools/[^/]+\.ps1$') {
                foreach ($n in $M.Tests.Keys) {
                    if ((Get-Val $M.Tests[$n] 'runner') -eq $path -and -not $direct.ContainsKey($n)) { $direct[$n] = "direct <- runner $path"; $mapped = $true }
                }
            }
            if (Test-GlobSet $script:MetaSet $path) { $meta = $true; $mapped = $true }
            if (-not $mapped) { $unmapped.Add($path) }
        }
    }
    foreach ($t in $Tags) {
        if (-not $M.TagGlobs.Contains($t)) { throw "unknown tag '$t' (see tags in tools/test_manifest.json)" }
        if (-not $tagHit.Contains($t)) { $tagHit[$t] = '-Tags' }
    }
    foreach ($n in $Tests) {
        $name = $n -replace '\.gd$', '' -replace '^.*[\\/]', ''
        if (-not $M.Tests.Contains($name)) { throw "unknown test '$n' (not in tools/test_manifest.json)" }
        $direct[$name] = '-Tests'
    }

    $fullHeadless = [bool]($core -or $unmapped.Count -gt 0)
    $smokeWanted = $UseFiles -or [bool]$Suite
    $suiteGate = $Suite -in @('gate', 'full'); $suiteFull = $Suite -eq 'full'

    foreach ($name in $M.Tests.Keys) {
        $e = $M.Tests[$name]; $kind = [string](Get-Val $e 'kind'); $tier = [string](Get-Val $e 'tier'); $mode = [string](Get-Val $e 'mode')
        $tags = @(Get-Val $e 'tags'); $batch = [string](Get-Val $e 'batch')
        $kst = [string](Get-Val $M.Known[$name] 'status')
        $reasons = New-Object System.Collections.Generic.List[string]
        if ($direct.ContainsKey($name)) { $reasons.Add($direct[$name]) }
        $isDirect = $reasons.Count -gt 0
        if ($kind -eq 'test' -and -not $isDirect) {
            $hits = @($tags | Where-Object { $tagHit.Contains([string]$_) })
            $firstHit = ($tags.Count -gt 0) -and $tagHit.Contains([string]$tags[0])
            $knownBlock = $kst -in @('known-fail', 'quarantined')
            $take = New-Object System.Collections.Generic.List[string]
            switch ($tier) {
                'smoke' { if ($smokeWanted -or $hits.Count) { $take.Add('smoke') } }
                'gate' {
                    if ($Suite -eq 'smoke') { }
                    elseif ($suiteGate) { $take.Add("suite $Suite") }
                    foreach ($h in $hits) { $take.Add("tag $h <- $($tagHit[[string]$h])") }
                    if ($core -and $mode -eq 'headless') { $take.Add("core <- $core (full headless)") }
                    if ($core -and $mode -eq 'gpu' -and $batch -match '^(integration|certified)@') { $take.Add("core <- $core (integration/certified gate)") }
                    if ($unmapped.Count -gt 0 -and $mode -eq 'headless') { $take.Add("unmapped <- $($unmapped[0]) (full headless)") }
                }
                'slow' {
                    if ($suiteFull) { $take.Add('suite full') }
                    if ($firstHit) { $take.Add("tag $($tags[0]) <- $($tagHit[[string]$tags[0]]) (first tag)") }
                    if ($fullHeadless -and $mode -eq 'headless') { $take.Add('full headless (core/unmapped)') }
                }
                'extended' { if ($Suite -eq 'extended') { $take.Add('suite extended') } }
                default { }
            }
            if ($take.Count -gt 0 -and $knownBlock -and $tier -ne 'smoke') {
                # known-fail and quarantined tests only with -Suite full, or when their first tag is in the set (a fix gets noticed)
                if ($suiteFull) { } elseif ($firstHit) { $take = New-Object System.Collections.Generic.List[string]; $take.Add("tag $($tags[0]) <- $($tagHit[[string]$tags[0]]) (first tag, $kst)") }
                else { $skipped[$name] = "$kst (not full, not first tag): $($take[0])"; $take.Clear() }
            }
            foreach ($t in $take) { $reasons.Add($t) }
        }
        if ($reasons.Count -gt 0) { $items[$name] = $reasons }
    }
    return [pscustomobject]@{
        Items = $items; Skipped = $skipped; Tags = @($tagHit.Keys); TagFiles = $tagHit; Core = $core; Unmapped = @($unmapped)
        Ignored = $ignored; Meta = $meta; Files = $Files
    }
}

function Get-Estimate($Specs, [int]$Parallel, [int]$LocalLanes) {
    $hl = @($Specs | Where-Object { $_.Mode -eq 'headless' })
    $gl = @($Specs | Where-Object { $_.Mode -eq 'gpu-local' })
    $gp = @($Specs | Where-Object { $_.Mode -eq 'gpu' })
    $safe = @($hl | Where-Object { $_.ParallelSafe }); $unsafe = @($hl | Where-Object { -not $_.ParallelSafe })
    $safeSum = ($safe | Measure-Object ExpectedS -Sum).Sum; $safeMax = ($safe | Measure-Object ExpectedS -Maximum).Maximum
    $unsafeSum = ($unsafe | Measure-Object ExpectedS -Sum).Sum
    $glSum = ($gl | Measure-Object ExpectedS -Sum).Sum; $glMax = ($gl | Measure-Object ExpectedS -Maximum).Maximum
    $gpSum = ($gp | Measure-Object ExpectedS -Sum).Sum
    $h = [math]::Max([double]$safeMax, [double]$safeSum / [math]::Max(1, $Parallel)) + [double]$unsafeSum
    $l = [math]::Max([double]$glMax, [double]$glSum / [math]::Max(1, $LocalLanes))
    return [pscustomobject]@{ Headless = $h; GpuLocal = $l; Gpu = [double]$gpSum; Total = $h + $l + [double]$gpSum }
}

# ---------------------------------------------------------------- profiles (design 8.2)

function Get-RealAppDataRoot {
    $appdata = [Environment]::GetEnvironmentVariable('APPDATA')
    # A shell that was isolated by another runner would point APPDATA into .godot; use the real roaming folder.
    if (-not $appdata -or $appdata -like '*\.godot\*') { $appdata = Join-Path $env:USERPROFILE 'AppData\Roaming' }
    return $appdata
}
function Get-RealProfileDir {
    return (Join-Path (Get-RealAppDataRoot) 'Godot\app_userdata\RingworldTechDemo')
}

# Creates (or reuses) .godot\test_runs\appdata_<label><suffix>. Returns the APPDATA root to hand to the process.
function Initialize-TestProfile {
    param([string]$Label, [string]$Suffix, [string]$Cache, [bool]$Recreate)
    $isoRoot = Join-Path $script:Root ".godot\test_runs\appdata_$Label$Suffix"
    $profile = Join-Path $isoRoot 'Godot\app_userdata\RingworldTechDemo'
    $real = Get-RealProfileDir
    $exists = Test-Path -LiteralPath $profile
    if ($Recreate -or $Cache -in @('copy', 'none') -or -not $exists) {
        # reuse + missing profile behaves like the first run: copy the warm caches.
        $withCaches = ($Cache -in @('copy', 'reuse')) -and -not $Recreate
        if (Test-Path -LiteralPath $isoRoot) { Remove-Item -LiteralPath $isoRoot -Recurse -Force }
        New-Item -ItemType Directory -Force (Join-Path $profile 'worlds') | Out-Null
        $slot = Join-Path $real 'worlds\slot_1'
        # A recreated (fresh) profile has no saved world: world_loading_smoke_test rejects a copied existing runtime-surface slot ("immutable backend and generation settings").
        if (-not $Recreate -and (Test-Path -LiteralPath $slot)) { Copy-Item -Recurse -LiteralPath $slot -Destination (Join-Path $profile 'worlds\slot_1') }
        Get-ChildItem -LiteralPath $real -Filter '*.tres' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $profile
        if ($withCaches) {
            foreach ($dir in 'shader_cache', 'vulkan', 'ring_cache', 'generated') {
                $src = Join-Path $real $dir
                if (Test-Path -LiteralPath $src) { Copy-Item -Recurse -LiteralPath $src -Destination (Join-Path $profile $dir) }
            }
        }
    }
    # Keeps the profile "recent" for prune_test_output.ps1 (24 h) even if a phase runner prunes without excluding it.
    Set-Content -LiteralPath (Join-Path $isoRoot '.keep_alive') -Value (Get-Date -Format o)
    return $isoRoot
}

function Invoke-Prune([string[]]$Exclude) {
    $prune = Join-Path $script:Root 'tools\prune_test_output.ps1'
    try { & $prune -Apply -Quiet -Exclude $Exclude | Out-Null } catch { Write-Warning "prune_test_output.ps1 failed: $($_.Exception.Message)" }
}

# Run directory retention: older than 7 days, and all but the 10 newest per label.
function Remove-OldRuns([string]$LabelDir, [string]$KeepRunId) {
    if (-not (Test-Path -LiteralPath $LabelDir)) { return }
    $runs = @(Get-ChildItem -LiteralPath $LabelDir -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' } | Sort-Object Name -Descending)
    for ($i = 0; $i -lt $runs.Count; $i++) {
        $r = $runs[$i]
        if ($r.Name -eq $KeepRunId) { continue }
        if ($i -ge 10 -or ((Get-Date) - $r.LastWriteTime).TotalDays -gt 7) { Remove-Item -LiteralPath $r.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- GPU phase (tools/gpu_queue.ps1)

# One acquire per GPU phase; a background job refreshes the ticket every 5 min while held.
function Get-GpuQueueDir { Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.godot\gpu_queue' }
function Get-GpuTicketLabel($Ctx) { 'run_tests_' + $Ctx.Label }
function Get-GpuWaiterCount($Ctx) {
    $d = Get-GpuQueueDir
    if (-not (Test-Path -LiteralPath $d)) { return 0 }
    $mine = '*_' + (Get-GpuTicketLabel $Ctx) + '.ticket'
    return @(Get-ChildItem -LiteralPath $d -Filter '*.ticket' | Where-Object { $_.Name -notlike $mine -and ((Get-Date) - $_.LastWriteTime).TotalMinutes -lt 15 }).Count
}
function Lock-GpuQueue($Ctx) {
    $q = Join-Path (Split-Path -Parent $PSScriptRoot) 'gpu_queue.ps1'
    $lbl = Get-GpuTicketLabel $Ctx
    Write-Host ("{0:HH:mm:ss} GPU queue: waiting for {1}" -f (Get-Date), $lbl)
    $out = & (Get-PsHost) -NoProfile -ExecutionPolicy Bypass -File $q -Acquire -Label $lbl 2>&1
    if ($LASTEXITCODE -ne 0) { throw "GPU queue acquire failed: $($out -join ' ')" }
    $Ctx.GpuHeld = $true; $Ctx.GpuSince = Get-Date
    $Ctx.GpuHeartbeat = Start-Job -ArgumentList (Get-GpuQueueDir), $lbl -ScriptBlock {
        param($d, $l)
        while ($true) { Start-Sleep -Seconds 300; Get-ChildItem $d -Filter "*_$l.ticket" -ErrorAction SilentlyContinue | ForEach-Object { $_.LastWriteTime = Get-Date } }
    }
    Write-Host ("{0:HH:mm:ss} GPU queue: acquired {1}" -f (Get-Date), $lbl)
    $others = @(Get-Process -Name 'Godot*' -ErrorAction SilentlyContinue)
    if ($others.Count -gt 0) {
        Write-Host ("WARNING: {0} Godot process(es) already running (PIDs {1}) outside the queue. The runner never kills processes it did not start." -f $others.Count, (($others | ForEach-Object { $_.Id }) -join ','))
    }
}
function Unlock-GpuQueue($Ctx) {
    if ($Ctx.GpuHeartbeat) { Stop-Job $Ctx.GpuHeartbeat -ErrorAction SilentlyContinue; Remove-Job $Ctx.GpuHeartbeat -Force -ErrorAction SilentlyContinue; $Ctx.GpuHeartbeat = $null }
    if ($Ctx.GpuHeld) {
        $q = Join-Path (Split-Path -Parent $PSScriptRoot) 'gpu_queue.ps1'
        & (Get-PsHost) -NoProfile -ExecutionPolicy Bypass -File $q -Release -Label (Get-GpuTicketLabel $Ctx) 2>&1 | Out-Null
        $Ctx.GpuHeld = $false
        Write-Host ("{0:HH:mm:ss} GPU queue: released {1}" -f (Get-Date), (Get-GpuTicketLabel $Ctx))
    }
}
function Enter-GpuPhase($Ctx) { Lock-GpuQueue $Ctx }
function Exit-GpuPhase($Ctx) { Unlock-GpuQueue $Ctx }
# Called between tests (nothing running) in the GPU phase: after holding the GPU for -YieldMinutes, hand it to a waiter and requeue.
function Invoke-GpuYield($Ctx) {
    if (-not $Ctx.GpuHeld -or [double]$Ctx.YieldMinutes -le 0) { return }
    if (((Get-Date) - $Ctx.GpuSince).TotalMinutes -lt $Ctx.YieldMinutes) { return }
    if ((Get-GpuWaiterCount $Ctx) -eq 0) { $Ctx.GpuSince = Get-Date; return }
    Write-Host ("{0:HH:mm:ss} GPU queue: held {1} min and others are waiting; yielding" -f (Get-Date), $Ctx.YieldMinutes)
    Unlock-GpuQueue $Ctx
    Start-Sleep -Seconds 60
    Lock-GpuQueue $Ctx
}

# ---------------------------------------------------------------- launch

function Get-RunnerParamNames([string]$RunnerPath) {
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($RunnerPath, [ref]$null, [ref]$null)
    if ($null -eq $ast.ParamBlock) { return @() }
    return @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
}

function Get-PsHost { if ($PSVersionTable.PSEdition -eq 'Core') { return (Get-Process -Id $PID).Path } return 'powershell.exe' }

# Builds the command line for a spec. Returns exe/args/env.
function Get-LaunchPlan($Ctx, $Spec) {
    $env_ = @()
    if ($Spec.Synthetic -or $Spec.Runner) {
        $runnerPath = Join-Path $script:Root $Spec.Runner
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runnerPath)
        $pn = Get-RunnerParamNames $runnerPath
        $appRoot = $null
        if ($Spec.Profile -ne 'own-runner' -or $pn -contains 'AppData') {
            $appRoot = $Ctx.ProfileRoot
            if ($Spec.Profile -eq 'fresh') { $appRoot = Initialize-TestProfile $Ctx.Label '_fresh' 'none' $true }
        }
        if ($pn -contains 'Label') { $a += @('-Label', $Ctx.Label) }
        if ($pn -contains 'Cache') { $a += @('-Cache', 'reuse') }
        if ($pn -contains 'AppData') { $a += @('-AppData', $appRoot) }
        $a += $Spec.UserArgs
        # .ps1 runners isolate themselves from the REAL APPDATA (some refuse -AppData equal to $env:APPDATA), so the child
        # always sees the real roaming folder, never the runner's isolated profile.
        $env_ += "APPDATA=$(Get-RealAppDataRoot)"
        return @{ Exe = (Get-PsHost); Args = (Join-ProcessArgs $a); Env = $env_ }
    }
    $appRoot = $Ctx.ProfileRoot
    if ($Spec.Profile -eq 'fresh') { $appRoot = Initialize-TestProfile $Ctx.Label '_fresh' 'none' $true }
    $a = @()
    if ($Spec.Mode -eq 'headless') { $a += '--headless' }
    $a += @('--path', $script:Root)
    $a += $Spec.Args
    $a += @('--script', "res://tools/$($Spec.Name).gd")
    if ($Spec.UserArgs.Count -gt 0) { $a += '--'; $a += $Spec.UserArgs }
    $env_ += "APPDATA=$appRoot"
    return @{ Exe = $Ctx.Engine; Args = (Join-ProcessArgs $a); Env = $env_ }
}

# ---------------------------------------------------------------- verdicts (design 5.5)

function Get-MarkerMatch($Lines, [string]$Marker, [bool]$Tolerant) {
    # Exact ^<MARKER>=PASS (an "ERROR:" prefix is allowed: push_error output). Tolerant (marker_fix) also accepts the
    # suffix form <MARKER>_PASS until WO5 normalises the markers.
    $m = [regex]::Escape($Marker)
    $sep = if ($Tolerant) { '(?:=|_)' } else { '=' }
    $rx = [regex]::new("^(?:ERROR:\s*)?$m$sep(PASS|FAIL|SKIP|CAPTURED)\b")
    $found = @{}
    foreach ($l in $Lines) {
        if ($l.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { continue }
        $r = $rx.Match($l)
        if ($r.Success -and -not $found.ContainsKey($r.Groups[1].Value)) { $found[$r.Groups[1].Value] = $l }
    }
    return $found
}

function Get-FirstErrorLine($Lines) {
    # Skips the known startup noise of the missing ring_voxel_mesher GDExtension (not the cause of any verdict).
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $l = $Lines[$i]
        if ($l -match 'GDExtension dynamic library not found|Error loading extension') { continue }
        if ($l -match '!FileAccess::exists\(path\)' -and ($i + 1) -lt $Lines.Count -and $Lines[$i + 1] -match 'open_dynamic_library') { continue }
        if ($l -match '^(SCRIPT ERROR|ERROR:|Parse Error)') { return $l }
    }
    return ''
}

# Known-failure resolution (design 6.1). Returns 'known' when a failure text matches the entry's regex (empty regex
# matches any failure); $null otherwise, so a different failure on a known-failing or flaky test stays a new `fail`.
# Per-machine scope is applied when the entries are loaded (Select-MachineKnown).
function Resolve-KnownFailure($Spec, [string]$FailText) {
    if ($Spec.KnownStatus -notin @('known-fail', 'flaky')) { return $null }
    if ([string]::IsNullOrEmpty($Spec.KnownMatch)) { return 'known' }
    if ([regex]::IsMatch($FailText, $Spec.KnownMatch)) { return 'known' }
    return $null
}

function Get-Verdict($Spec, $Proc, [bool]$TimedOut, [double]$TimeoutS) {
    $lines = $Proc.Lines()
    $v = [ordered]@{ status = 'error'; marker_line = ''; exit_code = $null; exit_anomaly = $false; detail = '' }
    if ($TimedOut) {
        $v.status = 'timeout'; $v.detail = "timed out after $([int]$TimeoutS) s (process tree killed)"
        return $v
    }
    $code = [int]$Proc.ExitCode
    $v.exit_code = $code
    $isCrash = ($code -lt 0) -or ($code -gt 255)
    $found = @{}
    if ($Spec.Marker) { $found = Get-MarkerMatch $lines $Spec.Marker ([bool]$Spec.MarkerFix) }
    $failText = $null
    if ($found.ContainsKey('FAIL')) {
        $v.status = 'fail'; $v.marker_line = $found['FAIL']; $v.detail = $found['FAIL']
    } elseif ($found.ContainsKey('PASS')) {
        $v.status = 'pass'; $v.marker_line = $found['PASS']
        if ($isCrash) { $v.status = 'crash'; $v.exit_anomaly = $true; $v.detail = "PASS marker printed, then crash exit code $code" }   # a crash after PASS is not a pass
        elseif ($code -ne 0) { $v.exit_anomaly = $true; $v.detail = "exit code $code after PASS marker" }
    } elseif ($found.ContainsKey('SKIP')) {
        $v.status = 'skip'; $v.marker_line = $found['SKIP']; $v.detail = $found['SKIP']
    } elseif ($found.ContainsKey('CAPTURED')) {
        $v.status = 'captured'; $v.marker_line = $found['CAPTURED']; $v.detail = $found['CAPTURED']
    } elseif ($Spec.MarkerFix) {
        # Fallback ONLY for entries that still carry marker_fix (extended/manual, no canonical marker): exit code decides,
        # plus a scan for obvious FAIL lines. Never applies to an entry without marker_fix.
        $failLine = $lines | Where-Object { $_ -match '^(?:ERROR:\s*)?.*(?:=FAIL\b|_FAIL\b|:\s*FAIL\b)' } | Select-Object -First 1
        if ($failLine) { $v.status = 'fail'; $v.detail = [string]$failLine }
        elseif ($isCrash) { $v.status = 'crash'; $v.detail = "exit code $code" }
        elseif ($code -eq 0) { $v.status = 'pass'; $v.detail = 'EXIT-CODE FALLBACK verdict (marker_fix entry, no canonical marker)' }
        else { $v.status = 'fail'; $v.detail = "exit code $code (marker_fix test)" }
    } else {
        if ($isCrash) { $v.status = 'crash'; $v.detail = "exit code $code; no marker" }
        elseif ($code -eq 0) { $v.status = 'error'; $v.detail = 'no-marker: exit 0 but no ' + $Spec.Marker + '=PASS/FAIL/SKIP line' }
        else { $v.status = 'fail'; $v.detail = "exit code $code; no marker" }
        $e = Get-FirstErrorLine $lines
        if ($e) { $v.detail = "$($v.detail) | $e" }
    }
    # Known failures: a matching failure is known-fail; a known-fail test that passes is fixed.
    if ($v.status -in @('fail', 'crash', 'error')) {
        $text = ($lines -join "`n")
        if ((Resolve-KnownFailure $Spec $text) -eq 'known') { $v.status = 'known-fail' }
        elseif ($Spec.KnownStatus -in @('known-fail', 'flaky') -and $Spec.KnownMatch) { $v.detail = "NEW failure (does not match known /$($Spec.KnownMatch)/): $($v.detail)" }
    } elseif ($v.status -eq 'pass' -and $Spec.KnownStatus -eq 'known-fail') {
        $v.status = 'fixed'
    }
    return $v
}

# ---------------------------------------------------------------- results + reporting (design 6)

function New-ResultRecord($Spec, [string]$Reason) {
    return [ordered]@{
        name = $Spec.Name; mode = $Spec.Mode; tier = $Spec.Tier; batch = $(if ($Spec.Batch) { $Spec.Batch } else { $null })
        status = 'not-run'; marker = $(if ($Spec.Marker) { $Spec.Marker } else { $null }); exit_code = $null; exit_anomaly = $false
        duration_s = $null; attempts = 0; reason = $Reason; detail = ''; log = "logs/$($Spec.Name).log"
        expected_s = $Spec.ExpectedS
    }
}

$script:StatusKeys = [ordered]@{
    'pass' = 'pass'; 'fail' = 'fail'; 'known-fail' = 'known_fail'; 'fixed' = 'fixed'; 'flaky-pass' = 'flaky_pass'; 'skip' = 'skip'
    'timeout' = 'timeout'; 'crash' = 'crash'; 'quarantined' = 'quarantined'; 'error' = 'error'; 'not-run' = 'not_run'
    'captured' = 'captured'; 'fail-batch-only' = 'fail_batch_only'
}
$script:BadStatuses = @('fail', 'timeout', 'crash', 'error', 'fail-batch-only')

function Get-Summary($Ctx) {
    $s = [ordered]@{}
    foreach ($k in $script:StatusKeys.Values) { $s[$k] = 0 }
    foreach ($r in $Ctx.Results.Values) { $s[$script:StatusKeys[$r.status]]++ }
    return $s
}

function Save-Results($Ctx, [bool]$Final) {
    $sum = Get-Summary $Ctx
    $wall = ((Get-Date) - $Ctx.Started).TotalSeconds + [double]$Ctx.PriorWall
    $sel = $Ctx.Selection
    $doc = [ordered]@{
        run_id = $Ctx.RunId; label = $Ctx.Label; suite = $Ctx.SuiteName; base = $Ctx.Base; head = $Ctx.Head; dirty = $Ctx.Dirty
        machine = $Ctx.Machine; manifest = $Ctx.Manifest; known_failures = $Ctx.KnownFailures
        selection = [ordered]@{
            changed_files = @($sel.Files).Count; tags = @($sel.Tags); unmapped = @($sel.Unmapped); core = [bool]$sel.Core
            skipped = @($sel.Skipped.Keys | ForEach-Object { "$_ : $($sel.Skipped[$_])" })
        }
        complete = $Final; wall_s = [math]::Round($wall, 1); summary = $sum
        tests = @($Ctx.Results.Values | ForEach-Object { $_ })
    }
    Write-AtomicText (Join-Path $Ctx.RunDir 'results.json') (ConvertTo-Json -InputObject $doc -Depth 8)
    Write-AtomicText (Join-Path $Ctx.RunDir 'results.md') (Format-ResultsMarkdown $Ctx $sum $wall $Final)
}

function Get-FirstFailureLine($Ctx, $R) {
    if ($R.status -in @('pass', 'skip', 'captured', 'not-run', 'flaky-pass')) { return '' }
    $d = [string]$R.detail
    if ($d.Length -gt 160) { $d = $d.Substring(0, 160) + '...' }
    if ($R.status -eq 'known-fail') { return "(known) $d" }
    return $d
}

function Format-ResultsMarkdown($Ctx, $Sum, [double]$Wall, [bool]$Final) {
    $sb = New-Object System.Text.StringBuilder
    $n = $Ctx.Results.Count
    $parts = @()
    foreach ($k in $script:StatusKeys.Keys) { $c = $Sum[$script:StatusKeys[$k]]; if ($c -gt 0) { $parts += "$c $k" } }
    $bad = @($Ctx.Results.Values | Where-Object { $script:BadStatuses -contains $_.status }).Count
    $exit = if ($bad -gt 0) { 1 } else { 0 }
    [void]$sb.AppendLine("# Test run $($Ctx.Label) $($Ctx.RunId)  ($($Ctx.SuiteName) vs $($Ctx.Base), $($Ctx.Machine.gpu))")
    $state = if ($Final) { "Exit $exit." } else { 'IN PROGRESS (not final).' }
    [void]$sb.AppendLine("$n selected: $($parts -join ', '). Wall $(Format-Duration $Wall). $state")
    $fixed = @($Ctx.Results.Values | Where-Object { $_.status -eq 'fixed' })
    if ($fixed.Count -gt 0) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine("**Known failure now passes: update tools/test_known_failures.json** ($((($fixed | ForEach-Object { $_.name }) -join ', ')))")
    }
    $attention = @($Ctx.Results.Values | Where-Object { $_.status -notin @('pass', 'skip', 'captured', 'not-run', 'flaky-pass') -or ($_.exit_anomaly) })
    $notRun = @($Ctx.Results.Values | Where-Object { $_.status -eq 'not-run' })
    $order = @('fail', 'timeout', 'crash', 'error', 'fail-batch-only', 'fixed', 'known-fail', 'quarantined')
    if ($attention.Count -gt 0 -or $notRun.Count -gt 0) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('| Status | Test | s | First failure line | Log |')
        [void]$sb.AppendLine('|---|---|---|---|---|')
        foreach ($r in ($attention | Sort-Object { $i = $order.IndexOf($_.status); if ($i -lt 0) { 99 } else { $i } }, name)) {
            $label = if ($r.status -in @('fail', 'timeout', 'crash', 'error')) { $r.status.ToUpper() } else { $r.status }
            if ($r.status -eq 'pass' -and $r.exit_anomaly) { $label = 'pass (exit anomaly)' }
            $dur = if ($null -ne $r.duration_s) { '{0:N1}' -f $r.duration_s } else { '-' }
            $first = (Get-FirstFailureLine $Ctx $r) -replace '\|', '/' -replace '[\r\n]+', ' '
            if ($r.status -eq 'pass' -and $r.exit_anomaly) { $first = $r.detail }
            $log = if ($r.status -eq 'quarantined' -and $null -eq $r.duration_s) { '-' } else { $r.log }
            [void]$sb.AppendLine("| $label | $($r.name) | $dur | $first | $log |")
        }
        if ($notRun.Count -gt 0) { [void]$sb.AppendLine("| not-run | $($notRun.Count) tests | - | $((($notRun | Select-Object -First 8 | ForEach-Object { $_.name }) -join ', '))$(if ($notRun.Count -gt 8) { ', ...' }) | - |") }
    }
    $passes = @($Ctx.Results.Values | Where-Object { $_.status -in @('pass', 'flaky-pass') })
    if ($VerbosePreference -eq 'Continue' -and $passes.Count -gt 0) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine("Passes: $($passes.Count): $((($passes | ForEach-Object { $_.name }) -join ', '))")
    } else {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine("Passes: $($passes.Count) (list with -Verbose; full data in results.json).")
    }
    $unm = @($Ctx.Selection.Unmapped)
    [void]$sb.AppendLine("Unmapped files: $(if ($unm.Count -gt 0) { $unm -join ', ' } else { 'none' })")
    return $sb.ToString()
}

# ---------------------------------------------------------------- execution

# Starts one test. Returns the running item or $null (recorded as error / quarantined / etc.).
function Start-TestItem($Ctx, $Spec) {
    $rec = $Ctx.Results[$Spec.Name]
    $rec.attempts = [int]$rec.attempts + 1
    $plan = Get-LaunchPlan $Ctx $Spec
    $log = Join-Path $Ctx.RunDir $rec.log
    $proc = New-Object RingTestProcV2
    $ok = $proc.Start($plan.Exe, $plan.Args, $script:Root, $log, [string[]]$plan.Env)
    if (-not $ok) {
        $rec.status = 'error'; $rec.detail = "could not start: $($proc.StartError)"
        Save-Results $Ctx $false
        return $null
    }
    $timeout = $Spec.TimeoutS
    if ($Spec.KnownStatus -eq 'quarantined' -and $Spec.KnownTimeout) { $timeout = [double]$Spec.KnownTimeout }
    Write-Host ("  start  {0}  [{1}, timeout {2}s]" -f $Spec.Name, $Spec.Mode, [int]$timeout)
    return [pscustomobject]@{ Spec = $Spec; Proc = $proc; Timeout = $timeout }
}

function Complete-TestItem($Ctx, $Item, [bool]$TimedOut) {
    $spec = $Item.Spec; $proc = $Item.Proc; $rec = $Ctx.Results[$spec.Name]
    $slept = $proc.SleptSeconds
    $sleptNote = if ($slept -ge 5) { "; system slept $([int]$slept) s during this test" } else { '' }
    if ($TimedOut) { $proc.KillTree(); [void]$proc.Complete("TIMEOUT after $([int]$Item.Timeout) s; process tree killed by runner$sleptNote") }
    else { [void]$proc.Complete("exit code $($proc.ExitCode)$sleptNote") }
    $v = Get-Verdict $spec $proc $TimedOut $Item.Timeout
    $dur = $proc.Elapsed   # active (sleep-free) seconds
    $rec.duration_s = [math]::Round($dur, 1)
    $rec.exit_code = $v.exit_code; $rec.exit_anomaly = $v.exit_anomaly; $rec.detail = $v.detail
    if ($slept -ge 5) {
        $rec['slept_s'] = [math]::Round($slept, 1)
        $rec.detail = "system slept $([int]$slept) s during this test; $($rec.detail)"
    }
    $status = $v.status
    # quarantined tests launched by -Suite full: a pass means fixed, anything else stays quarantined.
    if ($spec.KnownStatus -eq 'quarantined') { if ($status -eq 'pass') { $status = 'fixed' } else { $status = 'quarantined' } }
    # flaky: retry on failure, a later pass is flaky-pass.
    $requeue = $false
    if ($spec.KnownStatus -eq 'flaky') {
        if ($status -eq 'known-fail' -and $rec.attempts -le $spec.KnownRetries) { $requeue = $true; $rec.detail = "attempt $($rec.attempts) failed, retrying: $($v.detail)" }
        elseif ($status -eq 'pass' -and $rec.attempts -gt 1) { $status = 'flaky-pass' }
    }
    $rec.status = if ($requeue) { 'not-run' } else { $status }
    Write-Host ("  {0,-12} {1}  ({2:N1}s)" -f $(if ($requeue) { 'retry' } else { $rec.status }), $spec.Name, $dur)
    Save-Results $Ctx $false
    return $requeue
}

function Invoke-Lane {
    param($Ctx, [object[]]$Specs, [int]$Width)
    if (@($Specs).Count -eq 0) { return }
    $queue = New-Object System.Collections.Generic.List[object]; $queue.AddRange([object[]]@($Specs))
    $running = New-Object System.Collections.Generic.List[object]
    $Ctx.Active = $running
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        if ($queue.Count -gt 0 -and $running.Count -eq 0) { Invoke-GpuYield $Ctx }
        while ($queue.Count -gt 0 -and $running.Count -lt $Width) {
            $spec = $queue[0]; $queue.RemoveAt(0)
            $item = Start-TestItem $Ctx $spec
            if ($item) { $running.Add($item) }
        }
        Start-Sleep -Milliseconds 100
        for ($i = $running.Count - 1; $i -ge 0; $i--) {
            $it = $running[$i]
            $done = $false; $timedOut = $false
            if ($it.Proc.HasExited) { $done = $true }
            elseif ($it.Proc.Elapsed -gt $it.Timeout) { $done = $true; $timedOut = $true }
            if ($done) {
                $running.RemoveAt($i)
                $requeue = Complete-TestItem $Ctx $it $timedOut
                if ($requeue) { $queue.Insert(0, $it.Spec) }
            }
        }
    }
    $Ctx.Active = $null
}

function Stop-ActiveTests($Ctx) {
    if ($Ctx.Active) { foreach ($it in @($Ctx.Active)) { try { $it.Proc.KillTree() } catch { } } }
}

# Drops known-failure entries whose "machines" list (hardware_profile names) excludes this machine. No list = everywhere.
function Select-MachineKnown($M, $Machine) {
    foreach ($n in @($M.Known.Keys)) {
        $mach = @(Get-Val $M.Known[$n] 'machines' | Where-Object { $_ })
        if ($mach.Count -gt 0 -and $mach -notcontains $Machine.hardware_profile) { $M.Known.Remove($n) }
    }
}

function Get-MachineInfo {
    $gpu = 'unknown'
    try {
        $names = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name })
        $pick = $names | Where-Object { $_ -match 'NVIDIA|AMD|Radeon' } | Select-Object -First 1
        if (-not $pick) { $pick = $names | Select-Object -First 1 }
        if ($pick) { $gpu = [string]$pick }
    } catch { }
    $hw = 'unknown'
    if ($gpu -match '4070') { $hw = 'desktop_mid_high' } elseif ($gpu -match 'A1000') { $hw = 'laptop_a1000' }
    return [ordered]@{ gpu = $gpu; hardware_profile = $hw }
}

function Test-NeedsBuild {
    $dll = Join-Path $script:Root '.godot\mono\temp\bin\Debug\RingworldTechDemo.dll'
    if (-not (Test-Path -LiteralPath $dll)) { return 'no built assembly' }
    $built = (Get-Item -LiteralPath $dll).LastWriteTime
    $files = @(Invoke-Git @('ls-files', '*.cs', '*.csproj', '*.sln')) + @(Invoke-Git @('ls-files', '--others', '--exclude-standard', '*.cs', '*.csproj'))
    foreach ($f in $files) {
        $p = Join-Path $script:Root $f
        if ((Test-Path -LiteralPath $p) -and (Get-Item -LiteralPath $p).LastWriteTime -gt $built) { return "$f is newer than the built assembly" }
    }
    return $null
}

function Invoke-Build($Ctx) {
    $log = Join-Path $Ctx.RunDir 'build.log'
    $script_ = Join-Path $script:Root 'tools\use_godot_48.ps1'
    Write-Host 'Building C# (tools/use_godot_48.ps1 -Build) ...'
    $out = & (Get-PsHost) -NoProfile -ExecutionPolicy Bypass -File $script_ -Build 2>&1
    $code = $LASTEXITCODE
    Write-Utf8NoBom $log (($out | ForEach-Object { "$_" }) -join "`n")
    if ($code -ne 0) { throw "C# build failed (exit $code); see $log" }
}

# The manifest validator is a smoke check of its own (design 4.4), run through its script like a .ps1 runner.
function New-ValidatorSpec {
    return [pscustomobject]@{
        Name = 'manifest_validator'; Kind = 'test'; Mode = 'headless'; Tier = 'smoke'; Tags = @('tooling-policy'); Marker = 'VALIDATE_MANIFEST'; MarkerFix = ''
        Args = @(); UserArgs = @('-Quiet'); Runner = 'tools/testing/validate_manifest.ps1'; TimeoutS = 60; ExpectedS = 2; Profile = 'own-runner'
        ParallelSafe = $true; Batch = ''; Synthetic = $true; KnownStatus = ''; KnownMatch = ''; KnownRetries = 0; KnownTimeout = $null; Reason = 'smoke (manifest validator)'
    }
}

# ---------------------------------------------------------------- entry point

function Invoke-TestRun {
    [CmdletBinding()]
    param(
        [switch]$Changed, [string]$Base = 'main', [string]$Since = '', [string[]]$Files = @(),
        [string[]]$Tags = @(), [string[]]$Tests = @(),
        [ValidateSet('', 'smoke', 'gate', 'full', 'extended')][string]$Suite = '',
        [switch]$DryRun, [switch]$NoBuild, [string]$Label = 'default',
        [ValidateSet('copy', 'reuse', 'none')][string]$Cache = 'reuse',
        [int]$Parallel = 0, [int]$LocalLanes = 0, [double]$YieldMinutes = 0,
        [string]$Resume = '', [switch]$RetryFailed,
        [string]$ManifestPath = '', [string]$KnownFailuresPath = ''
    )
    # powershell.exe -File passes 'a,b' as one string; split comma lists for all list parameters.
    $Tags = @($Tags | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $Tests = @($Tests | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $toolsDir = Join-Path $script:Root 'tools'
    $labelDir = Join-Path $script:Root ".godot\test_runs\$Label"

    # ----- resume / retry: take the selection from a previous run directory
    $resumeRun = $null
    if ($Resume -or $RetryFailed) {
        $dir = $null
        if ($Resume -and $Resume -notin @('latest', 'last')) {
            foreach ($c in @($Resume, (Join-Path $labelDir $Resume), (Join-Path $script:Root $Resume))) { if ((Test-Path -LiteralPath (Join-Path $c 'results.json'))) { $dir = (Resolve-Path -LiteralPath $c).Path; break } }
        } else {
            $latest = Get-ChildItem -LiteralPath $labelDir -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'results.json')) } | Sort-Object Name -Descending | Select-Object -First 1
            if ($latest) { $dir = $latest.FullName }
        }
        if (-not $dir) { throw "no previous run to resume (label '$Label', -Resume '$Resume')" }
        $resumeRun = Read-JsonFile (Join-Path $dir 'results.json')
        $resumeRun | Add-Member -NotePropertyName _dir -NotePropertyValue $dir -Force
        if (-not $PSBoundParameters.ContainsKey('Label')) { $Label = [string]$resumeRun.label; $labelDir = Join-Path $script:Root ".godot\test_runs\$Label" }
    }

    # ----- manifest (a resumed run keeps the manifest it started with)
    if ($resumeRun -and -not $ManifestPath -and $resumeRun.manifest) { $ManifestPath = [string]$resumeRun.manifest; if (-not $KnownFailuresPath) { $KnownFailuresPath = [string]$resumeRun.known_failures } }
    $customManifest = [bool]$ManifestPath
    if (-not $ManifestPath) { $ManifestPath = Join-Path $toolsDir 'test_manifest.json' }
    $ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
    if (-not $KnownFailuresPath) { $KnownFailuresPath = Join-Path (Split-Path -Parent $ManifestPath) 'test_known_failures.json' }
    $M = Import-TestManifest $ManifestPath $KnownFailuresPath
    $machineInfo = Get-MachineInfo
    Select-MachineKnown $M $machineInfo
    $defaults = Get-Val $M.Manifest 'defaults'
    if ($Parallel -le 0) { $Parallel = [int](Get-Val $defaults 'headless_parallel'); if ($Parallel -le 0) { $Parallel = 6 } }
    if ($LocalLanes -le 0) { $LocalLanes = [int](Get-Val $defaults 'gpu_local_parallel'); if ($LocalLanes -le 0) { $LocalLanes = 3 } }
    $engine = Join-Path $script:Root ([string]$M.Manifest.engine)
    if ($resumeRun) { $Suite = [string]$resumeRun.suite -replace '^(changed|tests|tags)$', '' }

    # ----- selection
    $useFiles = $false; $filesList = @(); $suiteName = $Suite
    $sel = $null
    if ($resumeRun) {
        $suiteName = [string]$resumeRun.suite
        $sel = [pscustomobject]@{ Items = [ordered]@{}; Skipped = [ordered]@{}; Tags = @($resumeRun.selection.tags); Core = $null; Unmapped = @($resumeRun.selection.unmapped); Files = @(); Meta = $false; Ignored = 0 }
        foreach ($t in $resumeRun.tests) { $sel.Items[[string]$t.name] = @([string]$t.reason) }
    } else {
        $explicit = ($Tags.Count -gt 0) -or ($Tests.Count -gt 0) -or [bool]$Suite -or ($Files.Count -gt 0)
        if ($Changed -or $Since -or (-not $explicit)) { $useFiles = $true }
        if ($Files.Count -gt 0) { $useFiles = $true; $filesList = @($Files | ForEach-Object { $_ -replace '\\', '/' }) }
        elseif ($useFiles) { $filesList = Get-ChangedFiles $Base $Since $Label }
        if (-not $suiteName) { $suiteName = if ($useFiles) { 'changed' } elseif ($Tests.Count -gt 0) { 'tests' } else { 'tags' } }
        $sel = Get-Selection -M $M -Files $filesList -Tags $Tags -Tests $Tests -Suite $Suite -UseFiles $useFiles
    }

    # ----- specs
    $specs = New-Object System.Collections.Generic.List[object]
    foreach ($name in $sel.Items.Keys) {
        if ($name -eq 'manifest_validator') { $specs.Add((New-ValidatorSpec)); continue }
        if (-not $M.Tests.Contains($name)) { throw "test '$name' of the resumed run is not in manifest $ManifestPath" }
        $spec = New-TestSpec $name $M.Tests[$name] $M.Known[$name]
        $spec | Add-Member -NotePropertyName Reason -NotePropertyValue (($sel.Items[$name] | Select-Object -First 3) -join '; ') -Force
        $specs.Add($spec)
    }
    # The manifest validator is a smoke check of its own (design 4.4). Not added for a scratch -ManifestPath.
    $validatorWanted = (-not $customManifest) -and (-not $resumeRun) -and ($Suite -in @('smoke', 'gate', 'full', 'extended') -or ($useFiles -and $true))
    if ($validatorWanted) { $specs.Add((New-ValidatorSpec)) }
    if ($resumeRun) {
        # Only the remainder: not-run/timeout/crash/error (+fail/fail-batch-only with -RetryFailed). Everything else keeps its stored result.
        $redo = @('not-run', 'timeout', 'crash', 'error', 'running')
        if ($RetryFailed) { $redo += @('fail', 'fail-batch-only') }
        $keep = @{}
        foreach ($t in $resumeRun.tests) { if ($redo -notcontains [string]$t.status) { $keep[[string]$t.name] = $t } }
        $toRun = @($specs | Where-Object { -not $keep.ContainsKey($_.Name) })
    } else { $toRun = $specs.ToArray() }

    # Quarantined tests are not launched unless -Suite full.
    $quarantined = @($toRun | Where-Object { $_.KnownStatus -eq 'quarantined' -and $Suite -ne 'full' })
    $toLaunch = @($toRun | Where-Object { $quarantined -notcontains $_ })
    $est = Get-Estimate $toLaunch $Parallel $LocalLanes

    # ----- header / dry run
    Write-Host ("Selection: {0} tests ({1})" -f $specs.Count, $suiteName)
    if ($useFiles -and -not $resumeRun) {
        Write-Host ("  changed files: {0}  (ignored {1})  tags: {2}  core: {3}  unmapped: {4}" -f @($filesList).Count, $sel.Ignored, $(if ($sel.Tags.Count) { $sel.Tags -join ', ' } else { '-' }), $(if ($sel.Core) { $sel.Core } else { 'no' }), $(if ($sel.Unmapped.Count) { $sel.Unmapped -join ', ' } else { 'none' }))
        if ($sel.Meta) { Write-Host '  test-tooling files changed: smoke set (includes the manifest validator)' }
    } elseif (-not $resumeRun -and $sel.Tags.Count) { Write-Host ("  tags: {0}" -f ($sel.Tags -join ', ')) }
    if ($DryRun) {
        foreach ($s in ($specs | Sort-Object @{e = { switch ($_.Mode) { 'headless' { 0 } 'gpu-local' { 1 } default { 2 } } } }, @{e = { -$_.ExpectedS } }, Name)) {
            $flag = if ($quarantined -contains $s) { ' [quarantined: not launched]' } elseif ($s.KnownStatus) { " [$($s.KnownStatus)]" } else { '' }
            Write-Host ("  {0,-9} {1,-8} ~{2,4}s  {3}{4}" -f $s.Mode, $s.Tier, [int]$s.ExpectedS, $s.Name, $flag)
            Write-Host ("            reason: {0}" -f $s.Reason)
        }
        foreach ($k in $sel.Skipped.Keys) { Write-Host ("  skipped   {0}: {1}" -f $k, $sel.Skipped[$k]) }
        Write-Host ("Estimate: headless ~{0} ({1} lanes), gpu-local ~{2} ({3} lanes), gpu ~{4}; total ~{5} + build" -f (Format-Duration $est.Headless), $Parallel, (Format-Duration $est.GpuLocal), $LocalLanes, (Format-Duration $est.Gpu), (Format-Duration $est.Total))
        return 0
    }
    if ($specs.Count -eq 0 -and -not $resumeRun) { Write-Host 'Nothing selected.'; return 0 }
    if (-not (Test-Path -LiteralPath $engine)) { throw "engine not found: $engine" }

    # ----- run directory + context
    $runId = if ($resumeRun) { [string]$resumeRun.run_id } else { Get-Date -Format 'yyyyMMdd-HHmmss' }
    $runDir = if ($resumeRun) { $resumeRun._dir } else { Join-Path $labelDir $runId }
    New-Item -ItemType Directory -Force (Join-Path $runDir 'logs') | Out-Null
    $head = (Invoke-Git @('rev-parse', '--short', 'HEAD') | Select-Object -First 1)
    $dirty = (@(Invoke-Git @('status', '--porcelain')).Count -gt 0)
    $ctx = [pscustomobject]@{
        Label = $Label; RunId = $runId; RunDir = $runDir; SuiteName = $suiteName; Base = $(if ($resumeRun) { [string]$resumeRun.base } else { $Base })
        Manifest = $(if ($customManifest) { $ManifestPath } else { $null }); KnownFailures = $(if ($customManifest) { $KnownFailuresPath } else { $null }); Head = $head; Dirty = $dirty; Machine = $machineInfo; Selection = $sel; Engine = $engine; ProfileRoot = $null
        Results = [ordered]@{}; Started = (Get-Date); PriorWall = $(if ($resumeRun) { [double]$resumeRun.wall_s } else { 0.0 }); Active = $null
        YieldMinutes = $YieldMinutes; GpuHeld = $false; GpuSince = $null; GpuHeartbeat = $null
    }
    if ($resumeRun) {
        foreach ($t in $resumeRun.tests) {
            $r = [ordered]@{}; foreach ($p in $t.PSObject.Properties) { $r[$p.Name] = $p.Value }
            if ($redo -contains [string]$t.status) { $r['status'] = 'not-run' }
            $ctx.Results[[string]$t.name] = $r
        }
    }
    foreach ($s in $specs) { if (-not $ctx.Results.Contains($s.Name)) { $ctx.Results[$s.Name] = New-ResultRecord $s $s.Reason } }
    foreach ($s in $quarantined) { $ctx.Results[$s.Name]['status'] = 'quarantined'; $ctx.Results[$s.Name]['detail'] = 'quarantined (not launched; -Suite full launches it): ' + (Get-Val $M.Known[$s.Name] 'evidence') }
    Save-Results $ctx $false
    Write-Host ("Run {0}  ->  {1}" -f $runId, $runDir)

    $exitCode = 0
    [RingTestPower]::KeepAwake($true)
    try {
        # ----- build, profile, prune (design 8.2)
        if (-not $NoBuild) {
            $why = Test-NeedsBuild
            if ($why) { Write-Host "C# build needed: $why"; Invoke-Build $ctx } else { Write-Host 'C# build: up to date (skipped)' }
        }
        $excl = @("appdata_$Label", "appdata_${Label}_fresh")
        Invoke-Prune $excl
        $needProfile = @($toLaunch | Where-Object { -not ($_.Synthetic) }).Count -gt 0
        if ($needProfile) { $ctx.ProfileRoot = Initialize-TestProfile $Label '' $Cache $false }

        # ----- lanes: headless (parallel, longest first), then the GPU phase (gpu-local x3, then sequential gpu)
        $hl = @($toLaunch | Where-Object { $_.Mode -eq 'headless' })
        $safe = @($hl | Where-Object { $_.ParallelSafe } | Sort-Object @{e = { -$_.ExpectedS } }, Name)
        $unsafe = @($hl | Where-Object { -not $_.ParallelSafe } | Sort-Object @{e = { -$_.ExpectedS } }, Name)
        if ($hl.Count) { Write-Host ("Headless lane: {0} tests, {1} parallel ({2} run alone)" -f $hl.Count, $Parallel, $unsafe.Count) }
        Invoke-Lane $ctx $safe $Parallel
        Invoke-Lane $ctx $unsafe 1
        $gl = @($toLaunch | Where-Object { $_.Mode -eq 'gpu-local' } | Sort-Object @{e = { -$_.ExpectedS } }, Name)
        $gp = @($toLaunch | Where-Object { $_.Mode -eq 'gpu' } | Sort-Object Name)
        if ($gl.Count -or $gp.Count) {
            Enter-GpuPhase $ctx
            try {
                if ($gl.Count) { Write-Host ("GPU local lane: {0} tests, {1} parallel" -f $gl.Count, $LocalLanes) }
                Invoke-Lane $ctx $gl $LocalLanes
                if ($gp.Count) { Write-Host ("GPU lane: {0} tests, sequential" -f $gp.Count) }
                Invoke-Lane $ctx $gp 1
            } finally { Exit-GpuPhase $ctx }
        }
    } catch {
        Write-Host "run_tests: runner failure: $($_.Exception.Message)"
        Stop-ActiveTests $ctx
        Save-Results $ctx $false
        throw
    } finally {
        Stop-ActiveTests $ctx
        [RingTestPower]::KeepAwake($false)
    }

    Save-Results $ctx $true
    $bad = @($ctx.Results.Values | Where-Object { $script:BadStatuses -contains $_.status })
    $exitCode = if ($bad.Count -gt 0) { 1 } else { 0 }
    if ($exitCode -eq 0 -and @($ctx.Results.Values | Where-Object { $_.status -eq 'not-run' }).Count -eq 0) {
        Write-AtomicText (Join-Path $labelDir 'last_green.json') (ConvertTo-Json -InputObject ([ordered]@{ head = (Invoke-Git @('rev-parse', 'HEAD') | Select-Object -First 1); run_id = $runId; time = (Get-Date -Format o) }))
    }
    Remove-OldRuns $labelDir $runId
    Write-Host ''
    Get-Content -LiteralPath (Join-Path $runDir 'results.md') | Write-Host
    return $exitCode
}

Export-ModuleMember -Function Invoke-TestRun, Get-Selection, Import-TestManifest, Get-ChangedFiles
