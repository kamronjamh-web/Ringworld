using System;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Underground;

/// <summary>
/// Single owner of the underground-mode signal. Detects enclosure from the active terrain backend and publishes
/// Mode, EnclosureBlend, Openness, DepthMeters and SuspensionActive. Read-only in this phase: it suspends nothing.
/// </summary>
public partial class RingUndergroundModeController : Node
{
    public const float HeadHeightMeters = 1.55f;
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath Terrain3DGameplayPath { get; set; } = new("../Terrain3DGameplay");
    [Export(PropertyHint.Range, "16,2048,1")] public float ProbeDistanceMeters { get; set; } = 512f;
    [Export(PropertyHint.Range, "8,2048,1")] public int ProbeSampleBudgetPerFrame { get; set; } = 24;
    [Export(PropertyHint.Range, "1,100000,1")] public float FastTravelSpeedThresholdMetersPerSecond { get; set; } = 750f;
    [Export(PropertyHint.Range, "1,100000,1")] public float TeleportDistanceMeters { get; set; } = 64f;

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private IUndergroundProbeSource? _source;
    private readonly UndergroundEnclosureProbe _probe = new();
    private readonly UndergroundEnclosureStateMachine _machine = new();
    private RingCanonicalPosition _previousPosition;
    private bool _hasPreviousPosition;
    private string _forcedExitReason = "";

    /// <summary>Master toggle. Off behaves as permanent forced exit.</summary>
    public bool Enabled { get; set; } = true;
    public UndergroundModeOverride ModeOverride { get; set; } = UndergroundModeOverride.Auto;
    public UndergroundEnclosureParameters Parameters { get => _machine.Parameters; set => _machine.Parameters = value; }
    public UndergroundMode Mode => _machine.Mode;
    public float EnclosureBlend => _machine.EnclosureBlend;
    public float Openness => _machine.Openness;
    public float DepthMeters => _machine.DepthMeters;
    public bool SuspensionActive => _machine.SuspensionActive;
    public bool IsAvailable { get; private set; }
    public string ForcedExitReason => _forcedExitReason;
    public int LastProbeSamples { get; private set; }
    public double LastProbeMilliseconds { get; private set; }
    public double MaxProbeMilliseconds { get; private set; }
    public UndergroundEnclosureProbe Probe => _probe;

    public event Action<UndergroundMode, UndergroundMode>? ModeChanged;

    public override void _Ready()
    {
        // Publish before the underground lighting controller (-100) and the atmosphere compose.
        ProcessPriority = -110;
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _source = UndergroundProbeSourceFactory.Create(this, SmoothTerrainPath, GridManagerPath, Terrain3DGameplayPath);
        _machine.ModeChanged += (previous, next) => ModeChanged?.Invoke(previous, next);
    }

    public override void _Process(double delta)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Underground mode update (inclusive)");
        _probe.MaxDistanceMeters = ProbeDistanceMeters;
        _probe.SampleBudgetPerFrame = ProbeSampleBudgetPerFrame;
        IsAvailable = _explorer != null && _source != null && _source.IsAvailable;
        string reason = "";
        float depth = 0f;
        if (!IsAvailable) reason = "unavailable";
        else
        {
            RingCanonicalPosition position = _explorer!.CanonicalPosition;
            double headVertical = position.VerticalMeters + HeadHeightMeters;
            reason = ForcedExitReasonFor(position, delta);
            _previousPosition = position;
            _hasPreviousPosition = true;
            if (_source!.TryGetSurfaceHeight(position.AroundMeters, position.AcrossMeters, out float surface))
                depth = (float)(surface - headVertical);
            else if (reason.Length == 0) reason = "unavailable";
            // At or above the exit depth the machine exits (or never enters) regardless of
            // openness, so skip the probe there; near the surface is where rays march farthest.
            if (reason.Length == 0 && depth > _machine.Parameters.ExitDepthMeters)
            {
                var timer = Stopwatch.StartNew();
                LastProbeSamples = _probe.Update(_source, position.AroundMeters, headVertical, position.AcrossMeters);
                LastProbeMilliseconds = timer.Elapsed.TotalMilliseconds;
                MaxProbeMilliseconds = Math.Max(MaxProbeMilliseconds, LastProbeMilliseconds);
                if (_probe.SourceFailed) reason = "unavailable";
            }
        }
        if (reason.Length > 0) { LastProbeSamples = 0; LastProbeMilliseconds = 0; }
        _forcedExitReason = reason;
        _machine.Step(delta, new UndergroundEnclosureInput(depth, _probe.Openness, reason.Length > 0, ModeOverride));
    }

    private string ForcedExitReasonFor(in RingCanonicalPosition position, double delta)
    {
        if (!Enabled) return "disabled";
        if (_explorer!.IsSpawnTerrainSettling) return "spawn settle";
        if (_explorer.FastTravelSpeedMetersPerSecond > FastTravelSpeedThresholdMetersPerSecond) return "fast travel";
        if (_hasPreviousPosition)
        {
            double dAround = Math.Abs(position.AroundMeters - _previousPosition.AroundMeters);
            double circumference = _grid?.GetCanonicalRingCircumferenceMeters64() ?? 0;
            if (circumference > 0) dAround = Math.Min(dAround, Math.Abs(circumference - dAround));
            double dx = position.VerticalMeters - _previousPosition.VerticalMeters, dz = position.AcrossMeters - _previousPosition.AcrossMeters;
            if (Math.Sqrt(dAround * dAround + dx * dx + dz * dz) > TeleportDistanceMeters) return "teleport";
        }
        return "";
    }

    /// <summary>One-line live status for the tuning drawer.</summary>
    public string DescribeStatus()
    {
        if (!IsAvailable) return "Underground mode: unavailable (no terrain probe source).";
        string forced = _forcedExitReason.Length > 0 ? $" (forced surface: {_forcedExitReason})" : "";
        return $"Mode {Mode}{forced}  blend {EnclosureBlend:0.00}  depth {DepthMeters:0.#} m  openness {Openness:0.00}  suspension {(SuspensionActive ? "on" : "off")}  probe {LastProbeSamples} samples {LastProbeMilliseconds:0.000} ms";
    }
}
