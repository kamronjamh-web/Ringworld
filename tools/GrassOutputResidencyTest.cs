using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

/// <summary>Camera culling retires native outputs, preserves roots, and restores real visible grass.</summary>
public partial class GrassOutputResidencyTest : Node
{
    public string Result { get; private set; } = "RUNNING";
    private const long Circumference = 981_748L * 32L;
    private readonly RingCanonicalPosition _focus = new(186000, 0, 141000);
    private RingStandingGrassLayer _layer = null!;
    private RingTerrainSampler _sampler = null!;
    private RingLodSurfaceEditSnapshot _edits = null!;
    private RingLodGeometrySnapshot _geometry;
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;

    public async void Start()
    {
        string result;
        try { await Verify(); result = "PASS hidden native/GPU retirement, preserved roots, bounded restore, LOD union, pixels, rebase, CPU fallback, culling A/B"; }
        catch (Exception error) { result = "FAIL " + error; }
        finally { _viewport?.QueueFree(); await Frames(3); _sampler?.Dispose(); }
        Result = result;
    }

    private async Task Verify()
    {
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        _sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000d, 318000, 8000);
        _viewport = new SubViewport { Size = new Vector2I(512, 512), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(_viewport);
        _viewport.AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 1
        } });
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-60, 20, 0), LightEnergy = 1 });
        _camera = new Camera3D { Far = 2000, Fov = 100 }; _viewport.AddChild(_camera); _camera.Current = true;
        _layer = new RingStandingGrassLayer
        {
            UseGpuCulling = true, GpuFrustumCulling = false, CullGrassTiles = false,
            EnableHierarchicalCoverage = false, VisibilityRadiusMeters = 72, SpatialTileMeters = 48,
            MaximumInstances = 2000, MaximumInstancesPerTile = 256, MaximumResidentTiles = 8,
            MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 1,
            // This residency/pixel fixture retains its historical visible scale;
            // the meadow comparison separately exercises production short grass.
            GrassMinimumHeightMeters = .42f, GrassMaximumHeightMeters = .92f
        };
        _viewport.AddChild(_layer);
        _geometry = Geometry(Vector3.Zero);
        _edits = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        await Settle();
        var tile = _layer.CaptureTileAuditsForTests().First(item => item.InstanceCount > 20);
        Check(_layer.TryGetGpuGrassSourceForTests(tile.Around, tile.Across, out float[] source), "source missing");
        var initialRenderer = Renderer(tile);
        Vector3 center = Center(source, initialRenderer.GlobalTransform);
        Vector3 up = initialRenderer.GlobalBasis.Y.Normalized();
        Vector3 forward = initialRenderer.GlobalBasis.Z.Normalized();
        _camera.GlobalPosition = center + up * 25 + forward * 25;
        _camera.LookAt(center, up);
        _layer.ApplyGrassQuality(true, 40, 200, 64, 1, 1);
        _layer.CullGrassTiles = true;
        await Settle(); await WaitGpu(tile, true); await StepFrames(4);
        await CheckUnion(tile, source);
        int originalPixels = Pixels();
        Check(originalPixels > 20, "baseline GPU grass rendered no visible pixels");
        var baseline = _layer.CaptureTileAuditsForTests().ToDictionary(item => (item.Around, item.Across), item => (item.PlacementHash, item.InstanceCount));
        int builds = _layer.TotalGrassBuildCount;
        Transform3D visibleCamera = _camera.GlobalTransform;

        _layer.GpuFrustumCulling = true;
        _camera.LookAt(_camera.GlobalPosition + up, forward);
        ulong[] oldIds = _layer.CaptureTileAuditsForTests().SelectMany(item => new[] { item.RenderInstanceId, item.IntermediateRenderInstanceId }).Where(id => id != 0).ToArray();
        var backend = (RingGpuVegetationCompute)typeof(RingStandingGrassLayer).GetField("_gpuCompute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_layer)!;
        await StepFrames(12);
        Check(_layer.CaptureTileAuditsForTests().All(item => item.RenderInstanceId == 0 && item.IntermediateRenderInstanceId == 0)
            && backend.BatchCount == 0 && backend.EstimatedBytes == 0, "off-frustum tiles retained native/GPU outputs");
        Check(oldIds.All(id => !GodotObject.IsInstanceValid(GodotObject.InstanceFromId(id))), "native nodes survived output retirement");
        Check(_layer.GpuCullingRequestedBatchCount == 0 && Pixels() == 0, "hidden grass continued drawing");
        CheckRoots(baseline, builds);

        // Rebase while rendererless, then relocate the camera into the same
        // canonical view. Visibility must use current geometry, not old bounds.
        RingGrassTileData data = _layer.CaptureTileDataForTests().First(item => item.Key.Around == tile.Around && item.Key.Across == tile.Across);
        Transform3D before = RingGrassCoordinates.CreateTileWorldTransform(data.Bounds, data.AnchorHeight, _geometry);
        _geometry = Geometry(new Vector3(100, 0, 0));
        Transform3D after = RingGrassCoordinates.CreateTileWorldTransform(data.Bounds, data.AnchorHeight, _geometry);
        _camera.GlobalTransform = after * before.AffineInverse() * visibleCamera;
        _layer.GpuFrustumCulling = false;
        await Settle(); await WaitGpu(tile, true); await StepFrames(4);
        CheckRoots(baseline, builds);
        await CheckUnion(tile, source);
        int restoredPixels = Pixels();
        Check(restoredPixels > 20 && Math.Abs(restoredPixels - originalPixels) <= Math.Max(30, originalPixels * .25),
            $"visible grass pixels did not restore after rebase: {originalPixels}->{restoredPixels}");
        _layer.GpuFrustumCulling = true;
        await Settle(); await StepFrames(4);
        Check(Pixels() > 20, "restored camera frustum left GPU grass blank");
        CheckRoots(baseline, builds);

        // CPU fallback uses the same output retirement and source identity.
        _layer.GpuFrustumCulling = false;
        _layer.UseGpuCulling = false; await StepFrames(4);
        Check(Renderer(Find(tile)).Multimesh!.Buffer.AsSpan().SequenceEqual(source), "CPU fallback changed canonical rows");
        Check(Pixels() > 20, "CPU fallback became blank");
        _layer.GpuFrustumCulling = true;
        visibleCamera = _camera.GlobalTransform;
        up = Renderer(Find(tile)).GlobalBasis.Y.Normalized();
        forward = Renderer(Find(tile)).GlobalBasis.Z.Normalized();
        _camera.LookAt(_camera.GlobalPosition + up, forward); await StepFrames(8);
        Check(_layer.CaptureTileAuditsForTests().All(item => item.RenderInstanceId == 0 && item.IntermediateRenderInstanceId == 0), "CPU culling retained outputs");
        CheckRoots(baseline, builds);
        _camera.GlobalTransform = visibleCamera; await Settle(); await StepFrames(4);
        Check(Pixels() > 20, "CPU grass did not revive"); CheckRoots(baseline, builds);

        // The existing control remains a useful A/B: all outputs may stay
        // allocated when coarse culling is explicitly disabled.
        _layer.CullGrassTiles = false;
        _camera.LookAt(_camera.GlobalPosition + up, forward); await Settle(); await StepFrames(4);
        Check(_layer.CaptureTileAuditsForTests().All(item => item.RenderInstanceId != 0 && item.IntermediateRenderInstanceId != 0), "coarse culling disabled still retired outputs");
        CheckRoots(baseline, builds);
        await VerifyCoordinatedOutputCap(center, up, visibleCamera);
        GD.Print($"GRASS_OUTPUT_RESIDENCY_PIXELS before={originalPixels} restored={restoredPixels} roots={_layer.ResidentGrassInstanceCount}");
    }

    private async Task VerifyCoordinatedOutputCap(Vector3 center, Vector3 up, Transform3D visibleCamera)
    {
        // The effective resident window is much smaller than the requested range
        // in this bounded fixture. Retire outputs at that cap even when the
        // frustum A/B is off, and when distance LOD is disabled entirely.
        _layer.EnableGrassAppearance = true;
        _layer.EnableCoordinatedGrassHandoff = true;
        _layer.CullGrassTiles = false;
        _layer.GpuFrustumCulling = false;
        foreach (bool distanceLod in new[] { true, false })
        {
            _layer.EnableDistanceLod = distanceLod;
            _camera.GlobalTransform = visibleCamera;
            await Settle();
            var baseline = _layer.CaptureTileAuditsForTests().ToDictionary(item => (item.Around, item.Across), item => (item.PlacementHash, item.InstanceCount));
            int builds = _layer.TotalGrassBuildCount;
            float requested = distanceLod ? _layer.LodFarDistanceMeters : _layer.VisibilityRadiusMeters;
            Check(_layer.EffectiveGrassDrawDistance() < requested * .5f, "coordinated fixture did not constrain its requested range");
            _camera.GlobalPosition = center + up * 60f;
            _camera.LookAt(center, visibleCamera.Basis.Z.Normalized());
            await StepFrames(8);
            Check(_layer.CaptureTileAuditsForTests().All(item => item.RenderInstanceId == 0 && item.IntermediateRenderInstanceId == 0),
                $"coordinated cutoff retained native outputs with distance LOD {distanceLod}");
            Check(_layer.GrassDistanceRejectedTileCount > 0, "coordinated output cutoff did not reject distant tiles");
            CheckRoots(baseline, builds);
        }
    }

    private async Task Settle()
    {
        for (int i = 0; i < 300; i++)
        {
            Step(); await Frames(1);
            if (_layer.PendingGrassTileBuildCount == 0 && _layer.PendingGrassUploadCount == 0 && _layer.ResidentGrassTileCount > 0) return;
        }
        throw new TimeoutException("output uploads did not settle");
    }
    private async Task WaitGpu(RingStandingGrassLayer.TileAudit tile, bool paired)
    {
        for (int i = 0; i < 180; i++)
        {
            Step(); await Frames(1);
            if (_layer.IsGpuGrassBatchReadyForTests(tile.Around, tile.Across, false)
                && (!paired || _layer.IsGpuGrassBatchReadyForTests(tile.Around, tile.Across, true))) return;
        }
        throw new TimeoutException("visible GPU batches did not revive: " + _layer.GpuCullingStatus);
    }
    private void Step()
    {
        int before = _layer.TotalGrassUploadCount;
        _layer.StepForTests(_sampler, _edits, _focus, _geometry);
        Check(_layer.TotalGrassUploadCount - before <= _layer.MaximumTileUploadsPerFrame, "revive bypassed upload budget");
        Check(_layer.ResidentGrassInstanceCount <= _layer.MaximumGrassSourceInstances, "revive exceeded root budget");
    }
    private async Task StepFrames(int count) { for (int i = 0; i < count; i++) { Step(); await Frames(1); } }
    private void CheckRoots(Dictionary<(long, long), (ulong, int)> baseline, int builds)
    {
        var current = _layer.CaptureTileAuditsForTests();
        Check(_layer.TotalGrassBuildCount == builds && current.Length == baseline.Count
            && current.All(item => baseline.TryGetValue((item.Around, item.Across), out var expected) && expected == (item.PlacementHash, item.InstanceCount)),
            "presentation visibility rebuilt or altered canonical sources");
    }
    private async Task CheckUnion(RingStandingGrassLayer.TileAudit tile, float[] source)
    {
        var seen = new bool[source.Length / 20];
        foreach (bool intermediate in new[] { false, true })
        {
            var completion = new TaskCompletionSource<(uint[], float[])>();
            Check(_layer.RequestGpuGrassReadbackForTests(tile.Around, tile.Across, intermediate,
                (commands, rows) => completion.TrySetResult((commands, rows))), "union readback missing");
            var (commands, rows) = await completion.Task;
            Check(commands.Length >= 5 && commands[1] <= seen.Length, "invalid indirect command");
            for (int offset = 0; offset < commands[1] * 20; offset += 20)
            {
                int match = -1;
                for (int rank = 0; rank < seen.Length; rank++)
                    if (rows.AsSpan(offset, 20).SequenceEqual(source.AsSpan(rank * 20, 20))) { match = rank; break; }
                Check(match >= 0 && !seen[match], "LOD output changed or duplicated a source row"); seen[match] = true;
            }
        }
        Check(seen.All(value => value), "restored LOD union lost roots");
    }
    private int Pixels()
    {
        using Image image = _viewport.GetTexture().GetImage();
        int count = 0;
        for (int y = 0; y < image.GetHeight(); y++) for (int x = 0; x < image.GetWidth(); x++)
        { Color color = image.GetPixel(x, y); if (color.R + color.G + color.B > .1f) count++; }
        return count;
    }
    private RingStandingGrassLayer.TileAudit Find(RingStandingGrassLayer.TileAudit tile) => _layer.CaptureTileAuditsForTests().First(item => item.Around == tile.Around && item.Across == tile.Across);
    private static MultiMeshInstance3D Renderer(RingStandingGrassLayer.TileAudit tile) => GodotObject.InstanceFromId(tile.RenderInstanceId) as MultiMeshInstance3D ?? throw new InvalidOperationException("visible near renderer missing");
    private static Vector3 Center(float[] rows, Transform3D world)
    { Vector3 sum = Vector3.Zero; for (int offset = 0; offset < rows.Length; offset += 20) sum += world * new Vector3(rows[offset + 3], rows[offset + 7], rows[offset + 11]); return sum / (rows.Length / 20); }
    private RingLodGeometrySnapshot Geometry(Vector3 origin) => new(Transform3D.Identity, Vector3.Zero, origin, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, _focus);
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
