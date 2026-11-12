using System;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// D2: cell kinds of a valley-floor grid. A cell with any sample below 0 is Ocean. Emitter site discs, the Wall Engine stadiums and the Weather Lung
/// footprint come analytically from the registries, expanded by the corridor radius (300 m), the lateral cap (350 m) and half a cell diagonal, and
/// become Barrier cells (the Lung rectangle itself becomes Lung, the isolated receiving body). A strip's end columns are outlets. Rim walls and the
/// wrap are in <see cref="DrainageDomain"/>.
/// </summary>
public static class DrainageBarriers
{
    public const double CorridorRadiusMeters = 300d;
    public const double LateralCapMeters = 350d;

    public static DrainageCellKind[] Rasterise(DrainageGrid grid, TerrainGenerationParameters recipe, out int barrierCells)
    {
        DrainageGridSpec spec = grid.Spec;
        var kind = new DrainageCellKind[grid.Count];
        for (int i = 0; i < kind.Length; i++) kind[i] = grid.IsSea(i) ? DrainageCellKind.Ocean : DrainageCellKind.Land;
        if (!spec.IsWholeRing)
            for (int row = 0; row < spec.Rows; row++)
            {
                kind[row * spec.Columns] = StripOutlet(kind[row * spec.Columns]);
                kind[row * spec.Columns + spec.Columns - 1] = StripOutlet(kind[row * spec.Columns + spec.Columns - 1]);
            }
        double clearance = CorridorRadiusMeters + LateralCapMeters + 0.5 * Math.Sqrt(spec.AroundStepMeters * spec.AroundStepMeters + spec.AcrossStepMeters * spec.AcrossStepMeters);
        double circumference = spec.CircumferenceMeters;
        if (recipe.HasEmitterSites)
        {
            EmitterSiteTerrainShaper shaper = EmitterSiteTerrainShaper.GetOrCreate(recipe);
            foreach (EmitterSite site in shaper.Registry.Sites)
            {
                double reach = site.InfluenceRadius + clearance;
                double r2 = reach * reach;
                Mark(spec, kind, site.Around, site.Across, reach, reach, (da, dw) => da * da + dw * dw < r2, DrainageCellKind.Barrier);
            }
            if (shaper.Climate != null)
            {
                double half = shaper.Climate.Recipe.ForecourtLengthM * 0.5, reach = shaper.Climate.Recipe.InfluenceRadiusM + clearance, r2 = reach * reach;
                foreach (ClimateMachine m in shaper.Climate.Machines)
                    Mark(spec, kind, m.Around, m.Across, reach + half, reach, (da, dw) => { double sx = Math.Max(0d, Math.Abs(da) - half); return sx * sx + dw * dw < r2; }, DrainageCellKind.Barrier);
            }
            if (shaper.WeatherLung != null)
            {
                WeatherLungSite lung = shaper.WeatherLung.Site;
                double hx = shaper.WeatherLung.Recipe.BoundHalfXM, hy = shaper.WeatherLung.Recipe.BoundHalfYM, r2 = clearance * clearance;
                Mark(spec, kind, lung.Around, lung.Across, hx + clearance, hy + clearance, (da, dw) =>
                {
                    double ex = Math.Max(0d, Math.Abs(da) - hx), ey = Math.Max(0d, Math.Abs(dw) - hy);
                    return ex * ex + ey * ey < r2;
                }, DrainageCellKind.Barrier);
                Mark(spec, kind, lung.Around, lung.Across, hx, hy, (da, dw) => Math.Abs(da) < hx && Math.Abs(dw) < hy, DrainageCellKind.Lung);
            }
        }
        int barriers = 0;
        foreach (DrainageCellKind k in kind) if (k == DrainageCellKind.Barrier) barriers++;
        barrierCells = barriers;
        _ = circumference;
        return kind;
    }

    private static DrainageCellKind StripOutlet(DrainageCellKind kind) => kind == DrainageCellKind.Land ? DrainageCellKind.StripOutlet : kind;

    /// <summary>Sets <paramref name="value"/> on every grid cell whose centre, as signed offsets from (around, across), passes <paramref name="test"/>.</summary>
    private static void Mark(DrainageGridSpec spec, DrainageCellKind[] kind, long around, int across, double reachAround, double reachAcross,
        Func<double, double, bool> test, DrainageCellKind value)
    {
        double circumference = spec.CircumferenceMeters, step = spec.AroundStepMeters;
        long lo = (long)Math.Floor((around - reachAround) / step) - 1, hi = (long)Math.Floor((around + reachAround) / step) + 1;
        int rowLo = Math.Max(0, (int)Math.Floor((across - reachAcross) / spec.AcrossStepMeters) - 1);
        int rowHi = Math.Min(spec.Rows - 1, (int)Math.Floor((across + reachAcross) / spec.AcrossStepMeters) + 1);
        for (long ring = lo; ring <= hi; ring++)
        {
            int ringColumn = (int)(((ring % spec.RingColumns) + spec.RingColumns) % spec.RingColumns);
            int column = (ringColumn - spec.ColumnStart + spec.RingColumns) % spec.RingColumns;
            if (column >= spec.Columns) continue;
            for (int row = rowLo; row <= rowHi; row++)
            {
                (long ca, int cw) = spec.Centre(column, row);
                double da = ca - around;
                da -= circumference * Math.Round(da / circumference);
                if (!test(da, cw - across)) continue;
                int cell = row * spec.Columns + column;
                if (value == DrainageCellKind.Barrier && kind[cell] == DrainageCellKind.Lung) continue;
                kind[cell] = value;
            }
        }
    }
}
