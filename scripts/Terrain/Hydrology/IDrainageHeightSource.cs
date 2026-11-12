using System;
using System.Threading;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>A natural-height query position: around in [0, circumference) integer metres, across integer metres.</summary>
public readonly record struct DrainagePoint(long Around, int Across);

/// <summary>
/// Height source of the drainage bake (docs/t2-river-reset.md 4.2). Two implementations read the same recipe: the GPU one (the game) and the CPU one
/// (the deterministic reference for headless tests).
/// <para><b>Contract (T7, F31 c):</b> this source returns the NATURAL height of the recipe's height model, with the emitter-site operator off, no carve and
/// no relief term added by any later generation. Rivers, lakes and every later relief step are applied after it, in the shaped model, so the drainage
/// graph never depends on its own output. A later generation must not add a term here.</para>
/// </summary>
public interface IDrainageHeightSource
{
    string Name { get; }
    /// <summary>Fills the grid: per cell the lowest of 4 x 4 samples, its index and the centre moisture. The result does not depend on chunking or worker count.</summary>
    DrainageGrid SampleGrid(DrainageGridSpec spec, IProgress<double>? progress, CancellationToken cancellation);
    /// <summary>One natural height per point; <paramref name="heights"/> must be at least as long as <paramref name="positions"/>.</summary>
    void SamplePoints(ReadOnlySpan<DrainagePoint> positions, Span<float> heights);
}
