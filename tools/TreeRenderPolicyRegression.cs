using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Exercises compact tree LOD selection with synthetic fixtures, including
/// pruning and mid-tile merging. The fixture explicitly opts into the profile.
/// </summary>
public partial class TreeRenderPolicyRegression : Node
{
    private RingBiomeDecorationLayer? _layer;
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        try { await Run(); Result = "PASS compact LOD profile, pruning, mid merge, protected root"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        _layer?.QueueFree(); _layer = null;
    }

    private async Task Run()
    {
        _layer = new RingBiomeDecorationLayer
        {
            Enabled = false, UseGpuCulling = true, EnableImportedForestCollisions = false,
            UseCompactTreeLodProfile = true, MergeMidGpuTreeBatches = false, MergeFarGpuTreeBatches = false,
            PruneEmptyGpuTreeBatches = false, TreeNearDetailFadeStartMeters = 25, TreeNearDetailFadeEndMeters = 40,
            TreeBillboardFadeStartMeters = 300, TreeBillboardFadeEndMeters = 350, VisibilityRadiusMeters = 1000,
            MinimumLod5DistanceMeters = 700, MaximumLod5DistanceMeters = 1000
        };
        _layer.ForceCompactTreeLodProfileForTests = true;
        AddChild(_layer);
        Mesh[] meshes = { new BoxMesh(), new BoxMesh(), new BoxMesh(), new BoxMesh() };
        Transform3D[][] roots =
        {
            new[] { T(10), T(30), T(100), T(320), T(600) },
            new[] { T(110), T(330) }
        };
        _layer.ConfigureGpuTreeFixtureGroups(meshes, roots,
            new[] { new[] { 0f, 0f, 0f, 0f, 0f }, new[] { 0f, 0f } },
            new[] { new[] { false, true, false, false, false }, new[] { false, false } },
            new long[] { 0, 1 }, new[] { 0, 0 },
            new[] { new[] { new Vector2I(10, 0), new Vector2I(30, 0), new Vector2I(100, 0), new Vector2I(320, 0), new Vector2I(600, 0) }, new[] { new Vector2I(110, 0), new Vector2I(330, 0) } });
        await Settle(new RingCanonicalPosition(0, 0, 0));
        Dictionary<int, float[]> baseline = await Snapshot();
        Check(baseline[0].Length > 0 && baseline[2].Length > 0 && baseline[3].Length > 0, "compact profile did not populate 0/2/3");
        Check(baseline[1].Length == 0, "compact profile emitted skipped LOD1");
        _layer.PruneEmptyGpuTreeBatches = true; _layer.MergeMidGpuTreeBatches = true;
        await Settle(new RingCanonicalPosition(0, 0, 0));
        Dictionary<int, float[]> pruned = await Snapshot();
        foreach (int lod in new[] { 0, 2, 3 }) Check(SameMarkers(baseline[lod], pruned[lod], 16), "prune/merge changed accepted roots in LOD" + lod);
        await Settle(new RingCanonicalPosition(8, 0, 0));
        Dictionary<int, float[]> moved = await Snapshot();
        Check(moved[0].Length > 0, "protected near root was lost after reuse-region movement");
        await RunCapPruningCase(meshes);
    }

    private static Transform3D T(float x) => new(Basis.Identity, new Vector3(x, 0, 0));

    private async Task RunCapPruningCase(Mesh[] meshes)
    {
        // LOD2's compact cap is 32 + 64 + 128 = 224. The protected root at
        // 20m is outside its band at camera zero, but can enter it during the
        // 15m selection-reuse region and must survive a saturated ordinary set.
        const int groups = 4, ordinaryPerGroup = 75;
        var transforms = new Transform3D[groups][]; var ranks = new float[groups][];
        var protectedRoots = new bool[groups][]; var canonical = new Vector2I[groups][];
        var around = new long[groups]; var across = new int[groups];
        for (int group = 0; group < groups; group++)
        {
            int count = ordinaryPerGroup + (group == 0 ? 1 : 0);
            transforms[group] = new Transform3D[count]; ranks[group] = new float[count];
            protectedRoots[group] = new bool[count]; canonical[group] = new Vector2I[count]; around[group] = group;
            for (int i = 0; i < count; i++)
            {
                int ordinaryOrdinal = group * ordinaryPerGroup + (group == 0 ? i - 1 : i);
                float marker = group == 0 && i == 0 ? 20f : 100f + ordinaryOrdinal * .5f;
                transforms[group][i] = T(marker); canonical[group][i] = new Vector2I((int)MathF.Round(marker), 0);
                ranks[group][i] = 0; protectedRoots[group][i] = group == 0 && i == 0;
            }
        }
        // This fifth group can only produce rank-rejected LOD2 roots. Pruning
        // should remove its output while retaining the boundary protected group.
        Array.Resize(ref transforms, groups + 1); Array.Resize(ref ranks, groups + 1); Array.Resize(ref protectedRoots, groups + 1);
        Array.Resize(ref canonical, groups + 1); Array.Resize(ref around, groups + 1); Array.Resize(ref across, groups + 1);
        transforms[groups] = Enumerable.Range(0, 12).Select(i => T(240 + i)).ToArray();
        ranks[groups] = Enumerable.Repeat(2f, 12).ToArray(); protectedRoots[groups] = new bool[12];
        canonical[groups] = Enumerable.Range(0, 12).Select(i => new Vector2I(240 + i, 0)).ToArray(); around[groups] = groups;
        _layer!.MaximumLod1Trees = 32; _layer.MaximumLod2Trees = 64; _layer.MaximumLod3Trees = 128;
        _layer.MergeMidGpuTreeBatches = false; _layer.MergeFarGpuTreeBatches = false; _layer.PruneEmptyGpuTreeBatches = false;
        _layer.ConfigureGpuTreeFixtureGroups(meshes, transforms, ranks, protectedRoots, around, across, canonical);
        var zero = new RingCanonicalPosition(0, 0, 0); var inside = new RingCanonicalPosition(-10, 0, 0);
        await Settle(zero); float[] unprunedZero = (await Snapshot())[2]; int unprunedBatches = _layer.GpuTreeBatchCount;
        await Settle(inside); float[] unprunedInside = (await Snapshot())[2];
        int unprunedZeroRows = unprunedZero.Length / 16, unprunedZeroMarkers = Markers(unprunedZero, 16).Count;
        Check(unprunedZeroRows == 224 && unprunedZeroMarkers == 224,
            $"unpruned compact LOD2 did not saturate its cap (rows={unprunedZeroRows}, unique_markers={unprunedZeroMarkers})");
        Check(!Markers(unprunedZero, 16).Contains(20f) && Markers(unprunedInside, 16).Contains(20f), "boundary protected root did not enter after reuse movement");
        _layer.PruneEmptyGpuTreeBatches = true;
        await Settle(zero); float[] prunedZero = (await Snapshot())[2]; int prunedBatches = _layer.GpuTreeBatchCount;
        await Settle(inside); float[] prunedInside = (await Snapshot())[2];
        Check(SameOrderedMarkers(unprunedZero, prunedZero, 16), "cap-pruning changed saturated camera-zero roots");
        Check(SameOrderedMarkers(unprunedInside, prunedInside, 16), "cap-pruning lost boundary protected root after reuse movement");
        Check(prunedBatches < unprunedBatches, "rank-rejected/empty candidate pruning did not remove a batch");
    }
    private async Task Settle(RingCanonicalPosition focus)
    {
        for (int i = 0; i < 300; i++)
        {
            _layer!.StepGpuTreeFixture(Vector3.Zero, Transform3D.Identity, focus, 31_415_936);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_layer.GpuTreeCullingSettled) return;
        }
        throw new InvalidOperationException("fixture did not settle");
    }
    private async Task<Dictionary<int, float[]>> Snapshot()
    {
        var result = new Dictionary<int, float[]>();
        for (int lod = 0; lod < 5; lod++)
        {
            var done = new TaskCompletionSource<float[]>();
            _layer!.GpuTreeFixtureReadbackAll(lod, (_, rows) => done.TrySetResult(rows)); result[lod] = await done.Task;
        }
        return result;
    }
    private static bool SameMarkers(float[] a, float[] b, int stride)
    {
        var left = Markers(a, stride); var right = Markers(b, stride);
        return left.Count == right.Count && !left.Except(right).Any();
    }
    private static bool SameOrderedMarkers(float[] a, float[] b, int stride)
    {
        float[] left = Markers(a, stride).OrderBy(value => value).ToArray();
        float[] right = Markers(b, stride).OrderBy(value => value).ToArray();
        return left.SequenceEqual(right);
    }
    private static HashSet<float> Markers(float[] rows, int stride)
    {
        var result = new HashSet<float>();
        for (int i = 0; i + 3 < rows.Length; i += stride) result.Add(rows[i + 3]);
        return result;
    }
    private static void Check(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); }
}
