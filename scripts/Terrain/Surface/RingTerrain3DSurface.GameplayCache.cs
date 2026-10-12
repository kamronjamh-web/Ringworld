using System;
using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Copy-only startup cache export for the first adjacent gameplay window.</summary>
public partial class RingTerrain3DSurface
{
    /// <summary>
    /// Returns the initial gameplay child's canonical baseline and packed biome
    /// samples. The result is detached from renderer-owned arrays and is valid
    /// only immediately after synchronous gameplay initialization.
    /// </summary>
    public bool TryGetInitialGameplayCache(out RingSurfaceFrame frame, out float[] heights, out byte[] biomes)
    {
        if (_activePreparedSurface != null)
            return _activePreparedSurface.TryGetInitialGameplayCache(out frame, out heights, out biomes);

        frame = _frame;
        heights = Array.Empty<float>();
        biomes = Array.Empty<byte>();
        if (!_isBudgetedPreparation || _baselineWindowHeights == null || _biomeWeights == null
            || _baselineWindowHeights.GetLength(0) != PreparedWindowSide || _baselineWindowHeights.GetLength(1) != PreparedWindowSide
            || _biomeWeights.GetLength(0) != PreparedWindowSide || _biomeWeights.GetLength(1) != PreparedWindowSide)
        {
            return false;
        }

        heights = new float[PreparedWindowSide * PreparedWindowSide];
        biomes = new byte[PreparedWindowSide * PreparedWindowSide * 4];
        for (int z = 0; z < PreparedWindowSide; z++)
        for (int x = 0; x < PreparedWindowSide; x++)
        {
            int sample = z * PreparedWindowSide + x;
            heights[sample] = _baselineWindowHeights[x, z];
            Color color = _biomeWeights[x, z];
            int offset = sample * 4;
            biomes[offset] = ToByte(color.R);
            biomes[offset + 1] = ToByte(color.G);
            biomes[offset + 2] = ToByte(color.B);
            biomes[offset + 3] = ToByte(color.A);
        }
        return true;
    }

    private static byte ToByte(float value) => RingTerrain3DGameplayStream.QuantizeBiomeChannel(value);

    internal static Image CreateBiomeWeightImage(Color[,] weights, int startX, int startZ, int side)
    {
        var bytes = new byte[side * side * 4];
        for (int row = 0; row < side; row++)
        for (int column = 0; column < side; column++)
        {
            Color color = weights[startX + column, startZ + row];
            int offset = (row * side + column) * 4;
            bytes[offset] = ToByte(color.R); bytes[offset + 1] = ToByte(color.G);
            bytes[offset + 2] = ToByte(color.B); bytes[offset + 3] = ToByte(color.A);
        }
        return Image.CreateFromData(side, side, false, Image.Format.Rgba8, bytes);
    }
}
