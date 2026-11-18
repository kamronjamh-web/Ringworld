using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;
using Key = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterTileKey;
using Streamer = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterStreamer;
using DiskConfig = RingworldTechDemo.Voxel.LOD.RingBiomeDecorationLayer.ScatterDiskConfig;

namespace RingworldTechDemo.Tools;

/// <summary>T5-4b streamer section (s1-s7): ScatterStreamer driven directly with the test table on the review world around steep_eye.</summary>
public partial class ScatterTileDeterminismTest
{
    private RingLodSurfaceEditSnapshot EditsAt(ulong version) => new(version, (long)Circ, new Dictionary<RingworldTechDemo.Voxel.Core.RingTerrainColumnCoordinate, int>());

    private Streamer NewStreamer(DiskConfig? disk = null)
        => new(_ctx, _root.CreateIndependentCopy(), disk, WorldGenerationLedger.Production);

    private List<Key> NeighbourTiles()
    {
        Tile steep = T("steep_eye");
        var keys = new List<Key>();
        for (long a = steep.TA - 2; a <= steep.TA + 2; a++) for (long z = steep.TZ - 1; z <= steep.TZ + 1; z++) keys.Add(KeyOf(a, z));
        return keys;
    }

    private void Resident(Streamer s, IEnumerable<Key> keys) { foreach (Key k in keys) s.OnTreeTileResident(k, Trees(k).Ids); }

    private (double A, double Z) SteepFocus() { Tile t = T("steep_eye"); return ((t.TA * 8 + 4) * _aroundCell, t.TZ * 240.0 + 120.0); }

    private int PumpUntilIdle(Streamer s, double a, double z, float height, RingLodSurfaceEditSnapshot edits, ulong version)
    {
        int pumps = 0;
        for (; pumps < 4000; pumps++)
        {
            s.Pump(a, z, height, edits, version);
            if (s.JobsInFlight == 0) break;
            Thread.Sleep(2);
        }
        Check(pumps < 4000, "streamer did not go idle");
        return pumps;
    }

    private List<ScatterRecord> Snapshot(Streamer s) { var list = new List<ScatterRecord>(); s.CopyRecords(list); return list; }

    private void StreamerChecks(string cacheRoot)
    {
        (double fa, double fz) = SteepFocus();
        RingLodSurfaceEditSnapshot v0 = _ctx.Edits;
        List<Key> keys = NeighbourTiles();
        foreach (Key k in keys) Trees(k);

        // s1: installed records equal BuildScatterRows for the due rows and blocks, in natural and in reversed arrival order.
        List<ScatterRecord>? firstRun = null;
        foreach (bool reversed in new[] { false, true })
        {
            using var _ = new Disposer(() => { });
            Streamer s = NewStreamer();
            Resident(s, reversed ? Enumerable.Reverse(keys) : keys);
            PumpUntilIdle(s, fa, fz, 2f, v0, 0);
            int withRecords = 0, mismatches = 0;
            foreach (Key k in keys)
            {
                var (rows, mask) = s.DueFor(k, fa, fz, 2f);
                var state = s.StateOf(k)!;
                Check(state.BuiltRows == rows && (rows == 0 || state.CobbleBlockMask == (_table.BuildOrder.Take(rows).Any(r => r.BlockM > 0) ? mask : 0)), $"s1. tile ({k.Around},{k.Across}) built {state.BuiltRows} rows mask {state.CobbleBlockMask:X}, due {rows} / {mask:X}");
                List<ScatterRecord> expected = rows == 0 ? new() : Build(k, Trees(k), Array.Empty<ScatterRecord>(), 0, rows, mask).Records;
                if (!Same(expected, state.Records)) mismatches++;
                if (state.Records.Count > 0) withRecords++;
            }
            Check(mismatches == 0, $"s1. {mismatches} tiles differ from BuildScatterRows ({(reversed ? "reversed" : "natural")} arrival)");
            Key steepKey = KeyOf(T("steep_eye"));
            Check(s.DueFor(steepKey, fa, fz, 2f).BlockMask != 0 && s.StateOf(steepKey)!.CobbleBlockMask != 0 && s.StateOf(steepKey)!.Records.Any(r => r.ClassId == 3), "s1. the focus tile has no cobbles");
            Check(s.DueFor(KeyOf(steepKey.Around + 2, steepKey.Across), fa, fz, 2f).BlockMask == 0, "s1. a tile 480 m away is due cobble blocks");
            Check(s.DueFor(steepKey, fa, fz, 80f).BlockMask == 0, "s1. blocks are due while the focus is 80 m above the ground");
            Check(s.MemoryBytes > 0 && s.RecordsResident > 0 && s.TilesResident > 0 && s.RecordEpoch > 0, "s1. diagnostics are empty");
            var snap = Snapshot(s);
            if (firstRun == null) firstRun = snap; else Check(Same(firstRun, snap), "s1. reversed arrival order installed different records");
            Check(s.MaxJobsInFlightObserved is >= 1 and <= 2, $"s7. in-flight jobs peaked at {s.MaxJobsInFlightObserved}");
            Detail($"s1/s7. {(reversed ? "reversed" : "natural")}: {keys.Count} tiles, {withRecords} with records, {snap.Count} records, {s.JobsStarted} jobs, peak in flight {s.MaxJobsInFlightObserved}, {s.MemoryBytes} bytes");

            if (!reversed)
            {
                // s2: 3 km away and back.
                long epoch = s.RecordEpoch; long before = s.RecordsResident;
                PumpUntilIdle(s, fa + 3000.0, fz, 2f, v0, 0);
                Check(s.RecordEpoch > epoch && s.RecordsResident < before, $"s2. moving 3 km away dropped nothing ({before} -> {s.RecordsResident} records)");
                Check(s.StateOf(steepKey)!.BuiltRows < _table.BuildOrder.Count, "s2. the steep tile kept all rows 3 km away");
                long far = s.RecordsResident;
                PumpUntilIdle(s, fa + 6000.0, fz, 2f, v0, 0);
                Check(s.RecordsResident == 0 && s.MemoryBytes < 64L * keys.Count + 16L * keys.Sum(k => (long)Trees(k).Count) + 1, "s2. 6 km away left records behind");
                PumpUntilIdle(s, fa, fz, 2f, v0, 0);
                Check(Same(firstRun!, Snapshot(s)), "s2. the records after going away and back differ from before");
                Detail($"s2. 3 km away: {before} -> {far} records; 6 km: 0; back: identical ({Snapshot(s).Count})");

                // s4: evicting a tile drops its state.
                long e2 = s.RecordEpoch; long recs = s.RecordsResident;
                int steepCount = s.StateOf(steepKey)!.Records.Count;
                s.OnTreeTileEvicted(steepKey);
                Check(s.StateOf(steepKey) == null && s.RecordEpoch > e2 && s.RecordsResident == recs - steepCount, "s4. eviction did not drop the tile's state");
                s.OnTreeTilesInvalidated(new[] { KeyOf(steepKey.Around + 1, steepKey.Across) });
                Check(s.StateOf(KeyOf(steepKey.Around + 1, steepKey.Across)) == null, "s4. invalidation did not drop the tile's state");
                // An eviction while a job is in flight: the result is discarded and no state comes back.
                Resident(s, new[] { steepKey });
                s.Pump(fa, fz, 2f, v0, 0);
                long discarded = s.ResultsDiscarded;
                s.OnTreeTileEvicted(steepKey);
                while (s.JobsInFlight > 0) { s.Pump(fa, fz, 2f, v0, 0); Thread.Sleep(2); }
                Check(s.StateOf(steepKey) == null && s.ResultsDiscarded > discarded, "s4. a job for an evicted tile installed or was not counted as discarded");
                Detail("s4. eviction and invalidation drop state; a job finishing for an evicted tile is discarded");
            }
            s.Shutdown();
        }

        // s3: a result computed under edit version 0 is discarded when the pump runs with version 1.
        {
            Streamer s = NewStreamer();
            Key steepKey = KeyOf(T("steep_eye"));
            Resident(s, new[] { steepKey });
            s.Pump(fa, fz, 2f, v0, 0);
            Check(s.JobsInFlight == 1, "s3. no job started");
            while (!s.AllJobsCompleted) Thread.Sleep(2);
            long discarded = s.ResultsDiscarded;
            RingLodSurfaceEditSnapshot v1 = EditsAt(1);
            s.Pump(fa, fz, 2f, v1, 1);
            Check(s.ResultsDiscarded == discarded + 1, "s3. the stale result was not discarded");
            Check(s.StateOf(steepKey)!.Records.Count == 0 && s.StateOf(steepKey)!.BuiltRows == 0, "s3. a stale record was installed");
            PumpUntilIdle(s, fa, fz, 2f, v1, 1);
            Check(Same(s.StateOf(steepKey)!.Records, Build(steepKey, Trees(steepKey), Array.Empty<ScatterRecord>(), 0, 3, s.DueFor(steepKey, fa, fz, 2f).BlockMask).Records), "s3. the rebuilt records differ from a direct build");
            Detail($"s3. stale result discarded (discarded {s.ResultsDiscarded}); rebuilt under version 1");
            s.Shutdown();
        }

        // s5: disk.
        {
            Directory.CreateDirectory(cacheRoot);
            ulong treeHash = RingBiomeDecorationLayer.VegetationCacheSettingsHash((long)Math.Ceiling(Circ / 30.0), Circ, 30f, Width, 8, 12, .72f, .20f, .70f, 32f, true);
            var disk = new DiskConfig(cacheRoot, RingScatterTileFile.Identity(_root, _table, treeHash), 64L * 1024 * 1024, 0);
            Key steepKey = KeyOf(T("steep_eye"));
            string path = RingScatterTileFile.PathFor(cacheRoot, disk.Identity, steepKey.Around, steepKey.Across);
            RingScatterTileFile.ScatterDiskCacheForTests = false;
            Streamer none = NewStreamer(disk);
            Resident(none, new[] { steepKey });
            PumpUntilIdle(none, fa, fz, 2f, v0, 0);
            Check(none.StateOf(steepKey)!.Records.Count > 0, "s5. no records built without the disk");
            Check(!Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories).Any(), "s5. a scatter file was written for an open feature");
            List<ScatterRecord> built = Snapshot(none);
            none.Shutdown();

            RingScatterTileFile.ScatterDiskCacheForTests = true;
            try
            {
                Streamer writer = NewStreamer(disk);
                Resident(writer, new[] { steepKey });
                PumpUntilIdle(writer, fa, fz, 2f, v0, 0);
                Check(writer.DiskWrites == 1 && File.Exists(path), "s5. the tile was not written with the test override on");
                Check(Same(built, Snapshot(writer)), "s5. the disk-enabled build differs from the memory-only build");
                writer.Shutdown();
                int calls = RingBiomeDecorationLayer.ScatterRowsBuildCalls;
                Streamer reader = NewStreamer(disk);
                Resident(reader, new[] { steepKey });
                PumpUntilIdle(reader, fa, fz, 2f, v0, 0);
                Check(reader.DiskLoads == 1 && RingBiomeDecorationLayer.ScatterRowsBuildCalls == calls, $"s5. the fresh streamer did not load from disk without building (loads {reader.DiskLoads}, builds {RingBiomeDecorationLayer.ScatterRowsBuildCalls - calls})");
                Check(Same(built, Snapshot(reader)), "s5. the loaded records differ from the built ones");
                reader.Shutdown();
                Detail($"s5. disk: nothing written for the open feature; with the override one write, and a fresh streamer loaded {built.Count} records with zero builds");
            }
            finally { RingScatterTileFile.ScatterDiskCacheForTests = false; }
        }

        // s6: feature off.
        {
            using RingTerrainSampler gen1 = LandscapeWorld.For(1).CreateSampler();
            bool made = false;
            Streamer? none = RingBiomeDecorationLayer.CreateScatterStreamerFor(gen1, () => { made = true; return NewStreamer(); });
            Check(none == null && !made, "s6. a generation 1 world created a scatter streamer");
            Streamer? some = RingBiomeDecorationLayer.CreateScatterStreamerFor(_root, () => { made = true; return NewStreamer(); });
            Check(some != null && made, "s6. the review world did not create a scatter streamer");
            some?.Shutdown();
            Check(!Directory.EnumerateFiles(cacheRoot, "s_*.bin", SearchOption.AllDirectories).Any(f => !f.Contains("scatter")), "s6. stray scatter files");
            Detail("s6. generation 1: no streamer; review world: streamer");
        }
    }

    private sealed class Disposer : IDisposable
    {
        private readonly Action _a; public Disposer(Action a) { _a = a; } public void Dispose() => _a();
    }
}
