using System;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Role of a drainage cell. Basin is a terminal value only (S8b: the pit of a closed basin), never the kind of a cell. Ocean (any sample below 0), Lung (the Weather Lung basin) and StripOutlet (the two end columns of a strip) are outlets.</summary>
public enum DrainageCellKind : byte { Land, Ocean, Lung, Barrier, StripOutlet, Basin }

/// <summary>
/// The grid the conditioning and routing stages work on (docs/t2-river-reset.md 4.2, D2-D4): cell kinds, the unperturbed valley-floor height
/// (<see cref="Lowest"/>), the routing height (<see cref="Route"/>, the same plus the routing noise on land) and moisture. Neighbour lookups follow the
/// study's Grid: eight directions, closed across edges (rim walls), exact wrap around when <see cref="Wrap"/>, barriers block, and a diagonal link
/// cannot pass a blocked orthogonal neighbour. Cell index = row * Width + column. Synthetic fixtures build one directly.
/// </summary>
public sealed class DrainageDomain
{
    public static readonly int[] Dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    public static readonly int[] Dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public int Width { get; }
    public int Height { get; }
    public bool Wrap { get; }
    public double AroundStepMeters { get; }
    public double AcrossStepMeters { get; }
    public DrainageCellKind[] Kind { get; }
    public float[] Lowest { get; }
    public float[] Route { get; }
    public float[] Moisture { get; }

    public int Count => Width * Height;
    public double CellAreaKm2 => AroundStepMeters * AcrossStepMeters / 1_000_000d;

    public DrainageDomain(int width, int height, bool wrap, double aroundStepMeters, double acrossStepMeters,
        DrainageCellKind[] kind, float[] lowest, float[] route, float[] moisture)
    {
        Width = width; Height = height; Wrap = wrap;
        AroundStepMeters = aroundStepMeters; AcrossStepMeters = acrossStepMeters;
        Kind = kind; Lowest = lowest; Route = route; Moisture = moisture;
    }

    public bool IsLand(int cell) => Kind[cell] is DrainageCellKind.Land or DrainageCellKind.StripOutlet;
    public bool IsOutlet(int cell) => Kind[cell] is DrainageCellKind.Ocean or DrainageCellKind.Lung or DrainageCellKind.StripOutlet;

    public bool TryNeighbor(int cell, int direction, out int neighbor)
    {
        if (Kind[cell] == DrainageCellKind.Barrier) { neighbor = -1; return false; }
        int row = cell / Width, rowStart = row * Width;
        neighbor = NeighborIndex(row, rowStart, cell - rowStart, direction);
        return neighbor >= 0;
    }

    /// <summary>One cell index per direction, -1 for blocked or out-of-domain edges.</summary>
    public void GetNeighbors(int cell, Span<int> neighbors)
    {
        if (Kind[cell] == DrainageCellKind.Barrier) { neighbors[..8].Fill(-1); return; }
        int row = cell / Width, rowStart = row * Width, column = cell - rowStart;
        for (int d = 0; d < 8; d++) neighbors[d] = NeighborIndex(row, rowStart, column, d);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NeighborIndex(int row, int rowStart, int column, int direction)
    {
        int dx = Dx[direction], dy = Dy[direction];
        int nextRow = row + dy;
        if ((uint)nextRow >= (uint)Height) return -1;
        int nextColumn = column + dx;
        if ((uint)nextColumn >= (uint)Width)
        {
            if (!Wrap) return -1;
            nextColumn = nextColumn < 0 ? Width - 1 : 0;
        }
        int nextRowStart = nextRow * Width;
        int next = nextRowStart + nextColumn;
        if (Kind[next] == DrainageCellKind.Barrier) return -1;
        if (dx != 0 && dy != 0 && (Kind[rowStart + nextColumn] == DrainageCellKind.Barrier || Kind[nextRowStart + column] == DrainageCellKind.Barrier)) return -1;
        return next;
    }

    /// <summary>Geometric neighbour that ignores kinds (barriers included); -1 outside the rim.</summary>
    public int GeometricNeighbor(int cell, int direction)
    {
        int row = cell / Width, column = cell - row * Width;
        int nextRow = row + Dy[direction];
        if ((uint)nextRow >= (uint)Height) return -1;
        int nextColumn = column + Dx[direction];
        if ((uint)nextColumn >= (uint)Width)
        {
            if (!Wrap) return -1;
            nextColumn = nextColumn < 0 ? Width - 1 : 0;
        }
        return nextRow * Width + nextColumn;
    }

    /// <summary>Direction ordinal of the legal link from -> to, or -1.</summary>
    public int Direction(int from, int to)
    {
        int dy = to / Width - from / Width;
        int dx = to % Width - from % Width;
        if (Wrap && Math.Abs(dx) == Width - 1) dx = -Math.Sign(dx);
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0)) return -1;
        int ordinal = (dy + 1) * 3 + dx + 1;
        if (ordinal > 4) ordinal--;
        return TryNeighbor(from, ordinal, out int actual) && actual == to ? ordinal : -1;
    }

    public double StepMeters(int direction)
    {
        if (Dx[direction] == 0) return AcrossStepMeters;
        if (Dy[direction] == 0) return AroundStepMeters;
        return Math.Sqrt(AroundStepMeters * AroundStepMeters + AcrossStepMeters * AcrossStepMeters);
    }
}
