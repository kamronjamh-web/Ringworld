using System;
using System.IO;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>Full source roundtrip plus invalid metadata and prepared-required publication contracts.</summary>
public partial class ImportedTreePreparedCatalogTest : RefCounted
{
    public int RunFallback(string path)
    {
        bool ownsOutput = false;
        try
        {
            RequireNewScratchPath(path);
            ownsOutput = true;
            var identity = PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(ProjectSettings.GlobalizePath("res://"));
            var invalid = new PreparedImportedTreeCatalog
            {
                SchemaVersion = 999, EngineIdentity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity,
                ImplementationIdentity = PreparedImportedTreeCatalogIdentity.ImplementationIdentity,
                SourceFingerprint = new string('0', 64), FingerprintInputs = identity.Inputs
            };
            Require(ResourceSaver.Save(invalid, path) == Error.Ok, "invalid fixture save");
            ImportedTreeCatalog.ConfigureForDiagnostics(ImportedTreeCatalogLoadMode.Automatic, path);
            bool staleRejected = false;
            try { _ = ImportedTreeCatalog.Load(); }
            catch (InvalidOperationException exception) { staleRejected = exception.Message.Contains("force scene reimport", StringComparison.Ordinal); }
            Require(staleRejected && ImportedTreeCatalog.LoadedFrom == "not_loaded" && ImportedTreeCatalog.SourceLodBakeCount == 0,
                "stale inputs plus invalid schema silently fell back to cached source imports");
            invalid.SourceFingerprint = identity.Fingerprint;
            Require(ResourceSaver.Save(invalid, path) == Error.Ok, "fresh invalid fixture save");
            // Reload the overwritten fixture explicitly; the loader's normal cache must see this resource.
            _ = ResourceLoader.Load<PreparedImportedTreeCatalog>(path, cacheMode: ResourceLoader.CacheMode.ReplaceDeep);
            var loaded = ImportedTreeCatalog.Load();
            Require(loaded.Count == 30 && ImportedTreeCatalog.LoadedFrom == "source", "unsupported schema did not use source fallback");
            Require(ImportedTreeCatalog.FallbackReason.Contains("unsupported schema", StringComparison.Ordinal), "fallback diagnostic lost rejection reason");
            Require(ImportedTreeCatalog.SourceSceneInstantiationCount == 3 && ImportedTreeCatalog.SourceLodBakeCount == 120, "source fallback work counters");
            GD.Print("IMPORTED_TREE_PREPARED_FALLBACK=PASS stale_source_rejected=true source_fallback=true");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("IMPORTED_TREE_PREPARED_FALLBACK=FAIL " + exception);
            return 1;
        }
        finally
        {
            string absolute = ProjectSettings.GlobalizePath(path);
            if (ownsOutput && File.Exists(absolute)) File.Delete(absolute);
        }
    }

    public int Run(string path)
    {
        string manifest = path + ".manifest.json";
        bool ownsOutput = false;
        try
        {
            RequireNewScratchPath(path);
            RequireNewScratchPath(manifest);
            ownsOutput = true;
            if (new PrepareTreeAssets().Run(path, manifest) != 0) throw new InvalidOperationException("full catalog bake/parity failed");
            using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(manifest))))
                Require(report.RootElement.GetProperty("parity_verified").GetBoolean(), "manifest parity proof");
            var resource = ResourceLoader.Load<PreparedImportedTreeCatalog>(path, cacheMode: ResourceLoader.CacheMode.IgnoreDeep)
                ?? throw new InvalidOperationException("test resource missing");
            string engine = resource.EngineIdentity, implementation = resource.ImplementationIdentity, source = resource.SourceFingerprint;
            void Reject(string label, Action mutate, Action restore)
            {
                mutate();
                bool rejected = false;
                try { _ = PreparedImportedTreeCatalogValidation.Hydrate(resource, engine, implementation, source); }
                catch (InvalidOperationException) { rejected = true; }
                finally { restore(); }
                Require(rejected, "accepted invalid " + label);
            }
            Reject("schema", () => resource.SchemaVersion++, () => resource.SchemaVersion--);
            Reject("bake version", () => resource.BakeVersion++, () => resource.BakeVersion--);
            Reject("catalog identity", () => resource.CatalogVersion++, () => resource.CatalogVersion--);
            Reject("engine", () => resource.EngineIdentity = "other", () => resource.EngineIdentity = engine);
            Reject("implementation", () => resource.ImplementationIdentity = "other", () => resource.ImplementationIdentity = implementation);
            Reject("source fingerprint", () => resource.SourceFingerprint = new string('0', 64), () => resource.SourceFingerprint = source);
            Vector4 dimensions = resource.Dimensions[0];
            Reject("physical dimensions", () => resource.Dimensions[0] = new Vector4(float.NaN, 1, 1, 1), () => resource.Dimensions[0] = dimensions);
            int species = resource.SpeciesIds[0];
            Reject("species/order", () => resource.SpeciesIds[0] = (int)TreeSpecies.Pine, () => resource.SpeciesIds[0] = species);
            ArrayMesh first = resource.Meshes[0];
            Reject("empty mesh", () => resource.Meshes[0] = new ArrayMesh(), () => resource.Meshes[0] = first);
            ArrayMesh last = resource.Meshes[^1];
            resource.Meshes.RemoveAt(resource.Meshes.Count - 1);
            try
            {
                bool rejected = false;
                try { _ = PreparedImportedTreeCatalogValidation.Hydrate(resource, engine, implementation, source); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "accepted missing LOD");
            }
            finally { resource.Meshes.Add(last); }

            // A .bin-only edit must change the pure authoring digest, regardless of importer cache state.
            var identity = PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(ProjectSettings.GlobalizePath("res://"));
            Require(identity.Fingerprint == source && identity.Inputs.Length >= 9, "fresh source identity");
            Require(System.Array.Exists(identity.Inputs, p => p.Contains("/scene.bin=", StringComparison.Ordinal)), "external geometry participates in identity");
            VerifyBinFingerprint(ProjectSettings.GlobalizePath(path + "_inputs"));
            int sceneBakes = ImportedTreeCatalog.SourceSceneInstantiationCount, lodBakes = ImportedTreeCatalog.SourceLodBakeCount;
            ImportedTreeCatalog.ConfigureForDiagnostics(ImportedTreeCatalogLoadMode.PreparedRequired, path + "_missing.res");
            bool missingRejected = false;
            try { _ = ImportedTreeCatalog.Load(); }
            catch (InvalidOperationException) { missingRejected = true; }
            Require(missingRejected && ImportedTreeCatalog.LoadedFrom == "not_loaded", "missing required artifact published source catalog");
            ImportedTreeCatalog.ConfigureForDiagnostics(ImportedTreeCatalogLoadMode.PreparedRequired, path);
            var loaded = ImportedTreeCatalog.Load();
            Require(ImportedTreeCatalog.LoadedFrom == "prepared", "required prepared route");
            Require(loaded.Count == 30 && ReferenceEquals(loaded, ImportedTreeCatalog.Load()), "shared publication identity");
            Require(ImportedTreeCatalog.SourceSceneInstantiationCount == sceneBakes && ImportedTreeCatalog.SourceLodBakeCount == lodBakes, "prepared load called source baking");
            GD.Print("IMPORTED_TREE_PREPARED_CATALOG=PASS variants=30 meshes=120 source_bakes_during_load=0");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("IMPORTED_TREE_PREPARED_CATALOG=FAIL " + exception);
            return 1;
        }
        finally
        {
            foreach (string file in new[] { path, manifest })
            {
                string absolute = ProjectSettings.GlobalizePath(file);
                if (ownsOutput && File.Exists(absolute)) File.Delete(absolute);
            }
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private static void VerifyBinFingerprint(string scratch)
    {
        // Tiny isolated authoring tree: never edit the game's actual source geometry.
        string root = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(scratch).StartsWith(root, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(scratch), "identity fixture confinement");
        try
        {
            foreach (string species in new[] { "fir", "maple", "pine" })
            {
                string folder = Path.Combine(scratch, "resources", "trees", "lolipop", species);
                Directory.CreateDirectory(Path.Combine(folder, "textures"));
                foreach (string file in new[] { "scene.bin", "scene.gltf", "scene.gltf.import" }) File.WriteAllText(Path.Combine(folder, file), "fixture");
                File.WriteAllText(Path.Combine(folder, "textures", "bark.png.import"), "fixture policy");
            }
            var before = PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(scratch);
            File.AppendAllText(Path.Combine(scratch, "resources", "trees", "lolipop", "maple", "scene.bin"), "changed geometry");
            var after = PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(scratch);
            Require(before.Fingerprint != after.Fingerprint && before.Inputs.Length == after.Inputs.Length, ".bin-only edit did not invalidate source identity");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static void RequireNewScratchPath(string path)
    {
        string root = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string absolute = Path.GetFullPath(ProjectSettings.GlobalizePath(path));
        Require(absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !File.Exists(absolute), "new fixture path must be confined to .godot");
    }
}
