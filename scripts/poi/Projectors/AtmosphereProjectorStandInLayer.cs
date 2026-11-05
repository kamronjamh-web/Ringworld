using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Far stand-ins for the 252 atmosphere-shield projector towers (docs/containment-field-design.md 6.1, 6.2): one MultiMesh of the FarProxy mesh on the
/// emitter stand-in shader (<see cref="EmitterStandInLayer"/>'s material set-up, plus its opt-in minimum on-screen width so the 80 m wide towers stay about a
/// pixel wide far away). Instance custom data: .r = presented by the streamer (near handoff fade band applies), .g = hard hidden. Transforms are absolute
/// from the floating-origin snapshot, never deltas.
/// </summary>
public partial class AtmosphereProjectorStandInLayer : Node3D
{
    public const string MeshPath = "res://assets/poi/atmosphere_projector/merged/meshes/AtmosphereProjector_FarProxy.res";
    public const string ShaderPath = EmitterStandInLayer.ShaderPath;
    // 2 px with no width dither (energy floor 1): a narrower or dithered sub-pixel spike is erased by TAA / FSR jitter and neighbourhood clamping
    // (measured: an 80 m tower vanished beyond about 22 km at 1 px with the 0.35 floor), so far towers read about 2 px wide.
    public const float ObjectHeightMeters = 1600f, ObjectWidthMeters = 80f, MinWidthPixels = 2f, WidthEnergyFloor = 1f;
    public const float DefaultHandoffMeters = 14_000f, NearMarginMeters = 1_000f;
    public const float DefaultMaxDistanceMeters = 700_000f, DefaultFadeMeters = 40_000f, DefaultMinPixels = 1.5f;

    [Export] public bool Enabled { get => _enabled; set { _enabled = value; UpdateVisibility(); } }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GlobalTerrainPath { get; set; } = new("../GlobalTerrainLod3");

    private bool _enabled = true;
    private VoxelChunkGridManager? _grid;
    private DirectionalLight3D? _sun;
    private RingTerrainGlobalLod? _globalTerrain;
    private AtmosphereProjectorSites? _sites;
    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _material;
    private bool[] _hidden = Array.Empty<bool>(), _presented = Array.Empty<bool>();
    private Transform3D[] _mirror = Array.Empty<Transform3D>();
    private int _hiddenCount, _presentedCount;
    private Transform3D _lastGridGlobal, _lastLayerGlobal;
    private float _handoff = DefaultHandoffMeters, _maxDistance = DefaultMaxDistanceMeters, _fade = DefaultFadeMeters, _minPixels = DefaultMinPixels;
    private float _effectiveMax = DefaultMaxDistanceMeters;
    private bool _subscribed;

    public bool IsBound => _sites != null && _instances != null;
    public bool Unavailable { get; private set; }
    public AtmosphereProjectorSites? Sites => _sites;
    public int InstanceCount => _instances?.Multimesh?.InstanceCount ?? 0;
    public ShaderMaterial? Material => _material;
    public float HandoffMeters => _handoff;
    public float EffectiveMaxDistanceMeters => _effectiveMax;
    public int RebuildCount { get; private set; }
    public double LastRebuildMs { get; private set; }
    public int PresentedCount => _presentedCount;
    public int HiddenCount => _hiddenCount;

    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _globalTerrain = GetNodeOrNull<RingTerrainGlobalLod>(GlobalTerrainPath);
        UpdateVisibility();
        if (!AutoBind) SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (_grid != null && _subscribed) { _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged; _subscribed = false; }
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (_grid == null) return;
        if (!IsBound)
        {
            if (AutoBind && GridReady(_grid)) Bind(_grid);
            return;
        }
        if (!_sites!.Matches(_grid.GetCanonicalRingCircumferenceMeters64(), _grid.RingAcrossWidthMeters, _grid.GetStructuralWallTopGlobalY(), RimWallDescription.Current)
            || !_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal) || !GlobalTransform.IsEqualApprox(_lastLayerGlobal))
            Rebuild(_grid.CaptureLodGeometrySnapshot());
        RingPoiHaze.Tick(this, _grid, _sun);
        UpdateUniforms();
    }

    private static bool GridReady(VoxelChunkGridManager grid) => grid.GetCanonicalRingCircumferenceMeters64() > 0d && grid.RingAcrossWidthMeters > 0f;

    /// <summary>Builds the MultiMesh and the sites for the grid's current geometry. Returns false when the assets are missing or the grid has no ring geometry yet.</summary>
    public bool Bind(VoxelChunkGridManager grid)
    {
        if (IsBound) return true;
        if (!GridReady(grid)) return false;
        Mesh? mesh = GD.Load<Mesh>(MeshPath);
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (mesh == null || shader == null) { Unavailable = true; UpdateVisibility(); return false; }
        _grid = grid;
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        _sites = AtmosphereProjectorSites.Build(snapshot, grid.RingAcrossWidthMeters, RimWallDescription.Current);
        int count = _sites.Count;
        _hidden = new bool[count];
        _presented = new bool[count];
        _mirror = new Transform3D[count];
        _hiddenCount = _presentedCount = 0;
        _material = CreateMaterial(shader, mesh, grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        _instances = CreateMultiMeshInstance(mesh, count, _material);
        ApplyNearUniforms();
        Unavailable = false;
        if (!_subscribed) { grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged; _subscribed = true; }
        UpdateVisibility();
        Rebuild(snapshot);
        return true;
    }

    private static ShaderMaterial CreateMaterial(Shader shader, Mesh mesh, double ringRadius)
    {
        var material = new ShaderMaterial { Shader = shader };
        Aabb bounds = mesh.GetAabb();
        material.SetShaderParameter("object_height_m", ObjectHeightMeters);
        material.SetShaderParameter("distance_pivot", bounds.Position + bounds.Size * 0.5f);
        material.SetShaderParameter("use_instance_custom", true);
        material.SetShaderParameter("object_width_m", ObjectWidthMeters);
        material.SetShaderParameter("min_width_pixels", MinWidthPixels);
        material.SetShaderParameter("width_energy_floor", WidthEnergyFloor);
        Color albedo = new Color(0.18f, 0.23f, 0.28f);
        float metallic = 0.72f, roughness = 0.48f;
        if (mesh.GetSurfaceCount() > 0 && PoiMaterialParams.TryGetSurface(mesh.SurfaceGetMaterial(0), out Color surfaceAlbedo, out float surfaceMetallic, out float surfaceRoughness, out _))
        {
            albedo = surfaceAlbedo; metallic = surfaceMetallic; roughness = surfaceRoughness;
        }
        material.SetShaderParameter("albedo_color", new Vector3(albedo.R, albedo.G, albedo.B));
        material.SetShaderParameter("metallic_value", metallic);
        material.SetShaderParameter("roughness_value", roughness);
        RingPoiHaze.RegisterMaterial(material, ringRadius);
        global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
        return material;
    }

    private MultiMeshInstance3D CreateMultiMeshInstance(Mesh mesh, int instanceCount, Material material)
    {
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = mesh;
        multiMesh.InstanceCount = instanceCount;
        multiMesh.VisibleInstanceCount = instanceCount;
        for (int index = 0; index < instanceCount; index++) multiMesh.SetInstanceCustomData(index, new Color(0f, 0f, 0f, 0f));
        var instance = new MultiMeshInstance3D
        {
            Name = "ProjectorStandIns",
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

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        Rebuild(current);
        RingPoiHaze.Tick(this, _grid, _sun, true);
    }

    /// <summary>Rebuilds the sites when the grid geometry changed, then every instance transform from <paramref name="snapshot"/> (layer-local, absolute). Presented / hidden flags are kept.</summary>
    public void Rebuild(in RingLodGeometrySnapshot snapshot)
    {
        if (_grid == null || _instances?.Multimesh is not { } multiMesh) return;
        long started = Stopwatch.GetTimestamp();
        if (_sites == null || !_sites.Matches(snapshot.CircumferenceMeters, _grid.RingAcrossWidthMeters, snapshot.StructuralWallTopGlobalY, RimWallDescription.Current))
        {
            _sites = AtmosphereProjectorSites.Build(snapshot, _grid.RingAcrossWidthMeters, RimWallDescription.Current);
            if (_sites.Count != _mirror.Length) return;  // the count is the constant 2 * cue lines; never reallocates in practice
        }
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        _sites.FillWorldTransforms(snapshot, _mirror);
        for (int site = 0; site < _mirror.Length; site++)
        {
            Transform3D local = worldToLayer * _mirror[site];
            _mirror[site] = local;
            multiMesh.SetInstanceTransform(site, local);
        }
        if (_grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastLayerGlobal = GlobalTransform;
        RebuildCount++;
        LastRebuildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void ApplyNearUniforms()
    {
        _material?.SetShaderParameter("near_begin", _handoff);
        _material?.SetShaderParameter("near_margin", Mathf.Min(NearMarginMeters, _handoff));
    }

    private double CurrentEffectiveMax(Camera3D? camera)
    {
        double? fadeEnd = _globalTerrain != null && GodotObject.IsInstanceValid(_globalTerrain) && _globalTerrain.EnableFarDistanceFade
            ? _globalTerrain.FarFadeEndMeters : null;
        double? far = camera != null ? camera.Far : null;
        return EmitterStandInLayer.ComputeEffectiveMaxDistance(_maxDistance, fadeEnd, far);
    }

    private void UpdateUniforms()
    {
        if (_grid == null || _material == null) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null) return;
        _effectiveMax = (float)CurrentEffectiveMax(camera);
        float fade = Mathf.Min(_fade, 0.5f * _effectiveMax);
        Vector3 sun = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        RingPoiHaze.PoiLighting lighting = RingPoiHaze.PoiLighting.FromGrid(_grid);
        ShaderMaterial material = _material;
        _maxDistanceGate.Set(material, MaxDistanceName, _effectiveMax);
        _fadeGate.Set(material, FadeMetersName, fade);
        _minPixelsGate.Set(material, MinPixelsName, _minPixels);
        _cameraGate.Set(material, CameraWorldPositionName, camera.GlobalPosition);
        _ringCenterGate.Set(material, RingCenterName, _grid.GetCanonicalRingCenterWorld());
        _sunGate.Set(material, SunDirectionName, sun);
        _dayStartGate.Set(material, DayStartName, lighting.DayStart);
        _dayFullGate.Set(material, DayFullName, lighting.DayFull);
        _nightTintGate.Set(material, NightTintName, lighting.NightTint);
        _nightDirectGate.Set(material, NightDirectName, lighting.NightDirect);
        _dayDirectGate.Set(material, DayDirectName, lighting.DayDirect);
    }

    // Per-material change gates: an unchanged value must not dirty the material.
    private static readonly StringName MaxDistanceName = "max_distance", FadeMetersName = "fade_meters", MinPixelsName = "min_pixels",
        CameraWorldPositionName = "camera_world_position", RingCenterName = "ring_center", SunDirectionName = "sun_direction";
    private static readonly StringName DayStartName = "day_start_dot", DayFullName = "day_full_dot", NightTintName = "night_tint",
        NightDirectName = "night_direct_light_factor", DayDirectName = "day_direct_light_factor";
    private readonly EmitterUniformGate<float> _maxDistanceGate = new(RingMaterialWriteCounters.Site.EmitterStandIn),
        _fadeGate = new(RingMaterialWriteCounters.Site.EmitterStandIn), _minPixelsGate = new(RingMaterialWriteCounters.Site.EmitterStandIn),
        _dayStartGate = new(RingMaterialWriteCounters.Site.EmitterStandIn), _dayFullGate = new(RingMaterialWriteCounters.Site.EmitterStandIn),
        _nightDirectGate = new(RingMaterialWriteCounters.Site.EmitterStandIn), _dayDirectGate = new(RingMaterialWriteCounters.Site.EmitterStandIn);
    private readonly EmitterUniformGate<Vector3> _cameraGate = new(RingMaterialWriteCounters.Site.EmitterStandIn),
        _ringCenterGate = new(RingMaterialWriteCounters.Site.EmitterStandIn), _sunGate = new(RingMaterialWriteCounters.Site.EmitterStandIn),
        _nightTintGate = new(RingMaterialWriteCounters.Site.EmitterStandIn);

    private void UpdateVisibility() => Visible = _enabled && IsBound;

    // Streamer-facing per-site state (instance custom data). Out-of-range sites are ignored.
    private bool ValidSite(int site) => IsBound && site >= 0 && site < _mirror.Length;

    private void WriteCustomData(int site)
        => _instances?.Multimesh?.SetInstanceCustomData(site, new Color(_presented[site] ? 1f : 0f, _hidden[site] ? 1f : 0f, 0f, 0f));

    public void SetSitePresented(int site, bool presented)
    {
        if (!ValidSite(site) || _presented[site] == presented) return;
        _presented[site] = presented;
        _presentedCount += presented ? 1 : -1;
        WriteCustomData(site);
    }

    public bool IsSitePresented(int site) => ValidSite(site) && _presented[site];

    public void SetSiteHidden(int site, bool hidden)
    {
        if (!ValidSite(site) || _hidden[site] == hidden) return;
        _hidden[site] = hidden;
        _hiddenCount += hidden ? 1 : -1;
        WriteCustomData(site);
    }

    public bool IsSiteHidden(int site) => ValidSite(site) && _hidden[site];

    /// <summary>Layer-local transform last written to the MultiMesh for the site (CPU mirror).</summary>
    public Transform3D GetInstanceTransform(int site) => ValidSite(site) ? _mirror[site] : Transform3D.Identity;

    /// <summary>CPU estimate of how many stand-ins currently draw (not hidden, inside the max distance and outside the presented band).</summary>
    public int EstimateVisibleCount()
    {
        Camera3D? camera = IsInsideTree() ? GetViewport()?.GetCamera3D() : null;
        if (!IsBound || !Visible || camera == null) return 0;
        Vector3 cameraPosition = camera.GlobalPosition;
        float max = (float)CurrentEffectiveMax(camera);
        int visible = 0;
        for (int site = 0; site < _mirror.Length; site++)
        {
            if (_hidden[site]) continue;
            float distance = cameraPosition.DistanceTo(GlobalTransform * _mirror[site].Origin);
            if (distance > max) continue;
            if (_presented[site] && distance < _handoff - Mathf.Min(NearMarginMeters, _handoff)) continue;
            visible++;
        }
        return visible;
    }

    public string DescribeStatus()
    {
        if (!IsBound) return Unavailable ? "Projector stand-ins unavailable (missing assets)." : "Projector stand-ins waiting for the ring geometry.";
        Camera3D? camera = IsInsideTree() ? GetViewport()?.GetCamera3D() : null;
        double max = CurrentEffectiveMax(camera);
        return string.Format(CultureInfo.InvariantCulture,
            "Projector stand-ins {0}, visible~{1}, effective max {2:0} km, presented {3}, rebuilds {4} ({5:0.00} ms)",
            InstanceCount, EstimateVisibleCount(), max / 1000d, _presentedCount, RebuildCount, LastRebuildMs);
    }
}
