using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

internal enum RingGrassCardKind : byte { Card = 0, Low = 1 } // 2 was the retired hero kind; never reuse it (CUSTOM.x encodes kind*4)

/// <summary>Worker-side options of the card placement mode of <see cref="RingGrassTileBuilder.Build"/>. Immutable; part of the placement fingerprint of a Cards layer.</summary>
internal sealed record RingGrassCardPlacement
{
    public const int AtlasVariants = 4;

    /// <summary>Main card lattice density per square metre (lattice cells, jittered, canonical).</summary>
    public float NearDensity { get; init; } = 14f;
    /// <summary>Include the low upright layer in this tile (decided per tier by the caller).</summary>
    public bool IncludeLowLayer { get; init; }
    public float LowLayerDensity { get; init; } = 18f;
    /// <summary>Own cap for the low layer so dropping it is always a buffer suffix.</summary>
    public int LowLayerMaxInstances { get; init; } = 32768;
    public float TallHeight { get; init; } = .6f;
    public float ShortHeight { get; init; } = .3f;
    public float Lean { get; init; } = .12f;
    public float LowHeightMin { get; init; } = .11f;
    public float LowHeightMax { get; init; } = .20f;
    public float LowWidthMin { get; init; } = .30f;
    public float LowWidthMax { get; init; } = .48f;
    /// <summary>Spacing of the Terrain3D vertex grid the biome weights are interpolated from.</summary>
    public float BiomeGridSpacingMeters { get; init; } = 2f;

    /// <summary>Largest horizontal reach of one instance in unit-width local space: card gain 1.6 x compensation 2.5 x 0.5.</summary>
    public const float BoundsHorizontalExtent = 2.0f;
    public const float BoundsHeightScale = 2.5f;
}

/// <summary>Bayer-stratified rank of a lattice cell. Every prefix [0, f) of the rank order is itself a jittered grid.</summary>
internal static class RingGrassCardRank
{
    private static readonly byte[] Bayer16Table = BuildBayer16();

    private static byte[] BuildBayer16()
    {
        var table = new byte[256];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
        {
            int value = 0;
            // Standard recursive Bayer matrix [[4M,4M+2],[4M+3,4M+1]]: the finest coordinate bit is the lowest digit.
            for (int bit = 0; bit < 4; bit++)
            {
                int xb = (x >> bit) & 1, yb = (y >> bit) & 1;
                value = value * 4 + ((xb ^ yb) * 2 + yb);
            }
            table[y * 16 + x] = (byte)value;
        }
        return table;
    }

    public static int Bayer16(long i, long j) => Bayer16Table[(int)(j & 15) * 16 + (int)(i & 15)];

    /// <summary>32-bit rank code: Bayer value in the top byte, 24 hash bits below. Monotone in the code.</summary>
    public static uint Code(long i, long j, int bayerHashLow24) => ((uint)Bayer16(i, j) << 24) | (uint)(bayerHashLow24 & 0xFFFFFF);

    /// <summary>(bayer + hash)/256 with the hash in [0,1): the plan's rank, exact from the code.</summary>
    public static float ToRank(uint code) => Math.Min((float)(code / 4294967296.0), MathF.BitDecrement(1f));
}
