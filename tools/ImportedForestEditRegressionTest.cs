using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Worker-only regression for the root-support rule. It never edits a
/// live world: snapshots are passed to the same imported-forest tile builder.
/// </summary>
public partial class ImportedForestEditRegressionTest : RefCounted
{
    public bool Run()
    {
        try
        {
            const long circumference = 981_748L * 32L; const int width = 318_000; const double cell = 30;
            var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
                ?? throw new InvalidOperationException("terrain settings missing"));
            using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000.0, width, 8000);
            long cells = (long)Math.Ceiling(circumference / cell);
            long tx = (long)Math.Floor(186000 / cell) / 8, tz = (long)Math.Floor(141000 / cell) / 8;
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> baseline = Build(sampler, tx, tz, cells, settings.Seed, circumference, width);
            Require(baseline.Count > 0, "verified grove tile has no baseline records");
            RingBiomeDecorationLayer.ImportedForestPlacementRecord root = baseline[0];

            var lowered = Snapshot(root, circumference, -8, 1);
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> excavated = Build(sampler, tx, tz, cells, settings.Seed, circumference, width, lowered);
            Require(!Contains(excavated, root.Id), "excavated root survived or was relocated");
            Require(excavated.Count < baseline.Count, "excavation did not remove a placement");

            var raised = Snapshot(root, circumference, 8, 2);
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> elevated = Build(sampler, tx, tz, cells, settings.Seed, circumference, width, raised);
            Require(!Contains(elevated, root.Id), "raised edited root survived instead of rebuilding support");

            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> restored = Build(sampler, tx, tz, cells, settings.Seed, circumference, width);
            var identity = new RingLodSurfaceEditSnapshot(3, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
            List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> identityRestored = Build(sampler, tx, tz, cells, settings.Seed, circumference, width, identity);
            Require(Equal(baseline, restored) && Equal(baseline, identityRestored), "unmodified snapshot did not restore exact canonical records");
            GD.Print($"IMPORTED_FOREST_EDIT_REGRESSION baseline={baseline.Count} excavated={excavated.Count} raised={elevated.Count} root={root.Id:X}");
            return true;
        }
        catch (Exception ex) { GD.PushError("IMPORTED_FOREST_EDIT_REGRESSION=FAIL " + ex.Message); return false; }
    }

    static List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> Build(RingTerrainSampler s, long tx, long tz, long cells, int seed, long circumference, int width, RingLodSurfaceEditSnapshot? edits = null) =>
        RingBiomeDecorationLayer.BuildImportedForestReviewTile(s, tx, tz, cells, 30, seed, circumference, width, edits);
    static RingLodSurfaceEditSnapshot Snapshot(RingBiomeDecorationLayer.ImportedForestPlacementRecord root, long circumference, int delta, ulong version)
    {
        var columns = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long around = root.Around - 1; around <= root.Around + 1; around++)
        for (int across = root.Across - 1; across <= root.Across + 1; across++)
            columns[new RingTerrainColumnCoordinate(around, across)] = Mathf.RoundToInt(root.SurfaceHeight) + delta;
        return new RingLodSurfaceEditSnapshot(version, circumference, columns);
    }
    static bool Contains(IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord> records, ulong id)
    { foreach (var record in records) if (record.Id == id) return true; return false; }
    static bool Equal(IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord> a, IReadOnlyList<RingBiomeDecorationLayer.ImportedForestPlacementRecord> b)
    { if (a.Count != b.Count) return false; for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false; return true; }
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
