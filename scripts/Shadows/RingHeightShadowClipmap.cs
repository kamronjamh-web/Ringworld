using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Shadows;

public readonly record struct HeightShadowFillReport(long Texels, long ApertureTexels, double Seconds, int Rects, int Workers);

/// <summary>One clipmap level: CPU master heights in toroidal slot order (index = slotJ * N + slotI) and the window they currently describe.</summary>
public sealed class RingHeightShadowLevel
{
    public int TexelMeters { get; internal set; }
    public float[] Heights { get; } = new float[RingHeightShadowLattice.TextureSize * RingHeightShadowLattice.TextureSize];
    /// <summary>Window the master describes, or null before the first fill / after a reset. Updated only after a fill completes.</summary>
    public HeightShadowWindow? Window { get; internal set; }
    public long TexelsFilled { get; internal set; }
}

/// <summary>
/// CPU side of the S4 height-shadow clipmap (two levels, t0 and t1 = 4 * t0): worker fill of the master arrays, recentre strips, bore aperture carving.
/// Pure C# over <see cref="IHeightShadowSource"/>; the GPU upload lives in <see cref="RingTerrainHeightShadowGpu"/>. All fill methods block until done and are
/// meant to run on a worker thread; one fill at a time per clipmap. The master is only written inside the rects being filled (never a refill of the overlap).
/// </summary>
public sealed class RingHeightShadowClipmap
{
    public const int LevelCount = 2;
    public const int TileSize = 64;
    public long Circumference { get; }
    public int Width { get; }
    public int Level0TexelMeters { get; private set; }
    public RingHeightShadowLevel[] Levels { get; } = { new(), new() };
    /// <summary>Priority the fill workers run at (S4c: below normal so a full fill never competes with the frame; tests may leave it).</summary>
    public ThreadPriority WorkerPriority { get; set; } = ThreadPriority.Normal;

    public RingHeightShadowClipmap(long circumference, int width, int level0TexelMeters)
    {
        Circumference = circumference; Width = width;
        Reset(level0TexelMeters);
    }

    /// <summary>Changes the texel size (altitude band change or setting change); all levels lose their window and need a full fill.</summary>
    public void Reset(int level0TexelMeters)
    {
        RingHeightShadowLattice.AssertConfiguration(Circumference, Width, level0TexelMeters);
        Level0TexelMeters = level0TexelMeters;
        for (int l = 0; l < LevelCount; l++)
        {
            Levels[l].TexelMeters = l == 0 ? level0TexelMeters : RingHeightShadowLattice.Level1Texel(level0TexelMeters);
            Levels[l].Window = null;
        }
    }

    /// <summary>Window of <paramref name="level"/> centred on an unwrapped around / across position (J clamped between the walls).</summary>
    public HeightShadowWindow WindowAt(int level, double unwrappedAroundMeters, double acrossMeters)
    {
        int t = Levels[level].TexelMeters;
        (long ic, long jc) = RingHeightShadowLattice.CentreIndices(unwrappedAroundMeters, acrossMeters, t);
        return new HeightShadowWindow(ic, RingHeightShadowLattice.ClampJc(jc, t, Width));
    }

    /// <summary>Fills (or refills) a level for <paramref name="window"/>: only texels not already described by the level's current window are sampled.</summary>
    public HeightShadowFillReport FillLevel(int level, HeightShadowWindow window, IHeightShadowSource[] sources, CancellationToken ct, List<HeightShadowIndexRect>? filled = null)
    {
        RingHeightShadowLevel state = Levels[level];
        var rects = new List<HeightShadowIndexRect>(2);
        RingHeightShadowLattice.StripRects(state.Window, window, rects);
        HeightShadowFillReport report = FillRects(level, rects, window, sources, ct);
        state.Window = window; // only reached when the fill completed (FillRects throws on cancellation)
        filled?.AddRange(rects);
        return report;
    }

    /// <summary>Recentres a level when the target moved more than N/8; the report is empty when it did not.</summary>
    public HeightShadowFillReport Recentre(int level, long wantIc, long wantJc, IHeightShadowSource[] sources, CancellationToken ct, out bool moved, List<HeightShadowIndexRect>? filled = null)
    {
        RingHeightShadowLevel state = Levels[level];
        if (state.Window is not { } current)
        {
            moved = true;
            return FillLevel(level, new HeightShadowWindow(wantIc, RingHeightShadowLattice.ClampJc(wantJc, state.TexelMeters, Width)), sources, ct, filled);
        }
        HeightShadowWindow next = RingHeightShadowLattice.NextWindow(current, wantIc, wantJc, state.TexelMeters, Width, out moved);
        return moved ? FillLevel(level, next, sources, ct, filled) : default;
    }

    /// <summary>Samples the given unwrapped-index rects into the level's master (centre-out tile order around <paramref name="centre"/>), then carves bore apertures.</summary>
    public HeightShadowFillReport FillRects(int level, IReadOnlyList<HeightShadowIndexRect> rects, HeightShadowWindow centre, IHeightShadowSource[] sources, CancellationToken ct)
    {
        if (sources.Length == 0) throw new ArgumentException("at least one height source is required", nameof(sources));
        foreach (IHeightShadowSource source in sources)
            if (source.CircumferenceMeters != Circumference || source.WidthMeters != Width) throw new ArgumentException("height source geometry does not match the clipmap");
        var watch = Stopwatch.StartNew();
        RingHeightShadowLevel state = Levels[level];
        var tiles = new List<(HeightShadowIndexRect Rect, long Key)>();
        long total = 0;
        foreach (HeightShadowIndexRect rect in rects)
        {
            total += rect.Count;
            for (long j = rect.J0; j < rect.J1; j += TileSize)
                for (long i = rect.I0; i < rect.I1; i += TileSize)
                {
                    var tile = new HeightShadowIndexRect(i, j, (int)Math.Min(TileSize, rect.I1 - i), (int)Math.Min(TileSize, rect.J1 - j));
                    long di = Math.Abs(i + tile.Width / 2 - centre.Ic), dj = Math.Abs(j + tile.Height / 2 - centre.Jc);
                    tiles.Add((tile, Math.Max(di, dj)));
                }
        }
        tiles.Sort((a, b) => a.Key.CompareTo(b.Key));
        int next = -1;
        void Work(IHeightShadowSource source)
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority original = thread.Priority;
            bool lowered = WorkerPriority != original;
            if (lowered) { try { thread.Priority = WorkerPriority; } catch (Exception) { lowered = false; } }
            try
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= tiles.Count) return;
                    ct.ThrowIfCancellationRequested();
                    FillTile(state, tiles[index].Rect, source);
                }
            }
            finally { if (lowered) { try { thread.Priority = original; } catch (Exception) { } } }
        }
        if (sources.Length == 1 || tiles.Count < 2) Work(sources[0]);
        else
        {
            var tasks = new Task[sources.Length];
            for (int w = 0; w < tasks.Length; w++) { IHeightShadowSource source = sources[w]; tasks[w] = Task.Run(() => Work(source), CancellationToken.None); }
            try { Task.WaitAll(tasks); }
            catch (AggregateException error) { throw error.InnerExceptions.Count > 0 ? error.InnerExceptions[0] : error; }
        }
        long carved = 0;
        foreach (HeightShadowIndexRect rect in rects) carved += CarveApertures(state, rect, sources[0].EmitterSites);
        state.TexelsFilled += total;
        return new HeightShadowFillReport(total, carved, watch.Elapsed.TotalSeconds, rects.Count, sources.Length);
    }

    private void FillTile(RingHeightShadowLevel state, HeightShadowIndexRect tile, IHeightShadowSource source)
    {
        int t = state.TexelMeters, n = RingHeightShadowLattice.TextureSize;
        for (long j = tile.J0; j < tile.J1; j++)
        {
            int across = RingHeightShadowLattice.ClampAcrossMeters(j, t, Width);
            int row = RingHeightShadowLattice.SlotOf(j) * n;
            for (long i = tile.I0; i < tile.I1; i++)
                state.Heights[row + RingHeightShadowLattice.SlotOf(i)] = source.GetHeight(RingHeightShadowLattice.WrapAroundMeters(i, t, Circumference), across);
        }
    }

    /// <summary>
    /// Texels whose centre lies strictly inside a site's bore liner radius get the iris-rim height PadHeight - PlugDepthM, so the liner shadows like a cliff
    /// (matches EmitterSiteApertures.TryGetBoreFloorAt). Returns the number of carved texels.
    /// </summary>
    public long CarveApertures(RingHeightShadowLevel state, HeightShadowIndexRect rect, EmitterSiteRegistry? registry)
    {
        if (registry == null) return 0;
        int t = state.TexelMeters, n = RingHeightShadowLattice.TextureSize;
        double centreUnwrapped = (rect.I0 + rect.Width * 0.5) * t;
        long centreWrapped = RingHeightShadowLattice.PosMod((long)Math.Floor(centreUnwrapped), Circumference);
        double centreAcross = (rect.J0 + rect.Height * 0.5) * t;
        double radius = 0.5 * Math.Sqrt(((double)rect.Width * t) * ((double)rect.Width * t) + ((double)rect.Height * t) * ((double)rect.Height * t)) + t;
        var apertures = new SurfaceAperture[64];
        int found = EmitterSiteApertures.GetSurfaceApertures(registry, centreWrapped, centreAcross, radius, apertures);
        if (found > apertures.Length) { apertures = new SurfaceAperture[found]; found = EmitterSiteApertures.GetSurfaceApertures(registry, centreWrapped, centreAcross, radius, apertures); }
        long carved = 0;
        long half = Circumference >> 1;
        for (int a = 0; a < found; a++)
        {
            SurfaceAperture aperture = apertures[a];
            float liner = EmitterSiteApertures.LinerRadius(aperture.Kind);
            long d = RingHeightShadowLattice.PosMod(aperture.Around - centreWrapped, Circumference);
            if (d > half) d -= Circumference;
            double siteUnwrapped = Math.Floor(centreUnwrapped) + d; // site around nearest to the rect centre, same unwrapped frame as I * t
            long i0 = Math.Max(rect.I0, (long)Math.Floor((siteUnwrapped - liner) / t)), i1 = Math.Min(rect.I1 - 1, (long)Math.Ceiling((siteUnwrapped + liner) / t));
            long j0 = Math.Max(rect.J0, (long)Math.Floor((aperture.Across - liner) / t)), j1 = Math.Min(rect.J1 - 1, (long)Math.Ceiling((aperture.Across + liner) / t));
            float rim = aperture.SurfaceHeight - EmitterSiteApertures.PlugDepthM;
            double r2 = (double)liner * liner;
            for (long j = j0; j <= j1; j++)
            {
                double dc = RingHeightShadowLattice.ClampAcrossMeters(j, t, Width) - (double)aperture.Across;
                int row = RingHeightShadowLattice.SlotOf(j) * n;
                for (long i = i0; i <= i1; i++)
                {
                    double da = i * (double)t - siteUnwrapped;
                    if (da * da + dc * dc >= r2) continue;
                    state.Heights[row + RingHeightShadowLattice.SlotOf(i)] = rim;
                    carved++;
                }
            }
        }
        return carved;
    }

    /// <summary>Copies a slot rect of the master into <paramref name="destination"/> (row-major Width x Height), the staging payload of one GPU scatter.</summary>
    public void CopySlotRect(int level, HeightShadowSlotRect rect, Span<float> destination)
    {
        float[] master = Levels[level].Heights;
        int n = RingHeightShadowLattice.TextureSize;
        for (int y = 0; y < rect.Height; y++)
            master.AsSpan((rect.Y + y) * n + rect.X, rect.Width).CopyTo(destination.Slice(y * rect.Width, rect.Width));
    }
}
