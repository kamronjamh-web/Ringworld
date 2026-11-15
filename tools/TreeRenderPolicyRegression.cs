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
            UseCompactTreeLodProfile = true, UseTreeRenderLods = false, MergeMidGpuTreeBatches = false, MergeFarGpuTreeBatches = false,
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
        await RunRenderLodsCase(meshes);
        RunEffectiveEdgeResolver();
        await RunEffectiveEdgeCase(meshes);
    }

    // TR-9a, the resolver on its own: 64 bins of 10 roots over [0, 640) m, a cap of 400 (95 % = 380) ends the band at the upper edge of bin 37 (380 m); a band that
    // fits keeps the configured end; a cap that not even the first bin fits ends at the minimum; a stored end within one bin (10 m) is kept, one farther is replaced.
    private static void RunEffectiveEdgeResolver()
    {
        int[] histogram = Enumerable.Repeat(10, 64).ToArray();
        float End(float available, float stored, float minimum = 100) => RingBiomeDecorationLayer.ResolveGpuTreeEffectiveEnd(histogram, 0, 640, 0, available, stored, minimum);
        Check(Mathf.IsEqualApprox(End(400, float.NaN), 380), "effective end: 95 % of the cap is not at 380 m: " + End(400, float.NaN));
        Check(Mathf.IsEqualApprox(End(10000, float.NaN), 640), "effective end: a band that fits must keep the configured end");
        Check(Mathf.IsEqualApprox(End(0, float.NaN), 100), "effective end: an overfull first bin must end at the minimum");
        Check(Mathf.IsEqualApprox(End(400, 375), 375), "effective end: a 5 m change (under one bin) moved the stored end");
        Check(Mathf.IsEqualApprox(End(400, 360), 380), "effective end: a 20 m change (over one bin) did not move the stored end");
        // From the start of the second half the slot owns only the roots past 320 m: the same cap then ends 380 m farther out, capped at the configured end.
        Check(Mathf.IsEqualApprox(RingBiomeDecorationLayer.ResolveGpuTreeEffectiveEnd(histogram, 0, 640, 320, 400, float.NaN, 100), 640), "effective end: a band starting at 320 m (320 roots) fits 380");
    }

    // TR-9a on the real compute shader. LOD2 (slot 2, render LODs off) has a cap of 224 and a band of 25 to 350 m that holds 580 roots (50 to 339.5 m, 2 per metre).
    // With the effective edge the slot ends where its cap holds (about 153 m), the card slot starts 50 m earlier (the configured fade width), and every root is drawn
    // by exactly one slot outside the 50 m cross-dither. With the effective edge disabled the roots from 162 to 300 m are drawn by no slot: the negative control.
    private async Task RunEffectiveEdgeCase(Mesh[] meshes)
    {
        const int groups = 4, perGroup = 145;
        var transforms = new Transform3D[groups][]; var ranks = new float[groups][]; var protectedRoots = new bool[groups][];
        var canonical = new Vector2I[groups][]; var around = new long[groups]; var across = new int[groups];
        var all = new List<float>();
        for (int group = 0; group < groups; group++)
        {
            transforms[group] = new Transform3D[perGroup]; ranks[group] = new float[perGroup]; protectedRoots[group] = new bool[perGroup];
            canonical[group] = new Vector2I[perGroup]; around[group] = group;
            for (int i = 0; i < perGroup; i++)
            {
                float x = 50 + (group * perGroup + i) * .5f; all.Add(x);
                transforms[group][i] = T(x); canonical[group][i] = new Vector2I((int)MathF.Round(x), 0);
            }
        }
        var layer = _layer!;
        layer.MaximumLod1Trees = 32; layer.MaximumLod2Trees = 64; layer.MaximumLod3Trees = 128; layer.MaximumLod4Trees = 4096;
        layer.MergeMidGpuTreeBatches = false; layer.MergeFarGpuTreeBatches = false; layer.UseTreeRenderLods = false;
        layer.PruneEmptyGpuTreeBatches = true; layer.DisableGpuTreeEffectiveEdges = true;
        layer.ConfigureGpuTreeFixtureGroups(meshes, transforms, ranks, protectedRoots, around, across, canonical);
        var zero = new RingCanonicalPosition(0, 0, 0);

        // Negative control: the configured edges leave a hole in the forest.
        await Settle(zero);
        Dictionary<int, float[]> off = await Snapshot();
        HashSet<float> offDrawn = Markers(off[2], 16); offDrawn.UnionWith(Markers(off[3], 16)); offDrawn.UnionWith(Markers(off[0], 16));
        int missingOff = all.Count(x => !offDrawn.Contains(x));
        Check(missingOff > 0, "effective edges disabled: expected roots drawn by no slot (the negative control), found none");
        Check(layer.GpuTreeEffectiveEndsForTests() is null, "effective edges disabled: ends still reported");

        layer.DisableGpuTreeEffectiveEdges = false;
        await Settle(zero);
        float[] ends = layer.GpuTreeEffectiveEndsForTests() ?? throw new InvalidOperationException("effective edges: no ends in force with pruning and a canonical focus");
        Check(ends[2] < 350 - 20 && ends[2] > 100, $"effective edges: LOD2 end {ends[2]} did not move inward to the cap (expected about 153 m)");
        Check(ends[0] == 40 && ends[3] == 700, $"effective edges: slot 0 and card ends moved without a cap binding ({ends[0]}, {ends[3]})");
        Dictionary<int, float[]> on = await Snapshot();
        HashSet<float> s2 = Markers(on[2], 16), s3 = Markers(on[3], 16);
        float fadeStart = ends[2] - 50;
        int missing = 0, twice = 0, wrong = 0;
        foreach (float x in all)
        {
            bool in2 = s2.Contains(x), in3 = s3.Contains(x);
            if (!in2 && !in3) missing++;
            else if (in2 && in3 && (x < fadeStart - .01f || x >= ends[2] + .01f)) twice++;
            if (in2 && x >= ends[2] + .01f) wrong++;
            if (in3 && x < fadeStart - .01f) wrong++;
        }
        Check(missing == 0, $"effective edges: {missing} of {all.Count} roots drawn by no slot (LOD2 end {ends[2]:F1} m)");
        Check(twice == 0 && wrong == 0, $"effective edges: {twice} roots drawn twice outside the cross-dither, {wrong} outside their slot's band");
        Check(s2.Count <= 224, $"effective edges: LOD2 drew {s2.Count} roots over its cap of 224");

        // Standing still: the same pose again keeps the ends (the resolver's one-bin hysteresis is checked in RunEffectiveEdgeResolver).
        await Settle(zero);
        Check(layer.GpuTreeEffectiveEndsForTests()!.SequenceEqual(ends), "effective edges: ends changed while the camera stood still");
        GD.Print($"TR9a effective edges: LOD2 end {ends[2]:F1} m (configured 350), roots {all.Count}, missing without the effective edge {missingOff}");
    }

    // TR-5: with the render LODs on, slot 1 draws the authored LOD2 from the near fade to the reduced-detail hand-off (130-150 m) and slot 2 the decimated
    // LOD2b from 130 m to the billboard fade. Reuses the first fixture's roots at 10, 30, 100, 110, 320, 330 and 600 m.
    private async Task RunRenderLodsCase(Mesh[] meshes)
    {
        Transform3D[][] roots = { new[] { T(10), T(30), T(100), T(320), T(600) }, new[] { T(110), T(330), T(140) } };
        _layer!.MaximumLod1Trees = 256; _layer.MaximumLod2Trees = 640; _layer.MaximumLod3Trees = 1024;
        _layer.MergeMidGpuTreeBatches = false; _layer.PruneEmptyGpuTreeBatches = false; _layer.UseTreeRenderLods = true;
        _layer.TreeMidDetailFadeStartMeters = 130; _layer.TreeMidDetailFadeEndMeters = 150;
        _layer.ConfigureGpuTreeFixtureGroups(meshes, roots,
            new[] { new[] { 0f, 0f, 0f, 0f, 0f }, new[] { 0f, 0f, 0f } },
            new[] { new[] { false, false, false, false, false }, new[] { false, false, false } },
            new long[] { 0, 1 }, new[] { 0, 0 },
            new[] { new[] { new Vector2I(10, 0), new Vector2I(30, 0), new Vector2I(100, 0), new Vector2I(320, 0), new Vector2I(600, 0) }, new[] { new Vector2I(110, 0), new Vector2I(330, 0), new Vector2I(140, 0) } });
        await Settle(new RingCanonicalPosition(0, 0, 0));
        Dictionary<int, float[]> slots = await Snapshot();
        HashSet<float> s0 = Markers(slots[0], 16), s1 = Markers(slots[1], 16), s2 = Markers(slots[2], 16), s3 = Markers(slots[3], 16);
        Check(s0.SetEquals(new[] { 10f, 30f }), "render LODs: slot 0 roots " + string.Join(",", s0));
        Check(s1.SetEquals(new[] { 30f, 100f, 110f, 140f }), "render LODs: slot 1 (authored LOD2, 25 to 150 m) roots " + string.Join(",", s1));
        Check(s2.SetEquals(new[] { 140f, 320f, 330f }), "render LODs: slot 2 (LOD2b, 130 to 350 m) roots " + string.Join(",", s2));
        Check(s3.Contains(320f) && s3.Contains(330f) && s3.Contains(600f) && !s3.Contains(140f), "render LODs: card slot roots " + string.Join(",", s3));
        // The hand-off ranges the compute frame receives must be one continuous chain, and a 10 m shift of one range must be seen as broken.
        var chain = new Godot.Collections.Dictionary[4];
        for (int lod = 0; lod < 4; lod++) chain[lod] = _layer.TreeLodIntervalForTests(lod, true);
        for (int lod = 0; lod < 3; lod++)
        {
            Check(Mathf.IsEqualApprox((float)chain[lod]["out_start"], (float)chain[lod + 1]["in_start"]) && Mathf.IsEqualApprox((float)chain[lod]["out_end"], (float)chain[lod + 1]["in_end"]),
                $"render LODs: hand-off {lod} to {lod + 1} ranges disagree");
        }
        Check(Mathf.IsEqualApprox((float)chain[1]["out_start"], 130) && Mathf.IsEqualApprox((float)chain[1]["out_end"], 150), "render LODs: mid hand-off is not 130-150 m");
        Check(!Mathf.IsEqualApprox((float)chain[1]["out_start"] + 10, (float)chain[2]["in_start"]), "render LODs: negative control did not detect a 10 m shift");
        Check((bool)_layer.TreeLodIntervalForTests(1, true)["enabled"] && !(bool)_layer.TreeLodIntervalForTests(1, false)["enabled"], "render LODs: slot 1 enable flag");
        _layer.UseTreeRenderLods = false;
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
        _layer.DisableGpuTreeEffectiveEdges = true; // this case checks the guaranteed-prefix pruning against the configured bands; TR-9a has its own case
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
