using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Small, authoritative-field fixture for visually reviewing the production grass scheduler.</summary>
public partial class GrassMeadowBenchmarkDriver : Node3D
{
    [Signal] public delegate void MeadowReadyEventHandler();
    [Export] public float Density { get; set; } = 48;
    public int InstanceCount => _layer?.ResidentGrassInstanceCount ?? 0;
    public int TileCount => _layer?.ResidentGrassTileCount ?? 0;
    /// <summary>Benchmark-only control for capture A/B checks.</summary>
    public RingStandingGrassLayer? GetGrassLayerForTests() => _layer;
    public bool ReadyForCapture { get; private set; }
    public Vector3 WindCenter { get; private set; }
    public Vector3 WindAxis { get; private set; }
    private const long MeadowCircumference = 981_748L * 32L;
    private RingStandingGrassLayer? _layer;
    private SmoothTerrainVolumetricDensitySnapshot? _field;
    private RingCanonicalPosition _focus;
    private RingLodGeometrySnapshot _geometry;

    public override async void _Ready()
    {
        try { await BuildAsync(); }
        catch (Exception e) { GD.PushError("GRASS_MEADOW_BENCHMARK=FAIL " + e.Message); if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--grass-meadow-capture") >= 0) GetTree().Quit(1); }
    }

    private async Task BuildAsync()
    {
        const long circumference = MeadowCircumference;
        const int width = 318_000;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true) ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000d, width, 8000);
        RingCanonicalPosition focus = FindGrasslandFocus(sampler);
        _focus = focus;
        var hints = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long around = (long)focus.AroundMeters - 80; around <= focus.AroundMeters + 80; around++)
        for (int across = (int)focus.AcrossMeters - 80; across <= focus.AcrossMeters + 80; across++)
            hints[new RingTerrainColumnCoordinate(around, across)] = 42;
        var edits = new RingLodSurfaceEditSnapshot(1, circumference, hints);
        double angle = focus.AroundMeters / 5_000_000d;
        // Re-orient this small review frame so the real ring radial-up direction is Godot +Y.
        var source = new Basis(new Vector3((float)-Math.Cos(angle), 0, (float)-Math.Sin(angle)), Vector3.Up, new Vector3((float)-Math.Sin(angle), 0, (float)Math.Cos(angle)));
        var target = new Basis(Vector3.Up, Vector3.Right, Vector3.Back);
        var geometry = new RingLodGeometrySnapshot(new Transform3D(target * source.Transposed(), Vector3.Zero), Vector3.Zero, Vector3.Zero, 5_000_000d, 0, 0, circumference, -1000, 1000, focus);
        _geometry = geometry;
        WindCenter = new Vector3(0, 5_000_000f - (float)focus.VerticalMeters, 0);
        WindAxis = geometry.GridGlobalTransform.Basis.Y;
        var baseline = new MeadowPlaneBaseline(circumference, width);
        _field = new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 1, circumference));
        _layer = new RingStandingGrassLayer {
            VisibilityRadiusMeters = 24, SpatialTileMeters = 16, BladesPerSquareMeter = Mathf.Clamp(Density, 1, 96),
            MaximumInstancesPerTile = 24_576, MaximumInstances = 120_000, MaximumResidentTiles = 16,
            MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 2, MaximumSlopeDegrees = 35
        };
        AddChild(_layer); _layer.InitializeForTests();
        _layer.InitializeGrassGustTextureForTests(sampler.Seed);
        // The walkway and three rocks are canonical occupancy volumes, so this visibly exercises the same exclusion route as construction.
        var exclusions = new RingGrassExclusionSnapshot(1, circumference, new[] {
            new RingGrassExclusionBox(focus.AroundMeters - 24, focus.AroundMeters + 24, 41, 46, focus.AcrossMeters - 1, focus.AcrossMeters + 1),
            new RingGrassExclusionBox(focus.AroundMeters + 5, focus.AroundMeters + 8, 41, 47, focus.AcrossMeters + 8, focus.AcrossMeters + 11),
            new RingGrassExclusionBox(focus.AroundMeters - 10, focus.AroundMeters - 6, 41, 47, focus.AcrossMeters - 13, focus.AcrossMeters - 9),
            new RingGrassExclusionBox(focus.AroundMeters - 12, focus.AroundMeters - 8, 41, 47, focus.AcrossMeters + 6, focus.AcrossMeters + 10),
            new RingGrassExclusionBox(focus.AroundMeters - 200, focus.AroundMeters - 16, 0, 100, focus.AcrossMeters - 200, focus.AcrossMeters + 200),
            new RingGrassExclusionBox(focus.AroundMeters + 16, focus.AroundMeters + 200, 0, 100, focus.AcrossMeters - 200, focus.AcrossMeters + 200),
            new RingGrassExclusionBox(focus.AroundMeters - 16, focus.AroundMeters + 16, 0, 100, focus.AcrossMeters - 200, focus.AcrossMeters - 16),
            new RingGrassExclusionBox(focus.AroundMeters - 16, focus.AroundMeters + 16, 0, 100, focus.AcrossMeters + 16, focus.AcrossMeters + 200)
        });
        ulong deadline = Time.GetTicksMsec() + 12000;
        while (Time.GetTicksMsec() < deadline)
        {
            _layer.StepForTests(sampler, edits, focus, geometry, field: _field, exclusions: exclusions);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_layer.PendingGrassTileBuildCount == 0 && _layer.PendingGrassUploadCount == 0 && _layer.ResidentGrassInstanceCount > 0
                && (!_layer.EnableGrassGustTexture || _layer.GrassGustTextureReady))
            {
                int bladeEquivalentCount = _layer.ResidentGrassInstanceCount * _layer.GrassBladesPerSource;
                if (bladeEquivalentCount < 25_000) throw new InvalidOperationException($"dense meadow underfilled: {bladeEquivalentCount} blade-equivalents");
                ReadyForCapture = true; EmitSignal(SignalName.MeadowReady); GD.Print($"GRASS_MEADOW_READY tiles={TileCount} instances={InstanceCount} density={Density} worker_ms={_layer.LastWorkerBuildMilliseconds:F1} install_ms={_layer.LastInstallMilliseconds:F1}"); return;
            }
        }
        throw new TimeoutException("production grass layer did not settle");
    }

    /// <summary>The benchmark has no production weather node, so it publishes its simulation clock directly.</summary>
    public void SetGrassGustWeatherForTests(Vector2 wind, double simulationTime)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
        _layer.SetGrassGustWeatherForTests(wind, simulationTime, MeadowCircumference);
        // The fixture settles once then stops streaming. Refresh presentation so
        // every existing tile receives the new canonical UV origin each frame.
        _layer.RefreshLodPresentationForTests(_focus, _geometry, MeadowCircumference);
    }

    public override void _ExitTree()
    {
        // The layer cancels and disposes worker contexts in its own exit hook before the fixture leaves the tree.
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        _field?.DisposePooledWorkerContexts();
    }

    private static RingCanonicalPosition FindGrasslandFocus(RingTerrainSampler sampler)
    {
        const long aroundStart = 184_000; const int acrossStart = 139_000;
        float best = -1; long bestAround = 186_000; int bestAcross = 141_000;
        for (long around = aroundStart; around <= aroundStart + 4_000; around += 64)
        for (int across = acrossStart; across <= acrossStart + 4_000; across += 64)
        {
            float score = 0;
            for (long da = -16; da <= 16; da += 8)
            for (int db = -16; db <= 16; db += 8)
            {
                TerrainSample sample = sampler.GetTerrainSample(around + da, across + db);
                if (sample.HasSurfaceWater) continue;
                var weights = sampler.GetBiomeTextureWeights(sample);
                float grass = Mathf.Max(0, 1 - weights.Desert - weights.Forest - weights.Tundra - weights.Alpine);
                score += grass * .82f + weights.Forest * .42f;
            }
            if (score > best) { best = score; bestAround = around; bestAcross = across; }
        }
        GD.Print($"GRASS_MEADOW_SITE eligible_weight={best:F2}/25 around={bestAround} across={bestAcross}");
        return new RingCanonicalPosition(bestAround, 42.25, bestAcross);
    }

    private sealed class MeadowPlaneBaseline : ISmoothTerrainBaseline
    {
        public long AroundLength { get; } public int AcrossWidth { get; } public ulong Revision => 1401;
        public MeadowPlaneBaseline(long length, int width) { AroundLength = length; AcrossWidth = width; }
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            const float surface = 42.25f;
            return new SmoothTerrainSample(vertical - surface, (int)(vertical < surface - 2 ? SmoothTerrainMaterial.Rock : SmoothTerrainMaterial.Soil), Revision);
        }
    }
}
