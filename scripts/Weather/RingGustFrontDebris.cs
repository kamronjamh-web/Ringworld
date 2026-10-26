using System;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Gust front debris (docs/storm-revamp-s3-storm-arrival-design.md WO-D1): leaves in forest and grassland, dust on bare and dry ground, spray over water, blown by the
/// outflow of an arriving storm. Two stateless MultiMeshes (leaf quads, soft puffs) that follow the camera; every particle is a hashed lattice slot carried by the wind
/// (materials/ring_gust_debris_common.gdshaderinc), so this node only publishes a handful of uniforms per frame and costs nothing when there is no gust (both renderers are hidden).
/// The count is a graphics preset budget (<see cref="PresetBudget"/>, RingGraphicsQualityController.PresetWeather), never a menu setting. Both renderers sit on the
/// NoPrecipitationOcclusion layer so debris never shelters rain. Gust front strength 0 turns it off.
/// </summary>
public partial class RingGustFrontDebris : Node3D
{
    [Export] public NodePath WeatherLayerPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public bool AutoBind { get; set; } = true;

    /// <summary>Particle budget of the active graphics preset (Low 128, Medium 256, High 512, Very High 768, Ultra 1024). Three quarters are leaf quads, the rest puffs.</summary>
    public static int PresetBudget { get; set; } = 512;
    /// <summary>Capture and test hook: forces the gust level (0..1) instead of reading the observer's arrival state. Negative = off.</summary>
    public float ForcedLevel { get; set; } = -1.0f;
    /// <summary>Capture and test hook: forces the biome shares (leaf, dust, spray) for every cell. Null = sample the terrain under the observer.</summary>
    public Vector3? ForcedShares { get; set; }

    public const float DomainMeters = 128.0f;
    private const int Cells = 9;
    private const float CellSpacing = DomainMeters / 3.0f;

    private RingVolumetricCloudLayer? _layer;
    private DirectionalLight3D? _sun;
    private VoxelChunkGridManager? _grid;
    private RingLocalWeatherEffects? _weather;
    private MultiMeshInstance3D? _leaves, _puffs;
    private ShaderMaterial? _leafMaterial, _puffMaterial;
    private int _leafCount, _puffCount;
    private Vector2 _drift;
    private double _time;
    private float _level, _smoothedLevel;
    private readonly Vector4[] _kinds = new Vector4[Cells];
    private readonly Vector4[] _kindTargets = new Vector4[Cells];
    private readonly Godot.Collections.Array _kindsVariant = new();
    private int _sampleCursor;
    private bool _kindsSeeded;
    private bool _registered;
    private double _ringRadius;

    public float Level => _level;
    public bool Active => _leaves != null && _leaves.Visible;
    public int LeafInstances => _leafCount;
    public int PuffInstances => _puffCount;
    public Vector3[] LastShares { get; } = new Vector3[Cells];

    public override void _Ready()
    {
        if (AutoBind)
        {
            _layer = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherLayerPath);
            _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
            _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
            _weather = GetNodeOrNull<RingLocalWeatherEffects>(WeatherEffectsPath);
        }
        TopLevel = true;
        AddToGroup(RingRenderLayers.NoPrecipitationOcclusionGroup);
        BuildRenderers();
    }

    public void Bind(RingVolumetricCloudLayer? layer, DirectionalLight3D? sun, VoxelChunkGridManager? grid, RingLocalWeatherEffects? weather)
    { _layer = layer; _sun = sun; _grid = grid; _weather = weather; }

    private void BuildRenderers()
    {
        var leafShader = GD.Load<Shader>("res://materials/ring_gust_debris.gdshader");
        var puffShader = GD.Load<Shader>("res://materials/ring_gust_debris_puff.gdshader");
        if (leafShader == null || puffShader == null) return;
        _leafMaterial = new ShaderMaterial { Shader = leafShader };
        _puffMaterial = new ShaderMaterial { Shader = puffShader, RenderPriority = 2 };
        _leaves = MakeRenderer("Leaves", new QuadMesh { Size = Vector2.One }, _leafMaterial);
        _puffs = MakeRenderer("Puffs", new QuadMesh { Size = Vector2.One }, _puffMaterial);
        AddChild(_leaves); AddChild(_puffs);
        _leaves.Visible = false; _puffs.Visible = false;
        for (int i = 0; i < Cells; i++) _kindsVariant.Add(Vector4.Zero);
    }

    private static MultiMeshInstance3D MakeRenderer(string name, Mesh mesh, Material material)
    {
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, CustomAabb = new Aabb(new Vector3(-90.0f, -90.0f, -90.0f), new Vector3(180.0f, 180.0f, 180.0f)) };
        var instance = new MultiMeshInstance3D { Name = name, Multimesh = multi, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Layers = RingRenderLayers.NoPrecipitationOcclusion };
        return instance;
    }

    private static void ResizePool(MultiMeshInstance3D? renderer, ref int current, int wanted)
    {
        if (renderer == null || current == wanted) return;
        MultiMesh multi = renderer.Multimesh;
        multi.InstanceCount = 0;
        multi.InstanceCount = wanted;
        // MODEL_MATRIX of the shader is node x instance: every instance must be the identity, the shaders place the particle themselves.
        for (int i = 0; i < wanted; i++) multi.SetInstanceTransform(i, Transform3D.Identity);
        current = wanted;
    }

    /// <summary>Gust level (0..1) at the observer: the front itself and the sustained outflow behind it, scaled by Gust front strength.</summary>
    public static float GustLevel(in StormArrivalState arrival, float strength)
    {
        if (!arrival.Valid || strength <= 0.0f) return 0.0f;
        float outflow = 0.55f * arrival.Outflow * (1.0f - 0.7f * Mathf.Clamp(arrival.Trailing, 0.0f, 1.0f));
        return Mathf.Clamp(Mathf.Max(arrival.GustFront, outflow) * Mathf.Min(strength, 1.0f), 0.0f, 1.0f);
    }

    /// <summary>Leaf, dust and spray share of one terrain sample (the biome rule of the design: leaves in forest and grassland, dust on bare and dry ground, spray over water).</summary>
    public static Vector3 BiomeShares(TerrainBiomeId biome, bool surfaceWater)
    {
        if (surfaceWater || biome is TerrainBiomeId.Ocean or TerrainBiomeId.Lake or TerrainBiomeId.River) return new Vector3(0.0f, 0.0f, 1.0f);
        return biome switch
        {
            TerrainBiomeId.Forest => new Vector3(1.0f, 0.0f, 0.0f),
            TerrainBiomeId.Grassland => new Vector3(0.6f, 0.25f, 0.0f),
            TerrainBiomeId.Desert => new Vector3(0.0f, 1.0f, 0.0f),
            TerrainBiomeId.Tundra => new Vector3(0.1f, 0.5f, 0.0f),
            TerrainBiomeId.Alpine => new Vector3(0.05f, 0.6f, 0.0f),
            _ => new Vector3(0.2f, 0.4f, 0.0f)
        };
    }

    /// <summary>Dust is suppressed once the ground is wet (recent rain, fat drops, core); leaves lose half their share in the core.</summary>
    public static Vector2 WetScales(in StormArrivalState arrival)
    {
        float wet = Mathf.Max(Mathf.Clamp(arrival.FatDrop, 0.0f, 1.0f), Mathf.Clamp(arrival.Core, 0.0f, 1.0f));
        float dust = (1.0f - Mathf.SmoothStep(0.03f, 0.2f, arrival.RecentRainAmount)) * (1.0f - wet);
        float leaf = 1.0f - 0.5f * Mathf.Clamp(arrival.Core, 0.0f, 1.0f);
        return new Vector2(leaf, dust);
    }

    public override void _Process(double delta)
    {
        if (_leaves == null || _puffs == null || _leafMaterial == null || _puffMaterial == null) return;
        StormArrivalState arrival = StormArrivalMath.Observer;
        float strength = Mathf.Clamp(RingStormArrivalSettings.Current.GustFrontStrength, 0.0f, 2.0f);
        _level = ForcedLevel >= 0.0f ? Mathf.Clamp(ForcedLevel, 0.0f, 1.0f) : GustLevel(arrival, strength);
        // Fast rise, slower fall: the front hits at once, the debris thins out as the rain arrives.
        float rate = _level > _smoothedLevel ? 6.0f : 1.2f;
        _smoothedLevel = Mathf.Lerp(_smoothedLevel, _level, 1.0f - Mathf.Exp(-rate * (float)delta));
        int budget = Math.Clamp(PresetBudget, 16, 4096);
        bool show = _smoothedLevel > 0.012f && _grid != null && _layer != null && budget > 0;
        if (!show) { if (_leaves.Visible) { _leaves.Visible = false; _puffs.Visible = false; } return; }
        if (!_layer!.TryGetObserverCanonicalPosition(out RingCanonicalPosition observer)) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null) return;
        if (!_registered && _grid != null)
        {
            _ringRadius = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
            RingPoiHaze.RegisterMaterial(_leafMaterial, _ringRadius);
            RingPoiHaze.RegisterMaterial(_puffMaterial, _ringRadius);
            RingCloudShadowProjector.Register(_leafMaterial);
            _registered = true;
        }
        RingPoiHaze.Tick(this, _grid, _sun);
        int leafCount = budget * 3 / 4;
        ResizePool(_leaves, ref _leafCount, leafCount);
        ResizePool(_puffs, ref _puffCount, budget - leafCount);
        _leaves.Visible = true; _puffs.Visible = true;

        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(observer.AroundMeters, observer.VerticalMeters, observer.AcrossMeters);
        Vector3 around = (geometry.CanonicalToWorld(observer.AroundMeters + 10.0, observer.VerticalMeters, observer.AcrossMeters) - origin).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(observer.AroundMeters, observer.VerticalMeters, observer.AcrossMeters + 10.0) - origin).Normalized();
        Vector3 up = (geometry.CanonicalToWorld(observer.AroundMeters, observer.VerticalMeters + 10.0, observer.AcrossMeters) - origin).Normalized();
        Vector2 wind = _weather != null && _weather.HasCurrentWindSnapshot && _weather.CurrentWindSnapshot.Enabled && _weather.CurrentWindSnapshot.InstantWind.IsFinite()
            ? _weather.CurrentWindSnapshot.InstantWind : Vector2.Zero;
        // Debris travels with the wind it sees, slightly slower than the air.
        _drift += wind * (float)delta * 0.85f;
        _drift = new Vector2(Mathf.PosMod(_drift.X, DomainMeters), Mathf.PosMod(_drift.Y, DomainMeters));
        _time = (_time + delta) % 3600.0;
        UpdateKinds(observer, arrival, (float)delta);

        Vector3 cameraPosition = camera.GlobalPosition;
        _leaves.GlobalTransform = new Transform3D(Basis.Identity, cameraPosition);
        _puffs.GlobalTransform = new Transform3D(Basis.Identity, cameraPosition);
        Vector2 cameraAc = new((float)Mod(observer.AroundMeters, DomainMeters), (float)Mod(observer.AcrossMeters, DomainMeters));
        foreach (ShaderMaterial material in new[] { _leafMaterial, _puffMaterial })
        {
            material.SetShaderParameter("deb_around", around);
            material.SetShaderParameter("deb_across", across);
            material.SetShaderParameter("deb_up", up);
            material.SetShaderParameter("deb_cam_ac", cameraAc);
            material.SetShaderParameter("deb_drift", _drift);
            material.SetShaderParameter("deb_wind", wind);
            material.SetShaderParameter("deb_time", (float)_time);
            material.SetShaderParameter("deb_level", _smoothedLevel);
            material.SetShaderParameter("deb_kinds", _kindsVariant);
        }
        PublishLight(observer);
    }

    private static double Mod(double value, double modulus) { double r = value % modulus; return r < 0.0 ? r + modulus : r; }

    private void PublishLight(RingCanonicalPosition observer)
    {
        if (_puffMaterial == null) return;
        Color sunColor = _sun != null && GodotObject.IsInstanceValid(_sun) ? _sun.LightColor.SrgbToLinear() : new Color(1.0f, 0.96f, 0.9f);
        float energy = _sun != null && GodotObject.IsInstanceValid(_sun) && _sun.Visible ? Mathf.Clamp(_sun.LightEnergy, 0.0f, 2.0f) : 0.0f;
        _puffMaterial.SetShaderParameter("deb_sun_color", new Vector3(sunColor.R, sunColor.G, sunColor.B) * (energy * 0.30f));
        _puffMaterial.SetShaderParameter("deb_sky_color", new Vector3(0.36f, 0.44f, 0.56f) * 0.9f);
    }

    /// <summary>Samples one of the nine biome cells per frame, applies the wet scales and eases the published shares toward the target (no pop when the biome changes).</summary>
    private void UpdateKinds(RingCanonicalPosition observer, in StormArrivalState arrival, float delta)
    {
        Vector2 wet = WetScales(arrival);
        if (ForcedShares.HasValue) for (int i = 0; i < Cells; i++) _kindTargets[i] = new Vector4(ForcedShares.Value.X, ForcedShares.Value.Y, ForcedShares.Value.Z, 0.0f);
        else if (_grid?.TerrainSampler != null)
        {
            int count = _kindsSeeded ? 1 : Cells;
            for (int n = 0; n < count; n++)
            {
                int cell = _sampleCursor; _sampleCursor = (_sampleCursor + 1) % Cells;
                int cx = cell % 3, cy = cell / 3;
                double around = observer.AroundMeters + (cx - 1) * CellSpacing, across = observer.AcrossMeters + (cy - 1) * CellSpacing;
                TerrainSample sample = _grid.TerrainSampler.GetTerrainSample((long)Math.Floor(around), Mathf.FloorToInt((float)across));
                Vector3 shares = BiomeShares(sample.Biome, sample.HasSurfaceWater);
                LastShares[cell] = shares;
                _kindTargets[cell] = new Vector4(shares.X, shares.Y, shares.Z, 0.0f);
            }
        }
        float ease = _kindsSeeded ? 1.0f - Mathf.Exp(-1.5f * delta) : 1.0f;
        for (int i = 0; i < Cells; i++)
        {
            Vector4 target = _kindTargets[i];
            target = new Vector4(target.X * wet.X, target.Y * wet.Y, target.Z, 0.0f);
            _kinds[i] = _kinds[i].Lerp(target, ease);
            _kindsVariant[i] = _kinds[i];
        }
        _kindsSeeded = true;
    }

    public Godot.Collections.Dictionary GetDiagnostics() => new()
    {
        ["active"] = Active, ["level"] = _level, ["smoothed_level"] = _smoothedLevel, ["budget"] = PresetBudget,
        ["leaf_instances"] = _leafCount, ["puff_instances"] = _puffCount,
        ["leaf_layers"] = _leaves?.Layers ?? 0u, ["puff_layers"] = _puffs?.Layers ?? 0u,
        ["center_shares"] = _kinds[4], ["drift"] = _drift
    };
}
