using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Far stand-ins for all 595 emitter POIs (docs/emitter-poi-streaming-spec.md section B): two MultiMeshes (594 regular
/// spires, 1 main spire) drawn from the proxy meshes out to a maximum distance. Instance custom data carries streamer
/// state: .r = presented (the near handoff fade band applies), .g = hard hidden. With neither set a stand-in draws at any
/// distance. Transforms are rebuilt from the floating-origin snapshot, never applied as deltas.
/// </summary>
public partial class EmitterStandInLayer : Node3D, IEmitterBeamSource
{
    public const string RegularMeshPath = "res://assets/poi/beam_emitter/merged/meshes/BeamEmitter_FarProxy.res";
    public const string MainMeshPath = "res://assets/poi/main_spire/merged/meshes/MainSpire_FarProxy.res";
    public const string ShaderPath = "res://materials/ring_emitter_standin.gdshader";
    public const float RegularNearMarginMeters = 1000f, MainNearMarginMeters = 2000f;
    public const float RegularObjectHeightMeters = 1800f, MainObjectHeightMeters = 4000f;
    public const float DefaultMaxDistanceMeters = 700_000f, DefaultFadeMeters = 100_000f, DefaultMinPixels = 1.5f;
    public const float DefaultRegularHandoffMeters = 11_500f, DefaultMainHandoffMeters = 26_000f;

    [Export] public bool Enabled { get => _enabled; set { _enabled = value; UpdateVisibility(); } }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GlobalTerrainPath { get; set; } = new("../GlobalTerrainLod3");

    private bool _enabled = true;
    private bool _undergroundHidden;
    private VoxelChunkGridManager? _grid;
    private DirectionalLight3D? _sun;
    private RingTerrainGlobalLod? _globalTerrain;
    private EmitterSitePlacementSource? _placements;
    private object? _lastSamplerSeen;
    private MultiMeshInstance3D? _regularInstances, _mainInstances;
    private ShaderMaterial? _regularMaterial, _mainMaterial;
    private int[] _instanceOfSite = Array.Empty<int>();
    private bool[] _siteIsMain = Array.Empty<bool>();
    private bool[] _hidden = Array.Empty<bool>(), _presented = Array.Empty<bool>(), _beamActive = Array.Empty<bool>();
    private Transform3D[] _mirror = Array.Empty<Transform3D>();
    private int _hiddenCount, _presentedCount;
    private Transform3D _lastGridGlobal, _lastLayerGlobal;
    private float _maxDistance = DefaultMaxDistanceMeters, _fade = DefaultFadeMeters, _minPixels = DefaultMinPixels;
    private float _regularHandoff = DefaultRegularHandoffMeters, _mainHandoff = DefaultMainHandoffMeters;
    private float _effectiveMax = DefaultMaxDistanceMeters;

    public event Action? BeamsChanged;

    public bool IsBound => _placements != null && _regularInstances != null && _mainInstances != null;
    public bool Unavailable { get; private set; }
    public bool UndergroundHold { get; set; }
    public bool UndergroundHidden => _undergroundHidden;
    public int RebuildCount { get; private set; }
    public double LastRebuildMs { get; private set; }
    public int GeometryRevision { get; private set; }
    public int RegularInstanceCount => _regularInstances?.Multimesh?.InstanceCount ?? 0;
    public int MainInstanceCount => _mainInstances?.Multimesh?.InstanceCount ?? 0;
    public MultiMeshInstance3D? RegularInstances => _regularInstances;
    public MultiMeshInstance3D? MainInstances => _mainInstances;
    public ShaderMaterial? RegularMaterial => _regularMaterial;
    public ShaderMaterial? MainMaterial => _mainMaterial;
    public float MaxDistanceMeters => _maxDistance;
    public float EffectiveMaxDistanceMeters => _effectiveMax;
    public int HiddenCount => _hiddenCount;
    public int PresentedCount => _presentedCount;
    public float RegularHandoffMeters => _regularHandoff;
    public float MainHandoffMeters => _mainHandoff;

    // IEmitterBeamSource
    public int Count => _placements?.Count ?? 0;
    public ulong Identity => _placements?.Identity ?? 0UL;
    public int MainSiteIndex => _placements?.Registry.MainIndex ?? -1;

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
        if (_grid != null) _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged;
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (_grid == null) return;
        if (!IsBound)
        {
            if (!AutoBind) return;
            object? sampler = _grid.TerrainSampler;
            if (sampler == null || ReferenceEquals(sampler, _lastSamplerSeen)) return;
            _lastSamplerSeen = sampler;
            if (_grid.TerrainSampler?.EmitterSites is { } registry) Bind(_grid, new EmitterSitePlacementSource(registry));
            else { Unavailable = true; UpdateVisibility(); }
            return;
        }
        if (!_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal) || !GlobalTransform.IsEqualApprox(_lastLayerGlobal))
            Rebuild(_grid.CaptureLodGeometrySnapshot());
        UpdateUniforms();
    }

    /// <summary>Builds the two MultiMeshes for the placements and rebuilds transforms. Returns false when the assets are missing.</summary>
    public bool Bind(VoxelChunkGridManager grid, EmitterSitePlacementSource placements)
    {
        if (IsBound) return true;
        Mesh? regularMesh = GD.Load<Mesh>(RegularMeshPath), mainMesh = GD.Load<Mesh>(MainMeshPath);
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (regularMesh == null || mainMesh == null || shader == null) { Unavailable = true; UpdateVisibility(); return false; }
        _grid = grid;
        _placements = placements;
        int count = placements.Count;
        _siteIsMain = new bool[count];
        _instanceOfSite = new int[count];
        _hidden = new bool[count]; _presented = new bool[count]; _beamActive = new bool[count];
        _mirror = new Transform3D[count];
        int regular = 0, main = 0;
        for (int site = 0; site < count; site++)
        {
            bool isMain = placements.GetPlacement(site).Kind == SiteKind.Main;
            _siteIsMain[site] = isMain;
            _instanceOfSite[site] = isMain ? main++ : regular++;
        }
        double ringRadius = grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        _regularMaterial = CreateMaterial(shader, regularMesh, RegularObjectHeightMeters, ringRadius);
        _mainMaterial = CreateMaterial(shader, mainMesh, MainObjectHeightMeters, ringRadius);
        _regularInstances = CreateMultiMeshInstance("RegularStandIns", regularMesh, regular, _regularMaterial);
        _mainInstances = CreateMultiMeshInstance("MainStandIn", mainMesh, main, _mainMaterial);
        ApplyNearUniforms();
        Unavailable = false;
        grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged;
        UpdateVisibility();
        Rebuild(grid.CaptureLodGeometrySnapshot());
        return true;
    }

    private ShaderMaterial CreateMaterial(Shader shader, Mesh mesh, float objectHeight, double ringRadius)
    {
        var material = new ShaderMaterial { Shader = shader };
        Aabb bounds = mesh.GetAabb();
        material.SetShaderParameter("object_height_m", objectHeight);
        material.SetShaderParameter("distance_pivot", bounds.Position + bounds.Size * 0.5f);
        material.SetShaderParameter("use_instance_custom", true);
        var surface = mesh.GetSurfaceCount() > 0 ? mesh.SurfaceGetMaterial(0) as BaseMaterial3D : null;
        Color albedo = surface?.AlbedoColor ?? new Color(0.18f, 0.23f, 0.28f);
        material.SetShaderParameter("albedo_color", new Vector3(albedo.R, albedo.G, albedo.B));
        material.SetShaderParameter("metallic_value", surface?.Metallic ?? 0.72f);
        material.SetShaderParameter("roughness_value", surface?.Roughness ?? 0.48f);
        RingAtmosphereModel.ConfigureMaterial(material, ringRadius);
        global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
        return material;
    }

    private MultiMeshInstance3D CreateMultiMeshInstance(string name, Mesh mesh, int instanceCount, Material material)
    {
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = mesh;
        multiMesh.InstanceCount = instanceCount;
        multiMesh.VisibleInstanceCount = instanceCount;
        for (int index = 0; index < instanceCount; index++) multiMesh.SetInstanceCustomData(index, new Color(0f, 0f, 0f, 0f));
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

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        Rebuild(current);
    }

    /// <summary>Recomputes every instance transform from <paramref name="snapshot"/> (layer-local, absolute, never a delta).</summary>
    public void Rebuild(in RingLodGeometrySnapshot snapshot)
    {
        if (_placements == null || _regularInstances?.Multimesh is not { } regular || _mainInstances?.Multimesh is not { } main) return;
        long started = Stopwatch.GetTimestamp();
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        _placements.FillWorldTransforms(snapshot, _mirror);
        for (int site = 0; site < _mirror.Length; site++)
        {
            Transform3D local = worldToLayer * _mirror[site];
            _mirror[site] = local;
            (_siteIsMain[site] ? main : regular).SetInstanceTransform(_instanceOfSite[site], local);
        }
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastLayerGlobal = GlobalTransform;
        RebuildCount++;
        GeometryRevision++;
        LastRebuildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        BeamsChanged?.Invoke();
    }

    /// <summary>Applies the tuning values; handoff distances only affect sites the streamer has marked presented.</summary>
    public void Configure(bool enabled, float maxDistanceM, float fadeM, float minPixels, float regularHandoffM, float mainHandoffM)
    {
        _maxDistance = Mathf.Max(1f, maxDistanceM);
        _fade = Mathf.Max(0f, fadeM);
        _minPixels = Mathf.Max(0.01f, minPixels);
        _regularHandoff = Mathf.Max(0f, regularHandoffM);
        _mainHandoff = Mathf.Max(0f, mainHandoffM);
        ApplyNearUniforms();
        Enabled = enabled;
    }

    private void ApplyNearUniforms()
    {
        _regularMaterial?.SetShaderParameter("near_begin", _regularHandoff);
        _regularMaterial?.SetShaderParameter("near_margin", Mathf.Min(RegularNearMarginMeters, _regularHandoff));
        _mainMaterial?.SetShaderParameter("near_begin", _mainHandoff);
        _mainMaterial?.SetShaderParameter("near_margin", Mathf.Min(MainNearMarginMeters, _mainHandoff));
    }

    /// <summary>Smallest of the requested distance, the global terrain fade end (when fading) and 98% of the camera far plane.</summary>
    public static double ComputeEffectiveMaxDistance(double requested, double? globalFadeEnd, double? cameraFar)
    {
        double result = requested;
        if (globalFadeEnd is { } fadeEnd) result = Math.Min(result, fadeEnd);
        if (cameraFar is { } far) result = Math.Min(result, 0.98 * far);
        return result;
    }

    private double CurrentEffectiveMax(Camera3D? camera)
    {
        double? fadeEnd = _globalTerrain != null && GodotObject.IsInstanceValid(_globalTerrain) && _globalTerrain.EnableFarDistanceFade
            ? _globalTerrain.FarFadeEndMeters : null;
        double? far = camera != null ? camera.Far : null;
        return ComputeEffectiveMaxDistance(_maxDistance, fadeEnd, far);
    }

    private void UpdateUniforms()
    {
        if (UndergroundHold || _grid == null) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null) return;
        _effectiveMax = (float)CurrentEffectiveMax(camera);
        float fade = Mathf.Min(_fade, 0.5f * _effectiveMax);
        Vector3 cameraPosition = camera.GlobalPosition;
        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        Vector3 sun = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        foreach (ShaderMaterial? material in new[] { _regularMaterial, _mainMaterial })
        {
            if (material == null) continue;
            material.SetShaderParameter("max_distance", _effectiveMax);
            material.SetShaderParameter("fade_meters", fade);
            material.SetShaderParameter("min_pixels", _minPixels);
            material.SetShaderParameter("camera_world_position", cameraPosition);
            material.SetShaderParameter("ring_center", ringCenter);
            material.SetShaderParameter("sun_direction", sun);
        }
    }

    public void SetUndergroundHidden(bool hidden)
    {
        if (_undergroundHidden == hidden) return;
        _undergroundHidden = hidden;
        UpdateVisibility();
    }

    private void UpdateVisibility() => Visible = _enabled && IsBound && !_undergroundHidden;

    // Streamer-facing per-site state (instance custom data). Out-of-range sites are ignored.
    private bool ValidSite(int site) => IsBound && site >= 0 && site < _mirror.Length;

    private void WriteCustomData(int site)
    {
        MultiMesh? target = (_siteIsMain[site] ? _mainInstances : _regularInstances)?.Multimesh;
        target?.SetInstanceCustomData(_instanceOfSite[site], new Color(_presented[site] ? 1f : 0f, _hidden[site] ? 1f : 0f, 0f, 0f));
    }

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

    public void ClearHidden()
    {
        if (_hiddenCount == 0) return;
        for (int site = 0; site < _hidden.Length; site++)
        {
            if (!_hidden[site]) continue;
            _hidden[site] = false;
            WriteCustomData(site);
        }
        _hiddenCount = 0;
    }

    /// <summary>Layer-local transform last written to the MultiMesh for the site (CPU mirror).</summary>
    public Transform3D GetInstanceTransform(int site) => ValidSite(site) ? _mirror[site] : Transform3D.Identity;

    /// <summary>Reads the instance transform back from the MultiMesh (may be unavailable under the headless dummy renderer).</summary>
    public Transform3D ReadbackInstanceTransform(int site)
        => ValidSite(site) ? (_siteIsMain[site] ? _mainInstances : _regularInstances)!.Multimesh.GetInstanceTransform(_instanceOfSite[site]) : Transform3D.Identity;

    public Color ReadbackInstanceCustomData(int site)
        => ValidSite(site) ? (_siteIsMain[site] ? _mainInstances : _regularInstances)!.Multimesh.GetInstanceCustomData(_instanceOfSite[site]) : default;

    /// <summary>CPU estimate of how many stand-ins currently draw (not hidden, inside the max distance and the presented band).</summary>
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
            if (_presented[site])
            {
                float begin = _siteIsMain[site] ? _mainHandoff : _regularHandoff;
                float margin = _siteIsMain[site] ? MainNearMarginMeters : RegularNearMarginMeters;
                if (distance < begin - margin) continue;
            }
            visible++;
        }
        return visible;
    }

    public string DescribeStatus()
    {
        if (!IsBound) return Unavailable ? "Emitter stand-ins unavailable (needs the v5 sites world)." : "Emitter stand-ins waiting for the terrain sampler.";
        Camera3D? camera = IsInsideTree() ? GetViewport()?.GetCamera3D() : null;
        double max = CurrentEffectiveMax(camera);
        return string.Format(CultureInfo.InvariantCulture,
            "Stand-ins {0}+{1}={2}, visible~{3}, effective max {4:0} km, hidden {5}, presented {6}, rebuilds {7} ({8:0.00} ms)",
            RegularInstanceCount, MainInstanceCount, Count, EstimateVisibleCount(), max / 1000d, _hiddenCount, _presentedCount, RebuildCount, LastRebuildMs);
    }

    // IEmitterBeamSource
    public bool TryGetBeam(int site, out EmitterBeamState beam)
    {
        beam = default;
        if (!ValidSite(site) || _grid == null) return false;
        EmitterBeamGeometry geometry = _placements!.GetBeamGeometry(_grid.CaptureLodGeometrySnapshot(), site);
        beam = new EmitterBeamState(site, _siteIsMain[site] ? SiteKind.Main : SiteKind.Regular, geometry, _beamActive[site]);
        return true;
    }

    public bool IsBeamActive(int site) => ValidSite(site) && _beamActive[site];

    public void SetBeamActive(int site, bool active)
    {
        if (!ValidSite(site) || _beamActive[site] == active) return;
        _beamActive[site] = active;
        BeamsChanged?.Invoke();
    }

    public void SetAllBeamsActive(bool active)
    {
        bool changed = false;
        for (int site = 0; site < _beamActive.Length; site++)
        {
            if (_beamActive[site] == active) continue;
            _beamActive[site] = active;
            changed = true;
        }
        if (changed) BeamsChanged?.Invoke();
    }
}
