using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// A lake of the graph (S5, S8, S8b): the whole-metre level and the flooded cells (stored as <see cref="LakeRun"/>s). The outlet is the stem whose first reach has this lake
/// as <see cref="RiverReach.UpstreamLake"/> (-1: the lake spills into a cell below the channel threshold); <see cref="OutletS"/> is where that stem leaves the lake's
/// membership ring. Inlets are the stems that end in the lake. Cell coordinates are ring columns and rows.
/// </summary>
public struct RiverLake
{
    /// <summary>floor(RawLevel) in metres (S8).</summary>
    public float Level;
    /// <summary>The conditioning's spill level, report only.</summary>
    public float RawLevel;
    public int CellCount;
    public int OutletStem;
    public float OutletS;
    public int InletStart, InletCount;
    public int MinCol, MinRow, MaxCol, MaxRow;
    /// <summary>Lowest valley-floor sample among the flooded cells, and that cell.</summary>
    public float MinHeight;
    public int DeepestCol, DeepestRow;
    /// <summary>Id of the closed basin (<see cref="RiverBasin"/>) of a balance lake, -1 for a spilling lake (S8b).</summary>
    public int Basin;
    /// <summary>Inflow in runoff units (km^2 at moisture 0.5; discharge = q_s x value), report and tuning only.</summary>
    public float Inflow;

    public readonly float Depth => Level - MinHeight;
}

/// <summary>A stem ending in a lake: the arc parameter (the stem's end) and the end node's position in decimetres. The direction is the stem tangent at <see cref="S"/>.</summary>
public struct RiverInlet
{
    public int Stem;
    public float S;
    public int AroundDm, AcrossDm;
}

/// <summary>A run of flooded cells of one lake (or of dry floor of one closed basin, with the basin id in LakeId) in one row (ring columns, not wrapped across the seam). Sorted by row, then column.</summary>
public struct LakeRun
{
    public ushort Row, Col, Length;
    public int LakeId;
}

/// <summary>A closed basin (S8b), ids by lowest basin cell: its spill level, the level of its lowest lake (or its lowest cell when it holds none), and its dry-floor and lake cell counts. The dry floor is stored as runs.</summary>
public struct RiverBasin
{
    public float SpillLevel, FloorLevel;
    public int DryCellCount, LakeCount;
}
