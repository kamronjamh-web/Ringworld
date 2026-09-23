using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Parity fixture for runtime-selectable grouped grass compute phases.</summary>
public partial class GpuGrassGroupedPhasesTest : Node
{
    private sealed record Pair(int Baseline, int Grouped, bool Grass);
    private RingGpuVegetationCompute? _baseline, _grouped;
    private readonly List<Pair> _pairs = new();
    private Mesh? _mesh;
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        try { await Run(); Result = "PASS grouped grass phases match baseline across LOD, frustum, truncation, mixed fallback, and retirement"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        _baseline?.Dispose(); _grouped?.Dispose(); await Frames(3);
    }

    private async Task Run()
    {
        _baseline = new RingGpuVegetationCompute();
        _grouped = new RingGpuVegetationCompute();
        Check(!_baseline.GroupGrassPhases && !_grouped.GroupGrassPhases, "grouped grass defaults disabled");
        _grouped.GroupGrassPhases = true;
        _mesh = MakeMesh();
        foreach ((int count, int capacity, int tile) in new[] { (65, 17, 0), (97, 43, 1), (31, 31, 2) })
            AddPair($"grass-{tile}", GrassRows(count, tile), null, capacity, true);
        await WaitReady();

        await SubmitAndCompare(Vector3.Zero, 2, 36, 10, .75f, .35f, true, null, "near-handoff");
        await SubmitAndCompare(new Vector3(8, 0, 0), 2, 36, 10, .35f, .8f, true,
            new[] { new Vector4(1, 0, 0, 3), new Vector4(-1, 0, 0, 40) }, "frustum-density");
        await SubmitAndCompare(new Vector3(300, 0, 0), 2, 36, 10, 1, 1, true, null, "empty-accepted");
        await SubmitAndCompare(Vector3.Zero, 0, 1000, 1, 1, 1, false, null, "repeat-full-density");
        await SubmitAndCompare(Vector3.Zero, 0, 1000, 1, 1, 1, false, null, "repeat-full-density");
        _grouped.GroupGrassPhases = false;
        await SubmitAndCompare(new Vector3(1, 0, 0), 2, 36, 10, .75f, .35f, true, null, "runtime-grouping-disabled");
        _grouped.GroupGrassPhases = true;
        await SubmitAndCompare(new Vector3(-1, 0, 0), 2, 36, 10, .75f, .35f, true, null, "runtime-grouping-reenabled");
        await VerifyVisibilityRevisionBudget();

        AddPair("tree-fallback", TreeRows(65), TreeMetadata(65), 19, false);
        await WaitReady();
        await SubmitAndCompare(Vector3.Zero, 2, 36, 10, .75f, .35f, true, null, "mixed-fallback");

        Pair retired = _pairs[1];
        Check(_baseline.RemoveBatch(retired.Baseline) && _grouped.RemoveBatch(retired.Grouped), "retire paired grass batch");
        _pairs.RemoveAt(1); await Frames(4);
        await SubmitAndCompare(new Vector3(-4, 0, 0), 2, 36, 10, .65f, .5f, true, null, "post-retirement");
    }

    private void AddPair(string identity, float[] rows, Vector4[]? metadata, int capacity, bool grass)
    {
        Aabb bounds = new(new Vector3(-32, -4, -4), new Vector3(64, 8, 8));
        int baseline = _baseline!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(identity,
            new MultiMesh(), _mesh!, rows, metadata, capacity, bounds, grass));
        int grouped = _grouped!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(identity,
            new MultiMesh(), _mesh!, rows, metadata, capacity, bounds, grass));
        Check(baseline >= 0 && grouped >= 0, "paired registration " + identity);
        _pairs.Add(new Pair(baseline, grouped, grass));
    }

    private async Task WaitReady()
    {
        for (int frame = 0; frame < 180; frame++)
        {
            if (_baseline!.Ready && _grouped!.Ready) { await Frames(3); return; }
            await Frames(1);
        }
        throw new TimeoutException("GPU pipeline readiness");
    }

    private async Task SubmitAndCompare(Vector3 camera, float near, float far, float transition,
        float nearDensity, float farDensity, bool lod, Vector4[]? planes, string label,
        int maximumBatchesPerDispatch = 0, long visibilityRevision = 0, bool skipUnchanged = false)
    {
        var baselineFrames = new List<RingGpuVegetationCompute.BatchFrameParameters>(_pairs.Count);
        var groupedFrames = new List<RingGpuVegetationCompute.BatchFrameParameters>(_pairs.Count);
        for (int i = 0; i < _pairs.Count; i++)
        {
            Pair pair = _pairs[i];
            // Near and intermediate grass outputs intentionally share the same
            // candidate shape while exercising their complementary hand-off.
            int lodPass = pair.Grass && (i & 1) != 0 ? 1 : 0;
            baselineFrames.Add(Parameters(pair.Baseline, pair.Grass, lodPass, near, far, transition, nearDensity, farDensity, lod, planes, visibilityRevision));
            groupedFrames.Add(Parameters(pair.Grouped, pair.Grass, lodPass, near, far, transition, nearDensity, farDensity, lod, planes, visibilityRevision));
        }
        uint[] budgets = { 29, 29, 29, 29, 29 };
        Check(_baseline!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera, budgets, baselineFrames, skipUnchanged, maximumBatchesPerDispatch)), "baseline submit " + label);
        Check(_grouped!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera, budgets, groupedFrames, skipUnchanged, maximumBatchesPerDispatch)), "grouped submit " + label);
        await Frames(4);
        for (int i = 0; i < _pairs.Count; i++) await Compare(_pairs[i], label + " batch=" + i);
    }

    private static RingGpuVegetationCompute.BatchFrameParameters Parameters(int id, bool grass, int lodPass, float near,
        float far, float transition, float nearDensity, float farDensity, bool lod, Vector4[]? planes, long visibilityRevision) =>
        new(id, Transform3D.Identity, grass, lodPass, near, far, transition, nearDensity, farDensity, lod,
            1, false, planes, 1, null, visibilityRevision);

    private async Task VerifyVisibilityRevisionBudget()
    {
        const long firstRevision = 101, secondRevision = 102;
        foreach (Pair pair in _pairs)
        {
            Check(_baseline!.IsBatchReady(pair.Baseline) && _grouped!.IsBatchReady(pair.Grouped), "legacy readiness retains prior output");
            Check(!_baseline!.IsBatchReady(pair.Baseline, firstRevision) && !_grouped!.IsBatchReady(pair.Grouped, firstRevision), "old output is not fresh for new visibility token");
        }
        long baselineUpdates = _baseline!.CompletedUpdates, groupedUpdates = _grouped!.CompletedUpdates;
        await SubmitAndCompare(new Vector3(3, 0, 0), 2, 36, 10, .75f, .35f, true, null,
            "visibility-revision-first", maximumBatchesPerDispatch: 1, visibilityRevision: firstRevision, skipUnchanged: true);
        Check(_baseline.CompletedUpdates == baselineUpdates + 1 && _grouped.CompletedUpdates == groupedUpdates + 1, "first visibility revision dispatch");
        Check(FreshCount(_baseline, false, firstRevision) == 1 && FreshCount(_grouped, true, firstRevision) == 1, "one budgeted output completed first visibility revision");
        foreach (Pair pair in _pairs) Check(_baseline.IsBatchReady(pair.Baseline) && _grouped.IsBatchReady(pair.Grouped), "old output remains legacy-ready while deferred");
        Check(FreshCount(_baseline, false, secondRevision) == 0 && FreshCount(_grouped, true, secondRevision) == 0, "prior visibility output cannot count fresh for next token");
        baselineUpdates = _baseline.CompletedUpdates; groupedUpdates = _grouped.CompletedUpdates;
        await SubmitAndCompare(new Vector3(3, 0, 0), 2, 36, 10, .75f, .35f, true, null,
            "visibility-revision-forces-unchanged", maximumBatchesPerDispatch: 1, visibilityRevision: secondRevision, skipUnchanged: true);
        Check(_baseline.CompletedUpdates == baselineUpdates + 1 && _grouped.CompletedUpdates == groupedUpdates + 1, "visibility revision invalidated unchanged frame");
        Check(FreshCount(_baseline, false, secondRevision) == 1 && FreshCount(_grouped, true, secondRevision) == 1, "one budgeted output completed second visibility revision");
    }

    private int FreshCount(RingGpuVegetationCompute gpu, bool grouped, long visibilityRevision)
    {
        int count = 0;
        foreach (Pair pair in _pairs) if (pair.Grass && gpu.IsBatchReady(grouped ? pair.Grouped : pair.Baseline, visibilityRevision)) count++;
        return count;
    }

    private async Task Compare(Pair pair, string label)
    {
        (uint[] Commands, float[] Rows) baseline = await Read(_baseline!, pair.Baseline);
        (uint[] Commands, float[] Rows) grouped = await Read(_grouped!, pair.Grouped);
        Check(baseline.Commands.Length == grouped.Commands.Length && baseline.Commands.Length >= 5, label + " command allocation");
        for (int i = 0; i < baseline.Commands.Length; i++) Check(baseline.Commands[i] == grouped.Commands[i], label + " command word=" + i);
        uint count = baseline.Commands[1];
        int stride = pair.Grass ? RingGpuVegetationCompute.GrassFloats : RingGpuVegetationCompute.TreeFloats;
        Check(count <= baseline.Rows.Length / stride && count <= grouped.Rows.Length / stride, label + " valid prefix bounds");
        for (int i = 0; i < count * stride; i++) Check(baseline.Rows[i].Equals(grouped.Rows[i]), label + " output row word=" + i);
    }

    private async Task<(uint[] Commands, float[] Rows)> Read(RingGpuVegetationCompute gpu, int id)
    {
        var completion = new TaskCompletionSource<(uint[], float[])>();
        gpu.RequestReadbackForTests(id, (commands, rows) => completion.TrySetResult((commands, rows)));
        for (int frame = 0; frame < 180 && !completion.Task.IsCompleted; frame++) await Frames(1);
        Check(completion.Task.IsCompleted, "readback timeout");
        return await completion.Task;
    }

    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static float[] GrassRows(int count, int tile)
    {
        var rows = new float[count * RingGpuVegetationCompute.GrassFloats];
        for (int i = 0; i < count; i++)
        {
            int at = i * RingGpuVegetationCompute.GrassFloats;
            rows[at] = rows[at + 5] = rows[at + 10] = 1;
            rows[at + 3] = -16 + i * .45f + tile * .03f;
            rows[at + 11] = (i % 7) - 3;
            rows[at + 12] = .5f; rows[at + 16] = (i + tile) * .071f; rows[at + 17] = (i + 3) * .113f;
        }
        return rows;
    }
    private static float[] TreeRows(int count)
    {
        var rows = new float[count * RingGpuVegetationCompute.TreeFloats];
        for (int i = 0; i < count; i++) { int at = i * RingGpuVegetationCompute.TreeFloats; rows[at] = rows[at + 5] = rows[at + 10] = 1; rows[at + 3] = -16 + i * .45f; }
        return rows;
    }
    private static Vector4[] TreeMetadata(int count) { var values = new Vector4[count]; for (int i = 0; i < count; i++) values[i] = new Vector4((i + .5f) / count, 0, 0, 0); return values; }
    private static ArrayMesh MakeMesh()
    {
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 }; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
