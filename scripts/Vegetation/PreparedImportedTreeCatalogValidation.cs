using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Startup validation deliberately reads resource metadata only, never geometry arrays or texture pixels.</summary>
public static class PreparedImportedTreeCatalogValidation
{
    public static IReadOnlyList<ImportedTreeVariant> Hydrate(PreparedImportedTreeCatalog resource,
        string engineIdentity, string implementationIdentity, string? sourceFingerprint)
    {
        Require(resource.SchemaVersion == PreparedImportedTreeCatalog.CurrentSchemaVersion, "unsupported schema");
        Require(resource.BakeVersion == PreparedImportedTreeCatalog.CurrentBakeVersion, "unsupported bake version");
        Require(resource.CatalogVersion == ImportedTreeCatalog.CatalogVersion, "placement catalog version mismatch");
        Require(!string.IsNullOrEmpty(implementationIdentity) && resource.ImplementationIdentity == implementationIdentity, "implementation identity mismatch");
        Require(resource.EngineIdentity == engineIdentity, "engine identity mismatch");
        Require(resource.SourceFingerprint.Length == 64 && resource.FingerprintInputs.Length >= 9, "missing source provenance");
        Require(sourceFingerprint == null || resource.SourceFingerprint == sourceFingerprint, "authoring inputs changed; force reimport and rebuild prepared trees");
        Require(resource.VariantNames.Length == ImportedTreeCatalog.VariantCount
            && resource.SpeciesIds.Length == ImportedTreeCatalog.VariantCount
            && resource.VariantIndices.Length == ImportedTreeCatalog.VariantCount
            && resource.Dimensions.Count == ImportedTreeCatalog.VariantCount
            && resource.Meshes.Count == ImportedTreeCatalog.VariantCount * PreparedImportedTreeCatalog.LodsPerVariant, "catalog dimensions");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var variants = new List<ImportedTreeVariant>(ImportedTreeCatalog.VariantCount);
        for (int index = 0; index < ImportedTreeCatalog.VariantCount; index++)
        {
            TreeSpecies species = index < 12 ? TreeSpecies.Maple : index < 15 ? TreeSpecies.Fir : TreeSpecies.Pine;
            string name = resource.VariantNames[index];
            Require(resource.VariantIndices[index] == index && resource.SpeciesIds[index] == (int)species, $"variant{index} order/species");
            Require(!string.IsNullOrEmpty(name) && names.Add(name), $"variant{index} name");
            if (index > 0 && resource.SpeciesIds[index - 1] == (int)species)
                Require(string.CompareOrdinal(resource.VariantNames[index - 1], name) < 0, $"variant{index} authored ordering");
            Vector4 dimensions = resource.Dimensions[index];
            Require(dimensions.IsFinite() && dimensions.X > 0 && dimensions.Y > 0 && dimensions.Z > 0 && dimensions.W > 0, $"variant{index} physical metadata");
            var meshes = new Mesh[PreparedImportedTreeCatalog.LodsPerVariant];
            var materials = new List<ShaderMaterial>();
            for (int lod = 0; lod < meshes.Length; lod++)
            {
                ArrayMesh mesh = resource.Meshes[index * meshes.Length + lod]
                    ?? throw new InvalidOperationException($"Prepared tree catalog: variant{index} LOD{lod} missing mesh");
                Require(mesh.GetSurfaceCount() > 0, $"variant{index} LOD{lod} missing surfaces");
                Aabb bounds = mesh.GetAabb();
                Require(bounds.Position.IsFinite() && bounds.Size.IsFinite() && bounds.Size.Y > 0, $"variant{index} LOD{lod} bounds");
                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                {
                    Require(mesh.SurfaceGetMaterial(surface) is ShaderMaterial, $"variant{index} LOD{lod} material");
                    var material = (ShaderMaterial)mesh.SurfaceGetMaterial(surface);
                    Require(material.Shader?.ResourcePath == "res://materials/imported_tree.gdshader", "production shader path");
                    if (material.GetShaderParameter("tree_foliage_mode").AsInt32() == 1)
                    {
                        ulong format = (ulong)mesh.SurfaceGetFormat(surface);
                        Require((format & (ulong)Mesh.ArrayFormat.FormatCustom0) != 0
                            && ((format >> (int)Mesh.ArrayFormat.FormatCustom0Shift) & 7) == (ulong)Mesh.ArrayCustomFormat.RgbaFloat
                            && material.GetShaderParameter("tree_foliage_attachments").AsBool(), "lossless foliage attachment format");
                    }
                    materials.Add(material);
                }
                meshes[lod] = mesh;
            }
            variants.Add(new ImportedTreeVariant(index, species, name, meshes, materials.AsReadOnly(), dimensions.X, dimensions.Y, dimensions.Z, dimensions.W));
        }
        return variants.AsReadOnly();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Prepared tree catalog: " + message);
    }
}
