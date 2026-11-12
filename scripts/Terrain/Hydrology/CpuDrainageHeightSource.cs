using System;
using System.Threading;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// CPU drainage height source: the natural <see cref="TerrainGenerationHeightModel"/> (applyEmitterSites false), one instance per worker, which is the
/// deterministic reference (docs/t2-river-reset.md 4.7). See <see cref="IDrainageHeightSource"/> for the natural-height contract.
/// </summary>
public sealed class CpuDrainageHeightSource : IDrainageHeightSource
{
    private readonly TerrainGenerationParameters _recipe;
    private readonly DrainageClimateInputs? _climate;
    private readonly int _workers;

    public string Name => "cpu";

    /// <param name="climate">Null leaves moisture at 0.</param>
    public CpuDrainageHeightSource(TerrainGenerationParameters recipe, DrainageClimateInputs? climate, int workers)
    {
        _recipe = recipe;
        _climate = climate;
        _workers = Math.Max(1, workers);
    }

    public DrainageGrid SampleGrid(DrainageGridSpec spec, IProgress<double>? progress, CancellationToken cancellation)
    {
        var grid = new DrainageGrid(spec);
        int cols = spec.Columns, rows = spec.Rows, nextRow = -1, done = 0;
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, _ =>
        {
            using var model = new TerrainGenerationHeightModel(_recipe, false);
            using IDrainageMoistureReader? moisture = _climate?.CreateReader();
            int row;
            while (!cancellation.IsCancellationRequested && (row = Interlocked.Increment(ref nextRow)) < rows)
            {
                for (int c = 0; c < cols; c++)
                {
                    int cell = row * cols + c;
                    float best = float.PositiveInfinity;
                    int bestIndex = 0;
                    for (int j = 0; j < DrainageGridSpec.SamplesPerAxis; j++)
                    for (int i = 0; i < DrainageGridSpec.SamplesPerAxis; i++)
                    {
                        (long around, int across) = spec.Sample(c, row, i, j);
                        float h = (float)model.CalculateHeight(around, across);
                        if (h < best) { best = h; bestIndex = j * DrainageGridSpec.SamplesPerAxis + i; }
                    }
                    grid.Lowest[cell] = best;
                    grid.LowestIndex[cell] = (byte)bestIndex;
                    if (moisture != null)
                    {
                        (long around, int across) = spec.Centre(c, row);
                        grid.Moisture[cell] = moisture.Moisture(around, across);
                    }
                }
                progress?.Report((double)Interlocked.Increment(ref done) / rows);
            }
        });
        cancellation.ThrowIfCancellationRequested();
        return grid;
    }

    public void SamplePoints(ReadOnlySpan<DrainagePoint> positions, Span<float> heights)
    {
        if (heights.Length < positions.Length) throw new ArgumentException("heights is shorter than positions");
        const int chunk = 16384;
        if (positions.Length <= chunk || _workers == 1)
        {
            using var single = new TerrainGenerationHeightModel(_recipe, false);
            for (int i = 0; i < positions.Length; i++) heights[i] = (float)single.CalculateHeight(positions[i].Around, positions[i].Across);
            return;
        }
        DrainagePoint[] copy = positions.ToArray();
        var result = new float[copy.Length];
        int chunks = (copy.Length + chunk - 1) / chunk, next = -1;
        Parallel.For(0, _workers, new ParallelOptions { MaxDegreeOfParallelism = _workers }, _ =>
        {
            using var model = new TerrainGenerationHeightModel(_recipe, false);
            int c;
            while ((c = Interlocked.Increment(ref next)) < chunks)
                for (int i = c * chunk, end = Math.Min(copy.Length, i + chunk); i < end; i++) result[i] = (float)model.CalculateHeight(copy[i].Around, copy[i].Across);
        });
        result.AsSpan().CopyTo(heights);
    }
}
