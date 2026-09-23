using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

/// <summary>Real camera, production tile scheduler, GPU selection and CPU fallback.</summary>
public partial class GpuGrassIntegrationTest : Node
{
    public string Result { get; private set; } = "RUNNING";
    private RingStandingGrassLayer _layer = null!;
    private Camera3D _camera = null!;
    private RingTerrainSampler _sampler = null!;
    private RingCanonicalPosition _focus = new(186000, 0, 141000);
    private RingLodGeometrySnapshot _geometry;
    private RingLodSurfaceEditSnapshot _edits = null!;
    private const long Circumference = 981_748L * 32L;

    public async void Start()
    {
        string result;
        try { await Run(); result = "PASS production grass LOD union, stationary skip, frustum, rebase, edit, CPU fallback"; }
        catch (Exception error) { result = "FAIL " + error; }
        if (_layer != null) _layer.QueueFree();
        if (_camera != null) _camera.QueueFree();
        await Frames(3);
        _sampler?.Dispose();
        Result = result;
    }

    private async Task Run()
    {
        Check(RingStandingGrassLayer.ValidateTileFrustumCullingForTests(), "coarse frustum bounds regression");
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>(
            "res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        _sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000d, 318000, 8000);
        _camera = new Camera3D { Far = 2000 };
        AddChild(_camera);
        _camera.Current = true;
        _layer = new RingStandingGrassLayer { UseGpuCulling = true, GpuFrustumCulling = false,
            VisibilityRadiusMeters = 72, SpatialTileMeters = 48, MaximumInstances = 2000,
            MaximumInstancesPerTile = 256, MaximumResidentTiles = 8, MaximumTileBuildsInFlight = 2,
            MaximumTileUploadsPerFrame = 2, LodNearDensity = 1, LodFarDensity = 1 };
        AddChild(_layer);
        _geometry = Geometry(Vector3.Zero);
        _edits = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        await Settle();
        await VerifyDenseClumpLayerContract();
        var tile = _layer.CaptureTileAuditsForTests().Where(item => item.InstanceCount > 20)
            .OrderBy(item => Math.Pow(item.Around * 48.0 + 24.0 - _focus.AroundMeters, 2)
                + Math.Pow(item.Across * 48.0 + 24.0 - _focus.AcrossMeters, 2)).First();
        Check(_layer.TryGetGpuGrassSourceForTests(tile.Around, tile.Across, out float[] source), "source missing");
        await WaitGpu(tile, false);
        var initial = await Read(tile, false);
        Check(initial.Count == tile.InstanceCount && Same(source, initial.Rows), "unfiltered 20-float payload copy");
        await StepFrames(2);
        long stationaryCompleted = _layer.GpuCullingCompletedUpdates;
        await StepFrames(3);
        Check(_layer.GpuCullingCompletedUpdates == stationaryCompleted && _layer.GpuCullingSkippedUnchangedUpdates > 0,
            "stationary GPU grass did not skip unchanged culling");

        MultiMeshInstance3D renderer = Renderer(tile);
        Vector3 center = Center(source, renderer.GlobalTransform);
        Vector3 up = renderer.GlobalBasis.Y.Normalized();
        Vector3 cameraUp = renderer.GlobalBasis.Z.Normalized();
        _camera.GlobalPosition = center + up * 35;
        _camera.LookAt(center, cameraUp);
        long cameraCompleted = _layer.GpuCullingCompletedUpdates;
        await StepFrames(2);
        Check(_layer.GpuCullingCompletedUpdates > cameraCompleted, "camera transform did not refresh GPU grass culling");
        int builds = _layer.TotalGrassBuildCount;
        _layer.ApplyGrassQuality(true, 40, 200, 64, 1, 1);
        await Settle();
        await WaitGpu(tile, true);
        await StepFrames(4);
        var audit = Find(tile);
        Check(audit.RenderInstanceId == tile.RenderInstanceId && audit.IntermediateRenderInstanceId != 0
            && audit.PlacementHash == tile.PlacementHash && _layer.TotalGrassBuildCount >= builds,
            "late distance LOD rebuilt near roots or failed hierarchical preparation");
        builds = _layer.TotalGrassBuildCount;
        Check(renderer.GetInstanceShaderParameter("grass_gpu_culled").AsBool(), "GPU draw repeated vertex LOD");
        var near = await Read(tile, false);
        var middle = await Read(tile, true);
        Check(near.Count > 0 && middle.Count > 0, $"transition passes empty: {near.Count}, {middle.Count}");
        ValidateUnion(source, near.Rows, middle.Rows);
        GD.Print($"GRASS_GPU_LOD_COUNTS roots={tile.InstanceCount} near={near.Count} intermediate={middle.Count}");

        // Geometry rebasing may rotate and translate the local frame. Move the
        // camera by the actual old-to-new transform, then compare valid GPU rows.
        Transform3D previous = renderer.GlobalTransform;
        Transform3D cameraBefore = _camera.GlobalTransform;
        _geometry = Geometry(new Vector3(100, 0, 0));
        _layer.StepForTests(_sampler, _edits, _focus, _geometry);
        Transform3D relocation = renderer.GlobalTransform * previous.AffineInverse();
        _camera.GlobalTransform = relocation * cameraBefore;
        await StepFrames(4);
        Check(Find(tile).RenderInstanceId == tile.RenderInstanceId && _layer.TotalGrassBuildCount == builds,
            "rebase rebuilt resident roots");
        var rebasedNear = await Read(tile, false);
        var rebasedMiddle = await Read(tile, true);
        Check(Same(near.Rows, rebasedNear.Rows) && Same(middle.Rows, rebasedMiddle.Rows), "rebase changed GPU ownership or payload");

        _layer.ApplyGrassQuality(false, 40, 200, 64, 1, 1);
        _layer.GpuFrustumCulling = true;
        center = Center(source, renderer.GlobalTransform);
        up = renderer.GlobalBasis.Y.Normalized();
        cameraUp = renderer.GlobalBasis.Z.Normalized();
        _camera.GlobalPosition = center + up * 80;
        _camera.LookAt(_camera.GlobalPosition + up, cameraUp);
        int requestedBeforeTileCull = _layer.GpuCullingRequestedBatchCount;
        await StepFrames(4);
        Check(!renderer.Visible && _layer.GpuCullingRequestedBatchCount < requestedBeforeTileCull,
            "coarse tile culling did not hide the off-frustum renderer or reduce GPU requests");
        _camera.LookAt(center, cameraUp);
        await WaitGpu(tile, false);
        await StepFrames(2);
        Check(renderer.Visible && (await Read(tile, false)).Count > 0, "coarse tile culling did not restore a fresh visible GPU batch");

        // The coarse path intentionally prevents a dispatch for a fully hidden
        // tile. Disable only that layer to retain coverage for the shader/compute
        // per-root frustum rejection contract.
        int toggleBuilds = _layer.TotalGrassBuildCount;
        ulong toggleHash = Find(tile).PlacementHash;
        _layer.CullGrassTiles = false;
        _camera.LookAt(_camera.GlobalPosition + up, cameraUp);
        await WaitGpu(tile, false);
        await StepFrames(2);
        Check(renderer.Visible && (await Read(tile, false)).Count == 0, "per-root frustum culling did not reject roots when coarse culling was disabled");
        _camera.LookAt(center, cameraUp);
        _layer.GroupGrassComputePhases = !_layer.GroupGrassComputePhases;
        await WaitGpu(tile, false);
        await StepFrames(2);
        Check(_layer.TotalGrassBuildCount == toggleBuilds && Find(tile).PlacementHash == toggleHash
            && Find(tile).RenderInstanceId == tile.RenderInstanceId, "runtime compute/cull toggles rebuilt or replaced grass roots");
        _layer.CullGrassTiles = true;

        Rid indirectResource = renderer.Multimesh!.GetRid();
        _layer.UseGpuCulling = false;
        await StepFrames(3);
        Check(_layer.GpuCullingBatchCount == 0 && _layer.GpuCullingBytes == 0, "CPU fallback retained GPU batches");
        Check(renderer.Multimesh.GetRid() != indirectResource && Same(renderer.Multimesh.Buffer, source),
            "CPU fallback failed to restore a fresh complete buffer");
        Check(!renderer.GetInstanceShaderParameter("grass_gpu_culled").AsBool(), "CPU fallback skipped vertex LOD");
        _layer.UseGpuCulling = true;
        await WaitGpu(tile, false);
        await StepFrames(3);
        Check((await Read(tile, false)).Count > 0, "GPU re-enable did not draw");

        // A replaced terrain snapshot invalidates the sources through the same
        // production removal/install path as edits, including indirect retirement.
        _edits = new RingLodSurfaceEditSnapshot(3, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        await Settle();
        var edited = Find(tile);
        Check(edited.RenderInstanceId != tile.RenderInstanceId && _layer.TotalGrassBuildCount > builds,
            "terrain revision did not replace GPU sources");
        await WaitGpu(edited, false);
        await StepFrames(3);
        Check((await Read(edited, false)).Count > 0, "edited grass failed to return");
    }

    private async Task VerifyDenseClumpLayerContract()
    {
        var layer = new RingStandingGrassLayer
        {
            UseGpuCulling = false, UseGrassClumps = false, BladesPerSquareMeter = 16,
            VisibilityRadiusMeters = 24, SpatialTileMeters = 16, MaximumResidentTiles = 4,
            MaximumInstances = 800, MaximumInstancesPerTile = 800,
            MaximumTileBuildsInFlight = 1, MaximumTileUploadsPerFrame = 2, LodNearDensity = 1, LodFarDensity = 1
        };
        AddChild(layer); layer.InitializeForTests();
        try
        {
            await Settle(layer);
            RingStandingGrassLayer.TileAudit baseline = layer.CaptureTileAuditsForTests().First(tile => tile.InstanceCount > 0);
            int baselineSources = layer.ResidentGrassInstanceCount;
            long builds = layer.TotalGrassBuildCount;
            layer.UseGrassClumps = true;
            await Settle(layer);
            RingStandingGrassLayer.TileAudit clumped = layer.CaptureTileAuditsForTests().First(tile => tile.Around == baseline.Around && tile.Across == baseline.Across);
            RingGrassTileData clumpedData = layer.CaptureTileDataForTests().First(data => data.Key.Around == baseline.Around && data.Key.Across == baseline.Across);
            Mesh mesh = Renderer(clumped).Multimesh?.Mesh ?? throw new InvalidOperationException("dense clump CPU mesh missing");
            int vertices = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
            Check(layer.TotalGrassBuildCount > builds && clumpedData.BladesPerSource == 4
                && layer.GrassBladesPerSource == 4 && layer.MaximumGrassSourceInstances == 200
                && layer.MaximumGrassSourcesPerTile == 200 && layer.ResidentGrassInstanceCount <= 200
                && clumped.InstanceCount <= 200 && clumped.InstanceCount * 4 <= Math.Max(4, baselineSources)
                && vertices == 32 * 3, "dense clump toggle did not rebuild under source budgets with the CPU clump mesh");
        }
        finally { layer.QueueFree(); await Frames(2); }
    }

    private async Task Settle()
    {
        for (int i = 0; i < 240; i++)
        {
            await StepFrames(1);
            if (_layer.PendingGrassTileBuildCount == 0 && _layer.PendingGrassUploadCount == 0
                && _layer.ResidentGrassTileCount > 0) return;
        }
        throw new TimeoutException("grass scheduler did not settle");
    }
    private async Task Settle(RingStandingGrassLayer layer)
    {
        for (int i = 0; i < 240; i++)
        {
            layer.StepForTests(_sampler, _edits, _focus, _geometry);
            await Frames(1);
            if (layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0
                && layer.ResidentGrassTileCount > 0) return;
        }
        throw new TimeoutException("dense clump scheduler did not settle");
    }
    private async Task WaitGpu(RingStandingGrassLayer.TileAudit tile, bool paired)
    {
        for (int i = 0; i < 180; i++)
        {
            await StepFrames(1);
            if (_layer.IsGpuGrassBatchReadyForTests(tile.Around, tile.Across, false)
                && (!paired || _layer.IsGpuGrassBatchReadyForTests(tile.Around, tile.Across, true))) return;
        }
        throw new TimeoutException("grass GPU not ready: " + _layer.GpuCullingStatus);
    }
    private async Task StepFrames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _layer.StepForTests(_sampler, _edits, _focus, _geometry);
            await Frames(1);
        }
    }
    private async Task Frames(int count)
    { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task<(uint Count, float[] Rows)> Read(RingStandingGrassLayer.TileAudit tile, bool intermediate)
    {
        var completion = new TaskCompletionSource<(uint[], float[])>();
        Check(_layer.RequestGpuGrassReadbackForTests(tile.Around, tile.Across, intermediate,
            (commands, rows) => completion.TrySetResult((commands, rows))), "grass batch missing");
        var (commands, rows) = await completion.Task;
        Check(commands.Length >= 5, "indirect command missing");
        uint count = commands[1];
        Check(count <= tile.InstanceCount, "grass command exceeded capacity");
        var valid = new float[count * 20];
        Array.Copy(rows, valid, valid.Length);
        return (count, valid);
    }
    private RingLodGeometrySnapshot Geometry(Vector3 origin) =>
        new(Transform3D.Identity, Vector3.Zero, origin, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, _focus);
    private RingStandingGrassLayer.TileAudit Find(RingStandingGrassLayer.TileAudit tile) =>
        _layer.CaptureTileAuditsForTests().First(item => item.Around == tile.Around && item.Across == tile.Across);
    private static MultiMeshInstance3D Renderer(RingStandingGrassLayer.TileAudit tile) =>
        GodotObject.InstanceFromId(tile.RenderInstanceId) as MultiMeshInstance3D
        ?? throw new InvalidOperationException("grass renderer was released");
    private static Vector3 Center(float[] source, Transform3D transform)
    {
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < source.Length; i += 20) sum += transform * new Vector3(source[i + 3], source[i + 7], source[i + 11]);
        return sum / (source.Length / 20);
    }
    private static bool Same(float[] first, float[] second) => first.AsSpan().SequenceEqual(second);
    private static void ValidateUnion(float[] source, float[] near, float[] middle)
    {
        int count = source.Length / 20;
        var seen = new bool[count];
        foreach (float[] rows in new[] { near, middle })
            for (int offset = 0; offset < rows.Length; offset += 20)
            {
                int match = -1;
                for (int id = 0; id < count; id++)
                    if (rows.AsSpan(offset, 20).SequenceEqual(source.AsSpan(id * 20, 20))) { match = id; break; }
                Check(match >= 0, "compacted grass changed its source payload");
                Check(!seen[match], "root drawn by both LOD passes");
                seen[match] = true;
            }
        Check(Array.TrueForAll(seen, value => value), "root disappeared between LOD passes");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
