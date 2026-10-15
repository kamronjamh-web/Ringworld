using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Deterministic, sparse landmark towers that give the eye known-size objects
/// between local terrain and the whole-ring background. Three MultiMeshes keep
/// the layer inexpensive and avoid adding long-range shadow casters.
/// </summary>
public partial class RingScaleAnchorLayer : Node3D
{
	private readonly record struct LandmarkBuildRequest(
		long AroundCell,
		long AcrossCell,
		RingCanonicalPosition Focus,
		RingLodGeometrySnapshot Geometry,
		Transform3D WorldToLayer,
		double CircumferenceMeters,
		double RingWidthMeters,
		float CellSizeMeters,
		int AroundRadius,
		int AcrossRadius,
		int Maximum,
		float SpawnChance,
		float MajorChance,
		float MinorHeight,
		float MajorHeight,
		float MinorWidth,
		float MajorWidth,
		float FoundationDiameter,
		float BeaconDiameter,
		RingTerrainSampler Sampler);

	private readonly record struct LandmarkBuildResult(
		List<Transform3D> Foundations,
		List<Transform3D> Towers,
		List<Transform3D> Beacons);
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");

    [ExportGroup("Distribution")]
    [Export(PropertyHint.Range, "8000,100000,1000")]
    public float AnchorCellSizeMeters { get; set; } = 24000.0f;
    [Export(PropertyHint.Range, "10000,400000,5000")]
    public float VisibilityRadiusAroundMeters { get; set; } = 180000.0f;
    [Export(PropertyHint.Range, "10000,400000,5000")]
    public float VisibilityRadiusAcrossMeters { get; set; } = 160000.0f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SpawnChance { get; set; } = 0.19f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MajorLandmarkChance { get; set; } = 0.13f;
    [Export(PropertyHint.Range, "1,256,1")]
    public int MaximumVisibleLandmarks { get; set; } = 96;

    [ExportGroup("Recognizable dimensions")]
    [Export(PropertyHint.Range, "50,1000,10")]
    public float MinorTowerHeightMeters { get; set; } = 360.0f;
    [Export(PropertyHint.Range, "500,5000,50")]
    public float MajorTowerHeightMeters { get; set; } = 1600.0f;
    [Export(PropertyHint.Range, "10,300,5")]
    public float MinorTowerWidthMeters { get; set; } = 46.0f;
    [Export(PropertyHint.Range, "50,600,10")]
    public float MajorTowerWidthMeters { get; set; } = 150.0f;
    [Export(PropertyHint.Range, "50,1000,10")]
    public float FoundationDiameterMeters { get; set; } = 260.0f;
    [Export(PropertyHint.Range, "2,100,1")]
    public float BeaconDiameterMeters { get; set; } = 24.0f;

    [ExportGroup("Appearance")]
    [Export] public Color FoundationColor { get; set; } = new(0.10f, 0.14f, 0.18f, 1.0f);
    [Export] public Color TowerColor { get; set; } = new(0.22f, 0.29f, 0.34f, 1.0f);
    [Export] public Color BeaconColor { get; set; } = new(0.08f, 0.78f, 0.92f, 1.0f);
    [Export(PropertyHint.Range, "0,8,0.05")]
    public float BeaconEmissionEnergy { get; set; } = 2.4f;
    [Export(PropertyHint.Range, "1000,200000,1000")]
    public float BeaconEmissionFadeStartMeters { get; set; } = 30000.0f;
    [Export(PropertyHint.Range, "2000,300000,1000")]
    public float BeaconEmissionFadeEndMeters { get; set; } = 80000.0f;

    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private DirectionalLight3D? _sun;
    private MultiMeshInstance3D? _foundationInstances;
    private MultiMeshInstance3D? _towerInstances;
    private MultiMeshInstance3D? _beaconInstances;
    private ShaderMaterial? _foundationMaterial;
    private ShaderMaterial? _towerMaterial;
    private ShaderMaterial? _beaconMaterial;
    private long _lastAroundCell = long.MinValue;
    private long _lastAcrossCell = long.MinValue;
    private Vector3 _lastGridOrigin = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Task<LandmarkBuildResult>? _pendingBuild;
	private LandmarkBuildRequest? _queuedBuild;

    public int VisibleLandmarkCount { get; private set; }

    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (!Enabled || _grid?.TerrainSampler == null || _explorer == null)
        {
            Visible = false;
            SetProcess(false);
            return;
        }

        CreateMaterials();
        CreateMultiMeshes();
        QueueLandmarkRebuild(force: true);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (!Enabled || _grid == null || _explorer == null) return;
        UpdateLightingUniforms();
        CompleteLandmarkBuildIfReady();
        QueueLandmarkRebuild(force: !_lastGridOrigin.IsEqualApprox(_grid.GlobalPosition));
    }

    private void CreateMaterials()
    {
        if (_grid == null) return;
        Shader? shader = GD.Load<Shader>("res://materials/ring_scale_anchor.gdshader");
        if (shader == null) return;
        float noFadeStart = Mathf.Max(VisibilityRadiusAroundMeters, VisibilityRadiusAcrossMeters) + 1000.0f;
        float noFadeEnd = noFadeStart + 1000.0f;
        _foundationMaterial = CreateMaterial(
            shader, FoundationColor, BeaconColor, 0.03f, 0.82f, 0.58f, noFadeStart, noFadeEnd);
        _towerMaterial = CreateMaterial(
            shader, TowerColor, BeaconColor, 0.10f, 0.76f, 0.44f, noFadeStart, noFadeEnd);
        _beaconMaterial = CreateMaterial(
            shader,
            BeaconColor,
            BeaconColor,
            BeaconEmissionEnergy,
            0.25f,
            0.24f,
            BeaconEmissionFadeStartMeters,
            Mathf.Max(BeaconEmissionFadeStartMeters + 1.0f, BeaconEmissionFadeEndMeters));
    }

    private ShaderMaterial CreateMaterial(
        Shader shader,
        Color color,
        Color emissionColor,
        float emissionEnergy,
        float metallic,
        float roughness,
        float emissionFadeStartMeters,
        float emissionFadeEndMeters)
    {
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("anchor_color", new Vector3(color.R, color.G, color.B));
        material.SetShaderParameter(
            "anchor_emission_color",
            new Vector3(emissionColor.R, emissionColor.G, emissionColor.B));
        material.SetShaderParameter("anchor_emission_energy", emissionEnergy);
        material.SetShaderParameter("emission_fade_start_meters", emissionFadeStartMeters);
        material.SetShaderParameter("emission_fade_end_meters", emissionFadeEndMeters);
        material.SetShaderParameter("metallic_value", metallic);
        material.SetShaderParameter("roughness_value", roughness);
        RingAtmosphereModel.ConfigureMaterial(
            material,
            _grid!.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
        return material;
    }

    private void CreateMultiMeshes()
    {
        var foundationMesh = new CylinderMesh
        {
            TopRadius = 0.45f,
            BottomRadius = 0.56f,
            Height = 1.0f
        };
        var towerMesh = new CylinderMesh
        {
            TopRadius = 0.12f,
            BottomRadius = 0.54f,
            Height = 1.0f
        };
        var beaconMesh = new BoxMesh { Size = Vector3.One };
        _foundationInstances = CreateMultiMeshInstance("ScaleAnchorFoundations", foundationMesh, _foundationMaterial);
        _towerInstances = CreateMultiMeshInstance("ScaleAnchorTowers", towerMesh, _towerMaterial);
        _beaconInstances = CreateMultiMeshInstance("ScaleAnchorBeacons", beaconMesh, _beaconMaterial);
    }

    private MultiMeshInstance3D CreateMultiMeshInstance(string name, Mesh mesh, Material? material)
    {
        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = 0,
            VisibleInstanceCount = 0
        };
        var instance = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multiMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            ExtraCullMargin = Mathf.Max(VisibilityRadiusAroundMeters, VisibilityRadiusAcrossMeters)
        };
        AddChild(instance);
        RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
        return instance;
    }

    private void QueueLandmarkRebuild(bool force)
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler || _explorer == null) return;
        float cellSize = Mathf.Max(1000.0f, AnchorCellSizeMeters);
        RingCanonicalPosition focus = _explorer.CanonicalPosition;
        long aroundCell = (long)Math.Floor(focus.AroundMeters / cellSize);
        long acrossCell = (long)Math.Floor(focus.AcrossMeters / cellSize);
        if (!force && aroundCell == _lastAroundCell && acrossCell == _lastAcrossCell) return;
        _lastAroundCell = aroundCell;
        _lastAcrossCell = acrossCell;
        _lastGridOrigin = _grid.GlobalPosition;
        var request = new LandmarkBuildRequest(
            aroundCell, acrossCell, focus, _grid.CaptureLodGeometrySnapshot(), GlobalTransform.AffineInverse(),
            _grid.GetCanonicalRingCircumferenceMeters64(), _grid.RingAcrossWidthMeters, cellSize,
            Mathf.CeilToInt(VisibilityRadiusAroundMeters / cellSize), Mathf.CeilToInt(VisibilityRadiusAcrossMeters / cellSize),
            Mathf.Clamp(MaximumVisibleLandmarks, 1, 256), SpawnChance, MajorLandmarkChance,
            MinorTowerHeightMeters, MajorTowerHeightMeters, MinorTowerWidthMeters, MajorTowerWidthMeters,
            FoundationDiameterMeters, BeaconDiameterMeters, sampler.CreateIndependentCopy());
        if (_pendingBuild != null && !_pendingBuild.IsCompleted)
        {
            if (_queuedBuild is { } previous) previous.Sampler.Dispose();
            _queuedBuild = request;
            return;
        }
        _pendingBuild = Task.Run(() => BuildLandmarks(request));
    }

    private void CompleteLandmarkBuildIfReady()
    {
        if (_pendingBuild == null || !_pendingBuild.IsCompleted) return;
        Task<LandmarkBuildResult> completed = _pendingBuild;
        _pendingBuild = null;
        if (!completed.IsFaulted && !completed.IsCanceled
            && _foundationInstances != null && _towerInstances != null && _beaconInstances != null)
        {
            LandmarkBuildResult result = completed.Result;
            InstallTransforms(_foundationInstances, result.Foundations);
            InstallTransforms(_towerInstances, result.Towers);
            InstallTransforms(_beaconInstances, result.Beacons);
            VisibleLandmarkCount = result.Towers.Count;
        }
        if (_queuedBuild is { } queued) { _queuedBuild = null; _pendingBuild = Task.Run(() => BuildLandmarks(queued)); }
    }

    private static LandmarkBuildResult BuildLandmarks(LandmarkBuildRequest request)
    {
        using RingTerrainSampler sampler = request.Sampler;
        var foundations = new List<Transform3D>(request.Maximum);
        var towers = new List<Transform3D>(request.Maximum);
        var beacons = new List<Transform3D>(request.Maximum);
        for (long acrossCell = request.AcrossCell - request.AcrossRadius; acrossCell <= request.AcrossCell + request.AcrossRadius && towers.Count < request.Maximum; acrossCell++)
        for (long aroundCell = request.AroundCell - request.AroundRadius; aroundCell <= request.AroundCell + request.AroundRadius && towers.Count < request.Maximum; aroundCell++)
        {
            if (Hash01(aroundCell, acrossCell, sampler.Seed, 1) > Math.Clamp(request.SpawnChance, 0.0f, 1.0f)) continue;
            double around = (aroundCell + 0.16 + Hash01(aroundCell, acrossCell, sampler.Seed, 2) * 0.68) * request.CellSizeMeters;
            around %= request.CircumferenceMeters;
            if (around < 0.0) around += request.CircumferenceMeters;
            double across = (acrossCell + 0.16 + Hash01(aroundCell, acrossCell, sampler.Seed, 3) * 0.68) * request.CellSizeMeters;
            if (across < request.CellSizeMeters * 0.15 || across > request.RingWidthMeters - request.CellSizeMeters * 0.15) continue;
            TerrainSample sample = sampler.GetTerrainSample((int)Math.Round(around), (int)Math.Round(across));
            if (sample.HasSurfaceWater || sample.Region is TerrainRegion.DeepOcean or TerrainRegion.OceanShelf || sample.SiteMask.BlocksVegetation) continue;
            bool major = Hash01(aroundCell, acrossCell, sampler.Seed, 4) < Math.Clamp(request.MajorChance, 0.0f, 1.0f);
            float variation = 0.82f + 0.36f * Hash01(aroundCell, acrossCell, sampler.Seed, 5);
            float height = (major ? request.MajorHeight : request.MinorHeight) * variation;
            float width = (major ? request.MajorWidth : request.MinorWidth) * variation;
            float foundation = request.FoundationDiameter * (major ? 1.8f : 1.0f) * variation;
            foundations.Add(CreatePartTransform(request.Geometry, request.WorldToLayer, around, across, sample.SurfaceHeight, foundation, Math.Max(8.0f, width * 0.16f)));
            towers.Add(CreatePartTransform(request.Geometry, request.WorldToLayer, around, across, sample.SurfaceHeight, width, height));
            float beacon = request.BeaconDiameter * (major ? 1.7f : 1.0f);
            beacons.Add(CreatePartTransform(request.Geometry, request.WorldToLayer, around, across, sample.SurfaceHeight + height, beacon, beacon));
        }
        return new LandmarkBuildResult(foundations, towers, beacons);
    }

    private static Transform3D CreatePartTransform(
        in RingLodGeometrySnapshot geometry,
        Transform3D worldToLayer,
        double around,
        double across,
        double baseAltitude,
        float width,
        float height)
    {
        Vector3 baseWorld = geometry.CanonicalToWorld(around, baseAltitude, across);
        Vector3 up = (geometry.CanonicalToWorld(around, baseAltitude + 10.0, across) - baseWorld).Normalized();
        Vector3 tangent = (geometry.CanonicalToWorld(around + 10.0, baseAltitude, across) - baseWorld).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(around, baseAltitude, across + 10.0) - baseWorld).Normalized();
        Vector3 centerWorld = baseWorld + up * (height * 0.5f);
        var worldBasis = new Basis(tangent * width, up * height, acrossAxis * width);
        Transform3D worldTransform = new(worldBasis, centerWorld);
        return worldToLayer * worldTransform;
    }

    private static void InstallTransforms(
        MultiMeshInstance3D owner,
        IReadOnlyList<Transform3D> transforms)
    {
        MultiMesh? multiMesh = owner.Multimesh;
        if (multiMesh == null) return;
        multiMesh.InstanceCount = transforms.Count;
        multiMesh.VisibleInstanceCount = transforms.Count;
        for (int index = 0; index < transforms.Count; index++)
            multiMesh.SetInstanceTransform(index, transforms[index]);
    }

    private void UpdateLightingUniforms()
    {
        if (_grid == null || _explorer == null) return;
        Vector3 cameraPosition = _explorer.GlobalPosition;
        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        Vector3 sunDirection = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        foreach (ShaderMaterial? material in new[] { _foundationMaterial, _towerMaterial, _beaconMaterial })
        {
            if (material == null) continue;
            material.SetShaderParameter("camera_world_position", cameraPosition);
            material.SetShaderParameter("ring_center", ringCenter);
            material.SetShaderParameter("sun_direction", sunDirection);
        }
    }

    private static float Hash01(long x, long y, int seed, int salt)
    {
        ulong value = unchecked((ulong)x) * 0x9E3779B185EBCA87UL;
        value ^= unchecked((ulong)y) * 0xC2B2AE3D27D4EB4FUL;
        value ^= unchecked((uint)(seed * 397 ^ salt * 7919));
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (value >> 40) / 16777215.0f;
    }
}
