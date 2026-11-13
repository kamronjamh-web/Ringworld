using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// T2R-23 (docs/work-orders/terrain-vegetation/T2-rules.md, Spec RS2, rule C): sites remove the water that reaches them. A river whose water ends at a site is not mapped, from its source down, tributaries
/// included: <see cref="RiverGraphBuilder"/> leaves every cell whose terminal is an Intake cell (the edge of a keep-out) out of the channel cells. This class holds what the report tool and the tests read.
/// </summary>
public static class RiverSiteRemoval
{
    public const byte ReasonNone = 0, ReasonEndsAtSite = 1, ReasonSiteDrainedInlet = 2, ReasonDrainsToSite = 3;

    /// <summary>How a chain of the build without removal relates to a site (report tool): its next cell is an edge cell (1); its last or next cell lies in a site-drained basin (2); its water otherwise ends at a site (3, a tributary); 0 when it is not removed.</summary>
    public static byte Classify(int lastCell, int[] down, DrainageCellKind[] kind, DrainageCellKind[] terminal, bool[]? siteDrainedCell)
    {
        int next = down[lastCell];
        if (next >= 0 && kind[next] == DrainageCellKind.Intake) return ReasonEndsAtSite;
        if (siteDrainedCell != null && (siteDrainedCell[lastCell] || (next >= 0 && siteDrainedCell[next]))) return ReasonSiteDrainedInlet;
        if (terminal[lastCell] == DrainageCellKind.Intake) return ReasonDrainsToSite;
        return ReasonNone;
    }

    /// <summary>Per cell: it lies in a basin that holds no lake because it spills into a site. Null when the field has none.</summary>
    internal static bool[]? SiteDrainedCells(DrainageField f)
    {
        ReadOnlySpan<DrainageBasinInfo> basins = f.Basins.Span;
        bool any = false;
        foreach (DrainageBasinInfo info in basins) if (info.SiteDrained) { any = true; break; }
        if (!any) return null;
        ReadOnlySpan<int> basinId = f.BasinId;
        var cells = new bool[basinId.Length];
        for (int cell = 0; cell < cells.Length; cell++) cells[cell] = basinId[cell] >= 0 && basins[basinId[cell]].SiteDrained;
        return cells;
    }
}
