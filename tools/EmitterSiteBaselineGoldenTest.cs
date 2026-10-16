using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// P0 of docs/emitter-site-terrain-spec.md: golden terrain-definition hashes and height samples for the current
/// gen-17 v4 recipe and gen-16 world, so later phases can prove existing worlds stay bit-identical.
/// </summary>
public partial class EmitterSiteBaselineGoldenTest : RefCounted
{
    const string Format = "emitter_site_baseline_v1";

    /// <param name="fixtureOverride">Optional absolute fixture path (used to capture a new case beside the committed golden).</param>
    public int Run(bool capture, string fixtureOverride = "")
        => RunGolden(string.IsNullOrEmpty(fixtureOverride)
            ? ProjectSettings.GlobalizePath("res://tools/fixtures/emitter_site_baseline_golden.json") : fixtureOverride, capture);

    public static int RunGolden(string fixture, bool capture)
    {
        if (capture && File.Exists(fixture)) return Fail("Refusing to overwrite existing golden fixture: " + fixture);
        try
        {
            Golden actual = Capture();
            if (capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
                File.WriteAllText(fixture, JsonSerializer.Serialize(actual, JsonOptions) + System.Environment.NewLine);
                foreach (Case c in actual.Cases) Console.WriteLine($"EMITTER_SITE_BASELINE_CAPTURE {c.Name} terrain_hash={c.TerrainHash} recipe_hash={c.RecipeHash}");
                return 0;
            }
            if (!File.Exists(fixture)) return Fail("Golden fixture missing: " + fixture);
            Golden? expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions);
            if (expected == null) return Fail("Golden fixture could not be read.");
            var mismatches = Compare(expected, actual);
            if (mismatches.Count != 0) return Fail("EMITTER_SITE_BASELINE=FAIL mismatches=" + string.Join(",", mismatches));
            foreach (Case c in actual.Cases) Console.WriteLine($"EMITTER_SITE_BASELINE {c.Name} terrain_hash={c.TerrainHash}");
            Console.WriteLine("EMITTER_SITE_BASELINE=PASS cases=" + actual.Cases.Count);
            return 0;
        }
        catch (Exception exception) { return Fail(exception.ToString()); }
    }

    static Golden Capture()
    {
        var golden = new Golden { Format = Format, Cases = new List<Case>() };
        using (var sampler = EmitterSiteTestWorlds.CreateGen17(1337))
        {
            var item = new Case
            {
                Name = "gen17_v4_seed_1337", Description = "ringworld_surface_erosion_v4 default recipe, runtime surface generation",
                RecipeHash = EmitterSiteTestWorlds.Gen17Recipe(1337).DefinitionHash.ToString("X16"),
                GeneratorVersion = sampler.TerrainGeneratorVersion,
                TerrainHash = sampler.TerrainDefinitionHash.ToString("X16")
            };
            foreach ((string name, long a, int x) in Points()) item.Points.Add(Sample(name, sampler, a, x));
            golden.Cases.Add(item);
        }
        using (var sampler = EmitterSiteTestWorlds.CreateGen17Sites(1337))
        {
            EmitterSiteRegistry registry = sampler.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no emitter registry");
            var item = new Case
            {
                Name = "gen17_v5_seed_1337", Description = "ringworld_surface_sites_v5 default recipe with emitter sites, runtime surface generation (recorded in phase G0 of the grid/climate spec)",
                RecipeHash = EmitterSiteTestWorlds.Gen17SitesRecipe(1337).DefinitionHash.ToString("X16"),
                GeneratorVersion = sampler.TerrainGeneratorVersion,
                TerrainHash = sampler.TerrainDefinitionHash.ToString("X16")
            };
            foreach ((string name, long a, int x) in Points()) item.Points.Add(Sample(name, sampler, a, x));
            // Registry-derived points: centre and a 1 km offset on a land pad, an island and the main site (all frozen with the v5 registry).
            int landPad = -1, island = -1;
            for (int i = 0; i < registry.Count - 1 && (landPad < 0 || island < 0); i++)
            {
                if (landPad < 0 && !registry[i].IsIsland && !registry[i].IsMountain) landPad = i;
                if (island < 0 && registry[i].IsIsland) island = i;
            }
            foreach ((string label, int index) in new[] { ("land_pad", landPad), ("island", island), ("main", registry.MainIndex) })
            {
                if (index < 0) throw new InvalidOperationException("v5 registry has no " + label + " site");
                EmitterSite site = registry[index];
                item.Points.Add(Sample(label + "_center", sampler, site.Around, site.Across));
                item.Points.Add(Sample(label + "_offset_1km", sampler, site.Around + 1000L, site.Across + 1000));
            }
            golden.Cases.Add(item);
        }
        using (var sampler = EmitterSiteTestWorlds.CreateGen17Climate(1337))
        {
            ClimateMachineRegistry climate = sampler.ClimateMachines ?? throw new InvalidOperationException("v6 sampler has no climate registry");
            var item = new Case
            {
                Name = "gen17_v6_seed_1337", Description = "ringworld_surface_climate_v6 default recipe with emitter sites and Wall Engine forecourts, runtime surface generation (phase C2 of the grid/climate spec)",
                RecipeHash = EmitterSiteTestWorlds.Gen17ClimateRecipe(1337).DefinitionHash.ToString("X16"),
                GeneratorVersion = sampler.TerrainGeneratorVersion,
                TerrainHash = sampler.TerrainDefinitionHash.ToString("X16")
            };
            foreach ((string name, long a, int x) in Points()) item.Points.Add(Sample(name, sampler, a, x));
            // Registry-derived points: a dry machine, a quay machine and the first offline machine, each at the pad centre, 2 km on the pad and 5 km out (skirt).
            int dry = -1, quay = -1;
            for (int i = 0; i < climate.Count && (dry < 0 || quay < 0); i++)
            {
                bool isQuay = (climate[i].Flags & ClimateMachineFlags.Quay) != 0;
                if (dry < 0 && !isQuay && !climate[i].IsOffline) dry = i;
                if (quay < 0 && isQuay && !climate[i].IsOffline) quay = i;
            }
            foreach ((string label, int index) in new[] { ("machine_dry", dry), ("machine_quay", quay), ("machine_offline", climate.OfflineIndices[0]) })
            {
                if (index < 0) throw new InvalidOperationException("v6 registry has no " + label);
                ClimateMachine m = climate[index];
                int inward = m.Side == 0 ? 1 : -1;
                item.Points.Add(Sample(label + "_pad_center", sampler, m.Around, m.Across + inward * 1000));
                item.Points.Add(Sample(label + "_pad_2km", sampler, m.Around + 4000L, m.Across + inward * 2000));
                item.Points.Add(Sample(label + "_skirt_5km", sampler, m.Around - 6000L, m.Across + inward * 5000));
            }
            golden.Cases.Add(item);
        }
        using (var sampler = EmitterSiteTestWorlds.CreateGen16(1337))
        {
            var item = new Case
            {
                Name = "gen16_certified_seed_1337", Description = "ring_integration settings, BedrockY -2000, sea 0, rare peak 1848, no auto-raise",
                RecipeHash = "", GeneratorVersion = sampler.TerrainGeneratorVersion,
                TerrainHash = sampler.TerrainDefinitionHash.ToString("X16")
            };
            foreach ((string name, long a, int x) in Points()) item.Points.Add(Sample(name, sampler, a, x));
            golden.Cases.Add(item);
        }
        return golden;
    }

    static IEnumerable<(string, long, int)> Points() => new[] {
        ("lattice_mid", 1_570_796L, 159_000), ("lattice_ridge", 9_000_000L, 120_000), ("lattice_coast", 15_700_000L, 26_500),
        ("lattice_far", 23_561_940L, 291_500), ("odd", 1_570_797L, 159_001), ("seam_left", Circumference - 2, 159_000),
        ("seam_right", 2L, 159_000), ("wall_low", 5_000_000L, 0), ("wall_high", 5_000_000L, Width - 2),
        ("site_row0", 314_158L, 26_500), ("site_row50", 15_707_968L, 132_500), ("site_row99", 31_101_776L, 291_500)
    };

    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;

    static Point Sample(string name, RingTerrainSampler sampler, long around, int across)
    {
        float height = sampler.GetPresentationSurfaceHeight(around, across);
        if (!float.IsFinite(height)) throw new InvalidOperationException("non-finite height at " + name);
        TerrainSample s = sampler.GetTerrainSample(around, across);
        return new Point
        {
            Name = name, Around = around, Across = across, HeightBits = BitConverter.SingleToInt32Bits(height),
            SurfaceHeight = s.SurfaceHeight, Biome = (int)s.Biome, WaterKind = (int)s.WaterKind
        };
    }

    static List<string> Compare(Golden expected, Golden actual)
    {
        var bad = new List<string>();
        if (expected.Format != actual.Format) bad.Add("format");
        foreach (Case current in actual.Cases)
        {
            Case? want = expected.Cases.Find(c => c.Name == current.Name);
            if (want == null) { bad.Add(current.Name + ":missing"); continue; }
            if (want.TerrainHash != current.TerrainHash) bad.Add(current.Name + ":terrain_hash(" + want.TerrainHash + "->" + current.TerrainHash + ")");
            if (want.RecipeHash != current.RecipeHash) bad.Add(current.Name + ":recipe_hash");
            if (want.GeneratorVersion != current.GeneratorVersion) bad.Add(current.Name + ":generator_version");
            foreach (Point point in current.Points)
            {
                Point? old = want.Points.Find(p => p.Name == point.Name);
                if (old == null || old.HeightBits != point.HeightBits || old.SurfaceHeight != point.SurfaceHeight
                    || old.Biome != point.Biome || old.WaterKind != point.WaterKind) bad.Add(current.Name + ":" + point.Name);
            }
        }
        return bad;
    }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Golden { public string Format { get; set; } = ""; public List<Case> Cases { get; set; } = new(); }
    public sealed class Case
    {
        public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string RecipeHash { get; set; } = "";
        public int GeneratorVersion { get; set; } public string TerrainHash { get; set; } = ""; public List<Point> Points { get; set; } = new();
    }
    public sealed class Point
    {
        public string Name { get; set; } = ""; public long Around { get; set; } public int Across { get; set; }
        public int HeightBits { get; set; } public int SurfaceHeight { get; set; } public int Biome { get; set; } public int WaterKind { get; set; }
    }
}
