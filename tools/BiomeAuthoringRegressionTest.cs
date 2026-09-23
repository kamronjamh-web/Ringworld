using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Biomes;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo;

/// <summary>Bounded contract checks for the isolated biome authoring core.</summary>
public partial class BiomeAuthoringRegressionTest : RefCounted
{
    public bool Run()
    {
        try
        {
            string folder = "res://.godot/biome_workshop_tests";
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(folder));
            string path = folder + "/roundtrip.tres", futurePath = folder + "/future.tres";
            var preset = BiomeAuthoringPreset.CreateNew("Regression biome");
            preset.Seed = 9147; preset.BaseBiome = RingworldTechDemo.Voxel.Phase3.TerrainBiomeId.Forest;
            preset.MeanElevationMeters = 173; preset.ElevationVariationMeters = 71; preset.BroadFeatureScaleMeters = 1700;
            preset.DetailAmplitudeMeters = 23; preset.DetailFeatureScaleMeters = 190; preset.Humidity = .72f; preset.Temperature = .51f;
            preset.TreeDensity = .63f; preset.MinimumTreeSpacingMeters = 9; preset.TreeClustering = .66f; preset.ClearingStrength = .21f;
            preset.MapleWeight = 1; preset.FirWeight = 0; preset.PineWeight = 0; preset.MinimumTreeHeightMeters = 4; preset.MaximumTreeHeightMeters = 14;
            preset.GrassDensity = .8f; preset.GrassHeightMeters = 1.4f; preset.GroundMaterial = BiomeGroundMaterial.ForestFloor; preset.GroundTextureScale = 3;
            preset.GrassTint = new Color(.12f, .43f, .24f, 1);
            preset.GroundTint = new Color(.9f, .8f, .7f, 1);
            preset.MaximumTreeSlopeDegrees = 19;
            string id = preset.Identity;
            Require(preset.SaveAtomic(path) == Error.Ok, "roundtrip save failed");
            Require(BiomeAuthoringPreset.TryLoad(path, out var loaded, out string error) && loaded != null, "roundtrip load: " + error);
            Require(Equal(preset, loaded!), "roundtrip fields changed");
            Require(loaded!.GrassTint == preset.GrassTint && loaded.GroundTint == preset.GroundTint
                && loaded.MaximumTreeSlopeDegrees == preset.MaximumTreeSlopeDegrees
                && loaded.Version == BiomeAuthoringPreset.CurrentVersion, "roundtrip colors, slope, or version changed");
            Require(loaded!.Identity == id, "save changed identity");
            Require(preset.CreateDuplicateWithNewIdentity().Identity != id, "duplicate retained identity");
            BiomeAuthoringSnapshot retainedSnapshot = preset.CreateSnapshot();
            Require(retainedSnapshot.MeanElevationMeters == 173, "snapshot baseline wrong"); preset.MeanElevationMeters = 99;
            Require(retainedSnapshot.MeanElevationMeters == 173 && preset.CreateSnapshot().MeanElevationMeters == 99 && loaded!.MeanElevationMeters == 173, "retained snapshot/load isolation failed");
            Require(ResourceSaver.Save(new BiomeAuthoringPreset { Version = 99, Identity = id }, futurePath) == Error.Ok, "future fixture save failed");
            Require(!BiomeAuthoringPreset.TryLoad(futurePath, out _, out _), "future version loaded");
            var invalid = BiomeAuthoringPreset.CreateNew(); invalid.Humidity = float.NaN; Require(!invalid.TryValidate(out _), "NaN accepted");
            invalid = BiomeAuthoringPreset.CreateNew(); invalid.GroundMaterial = (BiomeGroundMaterial)99; Require(!invalid.TryValidate(out _), "invalid palette accepted");
            string oldId = loaded.Identity; loaded.Version = 99; Require(loaded.SaveAtomic(path) != Error.Ok, "invalid save accepted");
            Require(BiomeAuthoringPreset.TryLoad(path, out var preserved, out _) && preserved!.Identity == oldId, "failed save damaged destination");

            var flat = BiomeAuthoringPreset.CreateNew(); flat.MeanElevationMeters = 43; flat.ElevationVariationMeters = 0; flat.DetailAmplitudeMeters = 0;
            using (var sampler = new BiomeAuthoringSampler(flat))
            { Require(sampler.Sample(-300, 70).SurfaceHeight == 43 && sampler.Sample(400, -200).SurfaceHeight == 43, "flat authored elevation is not exact"); }

            var treePreset = BiomeAuthoringPreset.CreateNew(); treePreset.Seed = 555; treePreset.MeanElevationMeters = 100; treePreset.ElevationVariationMeters = 14; treePreset.DetailAmplitudeMeters = 2;
            treePreset.TreeDensity = 1; treePreset.MinimumTreeSpacingMeters = 8; treePreset.MapleWeight = 1; treePreset.FirWeight = treePreset.PineWeight = 0;
            using (var sampler = new BiomeAuthoringSampler(treePreset))
            {
                Rect2 left = new(-64, -64, 64, 128), right = new(0, -64, 64, 128), all = new(-64, -64, 128, 128);
                var once = sampler.GenerateTrees(all, 4096); var again = sampler.GenerateTrees(all, 4096);
                Require(EqualTrees(once, again) && once.Count > 0, "tree generation is not deterministic");
                var joined = new List<BiomeAuthoringTree>(sampler.GenerateTrees(left, 4096)); joined.AddRange(sampler.GenerateTrees(right, 4096));
                Require(Spaced(joined, treePreset.MinimumTreeSpacingMeters), "split bounds violate minimum spacing");
                foreach (var tree in once) Require(tree.Species == TreeSpecies.Maple, "single species weights ignored");
            }
            var changedSeed = treePreset.CreateDuplicateWithNewIdentity(); changedSeed.Seed++;
            using (var a = new BiomeAuthoringSampler(treePreset)) using (var b = new BiomeAuthoringSampler(changedSeed))
                Require(a.Sample(123, 456).SurfaceHeight != b.Sample(123, 456).SurfaceHeight, "seed does not affect surface");
            treePreset.TreeDensity = 0; using (var sampler = new BiomeAuthoringSampler(treePreset)) Require(sampler.GenerateTrees(new Rect2(-32, -32, 64, 64), 100).Count == 0, "zero tree density generated trees");
            treePreset.TreeDensity = 1; treePreset.MeanElevationMeters = -2; using (var sampler = new BiomeAuthoringSampler(treePreset)) Require(sampler.GenerateTrees(new Rect2(-32, -32, 64, 64), 100).Count == 0, "authored water did not exclude trees");
            var cold = BiomeAuthoringPreset.CreateNew(); cold.Temperature = .01f; cold.Humidity = .1f; var mild = cold.CreateDuplicateWithNewIdentity(); mild.Temperature = .55f; mild.Humidity = .9f;
            using (var a = new BiomeAuthoringSampler(cold)) using (var b = new BiomeAuthoringSampler(mild)) Require(b.Sample(10, 10).TreeDensity > a.Sample(10, 10).TreeDensity, "humidity/temperature do not affect tree suitability");
            GD.Print("BIOME_AUTHORING_REGRESSION=PASS"); return true;
        }
        catch (Exception e) { GD.PushError("BIOME_AUTHORING_REGRESSION=FAIL " + e.Message); return false; }
    }
    static bool Equal(BiomeAuthoringPreset a, BiomeAuthoringPreset b) => a.Identity == b.Identity && a.DisplayName == b.DisplayName && a.Seed == b.Seed && a.BaseBiome == b.BaseBiome && a.MeanElevationMeters == b.MeanElevationMeters && a.ElevationVariationMeters == b.ElevationVariationMeters && a.BroadFeatureScaleMeters == b.BroadFeatureScaleMeters && a.DetailAmplitudeMeters == b.DetailAmplitudeMeters && a.DetailFeatureScaleMeters == b.DetailFeatureScaleMeters && a.Humidity == b.Humidity && a.Temperature == b.Temperature && a.TreeDensity == b.TreeDensity && a.MinimumTreeSpacingMeters == b.MinimumTreeSpacingMeters && a.TreeClustering == b.TreeClustering && a.ClearingStrength == b.ClearingStrength && a.MapleWeight == b.MapleWeight && a.FirWeight == b.FirWeight && a.PineWeight == b.PineWeight && a.MinimumTreeHeightMeters == b.MinimumTreeHeightMeters && a.MaximumTreeHeightMeters == b.MaximumTreeHeightMeters && a.GrassDensity == b.GrassDensity && a.GrassHeightMeters == b.GrassHeightMeters && a.GroundMaterial == b.GroundMaterial && a.GroundTextureScale == b.GroundTextureScale;
    static bool EqualTrees(IReadOnlyList<BiomeAuthoringTree> a, IReadOnlyList<BiomeAuthoringTree> b) { if (a.Count != b.Count) return false; for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false; return true; }
    static bool Spaced(IReadOnlyList<BiomeAuthoringTree> trees, float distance) { for (int i = 0; i < trees.Count; i++) for (int j = i + 1; j < trees.Count; j++) { Vector3 delta = trees[i].Position - trees[j].Position; if (MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z) + .0001f < distance) return false; } return true; }
    static void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
}
