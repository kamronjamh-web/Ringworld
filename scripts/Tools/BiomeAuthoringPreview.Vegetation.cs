using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Biomes;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class BiomeAuthoringPreview
{
    private IEnumerable<int> BuildVegetation(Node3D root, BiomeAuthoringSampler sampler, BiomeAuthoringSnapshot preset)
    {
        const float grassHalf = 24f;
        const int grassLimit = 12000;
        // The authored range is 0..2: map the full range across the bounded
        // candidate budget instead of saturating at density 1.2.
        int grassCount = Mathf.Clamp(Mathf.RoundToInt(preset.GrassDensity * .5f * grassLimit), 0, grassLimit);
        if (grassCount > 0)
        {
            var random = new RandomNumberGenerator { Seed = (ulong)(preset.Seed ^ 0x6A551) };
            var transforms = new List<Transform3D>(grassCount);
            for (int attempt = 0; attempt < grassCount; attempt++)
            {
                float x = random.RandfRange(-grassHalf, grassHalf);
                float z = random.RandfRange(-grassHalf, grassHalf);
                BiomeAuthoringSurfaceSample surface = sampler.Sample(x, z);
                if (!surface.HasSurfaceWater && surface.SlopeDegrees <= 35)
                {
                    float height = preset.GrassHeightMeters * random.RandfRange(.7f, 1.2f);
                    transforms.Add(new Transform3D(Basis.FromScale(new Vector3(.12f, height, .12f)), new Vector3(x, surface.SurfaceHeight, z)));
                }
                if ((attempt & 31) == 0) yield return 0;
            }
            if (transforms.Count > 0)
            {
                var multi = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    InstanceCount = transforms.Count,
                    Mesh = RingGrassMeshFactory.CreateIntermediate()
                };
                for (int i = 0; i < transforms.Count; i++) multi.SetInstanceTransform(i, transforms[i]);
                root.AddChild(new MultiMeshInstance3D
                {
                    Name = "PreviewGrass",
                    Multimesh = multi,
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = preset.GrassTint,
                        Roughness = .9f,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled
                    }
                });
            }
        }
        var variants = new Dictionary<TreeSpecies, IReadOnlyList<ImportedTreeVariant>>();
        foreach (TreeSpecies species in Enum.GetValues<TreeSpecies>()) variants[species] = ImportedTreeCatalog.GetVariants(species);
        // Reservoir selection keeps the fixed cap representative across all rows.
        var selected = new List<BiomeAuthoringTree>(1200);
        var reservoir = new RandomNumberGenerator { Seed = (ulong)(preset.Seed ^ 0x51EED) };
        int candidates = 0;
        float spacing = Mathf.Min(128, preset.MinimumTreeSpacingMeters * 1.25f);
        for (float z = -128; z < 128; z += spacing)
        {
            var trees = sampler.GenerateTrees(new Rect2(-128, z, 256, Mathf.Min(spacing, 128 - z)), 256);
            foreach (var tree in trees)
            {
                candidates++;
                if (selected.Count < 1200) selected.Add(tree);
                else
                {
                    int replacement = reservoir.RandiRange(0, candidates - 1);
                    if (replacement < 1200) selected[replacement] = tree;
                }
            }
            yield return 0;
        }
        var buckets = new Dictionary<int, List<BiomeAuthoringTree>>();
        foreach (var tree in selected)
        {
            var list = variants[tree.Species];
            int index = Mathf.Abs(Mathf.RoundToInt(tree.Position.X + tree.Position.Z)) % list.Count;
            int key = list[index].Index;
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new();
            bucket.Add(tree);
        }
        foreach (var pair in buckets)
        {
            ImportedTreeVariant variant = null!;
            foreach (var list in variants.Values)
            foreach (var candidate in list)
                if (candidate.Index == pair.Key) variant = candidate;

            var mesh = (Mesh)variant.Lods[1].Duplicate(true);
            var sun = GetNodeOrNull<DirectionalLight3D>("Sun");
            var direction = sun == null ? Vector3.Down : sun.GlobalBasis.Z.Normalized();
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.SurfaceGetMaterial(surface) is not ShaderMaterial source) continue;
                var material = (ShaderMaterial)source.Duplicate(true);
                material.SetShaderParameter("day_start_dot", -2f);
                material.SetShaderParameter("day_full_dot", -1f);
                material.SetShaderParameter("night_tint", Colors.White);
                material.SetShaderParameter("night_direct_light_factor", 1f);
                material.SetShaderParameter("day_direct_light_factor", 1f);
                material.SetShaderParameter("sun_direction", direction);
                mesh.SurfaceSetMaterial(surface, material);
            }
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = pair.Value.Count,
                Mesh = mesh
            };
            for (int i = 0; i < pair.Value.Count; i++)
            {
                var tree = pair.Value[i];
                multi.SetInstanceTransform(i, new Transform3D(
                    Basis.FromEuler(new Vector3(0, tree.Yaw, 0)).Scaled(Vector3.One * tree.Height), tree.Position));
                if ((i & 31) == 0) yield return 0;
            }
            root.AddChild(new MultiMeshInstance3D { Name = "PreviewTrees_" + variant.Index, Multimesh = multi });
            yield return 0;
        }
    }
}
