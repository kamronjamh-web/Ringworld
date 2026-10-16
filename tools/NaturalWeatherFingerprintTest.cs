using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// G0 of docs/emitter-grid-climate-machines-spec.md: a deterministic fingerprint of the natural weather of a v5 world
/// (raw weather queries over a fixed point grid and time list, plus the natural lightning events of the first 10 minutes),
/// so the climate-machine work can prove v5 weather stays bit-identical. Reads production state only; changes nothing.
/// </summary>
public partial class NaturalWeatherFingerprintTest : RefCounted
{
    const string Format = "natural_weather_fingerprint_v1";
    const int PointsPerAxis = 16;
    const double LightningWindowSeconds = 600.0;
    const double LightningStepSeconds = 0.5;   // below RingLightningEvent.LifetimeSeconds (0.72) so no slot is stepped over
    const double SystemScanStepSeconds = 30.0;
    const int ListedEvents = 40;               // first events are listed for readable diffs; the hash covers all of them
    static readonly double[] Times = { 0.0, 137.5, 900.0, 2200.0, 7200.0, 86400.0 };

    delegate bool CreateNaturalEventFn(int a, int z, double now, out RingLightningEvent created);

    /// <summary>Canonical v5 recipe (seed 1337) and runtime-surface contract the fingerprint world is built from.</summary>
    public string RecipeJson() => EmitterSiteTestWorlds.Gen17SitesRecipe(1337).CanonicalJson;
    public string ContractId() => EmitterSiteTestWorlds.Contract;

    public int Run(Node weatherLayer, bool capture)
        => RunGolden(weatherLayer, ProjectSettings.GlobalizePath("res://tools/fixtures/natural_weather_v5_fingerprint_golden.json"), capture);

    public static int RunGolden(Node layer, string fixture, bool capture)
    {
        if (capture && File.Exists(fixture)) return Fail("Refusing to overwrite existing golden fixture: " + fixture);
        try
        {
            Golden actual = Capture(layer);
            Golden again = Capture(layer);
            if (JsonSerializer.Serialize(actual, JsonOptions) != JsonSerializer.Serialize(again, JsonOptions))
                return Fail("NATURAL_WEATHER_FINGERPRINT=FAIL two in-process captures differ");
            if (capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
                File.WriteAllText(fixture, JsonSerializer.Serialize(actual, JsonOptions) + System.Environment.NewLine);
                Console.WriteLine($"NATURAL_WEATHER_FINGERPRINT_CAPTURE terrain_hash={actual.TerrainHash} storm_cells={actual.StormCells} lightning_events={actual.LightningEventCount} lightning_hash={actual.LightningHash}");
                return 0;
            }
            if (!File.Exists(fixture)) return Fail("Golden fixture missing: " + fixture);
            Golden? expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions);
            if (expected == null) return Fail("Golden fixture could not be read.");
            List<string> mismatches = Compare(expected, actual);
            if (mismatches.Count != 0) return Fail("NATURAL_WEATHER_FINGERPRINT=FAIL mismatches=" + string.Join(",", mismatches));
            Console.WriteLine($"NATURAL_WEATHER_FINGERPRINT=PASS times={actual.Times.Count} storm_cells={actual.StormCells} lightning_events={actual.LightningEventCount} lightning_hash={actual.LightningHash}");
            return 0;
        }
        catch (Exception exception) { return Fail(exception.ToString()); }
    }

    static Golden Capture(Node layerNode)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        Type type = typeof(RingVolumetricCloudLayer);
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo rawQuery = type.GetMethod("QueryRawWeatherAtTime", Any) ?? throw new InvalidOperationException("QueryRawWeatherAtTime missing");
        MethodInfo buildSystem = type.GetMethod("TryBuildWeatherSystem", Any) ?? throw new InvalidOperationException("TryBuildWeatherSystem missing");
        var createEvent = (CreateNaturalEventFn)Delegate.CreateDelegate(typeof(CreateNaturalEventFn), layer,
            type.GetMethod("TryCreateNaturalEvent", Any) ?? throw new InvalidOperationException("TryCreateNaturalEvent missing"));

        var grid = layer.GetNode<VoxelChunkGridManager>(layer.GridManagerPath);
        var sampler = grid.TerrainSampler ?? throw new InvalidOperationException("weather layer has no terrain sampler");
        if (!sampler.UsesRuntimeSurfaceGeneration || sampler.EmitterSites == null)
            throw new InvalidOperationException("weather fingerprint requires a v5 (emitter sites) runtime-surface world");
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        double width = grid.RingAcrossWidthMeters;

        var golden = new Golden
        {
            Format = Format, Seed = sampler.Seed, TerrainHash = sampler.TerrainDefinitionHash.ToString("X16"),
            RegistryIdentity = sampler.EmitterSites.Identity.ToString("X16"),
            Circumference = circumference, Width = width,
            Settings = new Settings
            {
                FormationCellMeters = layer.FormationCellMeters, MacroWeatherCellMeters = layer.MacroWeatherCellMeters,
                MacroSystemSpawnChance = layer.MacroSystemSpawnChance, MaximumMacroSystemRadiusMeters = layer.MaximumMacroSystemRadiusMeters,
                WindX = layer.WeatherWindMetersPerSecond.X, WindY = layer.WeatherWindMetersPerSecond.Y,
                NaturalWeatherLifecyclesEnabled = layer.NaturalWeatherLifecyclesEnabled
            }
        };

        // Raw (no manual weather, no wind history) queries on a 16x16 grid offset from the cell lattice.
        foreach (double time in Times)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var row = new TimeRow { TimeSeconds = time };
            double coverage = 0, precipitation = 0;
            for (int i = 0; i < PointsPerAxis; i++)
            for (int j = 0; j < PointsPerAxis; j++)
            {
                double around = (i + 0.37) * circumference / PointsPerAxis;
                double across = (j + 0.5) * width / PointsPerAxis;
                var w = (RingVolumetricCloudLayer.LocalWeatherState)rawQuery.Invoke(layer,
                    new object[] { new RingCanonicalPosition(around, 0.0, across), time, false, true })!;
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{i},{j}|{w.SystemId}|{(int)w.SystemKind}|{Bits(w.SystemInfluence)}|{Bits(w.SystemRadiusMeters)}|{Bits(w.SystemCenterMeters.X)},{Bits(w.SystemCenterMeters.Y)}|{Bits(w.SystemRadiiMeters.X)},{Bits(w.SystemRadiiMeters.Y)}|{Bits(w.SystemOrientationRadians)}|{Bits(w.CloudCoverage)}|{Bits(w.PrecipitationPotential)}|{(int)w.DominantFormationProfile}|{Bits(w.CloudBaseAltitudeMeters)}|{Bits(w.CloudTopAltitudeMeters)}\n");
                sha.AppendData(Encoding.UTF8.GetBytes(line));
                coverage += w.CloudCoverage; precipitation += w.PrecipitationPotential;
                if (w.SystemId != 0) row.PointsWithSystem++;
                if (w.PrecipitationPotential > 0.05f) row.PointsRaining++;
                if (i % 5 == 0 && j % 5 == 0)
                    row.Samples.Add(new Sample
                    {
                        I = i, J = j, SystemId = w.SystemId, SystemKind = (int)w.SystemKind,
                        InfluenceBits = Bits(w.SystemInfluence), CoverageBits = Bits(w.CloudCoverage), PrecipitationBits = Bits(w.PrecipitationPotential)
                    });
            }
            row.Hash = Convert.ToHexString(sha.GetHashAndReset());
            row.CoverageSum = coverage; row.PrecipitationSum = precipitation;
            golden.Times.Add(row);
        }

        // Natural lightning of [0, 600) s: collect every cell that holds a major/exceptional front during the window, then
        // step each such cell at sub-lifetime resolution through TryCreateNaturalEvent and de-duplicate by event id.
        float cell = Mathf.Clamp(golden.Settings.MacroWeatherCellMeters, 16000f, 64000f);
        int aroundCount = Math.Max(1, Mathf.CeilToInt((float)(circumference / cell)));
        float maxRadius = Mathf.Clamp(golden.Settings.MaximumMacroSystemRadiusMeters, 40000f, 160000f);
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (double t in new[] { 0.0, LightningWindowSeconds })
        {
            lo = Math.Min(lo, Mathf.FloorToInt((float)((-maxRadius - golden.Settings.WindY * t) / cell)));
            hi = Math.Max(hi, Mathf.CeilToInt((float)((width + maxRadius - golden.Settings.WindY * t) / cell)));
        }
        var stormCells = new SortedSet<(int, int)>();
        for (double t = 0.0; t <= LightningWindowSeconds + 1e-9; t += SystemScanStepSeconds)
            for (int z = lo; z <= hi; z++)
            for (int a = 0; a < aroundCount; a++)
            {
                var args = new object?[] { a, z, sampler.Seed, t, null };
                if (!(bool)buildSystem.Invoke(layer, args)!) continue;
                string kind = args[4]!.GetType().GetProperty("Kind")!.GetValue(args[4])!.ToString()!;
                if (kind is "MajorFront" or "ExceptionalFront") stormCells.Add((a, z));
            }
        golden.StormCells = stormCells.Count;
        var events = new Dictionary<long, RingLightningEvent>();
        foreach ((int a, int z) in stormCells)
            for (double now = 0.0; now < LightningWindowSeconds; now += LightningStepSeconds)
                if (createEvent(a, z, now, out RingLightningEvent e) && e.StartTimeSeconds >= 0.0 && e.StartTimeSeconds < LightningWindowSeconds)
                    events[e.EventId] = e;
        var ordered = new List<RingLightningEvent>(events.Values);
        ordered.Sort((x, y) => { int c = x.StartTimeSeconds.CompareTo(y.StartTimeSeconds); return c != 0 ? c : x.EventId.CompareTo(y.EventId); });
        using var lightningSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (RingLightningEvent e in ordered)
        {
            var item = new Event
            {
                EventId = e.EventId, SystemId = e.SystemId, StartTimeBits = BitConverter.DoubleToInt64Bits(e.StartTimeSeconds),
                GroundAroundBits = BitConverter.DoubleToInt64Bits(e.GroundPosition.AroundMeters),
                GroundVerticalBits = BitConverter.DoubleToInt64Bits(e.GroundPosition.VerticalMeters),
                GroundAcrossBits = BitConverter.DoubleToInt64Bits(e.GroundPosition.AcrossMeters),
                CloudVerticalBits = BitConverter.DoubleToInt64Bits(e.CloudPosition.VerticalMeters),
                ShapeSeed = e.ShapeSeed, PlaneAngleBits = Bits(e.PlaneAngleRadians), EnergyBits = Bits(e.Energy), FlashRadiusBits = Bits(e.FlashRadiusMeters)
            };
            if (golden.LightningEvents.Count < ListedEvents) golden.LightningEvents.Add(item);
            lightningSha.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item) + "\n"));
        }
        golden.LightningEventCount = ordered.Count;
        golden.LightningHash = Convert.ToHexString(lightningSha.GetHashAndReset());
        return golden;
    }

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    static List<string> Compare(Golden expected, Golden actual)
    {
        var bad = new List<string>();
        if (expected.Format != actual.Format) bad.Add("format");
        if (expected.Seed != actual.Seed) bad.Add("seed");
        if (expected.TerrainHash != actual.TerrainHash) bad.Add("terrain_hash(" + expected.TerrainHash + "->" + actual.TerrainHash + ")");
        if (expected.RegistryIdentity != actual.RegistryIdentity) bad.Add("registry_identity");
        if (expected.Circumference != actual.Circumference || expected.Width != actual.Width) bad.Add("ring_dimensions");
        if (JsonSerializer.Serialize(expected.Settings) != JsonSerializer.Serialize(actual.Settings)) bad.Add("weather_settings");
        if (expected.Times.Count != actual.Times.Count) bad.Add("time_count");
        else
            for (int i = 0; i < actual.Times.Count; i++)
                if (JsonSerializer.Serialize(expected.Times[i]) != JsonSerializer.Serialize(actual.Times[i])) bad.Add("query_t" + actual.Times[i].TimeSeconds.ToString(CultureInfo.InvariantCulture));
        if (expected.StormCells != actual.StormCells) bad.Add("storm_cells(" + expected.StormCells + "->" + actual.StormCells + ")");
        if (expected.LightningHash != actual.LightningHash) bad.Add("lightning_hash(" + expected.LightningEventCount + "->" + actual.LightningEventCount + " events)");
        return bad;
    }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Golden
    {
        public string Format { get; set; } = ""; public int Seed { get; set; } public string TerrainHash { get; set; } = ""; public string RegistryIdentity { get; set; } = "";
        public double Circumference { get; set; } public double Width { get; set; }
        public Settings Settings { get; set; } = new(); public List<TimeRow> Times { get; set; } = new();
        public int StormCells { get; set; } public int LightningEventCount { get; set; } public string LightningHash { get; set; } = ""; public List<Event> LightningEvents { get; set; } = new();
    }
    public sealed class Settings
    {
        public float FormationCellMeters { get; set; } public float MacroWeatherCellMeters { get; set; } public float MacroSystemSpawnChance { get; set; }
        public float MaximumMacroSystemRadiusMeters { get; set; } public float WindX { get; set; } public float WindY { get; set; } public bool NaturalWeatherLifecyclesEnabled { get; set; }
    }
    public sealed class TimeRow
    {
        public double TimeSeconds { get; set; } public string Hash { get; set; } = ""; public int PointsWithSystem { get; set; } public int PointsRaining { get; set; }
        public double CoverageSum { get; set; } public double PrecipitationSum { get; set; } public List<Sample> Samples { get; set; } = new();
    }
    public sealed class Sample
    {
        public int I { get; set; } public int J { get; set; } public int SystemId { get; set; } public int SystemKind { get; set; }
        public int InfluenceBits { get; set; } public int CoverageBits { get; set; } public int PrecipitationBits { get; set; }
    }
    public sealed class Event
    {
        public long EventId { get; set; } public int SystemId { get; set; } public long StartTimeBits { get; set; }
        public long GroundAroundBits { get; set; } public long GroundVerticalBits { get; set; } public long GroundAcrossBits { get; set; } public long CloudVerticalBits { get; set; }
        public int ShapeSeed { get; set; } public int PlaneAngleBits { get; set; } public int EnergyBits { get; set; } public int FlashRadiusBits { get; set; }
    }
}
