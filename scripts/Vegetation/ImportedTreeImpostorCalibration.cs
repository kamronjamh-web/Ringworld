using System;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>TR-7: per-variant alpha cutoffs that make the covered area of the shared cards (at 325 m) and of the far quad (at 900 m) match the authored LOD2 mesh at 325 m. Version 3: cutoffs for
/// the cards and their own cutoffs for the far quad (version 2 had the cards' only; the RGB multiplier of version 1 is gone, the baked atlas carries the colour). The catalog builds each tier's
/// alpha mips for its cutoff. Made by the calibration mode of tree_billboard_match_gpu_test and checked in as a plain Resource whose metadata holds the arrays; a missing resource, another
/// version, variant count or fingerprint (different base-level atlas) is stale and the catalog falls back to the authored cutoffs.</summary>
public sealed class ImportedTreeImpostorCalibration
{
    public const string ResourcePath = "res://resources/trees/prepared/lolipop_impostor_calibration.res";
    public const int Version = 3;
    /// <summary>The crossed cards' cutoff per variant.</summary>
    public float[] Cutoffs { get; }
    /// <summary>The far quad's cutoff per variant.</summary>
    public float[] QuadCutoffs { get; }
    public int VariantCount => Cutoffs.Length;

    public ImportedTreeImpostorCalibration(float[] cutoffs, float[] quadCutoffs)
    {
        Cutoffs = cutoffs; QuadCutoffs = quadCutoffs;
    }

    /// <summary>Null when the resource is missing, malformed, from another version or made for another atlas (fingerprint) or variant count.</summary>
    public static ImportedTreeImpostorCalibration? Load(string fingerprint, int variantCount)
    {
        if (!ResourceLoader.Exists(ResourcePath)) return null;
        Resource? resource = ResourceLoader.Load<Resource>(ResourcePath, null, ResourceLoader.CacheMode.Ignore);
        if (resource == null || !resource.HasMeta("version") || !resource.HasMeta("fingerprint") || !resource.HasMeta("cutoffs") || !resource.HasMeta("quad_cutoffs")) return null;
        if (resource.GetMeta("version").AsInt32() != Version || resource.GetMeta("fingerprint").AsString() != fingerprint) return null;
        float[] cutoffs = resource.GetMeta("cutoffs").AsFloat32Array();
        float[] quadCutoffs = resource.GetMeta("quad_cutoffs").AsFloat32Array();
        if (cutoffs.Length != variantCount || quadCutoffs.Length != variantCount) return null;
        foreach (float value in cutoffs) if (!float.IsFinite(value) || value <= 0f || value >= 1f) return null;
        foreach (float value in quadCutoffs) if (!float.IsFinite(value) || value <= 0f || value >= 1f) return null;
        return new ImportedTreeImpostorCalibration(cutoffs, quadCutoffs);
    }

    public Error Save(string fingerprint)
    {
        var resource = new Resource();
        resource.SetMeta("version", Version);
        resource.SetMeta("fingerprint", fingerprint);
        resource.SetMeta("cutoffs", Cutoffs);
        resource.SetMeta("quad_cutoffs", QuadCutoffs);
        return ResourceSaver.Save(resource, ResourcePath);
    }
}
