using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Underground;

/// <summary>
/// Single owner of the underground-mode signal. Detects enclosure from the active terrain backend and publishes
/// Mode, EnclosureBlend, Openness, DepthMeters and SuspensionActive, and drives the registered surface-system participants.
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
    [Export] public NodePath AtmospherePath { get; set; } = new("../DaySideAtmosphere");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private IUndergroundProbeSource? _source;
    private readonly UndergroundEnclosureProbe _probe = new();
    private readonly UndergroundEnclosureStateMachine _machine = new();
    private RingCanonicalPosition _previousPosition;
    private bool _hasPreviousPosition;
    private string _forcedExitReason = "";
    private readonly List<UndergroundParticipant> _participants = new();
    private Node? _atmosphere;
    private DirectionalLight3D? _sun;
    private bool _participantsBound;
    private int _rebindCountdown;
    private bool _lightingDarkened;
    private bool _hideActive;
    private ICaveLodProfileTarget? _caveTarget;
    private readonly CaveLodProfilePolicy _caveProfilePolicy = new();
    private CaveLodProfile? _appliedCaveOverride;
    private int? _appliedRetainMargin;
    private float? _appliedHysteresis;
    private CaveLodProfileStatus _caveStatus;
    private bool _hasCaveStatus;
    private double _caveStatusElapsed;
    private readonly UndergroundFogCoupling _fog = new();
    private float _coarseFilledReach;

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
    /// <summary>Groups whose participants may suspend. A disabled group always receives the all-clear state.</summary>
    public UndergroundGroups EnabledGroups { get; set; } = UndergroundGroups.All;
    /// <summary>
    /// Groups whose participants the presentation suspends (the start menu parks the camera kilometres up) whatever the enclosure state. The switchboard
    /// rewrites every participant's state each frame, so this lives here: it is OR-ed with the underground suspend and is not filtered by
    /// <see cref="EnabledGroups"/> (that toggle is about underground mode only). Hide and shadow tiers are never presentation-driven.
    /// </summary>
    public UndergroundGroups PresentationSuspendGroups { get; set; } = UndergroundGroups.None;
    /// <summary>0..1 weight of the enclosure blend on direct sun and sky ambient in designed caves (decision D1).</summary>
    public float EnclosureLightingWeight { get; set; } = 1f;
    /// <summary>Faint cave ambient (decision D2), blended by EnclosureBlend and read by the atmosphere.</summary>
    public Color CaveAmbientColor { get; set; } = new(.16f, .20f, .30f, 1f);
    public float CaveAmbientEnergy { get; set; } = .035f;
    public Color CaveFogColor { get; set; } = new(.05f, .07f, .11f, 1f);
    /// <summary>Depth at which a fully sealed player may have far terrain hidden instead of only frozen (decision D3).</summary>
    public float DeepHideDepthMeters { get; set; } = 128f;
    /// <summary>True once sun and sky ambient were fully darkened as of the previous frame. Hide and shadow suspension require it.</summary>
    public bool LightingDarkened => _lightingDarkened;
    /// <summary>True while the hide tier (surface LOD proxies, global terrain, far viewport) is applied.</summary>
    public bool HideTierActive => _hideActive;
    /// <summary>Frames where a hide was applied without darkened lighting. Must stay zero.</summary>
    public int HideOrderingViolations { get; private set; }
    public IReadOnlyList<UndergroundParticipant> Participants => _participants;
    /// <summary>Cave LOD profile applied to the terrain backend while underground (and for the shrink delay after).</summary>
    public CaveLodProfile UndergroundCaveProfile { get; set; } = CaveLodProfile.UndergroundDefault;
    public int CoarseRetainMarginSectors { get; set; } = 1;
    public float CaveCoarseActivationHysteresisMeters { get; set; } = 64f;
    /// <summary>Seconds the cave profile is kept after returning to the surface.</summary>
    public float CaveProfileShrinkDelaySeconds { get; set; } = 10f;
    /// <summary>True while the cave profile is applied (underground or inside the shrink delay).</summary>
    public bool CaveLodProfileActive => _caveProfilePolicy.Active;
    public double CaveProfileSurfaceSeconds => _caveProfilePolicy.SurfaceSeconds;
    /// <summary>True while the terrain backend's coarse underground tier is active.</summary>
    public bool CaveLodCoarseActive { get; private set; }
    /// <summary>Distance in every ring direction the coarse tier covers at the smaller of its two horizontal radii; 0 without a terrain target.</summary>
    public float GuaranteedCoarseReachMeters { get; private set; }
    /// <summary>Fades the cave fog density to the coarse tier's filled reach. Deep caves only (needs the coarse latch).</summary>
    public bool CoupleFogToCaveDistance { get; set; } = true;
    /// <summary>Fraction of the coarse reach at which the fog is nearly opaque.</summary>
    public float FogEndFraction { get; set; } = .9f;
    /// <summary>Fog reach used while the coarse latch is on but the coarse ring is not yet filled (the 32 m bridge tier guarantees 1024 m).</summary>
    public float UnfilledCaveFogReachMeters { get; set; } = 1024f;
    /// <summary>0..1 smoothed weight the atmosphere multiplies its cave fog by; 0 outside the coarse underground latch.</summary>
    public float CaveFogWeight => _fog.CouplingWeight;
    public float CaveFogReachMeters => _fog.ReachMeters;
    public float CaveFogVisibleDistanceMeters => _fog.VisibleDistanceMeters;
    public float CaveFogDensity => _fog.Density;

    public event Action<UndergroundMode, UndergroundMode>? ModeChanged;

    public override void _Ready()
    {
        // Publish before the underground lighting controller (-100) and the atmosphere compose.
        ProcessPriority = -110;
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _source = UndergroundProbeSourceFactory.Create(this, SmoothTerrainPath, GridManagerPath, Terrain3DGameplayPath);
        _caveTarget = UndergroundProbeSourceFactory.CreateCaveLodTarget(this, SmoothTerrainPath);
        _machine.ModeChanged += (previous, next) => ModeChanged?.Invoke(previous, next);
        CallDeferred(nameof(BindParticipants));
        if (Array.Exists(OS.GetCmdlineUserArgs(), a => a.Equals("--underground-mode-roundtrip-smoke-test", StringComparison.OrdinalIgnoreCase)))
            GetParent().CallDeferred(Node.MethodName.AddChild, new UndergroundModeRoundTripSmoke { Name = "UndergroundModeRoundTripSmoke" });
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
        ApplyParticipants();
        ApplyCaveLodProfile(delta);
        UpdateCaveFog(delta);
    }


    public override void _ExitTree()
    {
        if (HasValidCaveTarget()) _caveTarget!.SetCaveProfileOverride(null);
        // Leave every surface system running if this controller goes away.
        foreach (UndergroundParticipant participant in _participants)
            if (participant.Present) participant.Apply(UndergroundParticipantState.Clear);
    }

    private void BindParticipants()
    {
        Node? root = GetParent();
        if (root == null) return;
        if (!_participantsBound)
        {
            _participants.AddRange(UndergroundParticipantFactory.CreateDefaults());
            _participantsBound = true;
        }
        foreach (UndergroundParticipant participant in _participants)
            if (!participant.Present) participant.Bind(root);
        _atmosphere = GetNodeOrNull(AtmospherePath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (!HasValidCaveTarget()) _caveTarget = UndergroundProbeSourceFactory.CreateCaveLodTarget(this, SmoothTerrainPath);
    }

    private bool HasValidCaveTarget() => _caveTarget is GodotObject target && GodotObject.IsInstanceValid(target);

    /// <summary>
    /// Applies the cave LOD profile to the terrain backend. The policy decides when it is active (immediately on
    /// entering, after a shrink delay on leaving, never while the master toggle is off); the backend only
    /// stores it and its own change detection does the work. The target is only touched when a value changes.
    /// </summary>
    private void ApplyCaveLodProfile(double delta)
    {
        CaveLodProfile? wanted = _caveProfilePolicy.Step(delta, Mode, !Enabled, UndergroundCaveProfile, CaveProfileShrinkDelaySeconds);
        if (!HasValidCaveTarget())
        {
            _caveTarget = null;
            _appliedCaveOverride = null;
            _appliedRetainMargin = null;
            _appliedHysteresis = null;
            _hasCaveStatus = false;
            CaveLodCoarseActive = false;
            GuaranteedCoarseReachMeters = 0f;
            _coarseFilledReach = 0f;
            return;
        }
        if (wanted != _appliedCaveOverride) { _caveTarget!.SetCaveProfileOverride(wanted); _appliedCaveOverride = wanted; }
        if (_appliedRetainMargin != CoarseRetainMarginSectors) { _caveTarget!.CoarseRetainMarginSectors = CoarseRetainMarginSectors; _appliedRetainMargin = CoarseRetainMarginSectors; }
        if (_appliedHysteresis != CaveCoarseActivationHysteresisMeters) { _caveTarget!.CoarseActivationHysteresisMeters = CaveCoarseActivationHysteresisMeters; _appliedHysteresis = CaveCoarseActivationHysteresisMeters; }
        // The telemetry walk touches every resident entry, so refresh it at the drawer's rate rather than every frame.
        if (!_hasCaveStatus || (_caveStatusElapsed += delta) >= .25)
        {
            _caveStatusElapsed = 0;
            _caveStatus = _caveTarget!.CaptureCaveLodProfileStatus();
            _hasCaveStatus = true;
            CaveLodCoarseActive = _caveStatus.CoarseActive;
            GuaranteedCoarseReachMeters = Math.Min(_caveStatus.EffectiveRadiusAround, _caveStatus.EffectiveRadiusAcross) * 2048f;
            _coarseFilledReach = UndergroundFogCoupling.NextCoarseFilledReach(_coarseFilledReach, _caveStatus.CoarseActive,
                _caveStatus.CoarseDesired, _caveStatus.CoarseDesiredReady, GuaranteedCoarseReachMeters);
        }
    }

    /// <summary>Publishes the cave fog density and weight. Release depth mirrors the terrain's coarse latch exit.</summary>
    private void UpdateCaveFog(double delta)
    {
        bool enabled = CoupleFogToCaveDistance && Enabled && IsAvailable && HasValidCaveTarget();
        float releaseDepth = 0f;
        if (enabled) releaseDepth = Math.Max(0f, _caveTarget!.CoarseUndergroundActivationDepthMeters - Math.Max(0f, CaveCoarseActivationHysteresisMeters));
        _fog.Step(delta, enabled, CaveLodCoarseActive, DepthMeters, releaseDepth, _coarseFilledReach, UnfilledCaveFogReachMeters, FogEndFraction);
    }

    /// <summary>
    /// Drives every participant from the published state. Runs in the same frame the state changes, so a forced
    /// exit restores everything immediately. Hide and shadow suspension additionally wait for confirmed darkness:
    /// the atmosphere publishes its applied sun/sky darkening each frame and this controller runs before it, so
    /// the value read here is always from a frame that has already been fully darkened.
    /// </summary>
    private void ApplyParticipants()
    {
        if (!_participantsBound) return;
        if (--_rebindCountdown <= 0)
        {
            _rebindCountdown = 120;
            BindParticipants();
        }
        _lightingDarkened = ReadLightingDarkened();
        bool suspension = SuspensionActive;
        bool hideEligible = suspension && _lightingDarkened
            && (ModeOverride == UndergroundModeOverride.ForceUnderground || (DepthMeters >= DeepHideDepthMeters && Openness <= 0f));
        bool anyHide = false;
        foreach (UndergroundParticipant participant in _participants)
        {
            if (!participant.Present) continue;
            bool enabled = (EnabledGroups & participant.Group) != 0;
            bool suspend = (enabled && suspension) || (PresentationSuspendGroups & participant.Group) != 0;
            bool hide = enabled && hideEligible;
            anyHide |= hide && participant.Group == UndergroundGroups.FarTerrain;
            participant.Apply(new UndergroundParticipantState(suspend, hide, suspend && _lightingDarkened, enabled ? EnclosureBlend : 0f));
        }
        if (anyHide && !_lightingDarkened) HideOrderingViolations++;
        _hideActive = anyHide;
    }

    private bool ReadLightingDarkened()
    {
        if (EnclosureLightingWeight < .999f || _atmosphere == null || !GodotObject.IsInstanceValid(_atmosphere)) return false;
        Variant darkness = _atmosphere.Get("underground_lighting_darkness");
        if (darkness.VariantType != Variant.Type.Float || darkness.AsSingle() < .999f) return false;
        return _sun == null || !GodotObject.IsInstanceValid(_sun) || _sun.LightEnergy <= .01f;
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
        string tier = SuspensionActive ? (_hideActive ? "freeze+hide" : "freeze") : "off";
        return $"Mode {Mode}{forced}  blend {EnclosureBlend:0.00}  depth {DepthMeters:0.#} m  openness {Openness:0.00}  suspension {tier}  dark {(_lightingDarkened ? "yes" : "no")}  participants {_participants.Count(p => p.Present)}/{_participants.Count}  probe {LastProbeSamples} samples {LastProbeMilliseconds:0.000} ms{DescribeCaveLod()}  fog {CaveFogWeight:0.00} {CaveFogVisibleDistanceMeters:0} m";
    }

    private string DescribeCaveLod()
    {
        if (!_hasCaveStatus) return "";
        CaveLodProfileStatus s = _caveStatus;
        string phase = _caveProfilePolicy.Active ? "underground" : "surface";
        string shrink = Mode == UndergroundMode.Surface && _caveProfilePolicy.Active
            ? $" shrink in {Math.Max(0.0, CaveProfileShrinkDelaySeconds - _caveProfilePolicy.SurfaceSeconds):0.#}s" : "";
        return $"  caveLOD {phase}{shrink} r{s.EffectiveRadiusAround}/{s.EffectiveRadiusAcross}/{s.EffectiveRadiusVertical} coarse desired/ready/visible/resident {s.CoarseDesired}/{s.CoarseReady}/{s.CoarseVisible}/{s.CoarseResident} workers {s.EffectiveWorkers} cave bytes {s.TotalEstimatedBytes / 1048576.0:0.0} MB reach {GuaranteedCoarseReachMeters:0} m";
    }
}
