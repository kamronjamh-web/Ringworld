using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Loading;

/// <summary>
/// One-time deletion of the retired proxy sector caches (docs/load-time-plan.md, #12 follow-up): every
/// <c>user://worlds/slot_*/lod_cache/lod_sectors</c> folder and <c>user://ring_cache/lod_sectors</c>. Only a folder named exactly
/// <c>lod_sectors</c> directly under those roots is touched; summaries, vegetation, scatter tiles, global_terrain.bin, hydrology and saves stay.
/// Runs on a worker. A locked or missing file is tolerated: the pass then leaves no marker and the next launch retries.
/// The marker is a small file directly under user:// (no existing settings resource is app level).
/// </summary>
public static class RingLegacyCacheCleanup
{
    internal const string MarkerFileName = "legacy_lod_sectors_cleanup_v1.done";
    internal const string TargetDirectoryName = "lod_sectors";
    private static int _started;

    internal readonly record struct Result(bool AlreadyDone, int Folders, long Files, long Bytes, int Failures, bool MarkerSet, double Milliseconds);

    /// <summary>Starts the cleanup once per process on a worker. Called from the loading bridge before the world attaches.</summary>
    public static void StartOnce()
    {
        if (!ShouldRun() || Interlocked.Exchange(ref _started, 1) != 0) return;
        string userRoot = ProjectSettings.GlobalizePath("user://");
        Task.Run(() =>
        {
            try
            {
                Result result = Run(userRoot);
                if (!result.AlreadyDone)
                    GD.Print($"Legacy cache cleanup: removed {result.Files} files ({result.Bytes / (1024.0 * 1024.0):0.0} MiB) from {result.Folders} lod_sectors folders in {result.Milliseconds:0} ms, failures={result.Failures}, marker={(result.MarkerSet ? "set" : "not set, retries next launch")}");
            }
            catch (Exception exception)
            {
                GD.PushWarning($"Legacy cache cleanup skipped: {exception.GetBaseException().Message}");
            }
        });
    }

    /// <summary>
    /// Script and test launches (the engine's --script argument) never run it unless they pass --legacy-cache-cleanup, so a test cannot touch a profile by accident.
    /// --no-legacy-cache-cleanup turns it off everywhere.
    /// </summary>
    private static bool ShouldRun()
    {
        string[] user = OS.GetCmdlineUserArgs();
        if (Array.Exists(user, argument => argument.Equals("--no-legacy-cache-cleanup", StringComparison.OrdinalIgnoreCase))) return false;
        if (Array.Exists(user, argument => argument.Equals("--legacy-cache-cleanup", StringComparison.OrdinalIgnoreCase))) return true;
        return !Array.Exists(OS.GetCmdlineArgs(), argument => argument.Equals("--script", StringComparison.OrdinalIgnoreCase) || argument.Equals("-s", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs one pass against a user data folder (its absolute path). A present marker makes it a no-op that scans nothing.</summary>
    internal static Result Run(string userRoot)
    {
        var watch = Stopwatch.StartNew();
        string marker = Path.Combine(userRoot, MarkerFileName);
        if (File.Exists(marker)) return new Result(true, 0, 0, 0, 0, true, 0.0);
        int folders = 0, failures = 0;
        long files = 0, bytes = 0;
        void Remove(string directory)
        {
            DirectoryInfo info = new(directory);
            // A missing folder is done; a link is never followed, so nothing outside the cache can be reached through it.
            if (!info.Exists) return;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { failures++; return; }
            folders++;
            RemoveTree(info, ref files, ref bytes, ref failures);
        }
        Remove(Path.Combine(userRoot, "ring_cache", TargetDirectoryName));
        string worlds = Path.Combine(userRoot, "worlds");
        if (Directory.Exists(worlds))
        {
            string[] slots;
            try { slots = Directory.GetDirectories(worlds, "slot_*", SearchOption.TopDirectoryOnly); }
            catch (Exception) { slots = Array.Empty<string>(); failures++; }
            foreach (string slot in slots) Remove(Path.Combine(slot, "lod_cache", TargetDirectoryName));
        }
        bool markerSet = false;
        if (failures == 0)
        {
            try
            {
                Directory.CreateDirectory(userRoot);
                File.WriteAllText(marker, "Retired lod_sectors proxy caches removed " + DateTime.UtcNow.ToString("O") + "\n");
                markerSet = true;
            }
            catch (Exception) { failures++; }
        }
        return new Result(false, folders, files, bytes, failures, markerSet, watch.Elapsed.TotalMilliseconds);
    }

    private static void RemoveTree(DirectoryInfo directory, ref long files, ref long bytes, ref int failures)
    {
        try
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { failures++; continue; }
                if (entry is DirectoryInfo child) { RemoveTree(child, ref files, ref bytes, ref failures); continue; }
                try
                {
                    long length = ((FileInfo)entry).Length;
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                    files++;
                    bytes += length;
                }
                catch (Exception) { failures++; }
            }
            directory.Delete(false);
        }
        catch (Exception) { failures++; }
    }
}
