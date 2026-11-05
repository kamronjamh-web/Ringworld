using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Diagnostics-only frame decomposition probe for the Phase C warm-load stall attribution (WO C3 step 1).
/// Driven by tools/menu_warm_stall_probe.gd; never part of a shipped scene. It changes no gameplay state: it only listens to
/// SceneTree / RenderingServer frame signals and inserts inert marker nodes next to the warm world's top-level children so the
/// gap between two markers is the process (or physics) time of the owner subtree in between.
///
/// Per frame (post_draw to post_draw) it records: post-draw continuations + present (pre_physics), physics, process_frame
/// signal handlers (GDScript awaits), node _process split by marker gaps, post-process (deferred calls, timers, tweens, RS sync)
/// and RS draw, plus GC count/pause/allocation deltas and pipeline compilation deltas. Frames above
/// <see cref="DetailThresholdMs"/> keep the full marker gap list.
/// </summary>
public partial class WarmStallProbe : Node
{
    public double DetailThresholdMs { get; set; } = 50.0;
    /// <summary>Opt-in render-thread CPU attribution; fixed storage allocated at Begin, exported after measurement.</summary>
    public bool VegetationRenderTrace { get; set; }
    public int VegetationRenderTraceCapacity { get; set; } = 32768;
    private RingVegetationWorkTrace.Capture? _vegetationTrace;
    private bool _vegetationTraceStopRequested;
    public bool VegetationRenderTraceComplete => _vegetationTrace?.Complete ?? true;

    /// <summary>A-B experiment only (quality presets WO-4 / N5): induce a blocking GC of this generation right after the frame's
    /// draw submission (post_draw, CPU slack while GPU-bound) once GcPaceMegabytes have been allocated since the last GC, or every
    /// GcPaceFrames frames. -1 = off.</summary>
    public int GcPaceGeneration { get; set; } = -1;
    public double GcPaceMegabytes { get; set; } = 0.0;
    public int GcPaceFrames { get; set; } = 0;
    public int GcPaceCollections { get; private set; }
    private long _gcPaceAllocAtLast;
    private int _gcPaceCountAtLast;
    private int _gcPaceFrameCounter;

    /// <summary>A-B experiment only: GCSettings.LatencyMode name applied at Begin ("SustainedLowLatency", "Interactive", ...); empty = unchanged.</summary>
    public string GcLatencyMode { get; set; } = "";

    private struct MarkEntry { public string Label; public ulong T; public bool Physics; public long MainAlloc; }

    private sealed class Frame
    {
        public ulong Start, FirstPhysics, ProcessSignal, FirstNode, LastNode, PreDraw, PostDraw;
        public long StartMainAlloc;
        public int PhysicsSteps;
        public readonly List<MarkEntry> Marks = new(128);

        public void Reset()
        {
            Start = FirstPhysics = ProcessSignal = FirstNode = LastNode = PreDraw = PostDraw = 0;
            StartMainAlloc = 0;
            PhysicsSteps = 0;
            Marks.Clear();
        }
    }

    private Frame _frame = new();
    private Frame _spareFrame = new();
    private ulong _lastPostDraw;
    private ulong _startUsec;
    private int _frameIndex;
    private readonly int[] _gc = new int[3];
    private TimeSpan _gcPause;
    private long _alloc;
    private readonly long[] _pipelines = new long[5];
    private long _textureMem, _bufferMem;
    private TimeSpan _jitTime;
    private long _jitMethods;
    private readonly StringBuilder _csv = new();
    private readonly StringBuilder _detail = new();
    private readonly List<StallProbeMarker> _markers = new();
    private Node? _loader;
    private Node? _warm;
    private string _context = "";
    private bool _started;
    // Main-thread allocation per owner gap (bytes, ms, frames) keyed by context and gap target; written to alloc_owners.csv.
    // Key: (context, previous mark label or null for a world-child gap, previous is physics, mark label, mark is physics). Strings are built at
    // WriteReport so the per-frame bookkeeping stays allocation-free; the probe's own CloseFrame allocation is excluded and reported as probe_self.
    private readonly Dictionary<(string ctx, string? prev, bool prevPhysics, string label, bool physics), (long bytes, double ms, int frames)> _ownerAlloc = new();
    private readonly List<(double ms, int index)> _gaps = new();
    private long _lastMainAlloc;
    private long _probeSelfBytes;
    private const int MaxPrintsPerContext = 20;
    private string? _printContext;
    private int _printedInContext;

    private static long MainAllocNow() => GC.GetAllocatedBytesForCurrentThread();

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public ulong MainThreadOsId { get; private set; }

    /// <summary>Begins recording. Call once from the driver after the probe is in the tree.</summary>
    public void Begin()
    {
        if (_started) return;
        _started = true;
        if (!string.IsNullOrEmpty(GcLatencyMode) && Enum.TryParse(GcLatencyMode, true, out System.Runtime.GCLatencyMode mode))
        {
            System.Runtime.GCSettings.LatencyMode = mode;
            GD.Print($"STALL_PROBE gc latency mode={System.Runtime.GCSettings.LatencyMode}");
        }
        if (GcPaceGeneration >= 0) GD.Print($"STALL_PROBE gc pace gen={GcPaceGeneration} mb={GcPaceMegabytes} frames={GcPaceFrames}");
        _gcPaceAllocAtLast = GC.GetTotalAllocatedBytes(false);
        _gcPaceCountAtLast = GC.CollectionCount(0);
        MainThreadOsId = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
        _startUsec = Time.GetTicksUsec();
        if (VegetationRenderTrace) _vegetationTrace = RingVegetationWorkTrace.Begin(VegetationRenderTraceCapacity);
        _lastPostDraw = _startUsec;
        _frame.Start = _startUsec;
        _lastMainAlloc = MainAllocNow();
        _frame.StartMainAlloc = _lastMainAlloc;
        SnapshotCounters(out _, out _, out _, out _, out _, out _);
        SceneTree tree = GetTree();
        tree.PhysicsFrame += OnPhysicsFrame;
        tree.ProcessFrame += OnProcessFrame;
        RenderingServer.FramePreDraw += OnPreDraw;
        RenderingServer.FramePostDraw += OnPostDraw;
        var first = new StallProbeMarker { Name = "StallProbeFirst", Probe = this, Label = "__first", ProcessPriority = int.MinValue, ProcessPhysicsPriority = int.MinValue };
        var last = new StallProbeMarker { Name = "StallProbeLast", Probe = this, Label = "__last", ProcessPriority = int.MaxValue, ProcessPhysicsPriority = int.MaxValue };
        tree.Root.AddChild(first);
        tree.Root.AddChild(last);
        _markers.Add(first);
        _markers.Add(last);
        _csv.Append("frame,t_ms,total_ms,pre_physics_ms,physics_ms,physics_steps,signal_handlers_ms,nodes_ms,post_process_ms,draw_ms,gc0,gc1,gc2,gc_pause_ms,alloc_mb,pipe_surface,pipe_mesh,pipe_canvas,pipe_draw,pipe_spec,tex_mem_mb_delta,buf_mem_mb_delta,jit_ms,jit_methods,context,top_gap,main_alloc_mb\n");
    }

    /// <summary>Burns about <paramref name="milliseconds"/> on the calling (main) thread so a sampled trace can be aligned to Time.get_ticks_usec.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public ulong StallProbeAlignmentSpin(double milliseconds)
    {
        ulong begin = Time.GetTicksUsec();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double sink = 0;
        while (watch.Elapsed.TotalMilliseconds < milliseconds) sink += Math.Sqrt(sink + 1.0);
        GD.Print($"STALL_PROBE_ALIGN begin_usec={begin} end_usec={Time.GetTicksUsec()} ms={milliseconds} os_thread={MainThreadOsId} sink={(sink > 0 ? 1 : 0)}");
        return begin;
    }

    /// <summary>Optional context source: the loader stage and warm state are sampled each frame from these GDScript owners.</summary>
    public void SetContextSources(Node? loader, Node? warm)
    {
        _loader = loader;
        _warm = warm;
    }

    public void SetContext(string context) => _context = context;

    /// <summary>Inserts one marker after every enabled top-level child of the world (same process and physics priority).</summary>
    public int InstrumentWorld(Node world)
    {
        var children = new List<Node>();
        foreach (Node child in world.GetChildren()) children.Add(child);
        var head = new StallProbeMarker { Name = "StallProbe_world_head", Probe = this, Label = "world_head" };
        world.AddChild(head);
        world.MoveChild(head, 0);
        _markers.Add(head);
        int added = 1;
        foreach (Node child in children)
        {
            if (!IsInstanceValid(child)) continue;
            var marker = new StallProbeMarker
            {
                Name = "StallProbe_" + child.Name,
                Probe = this,
                Label = child.Name.ToString(),
                ProcessPriority = child.ProcessPriority,
                ProcessPhysicsPriority = child.ProcessPhysicsPriority,
            };
            world.AddChild(marker);
            world.MoveChild(marker, child.GetIndex() + 1);
            _markers.Add(marker);
            added++;
        }
        return added;
    }

    /// <summary>Instruments the root-level siblings (menu, loader, warm owner, launcher, world) so their subtrees are separated too.</summary>
    public int InstrumentRootChild(Node child)
    {
        Node root = GetTree().Root;
        if (child.GetParent() != root) return 0;
        var marker = new StallProbeMarker { Name = "StallProbeRoot_" + child.Name, Probe = this, Label = "root:" + child.Name, ProcessPriority = child.ProcessPriority, ProcessPhysicsPriority = child.ProcessPhysicsPriority };
        root.AddChild(marker);
        root.MoveChild(marker, child.GetIndex() + 1);
        _markers.Add(marker);
        return 1;
    }

    internal void Mark(string label, bool physics)
    {
        ulong now = Time.GetTicksUsec();
        _frame.Marks.Add(new MarkEntry { Label = label, T = now, Physics = physics, MainAlloc = MainAllocNow() });
        if (!physics)
        {
            if (_frame.FirstNode == 0) _frame.FirstNode = now;
            _frame.LastNode = now;
        }
    }

    private void OnPhysicsFrame()
    {
        ulong now = Time.GetTicksUsec();
        if (_frame.FirstPhysics == 0) _frame.FirstPhysics = now;
        _frame.PhysicsSteps++;
        _frame.Marks.Add(new MarkEntry { Label = "physics_frame_signal", T = now, Physics = true, MainAlloc = MainAllocNow() });
    }

    private void OnProcessFrame()
    {
        ulong now = Time.GetTicksUsec();
        _frame.ProcessSignal = now;
        _frame.Marks.Add(new MarkEntry { Label = "process_frame_signal", T = now, MainAlloc = MainAllocNow() });
    }

    private void OnPreDraw()
    {
        ulong now = Time.GetTicksUsec();
        _frame.PreDraw = now;
        _frame.Marks.Add(new MarkEntry { Label = "pre_draw", T = now, MainAlloc = MainAllocNow() });
    }

    private void OnPostDraw()
    {
        ulong now = Time.GetTicksUsec();
        _frame.PostDraw = now;
        _frame.Marks.Add(new MarkEntry { Label = "post_draw", T = now, MainAlloc = MainAllocNow() });
        CloseFrame(now);
        PaceGc();
    }

    private void PaceGc()
    {
        if (GcPaceGeneration < 0) return;
        _gcPaceFrameCounter++;
        long alloc = GC.GetTotalAllocatedBytes(false);
        int count = GC.CollectionCount(0);
        if (count != _gcPaceCountAtLast)
        {
            // Any GC (natural or induced) resets the budget window.
            _gcPaceCountAtLast = count;
            _gcPaceAllocAtLast = alloc;
            _gcPaceFrameCounter = 0;
            return;
        }
        bool due = (GcPaceMegabytes > 0.0 && alloc - _gcPaceAllocAtLast >= (long)(GcPaceMegabytes * 1024 * 1024))
            || (GcPaceFrames > 0 && _gcPaceFrameCounter >= GcPaceFrames);
        if (!due) return;
        GC.Collect(Math.Clamp(GcPaceGeneration, 0, 2), GCCollectionMode.Forced, blocking: true);
        GcPaceCollections++;
        _gcPaceCountAtLast = GC.CollectionCount(0);
        _gcPaceAllocAtLast = GC.GetTotalAllocatedBytes(false);
        _gcPaceFrameCounter = 0;
    }

    private void SnapshotCounters(out int[] gcDelta, out double pauseMs, out double allocMb, out long[] pipeDelta, out double texMb, out double bufMb)
    {
        gcDelta = new int[3];
        for (int g = 0; g < 3; g++)
        {
            int count = GC.CollectionCount(g);
            gcDelta[g] = count - _gc[g];
            _gc[g] = count;
        }
        TimeSpan pause = GC.GetTotalPauseDuration();
        pauseMs = (pause - _gcPause).TotalMilliseconds;
        _gcPause = pause;
        long alloc = GC.GetTotalAllocatedBytes(false);
        allocMb = (alloc - _alloc) / (1024.0 * 1024.0);
        _alloc = alloc;
        // The monitors lock the scene-shader mutex, which background shader compiles hold; reuse the last counts while warming.
        long[] pipes = RingworldTechDemo.Loading.RingStartupWarmup.IsWarming ? (long[])_pipelines.Clone() : new long[]
        {
            (long)Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface),
            (long)Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh),
            (long)Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas),
            (long)Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw),
            (long)Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization),
        };
        pipeDelta = new long[5];
        for (int i = 0; i < 5; i++) { pipeDelta[i] = pipes[i] - _pipelines[i]; _pipelines[i] = pipes[i]; }
        long tex = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed);
        long buf = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed);
        texMb = (tex - _textureMem) / (1024.0 * 1024.0);
        bufMb = (buf - _bufferMem) / (1024.0 * 1024.0);
        _textureMem = tex;
        _bufferMem = buf;
    }

    private static double Ms(ulong from, ulong to) => from == 0 || to == 0 || to < from ? 0 : (to - from) / 1000.0;

    private string ReadContext()
    {
        string stage = "";
        string warm = "";
        try
        {
            if (_loader != null && IsInstanceValid(_loader)) stage = _loader.Get("_stage").AsString() + (_loader.Get("active").AsBool() ? "" : "(idle)");
            if (_warm != null && IsInstanceValid(_warm)) warm = "warm" + _warm.Get("state").AsInt32();
        }
        catch (Exception) { }
        return $"{stage}|{warm}|{_context}";
    }

    private void CloseFrame(ulong now)
    {
        Frame f = _frame;
        f.Start = _lastPostDraw;
        double total = Ms(f.Start, now);
        ulong physicsEnd = f.ProcessSignal != 0 ? f.ProcessSignal : f.FirstNode;
        double prePhysics = Ms(f.Start, f.FirstPhysics != 0 ? f.FirstPhysics : physicsEnd);
        double physics = f.FirstPhysics != 0 ? Ms(f.FirstPhysics, physicsEnd) : 0;
        double handlers = Ms(f.ProcessSignal, f.FirstNode);
        double nodes = Ms(f.FirstNode, f.LastNode);
        double post = Ms(f.LastNode, f.PreDraw);
        double draw = Ms(f.PreDraw, f.PostDraw);
        SnapshotCounters(out int[] gc, out double pauseMs, out double allocMb, out long[] pipes, out double texMb, out double bufMb);
        string context = ReadContext();
        TimeSpan jitNow = System.Runtime.JitInfo.GetCompilationTime(true); long jitCount = System.Runtime.JitInfo.GetCompiledMethodCount(true);
        double jitMs = (jitNow - _jitTime).TotalMilliseconds; long jitMethods = jitCount - _jitMethods; _jitTime = jitNow; _jitMethods = jitCount;

        // Largest gap between consecutive marks (what ran between them).
        f.Marks.Sort((a, b) => a.T.CompareTo(b.T));
        List<(double ms, int index)> gaps = _gaps;
        gaps.Clear();
        ulong previous = f.Start;
        long previousAlloc = f.StartMainAlloc;
        string ctxKey = _context;
        for (int i = 0; i < f.Marks.Count; i++)
        {
            MarkEntry mark = f.Marks[i];
            double ms = Ms(previous, mark.T);
            gaps.Add((ms, i));
            // Owner of the gap: the marker placed after a world child closes that child's subtree; engine marks name the phase before them.
            bool engine = mark.Label.StartsWith("__") || mark.Label is "physics_frame_signal" or "process_frame_signal" or "pre_draw" or "post_draw";
            string? prevLabel = engine ? (i > 0 ? f.Marks[i - 1].Label : "prev_post_draw") : null;
            bool prevPhysics = engine && i > 0 && f.Marks[i - 1].Physics;
            long bytes = Math.Max(0, mark.MainAlloc - previousAlloc);
            var key = (ctxKey, prevLabel, prevPhysics, mark.Label, mark.Physics);
            _ownerAlloc.TryGetValue(key, out var acc);
            _ownerAlloc[key] = (acc.bytes + bytes, acc.ms + ms, acc.frames + 1);
            previous = mark.T;
            previousAlloc = mark.MainAlloc;
        }
        long frameMainAlloc = f.Marks.Count > 0 ? Math.Max(0, f.Marks[^1].MainAlloc - f.StartMainAlloc) : 0;
        gaps.Sort((a, b) => b.ms.CompareTo(a.ms));
        string GapText(int index) => (index > 0 ? (f.Marks[index - 1].Physics ? "P:" : "") + f.Marks[index - 1].Label : "prev_post_draw")
            + " -> " + (f.Marks[index].Physics ? "P:" : "") + f.Marks[index].Label;
        string topGap = gaps.Count > 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0}ms {1}", gaps[0].ms, GapText(gaps[0].index)) : "";

        double tMs = (now - _startUsec) / 1000.0;
        _csv.Append(string.Format(CultureInfo.InvariantCulture,
            "{0},{1:0.0},{2:0.00},{3:0.00},{4:0.00},{5},{6:0.00},{7:0.00},{8:0.00},{9:0.00},{10},{11},{12},{13:0.00},{14:0.00},{15},{16},{17},{18},{19},{20:0.0},{21:0.0},{24:0.0},{25},\"{22}\",\"{23}\",{26:0.000}\n",
            _frameIndex, tMs, total, prePhysics, physics, f.PhysicsSteps, handlers, nodes, post, draw, gc[0], gc[1], gc[2], pauseMs, allocMb,
            pipes[0], pipes[1], pipes[2], pipes[3], pipes[4], texMb, bufMb, context, topGap.Replace("\"", "'"), jitMs, jitMethods, frameMainAlloc / (1024.0 * 1024.0)));

        if (total >= DetailThresholdMs)
        {
            _detail.Append(string.Format(CultureInfo.InvariantCulture,
                "FRAME {0} end_usec={1} t_ms={2:0.0} total={3:0.0} | pre_physics(post_draw continuations+present)={4:0.0} physics={5:0.0} (steps {6}) signal_handlers={7:0.0} nodes={8:0.0} post_process(deferred/timers/tweens/RS sync)={9:0.0} draw={10:0.0} | gc={11}/{12}/{13} pause={14:0.0}ms alloc={15:0.0}MB | pipelines surface={16} mesh={17} canvas={18} draw={19} spec={20} | tex+{21:0.0}MB buf+{22:0.0}MB | jit={24:0.0}ms/{25} | ctx={23}\n",
                _frameIndex, now, tMs, total, prePhysics, physics, f.PhysicsSteps, handlers, nodes, post, draw, gc[0], gc[1], gc[2], pauseMs, allocMb,
                pipes[0], pipes[1], pipes[2], pipes[3], pipes[4], texMb, bufMb, context, jitMs, jitMethods));
            int shown = 0;
            foreach (var gap in gaps)
            {
                if (gap.ms < 2.0 || shown >= 10) break;
                _detail.Append(string.Format(CultureInfo.InvariantCulture, "    {0,8:0.0} ms  {1}\n", gap.ms, GapText(gap.index)));
                shown++;
            }
            // Console printing costs ~1 ms per line on the main thread; cap it per context so hitch-heavy windows (flights) are not inflated.
            if (!ReferenceEquals(_printContext, _context)) { _printContext = _context; _printedInContext = 0; }
            if (_printedInContext++ < MaxPrintsPerContext)
                GD.Print(string.Format(CultureInfo.InvariantCulture, "STALL_PROBE_FRAME t_ms={0:0} total={1:0.0} top={2} ctx={3}", tMs, total, topGap, context));
        }

        _frameIndex++;
        _lastPostDraw = now;
        // Swap in the cleared spare frame (no per-frame List allocation).
        Frame next = _spareFrame;
        next.Reset();
        _spareFrame = f;
        _frame = next;
        // Everything allocated since the post_draw mark is the probe's own bookkeeping; keep it out of the next frame's first gap.
        long closeEnd = MainAllocNow();
        if (f.Marks.Count > 0) _probeSelfBytes += Math.Max(0, closeEnd - f.Marks[^1].MainAlloc);
        _frame.StartMainAlloc = closeEnd;
    }

    /// <summary>Writes stall_frames.csv (every frame) and stall_detail.txt (frames over the threshold) to <paramref name="directory"/>.</summary>
    public void WriteReport(string directory)
    {
        StopVegetationRenderTrace();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stall_frames.csv"), _csv.ToString());
        File.WriteAllText(Path.Combine(directory, "stall_detail.txt"), _detail.ToString());
        var owners = new StringBuilder("context,owner,frames,main_alloc_mb,ms\n");
        foreach (var pair in _ownerAlloc)
        {
            var k = pair.Key;
            string target = (k.physics ? "P:" : "") + k.label;
            string owner = k.prev == null ? target : (k.prevPhysics ? "P:" : "") + k.prev + "->" + target;
            owners.Append(string.Format(CultureInfo.InvariantCulture, "\"{0}\",\"{1}\",{2},{3:0.000},{4:0.0}\n",
                k.ctx.Replace("\"", "'"), owner.Replace("\"", "'"), pair.Value.frames, pair.Value.bytes / (1024.0 * 1024.0), pair.Value.ms));
        }
        owners.Append(string.Format(CultureInfo.InvariantCulture, "\"(all)\",\"probe_self\",{0},{1:0.000},0\n", _frameIndex, _probeSelfBytes / (1024.0 * 1024.0)));
        File.WriteAllText(Path.Combine(directory, "alloc_owners.csv"), owners.ToString());
        WriteVegetationRenderTrace(directory);
    }

    /// <summary>Close trace admission and wait for every admitted callback to record/release. Await Complete before writing a complete report.</summary>
    public void StopVegetationRenderTrace()
    {
        RingVegetationWorkTrace.Capture? capture = _vegetationTrace;
        if (capture == null || _vegetationTraceStopRequested) return;
        _vegetationTraceStopRequested = true;
        RingVegetationWorkTrace.Stop(capture);
    }

    private void WriteVegetationRenderTrace(string directory)
    {
        RingVegetationWorkTrace.Capture? capture = _vegetationTrace;
        if (capture == null) return;
        int count = capture.Count;
        double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        long newBatches = 0, newBytes = 0, retiredBatches = 0, retiredMirrors = 0;
        double totalMs = 0, ensureMs = 0, retireMs = 0, maxMs = 0;
        double outermostMs = 0;
        int outermostCount = 0;
        using (var csv = new StreamWriter(Path.Combine(directory, "vegetation_render_work.csv")))
        {
            csv.WriteLine("request_process_frame,request_usec,callback_start_usec,callback_end_usec,callback_start_t_ms,queue_wait_ms,instance,channel,callback_depth,new_batches,new_estimated_bytes,ensure_batch_ms,retired_batches,retired_mirrors,retire_ms,other_ms,total_ms,failed");
            for (int i = 0; i < count; i++)
            {
                RingVegetationWorkTrace.Sample row = capture.Samples[i];
                double total = row.TotalTicks * tickMs, ensure = row.EnsureTicks * tickMs, retire = row.RetireTicks * tickMs;
                double other = Math.Max(0, total - ensure - retire);
                csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{row.RequestFrame},{row.RequestUsec},{row.StartUsec},{row.EndUsec},{((long)row.StartUsec - (long)_startUsec) / 1000.0:0.000},{((long)row.StartUsec - (long)row.RequestUsec) / 1000.0:0.000},{row.Instance},{row.Channel},{row.CallbackDepth},{row.NewlyAllocated},{row.AllocatedBytes},{ensure:0.000},{row.RetiredBatches},{row.RetiredMirrors},{retire:0.000},{other:0.000},{total:0.000},{(row.Failed ? 1 : 0)}"));
                newBatches += row.NewlyAllocated; newBytes += row.AllocatedBytes;
                retiredBatches += row.RetiredBatches; retiredMirrors += row.RetiredMirrors;
                totalMs += total; ensureMs += ensure; retireMs += retire; maxMs = Math.Max(maxMs, total);
                if (row.CallbackDepth == 1) { outermostMs += total; outermostCount++; }
            }
        }
        var summary = new {
            samples = count, capacity = capture.Samples.Length, overflow = capture.Overflow, complete = capture.Complete,
            probe_start_usec = _startUsec, stopwatch_frequency = System.Diagnostics.Stopwatch.Frequency,
            new_batches = newBatches, new_estimated_bytes = newBytes, retired_batches = retiredBatches, retired_mirrors = retiredMirrors,
            callback_total_ms = totalMs, callback_max_ms = maxMs, ensure_batch_ms = ensureMs, retire_ms = retireMs,
            outermost_total_ms = outermostMs, outermost_callbacks = outermostCount,
            other_ms = Math.Max(0, totalMs - ensureMs - retireMs),
            channels = new { none = -1, grass = 0, trees = 1 },
            note = "Inclusive CPU wall time of render callbacks, not GPU execution; inline nested callbacks may overlap, so callback totals are not exclusive. Request fields identify callback enqueue, not the latest coalesced FrameRequest or the draw frame. Byte estimates cover newly allocated batch/shared-source buffers, not driver memory. Other includes initialization, mirrors, metadata, haze/shadow resources, dispatch recording and scheduling. Incomplete reports may omit an in-flight callback."
        };
        File.WriteAllText(Path.Combine(directory, "vegetation_render_summary.json"), System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public void End()
    {
        StopVegetationRenderTrace();
        if (!_started) return;
        _started = false;
        SceneTree tree = GetTree();
        tree.PhysicsFrame -= OnPhysicsFrame;
        tree.ProcessFrame -= OnProcessFrame;
        RenderingServer.FramePreDraw -= OnPreDraw;
        RenderingServer.FramePostDraw -= OnPostDraw;
        foreach (StallProbeMarker marker in _markers)
            if (IsInstanceValid(marker)) marker.QueueFree();
        _markers.Clear();
    }
}

/// <summary>Inert marker: records a timestamp when the SceneTree reaches it in the process and physics lists.</summary>
public partial class StallProbeMarker : Node
{
    public WarmStallProbe? Probe { get; set; }
    public string Label { get; set; } = "";

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    public override void _Process(double delta) => Probe?.Mark(Label, false);

    public override void _PhysicsProcess(double delta) => Probe?.Mark(Label, true);
}
