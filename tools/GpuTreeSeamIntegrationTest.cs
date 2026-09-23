using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Real-RD regression for canonical seam precision in merged far tree groups.</summary>
public partial class GpuTreeSeamIntegrationTest : Node
{
    private const double Circumference = 31_415_936d;
    private RingBiomeDecorationLayer? _layer;
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        string result;
        try { await Run(); result = "PASS canonical seam LOD2/3 merged and unmerged"; }
        catch (Exception error) { result = "FAIL " + error.Message; }
        if (_layer != null) { _layer.QueueFree(); _layer = null; }
        await Frames(3);
        Result = result;
    }

    private async Task Run()
    {
        _layer = new RingBiomeDecorationLayer
        {
            Enabled = false, UseGpuCulling = true, EnableImportedForestCollisions = false,
            CellSizeMeters = 20, SpatialTileCells = 4,
            Lod1DistanceMeters = 30, Lod2DistanceMeters = 60, Lod3DistanceMeters = 190.02f,
            MinimumLod5DistanceMeters = 190.03f, MaximumLod5DistanceMeters = 1500,
            VisibilityRadiusMeters = 1500, MergeFarGpuTreeBatches = true
        };
        AddChild(_layer);
        Mesh[] meshes = { new BoxMesh(), new BoxMesh(), new BoxMesh(), new BoxMesh() };
        long lastTile = AroundTileForProductionLayout((long)Circumference - 3);
        long firstTile = AroundTileForProductionLayout(1);
        // These tiny local X origins are deliberately only identity markers.
        // Canonical metadata supplies distance: +1 is LOD2 at 190.00263m,
        // while circumference-3 is LOD3 at 190.02368m. The odd negative-side
        // root rounds to circumference-4 under the old float-before-subtract
        // merge arithmetic, becoming 190.0421m and incorrectly entering LOD4.
        Transform3D[][] roots =
        {
            new[] { new Transform3D(Basis.Identity, new Vector3(11.125f, 0, -10)) },
            new[] { new Transform3D(Basis.Identity, new Vector3(12.375f, 0, -10)) }
        };
        _layer.ConfigureGpuTreeFixtureGroups(meshes, roots,
            new[] { new[] { 0f }, new[] { 0f } }, new[] { new[] { false }, new[] { false } },
            new[] { lastTile, firstTile }, new[] { -1, -1 },
            new[] { new[] { new Vector2I((int)Circumference - 3, -10) }, new[] { new Vector2I(1, -10) } });

        RingCanonicalPosition focusZero = new(0, 0, -200);
        RingCanonicalPosition focusCircumference = new(Circumference, 0, -200);
        await Settle(focusZero);
        Dictionary<int, float[]> mergedZero = await Snapshot();
        CheckMarkers(mergedZero, "merged focus zero");
        await Settle(focusCircumference);
        Dictionary<int, float[]> mergedCircumference = await Snapshot();
        Check(Same(mergedZero[2], mergedCircumference[2]) && Same(mergedZero[3], mergedCircumference[3]), "seam-equivalent canonical focus changed accepted prefixes");

        _layer.MergeFarGpuTreeBatches = false;
        await Settle(focusZero);
        Dictionary<int, float[]> unmerged = await Snapshot();
        CheckMarkers(unmerged, "unmerged focus zero");
        Check(Same(mergedZero[2], unmerged[2]) && Same(mergedZero[3], unmerged[3]), "merge changed exact LOD prefixes");

        _layer.MergeFarGpuTreeBatches = true;
        await Settle(focusZero);
        Dictionary<int, float[]> remerged = await Snapshot();
        CheckMarkers(remerged, "remerged focus zero");
        Check(Same(mergedZero[2], remerged[2]) && Same(mergedZero[3], remerged[3]), "remerge changed exact LOD prefixes");
    }

    // Mirrors CaptureVegetationLayout/AroundTileForCanonical: around-cell
    // count is ceiling(circumference / 20), then four cells per source tile.
    private static long AroundTileForProductionLayout(long canonicalAround)
    {
        long aroundCellCount = Math.Max(1L, (long)Math.Ceiling(Circumference / 20d));
        double aroundCellMeters = Circumference / aroundCellCount;
        double around = canonicalAround % Circumference;
        if (around < 0) around += Circumference;
        long cell = Math.Clamp((long)Math.Floor(around / aroundCellMeters), 0, aroundCellCount - 1);
        return cell / 4;
    }

    private async Task Settle(RingCanonicalPosition focus)
    {
        for (int i = 0; i < 240; i++)
        {
            _layer!.StepGpuTreeFixture(Vector3.Zero, Transform3D.Identity, focus, Circumference);
            await Frames(1);
            if (_layer.GpuTreeCullingSettled) return;
        }
        throw new InvalidOperationException("GPU tree seam fixture did not settle: " + _layer!.GpuTreeCullingStatus);
    }

    private async Task<Dictionary<int, float[]>> Snapshot()
    {
        var snapshot = new Dictionary<int, float[]>();
        for (int lod = 0; lod < 5; lod++) snapshot[lod] = (await ReadAll(lod)).Rows;
        return snapshot;
    }

    private async Task<(uint[] Commands, float[] Rows)> ReadAll(int lod)
    {
        var completion = new TaskCompletionSource<(uint[], float[])>();
        _layer!.GpuTreeFixtureReadbackAll(lod, (commands, rows) => completion.TrySetResult((commands, rows)));
        return await completion.Task;
    }

    private static void CheckMarkers(Dictionary<int, float[]> rows, string label)
    {
        string observed = $" [L0={Markers(rows[0])}; L1={Markers(rows[1])}; L2={Markers(rows[2])}; L3={Markers(rows[3])}; L4={Markers(rows[4])}]";
        Check(HasExactly(rows[2], 12.375f), label + " expected positive-seam root in LOD2" + observed);
        Check(HasExactly(rows[3], 11.125f), label + " expected negative-seam root in LOD3" + observed);
        Check(rows[0].Length == 0 && rows[1].Length == 0 && rows[4].Length == 0, label + " root leaked to another LOD");
    }

    private static bool HasExactly(float[] rows, float marker)
    {
        if (rows.Length != 12) return false;
        return Mathf.IsEqualApprox(rows[3], marker);
    }

    private static string Markers(float[] rows)
    {
        var markers = new List<string>();
        for (int row = 0; row < rows.Length / 12; row++) markers.Add(rows[row * 12 + 3].ToString("0.###"));
        return string.Join(",", markers);
    }

    private static bool Same(float[] left, float[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private async Task Frames(int count)
    { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public override void _ExitTree() => _layer?.QueueFree();
}
