using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>One derived form (T6-5): a snag, fallen tree or stump. Lods is indexed 0, 1, 2 and 3 (= LODF); unused slots are null (stumps hold LOD0 only; a log's LOD2 is its LOD1 mesh).
/// Materials holds one bark ShaderMaterial, duplicated from the source variant (wind off for logs and stumps; the duplicate does not follow shared celestial updates of the base materials).</summary>
public sealed class DerivedTreeForm
{
    public int Index { get; }
    public TreeFormKind Form { get; }
    public TreeSpecies Species { get; }
    public int SourceBaseVariant { get; }
    public Mesh?[] Lods { get; }
    public IReadOnlyList<ShaderMaterial> Materials { get; }
    /// <summary>Authored metres: snag height, log length (0.55 x tree height) or stump height.</summary>
    public float LengthMeters { get; }
    public float TrunkRadiusMeters { get; }

    internal DerivedTreeForm(int index, TreeFormKind form, TreeSpecies species, int source, Mesh?[] lods, IReadOnlyList<ShaderMaterial> materials, float length, float radius)
    {
        Index = index; Form = form; Species = species; SourceBaseVariant = source; Lods = lods; Materials = materials; LengthMeters = length; TrunkRadiusMeters = radius;
    }
}

/// <summary>Loader for lolipop_derived_catalog.res (indices 30-42). Main thread only, like ImportedTreeCatalog. Load() verifies the resource's source hashes against the live base catalog and throws on a mismatch.
/// Generation 1 never loads this (the fingerprint and the cache key only touch it in worlds that enable vegetation.forest_structure).</summary>
public static class DerivedTreeCatalog
{
    public const int CatalogVersion = 1;
    public const int FormCount = 13;
    public const int FirstIndex = 30;
    public const string ResourcePath = "res://resources/trees/prepared/lolipop_derived_catalog.res";

    private static IReadOnlyList<DerivedTreeForm>? _forms;

    public static IReadOnlyList<DerivedTreeForm> Load()
    {
        if (_forms != null) return _forms;
        IReadOnlyList<ImportedTreeVariant> baseCatalog = ImportedTreeCatalog.Load();
        var resource = ResourceLoader.Load<PreparedDerivedTreeCatalog>(ResourcePath)
            ?? throw new InvalidOperationException("Derived tree catalog missing or unreadable: " + ResourcePath + ". Run tools/prepare_derived_tree_assets.gd.");
        int lods = PreparedDerivedTreeCatalog.LodsPerForm;
        if (resource.SchemaVersion != PreparedDerivedTreeCatalog.CurrentSchemaVersion || resource.CatalogVersion != CatalogVersion)
            throw new InvalidOperationException($"Derived tree catalog schema {resource.SchemaVersion} / version {resource.CatalogVersion} is not supported.");
        int n = resource.FormIndices.Length;
        if (n != FormCount || resource.FormKinds.Length != n || resource.SourceVariants.Length != n || resource.LengthMeters.Length != n || resource.TrunkRadiusMeters.Length != n
            || resource.Meshes.Count != n * lods || resource.SourceHashes.Length != n * lods)
            throw new InvalidOperationException($"Derived tree catalog has {n} forms (expected {FormCount}) or inconsistent arrays.");
        var forms = new List<DerivedTreeForm>(n);
        for (int f = 0; f < n; f++)
        {
            if (resource.FormIndices[f] != FirstIndex + f) throw new InvalidOperationException($"Derived form slot {f} has index {resource.FormIndices[f]}, expected {FirstIndex + f}.");
            ImportedTreeSourceRow row = ImportedTreeSourceTable.Row(resource.FormIndices[f]);
            ImportedTreeVariant source = baseCatalog[resource.SourceVariants[f]];
            if (row.SourceBaseVariant != source.Index || (int)row.Form != resource.FormKinds[f])
                throw new InvalidOperationException($"Derived form {resource.FormIndices[f]} disagrees with the source table.");
            var kind = (TreeFormKind)resource.FormKinds[f];
            for (int lod = 0; lod < lods; lod++)
            {
                string hash = resource.SourceHashes[f * lods + lod];
                if (hash.Length == 0) continue;
                // Slot 3 (LODF) is fitted to the source LOD0; logs' slots 0 and 1 come from the source LOD1 and LOD2.
                int sourceLod = lod == 3 ? 0 : kind == TreeFormKind.FallenTree ? (lod == 0 ? 1 : 2) : lod;
                string live = ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)source.Lods[sourceLod]);
                if (live != hash)
                    throw new InvalidOperationException($"Derived form {resource.FormIndices[f]} slot {lod} was made from a different {source.Name} LOD{sourceLod} (stale). Run tools/prepare_derived_tree_assets.gd.");
            }
            if (source.Lods[0].SurfaceGetMaterial(0) is not ShaderMaterial bark) throw new InvalidOperationException($"{source.Name} bark material missing.");
            var material = (ShaderMaterial)bark.Duplicate(false);
            if (kind != TreeFormKind.Snag) material.SetShaderParameter("vegetation_wind_enabled", false); // wind bends along model +Y: wrong for a lying log or a stump
            var meshes = new Mesh?[lods];
            for (int lod = 0; lod < lods; lod++)
            {
                ArrayMesh? mesh = resource.Meshes[f * lods + lod];
                if (mesh == null) continue;
                if (mesh.GetSurfaceCount() != 1) throw new InvalidOperationException($"Derived form {resource.FormIndices[f]} LOD{lod} has {mesh.GetSurfaceCount()} surfaces.");
                mesh.SurfaceSetMaterial(0, material);
                meshes[lod] = mesh;
            }
            forms.Add(new DerivedTreeForm(resource.FormIndices[f], kind, row.Species, source.Index, meshes, new[] { material }, resource.LengthMeters[f], resource.TrunkRadiusMeters[f]));
        }
        return _forms = forms.AsReadOnly();
    }

    public static DerivedTreeForm Form(int index)
    {
        IReadOnlyList<DerivedTreeForm> forms = Load();
        int slot = index - FirstIndex;
        return slot >= 0 && slot < forms.Count ? forms[slot] : throw new ArgumentOutOfRangeException(nameof(index));
    }
}
