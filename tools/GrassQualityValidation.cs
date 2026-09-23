using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Verifies the selected live grass profile after streaming settles.</summary>
public partial class GrassQualityValidation : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);
    [Export] public NodePath GrassLayerPath { get; set; } = new("../StandingGrass");
    [Export] public bool RunOnReady { get; set; }
    [Export(PropertyHint.Range, "60,1200,1")] public int TimeoutFrames { get; set; } = 600;

    public override void _Ready()
    {
        if (RunOnReady) Run();
    }

    public async void Run()
    {
        try
        {
            VerifyProfiles();
            RingStandingGrassLayer layer = GetNodeOrNull<RingStandingGrassLayer>(GrassLayerPath)
                ?? throw new InvalidOperationException("live StandingGrass layer is unavailable");
            for (int frame = 0; frame < TimeoutFrames; frame++)
            {
                if (layer.ResidentGrassInstanceCount > 0 && layer.PendingGrassTileBuildCount == 0
                    && layer.PendingGrassUploadCount == 0) break;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!layer.EnableDistanceLod || layer.BladesPerSquareMeter <= 0 || layer.ResidentGrassInstanceCount <= 0)
                throw new InvalidOperationException("selected live grass profile did not produce dense distance-LOD roots");
            if (layer.PendingGrassTileBuildCount != 0 || layer.PendingGrassUploadCount != 0)
                throw new TimeoutException("live grass streaming did not settle");
            if (layer.ResidentGrassTileCount > layer.MaximumResidentTiles
                || layer.ResidentGrassInstanceCount > layer.MaximumInstances)
                throw new InvalidOperationException("live grass profile exceeded its resident budget");
            GD.Print($"GRASS_QUALITY_PROFILE=PASS roots={layer.ResidentGrassInstanceCount} tiles={layer.ResidentGrassTileCount} density={layer.BladesPerSquareMeter:0.#}");
            EmitSignal(SignalName.Completed, true);
        }
        catch (Exception exception)
        {
            GD.PushError("GRASS_QUALITY_PROFILE=FAIL " + exception.Message);
            EmitSignal(SignalName.Completed, false);
        }
    }

    public static void VerifyProfiles()
    {
        foreach (RingTerrainLodCoordinator.QualityPreset preset in Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
        foreach (string tier in new[] { "laptop_140v_baseline", "desktop_mid_high" })
            if (!RingGrassQualityProfile.For(preset, tier).HasSufficientInstanceBudget())
                throw new InvalidOperationException($"grass profile cap invariant failed for {preset}/{tier}");
    }

    public bool VerifyProfileContract()
    {
        try { VerifyProfiles(); return true; }
        catch (Exception exception) { GD.PushError("GRASS_QUALITY_PROFILE_CONTRACT=FAIL " + exception.Message); return false; }
    }

    /// <summary>Finds a dry grass-biome patch in the live sampler and immobilizes the real explorer above it.</summary>
    public bool FindAndPlaceGrassland(Node game)
    {
        VoxelChunkGridManager grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")
            ?? throw new InvalidOperationException("ChunkGridManager is unavailable");
        RingVoxelExplorerController explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer")
            ?? throw new InvalidOperationException("Explorer is unavailable");
        RingTerrainSampler sampler = grid.TerrainSampler
            ?? throw new InvalidOperationException("terrain sampler is not ready");
        float best = float.NegativeInfinity;
        long bestAround = 186_000;
        int bestAcross = 141_000;
        for (long around = 184_000; around <= 188_000; around += 64)
        for (int across = 139_000; across <= 143_000; across += 64)
        {
            float score = 0;
            for (long da = -16; da <= 16; da += 8)
            for (int db = -16; db <= 16; db += 8)
            {
                TerrainSample sample = sampler.GetTerrainSample(around + da, across + db);
                if (sample.HasSurfaceWater) continue;
                var weights = sampler.GetBiomeTextureWeights(sample);
                score += Mathf.Max(0, 1 - weights.Desert - weights.Forest - weights.Tundra - weights.Alpine) * .82f
                    + weights.Forest * .10f;
            }
            if (score > best) { best = score; bestAround = around; bestAcross = across; }
        }
        if (best <= 0) return false;
        float height = sampler.GetSmoothSurfaceHeight(bestAround, bestAcross);
        explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(bestAround, height + 1, bestAcross));
        explorer.SetPhysicsProcess(false);
        explorer.SetProcess(false);
        explorer.ProcessMode = Node.ProcessModeEnum.Disabled;
        explorer.SetProcessInput(false);
        explorer.SetProcessUnhandledInput(false);
        explorer.ResetCanonicalOrientationForCapture();
        return true;
    }

    public void RaiseForFarCapture(Node game, float meters = 32)
    {
        RingVoxelExplorerController explorer = game.GetNode<RingVoxelExplorerController>("Explorer");
        RingCanonicalPosition position = explorer.CanonicalPosition;
        explorer.SetBenchmarkCanonicalPosition(position with { VerticalMeters = position.VerticalMeters + Mathf.Max(1, meters) });
        explorer.ResetCanonicalOrientationForCapture();
        explorer.SetPresentationView(0, -35);
    }

    public Godot.Collections.Dictionary GetFocusDiagnostics(Node game)
    {
        VoxelChunkGridManager grid = game.GetNode<VoxelChunkGridManager>("ChunkGridManager");
        RingStandingGrassLayer layer = game.GetNode<RingStandingGrassLayer>("StandingGrass");
        RingCanonicalPosition focus = game.GetNode<RingVoxelExplorerController>("Explorer").CanonicalPosition;
        RingTerrainSampler sampler = grid.TerrainSampler ?? throw new InvalidOperationException("terrain sampler is not ready");
        int focusRoots = 0, nearPrefix = 0, focusTiles = 0;
        foreach (RingStandingGrassLayer.TileAudit audit in layer.CaptureTileAuditsForTests())
        {
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(audit.Around, audit.Across,
                layer.SpatialTileMeters, sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters);
            if (focus.AroundMeters < bounds.AroundMin || focus.AroundMeters >= bounds.AroundMax
                || focus.AcrossMeters < bounds.AcrossMin || focus.AcrossMeters >= bounds.AcrossMax) continue;
            focusTiles++; focusRoots += audit.InstanceCount; nearPrefix += audit.NearVisibleInstanceCount;
        }
        return new Godot.Collections.Dictionary {
            ["focus_roots"] = focusRoots, ["focus_tiles"] = focusTiles,
            ["near_prefix"] = nearPrefix, ["density"] = layer.BladesPerSquareMeter,
            ["around"] = focus.AroundMeters, ["across"] = focus.AcrossMeters
        };
    }

    /// <summary>Creates a stable camera from a real focused-tile blade root, independent of explorer input/orientation.</summary>
    public Camera3D CreateDedicatedCaptureCamera(Node game)
    {
        RingStandingGrassLayer layer = game.GetNode<RingStandingGrassLayer>("StandingGrass");
        RingCanonicalPosition focus = game.GetNode<RingVoxelExplorerController>("Explorer").CanonicalPosition;
        VoxelChunkGridManager grid = game.GetNode<VoxelChunkGridManager>("ChunkGridManager");
        RingTerrainSampler sampler = grid.TerrainSampler ?? throw new InvalidOperationException("terrain sampler is not ready");
        MultiMeshInstance3D? renderer = null;
        foreach (RingStandingGrassLayer.TileAudit audit in layer.CaptureTileAuditsForTests())
        {
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(audit.Around, audit.Across,
                layer.SpatialTileMeters, sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters);
            if (focus.AroundMeters >= bounds.AroundMin && focus.AroundMeters < bounds.AroundMax
                && focus.AcrossMeters >= bounds.AcrossMin && focus.AcrossMeters < bounds.AcrossMax)
            {
                renderer = GodotObject.InstanceFromId(audit.RenderInstanceId) as MultiMeshInstance3D;
                if (renderer?.Multimesh?.InstanceCount > 0) break;
            }
        }
        if (renderer?.Multimesh == null || renderer.Multimesh.InstanceCount <= 0)
            throw new InvalidOperationException("focused tile has no renderable grass instance");
        Transform3D blade = renderer.GlobalTransform * renderer.Multimesh.GetInstanceTransform(0);
        Vector3 root = blade.Origin;
        Vector3 up = blade.Basis.Y.Normalized();
        Vector3 tangent = blade.Basis.Z.Normalized();
        var camera = new Camera3D { Name = "GrassQualityCaptureCamera", Current = true, Far = 100_000 };
        game.AddChild(camera);
        camera.SetMeta("grass_root", root);
        camera.SetMeta("grass_up", up);
        camera.SetMeta("grass_tangent", tangent);
        SetDedicatedCaptureView(camera, false);
        return camera;
    }

    public void SetDedicatedCaptureView(Camera3D camera, bool elevated)
    {
        Vector3 root = camera.GetMeta("grass_root").AsVector3();
        Vector3 up = camera.GetMeta("grass_up").AsVector3();
        Vector3 tangent = camera.GetMeta("grass_tangent").AsVector3();
        camera.GlobalPosition = root + up * (elevated ? 10 : 1.5f) + tangent * (elevated ? 8 : 4);
        camera.LookAt(root + up * .4f, up);
        camera.MakeCurrent();
    }

    /// <summary>Fixture-only celestial state control; callers restore the returned state after captures.</summary>
    public Godot.Collections.Dictionary SetCaptureCycle(Node game, float fraction)
    {
        var cycle = game.GetNode<global::RingworldTechDemo.RingDayNightCycle>("RingDayNightCycle");
        var prior = new Godot.Collections.Dictionary { ["paused"] = cycle.Paused, ["seconds"] = cycle.GetSimulationSeconds() };
        cycle.Paused = true; cycle.SetCycleFraction(fraction);
        return prior;
    }
    public void RestoreCaptureCycle(Node game, Godot.Collections.Dictionary prior)
    {
        var cycle = game.GetNode<global::RingworldTechDemo.RingDayNightCycle>("RingDayNightCycle");
        cycle.SetSimulationSeconds(prior["seconds"].AsDouble()); cycle.Paused = prior["paused"].AsBool();
    }
    public void BeginStormWindCapture(Node game)
    {
        RingVoxelExplorerController explorer = game.GetNode<RingVoxelExplorerController>("Explorer");
        var clouds = game.GetNode<global::RingworldTechDemo.RingVolumetricCloudLayer>("RingVolumetricClouds");
        var effects = game.GetNode<global::RingworldTechDemo.RingLocalWeatherEffects>("LocalWeatherEffects");
        RingCanonicalPosition point = explorer.CanonicalPosition;
        Vector2 wind = new(15, 5);
        var state = new global::RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState(
            -9497, global::RingworldTechDemo.RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1, 1400,
            new Vector2((float)point.AroundMeters, (float)point.AcrossMeters), new Vector2(1400, 840), 0,
            1, 1, wind, global::RingworldTechDemo.CloudFormationProfile.Cumulonimbus,
            clouds.CloudBaseMeters, clouds.CloudTopMeters);
        clouds.RegisterManualWeather("grass-quality-capture", state);
        effects.SetExternalWeatherOverride(true, state);
    }
    public void EndStormWindCapture(Node game)
    {
        game.GetNode<global::RingworldTechDemo.RingVolumetricCloudLayer>("RingVolumetricClouds").UnregisterManualWeather("grass-quality-capture");
        game.GetNode<global::RingworldTechDemo.RingLocalWeatherEffects>("LocalWeatherEffects").SetExternalWeatherOverride(false);
    }
    public Godot.Collections.Dictionary GetLightingDiagnostics(Node game)
    {
        var cycle = game.GetNode<global::RingworldTechDemo.RingDayNightCycle>("RingDayNightCycle");
        var effects = game.GetNode<global::RingworldTechDemo.RingLocalWeatherEffects>("LocalWeatherEffects");
        return new Godot.Collections.Dictionary { ["cycle_fraction"] = cycle.GetCycleTimeSeconds() / Math.Max(1, cycle.FullCycleSeconds), ["daylight"] = cycle.DaylightFactor, ["night"] = cycle.NightFactor, ["resolved_wind"] = effects.ResolvedWindMetersPerSecond, ["weather"] = effects.LastResolvedWeather.ToString() };
    }
}

