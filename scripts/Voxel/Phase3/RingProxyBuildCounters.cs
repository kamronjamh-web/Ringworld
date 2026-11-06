using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Worker-side counters for the proxy sector build (gpu-offload WO-A0): sectors built, lattice points fetched, and the
/// terrain-model and climate evaluations behind them, per level (slot = log2 of the sample spacing in metres).
/// Interlocked, no allocation on the hot path; read by the performance export. Evaluations are attributed to a level only
/// while a worker thread is inside <see cref="Enter"/>, so unrelated sampler users (grass, trees, gameplay) are not counted.
/// </summary>
internal static class RingProxyBuildCounters
{
    public const int LevelSlots = 16;
    [ThreadStatic] private static int _activeSlot; // slot + 1; 0 = not inside a proxy sector build
    // Per-thread accumulators for the per-point counters; flushed with one Interlocked op each in Scope.Dispose.
    [ThreadStatic] private static long _pendingPoints;
    [ThreadStatic] private static long _pendingHeightOnly;
    [ThreadStatic] private static long _pendingModel;
    [ThreadStatic] private static long _pendingClimate;

    private static readonly long[] Sectors = new long[LevelSlots];
    private static readonly long[] Points = new long[LevelSlots];
    private static readonly long[] HeightOnlyPoints = new long[LevelSlots];
    private static readonly long[] ModelEvaluations = new long[LevelSlots];
    private static readonly long[] ClimateEvaluations = new long[LevelSlots];
    private static readonly long[] Installed = new long[LevelSlots];
    private static readonly long[] DroppedNotDesired = new long[LevelSlots];
    private static readonly long[] DroppedStale = new long[LevelSlots];
    private static long _shorelineRebuilds;
    private static long _shorelineSkips;
    // Install path (gpu-offload WO-S1): ArrayMesh surfaces created by sector installs, and how each install was carried.
    private static long _installSurfaceUploads;
    private static long _installSlotSectors;
    private static long _installMeshSectors;
    private static long _slotFallbacks; // slot-capable sector installed through a mesh (pool exhausted or layout changed)
    private static readonly long[] _slotOceanKinds = new long[3]; // dry, shared water grid, compact coastal mesh
    private static readonly long[] _slotPeakUsed = new long[LevelSlots]; // peak slots in use per level (high-water mark since process start)
    // GPU height backend (WO-G2): batches and sectors dispatched, sectors installed by class, coastal round trips to the CPU.
    private static readonly long[] GpuBatches = new long[LevelSlots];
    private static readonly long[] GpuSectorsDispatched = new long[LevelSlots];
    private static readonly long[] GpuInstalledDry = new long[LevelSlots];
    private static readonly long[] GpuInstalledOcean = new long[LevelSlots];
    private static readonly long[] GpuToCpu = new long[LevelSlots];
    private static readonly long[] GpuEditedToCpu = new long[LevelSlots];
    private static long _windowStart = Stopwatch.GetTimestamp();

    public readonly struct Scope : IDisposable
    {
        private readonly int _previous;
        public Scope(int previous) => _previous = previous;
        public void Dispose()
        {
            int slot = _activeSlot;
            if (slot > 0) Flush(slot - 1);
            _activeSlot = _previous;
        }
    }

    /// <summary>Marks the calling thread as building one sector at the given sample spacing and counts the sector.</summary>
    public static Scope Enter(int spacingMeters)
    {
        int slot = 0;
        for (int s = Math.Max(1, spacingMeters); s > 1 && slot < LevelSlots - 1; s >>= 1) slot++;
        int previous = _activeSlot;
        _activeSlot = slot + 1;
        Interlocked.Increment(ref Sectors[slot]);
        return new Scope(previous);
    }

    private static void Flush(int slot)
    {
        if (_pendingPoints != 0) { Interlocked.Add(ref Points[slot], _pendingPoints); _pendingPoints = 0; }
        if (_pendingHeightOnly != 0) { Interlocked.Add(ref HeightOnlyPoints[slot], _pendingHeightOnly); _pendingHeightOnly = 0; }
        if (_pendingModel != 0) { Interlocked.Add(ref ModelEvaluations[slot], _pendingModel); _pendingModel = 0; }
        if (_pendingClimate != 0) { Interlocked.Add(ref ClimateEvaluations[slot], _pendingClimate); _pendingClimate = 0; }
    }

    public static void AddModelEvaluations(int count)
    {
        if (_activeSlot > 0) _pendingModel += count;
    }

    public static void AddClimateEvaluation()
    {
        if (_activeSlot > 0) _pendingClimate++;
    }

    public static void AddPoint(bool heightOnly)
    {
        if (_activeSlot <= 0) return;
        if (heightOnly) _pendingHeightOnly++;
        else _pendingPoints++;
    }

    private static int SlotFor(int spacingMeters)
    {
        int slot = 0;
        for (int s = Math.Max(1, spacingMeters); s > 1 && slot < LevelSlots - 1; s >>= 1) slot++;
        return slot;
    }

    /// <summary>Main thread: a finished sector was installed at the given sample spacing.</summary>
    public static void AddInstalled(int spacingMeters) => Interlocked.Increment(ref Installed[SlotFor(spacingMeters)]);

    /// <summary>Main thread: a finished sector was discarded because it was no longer desired.</summary>
    public static void AddDroppedNotDesired(int spacingMeters) => Interlocked.Increment(ref DroppedNotDesired[SlotFor(spacingMeters)]);

    /// <summary>Main thread: a finished sector was discarded because its request or edit version was stale.</summary>
    public static void AddDroppedStale(int spacingMeters) => Interlocked.Increment(ref DroppedStale[SlotFor(spacingMeters)]);

    /// <summary>Main thread: one sector install, counting the AddSurfaceFromArrays calls it made (0 for a dry slot sector).</summary>
    public static void AddSlotFallback() => Interlocked.Increment(ref _slotFallbacks);

    public static void AddSlotOceanKind(int kind) => Interlocked.Increment(ref _slotOceanKinds[kind]);

    /// <summary>Main thread: records the slots in use after an install, as a per-level high-water mark.</summary>
    public static void NoteSlotsInUse(int spacingMeters, int used)
    {
        int slot = SlotFor(spacingMeters);
        if (used > Interlocked.Read(ref _slotPeakUsed[slot])) Interlocked.Exchange(ref _slotPeakUsed[slot], used);
    }

    public static void AddInstallPath(bool slot, int surfaceUploads)
    {
        if (slot) Interlocked.Increment(ref _installSlotSectors); else Interlocked.Increment(ref _installMeshSectors);
        if (surfaceUploads > 0) Interlocked.Add(ref _installSurfaceUploads, surfaceUploads);
    }

    /// <summary>Main thread: one GPU batch of <paramref name="sectors"/> sectors was recorded for the level at this spacing.</summary>
    public static void AddGpuBatch(int spacingMeters, int sectors)
    {
        int slot = SlotFor(spacingMeters);
        Interlocked.Increment(ref GpuBatches[slot]);
        Interlocked.Add(ref GpuSectorsDispatched[slot], sectors);
    }

    public static void AddGpuSector(int spacingMeters, bool ocean)
    {
        int slot = SlotFor(spacingMeters);
        if (ocean) Interlocked.Increment(ref GpuInstalledOcean[slot]); else Interlocked.Increment(ref GpuInstalledDry[slot]);
    }

    public static void AddGpuToCpu(int spacingMeters) => Interlocked.Increment(ref GpuToCpu[SlotFor(spacingMeters)]);
    /// <summary>A GPU-level sector kept on the CPU because an edit (or emitter site) touches it; counted once per routing pass it is seen in.</summary>
    public static void AddGpuEditedToCpu(int spacingMeters) => Interlocked.Increment(ref GpuEditedToCpu[SlotFor(spacingMeters)]);

    /// <summary>Total GPU batches (compute dispatch groups recorded) since the last reset: the "stationary adds 0 dispatches" check.</summary>
    public static long GpuBatchTotal()
    {
        long total = 0;
        for (int i = 0; i < LevelSlots; i++) total += Interlocked.Read(ref GpuBatches[i]);
        return total;
    }

    public static void AddShorelineRebuild() => Interlocked.Increment(ref _shorelineRebuilds);

    public static void AddShorelineSkip() => Interlocked.Increment(ref _shorelineSkips);

    public static void Reset()
    {
        for (int i = 0; i < LevelSlots; i++)
        {
            Interlocked.Exchange(ref Sectors[i], 0);
            Interlocked.Exchange(ref Points[i], 0);
            Interlocked.Exchange(ref HeightOnlyPoints[i], 0);
            Interlocked.Exchange(ref ModelEvaluations[i], 0);
            Interlocked.Exchange(ref ClimateEvaluations[i], 0);
            Interlocked.Exchange(ref Installed[i], 0);
            Interlocked.Exchange(ref DroppedNotDesired[i], 0);
            Interlocked.Exchange(ref DroppedStale[i], 0);
            Interlocked.Exchange(ref GpuBatches[i], 0);
            Interlocked.Exchange(ref GpuSectorsDispatched[i], 0);
            Interlocked.Exchange(ref GpuInstalledDry[i], 0);
            Interlocked.Exchange(ref GpuInstalledOcean[i], 0);
            Interlocked.Exchange(ref GpuToCpu[i], 0);
            Interlocked.Exchange(ref GpuEditedToCpu[i], 0);
        }
        Interlocked.Exchange(ref _shorelineRebuilds, 0);
        Interlocked.Exchange(ref _shorelineSkips, 0);
        Interlocked.Exchange(ref _installSurfaceUploads, 0);
        Interlocked.Exchange(ref _installSlotSectors, 0);
        Interlocked.Exchange(ref _installMeshSectors, 0);
        Interlocked.Exchange(ref _slotFallbacks, 0);
        for (int i = 0; i < _slotOceanKinds.Length; i++) Interlocked.Exchange(ref _slotOceanKinds[i], 0);
        Interlocked.Exchange(ref _windowStart, Stopwatch.GetTimestamp());
    }

    /// <summary>JSON-serialisable view of the counters since the last <see cref="Reset"/>.</summary>
    public static object Snapshot()
    {
        double seconds = Math.Max(1e-3, (Stopwatch.GetTimestamp() - Interlocked.Read(ref _windowStart)) / (double)Stopwatch.Frequency);
        var levels = new List<object>();
        long totalSectors = 0, totalPoints = 0, totalHeightOnly = 0, totalModel = 0, totalClimate = 0;
        long totalInstalled = 0, totalNotDesired = 0, totalStale = 0;
        for (int i = 0; i < LevelSlots; i++)
        {
            long sectors = Interlocked.Read(ref Sectors[i]);
            long points = Interlocked.Read(ref Points[i]);
            long heightOnly = Interlocked.Read(ref HeightOnlyPoints[i]);
            long model = Interlocked.Read(ref ModelEvaluations[i]);
            long climate = Interlocked.Read(ref ClimateEvaluations[i]);
            long installed = Interlocked.Read(ref Installed[i]);
            long notDesired = Interlocked.Read(ref DroppedNotDesired[i]);
            long stale = Interlocked.Read(ref DroppedStale[i]);
            if (sectors == 0 && points == 0 && heightOnly == 0 && model == 0 && climate == 0
                && installed == 0 && notDesired == 0 && stale == 0) continue;
            totalSectors += sectors; totalPoints += points; totalHeightOnly += heightOnly; totalModel += model; totalClimate += climate;
            totalInstalled += installed; totalNotDesired += notDesired; totalStale += stale;
            levels.Add(Row("spacing_" + (1 << i) + "m", sectors, points, heightOnly, model, climate, installed, notDesired, stale, seconds));
        }
        return new
        {
            windowSeconds = seconds,
            total = Row("all", totalSectors, totalPoints, totalHeightOnly, totalModel, totalClimate,
                totalInstalled, totalNotDesired, totalStale, seconds),
            levels,
            installSurfaceUploads = Interlocked.Read(ref _installSurfaceUploads),
            installSlotSectors = Interlocked.Read(ref _installSlotSectors),
            installMeshSectors = Interlocked.Read(ref _installMeshSectors),
            slotFallbackInstalls = Interlocked.Read(ref _slotFallbacks),
            slotPeakUsedBySpacing = SlotPeakSnapshot(),
            gpuHeights = GpuSnapshot(),
            slotSectorsDry = Interlocked.Read(ref _slotOceanKinds[0]),
            slotSectorsWaterGrid = Interlocked.Read(ref _slotOceanKinds[1]),
            slotSectorsCompactOcean = Interlocked.Read(ref _slotOceanKinds[2]),
            vertexTableReservedBytes = global::RingworldTechDemo.Voxel.LOD.RingTerrainProxySector.VertexTableReservedBytes,
            shorelineFieldRebuilds = Interlocked.Read(ref _shorelineRebuilds),
            shorelineFieldSkippedRebuilds = Interlocked.Read(ref _shorelineSkips),
            shorelineFieldRebuildsPerSecond = Interlocked.Read(ref _shorelineRebuilds) / seconds,
        };
    }

    private static object GpuSnapshot()
    {
        var levels = new List<object>();
        long batches = 0, sectors = 0, dry = 0, ocean = 0, toCpu = 0, editedToCpu = 0;
        for (int i = 0; i < LevelSlots; i++)
        {
            long b = Interlocked.Read(ref GpuBatches[i]), s = Interlocked.Read(ref GpuSectorsDispatched[i]);
            long d = Interlocked.Read(ref GpuInstalledDry[i]), o = Interlocked.Read(ref GpuInstalledOcean[i]), c = Interlocked.Read(ref GpuToCpu[i]), e = Interlocked.Read(ref GpuEditedToCpu[i]);
            if (b == 0 && s == 0 && d == 0 && o == 0 && c == 0 && e == 0) continue;
            batches += b; sectors += s; dry += d; ocean += o; toCpu += c; editedToCpu += e;
            levels.Add(new { level = "spacing_" + (1 << i) + "m", batches = b, sectorsDispatched = s, installedDry = d, installedOcean = o, routedToCpuCoastal = c, routedToCpuEdited = e });
        }
        return new { batches, sectorsDispatched = sectors, installedDry = dry, installedOcean = ocean, routedToCpuCoastal = toCpu, routedToCpuEdited = editedToCpu, levels };
    }

    private static Dictionary<string, long> SlotPeakSnapshot()
    {
        var peaks = new Dictionary<string, long>();
        for (int i = 0; i < LevelSlots; i++)
        {
            long peak = Interlocked.Read(ref _slotPeakUsed[i]);
            if (peak > 0) peaks["spacing_" + (1 << i) + "m"] = peak;
        }
        return peaks;
    }

    private static object Row(string name, long sectors, long points, long heightOnly, long model, long climate,
        long installed, long droppedNotDesired, long droppedStale, double seconds)
    {
        long lattice = points + heightOnly;
        return new
        {
            level = name,
            sectors,
            sectorsPerSecond = sectors / seconds,
            latticePoints = points,
            heightOnlyPoints = heightOnly,
            modelEvaluations = model,
            climateEvaluations = climate,
            sectorsInstalled = installed,
            sectorsDroppedNotDesired = droppedNotDesired,
            sectorsDroppedStale = droppedStale,
            modelEvaluationsPerPoint = lattice > 0 ? model / (double)lattice : 0.0,
            climateEvaluationsPerPoint = lattice > 0 ? climate / (double)lattice : 0.0,
        };
    }
}
