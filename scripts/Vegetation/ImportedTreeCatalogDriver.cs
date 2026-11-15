using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Small GDScript-facing bridge used only by catalog GPU regressions.</summary>
public partial class ImportedTreeCatalogDriver : Node
{
    public Mesh? LoadVariantMesh(int index, int lod)
    {
        var variants = ImportedTreeCatalog.Load();
        return index is >= 0 and < ImportedTreeCatalog.VariantCount && lod is >= 0 and < 4 ? variants[index].Lods[lod] : null;
    }

    public Godot.Collections.Array<ShaderMaterial> GetVariantMaterials(int index)
    {
        var result = new Godot.Collections.Array<ShaderMaterial>();
        var variants = ImportedTreeCatalog.Load();
        if (index is < 0 or >= ImportedTreeCatalog.VariantCount) return result;
        foreach (ShaderMaterial material in variants[index].Materials) result.Add(material);
        return result;
    }

    public ShaderMaterial? LoadVariantLodMaterial(int index, int lod)
    {
        Mesh? mesh = LoadVariantMesh(index, lod);
        return mesh?.GetSurfaceCount() > 0 ? mesh.SurfaceGetMaterial(0) as ShaderMaterial : null;
    }

    public Mesh? LoadSharedImpostorMesh() => ImportedTreeImpostorCatalog.Load().Mesh;

    public ShaderMaterial? LoadSharedImpostorMaterial() => ImportedTreeImpostorCatalog.Load().Material;

    public Mesh? LoadFarQuadMesh() => ImportedTreeImpostorCatalog.Load().FarQuadMesh;

    public ShaderMaterial? LoadFarQuadMaterial() => ImportedTreeImpostorCatalog.Load().FarQuadMaterial;

    public float GetVariantHeight(int index) => ImportedTreeCatalog.Load()[index].HeightMeters;

    // TR-7 calibration bridge (tree_billboard_match_gpu_test). An empty array means neutral: the authored cutoffs.
    public string GetImpostorFingerprint() => ImportedTreeImpostorCatalog.Load().CalibrationFingerprint;

    public bool ImpostorCalibrationLoaded() => ImportedTreeImpostorCatalog.Load().Calibration != null;

    /// <summary>True when the card atlas is TR-7a's bake, false when the pack's pictures are in use (resource missing or stale).</summary>
    public bool ImpostorBakeLoaded() => ImportedTreeImpostorCatalog.Load().Bake != null;

    public string GetImpostorBakeFallbackReason() => ImportedTreeImpostorAtlasBake.FallbackReason;

    /// <summary>The control of the test: false swaps the pack's own card pictures in for the bake, true swaps the bake back. Returns whether the bake is in use afterwards.</summary>
    public bool UseImpostorBake(bool enabled) => ImportedTreeImpostorCatalog.Load().UseBake(enabled);

    public float[] GetImpostorDefaultCutoffs() => new List<float>(ImportedTreeImpostorCatalog.Load().DefaultCutoffs).ToArray();

    public float[] GetImpostorCalibrationCutoffs() => ImportedTreeImpostorCatalog.Load().Calibration?.Cutoffs ?? GetImpostorDefaultCutoffs();

    public float[] GetImpostorCalibrationQuadCutoffs() => ImportedTreeImpostorCatalog.Load().Calibration?.QuadCutoffs ?? GetImpostorDefaultCutoffs();

    /// <summary>Applies the card and far-quad cutoffs (rebuilding the albedo arrays for them); an empty card array means neutral.</summary>
    public void ApplyImpostorCalibration(float[] cards, float[] quads)
    {
        ImportedTreeImpostorCatalog catalog = ImportedTreeImpostorCatalog.Load();
        catalog.ApplyCalibration(cards.Length == 0 ? null : new ImportedTreeImpostorCalibration(cards, quads));
    }

    public int SaveImpostorCalibration(float[] cards, float[] quads) =>
        (int)new ImportedTreeImpostorCalibration(cards, quads).Save(ImportedTreeImpostorCatalog.Load().CalibrationFingerprint);

    /// <summary>Pixels that differ from the background colour: [count, mean linear R, G, B, mean linear luminance].</summary>
    public float[] MeasureForeground(Image image, Color background)
    {
        double count = 0, r = 0, g = 0, b = 0;
        for (int y = 0; y < image.GetHeight(); y++)
            for (int x = 0; x < image.GetWidth(); x++)
            {
                Color pixel = image.GetPixel(x, y);
                if (Mathf.Abs(pixel.R - background.R) + Mathf.Abs(pixel.G - background.G) + Mathf.Abs(pixel.B - background.B) <= .03f) continue;
                Color linear = pixel.SrgbToLinear();
                count++; r += linear.R; g += linear.G; b += linear.B;
            }
        if (count == 0) return new float[5];
        return new[] { (float)count, (float)(r / count), (float)(g / count), (float)(b / count), (float)((.2126 * r + .7152 * g + .0722 * b) / count) };
    }
}
