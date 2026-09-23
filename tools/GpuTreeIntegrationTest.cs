using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Small real-RD fixture for the production tree GPU presentation path.</summary>
public partial class GpuTreeIntegrationTest : Node
{
    public string Result { get; private set; } = "RUNNING";
    private RingBiomeDecorationLayer? _layer;

    public async void Start()
    {
        string result;
        try { await Run(); result = "PASS tree LOD, stationary skip, grouped roots, metadata, replacement, toggles, rebase, teardown"; }
        catch (Exception error) { result = "FAIL " + error.Message; }
        if (_layer != null) { _layer.QueueFree(); _layer = null; }
        await Frames(3);
        Result = result;
    }

    private async Task Run()
    {
        _layer = new RingBiomeDecorationLayer { Enabled = false, UseGpuCulling = true, EnableImportedForestCollisions = false,
            Lod1DistanceMeters = 30, Lod2DistanceMeters = 60, Lod3DistanceMeters = 90, MinimumLod5DistanceMeters = 120,
            MaximumLod5DistanceMeters = 150, VisibilityRadiusMeters = 150 };
        AddChild(_layer);
        Mesh[] meshes = { new BoxMesh(), new BoxMesh(), new BoxMesh(), new BoxMesh() };
        Transform3D[] roots =
        {
            new Transform3D(new Basis(Vector3.Up, .2f).Scaled(new Vector3(1.1f,.9f,1.2f)), new Vector3(10,2,0)),
            new Transform3D(new Basis(Vector3.Up, -.5f).Scaled(new Vector3(.8f,1.3f,.9f)), new Vector3(50,3,0)),
            new Transform3D(new Basis(Vector3.Up, .7f).Scaled(new Vector3(1.2f,1.1f,.7f)), new Vector3(100,1,0))
        };
        _layer.ConfigureGpuTreeFixture(meshes, roots, new[] { 0f, 0f, 0f }, new[] { false, true, false });
        long epoch = _layer.GpuTreeSourceEpoch;
        await StepUntilReady(Vector3.Zero, Transform3D.Identity);
        var first = await Snapshot();
        Check(first[0].Count == 2 && first[3].Count == 1,
            $"initial lod bands/protected priority: {first[0].Count}, {first[1].Count}, {first[2].Count}, {first[3].Count}, {first[4].Count}");
        Check((await Count(1)) == 0 && (await Count(2)) == 0 && (await Count(4)) == 0, "protected root absent from other lods");
        var initial = await Snapshot();
        Check(ContainsBasis(initial[0], roots[0].Basis) && ContainsBasis(initial[0], roots[1].Basis), "non diagonal basis preserved");
        Check(_layer.GpuTreeBatchCount > 0 && _layer.GpuTreeResidentBytes > 0, "resident gpu batches");
        await StepFrames(Vector3.Zero, Transform3D.Identity, 2);
        Check(_layer.GpuTreeCullingSettled, "all gradual uploads ready and visible");
		_layer.MaximumTreeSunShadowBatches = 0;
		_layer.StepTreeSunShadowPolicyFixture(0, true, 1);
		Check(_layer.GpuTreeFixtureSunShadowRendererCount() == 0, "live shadow batch cap disabled every GPU caster");
		_layer.MaximumTreeSunShadowBatches = 128;
		_layer.TreeSunShadowDistanceMeters = 480;
		_layer.StepTreeSunShadowPolicyFixture(0, true, 1);
		Check(_layer.GpuTreeFixtureSunShadowRendererCount() > 0, "live shadow cap restored nearest GPU casters");
		Check(!_layer.GpuTreeFixtureLodCastsShadow(3), "far tree LOD never casts sun shadows");
		_layer.StepTreeSunShadowPolicyFixture(300, false, .1);
		Check(_layer.TreeSunShadowsSuppressed && _layer.GpuTreeFixtureSunShadowRendererCount() == 0, "fast travel suppresses existing GPU casters");
		_layer.StepTreeSunShadowPolicyFixture(0, false, 1);
		Check(_layer.TreeSunShadowsSuppressed, "streaming convergence holds shadows off");
		_layer.StepTreeSunShadowPolicyFixture(0, true, 1);
		Check(!_layer.TreeSunShadowsSuppressed && _layer.GpuTreeFixtureSunShadowRendererCount() > 0, "settled hold restores GPU casters");
        long staticEpoch = _layer.GpuTreeSourceEpoch;
        long completed = _layer.GpuTreeCompletedUpdates;
        await StepFrames(Vector3.Zero, Transform3D.Identity, 4);
        Check(_layer.GpuTreeSourceEpoch == staticEpoch, "static frame did not rebuild sources");
        Check(_layer.GpuTreeCompletedUpdates == completed, "settled static frame skipped backend dispatch");

        await StepFrames(new Vector3(70, 0, 0), Transform3D.Identity, 5);
        Check(_layer.GpuTreeSourceEpoch == epoch, "camera did not rewrite source");
        var moved = await Snapshot();
        Check(moved[0].Count == 1 && moved[1].Count == 1 && moved[2].Count == 1 && moved[3].Count == 0, "camera lod bands");
        await StepFrames(new Vector3(170, 0, 0), new Transform3D(Basis.Identity, new Vector3(100,0,0)), 5);
        Check(_layer.GpuTreeSourceEpoch == epoch, "rebase did not rewrite source");
        var rebased = await Snapshot();
        for (int lod = 0; lod < 5; lod++) Check(moved[lod].Count == rebased[lod].Count && Same(moved[lod].Rows, rebased[lod].Rows), "rebase local output " + lod);

        roots[2].Origin = new Vector3(20, 1, 0);
        _layer.ConfigureGpuTreeFixture(meshes, roots, new[] { 0f, 0f, 0f }, new[] { false, true, false });
        Check(_layer.GpuTreeSourceEpoch > epoch, "source update epoch");
        await StepUntilReady(Vector3.Zero, Transform3D.Identity);
        Check((await Count(0)) == 3, "edited source compacted");

        _layer.UseGpuCulling = false;
        _layer.StepGpuTreeFixture(Vector3.Zero, Transform3D.Identity);
        await Frames(3);
        Check(_layer.GpuTreeBatchCount == 0, "disabled retires batches");
        _layer.UseGpuCulling = true;
        await StepUntilReady(Vector3.Zero, Transform3D.Identity);
        Check((await Count(0)) == 3, "reenabled gpu presentation");
        await VerifyGroupedMerge(meshes);
        _layer.QueueFree(); _layer = null;
    }

    private async Task VerifyGroupedMerge(Mesh[] meshes)
    {
        RingBiomeDecorationLayer layer = _layer ?? throw new InvalidOperationException("GPU tree fixture missing");
        // Tile 0 and 1 share the 2x far bin; tile 2 is in the next bin.
        Transform3D[][] groups =
        {
            new[] { new Transform3D(new Basis(Vector3.Up, .31f), new Vector3(100, 2, 0)) },
            new[] { new Transform3D(new Basis(Vector3.Up, -.22f), new Vector3(101, 2, 0)) },
            new[] { new Transform3D(new Basis(Vector3.Up, .48f), new Vector3(102, 2, -1)) }
        };
        const int circumference = 31_415_936;
        _layer.CellSizeMeters = 20; _layer.SpatialTileCells = 4;
        _layer.Lod1DistanceMeters = 30; _layer.Lod2DistanceMeters = 60; _layer.Lod3DistanceMeters = 90;
        _layer.MinimumLod5DistanceMeters = 1000; _layer.MaximumLod5DistanceMeters = 1500;
        layer.ConfigureGpuTreeFixtureGroups(meshes, groups, new[] { new[] { 0f }, new[] { 0f }, new[] { 0f } },
            new[] { new[] { false }, new[] { false }, new[] { false } }, new long[] { 0, 1, 2 }, new[] { -1, -1, -1 },
            new[] { new[] { new Vector2I(10, -10) }, new[] { new Vector2I(90, -10) }, new[] { new Vector2I(170, -10) } });
        var focus = new RingworldTechDemo.Voxel.Core.RingCanonicalPosition(0, 0, -200);
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        int[] mergedIds = _layer.GpuTreeFixtureOutputIds(3);
        Check(mergedIds.Length == 2, "same-bin far groups merged; different bin retained");
        var completion = new TaskCompletionSource<(uint[], float[])>();
        _layer.GpuTreeFixtureReadbackAll(3, (commands, rows) => completion.TrySetResult((commands, rows)));
        (uint[] commands, float[] rows) = await completion.Task;
        uint roots = 0; for (int i = 1; i < commands.Length; i += 5) roots += commands[i];
        Check(roots == 3 && rows.Length == 36 && ContainsOrigins(rows, 100, 101, 102), "merged far exact root set without duplicates");
        layer.SetGpuTreeFixtureProtected(0, -1, 0, true);
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(SameIds(mergedIds, layer.GpuTreeFixtureOutputIds(3)), "protect retained merged far batch ids");
        Check((await ReadAll(0)).Rows.Length / 12 == 1 && ContainsOrigins((await ReadAll(0)).Rows, 100), "protected source did not move to LOD0");
        Check(!ContainsOrigins((await ReadAll(3)).Rows, 100), "protected source remained in LOD3");
        layer.SetGpuTreeFixtureProtected(0, -1, 0, false);
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(SameIds(mergedIds, layer.GpuTreeFixtureOutputIds(3)), "unprotect retained merged far batch ids");
        Check((await ReadAll(0)).Rows.Length == 0 && ContainsOrigins((await ReadAll(3)).Rows, 100), "unprotected source did not return to LOD3");
        layer.ReplaceGpuTreeFixtureSource(0, -1, 0, 110);
        Check(layer.GpuTreeFallbackBatchCount > 0, "source replacement did not retain a ready fallback output");
        Check(layer.GpuTreeFallbackBatchCount <= layer.MaximumGpuTreeBatches, "fallback replacement exceeded the output cap");
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(layer.GpuTreeFallbackBatchCount == 0, "ready replacement did not retire its fallback output");
        int[] replacedIds = layer.GpuTreeFixtureOutputIds(3);
        Check(replacedIds.Length == 2 && replacedIds[0] != mergedIds[0] && replacedIds[1] == mergedIds[1], "source replacement did not preserve unrelated merged output");
        Check(ContainsExactly((await ReadAll(3)).Rows, 110, 101, 102), "source replacement did not update merged root prefix");
        layer.Lod3DistanceMeters = 220;
        long beforeLodChange = layer.GpuTreeCompletedUpdates;
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(ContainsOrigins((await ReadAll(2)).Rows, 110, 101) && (await ReadAll(2)).Rows.Length / 12 == 2, "stationary LOD edge did not move 190/210 roots to LOD2");
        Check(ContainsOrigins((await ReadAll(3)).Rows, 102) && (await ReadAll(3)).Rows.Length / 12 == 1, "stationary LOD edge did not retain 255 root in LOD3");
        Check(layer.GpuTreeCompletedUpdates > beforeLodChange, "stationary LOD edge did not resume GPU updates");
        layer.Lod3DistanceMeters = 90;
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(ContainsExactly((await ReadAll(3)).Rows, 110, 101, 102), "restored LOD edge did not resume far output");
        layer.MergeFarGpuTreeBatches = false;
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(_layer.GpuTreeFixtureOutputIds(3).Length == 3, "unmerged far outputs recreate safely");
        var unmerged = await ReadAll(3);
        Check(ContainsExactly(unmerged.Rows, 110, 101, 102), "unmerged exact root set");
        layer.MergeFarGpuTreeBatches = true;
        await StepUntilReadyCanonical(Vector3.Zero, Transform3D.Identity, focus, circumference);
        Check(layer.GpuTreeFixtureOutputIds(3).Length == 2, "remerged far outputs recreate safely");
        Check(ContainsExactly((await ReadAll(3)).Rows, 110, 101, 102), "remerged exact root set");
    }

    private async Task StepUntilReady(Vector3 camera, Transform3D transform)
    {
        for (int i = 0; i < 180; i++)
        {
            _layer!.StepGpuTreeFixture(camera, transform);
            await Frames(1);
            if (_layer.GpuTreeCullingActive && await Count(0) + await Count(1) + await Count(2) + await Count(3) + await Count(4) > 0)
            {
                // Output registration is spread over several frames. The first
                // ready LOD does not imply that all requested LODs exist yet.
                await StepFrames(camera, transform, 10);
                return;
            }
        }
        throw new InvalidOperationException("gpu fixture timeout: " + _layer!.GpuTreeCullingStatus);
    }
    private async Task StepUntilReadyCanonical(Vector3 camera, Transform3D transform, RingworldTechDemo.Voxel.Core.RingCanonicalPosition focus, double circumference)
    {
        for (int i = 0; i < 180; i++) { _layer!.StepGpuTreeFixture(camera, transform, focus, circumference); await Frames(1); if (_layer.GpuTreeCullingSettled) return; }
        throw new InvalidOperationException("canonical gpu fixture timeout: " + _layer!.GpuTreeCullingStatus);
    }

    private async Task StepFrames(Vector3 camera, Transform3D transform, int frames)
    { for (int i = 0; i < frames; i++) { _layer!.StepGpuTreeFixture(camera, transform); await Frames(1); } }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task<uint> Count(int lod)
    {
        var completion = new TaskCompletionSource<uint>();
        _layer!.GpuTreeFixtureReadback(lod, (commands, _) => completion.TrySetResult(commands.Length >= 2 ? commands[1] : 0));
        return await completion.Task;
    }
    private async Task<(uint[] Commands, float[] Rows)> ReadAll(int lod)
    {
        var completion = new TaskCompletionSource<(uint[], float[])>();
        _layer!.GpuTreeFixtureReadbackAll(lod, (commands, rows) => completion.TrySetResult((commands, rows)));
        return await completion.Task;
    }
    private async Task<(uint Count, float[] Rows)[]> Snapshot()
    {
        var result = new (uint Count, float[] Rows)[5];
        for (int lod = 0; lod < result.Length; lod++)
        {
            var completion = new TaskCompletionSource<(uint[], float[])>();
            _layer!.GpuTreeFixtureReadback(lod, (commands, rows) => completion.TrySetResult((commands, rows)));
            (uint[] commands, float[] rows) = await completion.Task;
            uint count = commands.Length >= 2 ? commands[1] : 0;
            var validRows = new float[count * 12];
            Array.Copy(rows, validRows, validRows.Length);
            result[lod] = (count, validRows);
        }
        return result;
    }
    private static bool ContainsBasis((uint Count, float[] Rows) data, Basis basis)
    {
        for (int row = 0; row < (int)data.Count; row++)
        {
            int i = row * 12;
            if (data.Rows.Length >= i + 12 && Mathf.IsEqualApprox(data.Rows[i], basis.X.X)
                && Mathf.IsEqualApprox(data.Rows[i + 1], basis.Y.X) && Mathf.IsEqualApprox(data.Rows[i + 2], basis.Z.X)
                && Mathf.IsEqualApprox(data.Rows[i + 4], basis.X.Y) && Mathf.IsEqualApprox(data.Rows[i + 8], basis.X.Z)) return true;
        }
        return false;
    }
    private static bool Same(float[] left, float[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (!Mathf.IsEqualApprox(left[i], right[i])) return false;
        return true;
    }
    private static bool ContainsOrigins(float[] rows, params float[] expected)
    {
        var found = new System.Collections.Generic.HashSet<int>();
        for (int i = 3; i < rows.Length; i += 12) for (int e = 0; e < expected.Length; e++) if (Mathf.IsEqualApprox(rows[i], expected[e])) found.Add(e);
        return found.Count == expected.Length;
    }
    private static bool SameIds(int[] left, int[] right) => left.Length == right.Length && left.AsSpan().SequenceEqual(right);
    private static bool ContainsExactly(float[] rows, params float[] expected)
        => rows.Length == expected.Length * 12 && ContainsOrigins(rows, expected);
    public override void _ExitTree() => _layer?.QueueFree();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
