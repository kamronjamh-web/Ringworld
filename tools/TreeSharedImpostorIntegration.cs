using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Real-RD regression for shared imported-tree far impostor batching.</summary>
public partial class TreeSharedImpostorIntegration : Node
{
    private RingBiomeDecorationLayer? _layer;
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        string result;
        try { await Run(); result = "PASS shared far-tree impostor batching, rows, protection, and toggle fallback"; }
        catch (Exception error) { result = "FAIL " + error.Message; }
        if (_layer != null) { _layer.QueueFree(); _layer = null; }
        await Frames(12);
        Result = result;
    }

    private async Task Run()
    {
        _layer = new RingBiomeDecorationLayer
        {
            Enabled = false, UseGpuCulling = true, EnableImportedForestCollisions = false,
            MergeFarGpuTreeBatches = true, UseSharedFarTreeImpostors = false,
            CellSizeMeters = 20, SpatialTileCells = 4,
            Lod1DistanceMeters = 30, Lod2DistanceMeters = 60, Lod3DistanceMeters = 90,
            MinimumLod5DistanceMeters = 1000, MaximumLod5DistanceMeters = 1500, VisibilityRadiusMeters = 1500
        };
        AddChild(_layer);
        IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
        Transform3D[][] transforms =
        {
            new[]
            {
                new Transform3D(Basis.Identity, new Vector3(1120, 0, 0)),
                new Transform3D(Basis.Identity, new Vector3(1125, 0, 0))
            },
            new[]
            {
                new Transform3D(Basis.Identity, new Vector3(1130, 0, 0)),
                new Transform3D(Basis.Identity, new Vector3(1135, 0, 0))
            }
        };
        _layer.ConfigureGpuTreeBenchmarkFixture(catalog, transforms,
            new[] { new[] { 0f, 0f }, new[] { 0f, 0f } },
            new[] { new[] { true, false }, new[] { false, false } },
            new long[] { 0, 1 }, new[] { 0, 0 }, new[] { 0, 1 });

        Vector3 camera = new(1000, 0, 0);
        await Settle(camera);
        (int legacyBatches, uint legacyRoots, float[] legacyRows) = await FarSnapshot();
        Check(legacyBatches == 2 && legacyRoots == 3 && legacyRows.Length == 36, "legacy imported fixture far rows or protected exclusion changed");
        Check((await VisibleCount(0)) == 1, "protected imported root was not retained in near LOD");
        int sourceRoots = _layer.GpuTreeResidentRootCount;

        _layer.UseSharedFarTreeImpostors = true;
        await Settle(camera);
        (int sharedBatches, uint sharedRoots, float[] sharedRows) = await FarSnapshot();
        Check(sharedBatches < legacyBatches && sharedBatches == 1, "shared far toggle did not collapse same-bin variants");
        Check(sharedRoots == legacyRoots && sharedRows.Length == sharedRoots * 16, "shared far rows did not retain the unprotected root set");
        Check(VariantRows(sharedRows, 0) == 1 && VariantRows(sharedRows, 1) == 2, "shared custom variant IDs were not preserved");
        Check(_layer.GpuTreeResidentRootCount == sourceRoots && _layer.GpuTreeFallbackBatchCount == 0, "shared toggle changed sources or left fallback after settle");
        Check((await VisibleCount(0)) == 1, "protected metadata did not survive shared far merge");

        _layer.UseSharedFarTreeImpostors = false;
        await Settle(camera);
        (int restoredBatches, uint restoredRoots, float[] restoredRows) = await FarSnapshot();
        Check(restoredBatches == legacyBatches && restoredRoots == legacyRoots && restoredRows.Length == legacyRows.Length,
            "disabling shared far batches did not restore legacy format");
        Check(_layer.GpuTreeResidentRootCount == sourceRoots && _layer.GpuTreeFallbackBatchCount == 0, "legacy restoration changed sources or left fallback after settle");
    }

    private async Task Settle(Vector3 camera)
    {
        for (int frame = 0; frame < 240; frame++)
        {
            _layer!.StepGpuTreeFixture(camera, Transform3D.Identity);
            await Frames(1);
            if (_layer.GpuTreeCullingSettled) return;
        }
        throw new InvalidOperationException("shared impostor fixture did not settle: " + _layer!.GpuTreeCullingStatus);
    }

    private async Task<(int Batches, uint Roots, float[] Rows)> FarSnapshot()
    {
        var read = await ReadAll(3);
        Check(read.Commands.Length > 0, "far fixture did not produce indirect commands");
        int stride = _layer!.UseSharedFarTreeImpostors ? 16 : 12;
        uint roots = (uint)(read.Rows.Length / stride);
        return (_layer!.GpuTreeFixtureOutputIds(3).Length, roots, read.Rows);
    }

    private async Task<uint> VisibleCount(int lod)
    {
        var read = await ReadAll(lod);
        return read.Commands.Length >= 2 ? read.Commands[1] : 0;
    }

    private async Task<(uint[] Commands, float[] Rows)> ReadAll(int lod)
    {
        var completion = new TaskCompletionSource<(uint[], float[])>();
        _layer!.GpuTreeFixtureReadbackAll(lod, (commands, rows) => completion.TrySetResult((commands, rows)));
        return await completion.Task;
    }

    private static int VariantRows(float[] rows, int variant)
    {
        int count = 0;
        for (int row = 0; row < rows.Length / 16; row++) if (Mathf.IsEqualApprox(rows[row * 16 + 12], variant)) count++;
        return count;
    }

    private async Task Frames(int count)
    { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    public override void _ExitTree() => _layer?.QueueFree();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
