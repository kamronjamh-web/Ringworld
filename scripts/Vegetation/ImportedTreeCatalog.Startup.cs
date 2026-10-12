using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Loading;

namespace RingworldTechDemo.Vegetation;

public static partial class ImportedTreeCatalog
{
    /// <summary>Poll and advance on the main thread; disposal frees unattached source scenes.</summary>
    public static IEnumerable<bool> PrepareCooperatively()
    {
        if (_variants != null) yield break;
        using var claims = new RingThreadedResourceClaims();
        var paths = new List<string>();
        foreach (TreeSpecies species in OrderedSpecies)
            paths.Add($"res://resources/trees/lolipop/{species.ToString().ToLowerInvariant()}/scene.gltf");
        paths.Add("res://materials/imported_tree.gdshader");
        foreach (string path in paths)
        {
            Error error = claims.Request(path);
            if (error != Error.Ok) throw new InvalidOperationException($"Could not request tree resource {path}: {error}.");
        }
        while (true)
        {
            bool ready = true;
            foreach (string path in paths)
            {
                ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(path);
                if (status is ResourceLoader.ThreadLoadStatus.Failed or ResourceLoader.ThreadLoadStatus.InvalidResource)
                    throw new InvalidOperationException($"Could not load tree resource {path}: {status}.");
                ready &= status == ResourceLoader.ThreadLoadStatus.Loaded;
            }
            if (ready) break;
            yield return true;
        }
        // Consume only completed requests. BakeLod's existing shader load then
        // resolves the cached shader without introducing disk IO during baking.
        _ = claims.Take(paths[^1]);
        var variants = new List<ImportedTreeVariant>(VariantCount);
        for (int speciesIndex = 0; speciesIndex < OrderedSpecies.Length; speciesIndex++)
        {
            TreeSpecies species = OrderedSpecies[speciesIndex];
            PackedScene packed = claims.Take(paths[speciesIndex]) as PackedScene
                ?? throw new InvalidOperationException($"Missing loaded {species} tree scene.");
            Node root = packed.Instantiate();
            try
            {
                var anchors = new Dictionary<string, Node3D>(StringComparer.Ordinal);
                FindLodAnchors(root, anchors);
                var names = new List<string>();
                foreach ((string key, Node3D _) in anchors)
                    if (key.EndsWith(":0", StringComparison.Ordinal)) names.Add(key[..^2]);
                names.Sort(StringComparer.Ordinal);
                int expected = species == TreeSpecies.Maple ? 12 : species == TreeSpecies.Fir ? 3 : 15;
                if (names.Count != expected) throw new InvalidOperationException($"{species} has {names.Count} authored variants, expected {expected}.");
                yield return true;
                foreach (string name in names)
                {
                    Node3D lod0 = RequireAnchor(anchors, name, 0);
                    Bounds bounds = GetBounds(lod0, TransformWithoutLayoutTranslation(lod0));
                    float height = bounds.Max.Y - bounds.Min.Y;
                    float width = Mathf.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Z - bounds.Min.Z);
                    if (height <= Mathf.Epsilon || width <= Mathf.Epsilon) throw new InvalidOperationException($"{name} LOD0 has invalid bounds.");
                    var lods = new Mesh[4];
                    var materials = new List<ShaderMaterial>();
                    for (int lod = 0; lod < lods.Length; lod++)
                    {
                        lods[lod] = BakeLod(RequireAnchor(anchors, name, lod), bounds.Min.Y, width, height, materials, species);
                        yield return true;
                    }
                    float trunkHeight = height * (species == TreeSpecies.Maple ? .43f : .56f);
                    float trunkRadius = Mathf.Max(.08f, width * (species == TreeSpecies.Maple ? .035f : .025f));
                    variants.Add(new ImportedTreeVariant(variants.Count, species, name, lods, materials.AsReadOnly(), width, height, trunkRadius, trunkHeight));
                }
            }
            finally { root.Free(); }
        }
        if (variants.Count != VariantCount) throw new InvalidOperationException($"Imported tree catalog contains {variants.Count}, expected {VariantCount} variants.");
        _variants ??= variants.AsReadOnly();
    }
}
