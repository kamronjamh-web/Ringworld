using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The derived lookup structures of a <see cref="RiverGraph"/> (S5), rebuilt at load from the stored arrays: one bit per 1 km cell for flooded, ring (dry, 8-adjacent to
/// a flooded cell, barrier cells included) and Near ("something may be within reach", the operator's early exit), and 4 x 4 cell buckets listing the curve pieces whose
/// control-point box expanded by the reach radius overlaps them. Piece k of a stem is the quadratic piece of node k, so a piece id is the global node index.
/// A query allocates nothing.
/// </summary>
public sealed class RiverSpatialIndex
{
    /// <summary>300 m fade plus the 350 m lateral cap (S7).</summary>
    public const double ReachRadiusMeters = 650d;
    public const int BucketCells = 4;

    private readonly int _ringColumns, _rows;
    private readonly double _aroundStep, _acrossStep, _circumference;
    private readonly ulong[] _flooded, _ring, _near;
    private readonly int _bucketColumns, _bucketRows;
    private int[] _offsets;
    private int[] _entries;

    public int RingColumns => _ringColumns;
    public int Rows => _rows;
    public int BucketColumns => _bucketColumns;
    public int BucketRows => _bucketRows;
    public int EntryCount => _entries.Length;
    public long ResidentBytes => BitsetBytes + BucketOffsetBytes + BucketEntryBytes;
    public long BitsetBytes => 3L * _flooded.Length * 8;
    public long BucketOffsetBytes => (long)_offsets.Length * 4;
    public long BucketEntryBytes => (long)_entries.Length * 4;

    private RiverSpatialIndex(int ringColumns, int rows, double aroundStep, double acrossStep)
    {
        _ringColumns = ringColumns; _rows = rows; _aroundStep = aroundStep; _acrossStep = acrossStep; _circumference = aroundStep * ringColumns;
        int words = (int)(((long)ringColumns * rows + 63) / 64);
        _flooded = new ulong[words]; _ring = new ulong[words]; _near = new ulong[words];
        _bucketColumns = (ringColumns + BucketCells - 1) / BucketCells;
        _bucketRows = (rows + BucketCells - 1) / BucketCells;
        _offsets = Array.Empty<int>(); _entries = Array.Empty<int>();
    }

    public static RiverSpatialIndex Build(RiverNetwork net, int ringColumns, int rows, ReadOnlySpan<LakeRun> runs)
    {
        double aroundStep = (double)net.CircumferenceM / ringColumns, acrossStep = net.WidthM / rows;
        var index = new RiverSpatialIndex(ringColumns, rows, aroundStep, acrossStep);
        index.MarkLakes(runs);
        index.BuildPieces(net);
        index.CloseNear();
        return index;
    }

    private static bool Get(ulong[] bits, long i) => ((bits[i >> 6] >> (int)(i & 63)) & 1UL) != 0;
    private static void Set(ulong[] bits, long i) => bits[i >> 6] |= 1UL << (int)(i & 63);

    private long CellIndex(int col, int row) => (long)row * _ringColumns + col;

    private void MarkLakes(ReadOnlySpan<LakeRun> runs)
    {
        foreach (LakeRun run in runs)
            for (int c = 0; c < run.Length; c++) Set(_flooded, CellIndex(run.Col + c, run.Row));
        foreach (LakeRun run in runs)
            for (int c = 0; c < run.Length; c++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int row = run.Row + dy;
                    if ((uint)row >= (uint)_rows) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int col = (run.Col + c + dx + _ringColumns) % _ringColumns;
                        long i = CellIndex(col, row);
                        if (!Get(_flooded, i)) Set(_ring, i);
                    }
                }
    }

    /// <summary>Control-point box of piece k of a stem in unwrapped metres, expanded by the reach radius.</summary>
    private static void PieceBox(RiverNetwork net, int stem, int k, out double aroundLo, out double aroundHi, out double acrossLo, out double acrossHi)
    {
        RiverStemCurve c = net.Curves[stem];
        c.Controls(k, out double ax, out double ay, out double px, out double py, out double cx, out double cy);
        aroundLo = c.OriginAround + Math.Min(ax, Math.Min(px, cx)) - ReachRadiusMeters;
        aroundHi = c.OriginAround + Math.Max(ax, Math.Max(px, cx)) + ReachRadiusMeters;
        acrossLo = c.OriginAcross + Math.Min(ay, Math.Min(py, cy)) - ReachRadiusMeters;
        acrossHi = c.OriginAcross + Math.Max(ay, Math.Max(py, cy)) + ReachRadiusMeters;
    }

    private void CellRange(double aroundLo, double aroundHi, double acrossLo, double acrossHi, out int c0, out int c1, out int r0, out int r1)
    {
        c0 = (int)Math.Floor(aroundLo / _aroundStep);
        c1 = (int)Math.Floor(aroundHi / _aroundStep);
        if (c1 - c0 + 1 > _ringColumns) c1 = c0 + _ringColumns - 1;
        r0 = Math.Clamp((int)Math.Floor(acrossLo / _acrossStep), 0, _rows - 1);
        r1 = Math.Clamp((int)Math.Floor(acrossHi / _acrossStep), 0, _rows - 1);
    }

    private int Wrap(int col) { int m = col % _ringColumns; return m < 0 ? m + _ringColumns : m; }

    private void BuildPieces(RiverNetwork net)
    {
        int bucketCount = _bucketColumns * _bucketRows;
        var counts = new int[bucketCount + 1];
        int[]? cursor = null;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1) cursor = (int[])counts.Clone();
            for (int stem = 0; stem < net.Stems.Length; stem++)
            {
                RiverStem st = net.Stems[stem];
                for (int k = 0; k < st.NodeCount; k++)
                {
                    PieceBox(net, stem, k, out double a0, out double a1, out double b0, out double b1);
                    CellRange(a0, a1, b0, b1, out int c0, out int c1, out int r0, out int r1);
                    if (pass == 0) for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) Set(_near, CellIndex(Wrap(c), r));
                    int piece = st.NodeStart + k;
                    for (int br = r0 / BucketCells; br <= r1 / BucketCells; br++)
                    {
                        int last = -1;
                        for (int c = c0; c <= c1; c++)
                        {
                            int bc = Wrap(c) / BucketCells;
                            if (bc == last) continue;
                            last = bc;
                            int b = br * _bucketColumns + bc;
                            if (pass == 0) counts[b + 1]++; else _entries[cursor![b]++] = piece;
                        }
                    }
                }
            }
            if (pass == 0)
            {
                for (int b = 0; b < bucketCount; b++) counts[b + 1] += counts[b];
                _offsets = counts;
                _entries = new int[counts[bucketCount]];
            }
        }
    }

    /// <summary>Near also holds the lakes, their ring and the cells next to the ring.</summary>
    private void CloseNear()
    {
        for (int w = 0; w < _flooded.Length; w++) _near[w] |= _flooded[w] | _ring[w];
        for (int w = 0; w < _ring.Length; w++)
        {
            ulong bits = _ring[w];
            while (bits != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                long i = ((long)w << 6) + b;
                int row = (int)(i / _ringColumns), col = (int)(i - (long)row * _ringColumns);
                for (int dy = -1; dy <= 1; dy++)
                {
                    int r = row + dy;
                    if ((uint)r >= (uint)_rows) continue;
                    for (int dx = -1; dx <= 1; dx++) Set(_near, CellIndex(Wrap(col + dx), r));
                }
            }
        }
    }

    public int ColumnOf(double around)
    {
        double a = around % _circumference;
        if (a < 0) a += _circumference;
        return Math.Min((int)(a / _aroundStep), _ringColumns - 1);
    }

    public int RowOf(double across) => Math.Clamp((int)Math.Floor(across / _acrossStep), 0, _rows - 1);

    public bool IsFlooded(int col, int row) => Get(_flooded, CellIndex(col, row));
    public bool IsRing(int col, int row) => Get(_ring, CellIndex(col, row));
    public bool IsNear(int col, int row) => Get(_near, CellIndex(col, row));
    public bool IsNear(double around, double across) => Get(_near, CellIndex(ColumnOf(around), RowOf(across)));

    /// <summary>Piece ids (global node indices) listed in the bucket of the point: a superset of every piece within <see cref="ReachRadiusMeters"/> of it.</summary>
    public ReadOnlySpan<int> PiecesNear(double around, double across)
    {
        int b = RowOf(across) / BucketCells * _bucketColumns + ColumnOf(around) / BucketCells;
        return new ReadOnlySpan<int>(_entries, _offsets[b], _offsets[b + 1] - _offsets[b]);
    }

    /// <summary>Test hook for the negative control: a copy of the index with one piece removed from the bucket of the given point.</summary>
    internal RiverSpatialIndex WithoutEntry(double around, double across, int piece)
    {
        var copy = (RiverSpatialIndex)MemberwiseClone();
        int b = RowOf(across) / BucketCells * _bucketColumns + ColumnOf(around) / BucketCells;
        var entries = new List<int>(_entries);
        int at = entries.IndexOf(piece, _offsets[b], _offsets[b + 1] - _offsets[b]);
        if (at < 0) return copy;
        entries.RemoveAt(at);
        var offsets = (int[])_offsets.Clone();
        for (int i = b + 1; i < offsets.Length; i++) offsets[i]--;
        copy._entries = entries.ToArray();
        copy._offsets = offsets;
        return copy;
    }
}
