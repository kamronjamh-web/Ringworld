# Shared Godot launcher pieces (test-speed plan B1a): pre-launch parse check, supervised launch with a stall watchdog.
# Works on Windows PowerShell 5.1 and PowerShell 7. No Python. Windows only (taskkill).
#
# Exports: Test-GodotScriptParse | Invoke-GodotPreflight | Invoke-GodotJob | Test-UserPlaying
# Class RingGpuJobProc is a superset of RingTestProcV2 (tools/testing/TestRunner.psm1): merged timestamped log or two raw files,
# plus SilentSeconds (time since the last output line) and LineCount. All times are unbiased interrupt time (sleep does not count).

$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:DefaultEngine = Join-Path $script:Root 'artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe'

if (-not ('RingGpuJobProc' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

// One child process. Output goes either to one merged timestamped log (mergedLog set) or to two raw files without any prefix
// (stdoutLog / stderrLog), flushed per line. Raw lines are also kept in memory. Notes ("# text") go to the merged log, or to the stderr file.
public class RingGpuJobProc
{
    Process p;
    StreamWriter w;
    StreamWriter wOut;
    StreamWriter wErr;
    readonly object lk = new object();
    readonly List<string> lines = new List<string>();
    bool completed;
    bool merged;
    public string StartError = "";
    public int Pid = 0;
    [DllImport("kernel32.dll")]
    static extern bool QueryUnbiasedInterruptTime(out ulong t);
    ulong startU;
    ulong exitU;
    ulong lastLineU;

    static ulong NowU()
    {
        ulong t;
        QueryUnbiasedInterruptTime(out t);
        return t;
    }

    public bool Start(string exe, string args, string workDir, string mergedLog, string stdoutLog, string stderrLog, string[] envPairs)
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
            merged = !string.IsNullOrEmpty(mergedLog);
            if (merged) w = Open(mergedLog);
            else
            {
                if (!string.IsNullOrEmpty(stdoutLog)) wOut = Open(stdoutLog);
                if (!string.IsNullOrEmpty(stderrLog)) wErr = Open(stderrLog);
            }
            p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { Add(e.Data, false); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { Add(e.Data, true); };
            if (merged) w.WriteLine("# " + exe + " " + args);
            p.Start();
            startU = NowU();
            lastLineU = startU;
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

    static StreamWriter Open(string path)
    {
        string d = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        StreamWriter sw = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        sw.AutoFlush = true;
        return sw;
    }

    void Add(string line, bool isErr)
    {
        if (line == null) return;
        lock (lk)
        {
            lines.Add(line);
            lastLineU = NowU();
            if (merged)
            {
                if (w != null)
                {
                    double t = (DateTime.Now - p.StartTime).TotalSeconds;
                    w.WriteLine("[" + t.ToString("F3").PadLeft(8) + "] " + line);
                }
            }
            else
            {
                StreamWriter x = isErr ? wErr : wOut;
                if (x != null) x.WriteLine(line);
            }
        }
    }

    public void Note(string text)
    {
        lock (lk)
        {
            StreamWriter x = merged ? w : wErr;
            if (x != null) x.WriteLine("# " + text);
        }
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

    public double Elapsed
    {
        get
        {
            ulong end = exitU != 0 ? exitU : NowU();
            return (end - startU) / 10000000.0;
        }
    }
    public double Duration { get { return Elapsed; } }

    // Active seconds since the last output line (or since start when there was none).
    public double SilentSeconds
    {
        get
        {
            lock (lk)
            {
                ulong end = exitU != 0 ? exitU : NowU();
                return end > lastLineU ? (end - lastLineU) / 10000000.0 : 0.0;
            }
        }
    }

    public int LineCount { get { lock (lk) { return lines.Count; } } }

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

    // Lines from index 'from' on (cheap incremental scan).
    public string[] LinesFrom(int from)
    {
        lock (lk)
        {
            if (from >= lines.Count) return new string[0];
            return lines.GetRange(from, lines.Count - from).ToArray();
        }
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

    // Wait for the async readers to drain (bounded), then close the log(s).
    public void Complete(string footer)
    {
        if (completed) return;
        completed = true;
        try { if (p.HasExited) MarkExit(); } catch (Exception) { }
        try { Task t = Task.Factory.StartNew(delegate() { p.WaitForExit(); }); t.Wait(5000); } catch (Exception) { }
        lock (lk)
        {
            if (w != null) { w.WriteLine("# " + footer); w.Dispose(); w = null; }
            if (wErr != null) { wErr.Dispose(); wErr = null; }
            if (wOut != null) { wOut.Dispose(); wOut = null; }
        }
    }
}
'@
}

function ConvertTo-ProcessArgString([string[]]$a) {
    $out = foreach ($x in $a) {
        if ($x -eq '' -or $x -match '[\s"]') { '"' + ($x -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' } else { $x }
    }
    return ($out -join ' ')
}

function Test-UserPlaying {
    $playing = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'Godot%'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match '--remote-debug' })
    return ($playing.Count -gt 0 -or (Test-Path (Join-Path $script:Root '.godot\mono\temp\bin\DebugPlaySaved\RingworldTechDemo.dll')))
}

$script:ParsePattern = 'Parse Error|Compile Error|Failed to load script'

function Start-ParseProc([string]$Script, [string]$Engine) {
    $scratch = Join-Path $script:Root (".godot\gpujob_parse_{0}_{1}" -f $PID, ([guid]::NewGuid().ToString('N').Substring(0, 8)))
    New-Item -ItemType Directory -Force $scratch | Out-Null
    $argStr = ConvertTo-ProcessArgString @('--headless', '--path', $script:Root, '--check-only', '--script', $Script)
    $proc = New-Object RingGpuJobProc
    $ok = $proc.Start($Engine, $argStr, $script:Root, (Join-Path $scratch 'parse.log'), $null, $null, @("APPDATA=$scratch"))
    return [pscustomobject]@{ Script = $Script; Proc = $proc; Scratch = $scratch; StartOk = $ok }
}

function Complete-ParseProc($h, [double]$TimeoutS) {
    $proc = $h.Proc
    $timedOut = $false
    if ($h.StartOk) {
        while (-not $proc.HasExited) {
            if ($proc.Elapsed -ge $TimeoutS) { $timedOut = $true; $proc.KillTree(); break }
            Start-Sleep -Milliseconds 100
        }
        $proc.Complete('parse check done')
    }
    $errs = @()
    if (-not $h.StartOk) { $errs = @("could not start engine: $($proc.StartError)") }
    elseif ($timedOut) { $errs = @("parse check timed out after $TimeoutS s") }
    else {
        $errs = @($proc.Lines() | Where-Object { $_ -match $script:ParsePattern -and $_ -notmatch 'ring_voxel_mesher|gdextension' })
    }
    $secs = if ($h.StartOk) { [math]::Round($proc.Elapsed, 2) } else { 0 }
    for ($i = 0; $i -lt 5; $i++) {
        try { Remove-Item -LiteralPath $h.Scratch -Recurse -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 200 }
    }
    return [pscustomobject]@{ Script = $h.Script; Ok = ($errs.Count -eq 0); Errors = $errs; Seconds = $secs }
}

# The engine exits 0 even on a parse error, so the verdict comes from the output (Parse Error / Compile Error / Failed to load script).
function Test-GodotScriptParse {
    param([Parameter(Mandatory)][string]$Script, [string]$Engine = $script:DefaultEngine, [int]$TimeoutS = 60)
    if (-not (Test-Path -LiteralPath $Engine)) {
        return [pscustomobject]@{ Script = $Script; Ok = $false; Errors = @("engine not found: $Engine"); Seconds = 0 }
    }
    $h = Start-ParseProc $Script $Engine
    return (Complete-ParseProc $h $TimeoutS)
}

function Invoke-GodotPreflight {
    param([Parameter(Mandatory)][string[]]$EntryScripts, [string[]]$RequiredFiles = @(), [int]$Parallel = 4, [string]$Engine = $script:DefaultEngine)
    $problems = 0
    foreach ($f in $RequiredFiles) {
        $p = if ([System.IO.Path]::IsPathRooted($f)) { $f } else { Join-Path $script:Root $f }
        if (-not (Test-Path -LiteralPath $p)) { Write-Host "PREFLIGHT problem: required file missing: $f"; $problems++ }
    }
    $queue = New-Object System.Collections.Queue
    foreach ($s in ($EntryScripts | Where-Object { $_ })) { $queue.Enqueue($s) }
    $running = New-Object System.Collections.ArrayList
    $deadline = @{}
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt [math]::Max(1, $Parallel)) {
            $s = $queue.Dequeue()
            if (-not (Test-Path -LiteralPath $Engine)) { Write-Host "PREFLIGHT problem: engine not found: $Engine"; $problems++; continue }
            [void]$running.Add((Start-ParseProc $s $Engine))
        }
        foreach ($h in @($running)) {
            if ($h.Proc.HasExited -or $h.Proc.Elapsed -ge 60 -or -not $h.StartOk) {
                $r = Complete-ParseProc $h 60
                [void]$running.Remove($h)
                if (-not $r.Ok) {
                    $problems++
                    foreach ($e in $r.Errors) { Write-Host "PREFLIGHT problem: $($r.Script): $e" }
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($problems -eq 0) { Write-Host 'PREFLIGHT=PASS'; return $true }
    Write-Host "PREFLIGHT=FAIL $problems problem(s)"
    return $false
}

function Invoke-GodotJob {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string[]]$Arguments = @(),
        [string]$WorkDir = $script:Root,
        [string]$Log = '', [string]$StdOutLog = '', [string]$StdErrLog = '',
        [hashtable]$Env = @{},
        [double]$TimeoutS = 1800, [double]$StallS = 600,
        [string]$EntryScript = '',
        [scriptblock]$OnTick = $null
    )
    $ErrorActionPreference = 'Continue'
    if (-not $Log -and -not $StdOutLog -and -not $StdErrLog) { throw 'Invoke-GodotJob: give -Log, or -StdOutLog/-StdErrLog' }
    $pairs = @(foreach ($k in $Env.Keys) { "$k=$($Env[$k])" })
    $proc = New-Object RingGpuJobProc
    $argStr = ConvertTo-ProcessArgString $Arguments
    if (-not $proc.Start($Exe, $argStr, $WorkDir, $Log, $StdOutLog, $StdErrLog, $pairs)) {
        return [pscustomobject]@{ Status = 'error'; ExitCode = $null; Detail = "could not start: $($proc.StartError)"; Seconds = 0; Lines = @(); Pid = 0 }
    }
    $status = ''; $detail = ''
    $scanned = 0; $warned = $false
    $entryNeedle = if ($EntryScript) { "Failed to load script `"$EntryScript`"" } else { '' }
    $lastTick = [DateTime]::MinValue
    while ($true) {
        if ($proc.HasExited) { $status = 'exited'; break }
        if ($entryNeedle) {
            $new = $proc.LinesFrom($scanned)
            $scanned += $new.Count
            $hit = $new | Where-Object { $_.Contains($entryNeedle) } | Select-Object -First 1
            if ($hit) {
                $status = 'load-failed'; $detail = $hit
                $proc.Note("LOAD-FAILED: entry script did not load; process tree killed")
                $proc.KillTree(); break
            }
        }
        $silent = $proc.SilentSeconds
        if ($silent -ge $StallS) {
            $status = 'stalled'; $detail = "no output for $([int]$silent) s"
            $proc.Note("STALLED: no output for $([int]$silent) s; process tree killed")
            $proc.KillTree(); break
        }
        if (-not $warned -and $silent -ge ($StallS / 2)) {
            $warned = $true
            $proc.Note("WARNING: no output for $([int]$silent) s (stall limit $([int]$StallS) s)")
        }
        if ($proc.Elapsed -ge $TimeoutS) {
            $status = 'timeout'; $detail = "timed out after $([int]$TimeoutS) s of active time"
            $proc.Note("TIMEOUT: $([int]$TimeoutS) s; process tree killed")
            $proc.KillTree(); break
        }
        if ($OnTick -and (([DateTime]::UtcNow - $lastTick).TotalSeconds -ge 1)) {
            $lastTick = [DateTime]::UtcNow
            try { & $OnTick $proc } catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    for ($i = 0; $i -lt 50 -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 100 }
    $exit = $null
    try { if ($proc.HasExited) { $exit = $proc.ExitCode } } catch { }
    $lines = $proc.Lines()
    $proc.Complete("job $status, exit code $exit, $([math]::Round($proc.Elapsed, 1)) s")
    $lines = $proc.Lines()
    # The process may have ended on its own before the poll saw the line: still a load failure, with the real exit code.
    if ($status -eq 'exited' -and $entryNeedle -and @($lines | Where-Object { $_.Contains($entryNeedle) }).Count -gt 0) { $status = 'load-failed'; $detail = $entryNeedle }
    if ($status -eq 'load-failed') {
        $first = $lines | Where-Object { $_ -match 'Parse Error|Compile Error' } | Select-Object -First 1
        if ($first) { $detail = $first }
    }
    return [pscustomobject]@{
        Status = $status; ExitCode = $exit; Detail = $detail
        Seconds = [math]::Round($proc.Elapsed, 2); Lines = $lines; Pid = $proc.Pid
    }
}

Export-ModuleMember -Function Test-GodotScriptParse, Invoke-GodotPreflight, Invoke-GodotJob, Test-UserPlaying
