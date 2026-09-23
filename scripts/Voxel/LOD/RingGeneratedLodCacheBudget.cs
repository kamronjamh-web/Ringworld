using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Maintains a disposable, size-bounded cache for generated regional LOD data.
/// Authoritative world edits live outside these cache roots and are never
/// considered for eviction. Everything matched here is generated and rebuildable.
/// </summary>
internal static class RingGeneratedLodCacheBudget
{
    internal readonly record struct MaintenanceResult(
        string RootDirectory,
        long BytesBefore,
        long BytesAfter,
        int DeletedFileCount,
        string Error);

    private readonly record struct CacheFile(string Path, long Length, long AccessTicks);
    private readonly record struct WriteReservation(string RootDirectory, long ExistingLength, long ReservedGrowth);

    private static readonly ConcurrentDictionary<string, long> RecentAccessTicks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> NextInspectionTicks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ActiveMaintenance = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> ApproximateRootBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> RegisteredBudgetBytes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, MaintenanceResult> LatestMaintenance = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, object> RootWriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, WriteReservation> PendingWrites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<MaintenanceResult> CompletedMaintenance = new();

    internal static void Touch(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            RecentAccessTicks[NormalizePath(path)] = DateTime.UtcNow.Ticks;
    }

    internal static bool TryTakeCompleted(out MaintenanceResult result)
        => CompletedMaintenance.TryDequeue(out result);

    internal static bool TryGetLatest(string rootDirectory, out MaintenanceResult result)
        => LatestMaintenance.TryGetValue(NormalizeRoot(rootDirectory), out result);

    /// <summary>
    /// Reserves space for a background cache write. The first writer measures
    /// the existing cache synchronously, then concurrent writers serialize on a
    /// small per-root lock so they cannot all spend the same remaining budget.
    /// Call RecordWrite on success or CancelWriteReservation on failure.
    /// </summary>
    internal static bool CanWrite(
        string rootDirectory,
        string targetPath,
        long budgetBytes,
        long minimumFreeBytes,
        long estimatedWriteBytes)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)
            || string.IsNullOrWhiteSpace(targetPath)
            || budgetBytes <= 0L
            || estimatedWriteBytes < 0L)
        {
            return false;
        }

        string root = NormalizeRoot(rootDirectory);
        string target = NormalizePath(targetPath);
        object writeLock = RootWriteLocks.GetOrAdd(root, static _ => new object());
        lock (writeLock)
        {
            if (PendingWrites.ContainsKey(target)) return false;
            if (!TryResolveKnownBytes(root, out long knownBytes)) return false;
            if (RegisteredBudgetBytes.TryGetValue(root, out long registeredBudget))
                budgetBytes = Math.Min(budgetBytes, registeredBudget);

            long existingLength = 0L;
            try
            {
                if (File.Exists(target)) existingLength = new FileInfo(target).Length;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            long availableFreeBytes = long.MaxValue;
            try
            {
                string? driveRoot = Path.GetPathRoot(target);
                if (!string.IsNullOrWhiteSpace(driveRoot))
                {
                    var drive = new DriveInfo(driveRoot);
                    if (drive.IsReady) availableFreeBytes = drive.AvailableFreeSpace;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            if (!FitsWithinBudget(knownBytes, budgetBytes, existingLength, estimatedWriteBytes, availableFreeBytes, minimumFreeBytes))
                return false;

            long growth = Math.Max(0L, estimatedWriteBytes - existingLength);
            if (!PendingWrites.TryAdd(target, new WriteReservation(root, existingLength, growth))) return false;
            ApproximateRootBytes[root] = SaturatingAdd(knownBytes, growth);
            return true;
        }
    }

    private static bool TryResolveKnownBytes(string root, out long knownBytes)
    {
        if (ApproximateRootBytes.TryGetValue(root, out knownBytes)) return true;
        if (LatestMaintenance.TryGetValue(root, out MaintenanceResult latest)
            && string.IsNullOrEmpty(latest.Error))
        {
            knownBytes = Math.Max(0L, latest.BytesAfter);
            ApproximateRootBytes[root] = knownBytes;
            return true;
        }

        knownBytes = 0L;
        if (!Directory.Exists(root))
        {
            ApproximateRootBytes[root] = 0L;
            return true;
        }
        try
        {
            foreach (string pattern in new[] { "*.bin", "*.tmp" })
                foreach (string path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                    knownBytes = SaturatingAdd(knownBytes, new FileInfo(path).Length);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        ApproximateRootBytes[root] = knownBytes;
        return true;
    }

    internal static bool FitsWithinBudget(
        long knownBytes,
        long budgetBytes,
        long existingFileBytes,
        long estimatedWriteBytes,
        long availableFreeBytes,
        long minimumFreeBytes)
    {
        if (knownBytes < 0L
            || budgetBytes <= 0L
            || existingFileBytes < 0L
            || estimatedWriteBytes < 0L
            || availableFreeBytes < 0L
            || minimumFreeBytes < 0L)
        {
            return false;
        }
        long growth = Math.Max(0L, estimatedWriteBytes - existingFileBytes);
        if (growth > budgetBytes || knownBytes > budgetBytes - growth) return false;
        // Cache replacements are written to a temporary file before the atomic
        // move, so free-space safety must reserve the whole outgoing payload,
        // even when its final net growth is zero.
        return estimatedWriteBytes <= availableFreeBytes
            && availableFreeBytes - estimatedWriteBytes >= minimumFreeBytes;
    }

    internal static bool CanWriteRegistered(string targetPath, long estimatedWriteBytes, long minimumFreeBytes = 2L * 1024L * 1024L * 1024L)
    {
        string root = ResolveBudgetRoot(targetPath);
        return !string.IsNullOrEmpty(root)
            && RegisteredBudgetBytes.TryGetValue(root, out long budgetBytes)
            && CanWrite(root, targetPath, budgetBytes, minimumFreeBytes, estimatedWriteBytes);
    }

    internal static string ResolveBudgetRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            DirectoryInfo? directory = new FileInfo(Path.GetFullPath(path)).Directory;
            while (directory != null)
            {
                if (directory.Name.Equals("lod_cache", StringComparison.OrdinalIgnoreCase)
                    || directory.Name.Equals("ring_cache", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeRoot(directory.FullName);
                }
                directory = directory.Parent;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return string.Empty;
    }

    internal static void RecordWrite(string rootDirectory, string path, long length)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(path) || length < 0L) return;
        string root = NormalizeRoot(rootDirectory);
        string target = NormalizePath(path);
        RecentAccessTicks[target] = DateTime.UtcNow.Ticks;
        object writeLock = RootWriteLocks.GetOrAdd(root, static _ => new object());
        lock (writeLock)
        {
            long current = ApproximateRootBytes.TryGetValue(root, out long approximate) ? approximate : 0L;
            if (PendingWrites.TryRemove(target, out WriteReservation reservation))
            {
                long withoutReservation = Math.Max(0L, current - reservation.ReservedGrowth);
                long actualGrowth = Math.Max(0L, length - reservation.ExistingLength);
                ApproximateRootBytes[root] = SaturatingAdd(withoutReservation, actualGrowth);
            }
            else
            {
                // A caller that predates reservations remains safe: over-counting
                // until the next exact maintenance scan is preferable to overflow.
                ApproximateRootBytes[root] = SaturatingAdd(current, length);
            }
        }
    }

    internal static void CancelWriteReservation(string rootDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(path)) return;
        string root = NormalizeRoot(rootDirectory);
        string target = NormalizePath(path);
        object writeLock = RootWriteLocks.GetOrAdd(root, static _ => new object());
        lock (writeLock)
        {
            if (!PendingWrites.TryRemove(target, out WriteReservation reservation)) return;
            long current = ApproximateRootBytes.TryGetValue(root, out long approximate) ? approximate : 0L;
            ApproximateRootBytes[root] = Math.Max(0L, current - reservation.ReservedGrowth);
        }
    }

    internal static void Schedule(
        string rootDirectory,
        long budgetBytes,
        int trimToPercent,
        TimeSpan inspectionInterval)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || budgetBytes < 0L) return;

        rootDirectory = NormalizeRoot(rootDirectory);
        RegisteredBudgetBytes.AddOrUpdate(rootDirectory, budgetBytes, (_, current) => Math.Min(current, budgetBytes));

        long now = DateTime.UtcNow.Ticks;
        long intervalTicks = Math.Max(TimeSpan.FromSeconds(10).Ticks, inspectionInterval.Ticks);
        long next = NextInspectionTicks.GetOrAdd(rootDirectory, 0L);
        if (now < next) return;
        if (!NextInspectionTicks.TryUpdate(rootDirectory, now + intervalTicks, next)) return;
        if (!ActiveMaintenance.TryAdd(rootDirectory, 0)) return;

        int targetPercent = Math.Clamp(trimToPercent, 25, 100);
        _ = Task.Run(() =>
        {
            object writeLock = RootWriteLocks.GetOrAdd(rootDirectory, static _ => new object());
            lock (writeLock)
            {
                try
                {
                    // Hold the same root gate used by reservations through both
                    // the exact scan and publication. Otherwise a completed write
                    // could land in the narrow gap and disappear from accounting.
                    Publish(Trim(rootDirectory, budgetBytes, targetPercent));
                }
                catch (Exception exception)
                {
                    Publish(new MaintenanceResult(
                        rootDirectory,
                        0L,
                        0L,
                        0,
                        exception.Message));
                }
                finally
                {
                    ActiveMaintenance.TryRemove(rootDirectory, out _);
                }
            }
        });
    }

    private static MaintenanceResult Trim(string rootDirectory, long budgetBytes, int targetPercent)
    {
        if (!Directory.Exists(rootDirectory))
            return new MaintenanceResult(rootDirectory, 0L, 0L, 0, string.Empty);

        var files = new List<CacheFile>();
        long totalBytes = 0L;
        foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.bin", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(path);
                long length = info.Length;
                long accessTicks = RecentAccessTicks.TryGetValue(NormalizePath(path), out long recentTicks)
                    ? recentTicks
                    : info.LastWriteTimeUtc.Ticks;
                // A replacement writer reserved against this file's current
                // length. Do not evict it underneath that accounting contract.
                if (!PendingWrites.ContainsKey(NormalizePath(path)))
                    files.Add(new CacheFile(path, length, accessTicks));
                totalBytes = SaturatingAdd(totalBytes, length);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        long staleTemporaryCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(30)).Ticks;
        int deleted = 0;
        long staleTemporaryBytesDeleted = 0L;
        foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.tmp", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(path);
                long length = info.Length;
                totalBytes = SaturatingAdd(totalBytes, length);
                if (info.LastWriteTimeUtc.Ticks > staleTemporaryCutoff) continue;
                File.Delete(path);
                totalBytes = Math.Max(0L, totalBytes - length);
                staleTemporaryBytesDeleted = SaturatingAdd(staleTemporaryBytesDeleted, length);
                RecentAccessTicks.TryRemove(path, out _);
                deleted++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        long bytesBefore = SaturatingAdd(totalBytes, staleTemporaryBytesDeleted);
        if (totalBytes <= budgetBytes)
            return new MaintenanceResult(rootDirectory, bytesBefore, totalBytes, deleted, string.Empty);

        long targetBytes = Math.Max(0L, budgetBytes * targetPercent / 100L);
        files.Sort((left, right) => left.AccessTicks.CompareTo(right.AccessTicks));
        foreach (CacheFile file in files)
        {
            if (totalBytes <= targetBytes) break;
            try
            {
                File.Delete(file.Path);
                totalBytes = Math.Max(0L, totalBytes - file.Length);
                RecentAccessTicks.TryRemove(file.Path, out _);
                deleted++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new MaintenanceResult(rootDirectory, bytesBefore, totalBytes, deleted, string.Empty);
    }

    private static void Publish(MaintenanceResult result)
    {
        string root = NormalizeRoot(result.RootDirectory);
        MaintenanceResult normalized = result with { RootDirectory = root };
        LatestMaintenance[root] = normalized;
        if (!string.IsNullOrEmpty(normalized.Error))
        {
            // A failed scan must not replace a conservative known total with
            // zero; writers will retry measurement and otherwise fail closed.
            CompletedMaintenance.Enqueue(normalized);
            return;
        }
        object writeLock = RootWriteLocks.GetOrAdd(root, static _ => new object());
        lock (writeLock)
        {
            long pendingGrowth = 0L;
            foreach (WriteReservation reservation in PendingWrites.Values)
                if (reservation.RootDirectory.Equals(root, StringComparison.OrdinalIgnoreCase))
                    pendingGrowth = SaturatingAdd(pendingGrowth, reservation.ReservedGrowth);
            ApproximateRootBytes[root] = SaturatingAdd(Math.Max(0L, normalized.BytesAfter), pendingGrowth);
        }
        CompletedMaintenance.Enqueue(normalized);
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)); }
        catch (Exception) when (rootDirectory.Length > 0) { return Path.TrimEndingDirectorySeparator(rootDirectory); }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) when (path.Length > 0) { return path; }
    }

    private static long SaturatingAdd(long left, long right)
        => left >= long.MaxValue - right ? long.MaxValue : left + right;
}
