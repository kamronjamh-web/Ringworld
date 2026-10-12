using System;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Diagnostics;

/// <summary>Slow, cached diagnostic context. This is intentionally separate from per-frame event collection.</summary>
public partial class RingPerformanceMonitor
{
    private const double ContextRefreshSeconds = 0.2;
    private double _contextElapsed = ContextRefreshSeconds;
    private RingTerrainLodCoordinator? _contextCoordinator;
    private VoxelChunkGridManager? _contextGrid;
    private SmoothTerrainGrid? _contextSmoothTerrain;
    private RingBiomeDecorationLayer? _contextTrees;
    private RingStandingGrassLayer? _contextGrass;
    private RingWaterFftController? _contextWater;
    private RingGraphicsQualityController? _contextGraphics;
    private RingVoxelExplorerController? _contextExplorer;
    private int _framePendingTerrainUploads = -1;
    private int _frameActiveTerrainWorkers = -1;
    private int _frameTreeShadowRenderers = -1;
    private int _frameShadowAtlasSize = -1;

    private void UpdateContextForDiagnostics(double delta)
    {
        _contextElapsed += Math.Max(0.0, delta);
        if (_contextElapsed < ContextRefreshSeconds) return;
        _contextElapsed = 0.0;
        CacheContextNodes();

        RingTerrainLodCoordinator? coordinator = _contextCoordinator;
        VoxelChunkGridManager? grid = _contextGrid;
        SmoothTerrainGrid? smooth = _contextSmoothTerrain;
        RingBiomeDecorationLayer? trees = _contextTrees;
        RingStandingGrassLayer? grass = _contextGrass;
        RingWaterFftController? water = _contextWater;
        RingGraphicsQualityController? graphics = _contextGraphics;

        _framePendingTerrainUploads = coordinator?.PendingUploadCount ?? -1;
        _frameActiveTerrainWorkers = coordinator?.ActiveWorkerCount ?? -1;
        _frameTreeShadowRenderers = trees?.ActiveTreeSunShadowRendererCount ?? -1;
        _frameShadowAtlasSize = graphics?.EffectiveDirectionalShadowAtlasSize ?? -1;

        string terrain = coordinator == null ? "unavailable" :
            $"workers {coordinator.ActiveWorkerCount}/{coordinator.MaxConcurrentWorkerBuilds}; queued uploads {coordinator.PendingUploadCount}; active mesh {FormatBytes(coordinator.EstimatedActiveMeshBytes)}; staged {FormatBytes(coordinator.EstimatedPendingMeshBytes)}";
        string chunks = grid == null ? "unavailable" :
            $"loaded {grid.LoadedChunkCount}; loads {grid.PendingChunkLoadCount}; rebuilds {grid.PendingChunkRebuildCount}; mesh workers {grid.ActiveVoxelMeshWorkerCount}; mesh queued {grid.PendingVoxelMeshCount}; estimated data {FormatBytes(grid.EstimatedVoxelDataBytes)}";
        string smoothTerrain = smooth == null ? "unavailable" :
            $"desired {smooth.Metrics.Desired}; pending {smooth.Metrics.Pending}; ready {smooth.ReadyResultCount}; workers {smooth.Metrics.ActiveWorkers}; estimated {FormatBytes(smooth.Metrics.EstimatedBytes)}";
        string treeStatus = trees == null ? "unavailable" :
            $"shadow casters {trees.ActiveTreeSunShadowRendererCount}; gpu shadow renderers {trees.ActiveGpuTreeSunShadowRendererCount}; worker last {trees.LastWorkerBuildMilliseconds:0.0} ms";
        Godot.Collections.Dictionary? grassCache = grass?.SurfaceCacheDiagnostics;
        string grassStatus = grass == null ? "unavailable" :
            $"enabled {grass.Enabled}; streaming suspended {grass.StreamingSuspended}; resident tiles {grass.ResidentGrassTileCount}; roots {grass.ResidentGrassInstanceCount} (near {grass.ResidentGrassNearRootCount}, mid {grass.ResidentGrassMidRootCount}, far {grass.ResidentGrassFarRootCount}); "
            + $"pending builds {grass.PendingGrassTileBuildCount}; pending uploads {grass.PendingGrassUploadCount}; "
            + $"GPU registered batches {grass.GpuCullingBatchCount}; requested {grass.GpuCullingRequestedBatchCount}, dispatched {grass.GpuCullingDispatchedBatchCount}, deferred {grass.GpuCullingDeferredBatchCount}; reported buffers {FormatBytes(grass.GpuCullingBytes)}; "
            + $"surface cache {grassCache!["entries"].AsInt32()} entries / {FormatBytes(grassCache["estimated_bytes"].AsInt64())}";
        string waterStatus = water == null ? "unavailable" :
            $"{(water.Ready ? "ready" : "not ready")}; resolution {water.ActiveResolution}; cascades {water.ActiveCascadeCount}; actual wind {water.ActualWindSpeedMetersPerSecond:0.0} m/s; demand {water.WaterDemandStatus}; relevant {water.RelevantWaterSectorCount} chunks/{water.RelevantWaterCellCount} cells, {water.RelevantWaterAreaSquareKilometers:0.###} km², nearest {(float.IsFinite(water.NearestRelevantWaterDistanceMeters) ? $"{water.NearestRelevantWaterDistanceMeters:0} m" : "none")}; targets geometry {water.TargetGeometryUpdatesPerSecond:0.0}/{water.GeometryUpdatesPerSecond:0.0} Hz detail {water.TargetDetailUpdatesPerSecond:0.0}/{water.UpdatesPerSecond:0.0} Hz; actual geometry {water.ActualGeometryUpdatesPerSecond:0.0} Hz detail {water.ActualDetailUpdatesPerSecond:0.0} Hz; demand check {water.WaterDemandCheckMilliseconds:0.###} ms; completed FFT updates {water.CompletedFftUpdates}";
        string shadow = graphics == null ? "unavailable" :
            $"effective {graphics.EffectiveDirectionalShadowAtlasSize}px; depth {graphics.EffectiveDirectionalShadowDepthBits}-bit; minimum depth {FormatUnsignedBytes(graphics.EffectiveDirectionalShadowAtlasMinimumDepthBytes)}";
        string preset = graphics?.CurrentPresetName ?? coordinator?.CurrentPresetName ?? "unavailable";
        float speed = _contextExplorer?.ActualTravelSpeedMetersPerSecond ?? 0.0f;
        string memory = $"computed memory estimated; speed {speed:0.0} m/s";
        SetContextForDiagnostics(new RingPerformanceContext(
            terrain, chunks, smoothTerrain, treeStatus, grassStatus, waterStatus, shadow, preset,
            GetViewport().Scaling3DScale, memory, speed,
            (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
            (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed)));
    }

    private void CacheContextNodes()
    {
        _contextCoordinator ??= GetNodeOrNull<RingTerrainLodCoordinator>(TerrainCoordinatorPath);
        _contextGrid ??= GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _contextSmoothTerrain ??= GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _contextTrees ??= GetNodeOrNull<RingBiomeDecorationLayer>(TreesPath);
        _contextGrass ??= GetNodeOrNull<RingStandingGrassLayer>(GrassPath);
        _contextWater ??= GetNodeOrNull<RingWaterFftController>(WaterPath);
        _contextGraphics ??= GetNodeOrNull<RingGraphicsQualityController>(GraphicsPath);
        _contextExplorer ??= GetNodeOrNull<RingVoxelExplorerController>("../Explorer");
    }

    private static string FormatBytes(long bytes) => bytes < 1024L * 1024L
        ? $"{Math.Max(0, bytes) / 1024L} KiB"
        : $"{Math.Max(0, bytes) / (1024L * 1024L):0.0} MiB";

    private static string FormatUnsignedBytes(ulong bytes) => bytes > long.MaxValue
        ? "> 8 EiB"
        : FormatBytes((long)bytes);
}
