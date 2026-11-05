using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>Stage a fully verified catalog after the separate editor process forces source-scene reimport.</summary>
public partial class PrepareTreeAssets : RefCounted
{
    public int Run(string output, string manifest)
    {
        try
        {
            string root = ProjectSettings.GlobalizePath("res://");
            string artifactPath = ScratchPath(output);
            string manifestPath = ScratchPath(manifest);
            if (File.Exists(artifactPath) || File.Exists(manifestPath)) throw new IOException("Refusing to overwrite staging output.");
            var before = PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(root);
            IReadOnlyList<ImportedTreeVariant> source = ImportedTreeCatalog.BuildSourceForPreparation();
            PreparedImportedTreeCatalog resource = CreateResource(source, before);
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            if (ResourceSaver.Save(resource, output) != Error.Ok) throw new IOException("Failed to save prepared tree staging resource.");
            var loaded = ResourceLoader.Load<PreparedImportedTreeCatalog>(output, cacheMode: ResourceLoader.CacheMode.IgnoreDeep)
                ?? throw new IOException("Failed to reload prepared tree staging resource.");
            Verify(source, loaded, before.Fingerprint);
            var after = PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(root);
            if (after.Fingerprint != before.Fingerprint) throw new IOException("Tree authoring inputs changed during preparation; staging output cannot be published.");
            using var stream = File.OpenRead(artifactPath);
            string artifactHash = Convert.ToHexString(SHA256.HashData(stream));
            var report = new
            {
                schema_version = resource.SchemaVersion, bake_version = resource.BakeVersion,
                catalog_version = resource.CatalogVersion, engine_identity = resource.EngineIdentity,
                implementation_identity = resource.ImplementationIdentity, source_fingerprint = resource.SourceFingerprint,
                artifact_sha256 = artifactHash, artifact_path = "resources/trees/prepared/lolipop_catalog.res",
                parity_verified = true,
                inputs = before.Inputs.Select(record => { int split = record.LastIndexOf('='); return new { path = record[..split], sha256 = record[(split + 1)..] }; }).ToArray()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            GD.Print($"PREPARE_TREE_ASSETS=PASS variants={source.Count} meshes={loaded.Meshes.Count} artifact={output} manifest={manifest}");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARE_TREE_ASSETS=FAIL " + exception);
            return 1;
        }
    }

    public static PreparedImportedTreeCatalog CreateResource(IReadOnlyList<ImportedTreeVariant> source, PreparedTreeSourceIdentity identity)
    {
        string implementation = PreparedImportedTreeCatalogIdentity.ImplementationIdentity;
        if (string.IsNullOrEmpty(implementation)) throw new InvalidOperationException("Build the compiled tree preparation identity before baking assets.");
        var resource = new PreparedImportedTreeCatalog
        {
            EngineIdentity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity, ImplementationIdentity = implementation,
            SourceFingerprint = identity.Fingerprint, FingerprintInputs = identity.Inputs,
            VariantNames = source.Select(v => v.Name).ToArray(), VariantIndices = source.Select(v => v.Index).ToArray(),
            SpeciesIds = source.Select(v => (int)v.Species).ToArray()
        };
        foreach (ImportedTreeVariant variant in source)
        {
            resource.Dimensions.Add(new Vector4(variant.WidthMeters, variant.HeightMeters, variant.TrunkRadiusMeters, variant.TrunkHeightMeters));
            foreach (Mesh mesh in variant.Lods) resource.Meshes.Add(mesh as ArrayMesh ?? throw new InvalidOperationException("Source catalog mesh is not an ArrayMesh."));
        }
        return resource;
    }

    public static void Verify(IReadOnlyList<ImportedTreeVariant> source, PreparedImportedTreeCatalog loaded, string sourceFingerprint)
    {
        var hydrated = PreparedImportedTreeCatalogValidation.Hydrate(loaded, PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity,
            PreparedImportedTreeCatalogIdentity.ImplementationIdentity, sourceFingerprint);
        var textures = new SortedSet<string>(StringComparer.Ordinal);
        int foliage = 0, pinned = 0;
        for (int index = 0; index < source.Count; index++)
        {
            ImportedTreeVariant a = source[index], b = hydrated[index];
            if (a.Name != b.Name || a.Species != b.Species || a.Index != b.Index || a.Materials.Count != b.Materials.Count)
                throw new InvalidOperationException("Variant metadata/material ordering mismatch: " + index);
            ImportedTreePreparedParity.EqualVariant(new Vector4(a.WidthMeters, a.HeightMeters, a.TrunkRadiusMeters, a.TrunkHeightMeters),
                new Vector4(b.WidthMeters, b.HeightMeters, b.TrunkRadiusMeters, b.TrunkHeightMeters), "variant dimensions " + index);
            for (int lod = 0; lod < a.Lods.Length; lod++)
            {
                ImportedTreePreparedParity.CompareMesh((ArrayMesh)a.Lods[lod], (ArrayMesh)b.Lods[lod], lod, textures);
                ImportedTreePreparedParity.CheckWind((ArrayMesh)b.Lods[lod], ref foliage, ref pinned);
            }
        }
        if (foliage == 0 || pinned == 0) throw new InvalidOperationException("Prepared catalog has no pinned foliage attachments.");
        if (!string.IsNullOrEmpty(loaded.ResourcePath))
        {
            var dependencies = new SortedSet<string>(StringComparer.Ordinal);
            ImportedTreePreparedParity.CheckDependencies(loaded.ResourcePath, new HashSet<string>(StringComparer.Ordinal), dependencies);
            if (!dependencies.Any(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Prepared catalog lost external texture dependencies.");
        }
    }

    private static string ScratchPath(string path)
    {
        string absolute = Path.GetFullPath(ProjectSettings.GlobalizePath(path));
        string scratch = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(scratch, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Baker staging paths must be under res://.godot/.");
        return absolute;
    }
}
