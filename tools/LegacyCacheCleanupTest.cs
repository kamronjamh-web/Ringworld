using System;
using System.IO;
using Godot;
using RingworldTechDemo.Loading;

namespace RingworldTechDemo;

/// <summary>Headless contract for the one-time lod_sectors deletion: only those folders go, everything else stays, and the marker makes a second pass a no-op. Uses a per-run scratch user folder, never the real profile.</summary>
public partial class LegacyCacheCleanupTest : RefCounted
{
    public bool Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "ringworld_legacycleanup_" + Guid.NewGuid().ToString("N"));
        try
        {
            Build(root);
            RingLegacyCacheCleanup.Result first = RingLegacyCacheCleanup.Run(root);
            Require(!first.AlreadyDone && first.Failures == 0 && first.MarkerSet, "first pass failed or did not set the marker");
            Require(first.Folders == 3 && first.Files == 9, $"first pass removed {first.Folders} folders / {first.Files} files, expected 3 / 9");
            Require(first.Bytes == 9 * 100, "first pass byte count is wrong");
            Require(!Directory.Exists(Path.Combine(root, "ring_cache", "lod_sectors")), "machine lod_sectors survived");
            Require(!Directory.Exists(Path.Combine(root, "worlds", "slot_1", "lod_cache", "lod_sectors")), "slot_1 lod_sectors survived");
            Require(!Directory.Exists(Path.Combine(root, "worlds", "slot_2", "lod_cache", "lod_sectors")), "slot_2 lod_sectors survived");
            foreach (string kept in new[]
            {
                Path.Combine("ring_cache", "ring_summary_v17_a.bin"),
                Path.Combine("ring_cache", "other", "lod_sectors", "keep.bin"),
                Path.Combine("worlds", "slot_1", "lod_cache", "global_terrain.bin"),
                Path.Combine("worlds", "slot_1", "lod_cache", "ring_summary_v17_b.bin"),
                Path.Combine("worlds", "slot_1", "lod_cache", "vegetation", "tile.bin"),
                Path.Combine("worlds", "slot_1", "lod_cache", "scatter", "lod_sectors_like", "t.bin"),
                Path.Combine("worlds", "slot_1", "world.save"),
                Path.Combine("worlds", "slot_2", "hydrology", "graph.bin"),
                Path.Combine("worlds", "notslot", "lod_cache", "lod_sectors", "keep.bin"),
            })
                Require(File.Exists(Path.Combine(root, kept)), "cleanup removed a kept file: " + kept);
            Require(File.Exists(Path.Combine(root, RingLegacyCacheCleanup.MarkerFileName)), "marker file missing");

            // A second pass must not scan: a folder that appears afterwards survives.
            string late = Path.Combine(root, "worlds", "slot_1", "lod_cache", "lod_sectors", "late.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(late)!);
            File.WriteAllBytes(late, new byte[10]);
            RingLegacyCacheCleanup.Result second = RingLegacyCacheCleanup.Run(root);
            Require(second.AlreadyDone && second.Files == 0 && File.Exists(late), "second pass was not a no-op");

            // Without the marker an unlinkable file leaves the marker unset (retry next launch) while the rest is removed.
            File.Delete(Path.Combine(root, RingLegacyCacheCleanup.MarkerFileName));
            string locked = Path.Combine(root, "worlds", "slot_2", "lod_cache", "lod_sectors", "a", "locked.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
            File.WriteAllBytes(locked, new byte[10]);
            File.WriteAllBytes(Path.Combine(root, "worlds", "slot_2", "lod_cache", "lod_sectors", "a", "free.bin"), new byte[10]);
            using (new FileStream(locked, FileMode.Open, System.IO.FileAccess.Read, FileShare.None))
            {
                RingLegacyCacheCleanup.Result partial = RingLegacyCacheCleanup.Run(root);
                Require(partial.Failures > 0 && !partial.MarkerSet && !File.Exists(Path.Combine(root, RingLegacyCacheCleanup.MarkerFileName)), "a locked file must leave the marker unset");
                Require(!File.Exists(late), "partial pass did not remove the free files");
            }
            RingLegacyCacheCleanup.Result retry = RingLegacyCacheCleanup.Run(root);
            Require(retry.Failures == 0 && retry.MarkerSet && !File.Exists(locked), "retry after the lock released did not finish");
            GD.Print("LEGACY_CACHE_CLEANUP=PASS");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("LEGACY_CACHE_CLEANUP=FAIL " + exception.Message);
            return false;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    private static void Build(string root)
    {
        void File100(string relative)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[100]);
        }
        // Three lod_sectors folders, three files each (one nested).
        foreach (string target in new[] { Path.Combine("ring_cache", "lod_sectors"), Path.Combine("worlds", "slot_1", "lod_cache", "lod_sectors"), Path.Combine("worlds", "slot_2", "lod_cache", "lod_sectors") })
        {
            File100(Path.Combine(target, "key1", "s_0_0.bin"));
            File100(Path.Combine(target, "key1", "s_0_1.bin"));
            File100(Path.Combine(target, "key2", "s_1_0.bin"));
        }
        File100(Path.Combine("ring_cache", "ring_summary_v17_a.bin"));
        File100(Path.Combine("ring_cache", "other", "lod_sectors", "keep.bin"));
        File100(Path.Combine("worlds", "slot_1", "lod_cache", "global_terrain.bin"));
        File100(Path.Combine("worlds", "slot_1", "lod_cache", "ring_summary_v17_b.bin"));
        File100(Path.Combine("worlds", "slot_1", "lod_cache", "vegetation", "tile.bin"));
        File100(Path.Combine("worlds", "slot_1", "lod_cache", "scatter", "lod_sectors_like", "t.bin"));
        File100(Path.Combine("worlds", "slot_1", "world.save"));
        File100(Path.Combine("worlds", "slot_2", "hydrology", "graph.bin"));
        File100(Path.Combine("worlds", "notslot", "lod_cache", "lod_sectors", "keep.bin"));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
