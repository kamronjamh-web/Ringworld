using System;
using Godot;

namespace RingworldTechDemo.Vegetation.Canopy;

/// <summary>
/// Crown ellipsoid of one catalog variant (forest lighting, docs/forest-lighting-plan.md sections 2 to 4).
/// SPACE: catalog-normalized tree space, the same space as the baked LOD meshes and as row 4 of the impostor's variant_parameters
/// (width, bottom y, top y): the root is the origin, +Y up, and every axis is divided by the authored LOD0 height, so the tree is 1 unit tall.
/// A placed tree is that mesh scaled uniformly by the instance height in metres (CachedTree.Height), so
/// metres = value * instanceHeight. The authored-metre form of the same numbers is value * <see cref="HeightMeters"/>.
/// The ellipsoid is inscribed in the bounds of the foliage surfaces (alpha-cutout, tree_foliage_mode 1) of LOD0: centre = bounds centre,
/// vertical semi-axis = half the Y extent, horizontal semi-axis = mean of the half X and half Z extents.
/// </summary>
/// <param name="CentreX">Bounds centre x (normalized); the crown axis is at 0 for a tree whose foliage is centred on its trunk.</param>
/// <param name="CentreY">Crown centre height (normalized).</param>
/// <param name="CentreZ">Bounds centre z (normalized).</param>
/// <param name="RadiusHorizontal">Horizontal semi-axis (normalized).</param>
/// <param name="RadiusVertical">Vertical semi-axis (normalized).</param>
/// <param name="Porosity">Crown porosity p: sky gap fraction through one crown (0.20 broadleaf, 0.30 conifer).</param>
/// <param name="Kappa">Crown extinction per normalized unit: -ln(p) / (4/3 * r_mean), r_mean the mean of the three semi-axes. Per metre of a placed tree: Kappa / instanceHeight.</param>
/// <param name="KSpecies">Translucency T/R ratio in green (maple 0.9; fir, pine 0.35).</param>
/// <param name="HeightMeters">Authored LOD0 height in metres (the divisor of the normalization).</param>
public readonly record struct TreeCrownHull(int Variant, TreeSpecies Species, float CentreX, float CentreY, float CentreZ,
    float RadiusHorizontal, float RadiusVertical, float Porosity, float Kappa, float KSpecies, float HeightMeters)
{
    public float RadiusMean => (2f * RadiusHorizontal + RadiusVertical) / 3f;
    /// <summary>Crown base height (normalized).</summary>
    public float Base => CentreY - RadiusVertical;
    /// <summary>Crown top height (normalized).</summary>
    public float Top => CentreY + RadiusVertical;
    /// <summary>Raster channel A factor (1 broadleaf, 0.6 conifer).</summary>
    public float SpeciesFactor => TreeCrownHullTable.SpeciesFactor(Species);
    public float KappaPerMeter(float instanceHeightMeters) => Kappa / MathF.Max(0.01f, instanceHeightMeters);
}

/// <summary>Per-variant crown hulls and the species constants of the forest lighting plan. The numeric helpers are pure; the table itself is built lazily from the loaded catalog.</summary>
public static class TreeCrownHullTable
{
    private static TreeCrownHull[]? _table;

    public static float Porosity(TreeSpecies species) => species == TreeSpecies.Maple ? 0.20f : 0.30f;
    public static float KSpecies(TreeSpecies species) => species == TreeSpecies.Maple ? 0.9f : 0.35f;
    public static float SpeciesFactor(TreeSpecies species) => species == TreeSpecies.Maple ? 1.0f : 0.6f;
    public static float Kappa(float porosity, float meanRadius) => -MathF.Log(porosity) / (4f / 3f * MathF.Max(1e-4f, meanRadius));

    /// <summary>The hull from foliage bounds (normalized space).</summary>
    public static TreeCrownHull FromBounds(int variant, TreeSpecies species, Vector3 min, Vector3 max, float heightMeters)
    {
        float rv = MathF.Max(1e-4f, (max.Y - min.Y) * 0.5f);
        float rh = MathF.Max(1e-4f, ((max.X - min.X) * 0.5f + (max.Z - min.Z) * 0.5f) * 0.5f);
        float p = Porosity(species);
        float mean = (2f * rh + rv) / 3f;
        return new TreeCrownHull(variant, species, (min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f, rh, rv, p, Kappa(p, mean), KSpecies(species), heightMeters);
    }

    /// <summary>All 30 hulls, indexed by CachedTree.Variant. Main thread only (reads the catalog's meshes); built once.</summary>
    public static TreeCrownHull[] Get()
    {
        if (_table != null) return _table;
        var variants = ImportedTreeCatalog.Load();
        var table = new TreeCrownHull[variants.Count];
        for (int i = 0; i < variants.Count; i++) table[i] = FromVariant(i, variants[i]);
        _table = table;
        return table;
    }

    /// <summary>The hull of one catalog variant, from the foliage bounds of its authored LOD0.</summary>
    public static TreeCrownHull FromVariant(int index, ImportedTreeVariant variant)
    {
        Mesh lod0 = variant.Lods[0];
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
        Vector3 allMin = min, allMax = max;
        for (int s = 0; s < lod0.GetSurfaceCount(); s++)
        {
            bool foliage = lod0.SurfaceGetMaterial(s) is ShaderMaterial material
                && material.GetShaderParameter("tree_foliage_mode").AsInt32() == 1 && material.GetShaderParameter("use_alpha_cutout").AsBool();
            Vector3[] vertices = lod0.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            foreach (Vector3 v in vertices)
            {
                allMin = allMin.Min(v); allMax = allMax.Max(v);
                if (foliage) { min = min.Min(v); max = max.Max(v); }
            }
        }
        // A variant with no cutout surface (should not occur) falls back to the whole mesh.
        if (float.IsInfinity(min.X)) { min = allMin; max = allMax; }
        return FromBounds(index, variant.Species, min, max, variant.HeightMeters);
    }

    /// <summary>The shader packing (materials/ring_forest_lighting.gdshaderinc): hull = (centre y, horizontal semi-axis, vertical semi-axis, kappa), extra = (centre x, centre z, k_species, 1 = valid).</summary>
    public static void Pack(in TreeCrownHull hull, out Vector4 hullRow, out Vector4 extraRow)
    {
        hullRow = new Vector4(hull.CentreY, hull.RadiusHorizontal, hull.RadiusVertical, hull.Kappa);
        extraRow = new Vector4(hull.CentreX, hull.CentreZ, hull.KSpecies, 1f);
    }

    /// <summary>Test hook: forget the cached table.</summary>
    public static void Reset() => _table = null;
}
