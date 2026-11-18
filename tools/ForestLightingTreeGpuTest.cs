using System;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Canopy;

namespace RingworldTechDemo.Tools;

/// <summary>
/// FL-2 (docs/forest-lighting-plan.md sections 3 and 4): C# side of forest_lighting_tree_gpu_test.gd. Hands the GDScript fixture the crown hull rows of a variant (the same table that feeds
/// the mesh renderers' instance uniforms and the impostor's variant_parameters rows 8 and 9) and the mean linear foliage albedo of a variant's alpha-covered texels.
/// </summary>
public partial class ForestLightingTreeGpuTest : RefCounted
{
    /// <summary>[hull row, extra row] of the variant as the shaders read them.</summary>
    public Vector4[] GetHullRows(int variant)
    {
        TreeCrownHullTable.Pack(TreeCrownHullTable.Get()[variant], out Vector4 hull, out Vector4 extra);
        return new[] { hull, extra };
    }

    /// <summary>The variant's species name (maple, fir or pine).</summary>
    public string GetSpecies(int variant) => ImportedTreeCatalog.Load()[variant].Species.ToString().ToLowerInvariant();

    /// <summary>
    /// Mean linear albedo (texture only, no tint) of the texels whose alpha reaches the material's cutoff, over every foliage surface (alpha cutout) of the variant:
    /// [r, g, b, covered texel fraction, tint r, tint g, tint b].
    /// </summary>
    public float[] GetFoliageAlbedoMean(int variant)
    {
        double r = 0, g = 0, b = 0, covered = 0, total = 0;
        Color tint = Colors.White;
        foreach (ShaderMaterial material in ImportedTreeCatalog.Load()[variant].Materials)
        {
            if (!material.GetShaderParameter("use_alpha_cutout").AsBool()) continue;
            if (material.GetShaderParameter("albedo_texture").AsGodotObject() is not Texture2D texture) continue;
            Image? image = texture.GetImage();
            if (image == null || image.IsEmpty()) continue;
            if (image.IsCompressed()) image.Decompress();
            float cutoff = material.GetShaderParameter("alpha_cutoff").AsSingle();
            tint = material.GetShaderParameter("albedo_tint").AsColor();
            for (int y = 0; y < image.GetHeight(); y++)
                for (int x = 0; x < image.GetWidth(); x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    total++;
                    if (pixel.A < cutoff) continue;
                    Color linear = pixel.SrgbToLinear();
                    covered++; r += linear.R; g += linear.G; b += linear.B;
                }
        }
        if (covered == 0) return new float[7];
        return new[] { (float)(r / covered), (float)(g / covered), (float)(b / covered), (float)(covered / Math.Max(1.0, total)), tint.R, tint.G, tint.B };
    }
}
