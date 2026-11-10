using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Contract test of the pinned generation 1 world and the committed landscape pose fixture (docs/landscape-audit-t0-spec.md WO T0-1). Run via
/// tools/landscape_world_test.gd. Flags (after `--`): --capture (write the fixture, refuses to overwrite), --find (scan and print the differences from the
/// committed fixture), --compare-slot=&lt;abs path to world.json&gt; (informational), --fixture=&lt;path&gt; (validate another fixture file, for the can-it-fail check).
/// </summary>
public partial class LandscapeWorldTest : RefCounted
{
    static string Arg(string prefix)
    {
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return "";
    }

    static bool Flag(string flag) => OS.GetCmdlineUserArgs().Contains(flag);

    public bool Run()
    {
        var failures = new List<string>();
        try
        {
            RunChecks(failures);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PushError("LANDSCAPE_WORLD=FAIL " + failure);
        return failures.Count == 0;
    }

    static void RunChecks(List<string> failures)
    {
        var clock = Stopwatch.StartNew();
        TerrainGenerationParameters recipe = LandscapeWorld.Recipe;
        GD.Print($"LANDSCAPE_WORLD_RECIPE hash={LandscapeWorld.DefinitionHashHex} json_sha={LandscapeWorld.JsonSha256}");

        // (a) recipe identity and canonical round trip
        if (recipe.GeneratorId != TerrainGenerationParameters.WeatherLungGeneratorIdentity) failures.Add("(a) generator id is " + recipe.GeneratorId);
        if (recipe.Seed != 1337) failures.Add("(a) seed is " + recipe.Seed);
        TerrainGenerationParameters again = TerrainGenerationParameters.FromJson(recipe.CanonicalJson);
        if (again.CanonicalJson != recipe.CanonicalJson || again.DefinitionHash != recipe.DefinitionHash) failures.Add("(a) canonical json does not round trip");

        // (b) settings come from the production resource with the launcher overrides
        using (VoxelTerrainSettings settings = LandscapeWorld.CreateSettings())
        {
            if (Math.Abs(settings.TemperatureFrequency - 2.5e-6f) > 1e-12f) failures.Add($"(b) TemperatureFrequency {settings.TemperatureFrequency} is not the production 2.5e-6");
            if (Math.Abs(settings.MoistureFrequency - 5e-6f) > 1e-12f) failures.Add($"(b) MoistureFrequency {settings.MoistureFrequency} is not the production 5e-6");
            if (settings.BiomeCatalog == null) failures.Add("(b) production biome catalog missing");
            if (settings.Seed != 1337 || settings.BedrockY != -8000 || settings.SeaLevelY != 0 || settings.SurfaceGenerationContractId != EmitterSiteTestWorlds.Contract
                || settings.SurfaceGenerationRecipeJson != recipe.CanonicalJson) failures.Add("(b) launcher overrides not applied");
            if (settings.EnableProceduralCaves || settings.EnableNaturalOverhangs || settings.EnableUnified3dDensityTerrain || settings.EnableDesignedCaveDensityComposition)
                failures.Add("(b) caves / overhangs / 3D density must be off");
        }

        LandscapeWorld.Worker[] workers = LandscapeWorld.CreateWorkers(LandscapeWorld.DefaultWorkerCount);
        try
        {
            LandscapeWorld.Worker w = workers[0];

            // (c) natural height from shaped diagnostics equals the natural model bit for bit
            var lattice = LandscapeWorld.Lattice().ToArray();
            int checkedPoints = 0, differing = 0;
            for (int k = 0; k < lattice.Length; k += 16)
            {
                var d = w.Diag(lattice[k].Around, lattice[k].Across);
                double fromShaped = LandscapeWorld.NaturalHeight(d), natural = w.Natural.SampleNaturalDiagnostics(lattice[k].Around, lattice[k].Across).Height;
                checkedPoints++;
                if (BitConverter.DoubleToInt64Bits(fromShaped) != BitConverter.DoubleToInt64Bits(natural)) differing++;
            }
            if (checkedPoints < 1000 || differing != 0) failures.Add($"(c) natural height differs from shaped diagnostics at {differing} of {checkedPoints} lattice points");

            // flags
            string slot = Arg("--compare-slot=");
            if (slot.Length > 0) CompareSlot(slot);
            string fixtureArg = Arg("--fixture=");
            string fixturePath = fixtureArg.Length > 0 ? fixtureArg : ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath);
            if (Flag("--capture") || Flag("--find"))
            {
                var finderClock = Stopwatch.StartNew();
                List<LandscapePose> found = LandscapePoseFinder.Find(workers, s => GD.Print("LANDSCAPE_WORLD_FIND " + s));
                GD.Print($"LANDSCAPE_WORLD_FIND finished in {finderClock.Elapsed.TotalSeconds:F1} s with {found.Count} poses");
                if (Flag("--capture"))
                {
                    if (File.Exists(fixturePath)) failures.Add("--capture refuses to overwrite the existing fixture " + fixturePath);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!); File.WriteAllText(fixturePath, LandscapePoseFinder.ToJson(found)); GD.Print("LANDSCAPE_WORLD_CAPTURE wrote " + fixturePath); }
                }
                else if (File.Exists(fixturePath))
                {
                    List<string> diffs = LandscapePoseFinder.Diff(found, LandscapePoseFinder.ReadFixture(fixturePath));
                    GD.Print("LANDSCAPE_WORLD_FIND differences from the committed fixture: " + (diffs.Count == 0 ? "none" : ""));
                    foreach (string diff in diffs) GD.Print("LANDSCAPE_WORLD_FIND   " + diff);
                }
            }

            // (d), (e) every committed row
            if (!File.Exists(fixturePath)) failures.Add("(d) fixture missing: " + fixturePath + " (run with --capture once)");
            else
            {
                LandscapePoseFinder.Fixture fixture = LandscapePoseFinder.ReadFixture(fixturePath);
                foreach (string failure in LandscapePoseFinder.Validate(fixture, w)) failures.Add("(d/e) " + failure);
                GD.Print($"LANDSCAPE_WORLD fixture rows={fixture.Poses.Count} hash={fixture.DefinitionHash}");
            }
        }
        finally { foreach (var worker in workers) worker.Dispose(); }
        GD.Print($"LANDSCAPE_WORLD finished in {clock.Elapsed.TotalSeconds:F1} s");
    }

    static void CompareSlot(string worldJsonPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(worldJsonPath));
            string stored = FindRecipe(doc.RootElement) ?? "";
            if (stored.Length == 0) { GD.Print("LANDSCAPE_WORLD_SLOT no recipe json found in " + worldJsonPath); return; }
            bool equal = stored == LandscapeWorld.CanonicalJson;
            GD.Print($"LANDSCAPE_WORLD_SLOT recipe equals pinned: {equal} (slot sha {LandscapeWorld.Sha256Hex(stored)[..12]}, pinned sha {LandscapeWorld.JsonSha256[..12]})");
            if (!equal)
            {
                try { var slotRecipe = TerrainGenerationParameters.FromJson(stored); GD.Print($"LANDSCAPE_WORLD_SLOT slot generator {slotRecipe.GeneratorId} seed {slotRecipe.Seed} hash 0x{slotRecipe.DefinitionHash:X16}"); }
                catch (Exception e) { GD.Print("LANDSCAPE_WORLD_SLOT slot recipe does not parse: " + e.Message); }
            }
        }
        catch (Exception e) { GD.Print("LANDSCAPE_WORLD_SLOT could not read " + worldJsonPath + ": " + e.Message); }
    }

    /// <summary>Finds the first string value that parses as a surface-generation recipe (the saved settings keep it under a recipe-json key).</summary>
    static string? FindRecipe(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string s = element.GetString() ?? "";
                return s.StartsWith("{", StringComparison.Ordinal) && s.Contains("generator_id", StringComparison.Ordinal) ? s : null;
            case JsonValueKind.Object:
                foreach (JsonProperty p in element.EnumerateObject()) { string? r = FindRecipe(p.Value); if (r != null) return r; }
                return null;
            case JsonValueKind.Array:
                foreach (JsonElement e in element.EnumerateArray()) { string? r = FindRecipe(e); if (r != null) return r; }
                return null;
            default: return null;
        }
    }
}
