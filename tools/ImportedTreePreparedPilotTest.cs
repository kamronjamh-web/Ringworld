using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>One-variant serialization feasibility gate; no prepared runtime loader is installed.</summary>
public partial class ImportedTreePreparedPilotTest : RefCounted
{
    public int Run(string path, bool keep)
    {
        string absolute = ProjectSettings.GlobalizePath(path);
        bool created = false;
        try
        {
            string scratchRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(Path.GetFullPath(absolute).StartsWith(scratchRoot, StringComparison.OrdinalIgnoreCase), "pilot path must be under .godot");
            Require(!File.Exists(absolute), "refusing to overwrite a pilot artifact");
            ImportedTreeVariant source = ImportedTreeCatalog.BuildSourceForPreparation()[0];
            Require(source.Species == TreeSpecies.Maple && source.Index == 0, "first authored variant must be Maple");
            var prepared = new PreparedImportedTreeCatalog
            {
                EngineIdentity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity,
                // Empty fingerprint deliberately marks the pilot as unsuitable for production loading.
                VariantNames = new[] { source.Name }, VariantIndices = new[] { source.Index },
                SpeciesIds = new[] { (int)source.Species },
                Dimensions = new() { new Vector4(source.WidthMeters, source.HeightMeters, source.TrunkRadiusMeters, source.TrunkHeightMeters) }
            };
            foreach (Mesh mesh in source.Lods)
            {
                Require(mesh is ArrayMesh, "source must be an ArrayMesh");
                prepared.Meshes.Add((ArrayMesh)mesh);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            Error error = ResourceSaver.Save(prepared, path);
            created = File.Exists(absolute);
            Require(error == Error.Ok, $"resource save: {error}");
            var loaded = ResourceLoader.Load<PreparedImportedTreeCatalog>(path, cacheMode: ResourceLoader.CacheMode.IgnoreDeep)
                ?? throw new InvalidOperationException("prepared resource reload failed");
            Require(loaded.GetInstanceId() != prepared.GetInstanceId(), "reload returned original resource");
            CompareMetadata(prepared, loaded);
            var dependencies = new SortedSet<string>(StringComparer.Ordinal);
            ImportedTreePreparedParity.CheckDependencies(path, new HashSet<string>(StringComparer.Ordinal), dependencies);
            Require(dependencies.Any(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), "no external PNG dependencies");
            Require(dependencies.Contains("res://materials/imported_tree.gdshader"), "no external production shader dependency");
            int surfaces = 0, foliageSurfaces = 0, pinnedVertices = 0;
            var textureClasses = new SortedSet<string>(StringComparer.Ordinal);
            for (int lod = 0; lod < PreparedImportedTreeCatalog.LodsPerVariant; lod++)
            {
                ArrayMesh original = prepared.Meshes[lod], reloaded = loaded.Meshes[lod];
                Require(original.GetInstanceId() != reloaded.GetInstanceId(), $"LOD{lod}: reload reused original mesh");
                ImportedTreePreparedParity.CompareMesh(original, reloaded, lod, textureClasses);
                surfaces += reloaded.GetSurfaceCount();
                ImportedTreePreparedParity.CheckWind(reloaded, ref foliageSurfaces, ref pinnedVertices);
            }
            Require(foliageSurfaces > 0 && pinnedVertices > 0, "pilot did not exercise pinned foliage wind attachments");
            GD.Print($"IMPORTED_TREE_PREPARED_PILOT_DETAIL variant={source.Name} lods={loaded.Meshes.Count} surfaces={surfaces} foliage_surfaces={foliageSurfaces} pinned_vertices={pinnedVertices} texture_classes={string.Join(",", textureClasses)} dependencies={dependencies.Count}");
            if (keep) GD.Print($"IMPORTED_TREE_PREPARED_PILOT_ARTIFACT={path}");
            GD.Print("IMPORTED_TREE_PREPARED_PILOT=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError($"IMPORTED_TREE_PREPARED_PILOT=FAIL {exception}");
            return 1;
        }
        finally
        {
            if (created && !keep && File.Exists(absolute)) File.Delete(absolute);
        }
    }

    private static void CompareMetadata(PreparedImportedTreeCatalog source, PreparedImportedTreeCatalog loaded)
    {
        Require(loaded.SchemaVersion == PreparedImportedTreeCatalog.CurrentSchemaVersion, "schema version");
        Require(loaded.BakeVersion == PreparedImportedTreeCatalog.CurrentBakeVersion, "bake version");
        Require(loaded.CatalogVersion == ImportedTreeCatalog.CatalogVersion, "placement catalog version");
        Require(loaded.EngineIdentity == source.EngineIdentity && loaded.SourceFingerprint == source.SourceFingerprint, "provenance metadata");
        Require(loaded.FingerprintInputs.SequenceEqual(source.FingerprintInputs), "fingerprint input records");
        Require(loaded.VariantNames.SequenceEqual(source.VariantNames), "variant names/order");
        Require(loaded.VariantIndices.SequenceEqual(source.VariantIndices), "variant indices");
        Require(loaded.SpeciesIds.SequenceEqual(source.SpeciesIds), "species IDs");
        Require(loaded.Dimensions.Count == 1 && loaded.Dimensions.SequenceEqual(source.Dimensions), "physical dimensions");
        Require(loaded.Meshes.Count == PreparedImportedTreeCatalog.LodsPerVariant, "four flattened LOD meshes");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
