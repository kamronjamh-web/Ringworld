using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Hydrology;

public sealed record DrainageBakeOptions
{
    /// <summary>Amplitude of the routing noise (D4). 0 turns it off.</summary>
    public double RoutingNoiseMeters { get; init; } = DrainageBake.DefaultRoutingNoiseMeters;
    /// <summary>The recipe's <c>lake_abundance</c> (null reads it; 0.5 without the rivers feature). It sets the evaporation scale of the water balance, lambda = 4^(2 la - 1) (S8b); breaching does not read it.</summary>
    public double? LakeAbundance { get; init; }
    /// <summary>Multiplies E0. 1 in production; only the report tool's sweep changes it.</summary>
    public double EvaporationScale { get; init; } = 1d;
    /// <summary>T2R-19, T2R-23 (RS2): the edge cells of every site keep-out are outlets (<see cref="DrainageCellKind.Intake"/>): water that reaches a site leaves the map there. On in production; the report tool can switch it off for the "before" measurement.</summary>
    public bool SiteIntakes { get; init; } = true;
    /// <summary>T2R-19: the keep-out beyond a structure's influence radius, in metres (null is <see cref="DrainageBarriers.DefaultKeepOutMeters"/>, 300 m).</summary>
    public double? KeepOutMeters { get; init; }
}

/// <summary>
/// The immutable result of a drainage bake (docs/t2-river-reset.md 4.2). Arrays are filled once by the bake and never written again; consumers read them
/// through spans. Cell index = row * Width + column; <see cref="Spec"/> maps columns to ring positions (null for synthetic fixtures).
/// </summary>
public sealed class DrainageField
{
    private readonly DrainageDomain _domain;
    private readonly DrainageConditioned _conditioned;
    private readonly DrainageRouted _routed;

    internal DrainageField(DrainageGridSpec? spec, DrainageDomain domain, DrainageConditioned conditioned, DrainageRouted routed, DrainageBakeReport report)
    {
        Spec = spec; _domain = domain; _conditioned = conditioned; _routed = routed; Report = report;
    }

    public DrainageGridSpec? Spec { get; }
    public DrainageBakeReport Report { get; }
    public int Width => _domain.Width;
    public int Height => _domain.Height;
    public bool Wrap => _domain.Wrap;
    public int Count => _domain.Count;
    public double CellAreaKm2 => _domain.CellAreaKm2;
    public double AroundStepMeters => _domain.AroundStepMeters;
    public double AcrossStepMeters => _domain.AcrossStepMeters;
    public ReadOnlySpan<DrainageCellKind> Kind => _domain.Kind;
    /// <summary>Unperturbed valley-floor height per cell (the lowest sample).</summary>
    public ReadOnlySpan<float> Lowest => _domain.Lowest;
    public ReadOnlySpan<float> Moisture => _domain.Moisture;
    public ReadOnlySpan<float> RouteHeight => _domain.Route;
    public ReadOnlySpan<float> CutRouteHeight => _conditioned.CutRoute;
    public ReadOnlySpan<float> FilledHeight => _conditioned.Filled;
    public ReadOnlySpan<bool> Visited => _conditioned.Visited;
    public ReadOnlySpan<int> FloodOrder => _conditioned.FloodOrder;
    public ReadOnlySpan<int> Down => _routed.Down;
    public ReadOnlySpan<int> AreaCells => _routed.AreaCells;
    /// <summary>F (S6): the unperturbed filled surface.</summary>
    public ReadOnlySpan<float> UnperturbedFilled => _conditioned.UnperturbedFilled;
    /// <summary>r (S6): per-cell runoff, A_cell * max(0.1, m / 0.5).</summary>
    public ReadOnlySpan<double> Runoff => _routed.Runoff;
    /// <summary>R (S6): accumulated runoff.</summary>
    public ReadOnlySpan<double> RunoffAccumulation => _routed.RunoffAccumulation;
    public ReadOnlySpan<DrainageCellKind> Terminal => _routed.Terminal;
    /// <summary>Final lake per cell (S8b): spilling basins as before, closed basins only on their wet cells.</summary>
    public ReadOnlySpan<int> LakeId => _routed.LakeId;
    public ReadOnlyMemory<DrainageLake> Lakes => _routed.Lakes;
    /// <summary>The conditioned basin (a depression at least 2 m deep) of each cell, or -1.</summary>
    public ReadOnlySpan<int> BasinId => _conditioned.LakeId;
    /// <summary>One entry per basin, indexed by <see cref="BasinId"/>.</summary>
    public ReadOnlyMemory<DrainageBasinInfo> Basins => _routed.Basins;
    public DrainageRouted Routed => _routed;
    public DrainageConditioned Conditioned => _conditioned;
    public ReadOnlyMemory<DrainageBreach> Cuts => _conditioned.Cuts;
    public ulong DownHash => _routed.DownHash;
    public DrainageDomain Domain => _domain;

    /// <summary>True for a cell that shows lake water: in a lake's flooded set and its lowest sample below the lake level.</summary>
    public bool IsLakeWater(int cell)
    {
        int lake = _routed.LakeId[cell];
        return lake >= 0 && _conditioned.Effective[cell] < _routed.Lakes[lake].Level;
    }

    /// <summary>True for a cell of a closed basin that is not wet: salt flat or dry lakebed (S8b).</summary>
    public bool IsDryFloor(int cell)
    {
        int basin = _conditioned.LakeId[cell];
        return basin >= 0 && _routed.Basins[basin].Closed && _routed.LakeId[cell] < 0;
    }
}

/// <summary>D2-D4 on a <see cref="DrainageGrid"/>: barriers, routing noise, conditioning, routing and accumulation (docs/t2-river-reset.md 4.2).</summary>
public static class DrainageBake
{
    public const double DefaultRoutingNoiseMeters = 6d;
    public const double RoutingNoiseWavelengthMeters = 5000d;

    public static DrainageField Bake(DrainageGrid grid, TerrainGenerationParameters parameters, int workers, IProgress<double>? progress,
        CancellationToken cancellation, DrainageBakeOptions? options = null)
    {
        options ??= new DrainageBakeOptions();
        workers = Math.Max(1, workers);
        double lakeAbundance = options.LakeAbundance ?? (parameters.HasRivers ? parameters.GetExtensionField("lake_abundance") : 0.5d);
        DrainageGridSpec spec = grid.Spec;
        var clock = Stopwatch.StartNew();
        DrainageCellKind[] kind = DrainageBarriers.Rasterise(grid, parameters, out int barrierCells, out _, options.SiteIntakes, options.KeepOutMeters);
        double barrierSeconds = clock.Elapsed.TotalSeconds;
        progress?.Report(0.1);
        cancellation.ThrowIfCancellationRequested();

        clock.Restart();
        float[] route = BuildRoute(grid, kind, parameters.Seed, options.RoutingNoiseMeters, workers);
        double noiseSeconds = clock.Elapsed.TotalSeconds;
        var domain = new DrainageDomain(spec.Columns, spec.Rows, spec.IsWholeRing, spec.AroundStepMeters, spec.AcrossStepMeters, kind, grid.Lowest, route, grid.Moisture);
        return BakeDomain(spec, domain, lakeAbundance, workers, progress, cancellation, barrierSeconds, noiseSeconds, grid.Hash(), null, options.EvaporationScale);
    }

    /// <summary>Routing, accumulation and the water balance again on an existing conditioning (the report tool's sweep): breaching does not depend on lake_abundance, so nothing else changes.</summary>
    public static DrainageField Reroute(DrainageField field, double lakeAbundance, double evaporationScale, int workers)
    {
        var clock = Stopwatch.StartNew();
        DrainageRouted routed = DrainageRouting.Route(field.Domain, field.Conditioned, workers, DrainageBalanceParameters.From(lakeAbundance, evaporationScale));
        double routingSeconds = clock.Elapsed.TotalSeconds;
        DrainageBakeReport report = DrainageBakeReport.Build(field.Domain, field.Conditioned, routed, lakeAbundance, 0, 0, 0, 0, routingSeconds, evaporationScale);
        return new DrainageField(field.Spec, field.Domain, field.Conditioned, routed, report);
    }

    /// <summary>Conditioning and routing on a prepared domain (synthetic fixtures call this directly).</summary>
    public static DrainageField BakeDomain(DrainageGridSpec? spec, DrainageDomain domain, double lakeAbundance, int workers, IProgress<double>? progress,
        CancellationToken cancellation, double barrierSeconds = 0, double noiseSeconds = 0, ulong gridHash = 0, double? breachShare = null, double evaporationScale = 1d)
    {
        var clock = Stopwatch.StartNew();
        DrainageConditioned conditioned = DrainageConditioning.Condition(domain, breachShare ?? DrainageConditioning.BreachShare, workers, cancellation);
        double conditioningSeconds = clock.Elapsed.TotalSeconds;
        progress?.Report(0.6);
        clock.Restart();
        DrainageRouted routed = DrainageRouting.Route(domain, conditioned, workers, DrainageBalanceParameters.From(lakeAbundance, evaporationScale));
        double routingSeconds = clock.Elapsed.TotalSeconds;
        progress?.Report(0.9);
        DrainageBakeReport report = DrainageBakeReport.Build(domain, conditioned, routed, lakeAbundance, gridHash,
            barrierSeconds, noiseSeconds, conditioningSeconds, routingSeconds, evaporationScale);
        return new DrainageField(spec, domain, conditioned, routed, report);
    }

    private static float[] BuildRoute(DrainageGrid grid, DrainageCellKind[] kind, long seed, double amplitude, int workers)
    {
        DrainageGridSpec spec = grid.Spec;
        var route = new float[grid.Count];
        Array.Copy(grid.Lowest, route, route.Length);
        if (amplitude <= 0d) return route;
        double circumference = spec.CircumferenceMeters;
        int lattice = Math.Max(1, (int)Math.Round(circumference / RoutingNoiseWavelengthMeters));
        double latticeStep = circumference / lattice;
        Parallel.For(0, spec.Rows, new ParallelOptions { MaxDegreeOfParallelism = workers }, row =>
        {
            for (int col = 0; col < spec.Columns; col++)
            {
                int cell = row * spec.Columns + col;
                if (kind[cell] != DrainageCellKind.Land) continue;
                (long around, int across) = spec.Centre(col, row);
                route[cell] += (float)(amplitude * Noise(seed, lattice, latticeStep, around, across));
            }
        });
        return route;
    }

    /// <summary>Smooth value noise in [-1, 1] on a lattice of about 5 km that closes exactly around the ring.</summary>
    internal static double Noise(long seed, int lattice, double latticeStep, double around, double across)
    {
        double u = around / latticeStep, v = across / latticeStep;
        double fu = Math.Floor(u), fv = Math.Floor(v);
        int i0 = (int)(((long)fu % lattice + lattice) % lattice), i1 = (i0 + 1) % lattice, j0 = (int)fv, j1 = j0 + 1;
        double tx = Smooth(u - fu), ty = Smooth(v - fv);
        double a = Corner(seed, i0, j0), b = Corner(seed, i1, j0), c = Corner(seed, i0, j1), e = Corner(seed, i1, j1);
        return (a + (b - a) * tx) * (1 - ty) + (c + (e - c) * tx) * ty;
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Corner(long seed, int i, int j)
    {
        unchecked
        {
            ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)i * 0xBF58476D1CE4E5B9UL ^ (ulong)(uint)j * 0x94D049BB133111EBUL;
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27; h *= 0x94D049BB133111EBUL; h ^= h >> 31;
            return (h >> 11) * (1.0 / (1UL << 53)) * 2.0 - 1.0;
        }
    }
}
