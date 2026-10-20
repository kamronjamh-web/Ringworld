using System;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Poi;

public enum WeatherLungSunMode { Auto, Day, Night, Manual }

/// <summary>
/// Drives the Weather Lung's breathing station. Attached to the root of WeatherLungPOI.tscn (FarProxy, Markers, DetailMount); the Detail
/// scene is attached and detached by <see cref="AttachDetail"/> / <see cref="DetachDetail"/> (the streamer, or the test scene).
///
/// Phase is <c>frac((clock - t0) / cycle + phi0)</c>; the clock is the saved weather clock when a cloud layer is bound, else a local
/// accumulator, and retiming keeps the phase continuous. Poses are absolute and a pure function of phase and sun (see
/// <see cref="WeatherLungRig"/>), so throttled or skipped frames resume exactly. No weather or cloud state is written.
/// </summary>
public partial class WeatherLungController : Node3D, IWeatherLungWeatherSource
{
    private const float SunTauSeconds = 3.0f;
    private const double SunRefreshSeconds = 0.25;
    private static readonly StringName SolarVisibilityName = "current_solar_visibility";

    // --- Exported settings (S6 adds the grave-key menu and persistence for these) ---
    [Export] public bool Enabled { get => _enabled; set { if (_enabled == value) return; _enabled = value; ApplyEnabled(); } }
    [Export] public bool AnimationEnabled { get; set; } = true;
    /// <summary>Holds the current phase; <see cref="ScrubTo"/> sets it. Unfreezing continues from the held phase.</summary>
    [Export] public bool FreezePhase { get => _frozen; set => SetFrozen(value); }
    [Export(PropertyHint.Range, "30,1800,1")] public double CycleSeconds { get => _cycle; set => Retime(Math.Clamp(value, 30.0, 1800.0)); }
    [Export(PropertyHint.Range, "0,35,0.1")] public double VaneAmplitudeDeg { get; set; } = WeatherLungBreathMath.DefaultVaneAmplitudeDeg;
    [Export(PropertyHint.Range, "0,35,0.1")] public double GillAmplitudeDeg { get; set; } = WeatherLungBreathMath.DefaultGillAmplitudeDeg;
    [Export(PropertyHint.Range, "0,6,0.05")] public double TurbineMaxRpm { get; set; } = WeatherLungBreathMath.DefaultTurbineMaxRpm;
    [Export(PropertyHint.Range, "0,0.05,0.001")] public double RippleLag { get; set; } = WeatherLungBreathMath.DefaultRippleLag;
    [Export(PropertyHint.Range, "0,4,0.01")] public float NozzleEmissionScale { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float HeatGridEmissionScale { get; set; } = 1.0f;
    [Export] public WeatherLungSunMode SunMode { get; set; } = WeatherLungSunMode.Auto;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SunManual { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "1000,20000,100")] public float FullRateMeters { get; set; } = 8000f;
    [Export(PropertyHint.Range, "8000,30000,100")] public float ReducedRateMeters { get; set; } = 28000f;
    [Export(PropertyHint.Range, "1,60,1")] public float ReducedRateHz { get; set; } = 10f;
    /// <summary>Local-clock speed multiplier (test scene only; ignored while a weather clock is bound).</summary>
    [Export(PropertyHint.Range, "0,600,0.1")] public double TimeScale { get; set; } = 1.0;

    private bool _enabled = true;
    private bool _frozen;
    private double _frozenPhase;
    private double _cycle = WeatherLungBreathMath.DefaultCycleSeconds;
    private double _t0, _phi0;
    private double _clock;
    private double _localClock;

    private readonly WeatherLungRig _rig = new();
    private Node3D? _detail;
    private Node3D? _detailMount;
    private MeshInstance3D? _farProxy;
    private Marker3D? _plumeNozzleMarker, _plumeCrestMarker;

    private DirectionalLight3D? _star;
    private Node? _daySide;
    private EclipseEventDirector? _eclipse;
    private RingVolumetricCloudLayer? _clouds;
    private float _sunTarget = 1f, _sunSmoothed = 1f;
    private bool _sunInitialised;
    private double _sunRefreshAt;

    private double _phase;
    private double _reducedAccumulator;
    private string _tier = "idle";
    private int _updateCount;

    private bool _holdProxyVisible;
    private float _detailRange = 28000f, _dressingRange = 2000f, _dressingFade = 250f, _farProxyMaxKm = 700f;
    private MeshInstance3D[] _dressingTiles = Array.Empty<MeshInstance3D>();

    private ulong _identity;
    private int _revision;
    private double _centreAround, _centreAcross;

    public event Action? Changed;

    // --- IWeatherLungWeatherSource ---
    public bool IsPresent => _enabled && IsInsideTree();
    public ulong Identity => _identity;
    public int GeometryRevision => _revision;
    public double CentreAround => _centreAround;
    public double CentreAcross => _centreAcross;
    public Vector2 BasinRadii => new(14000f, 9000f);
    public double BreathPhase => _phase;
    public double InhaleLevel => WeatherLungBreathMath.InhaleLevel(_phase);
    public double ExhaleEnvelope => WeatherLungBreathMath.ExhaleEnvelope(_phase);
    public float SunAvailable => _sunSmoothed;

    /// <summary>Current breath phase (callable from GDScript, where non-exported C# properties are not visible).</summary>
    public double GetPhase() => _phase;

    public bool IsDetailAttached => _detail != null && _rig.IsBound;
    public WeatherLungRig Rig => _rig;
    public string UpdateTier => _tier;
    public int UpdateCount => _updateCount;

    public override void _Ready()
    {
        _detailMount = GetNodeOrNull<Node3D>("DetailMount");
        _farProxy = GetNodeOrNull<MeshInstance3D>("FarProxy");
        _plumeNozzleMarker = GetNodeOrNull<Marker3D>("Markers/PlumeNozzle");
        _plumeCrestMarker = GetNodeOrNull<Marker3D>("Markers/PlumeCrest");
        Visible = _enabled;
        SetProcess(true);
    }

    /// <summary>Called by the streamer when the world frame or placement changes.</summary>
    public void ConfigurePlacement(ulong identity, double centreAround, double centreAcross)
    {
        _identity = identity;
        _centreAround = centreAround;
        _centreAcross = centreAcross;
        _revision++;
        Changed?.Invoke();
    }

    /// <summary>Instances the Detail scene under DetailMount, makes it visible only while the FarProxy is hidden (HLOD) and binds the rig.</summary>
    public bool AttachDetail(Node3D detail)
    {
        foreach (string _ in AttachDetailStaged(detail)) { }
        return _detail == detail;
    }

    /// <summary>Why the last staged attach failed (empty when it succeeded).</summary>
    public string LastAttachError { get; private set; } = string.Empty;

    /// <summary>
    /// <see cref="AttachDetail"/> as a sequence of stages (it yields the stage name after each one) so the streamer can spread the work over frames:
    /// bind the pivots, bind the materials, mount and cache the dressing tiles, apply the ranges, pose the rig. The detail is only usable after the last stage.
    /// </summary>
    public System.Collections.Generic.IEnumerable<string> AttachDetailStaged(Node3D detail)
    {
        LastAttachError = string.Empty;
        DetachDetail();
        Node3D mount = _detailMount ?? this;
        if (detail.GetParent() != mount) { detail.GetParent()?.RemoveChild(detail); mount.AddChild(detail); }
        if (!_rig.BindPivots(detail, out string error))
        {
            LastAttachError = error;
            GD.PushError("WeatherLungController.AttachDetail: " + error);
            mount.RemoveChild(detail);
            yield break;
        }
        yield return "bind pivots";
        _rig.BindMaterials(detail);
        _rig.MarkBound();
        yield return "bind materials";
        _detail = detail;
        if (_farProxy != null) detail.VisibilityParent = detail.GetPathTo(_farProxy);
        CacheDressingTiles(detail);
        yield return "cache tiles";
        ApplyRanges();
        yield return "apply ranges";
        _rig.Apply(BuildPose(_phase, _sunSmoothed));
        Changed?.Invoke();
    }

    /// <summary>Unbinds the rig and removes (does not free) the Detail scene.</summary>
    public void DetachDetail()
    {
        if (_detail == null) return;
        _rig.Unbind();
        if (GodotObject.IsInstanceValid(_detail))
        {
            _detail.VisibilityParent = new NodePath();
            _detail.GetParent()?.RemoveChild(_detail);
        }
        _detail = null;
        _dressingTiles = Array.Empty<MeshInstance3D>();
        _tier = "idle";
        Changed?.Invoke();
    }

    /// <summary>Applies every tunable value (the grave-key menu and the saved defaults). Safe before the Detail scene is attached.</summary>
    public void ApplyTuning(in WeatherLungTuning t)
    {
        Enabled = t.Enabled;
        AnimationEnabled = t.AnimationEnabled;
        CycleSeconds = t.CycleSeconds;
        VaneAmplitudeDeg = t.VaneAmplitudeDeg;
        GillAmplitudeDeg = t.GillAmplitudeDeg;
        TurbineMaxRpm = t.TurbineMaxRpm;
        RippleLag = t.RippleLag;
        NozzleEmissionScale = t.NozzleEmissionScale;
        HeatGridEmissionScale = t.HeatGridEmissionScale;
        SunMode = t.SunMode;
        SunManual = t.SunManual;
        FullRateMeters = t.FullRateMeters;
        ReducedRateMeters = Mathf.Max(t.ReducedRateMeters, t.FullRateMeters);
        ReducedRateHz = t.ReducedRateHz;
        SetRanges(t.DetailRangeMeters, t.DressingRangeMeters, t.DressingFadeMeters, t.FarProxyMaxKm);
    }

    /// <summary>The node Detail is mounted under (the POI root before it is ready).</summary>
    public Node3D DetailMountNode => _detailMount ?? this;

    /// <summary>True while the Detail scene is being assembled: keeps the FarProxy visible at all distances until the handover.</summary>
    public bool HoldProxyVisible
    {
        get => _holdProxyVisible;
        set { if (_holdProxyVisible == value) return; _holdProxyVisible = value; ApplyRanges(); }
    }

    /// <summary>FarProxy begin (= the Detail visible range, via the HLOD parent) and end, and the dressing tile range / fade.</summary>
    public void SetRanges(float detailRangeMeters, float dressingRangeMeters, float dressingFadeMeters, float farProxyMaxKm)
    {
        _detailRange = Mathf.Clamp(detailRangeMeters, 1000f, 100000f);
        _dressingRange = Mathf.Clamp(dressingRangeMeters, 100f, 20000f);
        _dressingFade = Mathf.Clamp(dressingFadeMeters, 0f, 5000f);
        _farProxyMaxKm = Mathf.Clamp(farProxyMaxKm, 10f, 2000f);
        ApplyRanges();
    }

    private void ApplyRanges()
    {
        if (_farProxy != null && IsInstanceValid(_farProxy))
        {
            // While the streamer is still assembling the Detail scene the proxy stays visible at every distance (begin 0), so the handover never shows a half-built station.
            _farProxy.VisibilityRangeBegin = _holdProxyVisible ? 0f : _detailRange;
            _farProxy.VisibilityRangeEnd = _farProxyMaxKm * 1000f;
        }
        foreach (MeshInstance3D tile in _dressingTiles)
        {
            if (!IsInstanceValid(tile)) continue;
            tile.VisibilityRangeEnd = _dressingRange;
            tile.VisibilityRangeEndMargin = _dressingFade;
        }
    }

    // Dressing tiles are direct children (sector scenes) or grouped under pieces (Arch/Dressing/Tiles_k).
    private static void CollectTiles(Node node, System.Collections.Generic.List<MeshInstance3D> tiles)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mi) tiles.Add(mi);
            else CollectTiles(child, tiles);
        }
    }

    private void CacheDressingTiles(Node3D detail)
    {
        var tiles = new System.Collections.Generic.List<MeshInstance3D>();
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(detail);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n.Name == "Dressing")
            {
                CollectTiles(n, tiles);
                continue;
            }
            foreach (Node c in n.GetChildren()) stack.Push(c);
        }
        _dressingTiles = tiles.ToArray();
    }

    /// <summary>Sun sources: the key light (StarLight), the day-side atmosphere node (GDScript), the eclipse director and the cloud layer (weather clock).</summary>
    public void BindSunSources(DirectionalLight3D? star, Node? daySide, EclipseEventDirector? eclipse, RingVolumetricCloudLayer? clouds)
    {
        _star = star;
        _daySide = daySide;
        _eclipse = eclipse;
        _clouds = clouds;
        _sunInitialised = false;
    }

    /// <summary>Holds the phase at <paramref name="phase"/> (wrapped) and freezes.</summary>
    public void ScrubTo(double phase)
    {
        _frozenPhase = WeatherLungBreathMath.Frac(phase);
        _frozen = true;
        _phase = _frozenPhase;
        if (_rig.IsBound) _rig.Apply(BuildPose(_phase, _sunSmoothed));
    }

    public override void _Process(double delta)
    {
        if (!_enabled) { _tier = "disabled"; return; }
        _localClock += delta * TimeScale;
        _clock = _clouds != null && IsInstanceValid(_clouds) ? _clouds.CloudSimulationTimeSeconds : _localClock;
        _phase = _frozen ? _frozenPhase : WeatherLungBreathMath.Phase(_clock, _t0, _cycle, _phi0);
        UpdateSun(delta);

        if (!_rig.IsBound || !AnimationEnabled) { _tier = _rig.IsBound ? "paused" : "idle"; return; }
        if (_detail == null || !_detail.IsVisibleInTree()) { _tier = "hidden"; return; }
        Camera3D? camera = GetViewport()?.GetCamera3D();
        double distance = camera != null ? camera.GlobalPosition.DistanceTo(GlobalPosition) : 0.0;
        if (distance <= FullRateMeters)
        {
            _tier = "full";
            _reducedAccumulator = 0.0;
        }
        else if (distance <= ReducedRateMeters)
        {
            _tier = "reduced";
            _reducedAccumulator += delta;
            if (_reducedAccumulator < 1.0 / Math.Max(1.0, ReducedRateHz)) return;
            _reducedAccumulator = 0.0;
        }
        else { _tier = "far"; return; }
        _rig.Apply(BuildPose(_phase, _sunSmoothed));
        _updateCount++;
    }

    private WeatherLungPose BuildPose(double phase, float sun) => new()
    {
        Phase = phase, Sun = sun,
        VaneAmplitudeDeg = Math.Clamp(VaneAmplitudeDeg, 0.0, 35.0), GillAmplitudeDeg = Math.Clamp(GillAmplitudeDeg, 0.0, 35.0),
        RippleLag = Math.Clamp(RippleLag, 0.0, 0.05),
        TurbineRevolutions = WeatherLungBreathMath.TurbineRevolutions(TurbineMaxRpm, _cycle),
        NozzleEmissionScale = NozzleEmissionScale, HeatGridEmissionScale = HeatGridEmissionScale,
    };

    // --- sun ---
    private void UpdateSun(double delta)
    {
        switch (SunMode)
        {
            case WeatherLungSunMode.Day: _sunSmoothed = _sunTarget = 1f; _sunInitialised = true; return;
            case WeatherLungSunMode.Night: _sunSmoothed = _sunTarget = 0f; _sunInitialised = true; return;
            case WeatherLungSunMode.Manual: _sunSmoothed = _sunTarget = Mathf.Clamp(SunManual, 0f, 1f); _sunInitialised = true; return;
        }
        double now = _localClock;
        if (!_sunInitialised || now >= _sunRefreshAt)
        {
            _sunRefreshAt = now + SunRefreshSeconds;
            _sunTarget = ComputeAutoSun();
            if (!_sunInitialised) { _sunSmoothed = _sunTarget; _sunInitialised = true; }
        }
        _sunSmoothed += (_sunTarget - _sunSmoothed) * (1f - MathF.Exp(-(float)delta / SunTauSeconds));
    }

    private float ComputeAutoSun()
    {
        if (_star == null || !IsInstanceValid(_star)) return 1f;
        Vector3 up = IsInsideTree() ? GlobalTransform.Basis.Y.Normalized() : Vector3.Up;
        Vector3 toSun = _star.GlobalTransform.Basis.Z.Normalized();
        float visibility = _daySide != null && IsInstanceValid(_daySide) ? Mathf.Clamp(_daySide.Get(SolarVisibilityName).AsSingle(), 0f, 1f) : 1f;
        float hub = _eclipse != null && IsInstanceValid(_eclipse) ? _eclipse.HubLightDay : 0f;
        // The POI's up points inward, so the outward direction the atmosphere model expects is -up.
        return RingAtmosphereModel.LocalHazeDay(-up, toSun, visibility, hub);
    }

    // --- phase bookkeeping ---
    private void SetFrozen(bool value)
    {
        if (_frozen == value) return;
        if (value) { _frozenPhase = _phase; _frozen = true; }
        else
        {
            _frozen = false;
            _phi0 = WeatherLungBreathMath.Frac(_frozenPhase - (_clock - _t0) / _cycle);
        }
    }

    private void Retime(double cycle)
    {
        if (cycle == _cycle) return;
        double p = _frozen ? _frozenPhase : WeatherLungBreathMath.Phase(_clock, _t0, _cycle, _phi0);
        _cycle = cycle;
        _phi0 = WeatherLungBreathMath.Frac(p - (_clock - _t0) / _cycle);
    }

    private void ApplyEnabled()
    {
        Visible = _enabled;
        Changed?.Invoke();
    }

    public bool TryGetPlume(out WeatherLungPlumeState plume)
    {
        if (!IsPresent || _plumeNozzleMarker == null || _plumeCrestMarker == null)
        {
            plume = default;
            return false;
        }
        plume = new WeatherLungPlumeState(_plumeNozzleMarker.GlobalPosition, _plumeCrestMarker.GlobalPosition,
            GlobalTransform.Basis.Y.Normalized(), (float)ExhaleEnvelope);
        return true;
    }

    /// <summary>One-line status for the tuning drawer / test scene (allocates; call at a few Hz).</summary>
    public string DescribeStatus()
    {
        int revs = WeatherLungBreathMath.TurbineRevolutions(TurbineMaxRpm, _cycle);
        return $"phase {_phase:0.000} ({(_phase < 0.5 ? "inhale" : "exhale")}) cycle {_cycle:0}s revs/cycle {revs} sun {_sunSmoothed:0.00} ({SunMode}) " +
            $"detail {(IsDetailAttached ? "attached" : "none")} tier {_tier} updates {_updateCount}";
    }
}
