using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Godot;

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

    private sealed class MarkEntry { public string Label = ""; public ulong T; public bool Physics; }

    private sealed class Frame
    {
        public ulong Start, FirstPhysics, ProcessSignal, FirstNode, LastNode, PreDraw, PostDraw;
        public int PhysicsSteps;
        public readonly List<MarkEntry> Marks = new();
    }

    private Frame _frame = new();
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

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public ulong MainThreadOsId { get; private set; }

    /// <summary>Begins recording. Call once from the driver after the probe is in the tree.</summary>
    public void Begin()
    {
        if (_started) return;
        _started = true;
        MainThreadOsId = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
        _startUsec = Time.GetTicksUsec();
        _lastPostDraw = _startUsec;
        _frame.Start = _startUsec;
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
        _csv.Append("frame,t_ms,total_ms,pre_physics_ms,physics_ms,physics_steps,signal_handlers_ms,nodes_ms,post_process_ms,draw_ms,gc0,gc1,gc2,gc_pause_ms,alloc_mb,pipe_surface,pipe_mesh,pipe_canvas,pipe_draw,pipe_spec,tex_mem_mb_delta,buf_mem_mb_delta,jit_ms,jit_methods,context,top_gap\n");
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
        _frame.Marks.Add(new MarkEntry { Label = label, T = now, Physics = physics });
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
        _frame.Marks.Add(new MarkEntry { Label = "physics_frame_signal", T = now, Physics = true });
    }

    private void OnProcessFrame()
    {
        ulong now = Time.GetTicksUsec();
        _frame.ProcessSignal = now;
        _frame.Marks.Add(new MarkEntry { Label = "process_frame_signal", T = now });
    }

    private void OnPreDraw()
    {
        ulong now = Time.GetTicksUsec();
        _frame.PreDraw = now;
        _frame.Marks.Add(new MarkEntry { Label = "pre_draw", T = now });
    }

    private void OnPostDraw()
    {
        ulong now = Time.GetTicksUsec();
        _frame.PostDraw = now;
        _frame.Marks.Add(new MarkEntry { Label = "post_draw", T = now });
        CloseFrame(now);
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
        var gaps = new List<(double ms, string what)>();
        ulong previous = f.Start;
        string previousLabel = "prev_post_draw";
        foreach (MarkEntry mark in f.Marks)
        {
            double ms = Ms(previous, mark.T);
            gaps.Add((ms, $"{previousLabel} -> {(mark.Physics ? "P:" : "")}{mark.Label}"));
            previous = mark.T;
            previousLabel = (mark.Physics ? "P:" : "") + mark.Label;
        }
        gaps.Sort((a, b) => b.ms.CompareTo(a.ms));
        string topGap = gaps.Count > 0 ? $"{gaps[0].ms:0.0}ms {gaps[0].what}" : "";

        double tMs = (now - _startUsec) / 1000.0;
        _csv.Append(string.Format(CultureInfo.InvariantCulture,
            "{0},{1:0.0},{2:0.00},{3:0.00},{4:0.00},{5},{6:0.00},{7:0.00},{8:0.00},{9:0.00},{10},{11},{12},{13:0.00},{14:0.00},{15},{16},{17},{18},{19},{20:0.0},{21:0.0},{24:0.0},{25},\"{22}\",\"{23}\"\n",
            _frameIndex, tMs, total, prePhysics, physics, f.PhysicsSteps, handlers, nodes, post, draw, gc[0], gc[1], gc[2], pauseMs, allocMb,
            pipes[0], pipes[1], pipes[2], pipes[3], pipes[4], texMb, bufMb, context, topGap.Replace("\"", "'"), jitMs, jitMethods));

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
                _detail.Append(string.Format(CultureInfo.InvariantCulture, "    {0,8:0.0} ms  {1}\n", gap.ms, gap.what));
                shown++;
            }
            GD.Print(string.Format(CultureInfo.InvariantCulture, "STALL_PROBE_FRAME t_ms={0:0} total={1:0.0} top={2} ctx={3}", tMs, total, topGap, context));
        }

        _frameIndex++;
        _lastPostDraw = now;
        _frame = new Frame();
    }

    /// <summary>Writes stall_frames.csv (every frame) and stall_detail.txt (frames over the threshold) to <paramref name="directory"/>.</summary>
    public void WriteReport(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stall_frames.csv"), _csv.ToString());
        File.WriteAllText(Path.Combine(directory, "stall_detail.txt"), _detail.ToString());
    }

    public void End()
    {
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
