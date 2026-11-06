using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// World-anchored, toroidally addressed R8 coverage atlas for the FFT ocean
/// ownership proof.  A source cell's texel is its absolute integer 2 m cell
/// index modulo the atlas resolution, so re-centring on a 32 m crossing needs
/// no rebuild: only the cells entering or leaving the 512 x 512 cell window
/// are rasterised or cleared.  A per-texel count makes removal exact, so the
/// bytes always equal the full window-relative rasterisation (the contract
/// test compares them against it).  The window itself is still enforced by the
/// shader, so wrapped texels outside it are never sampled.
/// </summary>
internal sealed class FftOceanCoverageAtlas
{
    public const int Resolution = 512;
    public const float CellMeters = 2.0f;
    private const int Half = Resolution / 2;

    public readonly struct Rect
    {
        public readonly long X0, X1, Z0, Z1;
        public Rect(long x0, long x1, long z0, long z1) { X0 = x0; X1 = x1; Z0 = z0; Z1 = z1; }
        public bool IsEmpty => X1 <= X0 || Z1 <= Z0;
    }

    private readonly byte[] _bytes = new byte[Resolution * Resolution];
    private readonly int[] _counts = new int[Resolution * Resolution];
    private readonly Dictionary<RingTerrainSectorCoordinate, bool[]> _rasteredCells = new();
    private readonly Dictionary<RingTerrainSectorCoordinate, int> _rasteredQuadCells = new();
    private bool _valid;
    private int _sectorSize;
    private double _circumference;
    private double _gridAround;
    private double _gridAcross;
    private long _ux;
    private long _uz;

    public byte[] Bytes => _bytes;
    /// <summary>True when Bytes changed since the last ClearDirty.</summary>
    public bool Dirty { get; private set; }
    public long FullRebuildCount { get; private set; }
    public long IncrementalUpdateCount { get; private set; }
    public void ClearDirty() => Dirty = false;

    /// <summary>Texel of the window center cell origin, for the shader's toroidal lookup.</summary>
    public Vector2 OriginTexel => new(PosMod(_ux), PosMod(_uz));

    public void Invalidate() => _valid = false;

    /// <summary>
    /// Brings the atlas to the state of rasterising <paramref name="visible"/>
    /// about the grid origin (gridAround, gridAcross), which must be multiples
    /// of 2 m.  Entries are compared by mask reference (a changed sector
    /// carries a new mask).
    /// </summary>
    public void Update(
        IReadOnlyDictionary<RingTerrainSectorCoordinate, (int ProxyQuadCells, bool[] CoveredCells)> visible,
        int sectorSize, double circumference, double gridAround, double gridAcross)
    {
        bool full = !_valid || sectorSize != _sectorSize || circumference != _circumference;
        if (!full)
        {
            SyncSectors(visible);
            long stepX = (long)Math.Round(SignedPeriodicDistance(gridAround, _gridAround, circumference) / CellMeters);
            long stepZ = (long)Math.Round((gridAcross - _gridAcross) / CellMeters);
            if (Math.Abs(stepX) >= Resolution || Math.Abs(stepZ) >= Resolution) full = true;
            else if (stepX != 0 || stepZ != 0) Move(stepX, stepZ, gridAround, gridAcross);
            if (!full) IncrementalUpdateCount++;
        }
        if (full) Rebuild(visible, sectorSize, circumference, gridAround, gridAcross);
    }

    private void Rebuild(
        IReadOnlyDictionary<RingTerrainSectorCoordinate, (int ProxyQuadCells, bool[] CoveredCells)> visible,
        int sectorSize, double circumference, double gridAround, double gridAcross)
    {
        Array.Clear(_counts);
        Array.Clear(_bytes);
        _rasteredCells.Clear();
        _rasteredQuadCells.Clear();
        _sectorSize = sectorSize;
        _circumference = circumference;
        _gridAround = gridAround;
        _gridAcross = gridAcross;
        _ux = (long)Math.Round(gridAround / CellMeters);
        _uz = (long)Math.Round(gridAcross / CellMeters);
        _valid = true;
        FullRebuildCount++;
        foreach ((RingTerrainSectorCoordinate coordinate, (int quadCells, bool[] covered) mask) in visible)
        {
            Raster(coordinate, mask.quadCells, mask.covered, +1, Window());
            _rasteredCells[coordinate] = mask.covered;
            _rasteredQuadCells[coordinate] = mask.quadCells;
        }
        Dirty = true;
    }

    private void SyncSectors(IReadOnlyDictionary<RingTerrainSectorCoordinate, (int ProxyQuadCells, bool[] CoveredCells)> visible)
    {
        List<RingTerrainSectorCoordinate>? removed = null;
        foreach ((RingTerrainSectorCoordinate coordinate, bool[] covered) in _rasteredCells)
        {
            if (visible.TryGetValue(coordinate, out var current) && ReferenceEquals(current.CoveredCells, covered)
                && current.ProxyQuadCells == _rasteredQuadCells[coordinate]) continue;
            (removed ??= new()).Add(coordinate);
        }
        if (removed != null)
            foreach (RingTerrainSectorCoordinate coordinate in removed)
            {
                Raster(coordinate, _rasteredQuadCells[coordinate], _rasteredCells[coordinate], -1, Window());
                _rasteredCells.Remove(coordinate);
                _rasteredQuadCells.Remove(coordinate);
            }
        foreach ((RingTerrainSectorCoordinate coordinate, (int quadCells, bool[] covered) mask) in visible)
        {
            if (_rasteredCells.ContainsKey(coordinate)) continue;
            Raster(coordinate, mask.quadCells, mask.covered, +1, Window());
            _rasteredCells[coordinate] = mask.covered;
            _rasteredQuadCells[coordinate] = mask.quadCells;
        }
    }

    private void Move(long stepX, long stepZ, double gridAround, double gridAcross)
    {
        Rect old = Window();
        Rect next = new(old.X0 + stepX, old.X1 + stepX, old.Z0 + stepZ, old.Z1 + stepZ);
        Span<Rect> regions = stackalloc Rect[4];
        // Cells are located under the state that owned them: leaving ones under
        // the old grid, entering ones under the new grid.
        int count = Subtract(old, next, regions);
        for (int i = 0; i < count; i++) RasterAll(-1, regions[i]);
        _ux += stepX;
        _uz += stepZ;
        _gridAround = gridAround;
        _gridAcross = gridAcross;
        count = Subtract(next, old, regions);
        for (int i = 0; i < count; i++) RasterAll(+1, regions[i]);
    }

    private void RasterAll(int sign, Rect region)
    {
        foreach ((RingTerrainSectorCoordinate coordinate, bool[] covered) in _rasteredCells)
            Raster(coordinate, _rasteredQuadCells[coordinate], covered, sign, region);
    }

    private Rect Window() => new(_ux - Half, _ux + Half, _uz - Half, _uz + Half);

    // a minus b as up to four disjoint rectangles.
    private static int Subtract(Rect a, Rect b, Span<Rect> output)
    {
        long ix0 = Math.Max(a.X0, b.X0), ix1 = Math.Min(a.X1, b.X1);
        long iz0 = Math.Max(a.Z0, b.Z0), iz1 = Math.Min(a.Z1, b.Z1);
        if (ix1 <= ix0 || iz1 <= iz0)
        {
            output[0] = a;
            return 1;
        }
        int n = 0;
        Rect left = new(a.X0, ix0, a.Z0, a.Z1), right = new(ix1, a.X1, a.Z0, a.Z1);
        Rect below = new(ix0, ix1, a.Z0, iz0), above = new(ix0, ix1, iz1, a.Z1);
        if (!left.IsEmpty) output[n++] = left;
        if (!right.IsEmpty) output[n++] = right;
        if (!below.IsEmpty) output[n++] = below;
        if (!above.IsEmpty) output[n++] = above;
        return n;
    }

    // Marks (+1) or unmarks (-1) the texel of every covered cell whose absolute
    // cell index lies inside region.  Per-cell maths mirrors the reference
    // rasteriser (centroid, periodic around delta, float floor) so the result is
    // byte-identical; the loop bounds merely skip cells that cannot qualify.
    private void Raster(RingTerrainSectorCoordinate coordinate, int cells, bool[] covered, int sign, Rect region)
    {
        if (region.IsEmpty || cells <= 0) return;
        double sectorSize = _sectorSize;
        double step = sectorSize / cells;
        double baseAround = coordinate.AroundSector * sectorSize;
        double baseAcross = coordinate.AcrossSector * sectorSize;
        double delta0 = SignedPeriodicDistance(baseAround, _gridAround, _circumference);
        (int xs, int xe) = Bounds(region.X0 - _ux, region.X1 - _ux, delta0, step, cells,
            Math.Abs(delta0) + sectorSize * 2.0 + 1024.0 > _circumference * 0.5);
        (int zs, int ze) = Bounds(region.Z0 - _uz, region.Z1 - _uz, baseAcross - _gridAcross, step, cells, false);
        for (int z = zs; z < ze; z++)
        {
            double across = coordinate.AcrossSector * sectorSize + (z + 0.5) * sectorSize / cells;
            long az = _uz + (long)MathF.Floor((float)((across - _gridAcross) / CellMeters));
            if (az < region.Z0 || az >= region.Z1) continue;
            int row = PosMod(az) * Resolution;
            for (int x = xs; x < xe; x++)
            {
                if (!covered[z * cells + x]) continue;
                double around = coordinate.AroundSector * sectorSize + (x + 0.5) * sectorSize / cells;
                double deltaAround = SignedPeriodicDistance(around, _gridAround, _circumference);
                long ax = _ux + (long)MathF.Floor((float)(deltaAround / CellMeters));
                if (ax < region.X0 || ax >= region.X1) continue;
                int index = row + PosMod(ax);
                if (sign > 0)
                {
                    if (_counts[index]++ == 0) { _bytes[index] = byte.MaxValue; Dirty = true; }
                }
                else if (_counts[index] > 0 && --_counts[index] == 0)
                {
                    _bytes[index] = 0;
                    Dirty = true;
                }
            }
        }
    }

    // Cell index range that can land in [lo, hi) cells of the window, given the
    // sector origin offset in meters.  Conservative by a cell each side.
    private static (int Start, int End) Bounds(long lo, long hi, double originOffset, double step, int cells, bool all)
    {
        if (all) return (0, cells);
        int start = (int)Math.Clamp(Math.Floor((lo * (double)CellMeters - originOffset) / step - 0.5) - 1.0, 0.0, cells);
        int end = (int)Math.Clamp(Math.Ceiling((hi * (double)CellMeters - originOffset) / step - 0.5) + 1.0, 0.0, cells);
        return (start, Math.Max(start, end));
    }

    private static int PosMod(long value)
    {
        int m = (int)(value % Resolution);
        return m < 0 ? m + Resolution : m;
    }

    private static double SignedPeriodicDistance(double from, double to, double circumference)
    {
        double length = Math.Max(1.0, circumference);
        double distance = (from - to) % length;
        if (distance > length * 0.5) distance -= length;
        if (distance < -length * 0.5) distance += length;
        return distance;
    }

    /// <summary>
    /// Test helper: the window-relative bytes equivalent to the reference
    /// rasteriser (pixel (px, pz) reads the texel of absolute cell
    /// (ux - Half + px, uz - Half + pz)).
    /// </summary>
    public byte[] ToWindowRelative()
    {
        var result = new byte[Resolution * Resolution];
        for (int pz = 0; pz < Resolution; pz++)
            for (int px = 0; px < Resolution; px++)
                result[pz * Resolution + px] = _bytes[PosMod(_uz - Half + pz) * Resolution + PosMod(_ux - Half + px)];
        return result;
    }
}
