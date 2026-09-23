using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>GPU A/B fixture for the tree dispatch optimizer and 16-float tree ABI.</summary>
public partial class TreeComputeOptimizationRegression : Node
{
    private RingGpuVegetationCompute? _legacy, _optimized;
    private Mesh? _mesh;
    private int[] _legacyIds = Array.Empty<int>(), _optimizedIds = Array.Empty<int>();
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        try { await Run(); Result = "PASS tree optimized dispatch matches legacy, including custom rows"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private async Task Run()
    {
        _mesh = MakeMesh();
        _legacy = new RingGpuVegetationCompute { OptimizeTreeDispatches = false };
        _optimized = new RingGpuVegetationCompute { OptimizeTreeDispatches = true };
        _legacyIds = Register(_legacy, "legacy"); _optimizedIds = Register(_optimized, "optimized");
        for (int i = 0; i < 180 && (!_legacy.Ready || !_optimized.Ready); i++) await Frame();
        Check(_legacy.Ready && _optimized.Ready, "compute ready");

        // Covers reversed request order, global caps 0/1/17, protected LOD0,
        // ordinary LOD0, LOD1, and both fused and non-fused batch sizes.
        await Compare(0, true, 0, false);
        await Compare(1, true, 0, true);
        await Compare(17, true, 0, false);
        await Compare(17, true, 0, false); // repeated same frame state
        await Compare(17, false, 0, true);
        await Compare(17, true, 1, true);
        await Compare(0, true, 0, true); // nonzero-to-zero command reset
        Check(_optimized.BarrierCount < _legacy.BarrierCount, "optimized barriers");
    }

    private int[] Register(RingGpuVegetationCompute gpu, string prefix)
    {
        int small = gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(prefix + "-small", new MultiMesh(), _mesh!, Rows(37, 12, 0), Metadata(37), 37, Bounds(), false));
        int large = gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(prefix + "-large", new MultiMesh(), _mesh!, Rows(130, 12, 1), Metadata(130), 130, Bounds(), false));
        int custom = gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(prefix + "-custom", new MultiMesh(), _mesh!, Rows(19, 16, 2), Metadata(19), 19, Bounds(), false, true));
        Check(small >= 0 && large >= 0 && custom >= 0, "register " + prefix);
        return new[] { small, large, custom };
    }

    private async Task Compare(uint cap, bool protectedRoots, int lod, bool reverse)
    {
        for (int i = 0; i < _legacyIds.Length; i++)
        {
            int count = i == 0 ? 37 : i == 1 ? 130 : 19;
            Check(_legacy!.UpdateMetadata(_legacyIds[i], Metadata(count, protectedRoots)), "legacy metadata");
            Check(_optimized!.UpdateMetadata(_optimizedIds[i], Metadata(count, protectedRoots)), "optimized metadata");
        }
        var legacyFrames = Frames(_legacyIds, protectedRoots, lod, reverse);
        var optimizedFrames = Frames(_optimizedIds, protectedRoots, lod, reverse);
        uint[] budgets = { cap, cap, cap, cap, cap };
        long legacyTarget = _legacy!.CompletedUpdates + 1;
        long optimizedTarget = _optimized!.CompletedUpdates + 1;
        Check(_legacy!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(Vector3.Zero, budgets, legacyFrames, false)), "legacy submit");
        Check(_optimized!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(Vector3.Zero, budgets, optimizedFrames, false)), "optimized submit");
        await WaitForCompletion(_legacy, legacyTarget, "legacy completion");
        await WaitForCompletion(_optimized, optimizedTarget, "optimized completion");
        int[] expected = ExpectedCounts(cap, protectedRoots, lod, reverse);
        for (int i = 0; i < _legacyIds.Length; i++)
        {
            var a = await Read(_legacy, _legacyIds[i]);
            var b = await Read(_optimized, _optimizedIds[i]);
            Check(a.Commands.Length == b.Commands.Length, "command size");
            for (int word = 0; word < a.Commands.Length; word++) Check(a.Commands[word] == b.Commands[word], "command parity");
            int stride = i == 2 ? RingGpuVegetationCompute.TreeCustomFloats : RingGpuVegetationCompute.TreeFloats;
            int selected = (int)a.Commands[1];
            Check(selected == expected[i], "expected selected count");
            for (int row = 0; row < selected * stride; row++) Check(a.Rows[row] == b.Rows[row], "row parity");
            if (i == 2)
                for (int row = 0; row < selected; row++)
                {
                    int source = (int)MathF.Round(a.Rows[row * stride + 3] + 20 - .02f);
                    for (int extra = 12; extra < 16; extra++) Check(a.Rows[row * stride + extra] == 200 + source * 4 + extra, "custom data copied");
                }
        }
    }

    private static RingGpuVegetationCompute.BatchFrameParameters[] Frames(int[] ids, bool protectedRoots, int lod, bool reverse)
    {
        var frames = new RingGpuVegetationCompute.BatchFrameParameters[ids.Length];
        for (int i = 0; i < ids.Length; i++) frames[i] = new RingGpuVegetationCompute.BatchFrameParameters(ids[i], Transform3D.Identity, false, lod, 0, 1000, 1, 1, 1, true, 1, protectedRoots, null, 0);
        if (reverse) Array.Reverse(frames);
        return frames;
    }

    private static int[] ExpectedCounts(uint cap, bool protectedRoots, int lod, bool reverse)
    {
        int[] counts = { 37, 130, 19 }, selected = new int[3];
        int[] order = reverse ? new[] { 2, 1, 0 } : new[] { 0, 1, 2 };
        uint remaining = cap;
        if (protectedRoots && lod == 0)
            foreach (int index in order)
                if (remaining > 0) { selected[index]++; remaining--; }
        foreach (int index in order)
        {
            int available = counts[index] - (protectedRoots ? 1 : 0);
            int take = (int)Math.Min((uint)available, remaining);
            selected[index] += take;
            remaining -= (uint)take;
        }
        return selected;
    }

    private static async Task<(uint[] Commands, float[] Rows)> Read(RingGpuVegetationCompute gpu, int id)
    {
        var completion = new TaskCompletionSource<(uint[], float[])>();
        gpu.RequestReadbackForTests(id, (commands, rows) => completion.TrySetResult((commands, rows)));
        return await completion.Task;
    }

    private async Task WaitForCompletion(RingGpuVegetationCompute gpu, long target, string text)
    {
        for (int frame = 0; frame < 180 && gpu.CompletedUpdates < target; frame++) await Frame();
        Check(gpu.CompletedUpdates >= target, text);
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    public override void _ExitTree() { _legacy?.Dispose(); _optimized?.Dispose(); }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static Aabb Bounds() => new(new Vector3(-100, -2, -2), new Vector3(200, 4, 4));
    private static Vector4[] Metadata(int count, bool protectedRoots = true) { var data = new Vector4[count]; for (int i = 0; i < count; i++) data[i] = new Vector4(0, protectedRoots && i == count - 1 ? 1 : 0, 0, 0); return data; }
    private static float[] Rows(int count, int stride, int batch)
    {
        var data = new float[count * stride];
        for (int i = 0; i < count; i++) { int at = i * stride; data[at] = data[at + 5] = data[at + 10] = 1; data[at + 3] = -20 + i + batch * .01f; for (int extra = 12; extra < stride; extra++) data[at + extra] = batch * 100 + i * 4 + extra; }
        return data;
    }
    private static ArrayMesh MakeMesh()
    {
        var mesh = new ArrayMesh(); var vertices = new[] { new Vector3(-.1f, 0, 0), new Vector3(.1f, 0, 0), new Vector3(0, .2f, 0) }; var indices = new[] { 0, 1, 2 };
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max); arrays[(int)Mesh.ArrayType.Vertex] = vertices; arrays[(int)Mesh.ArrayType.Index] = indices; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
