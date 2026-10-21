using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only access to the global-LOD preparation pipeline.</summary>
/// <remarks>Kept in a partial so the regression runner drives the production worker and publisher.</remarks>
public partial class RingTerrainGlobalLod
{
    internal readonly record struct GlobalPreparationProbe(
        int SegmentIndex,
        int SegmentCount,
        int LocalAroundCount,
        int AcrossCount,
        int NextVertex,
        int VertexCount,
        ulong Version,
        Vector3 FirstTerrainVertex,
        Color FirstTerrainColor,
        bool FirstDisplayedWater);
    internal readonly record struct GlobalPreparedVertex(Vector3 TerrainVertex, Color TerrainColor, bool DisplayedWater);

    internal void ConfigureGlobalPreparationFixture(VoxelChunkGridManager grid, int aroundSegments = 64, int acrossSegments = 4, int segmentCount = 1)
    {
        _grid = grid;
        AroundSegments = aroundSegments;
        AcrossSegments = acrossSegments;
        GlobalSegmentCount = segmentCount;
        RingWidthMeters = Math.Max(1, Mathf.RoundToInt(grid.RingAcrossWidthMeters));
        UseMinMaxFeaturePreservation = false;
        EnableMacroTerrainShadows = false;
        BuildStructuralEdgeWalls = false;
        BuildStructuralUnderside = false;
        BuildTerrainRoofUnderside = false;
        BuildLowerHullCavityFloor = false;
        BuildTerrainOccluder = false;
        _observedEditVersion = grid.LodSurfaceEditVersion;
        SetProcess(false);
    }

    internal void StartGlobalPreparationFixture(int segmentIndex, int segmentCount, MeshInstance3D? existing, RingLodSurfaceEditSnapshot edits)
    {
        _ = ServicePreparedSegment(segmentIndex, segmentCount, existing, edits, ulong.MaxValue, out _);
    }

    internal bool PublishGlobalPreparationFixture(int segmentIndex, int segmentCount, MeshInstance3D? existing, RingLodSurfaceEditSnapshot edits)
    {
        return ServicePreparedSegment(segmentIndex, segmentCount, existing, edits, ulong.MaxValue, out bool published) && published;
    }

    internal bool TryPublishGlobalPreparationAtDeadlineFixture(int segmentIndex, int segmentCount, MeshInstance3D? existing, RingLodSurfaceEditSnapshot edits, ulong deadline)
    {
        return ServicePreparedSegment(segmentIndex, segmentCount, existing, edits, deadline, out bool published) && published;
    }

    internal void WaitForGlobalPreparationWorkerFixture()
    {
        Task? worker = _pendingSamplingTask;
        if (worker == null) return;
        try
        {
            if (!worker.Wait(10_000)) throw new TimeoutException("Global preparation worker did not complete within ten seconds.");
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
            return;
        }
        if (worker.IsFaulted) throw worker.Exception?.GetBaseException() ?? new InvalidOperationException("Global preparation worker faulted.");
    }

    internal GlobalPreparationProbe GetGlobalPreparationProbeFixture()
    {
        PendingSegmentPreparation state = _pendingPreparation
            ?? throw new InvalidOperationException("No global segment preparation is pending.");
        return new GlobalPreparationProbe(
            state.SegmentIndex, state.SegmentCount, state.LocalAroundCount, state.AcrossCount,
            state.NextVertex, state.VertexCount, state.Version,
            state.TerrainVertices[0], state.TerrainColors[0], state.DisplayedWater[0]);
    }
    internal GlobalPreparedVertex GetGlobalPreparedVertexFixture(int index)
    {
        PendingSegmentPreparation state = _pendingPreparation
            ?? throw new InvalidOperationException("No global segment preparation is pending.");
        if (index < 0 || index >= state.VertexCount) throw new ArgumentOutOfRangeException(nameof(index));
        return new GlobalPreparedVertex(state.TerrainVertices[index], state.TerrainColors[index], state.DisplayedWater[index]);
    }
    internal (int AroundStart, int LocalAroundCount, int AcrossCount, double AroundSpacing, double AcrossSpacing) GetGlobalPreparationSamplingFixture()
    {
        PendingSegmentPreparation state = _pendingPreparation
            ?? throw new InvalidOperationException("No global segment preparation is pending.");
        return (state.AroundStartIndex, state.LocalAroundCount, state.AcrossCount, state.AroundSpacing, state.AcrossSpacing);
    }

    internal void InvalidateGlobalPreparationFixture() => InvalidatePendingPreparation();
    internal void ExitGlobalPreparationFixture() => _ExitTree();
    internal Task? CaptureGlobalPreparationWorkerFixture() => _pendingSamplingTask;
    internal bool HasGlobalPreparationFixture => _pendingPreparation != null;
    internal bool GlobalPreparationReadyFixture => _pendingSamplingReady;
    internal int GlobalPreparationWorkerCountFixture => _pendingSamplingTask == null ? 0 : 1;
    internal MeshInstance3D? GetGlobalTerrainSegmentFixture(int index) => index >= 0 && index < _terrainSegments.Count ? _terrainSegments[index] : null;
    internal int RetainedGlobalTerrainSegmentCountFixture => _retainedTerrainSegments.Count;
    internal MeshInstance3D? GetRetainedGlobalTerrainSegmentFixture(int index) => index >= 0 && index < _retainedTerrainSegments.Count ? _retainedTerrainSegments[index] : null;
    internal void BuildGlobalRingFixture() => BuildGlobalRing();
    internal void ServiceInitialGlobalBuildFixture() => ServiceInitialGlobalBuild();
    internal void QueueDirtyGlobalSegmentFixture(int index) => QueueDirtySegment(index, _terrainSegments.Count);
    internal void SetDirtyGlobalEditsFixture(RingLodSurfaceEditSnapshot edits) => _pendingSegmentEdits = edits;
    internal int QueuedDirtyGlobalSegmentCountFixture => _dirtySegmentQueue.Count;
    internal void ServiceDirtyGlobalSegmentsFixture() => ServiceSegmentRebuilds();
    internal void InstallGlobalPreparationPyramidFixture(RingTerrainMinMaxPyramid pyramid, ulong observedEditVersion)
    {
        _heightPyramid = pyramid;
        _observedEditVersion = observedEditVersion;
    }
    internal void RefreshGlobalPreparationEditsFixture() => RefreshEditedSegments();
    internal void InstallPublishedGlobalSegmentsFixture(int count)
    {
        for (int index = 0; index < count; index++)
        {
            var segment = new MeshInstance3D { Name = $"GlobalTerrainMappingFixture_{index:D3}" };
            AddChild(segment);
            _terrainSegments.Add(segment);
        }
    }
    internal bool IsDirtyGlobalSegmentQueuedFixture(int index) => _queuedDirtySegments.Contains(index);
    internal void WaitForAllGlobalPreparationWorkersFixture()
    {
        for (int attempt = 0; _initialSegmentQueue.Count > 0; attempt++)
        {
            if (attempt >= 256) throw new InvalidOperationException("Initial global preparation did not drain after 256 service attempts.");
            ServiceInitialGlobalBuild();
            WaitForGlobalPreparationWorkerFixture();
        }
    }

    internal void AddQualityIdentityFixtures(MeshInstance3D hull, Node3D occluder)
    {
        AddChild(hull);
        AddChild(occluder);
        _hullSegments.Add(hull);
        _occluderRoot = occluder;
    }

    internal void AddHullRoofGateFixtures(int count)
    {
        _hullRoofTotalCount = count;
        for (int index = 0; index < count; index++)
        {
            var roof = new MeshInstance3D { Name = $"GlobalStructuralRoofFixture_{index:D3}", Mesh = new BoxMesh(), Visible = false };
            AddChild(roof);
            _hullRoofSegments.Add(roof);
        }
    }
    internal void RunRegionalVisibilityFixture(Node3D focus)
    {
        _focus = focus;
        EnableFarDistanceFade = true;
        UpdateRegionalSegmentVisibility(1.0);
    }
    internal bool IsHullRoofVisibleFixture(int index) => _hullRoofSegments[index].Visible;

    internal MeshInstance3D? FirstHullFixture => _hullSegments.Count == 0 ? null : _hullSegments[0];
    internal Node3D? OccluderFixture => _occluderRoot;
    internal bool PublishedGlobalMeshContainsVertexFixture(int segmentIndex, Vector3 expected)
    {
        if (GetGlobalTerrainSegmentFixture(segmentIndex)?.Mesh is not ArrayMesh mesh || mesh.GetSurfaceCount() == 0) return false;
        var arrays = mesh.SurfaceGetArrays(0);
        foreach (Vector3 vertex in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
            if (vertex.DistanceTo(expected) < 0.001f) return true;
        return false;
    }

    internal bool TryCapturePublishedGlobalMorphFixture(out RingTerrainGlobalMorphSnapshot snapshot)
        => TryCaptureReadyMorphSnapshot(out snapshot);

    internal bool TrySamplePublishedGlobalMorphFixture(double aroundMeters, double acrossMeters, out Vector3 worldPosition)
    {
        worldPosition = default;
        return _grid != null
            && TryCaptureReadyMorphSnapshot(out RingTerrainGlobalMorphSnapshot snapshot)
            && snapshot.TrySampleTerrainWorld(
                _grid.CaptureLodGeometrySnapshot(), aroundMeters, acrossMeters, out worldPosition);
    }

    /// <summary>Verifies the shared shoreline clip and the global-mesh water triangle that uses it.</summary>
    public static string RunShorelineClipContract()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Shoreline clip contract failed: {message}");
        }

        // Pure clip: one wet corner -> triangle; two wet corners -> quad; dry -> nothing; wet -> unchanged.
        Span<Vector3> weights = stackalloc Vector3[RingShorelineClip.MaxVertices];
        Span<float> lengths = stackalloc float[3] { 100.0f, 100.0f, 100.0f };
        Span<float> oneWet = stackalloc float[3] { 10.0f, -10.0f, -10.0f };
        int count = RingShorelineClip.ClipTriangle(oneWet, lengths, 0.0f, weights);
        Require(count == 3, $"one wet corner must clip to a triangle, got {count}");
        Require(weights[1] == new Vector3(1.0f, 0.0f, 0.0f), "wet corner must be kept");
        Require(weights[0].IsEqualApprox(new Vector3(0.5f, 0.0f, 0.5f)) && weights[2].IsEqualApprox(new Vector3(0.5f, 0.5f, 0.0f)),
            "cut vertices must sit at the zero contour");
        Span<float> twoWet = stackalloc float[3] { 10.0f, 10.0f, -10.0f };
        count = RingShorelineClip.ClipTriangle(twoWet, lengths, 0.0f, weights);
        Require(count == 4, $"two wet corners must clip to a quad, got {count}");
        Span<float> dry = stackalloc float[3] { -1.0f, -2.0f, -3.0f };
        Require(RingShorelineClip.ClipTriangle(dry, lengths, 0.0f, weights) == 0, "dry triangle must vanish");
        Span<float> wet = stackalloc float[3] { 1.0f, 2.0f, 3.0f };
        Require(RingShorelineClip.ClipTriangle(wet, lengths, 0.0f, weights) == 3, "wet triangle must be kept whole");
        // The overlap bias pushes the cut onto the dry side by overlap / edge length.
        float plain = RingShorelineClip.EdgeInterpolation(10.0f, -10.0f, true, 100.0f, 0.0f);
        float biased = RingShorelineClip.EdgeInterpolation(10.0f, -10.0f, true, 100.0f, 1.0f);
        Require(Mathf.IsEqualApprox(plain, 0.5f) && Mathf.IsEqualApprox(biased, 0.51f), "overlap must bias toward the dry side");

        // Global mesh triangle: clipped area must follow the shoreline, not the 2-of-3 vertex rule.
        var state = new PendingSegmentPreparation
        {
            SegmentIndex = 0, SegmentCount = 1, AroundStartIndex = 0, LocalAroundCount = 1, AcrossCount = 1,
            Existing = null, Sampler = null!, Edits = null!, AroundSpacing = 100.0, AcrossSpacing = 100.0,
            UsesPresentationSurface = true, PreserveFeatures = false, Underlay = 0.0f,
            LowColor = Colors.White, HighColor = Colors.White, WorldToLocal = Transform3D.Identity,
            Geometry = default!, HeightPyramid = null,
            TerrainVertices = new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 0, 100) },
            WaterVertices = new[] { new Vector3(0, 10, 0), new Vector3(100, 10, 0), new Vector3(0, 10, 100) },
            TerrainCanonicalHeights = new float[3], RawTerrainHeights = new[] { 0.0f, 10.0f, 20.0f },
            RawWaterHeights = new[] { 10.0f, 10.0f, 20.0f }, WaterCanonicalOffset = 0.0f,
            TerrainColors = new Color[3], WaterColors = new[] { Colors.Blue, Colors.Blue, Colors.Blue },
            DisplayedWater = new[] { true, false, false }, Version = 0
        };
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        int triangles = AddClippedWaterTriangle(surface, state, 0, 1, 2);
        Require(triangles == 1, $"expected one clipped water triangle, got {triangles}");
        ArrayMesh mesh = surface.Commit();
        Vector3[] vertices = (Vector3[])mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
        float area = (vertices[1] - vertices[0]).Cross(vertices[2] - vertices[0]).Length() * 0.5f;
        // The wet corner reaches ~1.0 along edge 0->1 and 0.5 along edge 0->2: half the 5000 m^2 triangle.
        Require(Mathf.Abs(area - 2500.0f) < 100.0f,
            $"clipped area {area} must follow the contour (about 2500 of the 5000 m^2 triangle)");
        state.DisplayedWater = new[] { true, true, false };
        surface.Begin(Mesh.PrimitiveType.Triangles);
        Require(AddClippedWaterTriangle(surface, state, 0, 1, 2) == 2, "two wet corners must produce a quad (two triangles)");
        return $"pure=ok oneWetArea={area:0.0}";
    }
}
