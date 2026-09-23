using System;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Disposable F6 entry for the production integration scene. It starts the
/// verified Certified-seed clearing-to-grove route and validates its fixed
/// terrain/forest samples; it never changes the world's generator, save, or
/// decoration streamer.
/// </summary>
public partial class PlayableForest : Node
{
    private const string WorldScenePath = "res://scenes/VoxelRingIntegrationTest.tscn";
    // Verified by ForestRouteDiscovery against the Certified seed 1337 terrain
    // and regional sampler. This is deliberately fixed: entering the tour does
    // not perform a world scan or change with rendering/quality state.
    private static readonly Route CertifiedRoute = new(
        new Point(186_672, 140_520), new Point(186_336, 140_760), new Point(186_000, 141_000));
    private Node3D? _world;

    public bool RouteReady { get; private set; }
    public long RouteStartAroundMeters { get; private set; }
    public int RouteStartAcrossMeters { get; private set; }
    public long RouteWoodlandAroundMeters { get; private set; }
    public int RouteWoodlandAcrossMeters { get; private set; }
    public long RouteGroveAroundMeters { get; private set; }
    public int RouteGroveAcrossMeters { get; private set; }

    public Godot.Collections.Dictionary GetRouteDiagnostics() => new()
    {
        ["ready"] = RouteReady,
        ["start_around"] = RouteStartAroundMeters,
        ["start_across"] = RouteStartAcrossMeters,
        ["woodland_around"] = RouteWoodlandAroundMeters,
        ["woodland_across"] = RouteWoodlandAcrossMeters,
        ["grove_around"] = RouteGroveAroundMeters,
        ["grove_across"] = RouteGroveAcrossMeters,
    };

    public Godot.Collections.Dictionary GetTourSettlementDiagnostics()
    {
        var result = new Godot.Collections.Dictionary();
        var explorer = _world?.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        var smooth = _world?.GetNodeOrNull<SmoothTerrainGrid>("SmoothTerrainGrid");
        if (explorer == null) return result;
        RingCanonicalPosition canonical = explorer.CanonicalPosition;
        result["canonical"] = canonical.ToString();
        result["grounded"] = explorer.IsOnFloor();
        result["velocity"] = explorer.CanonicalVelocity;
        result["altitude"] = explorer.CurrentAltitudeAboveTerrainMeters;
        result["surface_up"] = explorer.GetCanonicalSurfaceUp();
        result["movement_resident"] = smooth?.IsMovementTerrainResident(canonical.AroundMeters, canonical.VerticalMeters, canonical.AcrossMeters, explorer.BodyWidth * .5f, explorer.BodyHeight) ?? false;
        return result;
    }

    public override void _Ready()
    {
        var packed = GD.Load<PackedScene>(WorldScenePath);
        if (packed == null)
        {
            GD.PushError("PLAYABLE_FOREST=FAIL missing integration scene");
            return;
        }
        _world = packed.Instantiate<Node3D>();
        // This scene is intentionally a tour, not a save slot. Removing the
        // instance-local manager before it enters the tree avoids loading or
        // writing whichever normal-world slot happens to be active.
        _world.GetNodeOrNull<Node>("WorldSaveManager")?.Free();
        if (_world.GetNodeOrNull<RingBiomeDecorationLayer>("BiomeDecorations") is { } decorations)
            decorations.EnableVegetationDiskCache = false;
        CallDeferred(MethodName.ConfigureWorldAsync);
    }

    private async void ConfigureWorldAsync()
    {
        if (_world == null) return;
        var launcherScene = GD.Load<PackedScene>("res://scenes/WorldSizeLauncher.tscn");
        if (launcherScene == null) { GD.PushError("PLAYABLE_FOREST=FAIL missing world launcher"); return; }
        Node launcher = launcherScene.Instantiate();
        AddChild(launcher);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        launcher.Call("_on_certified_preset_pressed");
        launcher.Call("_configure_game", _world, 5_000_000.0);
        launcher.Free();
        AddChild(_world);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        ConfigureRoute();
    }

    private void ConfigureRoute()
    {
        if (_world == null) return;
        var grid = _world.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        var explorer = _world.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (grid?.TerrainSampler == null || explorer == null || !ValidateRoute(grid, CertifiedRoute))
        {
            GD.PushError("PLAYABLE_FOREST=FAIL no traversable seeded forest route");
            return;
        }

        Route route = CertifiedRoute;
        RouteStartAroundMeters = route.Start.Around;
        RouteStartAcrossMeters = route.Start.Across;
        RouteWoodlandAroundMeters = route.Woodland.Around;
        RouteWoodlandAcrossMeters = route.Woodland.Across;
        RouteGroveAroundMeters = route.Grove.Around;
        RouteGroveAcrossMeters = route.Grove.Across;
        WarpToRoutePoint(route.Start.Around, route.Start.Across);
        ConfigurePresentation(route);
        RouteReady = true;
        GD.Print($"PLAYABLE_FOREST_ROUTE=PASS start=({route.Start.Around},{route.Start.Across}) woodland=({route.Woodland.Around},{route.Woodland.Across}) grove=({route.Grove.Around},{route.Grove.Across})");
    }

    /// <summary>Fixture-safe route warp. SetCanonicalSpawn starts terrain
    /// residency, then RestoreSavedState restores a body-clearance height and
    /// cancels the initial surface+1.05m clamp before physics resumes.</summary>
    public bool WarpToRoutePoint(long around, int across)
    {
        var grid = _world?.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        var explorer = _world?.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (grid?.TerrainSampler == null || explorer == null) return false;
        TerrainSample surface = grid.TerrainSampler.GetTerrainSample(around, across);
        Point destination = around == CertifiedRoute.Start.Around && across == CertifiedRoute.Start.Across ? CertifiedRoute.Woodland
            : CertifiedRoute.Grove;
        float yaw = Mathf.Atan2(destination.Across - across, destination.Around - around);
        double vertical = surface.SurfaceHeight + 3.0;
        explorer.SetCanonicalSpawnMeters(around, vertical, across);
        explorer.RestoreSavedState(new RingCanonicalPosition(around, vertical, across), Vector3.Zero, yaw, 0f, false, 0);
        return true;
    }

    private void ConfigurePresentation(in Route route)
    {
        _world?.GetNodeOrNull<CanvasLayer>("LiveTuningDrawer")?.Hide();
        if (_world?.GetNodeOrNull<CanvasLayer>("ForestTrailHud") == null)
        {
            var layer = new CanvasLayer { Name = "ForestTrailHud", Layer = 20 };
            var label = new Label
            {
                Text = "FOREST TRAIL\nWalk ahead: clearing → woodland → grove\nWASD move  •  Shift sprint  •  Space jump  •  V flight",
                Position = new Vector2(24, 122),
                Modulate = new Color(.92f, 1f, .92f)
            };
            layer.AddChild(label);
            _world?.AddChild(layer);
        }
        if (_world?.GetNodeOrNull<Label>("Instructions") is { } instructions)
        {
            instructions.Hide();
        }
    }

    private static bool ValidateRoute(VoxelChunkGridManager grid, in Route route)
    {
        RingTerrainSampler terrain = grid.TerrainSampler!;
        long circumference = (long)Math.Round(grid.GetCanonicalRingCircumferenceMeters64());
        int width = Mathf.RoundToInt(grid.RingAcrossWidthMeters);
        var forest = new ForestRegionSampler(terrain.Seed, circumference, width);
        return TrySample(forest, terrain, route.Start.Around, route.Start.Across, out ForestRegionSample start, out _)
            && TrySample(forest, terrain, route.Woodland.Around, route.Woodland.Across, out ForestRegionSample woodland, out _)
            && TrySample(forest, terrain, route.Grove.Around, route.Grove.Across, out ForestRegionSample grove, out _)
            && start.Habitat == ForestHabitat.Clearing && woodland.Habitat == ForestHabitat.OpenWoodland
            && grove.Habitat == ForestHabitat.DenseForest;
    }

    private static bool TrySample(ForestRegionSampler forest, RingTerrainSampler terrain, int around, int across, out ForestRegionSample sample, out TerrainSample terrainSample)
    {
        terrainSample = terrain.GetTerrainSample(around, across);
        int plus = terrain.GetTerrainSample(around + 8, across).SurfaceHeight;
        int minus = terrain.GetTerrainSample(around - 8, across).SurfaceHeight;
        int sidePlus = terrain.GetTerrainSample(around, across + 8).SurfaceHeight;
        int sideMinus = terrain.GetTerrainSample(around, across - 8).SurfaceHeight;
        float slope = Mathf.RadToDeg(Mathf.Atan(Mathf.Max(Math.Abs(plus - minus), Math.Abs(sidePlus - sideMinus)) / 16f));
        sample = forest.Sample(around, across, terrainSample, slope);
        // Matches the production tree suitability ceiling while excluding only
        // slopes that a normal player route cannot reasonably use.
        return !terrainSample.HasSurfaceWater && slope < 22f;
    }

    private readonly record struct Point(int Around, int Across);
    private readonly record struct Route(Point Start, Point Woodland, Point Grove);
}
