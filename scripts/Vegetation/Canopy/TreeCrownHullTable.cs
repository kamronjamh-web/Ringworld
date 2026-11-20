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
/// <param name="Porosity">Crown porosity p: sky gap fraction through one crown, measured per catalog variant (see <see cref="TreeCrownHullTable.MeasuredPorosity"/>; about 0.38 maple, 0.28 fir, 0.45 pine).</param>
/// <param name="Kappa">Crown extinction per normalized unit: -ln(p_model) / (4/3 * r_mean), with p_model = <see cref="TreeCrownHullTable.CrownModelPorosity"/> (not the measured Porosity), r_mean the mean of the three semi-axes. Per metre of a placed tree: Kappa / instanceHeight.</param>
/// <param name="KSpecies">Translucency T/R ratio in green (maple 0.9; fir, pine 0.35).</param>
/// <param name="HeightMeters">Authored LOD0 height in metres (the divisor of the normalization).</param>
/// <param name="LeafInclination">Leaf area fractions (sum 1) of four inclination classes measured from vertical, each 22.5 degrees wide (centred 11.25, 33.75, 56.25, 78.75 degrees): the area-weighted inclination acos(|n.y|) of the geometric triangle normals of the alpha-cutout surfaces of LOD2. Azimuth is taken as uniform by the shaders.</param>
public readonly record struct TreeCrownHull(int Variant, TreeSpecies Species, float CentreX, float CentreY, float CentreZ,
    float RadiusHorizontal, float RadiusVertical, float Porosity, float Kappa, float KSpecies, float HeightMeters, Vector4 LeafInclination)
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

    /// <summary>Species porosity (shrubs and any index outside <see cref="MeasuredPorosity"/>): the means of the measured canopy variants, rounded (forest variant survey 2026-11-20).</summary>
    public static float Porosity(TreeSpecies species) => species switch { TreeSpecies.Maple => 0.38f, TreeSpecies.Fir => 0.28f, _ => 0.45f };

    /// <summary>
    /// Measured sky gap fraction of catalog variants 0-29: 1 minus the share of the hull's top-view disc covered by LOD0 foliage (alpha-tested at the material cutoff) or bark,
    /// from a software top-down render at 384 px (forest variant survey 2026-11-20, .godot/audit_baseline/common/forest/variant_survey/raw/density.json, view 0, fill_hull_any).
    /// Re-measure when a base variant's LOD0 or its foliage texture changes.
    /// </summary>
    public static readonly float[] MeasuredPorosity =
    {
        0.52f, 0.58f, 0.60f, 0.38f, 0.33f, 0.35f, 0.39f, 0.40f, 0.39f, 0.42f, 0.42f, 0.44f, // maple: saplings 0-2, large 3-5, medium 6-8, small 9-11
        0.25f, 0.32f, 0.32f,                                                                // fir 12-14
        0.42f, 0.47f, 0.46f, 0.49f, 0.48f, 0.48f, 0.44f, 0.44f, 0.47f,                     // pine big 15-17, large 18-20, medium 21-23
        0.48f, 0.50f, 0.47f, 0.55f, 0.55f, 0.57f,                                          // pine saplings 24-26, small 27-29
    };

    /// <summary>The porosity of one hull row: the measured value for catalog variants 0-29, else the species value.</summary>
    public static float PorosityOf(int variant, TreeSpecies species)
        => variant >= 0 && variant < MeasuredPorosity.Length ? MeasuredPorosity[variant] : Porosity(species);
    /// <summary>The porosity behind <see cref="TreeCrownHull.Kappa"/>, the per-fragment crown model of translucency and crown occlusion (FL-2, calibrated so mesh and cards agree): 0.20 broadleaf, 0.30 conifer.
    /// Kept apart from the measured <see cref="PorosityOf"/>, which feeds only the canopy shade raster: with the measured pine gap the pine mesh/card backlit agreement fell outside FL-2's 10 % (0.898).</summary>
    public static float CrownModelPorosity(TreeSpecies species) => species == TreeSpecies.Maple ? 0.20f : 0.30f;
    public static float KSpecies(TreeSpecies species) => species == TreeSpecies.Maple ? 0.9f : 0.35f;
    public static float SpeciesFactor(TreeSpecies species) => species == TreeSpecies.Maple ? 1.0f : 0.6f;
    public static float Kappa(float porosity, float meanRadius) => -MathF.Log(porosity) / (4f / 3f * MathF.Max(1e-4f, meanRadius));

    /// <summary>The hull from foliage bounds (normalized space).</summary>
    public static TreeCrownHull FromBounds(int variant, TreeSpecies species, Vector3 min, Vector3 max, float heightMeters, Vector4? leafInclination = null)
    {
        float rv = MathF.Max(1e-4f, (max.Y - min.Y) * 0.5f);
        float rh = MathF.Max(1e-4f, ((max.X - min.X) * 0.5f + (max.Z - min.Z) * 0.5f) * 0.5f);
        float p = PorosityOf(variant, species);
        float mean = (2f * rh + rv) / 3f;
        return new TreeCrownHull(variant, species, (min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f, rh, rv, p, Kappa(CrownModelPorosity(species), mean), KSpecies(species), heightMeters, leafInclination ?? new Vector4(0.25f, 0.25f, 0.25f, 0.25f));
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
        return FromBounds(index, variant.Species, min, max, variant.HeightMeters, LeafInclinationOf(variant));
    }

    /// <summary>
    /// T6-7: the hull of one shrub (derived forms 43-54, a single foliage surface, normalised space: 1 unit tall): the ellipsoid fitted to the card bounds with the same convention as <see cref="FromBounds"/>,
    /// the porosity and k_species of the source species, and the leaf inclination of the card normals (area-weighted, all triangles). Shrubs are not in the canopy raster; the hull feeds translucency and crown occlusion.
    /// </summary>
    public static TreeCrownHull FromShrub(int index, TreeSpecies species, Mesh lod0)
    {
        Godot.Collections.Array arrays = lod0.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
        foreach (Vector3 v in vertices) { min = min.Min(v); max = max.Max(v); }
        float[] area = new float[4];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
            AddTriangleInclination((b - a).Cross(c - a), area);
        }
        return FromBounds(index, species, min, max, 1f, NormalizeLeafInclination(area));
    }

    /// <summary>The leaf inclination class weights of a variant: from the alpha-cutout surfaces of its LOD2 (LOD0 when LOD2 has none), area-weighted, normalised to sum 1.</summary>
    public static Vector4 LeafInclinationOf(ImportedTreeVariant variant)
    {
        float[] area = new float[4];
        if (!AccumulateLeafInclination(variant.Lods[Math.Min(2, variant.Lods.Length - 1)], area)) AccumulateLeafInclination(variant.Lods[0], area);
        return NormalizeLeafInclination(area);
    }

    /// <summary>Adds the triangle areas of every alpha-cutout surface of the mesh to the four inclination classes; false when the mesh has no cutout surface.</summary>
    private static bool AccumulateLeafInclination(Mesh mesh, float[] area)
    {
        bool any = false;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial material || !material.GetShaderParameter("use_alpha_cutout").AsBool()) continue;
            any = true;
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(s);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Array.Empty<int>() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int count = indices.Length > 0 ? indices.Length : vertices.Length;
            for (int i = 0; i + 2 < count; i += 3)
            {
                Vector3 a = vertices[indices.Length > 0 ? indices[i] : i], b = vertices[indices.Length > 0 ? indices[i + 1] : i + 1], c = vertices[indices.Length > 0 ? indices[i + 2] : i + 2];
                AddTriangleInclination((b - a).Cross(c - a), area);
            }
        }
        return any;
    }

    /// <summary>Adds one triangle (its unnormalised cross-product normal, whose length is twice the area) to the class areas.</summary>
    public static void AddTriangleInclination(Vector3 crossNormal, float[] area)
    {
        float length = crossNormal.Length();
        if (length < 1e-12f) return;
        float inclination = MathF.Acos(Math.Clamp(MathF.Abs(crossNormal.Y) / length, 0f, 1f));
        area[Math.Min((int)MathF.Floor(inclination / (MathF.PI / 8f)), 3)] += 0.5f * length;
    }

    /// <summary>Class areas to weights summing to 1 (uniform when there is no area).</summary>
    public static Vector4 NormalizeLeafInclination(float[] area)
    {
        float total = area[0] + area[1] + area[2] + area[3];
        return total <= 0f ? new Vector4(0.25f, 0.25f, 0.25f, 0.25f) : new Vector4(area[0], area[1], area[2], area[3]) / total;
    }

    /// <summary>The shader packing (materials/ring_forest_lighting.gdshaderinc): hull = (centre y, horizontal semi-axis, vertical semi-axis, kappa), extra = (centre x, centre z, k_species, 1 = valid), lad = the four leaf inclination class weights.</summary>
    public static void Pack(in TreeCrownHull hull, out Vector4 hullRow, out Vector4 extraRow, out Vector4 ladRow)
    {
        hullRow = new Vector4(hull.CentreY, hull.RadiusHorizontal, hull.RadiusVertical, hull.Kappa);
        extraRow = new Vector4(hull.CentreX, hull.CentreZ, hull.KSpecies, 1f);
        ladRow = hull.LeafInclination;
    }

    /// <summary>Test hook: forget the cached table.</summary>
    public static void Reset() => _table = null;
}
