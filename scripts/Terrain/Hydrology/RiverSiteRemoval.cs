using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// T2R-23 (docs/work-orders/terrain-vegetation/T2-rules.md, Spec RS2): which rivers a site removes. None: no removal (production in Part A). Branch: the user's wording, the stems that end at a site or are inlets of a site-drained basin
/// are removed whole and the stems that joined them stay and end with <see cref="RiverTerminal.Cut"/>. Catchment: every cell whose water ends at a site is not a channel cell, so everything that drains into a site is removed.
/// </summary>
public enum RiverSiteRemovalRule : byte { None, Branch, Catchment }

/// <summary>What <see cref="RiverSiteRemoval.Mark"/> removed: stems, their cells, and the stems by reason (index 1 ends at a site, 2 inlet of a site-drained basin).</summary>
public readonly record struct RiverSiteRemovalStats(int RemovedStems, int RemovedCells, int[] ByReason);

/// <summary>The marking of RS2 "Rivers", written as functions of arrays (chains, <c>down</c>, cell kinds, terminals and the site-drained cells) so that a test calls it without a grid.</summary>
public static class RiverSiteRemoval
{
    public const byte ReasonNone = 0, ReasonEndsAtSite = 1, ReasonSiteDrainedInlet = 2, ReasonDrainsToSite = 3;

    /// <summary>Why a chain is removed: its next cell is an edge cell (1); its last or next cell lies in a site-drained basin (2); its water otherwise ends at a site (3, rule Catchment only); 0 when it is not removed.</summary>
    public static byte Classify(int lastCell, int[] down, DrainageCellKind[] kind, DrainageCellKind[] terminal, bool[]? siteDrainedCell)
    {
        int next = down[lastCell];
        if (next >= 0 && kind[next] == DrainageCellKind.Intake) return ReasonEndsAtSite;
        if (siteDrainedCell != null && (siteDrainedCell[lastCell] || (next >= 0 && siteDrainedCell[next]))) return ReasonSiteDrainedInlet;
        if (terminal[lastCell] == DrainageCellKind.Intake) return ReasonDrainsToSite;
        return ReasonNone;
    }

    /// <summary>Rule Branch: the cells of every chain with reason 1 or 2. Chains that joined one are not marked; the builder ends them with Cut.</summary>
    internal static bool[] Mark(IReadOnlyList<int[]> chains, int[] down, DrainageCellKind[] kind, DrainageCellKind[] terminal, bool[]? siteDrainedCell, out RiverSiteRemovalStats stats)
    {
        var removed = new bool[down.Length];
        var byReason = new int[4];
        int stems = 0, cells = 0;
        foreach (int[] chain in chains)
        {
            byte reason = Classify(chain[^1], down, kind, terminal, siteDrainedCell);
            if (reason is not (ReasonEndsAtSite or ReasonSiteDrainedInlet)) continue;
            stems++; byReason[reason]++;
            foreach (int cell in chain) { removed[cell] = true; cells++; }
        }
        stats = new RiverSiteRemovalStats(stems, cells, byReason);
        return removed;
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
