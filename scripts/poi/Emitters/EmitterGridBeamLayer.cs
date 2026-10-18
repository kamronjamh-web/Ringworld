using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Emitter relay grid, main-camera layer (docs/emitter-grid-climate-machines-spec.md G2-G4): 600 link ribbons in one
/// MultiMesh (origin = endpoint A, basis X = B - A, custom data = pulse coordinates) plus a small MultiMesh of head glow
/// sprites. Links are near-invisible at rest and revealed by the one-way pulse, which is a pure function of the saved
/// weather clock (<see cref="RingVolumetricCloudLayer.CloudSimulationTimeSeconds"/>). All endpoint transforms are rebuilt
/// from the floating-origin snapshot on every anchor change, never applied as deltas. Only active on worlds with emitter sites.
/// </summary>
public partial class EmitterGridBeamLayer : Node3D
{
    public const string BeamShaderPath = "res://materials/ring_emitter_grid_beam.gdshader";
    public const string HeadShaderPath = "res://materials/ring_emitter_grid_head.gdshader";
    public const string CoreShaderPath = "res://materials/ring_emitter_grid_beam_core.gdshader";
    /// <summary>
    /// Shader front slots and head sprites. The eclipse ignition (12 fronts climbing the arch both ways, plus 6 meeting-flare spans) needs more than
    /// the 12 sprites the normal pulse ever used (6 in flight at the default speed); the far layer shares the constant.
    /// </summary>
    public const int MaxShaderFronts = 24, MaxHeadSprites = 24, RibbonSubSegments = 16;
    /// <summary>Default <see cref="EventPeak"/>: the night peak is multiplied by this while eclipse ignition fronts are live (spec E.3).</summary>
    public const float DefaultEventPeak = 1.5f;
    public const float DefaultFadeMeters = 100_000f;
    /// <summary>
    /// Render priority of the near ocean (ring_ocean_proxy.gdshader: blend_mix, ALPHA 0.995, depth_draw_always; the material keeps the default 0).
    /// Transparent passes are sorted by priority first and by instance depth second, so a beam at the same priority as the water was drawn
    /// before or after it depending on the camera, and the near-opaque water then painted over the additive halo behind it.
    /// </summary>
    public const int NearWaterRenderPriority = 0;
    /// <summary>
    /// The halo and head sprites draw after the near water (which writes depth, so beams below the surface stay hidden), and the depth-writing
    /// core draws after the halo and the head sprites, or they depth-fight the depth it writes.
    /// </summary>
    public const int HaloRenderPriority = NearWaterRenderPriority + 1, CoreRenderPriority = HaloRenderPriority + 1;
    private const float NoHandoff = 1e12f;

    [Export] public bool Enabled { get => _enabled; set { _enabled = value; UpdateVisibility(); } }
    [Export] public bool AutoBind { get; set; } = true;
    /// <summary>Loads user://gameplay_emitter_grid_defaults.tres on ready when it exists (the G3 drawer applies the same file later).</summary>
    [Export] public bool LoadSavedDefaults { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GlobalTerrainPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");

    private bool _enabled = true;
    private bool _undergroundHidden;
    private VoxelChunkGridManager? _grid;
    private DirectionalLight3D? _sun;
    private RingTerrainGlobalLod? _globalTerrain;
    private RingVolumetricCloudLayer? _weather;
    private object? _lastSamplerSeen;
    private EmitterGridTopology? _topology;
    private EmitterGridPulseClock? _clock;
    private GameplayEmitterGridSettings _settings = new();
    private EmitterGridPulseSettings _pulseSettings = new();
    private MultiMeshInstance3D? _linkInstances, _headInstances;
    private ShaderMaterial? _beamMaterial, _headMaterial, _coreMaterial;
    private RingScaledFarViewport? _farViewport;
    private EmitterGridFarLayer? _farLayer;
    private double _farBindRetry;
    private Vector2 _handoffBand = new(NoHandoff, 2f * NoHandoff);
    private readonly int[] _headLink = new int[MaxHeadSprites];
    private readonly double[] _headFraction = new double[MaxHeadSprites];
    private Vector3D[] _endA = Array.Empty<Vector3D>(), _endB = Array.Empty<Vector3D>();
    private Transform3D[] _mirror = Array.Empty<Transform3D>();
    private Transform3D _worldToLayer = Transform3D.Identity;
    private Transform3D _lastGridGlobal, _lastLayerGlobal;
    private readonly Vector4[] _fronts = new Vector4[MaxShaderFronts];
    // Per-material change gates for the per-frame uniforms: an unchanged value must not dirty the material.
    private static readonly StringName MaxDistanceName = "max_distance", FadeMetersName = "fade_meters", NearClipName = "near_clip",
        CameraWorldPositionName = "camera_world_position", RingCenterName = "ring_center", SunDirectionName = "sun_direction",
        HandoffName = "eg_handoff", FrontCountName = "eg_front_count", FrontsName = "eg_fronts", FlareName = "eg_flare";
    private readonly EmitterUniformGate<float> _maxDistanceGate = new(RingMaterialWriteCounters.Site.EmitterGrid),
        _fadeGate = new(RingMaterialWriteCounters.Site.EmitterGrid), _nearClipGate = new(RingMaterialWriteCounters.Site.EmitterGrid),
        _flareGate = new(RingMaterialWriteCounters.Site.EmitterGrid);
    private readonly EmitterUniformGate<Vector3> _cameraGate = new(RingMaterialWriteCounters.Site.EmitterGrid),
        _ringCenterGate = new(RingMaterialWriteCounters.Site.EmitterGrid), _sunGate = new(RingMaterialWriteCounters.Site.EmitterGrid);
    private readonly EmitterUniformGate<Vector2> _handoffGate = new(RingMaterialWriteCounters.Site.EmitterGrid);
    private readonly EmitterUniformGate<int> _frontCountGate = new(RingMaterialWriteCounters.Site.EmitterGrid);
    private readonly EmitterVector4ArrayGate _frontsGate = new(RingMaterialWriteCounters.Site.EmitterGrid);
    private readonly Vector3[] _headLocal = new Vector3[MaxHeadSprites];
    private int _frontCount, _headCount;
    // Eclipse ignition fronts (docs/eclipse-false-sun-spec.md E.3), pushed by the director each frame and appended after the normal fronts.
    private readonly Vector4[] _eventFronts = new Vector4[MaxShaderFronts];
    private int _eventCount;
    private float _eventWeight, _appliedEventWeight, _appliedEventPeak = DefaultEventPeak;
    private double? _gridTimeOverride;
    private readonly EmitterGridClockSlew _clockSlew = new();
    private double _sessionTime, _time, _frameDt = 1d / 60d, _appliedStreakDt;
    private float _effectiveMax = 700_000f;
    private bool _showAllLinks;

    public bool IsBound => _topology != null && _linkInstances != null && _clock != null;
    public bool Unavailable { get; private set; }
    public bool UndergroundHold { get; set; }
    public bool UndergroundHidden => _undergroundHidden;
    public int RebuildCount { get; private set; }
    /// <summary>Number of times the visibility look uniforms (night peak, forced night) were written: bind, settings and event changes, never a steady frame.</summary>
    public int LookWriteCount { get; private set; }
    public double LastRebuildMs { get; private set; }
    public int GeometryRevision { get; private set; }
    public int LinkCount => _topology?.LinkCount ?? 0;
    public int RibbonVertexCount { get; private set; }
    public int LiveFrontCount => _frontCount;
    /// <summary>The shader front array as last built by <see cref="UpdateFronts"/>: the normal fronts, then the eclipse ignition fronts.</summary>
    public ReadOnlySpan<Vector4> ShaderFronts => _fronts.AsSpan(0, _frontCount);
    public int HeadSpriteCount => _headCount;
    public EmitterGridTopology? Topology => _topology;
    public EmitterGridPulseClock? Clock => _clock;
    public MultiMeshInstance3D? LinkInstances => _linkInstances;
    public MultiMeshInstance3D? HeadInstances => _headInstances;
    public ShaderMaterial? BeamMaterial => _beamMaterial;
    public ShaderMaterial? HeadMaterial => _headMaterial;
    public ShaderMaterial? CoreMaterial => _coreMaterial;
    public EmitterGridFarLayer? FarLayer => FarLayerValid ? _farLayer : null;
    /// <summary>True when the far layer exists, is enabled and is visible (its materials are then driven every frame).</summary>
    public bool FarLayerActive => FarLayerValid && _settings.FarLayerEnabled && _farLayer!.Visible;
    /// <summary>Last resolved near-to-far handoff band in metres (the 1e12 sentinel means near only).</summary>
    public Vector2 HandoffBandMeters => _handoffBand;
    private bool FarLayerValid => _farLayer != null && GodotObject.IsInstanceValid(_farLayer);
    public float EffectiveMaxDistanceMeters => _effectiveMax;
    /// <summary>Time of the last pulse evaluation (seconds on the weather clock, or the session clock when unsynchronised).</summary>
    public double PulseTimeSeconds => _time;
    public bool ClockSynchronised => _weather != null && GodotObject.IsInstanceValid(_weather);
    public GameplayEmitterGridSettings Settings => _settings;

    // Debug, not saved (drawer lands in G3).
    public bool FreezePulse { get; set; }
    /// <summary>Visual-only time offset applied on top of the clock; never written back to it.</summary>
    public double PulseScrubSeconds { get; set; }
    public bool ShowAllLinks { get => _showAllLinks; set { _showAllLinks = value; SetBeamUniform("show_all", value ? 1f : 0f); } }

    // Eclipse event hooks (the EclipseEventDirector pushes these each frame; all of them are inert at their defaults).
    /// <summary>Multiplier of the night peak while ignition fronts are live (spec E.3; saved as the eclipse beam setting GridEventPeak).</summary>
    public float EventPeak { get; set; } = DefaultEventPeak;
    /// <summary>1 draws the normal pulse fronts; 0 suppresses them (the head sprites too). The director sets 0 from the main fire to the end of the hand-off (Q3).</summary>
    public float NormalPulseWeight { get; set; } = 1f;
    /// <summary>Number of eclipse ignition fronts appended after the normal ones.</summary>
    public int EventFrontCount => _eventCount;
    /// <summary>0..1 share of the event look (night forced, peak raised) currently applied; 0 outside the ignition.</summary>
    public float EventWeight => _eventWeight;
    /// <summary>True while the pulse clock is driven by the eclipse director's grid time instead of the weather clock.</summary>
    public bool GridClockOverridden => _gridTimeOverride.HasValue;
    public double? GridTimeOverrideSeconds => _gridTimeOverride;

    /// <summary>
    /// Replaces the clock the pulse maths read (the director's <c>GridTimeSeconds</c> while <c>GridClockOverrideActive</c>); null returns to the
    /// weather clock. Never written back to either clock.
    /// </summary>
    public void SetGridClockOverride(double? seconds) => _gridTimeOverride = seconds.HasValue && double.IsFinite(seconds.Value) ? seconds : null;

    /// <summary>
    /// Sets the eclipse ignition fronts as shader vectors (headQ, column, signed travelled, plateau; docs/eclipse-false-sun-spec.md E.3). They are
    /// appended after the normal fronts at the next <see cref="UpdateFronts"/> (at most <see cref="MaxShaderFronts"/> in all; the normal fronts
    /// give way). <paramref name="eventWeight"/> (0..1) scales the event look: the night peak by <see cref="EventPeak"/> and the background to night.
    /// An empty span clears the event.
    /// </summary>
    public void SetEventFronts(ReadOnlySpan<Vector4> fronts, float eventWeight = 1f)
    {
        int n = Math.Min(fronts.Length, MaxShaderFronts);
        for (int i = 0; i < n; i++) _eventFronts[i] = fronts[i];
        for (int i = n; i < _eventCount; i++) _eventFronts[i] = Vector4.Zero;
        _eventCount = n;
        _eventWeight = n == 0 || !float.IsFinite(eventWeight) ? 0f : Math.Clamp(eventWeight, 0f, 1f);
    }

    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _globalTerrain = GetNodeOrNull<RingTerrainGlobalLod>(GlobalTerrainPath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        if (LoadSavedDefaults)
        {
            var defaults = new GameplayEmitterGridDefaults();
            defaults.Load();
            if (defaults.HasSavedFile) Configure(defaults.Settings);
        }
        UpdateVisibility();
        if (!AutoBind) SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (_grid != null) _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged;
        if (FarLayerValid) _farLayer!.QueueFree();
        _farLayer = null;
    }

    public override void _Process(double delta)
    {
        if (_grid == null) return;
        if (!IsBound)
        {
            if (!AutoBind) return;
            object? sampler = _grid.TerrainSampler;
            if (sampler == null || ReferenceEquals(sampler, _lastSamplerSeen)) return;
            _lastSamplerSeen = sampler;
            if (_grid.TerrainSampler?.EmitterSites is { } registry) Bind(_grid, EmitterGridTopology.Build(registry));
            else { Unavailable = true; UpdateVisibility(); }
            return;
        }
        if (!_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal) || !GlobalTransform.IsEqualApprox(_lastLayerGlobal))
            Rebuild(_grid.CaptureLodGeometrySnapshot());
        Update(delta);
    }

    /// <summary>Creates the link and head MultiMeshes for the topology and rebuilds the endpoint transforms. False when the assets are missing.</summary>
    public bool Bind(VoxelChunkGridManager grid, EmitterGridTopology topology)
    {
        if (IsBound) return true;
        Shader? beamShader = GD.Load<Shader>(BeamShaderPath), headShader = GD.Load<Shader>(HeadShaderPath), coreShader = GD.Load<Shader>(CoreShaderPath);
        if (beamShader == null || headShader == null || coreShader == null) { Unavailable = true; UpdateVisibility(); return false; }
        _grid = grid;
        _topology = topology;
        int count = topology.LinkCount;
        _endA = new Vector3D[count]; _endB = new Vector3D[count];
        _mirror = new Transform3D[count];
        double ringRadius = grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        _beamMaterial = CreateMaterial(beamShader, ringRadius);
        _headMaterial = CreateMaterial(headShader, ringRadius);
        _coreMaterial = CreateMaterial(coreShader, ringRadius);
        _beamMaterial.RenderPriority = HaloRenderPriority;
        _headMaterial.RenderPriority = HaloRenderPriority;
        _coreMaterial.RenderPriority = CoreRenderPriority;
        _linkInstances = CreateLinkInstances(topology, _beamMaterial);
        _headInstances = CreateHeadInstances(_headMaterial);
        ApplySettings();
        Unavailable = false;
        grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged;
        UpdateVisibility();
        Rebuild(grid.CaptureLodGeometrySnapshot());
        return true;
    }

    private static ShaderMaterial CreateMaterial(Shader shader, double ringRadius)
    {
        var material = new ShaderMaterial { Shader = shader };
        RingAtmosphereModel.ConfigureMaterial(material, ringRadius);
        return material;
    }

    private MultiMeshInstance3D CreateLinkInstances(EmitterGridTopology topology, Material material)
    {
        ArrayMesh mesh = BuildRibbonMesh(out int vertexCount);
        RibbonVertexCount = vertexCount;
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = mesh;
        multiMesh.InstanceCount = topology.LinkCount;
        multiMesh.VisibleInstanceCount = topology.LinkCount;
        for (int i = 0; i < topology.LinkCount; i++)
        {
            EmitterLink link = topology[i];
            multiMesh.SetInstanceCustomData(i, new Color((float)link.P0, (float)link.P1, link.Column, (float)(byte)link.Kind));
        }
        return AddInstance("GridLinks", multiMesh, material);
    }

    private MultiMeshInstance3D CreateHeadInstances(Material material)
    {
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = new QuadMesh { Size = Vector2.One, CustomAabb = BigAabb() };
        multiMesh.InstanceCount = MaxHeadSprites;
        multiMesh.VisibleInstanceCount = 0;
        for (int i = 0; i < MaxHeadSprites; i++) multiMesh.SetInstanceCustomData(i, new Color(1f, 0f, 0f, 0f));
        return AddInstance("GridHeads", multiMesh, material);
    }

    private MultiMeshInstance3D AddInstance(string name, MultiMesh multiMesh, Material material)
    {
        var instance = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multiMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        AddChild(instance);
        RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
        return instance;
    }

    private static Aabb BigAabb() => new(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f));

    /// <summary>
    /// 16 independent sub-segments of 4 vertices each: UV = (f, side), UV2 = (f_a, f_b) of the enclosing sub-segment, so the
    /// vertex stage can collapse or near-clip a whole sub-segment without producing partial triangles.
    /// </summary>
    public static ArrayMesh BuildRibbonMesh(out int vertexCount)
    {
        const int segments = RibbonSubSegments;
        vertexCount = segments * 4;
        var vertices = new Vector3[vertexCount];
        var uv = new Vector2[vertexCount];
        var uv2 = new Vector2[vertexCount];
        var indices = new int[segments * 6];
        for (int s = 0; s < segments; s++)
        {
            float fa = s / (float)segments, fb = (s + 1) / (float)segments;
            for (int corner = 0; corner < 4; corner++)
            {
                int v = s * 4 + corner;
                float f = corner < 2 ? fa : fb, side = (corner & 1) == 0 ? -1f : 1f;
                vertices[v] = new Vector3(f, side * 0.01f, 0f);
                uv[v] = new Vector2(f, side);
                uv2[v] = new Vector2(fa, fb);
            }
            int b = s * 4, i = s * 6;
            indices[i] = b; indices[i + 1] = b + 1; indices[i + 2] = b + 2;
            indices[i + 3] = b + 1; indices[i + 4] = b + 3; indices[i + 5] = b + 2;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = BigAabb();
        return mesh;
    }

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        Rebuild(current);
    }

    /// <summary>Recomputes every link endpoint transform from <paramref name="snapshot"/> (layer-local, absolute, never a delta). Allocation free.</summary>
    public void Rebuild(in RingLodGeometrySnapshot snapshot)
    {
        if (_topology == null || _linkInstances?.Multimesh is not { } links) return;
        long started = Stopwatch.GetTimestamp();
        _worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        _topology.FillWorldEndpoints(snapshot, _endA, _endB);
        Basis basis = _worldToLayer.Basis;
        for (int i = 0; i < _mirror.Length; i++)
        {
            Vector3D a = _endA[i], b = _endB[i];
            var local = new Transform3D(new Basis(basis * (b - a).ToVector3(), Vector3.Up, Vector3.Back), _worldToLayer * a.ToVector3());
            _mirror[i] = local;
            links.SetInstanceTransform(i, local);
        }
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastLayerGlobal = GlobalTransform;
        RebuildCount++;
        GeometryRevision++;
        LastRebuildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Layer-local transform last written for the link (CPU mirror).</summary>
    public Transform3D GetLinkTransform(int link) => link >= 0 && link < _mirror.Length ? _mirror[link] : Transform3D.Identity;

    public Vector3D GetWorldEndpointA(int link) => _endA[link];
    public Vector3D GetWorldEndpointB(int link) => _endB[link];

    /// <summary>Applies the tuning values (pulse clock, visibility and width uniforms, range, enable).</summary>
    public void Configure(GameplayEmitterGridSettings settings)
    {
        _settings = GameplayEmitterGridDefaults.Sanitize(settings);
        ApplySettings();
        Enabled = _settings.GridEnabled;
    }

    private void ApplySettings()
    {
        GameplayEmitterGridSettings s = _settings;
        double wake = Math.Max(s.PulseWakeSeconds, 0.01), trail = Math.Max(s.PulseTrailSeconds, 0.5);
        _pulseSettings = new EmitterGridPulseSettings
        {
            PeriodSeconds = s.PulsePeriodSeconds,
            AutoSpeed = s.PulseSpeedMode == 0,
            ManualSpeedMetersPerSecond = s.PulseSpeedKmPerSecond * 1000d,
            Direction = s.PulseDirection == 0 ? EmitterPulseDirection.Prograde : EmitterPulseDirection.Retrograde,
            ColumnStaggerSeconds = s.ColumnStaggerSeconds,
            TrailSeconds = trail,
            WakeSeconds = wake,
            WakeWeight = s.PulseWakeLevel,
            LeadingEdgeMeters = s.PulseLeadingEdgeKm * 1000d,
            RelaunchFlareGain = s.RelaunchFlareStrength,
            // Keep the cut-off beyond the trail even when the wake is short.
            CutoffWakeMultiples = Math.Max(4d, 6d * trail / wake)
        };
        if (_topology == null) return;
        _clock = new EmitterGridPulseClock(_topology.Registry.Recipe.Seed, _topology.CircumferenceMeters, _pulseSettings);
        _appliedStreakDt = 0d;
        if (_beamMaterial != null)
        {
            SetBeamUniform("beam_widths", new Vector4(s.CoreWidthMeters, s.HaloWidthMeters, s.MinPixelWidth, s.FarCompensation));
            SetBeamUniform("halo_amplitude", s.HaloAmplitude);
            SetBeamUniform("base_emission", s.BaseEmission);
            SetBeamUniform("show_all", _showAllLinks ? 1f : 0f);
            SetBeamUniform("core_depth", new Vector2(s.CoreDepthThreshold, s.CoreDepthMinPixels));
            _beamMaterial.NextPass = s.DepthWriteCore ? _coreMaterial : null;
            _beamMaterial.SetShaderParameter("world_scale", 1f);
            _coreMaterial?.SetShaderParameter("world_scale", 1f);
            ApplyPulseUniforms();
        }
        if (_headMaterial != null)
        {
            float near = s.HeadSpriteNearKm * 1000f, full = Mathf.Max(s.HeadSpriteFullKm * 1000f, near + 1000f);
            SetHeadUniform("sprite_min_pixels", s.FrontSpriteMinPixels);
            SetHeadUniform("head_near_m", near);
            SetHeadUniform("head_full_m", full);
            SetHeadUniform("base_emission", s.BaseEmission);
            _headMaterial.SetShaderParameter("world_scale", 1f);
        }
        UpdateFarVisibility();
    }

    /// <summary>Writes a uniform to the near halo, the near core pass and (when present) the far beam material.</summary>
    private void SetBeamUniform(StringName name, Variant value)
    {
        _beamMaterial?.SetShaderParameter(name, value);
        _coreMaterial?.SetShaderParameter(name, value);
        if (FarLayerValid) _farLayer!.BeamMaterial?.SetShaderParameter(name, value);
    }

    /// <summary>Writes a uniform to the near head material and (when present) the far head material.</summary>
    private void SetHeadUniform(StringName name, Variant value)
    {
        _headMaterial?.SetShaderParameter(name, value);
        if (FarLayerValid) _farLayer!.HeadMaterial?.SetShaderParameter(name, value);
    }

    private void ApplyVisibilityUniforms()
    {
        // During the ignition the night peak is raised by EventPeak and B is forced to night (the hub day would otherwise dim the fronts).
        float nightPeak = _settings.NightPeakIntensity * (1f + (EventPeak - 1f) * _eventWeight);
        var visibility = new Vector4(nightPeak, _settings.DayPeakIntensity, _settings.NightResidual, _settings.DayResidual);
        var day = new Vector2(50_000f, 500_000f);
        SetBeamUniform("eg_visibility", visibility);
        SetBeamUniform("eg_day_distance", day);
        SetBeamUniform("eg_force_night", _eventWeight);
        SetHeadUniform("eg_visibility", visibility);
        SetHeadUniform("eg_day_distance", day);
        SetHeadUniform("eg_force_night", _eventWeight);
        _appliedEventWeight = _eventWeight;
        _appliedEventPeak = EventPeak;
        LookWriteCount++;
    }

    private void ApplyPulseUniforms()
    {
        EmitterGridPulseClock clock = _clock!;
        double v = clock.SpeedMetersPerSecond;
        SetBeamUniform("eg_circumference", (float)clock.CircumferenceMeters);
        SetBeamUniform("eg_direction", clock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f);
        SetBeamUniform("eg_wake", new Vector2((float)(v * clock.Settings.WakeSeconds), (float)clock.Settings.WakeWeight));
        ApplyVisibilityUniforms();
        WriteStreak(_frameDt);
    }

    private void WriteStreak(double frameDt)
    {
        EmitterGridPulseClock clock = _clock!;
        double v = clock.SpeedMetersPerSecond;
        SetBeamUniform("eg_pulse_shape", new Vector4((float)clock.Settings.LeadingEdgeMeters, (float)clock.CutoffMeters,
            (float)(v * frameDt), (float)(v * clock.Settings.TrailSeconds)));
        _appliedStreakDt = frameDt;
    }

    /// <summary>Smallest of the requested distance, the global terrain fade end (when fading) and 98% of the camera far plane.</summary>
    private double CurrentEffectiveMax(Camera3D? camera)
    {
        double? fadeEnd = _globalTerrain != null && GodotObject.IsInstanceValid(_globalTerrain) && _globalTerrain.EnableFarDistanceFade
            ? _globalTerrain.FarFadeEndMeters : null;
        double? far = camera != null ? camera.Far : null;
        return EmitterStandInLayer.ComputeEffectiveMaxDistance(_settings.NearLayerMaxKm * 1000d, fadeEnd, far);
    }

    private double CurrentTime(double delta)
    {
        double baseTime;
        if (_weather != null && GodotObject.IsInstanceValid(_weather)) baseTime = _weather.CloudSimulationTimeSeconds;
        else
        {
            _sessionTime += delta;
            baseTime = _sessionTime;
        }
        return _clockSlew.Resolve(_gridTimeOverride, baseTime, _clock?.PeriodSeconds ?? 300d);
    }

    private void Update(double delta)
    {
        if (UndergroundHold || !Visible || _clock == null || _beamMaterial == null || _headMaterial == null) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null || _grid == null) return;
        if (!FreezePulse) _time = CurrentTime(delta) + PulseScrubSeconds;
        if (delta > 0d) _frameDt = Math.Clamp(_frameDt + (delta - _frameDt) * 0.1, 1d / 240d, 1d / 20d);

        _effectiveMax = (float)CurrentEffectiveMax(camera);
        float fade = Mathf.Min(DefaultFadeMeters, 0.5f * _effectiveMax);
        Vector3 cameraPosition = camera.GlobalPosition;
        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        Vector3 sun = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        UpdateFronts(_time);
        // Only an event (or a change of EventPeak) touches the look uniforms; a normal frame writes nothing extra.
        if (_eventWeight != _appliedEventWeight || EventPeak != _appliedEventPeak) ApplyVisibilityUniforms();
        // The knot relaunch flare belongs to the normal pulse: it is suppressed with it (Q3), bit-identical at weight 1.
        float flare = NormalPulseWeight > 0f ? (float)_clock.FlareEnvelope(_time) : 0f;

        WriteCommonUniforms(_beamMaterial, camera.Near, fade, cameraPosition, ringCenter, sun);
        WriteCommonUniforms(_headMaterial, camera.Near, fade, cameraPosition, ringCenter, sun);
        if (_coreMaterial != null) WriteCommonUniforms(_coreMaterial, camera.Near, fade, cameraPosition, ringCenter, sun);

        if (_farLayer == null && _settings.FarLayerEnabled && _farViewport != null && GodotObject.IsInstanceValid(_farViewport) && _farViewport.HasScaledWorld
            && (_farBindRetry -= delta) <= 0d)
        {
            _farBindRetry = 0.5d;
            BindFarLayer(_farViewport.ScaledPoint, _farViewport.CoordinateScale, _farViewport.AddScaledWorldChild);
        }

        Vector2 band = ResolveCurrentHandoffBand();
        _handoffBand = band;
        _handoffGate.Set(_beamMaterial, HandoffName, band);
        _handoffGate.Set(_coreMaterial, HandoffName, band);
        _handoffGate.Set(_headMaterial, HandoffName, band);

        WriteFrontUniforms(_beamMaterial, _coreMaterial, FarLayerValid ? _farLayer!.BeamMaterial : null, flare);
        if (Math.Abs(_frameDt - _appliedStreakDt) > 0.05 * _appliedStreakDt) WriteStreak(_frameDt);
        WriteHeads();
        if (FarLayerActive) WriteFarUniforms(fade, band, flare);
    }

    private Vector2 ResolveCurrentHandoffBand()
    {
        bool known = _farViewport != null && GodotObject.IsInstanceValid(_farViewport);
        return ResolveHandoffBand(FarLayerActive, _settings.FarHandoffFollowsClouds, known, known && _farViewport!.NearCloudOwnershipEnabled,
            known ? _farViewport!.FarCloudClipmapHandoffStartMeters : 0d, known ? _farViewport!.LocalCloudRadiusMeters : 0d,
            _settings.FarHandoffStartKm, _settings.FarHandoffEndKm);
    }

    /// <summary>
    /// Metres band over which a beam seen against the far backdrop hands off from the near to the far layer. The far layer
    /// inactive keeps the sentinel (near only); following the clouds uses the cloud handoff, or (0, 1) when the near clouds
    /// are suspended; otherwise the manual km values.
    /// </summary>
    public static Vector2 ResolveHandoffBand(bool farLayerActive, bool followClouds, bool cloudOwnershipKnown, bool nearCloudOwnership,
        double cloudStartMeters, double cloudEndMeters, double manualStartKm, double manualEndKm)
    {
        if (!farLayerActive) return new Vector2(NoHandoff, 2f * NoHandoff);
        if (followClouds && cloudOwnershipKnown)
            return nearCloudOwnership ? new Vector2((float)cloudStartMeters, (float)Math.Max(cloudEndMeters, cloudStartMeters + 1d)) : new Vector2(0f, 1f);
        return new Vector2((float)(manualStartKm * 1000d), (float)Math.Max(manualEndKm * 1000d, manualStartKm * 1000d + 1000d));
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        if (edge1 == edge0) return x < edge0 ? 0f : 1f; // GLSL leaves this undefined; the shaders never hit it
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float NearFade(float d, float maxDistance, float fade) => 1f - Smoothstep(Math.Max(maxDistance - fade, 0f), maxDistance, d);
    private static float BandWeight(float d, Vector2 band) => 1f - Smoothstep(band.X, Math.Max(band.Y, band.X + 1f), d);

    /// <summary>CPU mirror of the near layer weight in ring_emitter_grid_layers.gdshaderinc (<paramref name="backdrop"/>: no opaque main-camera surface behind the fragment).</summary>
    public static float NearLayerWeight(float d, bool backdrop, float maxDistance, float fade, Vector2 band)
    {
        float nearFade = NearFade(d, maxDistance, fade);
        return backdrop ? Math.Min(nearFade, BandWeight(d, band)) : nearFade;
    }

    /// <summary>CPU mirror of the far layer weight in ring_emitter_grid_layers.gdshaderinc.</summary>
    public static float FarLayerWeight(float d, float maxDistance, float fade, Vector2 band) =>
        1f - Math.Min(NearFade(d, maxDistance, fade), BandWeight(d, band));

    /// <summary>
    /// Creates the far layer for the bound topology and attaches it through <paramref name="attach"/> (normally
    /// <see cref="RingScaledFarViewport.AddScaledWorldChild"/>). False when unbound, the assets are missing or the attach fails.
    /// </summary>
    public bool BindFarLayer(Func<double, double, double, Vector3> scaledPoint, float coordinateScale, Func<Node3D, bool> attach)
    {
        if (FarLayerValid) return true;
        if (_topology == null || _grid == null) return false;
        var layer = new EmitterGridFarLayer { Name = "EmitterGridFar" };
        if (!layer.Build(_topology, scaledPoint, coordinateScale, _topology.CircumferenceMeters / Math.Tau) || !attach(layer))
        {
            layer.Free();
            return false;
        }
        _farLayer = layer;
        if (_clock != null) ApplySettings();
        else UpdateFarVisibility();
        return true;
    }

    private void UpdateFarVisibility()
    {
        if (FarLayerValid) _farLayer!.Visible = Visible && _settings.FarLayerEnabled;
    }

    private void WriteFarUniforms(float nearFade, Vector2 band, float flare)
    {
        EmitterGridFarLayer far = _farLayer!;
        Camera3D? camera = far.GetViewport()?.GetCamera3D();
        float nearClip = camera?.Near ?? 0.1f;
        Vector3 cameraPosition = camera?.GlobalPosition ?? Vector3.Zero;
        Vector3 sunWorld = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Vector3 sun = _grid != null ? (_grid.GlobalBasis.Orthonormalized().Inverse() * sunWorld).Normalized() : sunWorld;
        WriteFarCommon(far.BeamMaterial, nearFade, nearClip, cameraPosition, sun, band);
        WriteFarCommon(far.HeadMaterial, nearFade, nearClip, cameraPosition, sun, band);
        // The far beam material already received the fronts and flare through SetBeamUniform; the head sprites need them too.
        if (far.HeadMaterial is { } head) WriteFrontUniforms(head, null, null, flare);
    }

    /// <summary>Writes the front count, front array and flare to each given material, skipping values it already holds.</summary>
    private void WriteFrontUniforms(ShaderMaterial? first, ShaderMaterial? second, ShaderMaterial? third, float flare)
    {
        _frontCountGate.Set(first, FrontCountName, _frontCount);
        _frontCountGate.Set(second, FrontCountName, _frontCount);
        _frontCountGate.Set(third, FrontCountName, _frontCount);
        _frontsGate.Set(first, FrontsName, _fronts);
        _frontsGate.Set(second, FrontsName, _fronts);
        _frontsGate.Set(third, FrontsName, _fronts);
        _frontsGate.End();
        _flareGate.Set(first, FlareName, flare);
        _flareGate.Set(second, FlareName, flare);
        _flareGate.Set(third, FlareName, flare);
    }

    private void WriteFarCommon(ShaderMaterial? material, float nearFade, float nearClip, Vector3 cameraPosition, Vector3 sun, Vector2 band)
    {
        if (material == null) return;
        _maxDistanceGate.Set(material, MaxDistanceName, _effectiveMax);
        _fadeGate.Set(material, FadeMetersName, nearFade);
        _nearClipGate.Set(material, NearClipName, nearClip);
        _cameraGate.Set(material, CameraWorldPositionName, cameraPosition);
        _ringCenterGate.Set(material, RingCenterName, Vector3.Zero);
        _sunGate.Set(material, SunDirectionName, sun);
        _handoffGate.Set(material, HandoffName, band);
    }

    private void WriteCommonUniforms(ShaderMaterial material, float near, float fade, Vector3 cameraPosition, Vector3 ringCenter, Vector3 sun)
    {
        _maxDistanceGate.Set(material, MaxDistanceName, _effectiveMax);
        _fadeGate.Set(material, FadeMetersName, fade);
        _nearClipGate.Set(material, NearClipName, near);
        _cameraGate.Set(material, CameraWorldPositionName, cameraPosition);
        _ringCenterGate.Set(material, RingCenterName, ringCenter);
        _sunGate.Set(material, SunDirectionName, sun);
    }

    /// <summary>Evaluates the live fronts of every chain at time <paramref name="t"/> into the shader array and the head sprite positions.</summary>
    public void UpdateFronts(double t)
    {
        if (_clock == null || _topology == null) return;
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        int total = 0, heads = 0;
        double c = _clock.CircumferenceMeters;
        // The ignition fronts keep their slots; a suppressed normal pulse (weight 0) contributes neither fronts nor head sprites.
        int normalCap = NormalPulseWeight > 0f ? MaxShaderFronts - _eventCount : 0;
        for (int column = 0; column < _topology.Columns && total < normalCap; column++)
        {
            int n = _clock.GetFronts(t, column, buffer);
            for (int i = 0; i < n && total < normalCap; i++)
            {
                double head = buffer[i].HeadQ;
                _fronts[total++] = new Vector4((float)head, column, 0f, 0f);
                if (head >= 0d && head <= c) AddHeadSprite(column, head, ref heads);
            }
        }
        for (int i = 0; i < _eventCount; i++)
        {
            Vector4 front = _eventFronts[i];
            _fronts[total++] = front;
            // Only a front that still has a head (w >= 0) climbs the arch; absorbed fronts and the meeting flare are plateau spans.
            if (front.W >= 0f && front.X >= 0f && front.X <= c) AddHeadSprite((int)front.Y, front.X, ref heads);
        }
        for (int i = total; i < MaxShaderFronts; i++) _fronts[i] = Vector4.Zero;
        _frontCount = total;
        _headCount = heads;
    }

    private void AddHeadSprite(int column, double headQ, ref int heads)
    {
        if (heads >= MaxHeadSprites || !TryHeadLocation(column, headQ, out int link, out double fraction)) return;
        Vector3D world = _endA[link] + (_endB[link] - _endA[link]) * fraction;
        _headLocal[heads] = _worldToLayer * world.ToVector3();
        _headLink[heads] = link;
        _headFraction[heads] = fraction;
        heads++;
    }

    private void WriteHeads()
    {
        if (_headInstances?.Multimesh is not { } heads) return;
        for (int i = 0; i < _headCount; i++) heads.SetInstanceTransform(i, new Transform3D(Basis.Identity, _headLocal[i]));
        if (heads.VisibleInstanceCount != _headCount) heads.VisibleInstanceCount = _headCount;
        SyncFarHeads();
    }

    /// <summary>Writes the heads found by the last <see cref="UpdateFronts"/> to the far layer sprites (no-op without an active far layer).</summary>
    public void SyncFarHeads()
    {
        if (FarLayerActive) _farLayer!.WriteHeads(_headLink, _headFraction, _headCount);
    }

    /// <summary>Layer-local position of a head at direction-adjusted coordinate <paramref name="q"/> on <paramref name="column"/>.</summary>
    public bool TryHeadLocal(int column, double q, out Vector3 local)
    {
        local = default;
        if (!TryHeadLocation(column, q, out int index, out double f)) return false;
        Vector3D world = _endA[index] + (_endB[index] - _endA[index]) * f;
        local = _worldToLayer * world.ToVector3();
        return true;
    }

    /// <summary>Link index and chord fraction of the head at direction-adjusted coordinate <paramref name="q"/> on <paramref name="column"/>.</summary>
    public bool TryHeadLocation(int column, double q, out int link, out double fraction)
    {
        link = 0; fraction = 0d;
        if (_topology == null || _clock == null) return false;
        double c = _topology.CircumferenceMeters;
        double p = _clock.Settings.Direction == EmitterPulseDirection.Prograde ? q : c - q;
        int first = _topology.ChainFirstLink(column), lo = 0, hi = _topology.LinksPerChain - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_topology[first + mid].P0 <= p) lo = mid; else hi = mid - 1;
        }
        EmitterLink found = _topology[first + lo];
        double span = found.P1 - found.P0;
        fraction = span > 0d ? Math.Clamp((p - found.P0) / span, 0d, 1d) : 0d;
        link = first + lo;
        return true;
    }

    // ---------------------------------------------------------------- cloud light (tweaks T5)

    /// <summary>Times <see cref="GatherCloudLight"/> was called (a test counter: grid links off must not gather).</summary>
    public long CloudLightGatherCount { get; private set; }
    /// <summary>Links the last gather offered (a diagnostic for the captures and the status line).</summary>
    public int CloudLightOfferedCount { get; private set; }
    private double[] _linkAltitudeA = Array.Empty<double>(), _linkAltitudeB = Array.Empty<double>(), _linkSagitta = Array.Empty<double>();
    private readonly EmitterEventFront[] _eventScratch = new EmitterEventFront[MaxShaderFronts];

    /// <summary>
    /// Offers the grid links near the camera that are lit by a pulse right now and cross the cloud deck, as transient segment lights (tier 1: after the beams and
    /// the orbs). For each link within <paramref name="reach"/> + <paramref name="radius"/> of <paramref name="camera"/> whose altitude at the camera's closest point
    /// lies in [<paramref name="floor"/>, <paramref name="ceiling"/>] (widened by the radius) the pulse brightness there (the normal fronts, the ignition fronts and the
    /// knot flare, the shader's maths) gives the colour, and the segment is the chord within <paramref name="radius"/> of that point. Allocation free; nothing when the
    /// layer is hidden or the grid is dark.
    /// </summary>
    public void GatherCloudLight(EclipseCloudLightFeeder feeder, Vector3D camera, double floor, double ceiling, double reach, float radius, float intensity, Vector3 color, double pulseTime)
    {
        CloudLightGatherCount++;
        CloudLightOfferedCount = 0;
        if (!IsBound || !Visible || UndergroundHold || _topology == null || _clock == null || intensity <= 0f) return;
        EmitterGridTopology topology = _topology;
        EmitterGridPulseClock clock = _clock;
        if (_linkSagitta.Length != topology.LinkCount) BuildLinkAltitudes(topology);
        for (int i = 0; i < _eventCount; i++)
        {
            Vector4 f = _eventFronts[i];
            _eventScratch[i] = new EmitterEventFront(f.X, (int)MathF.Round(f.Y), f.Z, f.W);
        }
        ReadOnlySpan<EmitterEventFront> events = _eventScratch.AsSpan(0, _eventCount);
        double t = pulseTime, cull = reach + radius, low = floor - radius, high = ceiling + radius;
        for (int l = 0; l < topology.LinkCount; l++)
        {
            Vector3D a = _endA[l], b = _endB[l];
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z, len2 = abx * abx + aby * aby + abz * abz;
            if (len2 < 1d) continue;
            double s = Math.Clamp(((camera.X - a.X) * abx + (camera.Y - a.Y) * aby + (camera.Z - a.Z) * abz) / len2, 0d, 1d);
            double dx = a.X + abx * s - camera.X, dy = a.Y + aby * s - camera.Y, dz = a.Z + abz * s - camera.Z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > cull || !feeder.Wants(EclipseCloudLightFeeder.TierGrid, distance)) continue;
            double altitude = _linkAltitudeA[l] + (_linkAltitudeB[l] - _linkAltitudeA[l]) * s + 4d * s * (1d - s) * _linkSagitta[l];
            if (altitude < low || altitude > high) continue;
            ref readonly EmitterLink link = ref topology.Links[l];
            double pulse = CloudLightPulse(clock, link, s, t, events);
            if (pulse < 0.03d) continue;
            double chord = Math.Sqrt(len2), half = radius / chord, s0 = Math.Max(0d, s - half), s1 = Math.Min(1d, s + half);
            var from = new Vector3((float)(a.X + abx * s0), (float)(a.Y + aby * s0), (float)(a.Z + abz * s0));
            var to = new Vector3((float)(a.X + abx * s1), (float)(a.Y + aby * s1), (float)(a.Z + abz * s1));
            feeder.Offer(new SunshineSegmentLight(from, to, radius, color * (intensity * (float)pulse), false), EclipseCloudLightFeeder.TierGrid, distance);
            CloudLightOfferedCount++;
        }
    }

    /// <summary>Pulse brightness 0..1 at fraction <paramref name="f"/> of a link: the shader's sum of the normal fronts (times the suppression weight), the ignition fronts and the knot flare.</summary>
    private double CloudLightPulse(EmitterGridPulseClock clock, in EmitterLink link, double f, double t, ReadOnlySpan<EmitterEventFront> events)
    {
        double q = clock.ToQ(link.P0 + f * (link.P1 - link.P0));
        double sum = NormalPulseWeight > 0f ? clock.PulseAtQ(t, link.Column, q) * NormalPulseWeight : 0d;
        for (int i = 0; i < events.Length; i++)
            if (events[i].Column == link.Column) sum += clock.EventFrontAtQ(events[i], q);
        sum = Math.Min(1d, sum);
        if (NormalPulseWeight > 0f && link.Kind != EmitterLinkKind.Chain) sum = Math.Max(sum, clock.FlareEnvelope(t));
        return sum;
    }

    /// <summary>Altitudes of both ends and the sagitta of every link, so a point at chord fraction s sits at altitude A + (B - A) s + 4 s (1 - s) sagitta.</summary>
    private void BuildLinkAltitudes(EmitterGridTopology topology)
    {
        int count = topology.LinkCount;
        _linkAltitudeA = new double[count]; _linkAltitudeB = new double[count]; _linkSagitta = new double[count];
        for (int i = 0; i < count; i++)
        {
            EmitterLink link = topology[i];
            _linkAltitudeA[i] = topology.RelayNode(link.FromSite).Vertical;
            _linkAltitudeB[i] = topology.RelayNode(link.ToSite).Vertical;
            _linkSagitta[i] = topology.GetGeometry(i).SagittaM;
        }
    }

    public void SetUndergroundHidden(bool hidden)
    {
        if (_undergroundHidden == hidden) return;
        _undergroundHidden = hidden;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        Visible = _enabled && IsBound && !_undergroundHidden;
        UpdateFarVisibility();
    }

    /// <summary>Debug "Fire pulse now": a visual-only offset so the next launch happens now.</summary>
    public void FirePulseNow()
    {
        if (_clock == null) return;
        double t = CurrentTime(0d);
        PulseScrubSeconds = _clock.LaunchTime(_clock.LatestLaunch(t) + 1) - t;
    }

    public string DescribeStatus()
    {
        if (!IsBound) return Unavailable ? "Emitter grid unavailable (needs the v5 sites world)." : "Emitter grid waiting for the terrain sampler.";
        EmitterGridPulseClock clock = _clock!;
        string farState = FarLayerActive ? "active" : FarLayerValid || !_settings.FarLayerEnabled ? "off" : "waiting";
        string handoff = _handoffBand.X >= NoHandoff ? "near only"
            : string.Format(CultureInfo.InvariantCulture, "{0:0}-{1:0} km", _handoffBand.X / 1000f, _handoffBand.Y / 1000f);
        return string.Format(CultureInfo.InvariantCulture,
            "Grid {0} links id {1:X8}, clock {2}, {3:0.0} km/s {4}, launch {5}, fronts {6}, lit links {7}, rebuilds {8} ({9:0.00} ms), core depth {10}, far layer {11}, handoff {12}",
            LinkCount, _topology!.Identity & 0xFFFFFFFFUL, ClockSynchronised ? "weather" : "unsynchronised", clock.SpeedMetersPerSecond / 1000d,
            clock.Settings.Direction, clock.LatestLaunch(_time), _frontCount, clock.CountLitLinks(_topology, _time), RebuildCount, LastRebuildMs,
            _settings.DepthWriteCore ? "on" : "off", farState, handoff);
    }
}

/// <summary>
/// Continuous hand-over between the weather clock and the eclipse director's grid clock (docs/eclipse-false-sun-spec.md, E4 review fixes).
/// A raw switch teleports the pulse phase by the offset between the two clocks; instead the offset d (wrapped into (-P/2, P/2]) is slewed out over
/// one period P, so the pulse runs at 0.5-1.5x, never reverses and stays phase-continuous mod P. A time jump (scrub, load) snaps.
/// </summary>
public sealed class EmitterGridClockSlew
{
    /// <summary>A step of either clock larger than this (seconds between two evaluations) is a jump, not a frame.</summary>
    public const double JumpSeconds = 5d;

    private enum Mode { None, ToWeather, ToOverride }
    private Mode _mode;
    private double _d, _start;
    private double? _prevOverride;
    private double _prevBase = double.NaN, _prevOverrideStep, _prevResult;

    /// <summary>True while an override/weather hand-over is being slewed.</summary>
    public bool Slewing => _mode != Mode.None;

    public void Reset()
    {
        _mode = Mode.None;
        _prevOverride = null;
        _prevBase = double.NaN;
    }

    private static double Wrap(double x, double period)
    {
        double r = x - Math.Floor(x / period) * period; // [0, P)
        return r > 0.5d * period ? r - period : r;
    }

    /// <summary>The pulse time for this evaluation. <paramref name="overrideTime"/> null is the weather (or session) clock.</summary>
    public double Resolve(double? overrideTime, double baseTime, double period)
    {
        period = Math.Max(1d, period);
        bool baseJump = !double.IsNaN(_prevBase) && Math.Abs(baseTime - _prevBase) > JumpSeconds;
        double overrideStep = overrideTime.HasValue && _prevOverride.HasValue ? overrideTime.Value - _prevOverride.Value : 0d;
        bool overrideJump = Math.Abs(overrideStep) > JumpSeconds;
        bool hadPrev = !double.IsNaN(_prevBase);

        if (overrideTime.HasValue != _prevOverride.HasValue && hadPrev)
        {
            if (overrideTime.HasValue)
            {
                // none -> override: start from where the pulse time was.
                if (baseJump) _mode = Mode.None;
                else { _mode = Mode.ToOverride; _d = Wrap(overrideTime.Value - (_prevResult + (baseTime - _prevBase)), period); _start = overrideTime.Value; }
            }
            else
            {
                // override -> none: start from where the pulse time was.
                if (baseJump || Math.Abs(_prevOverrideStep) > JumpSeconds) _mode = Mode.None;
                else { _mode = Mode.ToWeather; _d = Wrap(_prevResult + (baseTime - _prevBase) - baseTime, period); _start = baseTime; }
            }
        }
        else if (baseJump || overrideJump) _mode = Mode.None;

        double result;
        if (_mode == Mode.ToWeather && !overrideTime.HasValue)
        {
            double u = Math.Clamp((baseTime - _start) / period, 0d, 1d);
            result = baseTime + _d * (1d - u);
            if (u >= 1d) _mode = Mode.None;
        }
        else if (_mode == Mode.ToOverride && overrideTime.HasValue)
        {
            double u = Math.Clamp((overrideTime.Value - _start) / period, 0d, 1d);
            result = overrideTime.Value - _d * (1d - u);
            if (u >= 1d) _mode = Mode.None;
        }
        else
        {
            _mode = Mode.None;
            result = overrideTime ?? baseTime;
        }

        _prevOverrideStep = overrideStep;
        _prevOverride = overrideTime;
        _prevBase = baseTime;
        _prevResult = result;
        return result;
    }
}
