using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Regression for empty grass residents consuming the GPU registration budget.</summary>
public partial class GpuGrassRegistrationTest : Node
{
    private RingStandingGrassLayer? _layer;
    private Camera3D? _camera;
    public string Result { get; private set; } = "RUNNING";
    public async void Start()
    {
        string result;
        try { await Run(); result = "PASS empty registration budget, late LOD, shared source"; }
        catch (Exception error) { result = "FAIL " + error.Message; }
        _layer?.QueueFree(); _camera?.QueueFree(); await Frames(3);
        Result = result;
    }
    private async Task Run()
    {
        _camera = new Camera3D { Current = true, Far = 1000 }; AddChild(_camera);
        _layer = new RingStandingGrassLayer { UseGpuCulling = true, GpuFrustumCulling = false, MaximumTileUploadsPerFrame = 2, MaximumTileUploadMilliseconds = 20, LodNearDensity = 1, LodFarDensity = .25f, EnableDistanceLod = true };
        AddChild(_layer); await Frames(3);
        Check(_layer.IntermediateGpuCapacityForTests(6000) == 2600, "intermediate output capacity math");
        float[] densityFixture = GrassCandidates(6000);
        Check(_layer.IntermediateGpuCapacitySweepForTests(densityFixture), "intermediate deterministic hash sweep overflow");
        foreach ((float near, float far) in new[] { (.05f, 1f), (1f, .05f), (.70f, .30f), (.30f, .70f) })
        {
            _layer.LodNearDensity = near; _layer.LodFarDensity = far;
            Check(_layer.IntermediateGpuCapacitySweepForTests(densityFixture), $"intermediate capacity overflow near={near} far={far}");
        }
        _layer.LodNearDensity = 1; _layer.LodFarDensity = .25f;
        _layer.EnableDistanceLod = false;
        Check(_layer.IntermediateGpuCapacityForTests(6000) == 6000, "disabled distance LOD resized output");
        _layer.EnableDistanceLod = true;
        _layer.ConfigureGpuRegistrationFixture(2, 3);
        for (int i = 0; i < 90; i++) { await StepBounded(); }
        Check(_layer.GpuRegistrationFixtureBatchId(0, false) < 0 && _layer.GpuRegistrationFixtureBatchId(1, false) < 0, "empty residents allocated GPU batches");
        Check(_layer.GpuRegistrationFixtureBatchId(2, false) >= 0 && _layer.GpuRegistrationFixtureBatchId(3, false) >= 0 && _layer.GpuRegistrationFixtureBatchId(4, false) >= 0, "populated residents starved behind empty registration attempts");
        await WaitReady(2, false); await WaitReady(3, false); await WaitReady(4, false);
        float[] payload = await Read(2, false);
        Check(payload.Length >= 20 && Mathf.IsEqualApprox(payload[3], 102), "populated payload changed");
        _layer.UseGpuCulling = false; _layer.StepGpuRegistrationFixture(); await Frames(3);
        Check(_layer.GpuCullingBatchCount == 0, "GPU disable did not retire registration fixture");
        _layer.UseGpuCulling = true;
        for (int i = 0; i < 90; i++) await StepBounded();
        await WaitReady(2, false); await WaitReady(3, false); await WaitReady(4, false);
        Check((await Read(2, false)).Length >= 20, "payload lost after GPU re-enable");
        _layer.EnableGpuRegistrationFixtureIntermediate();
        for (int i = 0; i < 90; i++) await StepBounded();
        await WaitReady(2, true); await WaitReady(3, true); await WaitReady(4, true);
        Check(_layer.GpuRegistrationFixtureBatchId(0, false) < 0 && _layer.GpuRegistrationFixtureBatchId(1, false) < 0 && _layer.GpuRegistrationFixtureBatchId(0, true) < 0 && _layer.GpuRegistrationFixtureBatchId(1, true) < 0, "empty residents allocated a late LOD GPU batch");
        Check((await Read(2, true)).Length >= 20, "payload lost after late intermediate registration");
        // Three populated tiles own three immutable 20-float source uploads;
        // their late intermediate draws must not upload those roots again.
        Check(_layer.GpuCullingSharedSourceCount == 3 && _layer.GpuCullingSharedSourceBytes == 3 * 20 * 4,
            "late LOD duplicated grass source uploads");
        Check(_layer.GpuCullingBytes >= _layer.GpuCullingSharedSourceBytes + _layer.GpuCullingOutputBytes,
            "grass GPU memory diagnostics are inconsistent");
    }
    private async Task WaitReady(long tile, bool intermediate)
    { for (int i = 0; i < 90; i++) { await StepBounded(); if (_layer!.IsGpuGrassBatchReadyForTests(tile, 0, intermediate)) return; } throw new InvalidOperationException("GPU fixture batch not ready"); }
    private async Task StepBounded()
    {
        int before = _layer!.GpuCullingBatchCount;
        _layer.StepGpuRegistrationFixture(); await Frames(1);
        Check(_layer.GpuCullingBatchCount - before <= 2, "more than two valid tiles registered in one frame");
    }
    private async Task<float[]> Read(long tile, bool intermediate)
    {
        var completion = new TaskCompletionSource<float[]>();
        Check(_layer!.RequestGpuGrassReadbackForTests(tile, 0, intermediate, (_, rows) => completion.TrySetResult(rows)), "readback unavailable");
        for (int i = 0; i < 90 && !completion.Task.IsCompleted; i++) await Frames(1);
        Check(completion.Task.IsCompleted, "readback timeout");
        return await completion.Task;
    }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static float[] GrassCandidates(int count)
    {
        var rows = new float[count * 20];
        for (int i = 0; i < count; i++) { int offset = i * 20; rows[offset] = rows[offset + 5] = rows[offset + 10] = 1; rows[offset + 16] = i * .013f; rows[offset + 17] = i * .037f; }
        return rows;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
