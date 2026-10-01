using System;
using Godot;
using GodotArray = Godot.Collections.Array;
using GodotDictionary = Godot.Collections.Dictionary;

namespace RingworldTechDemo.Terrain.Generation;

/// <summary>
/// Bulk-only worker bridge for the bounded 128 m terrain tiles and their 3 by 3
/// RF payload. It deliberately owns no terrain state or model parameters.
/// </summary>
public partial class TerrainGenerationPacking : RefCounted
{
    public string LastError { get; private set; } = string.Empty;
    public bool WasCancelled { get; private set; }

    /// <summary>
    /// Converts the sixteen ascending source strips into one canonical tile. Arrays
    /// cross the GDScript boundary by value, so returning one complete buffer avoids
    /// relying on an in-place mutation of a marshalled packed array.
    /// </summary>
    public float[] ComposeSampledTile(
        float[] stripSamples, GodotDictionary edits, int around, int across, int direction, int circumference, GodotObject cancellationToken)
    {
        Reset();
        if (direction is not (-1 or 1) || circumference <= 0 || stripSamples.Length != TileSide * TileSide)
            return FailFloats("Invalid sampled tile.");

        var destination = new float[TileSide * TileSide];
        for (int row = 0; row < TileSide; row++)
        {
            if ((row & 7) == 0 && IsCancelled(cancellationToken)) return Array.Empty<float>();
            int stripStart = row & ~7;
            int localRow = row - stripStart;
            int sourceRow = direction > 0 ? localRow : 7 - localRow;
            int canonicalAcross = across + direction * row * SpacingMeters;
            int sourceOffset = (stripStart + sourceRow) * TileSide;
            int destinationOffset = row * TileSide;
            for (int column = 0; column < TileSide; column++)
            {
                int canonicalAround = PositiveModulo(around + column * SpacingMeters, circumference);
                Variant key = new Vector2I(canonicalAround, canonicalAcross);
                float value = stripSamples[sourceOffset + column];
                if (edits.TryGetValue(key, out Variant edit))
                {
                    if (edit.VariantType is not (Variant.Type.Int or Variant.Type.Float))
                        return FailFloats("Invalid terrain edit.");
                    value = (float)edit.AsDouble();
                }
                if (!float.IsFinite(value) || value < MinHeight || value > MaxHeight)
                    return FailFloats("Invalid sampled height.");
                destination[destinationOffset + column] = value;
            }
        }
        return destination;
    }

    public float[] AssembleAbsoluteWindow(GodotArray tiles, GodotObject cancellationToken)
    {
        Reset();
        if (tiles.Count != RegionCount * RegionCount)
            return FailFloats("Invalid tile count.");

        var heights = new float[WindowSide * WindowSide];
        for (int regionZ = 0; regionZ < RegionCount; regionZ++)
        {
            for (int regionX = 0; regionX < RegionCount; regionX++)
            {
                float[] tile;
                try { tile = tiles[regionZ * RegionCount + regionX].As<float[]>(); }
                catch (Exception) { return FailFloats("Invalid tile."); }
                if (tile.Length != TileSide * TileSide) return FailFloats("Invalid tile.");
                for (int row = 0; row < TileSide; row++)
                {
                    if ((row & 7) == 0 && IsCancelled(cancellationToken)) return Array.Empty<float>();
                    int sourceOffset = row * TileSide;
                    int destinationOffset = (regionZ * TileSide + row) * WindowSide + regionX * TileSide;
                    for (int column = 0; column < TileSide; column++)
                        heights[destinationOffset + column] = tile[sourceOffset + column];
                }
            }
        }
        return heights;
    }

    /// <summary>Validates absolute float32 samples then makes z-major/x-major RF tiles.</summary>
    public Godot.Collections.Array<Image> PackRelativeImages(float[] absoluteHeights, double frameHeight, GodotObject cancellationToken)
    {
        Reset();
        var images = new Godot.Collections.Array<Image>();
        if (absoluteHeights.Length != WindowSide * WindowSide || !double.IsFinite(frameHeight) || frameHeight < MinHeight || frameHeight > MaxHeight)
            return images;
        for (int row = 0; row < WindowSide; row++)
        {
            if ((row & 7) == 0 && IsCancelled(cancellationToken)) return new Godot.Collections.Array<Image>();
            int offset = row * WindowSide;
            for (int column = 0; column < WindowSide; column++)
            {
                float height = absoluteHeights[offset + column];
                if (!float.IsFinite(height) || height < MinHeight || height > MaxHeight) return new Godot.Collections.Array<Image>();
            }
        }
        for (int regionZ = 0; regionZ < RegionCount; regionZ++)
        for (int regionX = 0; regionX < RegionCount; regionX++)
        {
            var relative = new float[TileSide * TileSide];
            for (int row = 0; row < TileSide; row++)
            {
                if ((row & 7) == 0 && IsCancelled(cancellationToken)) return new Godot.Collections.Array<Image>();
                int sourceOffset = (regionZ * TileSide + row) * WindowSide + regionX * TileSide;
                int destinationOffset = row * TileSide;
                for (int column = 0; column < TileSide; column++)
                    relative[destinationOffset + column] = (float)((double)absoluteHeights[sourceOffset + column] - frameHeight);
            }
            var bytes = new byte[relative.Length * sizeof(float)];
            Buffer.BlockCopy(relative, 0, bytes, 0, bytes.Length);
            Image image = Image.CreateFromData(TileSide, TileSide, false, Image.Format.Rf, bytes);
            if (image is null || image.IsEmpty()) return new Godot.Collections.Array<Image>();
            images.Add(image);
        }
        return images;
    }

    private void Reset() { LastError = string.Empty; WasCancelled = false; }
    private float[] FailFloats(string error) { LastError = error; return Array.Empty<float>(); }
    private bool IsCancelled(GodotObject? cancellationToken)
    {
        if (cancellationToken is null) return false;
        if (cancellationToken.Call("is_cancelled").AsBool()) { WasCancelled = true; return true; }
        return false;
    }
    private const int TileSide = 128, RegionCount = 3, WindowSide = TileSide * RegionCount, SpacingMeters = 2;
    private const float MinHeight = -8000f, MaxHeight = 8000f;
    private static int PositiveModulo(int value, int modulus) { int remainder = value % modulus; return remainder < 0 ? remainder + modulus : remainder; }
}
