using System;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// Geometry of a valley-floor grid (docs/t2-river-reset.md 4.2, D1): <see cref="RingColumns"/> = round(circumference / 1000) columns of equal spacing
/// around the whole ring and <see cref="Rows"/> = round(width / 1000) rows across, exact wrap. A spec covers the whole ring or a strip of whole columns
/// (a strip may cross the seam). Every cell takes 4 x 4 samples at integer-metre positions on a 250 m lattice plus a centre point for moisture.
/// The positions are computed once, here, and passed to both height sources, so the GPU and the CPU read identical integers.
/// </summary>
public sealed class DrainageGridSpec
{
    public const int SamplesPerAxis = 4;
    public const int SamplesPerCell = SamplesPerAxis * SamplesPerAxis;
    /// <summary>Entries per column or row in <see cref="AroundSamples"/> and <see cref="AcrossSamples"/>: four samples, then the centre.</summary>
    public const int Stride = SamplesPerAxis + 1;

    public int RingColumns { get; }
    public int ColumnStart { get; }
    public int Columns { get; }
    public int Rows { get; }
    public long CircumferenceMeters { get; }
    public double AroundStepMeters { get; }
    public double AcrossStepMeters { get; }
    /// <summary>Per column, Stride wrapped positions in [0, circumference): samples i = 0..3 then the centre.</summary>
    public long[] AroundSamples { get; }
    /// <summary>Per row, Stride positions across: samples j = 0..3 then the centre.</summary>
    public int[] AcrossSamples { get; }

    public int CellCount => Columns * Rows;
    public bool IsWholeRing => Columns == RingColumns;

    private DrainageGridSpec(TerrainGenerationParameters p, int columnStart, int columns)
    {
        CircumferenceMeters = checked((long)p.CircumferenceM);
        RingColumns = (int)Math.Round(p.CircumferenceM / 1000d);
        Rows = (int)Math.Round(p.WidthM / 1000d);
        if (columns < 1 || columns > RingColumns) throw new ArgumentOutOfRangeException(nameof(columns));
        ColumnStart = ((columnStart % RingColumns) + RingColumns) % RingColumns;
        Columns = columns;
        AroundStepMeters = p.CircumferenceM / RingColumns;
        AcrossStepMeters = p.WidthM / Rows;
        AroundSamples = new long[columns * Stride];
        for (int c = 0; c < columns; c++)
        {
            double origin = (ColumnStart + c) * AroundStepMeters;
            for (int i = 0; i < SamplesPerAxis; i++) AroundSamples[c * Stride + i] = WrapAround(origin + (i + 0.5) * AroundStepMeters / SamplesPerAxis);
            AroundSamples[c * Stride + SamplesPerAxis] = WrapAround(origin + 0.5 * AroundStepMeters);
        }
        AcrossSamples = new int[Rows * Stride];
        for (int r = 0; r < Rows; r++)
        {
            double origin = r * AcrossStepMeters;
            for (int j = 0; j < SamplesPerAxis; j++) AcrossSamples[r * Stride + j] = (int)Math.Floor(origin + (j + 0.5) * AcrossStepMeters / SamplesPerAxis + 0.5);
            AcrossSamples[r * Stride + SamplesPerAxis] = (int)Math.Floor(origin + 0.5 * AcrossStepMeters + 0.5);
        }
    }

    private long WrapAround(double meters)
    {
        long v = (long)Math.Floor(meters + 0.5) % CircumferenceMeters;
        return v < 0 ? v + CircumferenceMeters : v;
    }

    /// <summary>The whole ring, starting at around = 0.</summary>
    public static DrainageGridSpec Ring(TerrainGenerationParameters p) => new(p, 0, (int)Math.Round(p.CircumferenceM / 1000d));

    /// <summary>A strip of <paramref name="kilometres"/> whole columns of the ring grid starting at the column nearest <paramref name="startMeters"/>; it wraps at the seam.</summary>
    public static DrainageGridSpec Strip(TerrainGenerationParameters p, double startMeters, int kilometres)
        => new(p, (int)Math.Round(startMeters / (p.CircumferenceM / Math.Round(p.CircumferenceM / 1000d))), kilometres);

    /// <summary>Ring column index (0..RingColumns-1) of the strip column <paramref name="column"/>.</summary>
    public int RingColumn(int column) => (ColumnStart + column) % RingColumns;

    public (long Around, int Across) Sample(int column, int row, int i, int j) => (AroundSamples[column * Stride + i], AcrossSamples[row * Stride + j]);
    public (long Around, int Across) Centre(int column, int row) => (AroundSamples[column * Stride + SamplesPerAxis], AcrossSamples[row * Stride + SamplesPerAxis]);
}

/// <summary>
/// The valley-floor grid: per cell the lowest of its 16 natural-height samples, which sample that was (j * 4 + i, the first wins a tie) and the moisture at
/// the centre. A cell with any sample below 0 is sea, which is the same as its lowest sample being below 0. Cell index = row * Columns + column.
/// </summary>
public sealed class DrainageGrid
{
    public DrainageGridSpec Spec { get; }
    public float[] Lowest { get; }
    public byte[] LowestIndex { get; }
    public float[] Moisture { get; }

    public DrainageGrid(DrainageGridSpec spec)
    {
        Spec = spec;
        Lowest = new float[spec.CellCount];
        LowestIndex = new byte[spec.CellCount];
        Moisture = new float[spec.CellCount];
    }

    public int Columns => Spec.Columns;
    public int Rows => Spec.Rows;
    public int Count => Spec.CellCount;
    public bool IsSea(int cell) => Lowest[cell] < 0f;

    /// <summary>FNV-1a over the bit patterns of every array, for determinism checks.</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < Lowest.Length; i++)
        {
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(Lowest[i])) * 1099511628211UL;
            h = (h ^ LowestIndex[i]) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(Moisture[i])) * 1099511628211UL;
        }
        return h;
    }
}
