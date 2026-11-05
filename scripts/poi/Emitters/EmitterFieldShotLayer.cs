using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Owns the emitters' field shots at runtime (the orb each ground emitter fires up to the containment field). Data side: it builds the pure
/// <see cref="EmitterFieldShotSchedule"/> from the emitter grid topology, the eclipse beam origin and the live field profile, keeps its timing in step with the
/// eclipse orb settings and the grave-menu interval, and lists the active shots on the weather clock. The ripples are published by
/// <see cref="EmitterShotDisturbanceAdapter"/> in the disturbance host, which reads <see cref="Schedule"/>. Runs at the default priority, before the host (100).
/// Render side: every active shot is one instance of a MultiMesh of orb quads (the false sun's orb, <c>ring_emitter_field_shot.gdshader</c>), with a dispersal flare
/// at the field. The near layer (this node's "FieldShotOrbs") draws the shots inside the near range; <see cref="EmitterFieldShotFarLayer"/> draws every shot in the
/// scaled far world and the two hand off by the same weights as the relay grid and the hub beams (settings: <c>EclipseBeamSettings</c> orb look, far layer, depth core).
/// </summary>
public partial class EmitterFieldShotLayer : Node3D
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GridLayerPath { get; set; } = new("../EmitterGrid");
    [Export] public NodePath DirectorPath { get; set; } = new("../EclipseEvent");
    [Export] public NodePath HostPath { get; set; } = new("../ContainmentDisturbances");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");

    [Export] public NodePath GlobalTerrainPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");

    public const string NearShaderPath = "res://materials/ring_emitter_field_shot.gdshader";
    public const string CoreShaderPath = "res://materials/ring_emitter_field_shot_core.gdshader";
    /// <summary>The colour pass draws after the near water, the depth-writing core after it (same values as the hub beams and the relay grid).</summary>
    public const int HaloRenderPriority = EmitterGridBeamLayer.HaloRenderPriority, CoreRenderPriority = EmitterGridBeamLayer.CoreRenderPriority;
    public const int InitialCapacity = 64;
    private const float NoHandoff = 1e12f;

    public static EmitterFieldShotLayer? Instance { get; private set; }

    private VoxelChunkGridManager? _grid;
    private EmitterGridBeamLayer? _gridLayer;
    private EclipseEventDirector? _director;
    private RingContainmentDisturbanceHost? _host;
    private RingVolumetricCloudLayer? _weather;
    private (EmitterGridTopology? Topology, EmitterBeamOrigin Origin, ContainmentFieldProfile? Profile, double Radius, double VerticalBase) _built;
    private RingLodGeometrySnapshot _snapshot;
    private bool _hasSnapshot;
    private RingTerrainGlobalLod? _globalTerrain;
    private RingScaledFarViewport? _farViewport;
    private EclipseBeamSettings _beams = new();
    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _nearMaterial, _coreMaterial;
    private EmitterFieldShotFarLayer? _farLayer;
    private double _farBindRetry;
    private int _capacity;
    private Vector3[] _nearOrigins = Array.Empty<Vector3>();
    private Color[] _nearCustom = Array.Empty<Color>();
    private bool _undergroundHidden, _shown = true, _lookApplied;
    private float _effectiveMax = 700_000f, _appliedMax = -1f, _appliedFade = -1f, _appliedNear = -1f, _farAppliedNear = -1f;
    private Vector3 _appliedRingCenter = new(float.NaN, 0f, 0f);
    private Vector2 _handoffBand = new(NoHandoff, 2f * NoHandoff), _appliedBand = new(float.NaN, 0f);
    private bool _warnedMissingShaders;

    /// <summary>The live schedule (null until the grid, the field profile and the world are all available).</summary>
    public EmitterFieldShotSchedule? Schedule { get; private set; }
    /// <summary>Shots in flight or dispersing at <see cref="NowSeconds"/> (reused every frame).</summary>
    public List<EmitterFieldShot> ActiveShots { get; } = new();
    public double NowSeconds { get; private set; } = double.NaN;
    public double IntervalSeconds { get; private set; }
    public int Builds { get; private set; }
    /// <summary>Test seam: replaces the timing computed from the eclipse settings and the grid settings.</summary>
    public EmitterFieldShotTiming? TimingOverride { get; set; }
    /// <summary>Held while the player is enclosed underground: the render side writes nothing (the data side keeps running for the ripples).</summary>
    public bool UndergroundHold { get; set; }
    public bool UndergroundHidden => _undergroundHidden;
    /// <summary>Shots drawn by the near layer / the far layer this frame.</summary>
    public int DrawnNear { get; private set; }
    public int DrawnFar { get; private set; }
    /// <summary>Wall time of the whole last <c>_Process</c> (data and render side), in microseconds.</summary>
    public long LastUpdateMicroseconds { get; private set; }
    public MultiMeshInstance3D? NearInstances => _instances;
    public ShaderMaterial? NearMaterial => _nearMaterial;
    public ShaderMaterial? CoreMaterial => _coreMaterial;
    public EmitterFieldShotFarLayer? FarLayer => FarLayerValid ? _farLayer : null;
    public bool FarLayerActive => FarLayerValid && _beams.FarLayerEnabled && _farLayer!.Visible;
    /// <summary>The near range (metres) the near layer keeps a shot within (ring_emitter_grid_layers: max_distance).</summary>
    public float EffectiveMaxDistanceMeters => _effectiveMax;
    public EclipseBeamSettings Beams => _beams;
    private bool FarLayerValid => _farLayer != null && IsInstanceValid(_farLayer);

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        Schedule = null;
        ActiveShots.Clear();
        _hasSnapshot = false;
        if (FarLayerValid)
        {
            _farLayer!.GetParent()?.RemoveChild(_farLayer);
            _farLayer.QueueFree();
        }
        _farLayer = null;
        if (_instances != null && IsInstanceValid(_instances)) _instances.QueueFree();
        _instances = null;
        _nearMaterial = null;
        _coreMaterial = null;
    }

    public override void _Process(double delta)
    {
        long started = Stopwatch.GetTimestamp();
        ProcessData();
        ProcessRender(delta);
        LastUpdateMicroseconds = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000.0);
    }

    private void ProcessData()
    {
        _hasSnapshot = false;
        if (!Enabled) { Schedule = null; ActiveShots.Clear(); return; }
        Resolve();
        if (_grid == null || _gridLayer == null || !_gridLayer.IsBound || _gridLayer.Topology == null || _weather == null) return;
        EclipseEventSettings eclipse = _director?.Engine.Settings ?? new EclipseEventSettings();
        IntervalSeconds = _gridLayer.Settings.FieldShotIntervalSeconds;
        EmitterFieldShotTiming timing = TimingOverride ?? EmitterFieldShotTiming.FromEclipse(eclipse, IntervalSeconds);
        EnsureSchedule(_gridLayer.Topology, eclipse.Beams.BeamOrigin, timing);
        NowSeconds = _weather.CloudSimulationTimeSeconds;
        if (Schedule == null) { ActiveShots.Clear(); return; }
        Schedule.Timing = timing;
        Schedule.CollectActive(NowSeconds, ActiveShots);
    }

    private void Resolve()
    {
        if (_grid == null || !IsInstanceValid(_grid)) _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (_gridLayer == null || !IsInstanceValid(_gridLayer)) _gridLayer = GetNodeOrNull<EmitterGridBeamLayer>(GridLayerPath);
        if (_director == null || !IsInstanceValid(_director)) _director = GetNodeOrNull<EclipseEventDirector>(DirectorPath);
        if (_host == null || !IsInstanceValid(_host)) _host = GetNodeOrNull<RingContainmentDisturbanceHost>(HostPath);
        if (_weather == null || !IsInstanceValid(_weather)) _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        if (_globalTerrain == null || !IsInstanceValid(_globalTerrain)) _globalTerrain = GetNodeOrNull<RingTerrainGlobalLod>(GlobalTerrainPath);
        if (_farViewport == null || !IsInstanceValid(_farViewport)) _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
    }

    private void EnsureSchedule(EmitterGridTopology topology, EmitterBeamOrigin origin, EmitterFieldShotTiming timing)
    {
        ContainmentFieldProfile? profile = _host?.Profile;
        if (profile == null || _grid == null) { Schedule = null; return; }
        RingLodGeometrySnapshot snapshot = _grid.CaptureLodGeometrySnapshot();
        _snapshot = snapshot;
        _hasSnapshot = true;
        var key = (topology, origin, profile, snapshot.RingRadiusMeters, snapshot.RingVerticalBaseOffsetMeters);
        if (Schedule != null && ReferenceEquals(_built.Topology, key.topology) && _built.Origin == key.origin && _built.Profile == key.profile
            && _built.Radius == key.RingRadiusMeters && _built.VerticalBase == key.RingVerticalBaseOffsetMeters) return;
        _built = key;
        Schedule = EmitterFieldShotSchedule.Build(topology, origin, profile.Value, snapshot.RingRadiusMeters, snapshot.RingVerticalBaseOffsetMeters, timing);
        Builds++;
    }

    public string DescribeStatus()
    {
        if (Schedule == null) return "Emitter field shots: no schedule (grid, field or weather not ready).";
        string far = FarLayerActive ? "active" : FarLayerValid || !_beams.FarLayerEnabled ? "off" : "waiting";
        return string.Create(CultureInfo.InvariantCulture,
            $"Emitter field shots: {Schedule.BeamCount} emitters ({Schedule.ExcludedBeams} excluded), interval {(Schedule.Timing.Off ? "off" : IntervalSeconds.ToString("0") + " s")}, {ActiveShots.Count} in flight, t {NowSeconds:0.0} s, drawn near {DrawnNear} far {DrawnFar} (far layer {far}), update {LastUpdateMicroseconds} us.");
    }

    // ---------------------------------------------------------------- render side

    /// <summary>One quad (4 vertices, 6 indices), every vertex at the origin, UV = the corner (-1 or 1 on both axes): the vertex stage billboards it around the instance origin.</summary>
    public static ArrayMesh BuildOrbMesh()
    {
        var vertices = new Vector3[4];
        var uv = new Vector2[4];
        for (int corner = 0; corner < 4; corner++) uv[corner] = new Vector2((corner & 1) == 0 ? -1f : 1f, corner < 2 ? -1f : 1f);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 1, 3, 2 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f));
        return mesh;
    }

    /// <summary>The shot as the shader gets it: (orb envelope, flare, spread, energy).</summary>
    private static Color CustomOf(in EmitterFieldShotState state, float energy) => new(state.OrbEnvelope, state.Flare, state.Spread, energy);

    public void SetUndergroundHidden(bool hidden)
    {
        if (_undergroundHidden == hidden) return;
        _undergroundHidden = hidden;
        UpdateVisibility(Enabled && Schedule != null);
    }

    private void UpdateVisibility(bool wanted)
    {
        bool show = wanted && !_undergroundHidden;
        if (show == _shown) return;
        _shown = show;
        if (_instances != null && IsInstanceValid(_instances)) _instances.Visible = show;
        if (FarLayerValid) _farLayer!.Visible = show && _beams.FarLayerEnabled;
        if (!show)
        {
            DrawnNear = 0;
            DrawnFar = 0;
            if (_instances?.Multimesh is { } mm && mm.VisibleInstanceCount != 0) mm.VisibleInstanceCount = 0;
            if (FarLayerValid) _farLayer!.Clear();
        }
    }

    private void ProcessRender(double delta)
    {
        UpdateVisibility(Enabled && Schedule != null);
        if (!_shown || UndergroundHold || Schedule == null || !_hasSnapshot) return;
        EclipseBeamSettings beams = _director?.Engine.Settings.Beams ?? _beams;
        if (!_lookApplied || (!ReferenceEquals(beams, _beams) && beams != _beams)) ApplyLook(beams);
        if (!EnsureNear()) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        EmitterGridBeamLayer? gridLayer = _gridLayer != null && IsInstanceValid(_gridLayer) ? _gridLayer : null;
        var gridSettings = gridLayer?.Settings;
        float nearMaxKm = gridSettings?.NearLayerMaxKm ?? 700f;
        double? fadeEnd = _globalTerrain != null && IsInstanceValid(_globalTerrain) && _globalTerrain.EnableFarDistanceFade ? _globalTerrain.FarFadeEndMeters : null;
        _effectiveMax = (float)EmitterStandInLayer.ComputeEffectiveMaxDistance(nearMaxKm * 1000d, fadeEnd, camera?.Far);
        float fade = Mathf.Min(EmitterGridBeamLayer.DefaultFadeMeters, 0.5f * _effectiveMax);
        Vector3 cameraPosition = camera?.GlobalPosition ?? Vector3.Zero;
        Vector3 ringCenter = _grid!.GetCanonicalRingCenterWorld();
        if (camera != null)
        {
            // The near material and core need the camera every frame; the rest only when it changes.
            _nearMaterial!.SetShaderParameter("camera_world_position", cameraPosition);
            _coreMaterial!.SetShaderParameter("camera_world_position", cameraPosition);
            if (camera.Near != _appliedNear) { SetNearUniform("near_clip", camera.Near); _appliedNear = camera.Near; }
        }
        if (_effectiveMax != _appliedMax) SetNearUniform("max_distance", _effectiveMax);
        if (fade != _appliedFade) SetNearUniform("fade_meters", fade);
        if (ringCenter != _appliedRingCenter) { SetNearUniform("ring_center", ringCenter); _appliedRingCenter = ringCenter; }

        if (_farLayer == null && _beams.FarLayerEnabled && _farViewport != null && IsInstanceValid(_farViewport) && _farViewport.HasScaledWorld && (_farBindRetry -= delta) <= 0d)
        {
            _farBindRetry = 0.5d;
            BindFarLayer(_farViewport.CoordinateScale, _farViewport.AddScaledWorldChild);
        }
        bool farActive = FarLayerActive;
        bool follows = gridSettings?.FarHandoffFollowsClouds ?? true;
        bool known = _farViewport != null && IsInstanceValid(_farViewport);
        Vector2 band = EmitterGridBeamLayer.ResolveHandoffBand(farActive, follows, known, known && _farViewport!.NearCloudOwnershipEnabled,
            known ? _farViewport!.FarCloudClipmapHandoffStartMeters : 0d, known ? _farViewport!.LocalCloudRadiusMeters : 0d,
            gridSettings?.FarHandoffStartKm ?? 120f, gridSettings?.FarHandoffEndKm ?? 172f);
        _handoffBand = band;
        if (band != _appliedBand) SetNearUniform("eg_handoff", band);
        if (farActive) WriteFarUniforms(fade, band);
        _appliedMax = _effectiveMax;
        _appliedFade = fade;
        _appliedBand = band;

        WriteShots(camera != null, cameraPosition, farActive);
    }

    /// <summary>Evaluates every active shot at the weather time and writes the instances: near for those inside the near range, far (ring-wide) for all of them.</summary>
    private void WriteShots(bool haveCamera, Vector3 cameraPosition, bool farActive)
    {
        int active = ActiveShots.Count;
        EnsureCapacity(active);
        var frame = new EmitterSiteFrameContext(_snapshot);
        EmitterFieldShotSchedule schedule = Schedule!;
        float maxSquared = _effectiveMax * _effectiveMax;
        EmitterFieldShotFarLayer? far = farActive ? _farLayer : null;
        far?.Begin(active);
        MultiMesh mm = _instances!.Multimesh;
        int near = 0;
        for (int i = 0; i < active; i++)
        {
            EmitterFieldShot shot = ActiveShots[i];
            EmitterFieldShotState state = schedule.Evaluate(shot, NowSeconds);
            if (state.Phase == EmitterFieldShotPhase.None) continue;
            Color custom = CustomOf(state, shot.Energy);
            Vector3 world = frame.WorldPosition(state.AroundMeters, state.VerticalMeters, state.AcrossMeters).ToVector3();
            if (!haveCamera || (world - cameraPosition).LengthSquared() < maxSquared)
            {
                _nearOrigins[near] = world;
                _nearCustom[near] = custom;
                mm.SetInstanceTransform(near, new Transform3D(Basis.Identity, world));
                mm.SetInstanceCustomData(near, custom);
                near++;
            }
            far?.Add(_farViewport!.ScaledPoint(state.AroundMeters, state.VerticalMeters, state.AcrossMeters), custom);
        }
        if (mm.VisibleInstanceCount != near) mm.VisibleInstanceCount = near;
        DrawnNear = near;
        far?.End();
        DrawnFar = far?.DrawnCount ?? 0;
    }

    /// <summary>Test mirror of a near instance written this frame: world origin and custom data (orb envelope, flare, spread, energy).</summary>
    public (Vector3 Origin, Color Custom) GetNearInstance(int index)
        => index >= 0 && index < DrawnNear ? (_nearOrigins[index], _nearCustom[index]) : (Vector3.Zero, default);

    /// <summary>Test mirror of a far instance written this frame: scaled-space origin and custom data.</summary>
    public (Vector3 Origin, Color Custom) GetFarInstance(int index) => FarLayerValid ? _farLayer!.GetInstance(index) : (Vector3.Zero, default);

    private void EnsureCapacity(int count)
    {
        if (_instances?.Multimesh is not { } mm || count <= _capacity) return;
        while (_capacity < count) _capacity *= 2;
        Array.Resize(ref _nearOrigins, _capacity);
        Array.Resize(ref _nearCustom, _capacity);
        mm.InstanceCount = _capacity;
    }

    /// <summary>Creates the near MultiMesh and its materials once (false when a shader is missing).</summary>
    private bool EnsureNear()
    {
        if (_instances != null && IsInstanceValid(_instances)) return true;
        Shader? shader = GD.Load<Shader>(NearShaderPath), core = GD.Load<Shader>(CoreShaderPath);
        if (shader == null || core == null || _grid == null)
        {
            if (!_warnedMissingShaders) { GD.PushWarning("Emitter field shot shaders are missing; the orbs are not drawn."); _warnedMissingShaders = true; }
            return false;
        }
        double ringRadius = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        _nearMaterial = CreateMaterial(shader, ringRadius);
        _coreMaterial = CreateMaterial(core, ringRadius);
        _nearMaterial.RenderPriority = HaloRenderPriority;
        _coreMaterial.RenderPriority = CoreRenderPriority;
        _capacity = InitialCapacity;
        _nearOrigins = new Vector3[_capacity];
        _nearCustom = new Color[_capacity];
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = BuildOrbMesh();
        multiMesh.InstanceCount = _capacity;
        multiMesh.VisibleInstanceCount = 0;
        _instances = new MultiMeshInstance3D
        {
            Name = "FieldShotOrbs",
            Multimesh = multiMesh,
            MaterialOverride = _nearMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            TopLevel = true,
            Visible = _shown
        };
        AddChild(_instances);
        _instances.GlobalTransform = Transform3D.Identity;
        RenderingServer.InstanceSetIgnoreCulling(_instances.GetInstance(), true);
        ApplyLook(_beams);
        return true;
    }

    private static ShaderMaterial CreateMaterial(Shader shader, double ringRadius)
    {
        var material = new ShaderMaterial { Shader = shader };
        RingAtmosphereModel.ConfigureMaterial(material, ringRadius);
        material.SetShaderParameter("world_scale", 1f);
        return material;
    }

    /// <summary>Applies the orb look (the false sun's orb settings), the depth core and the far layer switch. Only changed values reach the GPU.</summary>
    private void ApplyLook(EclipseBeamSettings beams)
    {
        _beams = beams.Sanitized();
        _lookApplied = true;
        EclipseBeamSettings b = _beams;
        if (_nearMaterial != null)
        {
            SetUniform("orb_look", new Vector2((float)b.OrbHaloMeters, (float)b.OrbEmission));
            SetUniform("orb_core", new Vector2((float)b.OrbRadiusMeters, (float)b.OrbMinPixelRadius));
            SetUniform("far_compensation", (float)b.FarCompensation);
            SetUniform("core_depth", new Vector2((float)b.CoreDepthThreshold, (float)b.CoreDepthMinPixels));
            SetUniform("day_visibility", (float)b.DayVisibility);
            _nearMaterial.NextPass = b.DepthWriteCore ? _coreMaterial : null;
        }
        if (FarLayerValid) _farLayer!.Visible = _shown && b.FarLayerEnabled;
        // Force the next frame to rewrite the layer uniforms (a far layer that was off may have missed some).
        _appliedMax = _appliedFade = -1f;
        _appliedBand = new Vector2(float.NaN, 0f);
    }

    private void SetNearUniform(StringName name, Variant value)
    {
        _nearMaterial?.SetShaderParameter(name, value);
        _coreMaterial?.SetShaderParameter(name, value);
    }

    /// <summary>Writes a uniform to the near colour pass, the near core pass and (when present) the far material.</summary>
    private void SetUniform(StringName name, Variant value)
    {
        SetNearUniform(name, value);
        if (FarLayerValid) _farLayer!.Material?.SetShaderParameter(name, value);
    }

    /// <summary>
    /// Creates the far layer and attaches it through <paramref name="attach"/> (normally <see cref="RingScaledFarViewport.AddScaledWorldChild"/>).
    /// False when the assets are missing or the attach fails.
    /// </summary>
    public bool BindFarLayer(float coordinateScale, Func<Node3D, bool> attach)
    {
        if (FarLayerValid) return true;
        if (_grid == null) return false;
        var layer = new EmitterFieldShotFarLayer { Name = "EmitterFieldShotsFar" };
        if (!layer.Build(coordinateScale, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau) || !attach(layer))
        {
            layer.Free();
            return false;
        }
        _farLayer = layer;
        layer.Visible = _shown && _beams.FarLayerEnabled;
        // Mirror the whole look onto the new far material.
        ApplyLook(_beams);
        _farAppliedNear = -1f;
        return true;
    }

    private void WriteFarUniforms(float fade, Vector2 band)
    {
        ShaderMaterial? material = _farLayer!.Material;
        if (material == null) return;
        float nearClip = _farLayer.GetViewport()?.GetCamera3D()?.Near ?? 0.1f;
        if (_effectiveMax != _appliedMax) material.SetShaderParameter("max_distance", _effectiveMax);
        if (fade != _appliedFade) material.SetShaderParameter("fade_meters", fade);
        if (nearClip != _farAppliedNear) { material.SetShaderParameter("near_clip", nearClip); _farAppliedNear = nearClip; }
        if (band != _appliedBand) material.SetShaderParameter("eg_handoff", band);
    }
}
