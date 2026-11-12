using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

public partial class WorldGenerationPublishTest : RefCounted
{
    public string RecipeJson(bool runtime) => runtime
        ? TerrainGenerationParameters.WorldDefault(generation: 1, seed: 1337).CanonicalJson
        : TerrainGenerationParameters.WeatherLungDefault().CanonicalJson;

    public string CheckState(string source, int generation, int mask)
    {
        WorldGenerationState state = WorldGeneration.Current;
        if (state.Generation == generation && state.FeatureMask == mask && state.Source == source && state.ReviewFeatures.Count == 0)
            return string.Empty;
        return $"expected generation {generation}, mask {mask} and source {source}, got generation {state.Generation}, mask {state.FeatureMask} and source {state.Source}";
    }

    public string CheckRuntimeSampler(VoxelTerrainSettings settings)
    {
        try
        {
            WorldGenerationState published = WorldGeneration.Current;
            Require(settings.SurfaceGenerationRecipeJson == RecipeJson(true), "configured world settings lack the stored v9 recipe");
            using var sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference,
                EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            Require(sampler.WorldGenerationNumber == published.Generation && sampler.WorldFeatureMask == published.FeatureMask,
                "runtime sampler disagrees with the published world generation");
            Require(WorldGeneration.PublishForTests(3, 5), "could not publish a different world for the copy check");
            using var copy = sampler.CreateIndependentCopy();
            Require(copy.WorldGenerationNumber == sampler.WorldGenerationNumber && copy.WorldFeatureMask == sampler.WorldFeatureMask,
                "independent sampler changed with the current world");
            Require(sampler.WorldGenerationNumber == 1 && sampler.WorldFeatureMask == 0 && published.Generation == 1 && published.FeatureMask == 0,
                "a new publication mutated an earlier sampler or publication snapshot");
            return string.Empty;
        }
        catch (Exception exception) { return exception.Message; }
    }

    public string CheckContracts()
    {
        try
        {
            using var bridge = new WorldGenerationBridge();
            WorldGenerationLedger production = WorldGenerationLedger.Production;
            Require(bridge.ListFrozenGenerations().SequenceEqual(new[] { 1 }) && bridge.LatestFrozen == 1 && production.Rows.Count == 1 && production.Rows[0].Features.Count == 0
                && production.FeatureSet(1, Array.Empty<string>()).Count == 0,
                "production ledger is not the generation-1 baseline with no frozen feature bits");
            Require(production.Features.All(feature => feature.FrozenIn == 0) && bridge.ListOpenFeatures().Length == production.Features.Count,
                "production ledger has a frozen feature outside generation 1 or an open feature that is not listed as open");
            WorldGenerationState before = WorldGeneration.Current;
            Require(!bridge.PublishRecipeJson("{") && bridge.LastError.Length != 0 && ReferenceEquals(before, WorldGeneration.Current),
                "a malformed recipe replaced the current world or did not provide an error");
            Require(bridge.ValidateRecipeJson(RecipeJson(true)) && bridge.LastError.Length == 0 && ReferenceEquals(before, WorldGeneration.Current),
                "validating a recipe changed the current world or retained a stale error");

            Require(WorldGeneration.PublishForTests(3, 5), "main-thread publication was rejected");
            WorldGenerationState published = WorldGeneration.Current;
            GD.Print("WORLD_GENERATION_PUBLISH expected off-thread rejection follows");
            Require(!Task.Run(() => WorldGeneration.PublishForTests(4, 7)).GetAwaiter().GetResult(), "off-thread publication was accepted");
            Require(ReferenceEquals(published, WorldGeneration.Current), "off-thread publication changed the current world");

            CheckSettingsFiles();
            CheckLedger();
            return string.Empty;
        }
        catch (Exception exception) { return exception.Message; }
        finally { WorldGeneration.PublishLegacy("legacy"); }
    }

    public bool PublishShaderProbe(bool enabled) => enabled
        ? WorldGeneration.PublishForTests(3, 5)
        : WorldGeneration.PublishLegacy("legacy");

    private static void CheckSettingsFiles()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("ringworld-generation-");
        try
        {
            string userPath = Path.Combine(directory.FullName, "atmosphere.visual.tres");
            string expected = Path.Combine(directory.FullName, "atmosphere.visual_g03.tres");
            byte[] original = { 0, 1, 255 };
            File.WriteAllBytes(userPath, original);
            Require(WorldGenerationSettingsFiles.Resolve(userPath, 4) == userPath && Directory.GetFiles(directory.FullName).Length == 1,
                "earlier generation did not keep the base path alone");
            Require(WorldGenerationSettingsFiles.Resolve(userPath, 3) == expected && File.ReadAllBytes(expected).SequenceEqual(original),
                "generation-specific file was not seeded with the base bytes");
            byte[] edited = { 8, 10, 0 };
            File.WriteAllBytes(expected, edited);
            Require(WorldGenerationSettingsFiles.Resolve(userPath, 3) == expected && File.ReadAllBytes(expected).SequenceEqual(edited),
                "resolving again overwrote the generation-specific settings");

            string blocked = Path.Combine(directory.FullName, "atmosphere.visual_g02.tres");
            Directory.CreateDirectory(blocked);
            try
            {
                WorldGenerationSettingsFiles.Resolve(userPath, 2);
                throw new InvalidOperationException("copy onto a directory unexpectedly succeeded");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            Require(Directory.GetFiles(directory.FullName).Length == 2 && File.ReadAllBytes(userPath).SequenceEqual(original),
                "a generation-specific copy modified the base or left a temporary file");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void CheckLedger()
    {
        const string frozen = "test.frozen";
        const string open = "test.open";
        var ledger = WorldGenerationLedger.CreateForTests(
            new WorldGenerationLedger.Row[] { new(1, "baseline", Array.Empty<string>(), ""), new(2, "test", new[] { frozen }, "") },
            new WorldGenerationLedger.FeatureInfo[] { new(frozen, 0, "test", 2), new(open, 2, "test", 0) });
        int mask = ledger.MaskFor(2, new[] { open });
        Require(ledger.MaskFor(1, Array.Empty<string>()) == 0 && mask == 5 && ledger.Has(mask, frozen) && ledger.Has(mask, open)
            && !ledger.Has(mask, "test.unknown") && !ledger.Has(0, frozen), "synthetic ledger masks disagree with the feature table");
        Require(!ledger.Validate(2, new[] { "test.unknown" }, out _) && !ledger.Validate(ledger.LatestFrozen + 1, Array.Empty<string>(), out _),
            "synthetic ledger accepted an unknown feature or unfrozen generation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
