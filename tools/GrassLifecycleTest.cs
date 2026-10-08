using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

/// <summary>Production scheduler suspension, completed-worker race, and native/GPU retirement.</summary>
public partial class GrassLifecycleTest : Node
{
    public string Result { get; private set; } = "RUNNING";
    private const long Circumference = 981_748L * 32L;
    private readonly RingCanonicalPosition _focus = new(186000, 0, 141000);
    private RingStandingGrassLayer _layer = null!;
    private RingTerrainSampler _sampler = null!;
    private RingLodSurfaceEditSnapshot _edits = null!;
    private RingLodGeometrySnapshot _geometry;
    private Camera3D _camera = null!;

    public async void Start()
    {
        string result;
        try { await Verify(); result = "PASS completed-worker disable race, repeated suspension, resume parity, native/GPU retirement"; }
        catch (Exception error) { result = "FAIL " + error; }
        finally
        {
            _layer?.QueueFree(); _camera?.QueueFree();
            await Frames(3);
            _sampler?.Dispose();
        }
        Result = result;
    }

    private async Task Verify()
    {
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        _sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000d, 318000, 8000);
        _camera = new Camera3D { Far = 2000 }; AddChild(_camera); _camera.Current = true;
        _layer = new RingStandingGrassLayer
        {
            UseGpuCulling = false, GpuFrustumCulling = false, CullGrassTiles = false,
            VisibilityRadiusMeters = 72, SpatialTileMeters = 48, MaximumInstances = 2000,
            MaximumInstancesPerTile = 256, MaximumResidentTiles = 8,
            MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 2
        };
        AddChild(_layer);
        _geometry = new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circumference / Math.Tau,
            0, 0, Circumference, -1000, 1000, _focus);
        _edits = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());

        // Await the actual scheduled tasks without another Step: their successful
        // results are now waiting in Drain when the user disables grass.
        Step();
        IDictionary jobs = Field<IDictionary>("_jobs");
        Task[] tasks = jobs.Values.Cast<object>().Select(job => (Task)job.GetType().GetField("Task")!.GetValue(job)!).ToArray();
        Check(tasks.Length > 0, "race fixture did not schedule workers");
        await Task.WhenAll(tasks);
        Check(tasks.All(task => task.Status == TaskStatus.RanToCompletion), "race requires completed successful builds");
        _layer.Enabled = false; Step();
        CheckEmpty("completed worker disable");
        long builds = _layer.TotalGrassBuildCount, uploads = _layer.TotalGrassUploadCount;
        for (int i = 0; i < 8; i++) { Step(); await Frames(1); CheckEmpty("repeated disabled frame"); }
        Check(_layer.TotalGrassBuildCount == builds && _layer.TotalGrassUploadCount == uploads, "disabled grass scheduled work");

        _layer.Enabled = true;
        await Settle();
        var baseline = _layer.CaptureTileAuditsForTests().ToDictionary(tile => (tile.Around, tile.Across), tile => (tile.PlacementHash, tile.InstanceCount));
        Check(baseline.Values.Any(tile => tile.InstanceCount > 0), "resume produced no roots");
        for (int cycle = 0; cycle < 3; cycle++)
        {
            _layer.UseGpuCulling = true;
            RingGpuVegetationCompute? backend = null;
            for (int i = 0; i < 180; i++)
            {
                Step(); await Frames(1);
                backend = Field<RingGpuVegetationCompute?>("_gpuCompute");
                if (backend is { Ready: true, BatchCount: > 0, EstimatedBytes: > 0 }) break;
            }
            Check(backend is { Ready: true, BatchCount: > 0, EstimatedBytes: > 0 }, "real GPU allocation required");
            ulong[] rendererIds = _layer.CaptureTileAuditsForTests().SelectMany(tile => new[] { tile.RenderInstanceId, tile.IntermediateRenderInstanceId }).Where(id => id != 0).ToArray();
            if (cycle == 0) { _layer.Enabled = false; Step(); }
            else if (cycle == 1) Step(_layer.MaximumViewerAltitudeAboveTerrainMeters + 1);
            else Step(0, _layer.SuspendStreamingAboveSpeedMetersPerSecond + 1);
            CheckEmpty("suspension transition");
            Check(backend!.BatchCount == 0, "suspension retained GPU batches");
            for (int i = 0; i < 12; i++)
            {
                if (cycle == 0) Step();
                else if (cycle == 1) Step(_layer.MaximumViewerAltitudeAboveTerrainMeters + 1);
                else Step(0, _layer.SuspendStreamingAboveSpeedMetersPerSecond + 1);
                await Frames(1); CheckEmpty("suspended worker reap");
            }
            Check(backend.EstimatedBytes == 0 && backend.OutputBytes == 0 && backend.SharedSourceBytes == 0,
                "render-thread retirement retained GPU buffers");
            Check(rendererIds.All(id => !GodotObject.IsInstanceValid(GodotObject.InstanceFromId(id))), "suspension retained native renderer nodes");
            _layer.Enabled = true; await Settle();
            var resumed = _layer.CaptureTileAuditsForTests();
            Check(resumed.Length == baseline.Count && resumed.All(tile => baseline.TryGetValue((tile.Around, tile.Across), out var expected)
                && expected == (tile.PlacementHash, tile.InstanceCount)), "resume changed immutable source placement/budgets");
        }
    }

    private async Task Settle()
    {
        for (int i = 0; i < 300; i++)
        {
            int before = _layer.TotalGrassUploadCount; Step();
            Check(_layer.TotalGrassUploadCount - before <= _layer.MaximumTileUploadsPerFrame, "resume bypassed upload budget");
            Check(_layer.ResidentGrassInstanceCount <= _layer.MaximumGrassSourceInstances, "resume exceeded source budget");
            await Frames(1);
            if (_layer.PendingGrassTileBuildCount == 0 && _layer.PendingGrassUploadCount == 0 && _layer.ResidentGrassTileCount > 0) return;
        }
        throw new TimeoutException("resume did not settle");
    }
    private void CheckEmpty(string phase)
    {
        Check(_layer.StreamingSuspended && !_layer.Visible && _layer.ResidentGrassTileCount == 0
            && _layer.ResidentGrassInstanceCount == 0 && _layer.PendingGrassUploadCount == 0
            && _layer.RenderedGrassTileCount == 0 && _layer.InstalledGrassClumpCount == 0, phase + " retained or resurrected grass");
    }
    private T Field<T>(string name) => (T)typeof(RingStandingGrassLayer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_layer)!;
    private void Step(float altitude = 0, float speed = 0) => _layer.StepForTests(_sampler, _edits, _focus, _geometry, altitude, speed);
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
