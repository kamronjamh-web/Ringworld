using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// WO-G2 of docs/gpu-offload-plan.md: GPU sector heights for the 8, 16 and 32 m levels. A level resolves its
/// <see cref="ProxyHeightBackend"/> when it configures its slot install (the K2 assemble writes straight into the level's slots, so the
/// backend exists only with slot install on). Sectors the GPU cannot own (edited, near an emitter site, at a ring edge, coastal, or any
/// level or recipe outside scope) stay on the CPU build, which is also the fallback for every failure.
/// </summary>
public partial class RingTerrainProxySector
{
    public enum ProxyHeightBackendKind { Cpu, Gpu }

    /// <summary>Debug master switch: <c>--proxy-heights=gpu</c> (default cpu). Not a menu setting; read once per process.</summary>
    internal static bool GpuHeightsEnabled { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--proxy-heights=gpu") >= 0;

    /// <summary>The coarse levels the GPU backend may serve.</summary>
    internal static bool SpacingUsesGpuHeights(int spacingMeters) => spacingMeters is 8 or 16 or 32;

    private ProxyGpuSectorBackend? _gpuBackend;
    private string _gpuBackendReason = "off";
    private bool _gpuBackendFailed;
    private bool _gpuSelfCheckQueued, _gpuSelfCheckPassed;
    private bool _gpuRefreshRequested;
    // Sectors the GPU already declined (coastal summary, pool or layout change): they build on the CPU for the rest of the session.
    private readonly HashSet<RingTerrainSectorCoordinate> _gpuCpuRoute = new();
    private readonly List<ProxyGpuSectorJob> _gpuScratchDropped = new();
    private static int[]? _placeholderOceanIndices;

    /// <summary>Per-level backend as <see cref="ProxyHeightBackendKind"/> (plan section 6): Gpu only while a backend is live and healthy.</summary>
    public ProxyHeightBackendKind HeightBackend => _gpuBackend is { Failed: false } && !_gpuBackendFailed ? ProxyHeightBackendKind.Gpu : ProxyHeightBackendKind.Cpu;
    public string HeightBackendReason => _gpuBackendReason;
    internal bool GpuHeightsBusy => _gpuBackend is { } backend && backend.Busy;
    internal long GpuBatchesDispatched => _gpuBackend?.BatchesDispatched ?? 0L;
    internal long GpuSectorsDispatched => _gpuBackend?.SectorsDispatched ?? 0L;
    internal int GpuTargetBatchSectors => _gpuBackend?.TargetBatchSectors ?? 0;
    internal long GpuMeasuredBatches => _gpuBackend?.MeasuredBatches ?? 0L;
    internal double GpuMaxBatchMicroseconds => _gpuBackend?.MaxMeasuredBatchMicroseconds ?? 0.0;
    internal int GpuCpuRoutedCount => _gpuCpuRoute.Count;

    /// <summary>
    /// Why a level cannot use the GPU backend, or "" when it can. Pure on its inputs so the routing contract test can exercise every row
    /// without a device. Compatibility renderer, blocky levels, the terminal global-morph level and unsupported recipes all stay on the CPU.
    /// </summary>
    internal static string GpuBackendUnavailableReason(
        bool flagOn, bool slotInstall, int spacing, int size, int parentSpacing, bool blocky, bool hasGlobalMorph,
        bool buildSkirts, bool runtimeSurface, bool hasDevice, TerrainGenerationParameters? recipe)
    {
        if (!flagOn) return "off";
        if (!SpacingUsesGpuHeights(spacing)) return "level not in the GPU set (8, 16, 32 m)";
        if (!slotInstall) return "slot install is off";
        if (!hasDevice) return "no RenderingDevice";
        if (blocky) return "blocky level";
        if (hasGlobalMorph) return "terminal global-morph level";
        if (!runtimeSurface || recipe == null) return "no runtime surface generation";
        if (!buildSkirts) return "skirts disabled";
        if (size != spacing * 64) return "sector is not 64 cells";
        if (parentSpacing != spacing * 2) return "parent is not 2x spacing";
        if (!ProxyGpuHeightKernel.IsSupported(recipe, out string reason)) return reason;
        return "";
    }

    /// <summary>
    /// Whether a sector may be built by the GPU. Edited and site-influenced sectors, sectors whose lattice or halo reaches a ring edge
    /// (structural walls, clamped rows) and sectors the GPU already declined as coastal stay on the CPU (docs/gpu-offload-plan.md 4.1).
    /// </summary>
    internal static bool GpuSectorRoutable(RingTerrainSectorCoordinate coordinate, int size, int spacing, int ringWidth,
        bool touchesEdits, bool touchesSite, bool declinedByGpu)
    {
        if (touchesEdits || touchesSite || declinedByGpu) return false;
        long acrossStart = (long)coordinate.AcrossSector * size;
        long acrossEnd = acrossStart + size;
        // Window halo, parent samples and the 16 m biome centres all stay two lattice steps inside the last presentation row.
        return acrossStart - 2L * spacing >= 0 && acrossEnd + 2L * spacing <= ringWidth - 2L - 8L;
    }

    /// <summary>Resolves the backend for this level. Called from <see cref="RefreshVertexTableInstall"/> once the slot pool exists.</summary>
    private void RefreshGpuHeightBackend()
    {
        RingTerrainSampler? sampler = _grid?.TerrainSampler as RingTerrainSampler;
        int size = Mathf.Max(32, SectorSizeMeters);
        string reason = GpuBackendUnavailableReason(GpuHeightsEnabled && !_gpuBackendFailed, _slotInstallActive && _vertexTable != null, SampleSpacingMeters, size,
            ResolveMorphTargetSpacingMeters(), UseBlockyHeightfield, _globalMorphTarget != null, BuildSectorSkirts,
            sampler?.UsesRuntimeSurfaceGeneration == true, RenderingServer.GetRenderingDevice() != null, sampler?.RuntimeSurfaceRecipe);
        if (_gpuBackendFailed) reason = "failed: " + _gpuBackendReason;
        if (reason.Length != 0)
        {
            if (_gpuBackend != null) DisposeGpuBackend();
            if (!_gpuBackendFailed) _gpuBackendReason = reason;
            return;
        }
        if (_gpuBackend != null) { _gpuBackendReason = "gpu"; return; }
        ProxyGpuClimateSettings? climate = sampler!.CreateGpuClimateSettings();
        if (climate == null) { _gpuBackendReason = "no climate settings"; return; }
        try
        {
            _gpuBackend = new ProxyGpuSectorBackend(SampleSpacingMeters, sampler.RuntimeSurfaceRecipe!, climate);
            _gpuBackendReason = "gpu";
            _gpuSelfCheckQueued = false;
            _gpuSelfCheckPassed = false;
            GD.Print($"{LodLabel} proxy heights: GPU backend requested ({SampleSpacingMeters} m)");
        }
        catch (Exception error)
        {
            _gpuBackendReason = "init failed: " + error.Message;
            _gpuBackendFailed = true;
        }
    }

    private void DisposeGpuBackend()
    {
        ProxyGpuSectorBackend? backend = _gpuBackend;
        _gpuBackend = null;
        if (backend == null) return;
        // Queued (not yet dispatched) jobs hand their slots back; results still in flight are dropped as they arrive.
        _gpuScratchDropped.Clear();
        backend.DropQueued(_ => true, _gpuScratchDropped);
        foreach (ProxyGpuSectorJob job in _gpuScratchDropped) ReleaseGpuJobSlot(job);
        _gpuScratchDropped.Clear();
        while (backend.TryTakeResult(out ProxyGpuSectorResult? result))
            if (result != null) ReleaseGpuJobSlot(result.Job);
        backend.Dispose();
    }

    private void FailGpuBackend(string reason)
    {
        if (_gpuBackendFailed) return;
        _gpuBackendFailed = true;
        _gpuBackendReason = reason;
        GD.PushWarning($"{LodLabel} proxy heights: GPU backend disabled, falling back to the CPU build: {reason}");
        DisposeGpuBackend();
        _gpuRefreshRequested = true;
    }

    private void ReleaseGpuJobSlot(ProxyGpuSectorJob job)
    {
        if (_vertexTable != null && _vertexTable.Generation == job.PoolGeneration) _vertexTable.Release(job.Slot);
    }

    /// <summary>Hands the slot of a GPU sector that was dropped before install back to the pool. No-op for worker sectors.</summary>
    private void ReleaseDroppedGpuSector(SectorData? data)
    {
        if (data?.SlotPayload is not { GpuSlot: >= 0 } payload) return;
        if (_vertexTable != null && _vertexTable.Generation == payload.PoolGeneration) _vertexTable.Release(payload.GpuSlot);
    }

    private void ReleaseAllDroppedGpuSectors()
    {
        foreach (SectorData pending in _pendingUploads) ReleaseDroppedGpuSector(pending);
        foreach (SectorData completed in _completedWorkerSectors) ReleaseDroppedGpuSector(completed);
    }

    /// <summary>
    /// Main thread, from <see cref="StartStreamingUpdate"/>: moves every GPU-routable sector out of <paramref name="missing"/> into the GPU
    /// queue (priority order kept) and leaves the CPU sectors behind. Queued jobs are re-sorted on every update; dispatched ones finish.
    /// </summary>
    private void RouteMissingToGpu(List<RingTerrainSectorCoordinate> missing, RingLodSurfaceEditSnapshot editSnapshot, RingTerrainSampler sampler,
        int size, int spacing, int parentSpacing, int ringWidth)
    {
        ProxyGpuSectorBackend? backend = _gpuBackend;
        VertexTablePool? pool = _vertexTable;
        if (backend == null || pool == null || _gpuBackendFailed || _globalMorphTarget != null || !_slotInstallActive) return;
        _gpuScratchDropped.Clear();
        backend.DropQueued(_ => true, _gpuScratchDropped);
        foreach (ProxyGpuSectorJob job in _gpuScratchDropped) ReleaseGpuJobSlot(job);
        _gpuScratchDropped.Clear();
        bool hasEdits = editSnapshot.EditedColumnCount > 0;
        long aroundLength = sampler.AroundRingVoxelLength;
        var cpu = new List<RingTerrainSectorCoordinate>(missing.Count);
        foreach (RingTerrainSectorCoordinate coordinate in missing)
        {
            if (backend.IsQueued(coordinate.AroundSector, coordinate.AcrossSector)) continue; // already in flight
            bool touchesEdits = hasEdits && SectorTouchesEdits(editSnapshot, coordinate, size, spacing, parentSpacing * 2, aroundLength, ringWidth);
            long around0 = coordinate.AroundSector * (long)size;
            long across0 = (long)coordinate.AcrossSector * size;
            bool touchesSite = sampler.IntersectsSiteInfluence(around0 - 4L * spacing, around0 + size + 4L * spacing, across0 - 4.0 * spacing, across0 + size + 4.0 * spacing);
            if (touchesEdits || touchesSite) RingProxyBuildCounters.AddGpuEditedToCpu(SampleSpacingMeters);
            if (!GpuSectorRoutable(coordinate, size, spacing, ringWidth, touchesEdits, touchesSite, _gpuCpuRoute.Contains(coordinate))
                || !pool.TryAcquire(out int slot))
            {
                cpu.Add(coordinate);
                continue;
            }
            bool selfCheck = !_gpuSelfCheckQueued;
            _gpuSelfCheckQueued = true;
            backend.Enqueue(new ProxyGpuSectorJob(coordinate.AroundSector, coordinate.AcrossSector, slot, _lastSurfaceEditVersion, pool.Generation, selfCheck));
        }
        missing.Clear();
        missing.AddRange(cpu);
    }

    private ProxyGpuSectorGlobals BuildGpuGlobals(RingLodGeometrySnapshot geometry, Transform3D ownerInverse, RingTerrainSampler sampler)
        => CreateGpuGlobals(SampleSpacingMeters, Mathf.Max(32, SectorSizeMeters), ResolveMorphTargetSpacingMeters(), RadialUnderlayOffsetMeters,
            _morphTargetUnderlayOffsetMeters, _morphTargetRestoresWaterUnderlay, SectorSkirtDepthMeters, geometry, ownerInverse, sampler,
            LowTerrainColor, HighTerrainColor);

    /// <summary>The per-level constants of a GPU batch, resolved from the level settings exactly as <see cref="StartStreamingUpdate"/> hands them to the CPU worker.</summary>
    internal static ProxyGpuSectorGlobals CreateGpuGlobals(int spacing, int size, int parentSpacing, float underlay, float parentUnderlay,
        bool parentRestoresWaterUnderlay, float skirtDepthMeters, RingLodGeometrySnapshot geometry, Transform3D ownerInverse,
        RingTerrainSampler sampler, Color low, Color high)
    {
        // Reference water colours: ResolveSeededColor's ocean branch at neutral moisture (the shader adds the moisture variation).
        TerrainSample shallow = new(-10, TerrainRegion.OceanShelf, 0.0f, 0.5f, 0.0f, 0.5f, 0.5f, TerrainBiomeId.Ocean, SurfaceWaterKind.Ocean, 0);
        TerrainSample deep = shallow with { SurfaceHeight = -600 };
        return new ProxyGpuSectorGlobals
        {
            Spacing = spacing, Size = size, ParentSpacing = parentSpacing,
            Underlay = underlay, ParentUnderlay = parentUnderlay,
            OwnWaterRestore = spacing is 2 or 4 or 8 ? underlay : 0.0f,
            ParentWaterRestore = parentRestoresWaterUnderlay ? parentUnderlay : 0.0f,
            SkirtDepth = Mathf.Max(0.1f, skirtDepthMeters),
            Radius = geometry.RingRadiusMeters, RadialDistance = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters,
            RecipeCircumferenceMeters = sampler.RuntimeSurfaceRecipe!.CircumferenceM, GeometryCircumferenceMeters = geometry.CircumferenceMeters,
            BedrockY = sampler.BedrockY, MaximumSurfaceHeight = sampler.MaximumPossibleSurfaceHeight,
            OceanAlpha = RingWaterModel.EncodeWaterKind(default, SurfaceWaterKind.Ocean).A,
            ShallowOcean = RingTerrainVisuals.ResolveSeededColor(shallow, 0, sampler, low, high),
            DeepOcean = RingTerrainVisuals.ResolveSeededColor(deep, 0, sampler, low, high),
            LowTerrain = low, HighTerrain = high,
            Grid = geometry.GridGlobalTransform.Basis, OwnerInverse = ownerInverse.Basis,
        };
    }

    /// <summary>Per-frame hook (start of <see cref="ApplyCompletedBatchCore"/>): collects finished GPU sectors and records at most one batch.</summary>
    private void PumpGpuHeights()
    {
        ProxyGpuSectorBackend? backend = _gpuBackend;
        if (backend == null)
        {
            if (_gpuRefreshRequested) RequestGpuStreamingRefresh();
            return;
        }
        if (backend.Failed) { FailGpuBackend(backend.Error); return; }
        if (!backend.Ready) return;
        VertexTablePool? pool = _vertexTable;
        while (backend.TryTakeResult(out ProxyGpuSectorResult? result))
            if (result != null) CompleteGpuResult(result, pool);
        if (backend.PumpWanted && pool != null && _grid?.TerrainSampler is RingTerrainSampler sampler)
        {
            ProxyGpuSectorGlobals globals = BuildGpuGlobals(_grid.CaptureLodGeometrySnapshot(), GlobalTransform.AffineInverse(), sampler);
            int generation = pool.Generation;
            if (backend.Pump(globals, () => new ProxyGpuSlotTargets(pool.PosRid, pool.ChordRid, pool.AttrRid, generation, pool.Ready)))
                RingProxyBuildCounters.AddGpuBatch(SampleSpacingMeters, backend.LastBatchSectors);
        }
        if (backend.InFlightCount > 0) backend.PollTimes();
        if (_gpuRefreshRequested) RequestGpuStreamingRefresh();
    }

    private void RequestGpuStreamingRefresh()
    {
        _gpuRefreshRequested = false;
        // Forget the positional sentinels so the next update rescans for missing sectors (the declined ones now route to the CPU).
        _lastAround = long.MinValue;
        _lastAcross = int.MinValue;
        _lastPredictedAround = long.MinValue;
        _lastPredictedAcross = int.MinValue;
        QueueStreamingUpdate(false);
    }

    private void CompleteGpuResult(ProxyGpuSectorResult result, VertexTablePool? pool)
    {
        ProxyGpuSectorJob job = result.Job;
        var coordinate = new RingTerrainSectorCoordinate(job.AroundSector, job.AcrossSector);
        bool poolValid = pool != null && pool.Generation == job.PoolGeneration;
        if (!poolValid) { _gpuRefreshRequested = true; return; } // the pool (and with it the slot) is gone; the next update rebuilds the sector
        if (result.Heights != null && !VerifyGpuHeights(result, coordinate))
        {
            pool!.Release(job.Slot);
            FailGpuBackend("self-check against the CPU height model failed");
            return;
        }
        if (result.Heights != null) _gpuSelfCheckPassed = true;
        if (result.Class == ProxyGpuSectorClass.Coastal)
        {
            // Partial wet triangles need the CPU's shoreline clip and compact ocean mesh: one round trip, then the CPU owns the sector.
            pool!.Release(job.Slot);
            _gpuCpuRoute.Add(coordinate);
            if (_gpuCpuRoute.Count > 8192) _gpuCpuRoute.Clear();
            RingProxyBuildCounters.AddGpuToCpu(SampleSpacingMeters);
            _gpuRefreshRequested = true;
            return;
        }
        SectorData data = BuildGpuSectorData(result, coordinate, pool!);
        long bytes = Math.Max(0L, data.EstimatedBytes);
        lock (_stagedMeshLock) { _completedWorkerSectors.Enqueue(data); _stagedMeshBytes += bytes; }
        RingProxyBuildCounters.AddGpuSector(SampleSpacingMeters, result.Class == ProxyGpuSectorClass.Ocean);
    }

    private SectorData BuildGpuSectorData(ProxyGpuSectorResult result, RingTerrainSectorCoordinate coordinate, VertexTablePool pool)
    {
        int cells = pool.Cells;
        bool ocean = result.Class == ProxyGpuSectorClass.Ocean;
        Aabb visual = new(result.VisualMin, result.VisualMax - result.VisualMin);
        if (result.Reach > 0.0f)
        {
            Vector3 reach = Vector3.One * result.Reach;
            visual = new Aabb(result.VisualMin - reach, result.VisualMax - result.VisualMin + reach * 2.0f);
        }
        Aabb meshBounds = new(result.MeshMin, result.MeshMax - result.MeshMin);
        SurfaceCoverageMask oceanMask = ocean
            ? new SurfaceCoverageMask(cells, 1, result.OceanCells, result.DepthBytes)
            : new SurfaceCoverageMask(cells, 1, new bool[cells * cells], new byte[(cells + 1) * (cells + 1)]);
        int[] oceanIndices = Array.Empty<int>();
        if (ocean)
        {
            // Only the length matters downstream (water present, cull margin, demand patch): the shared water grid draws the quads.
            _placeholderOceanIndices ??= new int[cells * cells * 6];
            oceanIndices = _placeholderOceanIndices;
        }
        var payload = new SlotInstallPayload
        {
            Blocks = EmptySlotBlocks, Cells = cells, HasChord = true, WaterGrid = ocean, CompactOcean = null, MeshBounds = meshBounds,
            GpuSlot = result.Job.Slot, PoolGeneration = result.Job.PoolGeneration,
        };
        return new SectorData(coordinate, _requestVersion, result.Job.EditVersion, Vector3.Zero,
            Array.Empty<Vector3>(), Array.Empty<Vector3>(), ocean ? new Color[1] : Array.Empty<Color>(), ocean ? new Vector2[1] : Array.Empty<Vector2>(),
            Array.Empty<Vector2>(), Array.Empty<Vector3>(), true, visual, Array.Empty<int>(), oceanIndices, null, oceanMask, ocean, null)
        {
            SlotPayload = payload,
        };
    }

    private static readonly VertexTableSlotBlocks EmptySlotBlocks = new() { PosMorph = Array.Empty<byte>(), Attr = Array.Empty<byte>() };

    /// <summary>The 64-point self-check of plan section 6: the first GPU batch's heights against the CPU presentation height.</summary>
    private bool VerifyGpuHeights(ProxyGpuSectorResult result, RingTerrainSectorCoordinate coordinate)
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler || result.Heights == null) return true;
        int size = Mathf.Max(32, SectorSizeMeters), spacing = SampleSpacingMeters;
        double worst = 0.0;
        for (int i = 0; i < 64; i++)
        {
            int x = 3 + (i % 8) * 8, z = 3 + (i / 8) * 8;
            long around = coordinate.AroundSector * (long)size + x * spacing;
            int across = coordinate.AcrossSector * size + z * spacing;
            float cpu = sampler.GetPresentationSurfaceHeight(around, across);
            float gpu = result.Heights[(z + 1) * 67 + (x + 1)];
            worst = Math.Max(worst, Math.Abs(cpu - gpu));
        }
        GpuSelfCheckWorstMeters = worst;
        return worst <= 0.05;
    }

    internal double GpuSelfCheckWorstMeters { get; private set; } = double.NaN;
    internal bool GpuSelfCheckPassed => _gpuSelfCheckPassed;
}
