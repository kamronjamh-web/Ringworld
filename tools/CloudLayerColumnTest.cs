using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// S6 WO1: pure CPU cloud layer column (RingCloudLayers) over fair, overcast, a warm-front s-sweep, a cold front, a storm and a diurnal sweep,
/// against tools/fixtures/cloud_layer_column_golden.json. Asserts that the low base is below 1 km only where weatherness &gt; 0, that every top is
/// at most 18000 m, that the layers are ordered, and that the factory table validates and its LOWERS_BASE rows follow section 2.2.
/// Run: Godot --headless --path . --script res://tools/cloud_layer_column_test.gd [-- --capture]. Prints CLOUD_LAYER_COLUMN=PASS.
/// </summary>
public partial class CloudLayerColumnTest : RefCounted
{
    const string Format = "cloud_layer_column_v1";
    const string GoldenPath = "res://tools/fixtures/cloud_layer_column_golden.json";
    const float Circumference = 31_415_936.0f;
    const float CenterAround = 1_000_000.0f;
    const float CenterAcross = 100_000.0f;
    const float Major = 60_000.0f;
    const float Minor = 40_000.0f;
    const float Tolerance = 1e-5f;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int Run(bool capture)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            var table = ResourceLoader.Load<CloudLayerTable>(CloudLayerTable.FactoryPath);
            if (table == null) { Console.Error.WriteLine("CLOUD_LAYER_COLUMN=FAIL table missing"); return 1; }
            Check(table.Validate(out string tableError), "table invalid: " + tableError);
            CheckGenusRows(table, Check);

            var golden = new Golden { Format = Format };
            var noSystems = Array.Empty<CloudLayerSystem>();
            Vector3 noon = new(-1.0f, 0.0f, 0.0f); // up(around 0) = (-1, 0, 0): the sun is overhead there

            // Fair weather and overcast (no system): the cluster terms drive the layers.
            foreach ((string name, float coverage, float clusterMid, float clusterHigh) in new[]
                { ("fair_sparse", 0.25f, -0.10f, -0.20f), ("fair_cu", 0.45f, 0.10f, 0.05f), ("fair_broken", 0.65f, 0.20f, 0.25f), ("overcast", 0.95f, 0.30f, 0.30f) })
            {
                CloudLayerPoint p = Point(CenterAround, CenterAcross, coverage, clusterMid, clusterHigh, noon);
                Record(golden, name, p, noSystems, table, Check, expectWeatherness: false);
            }

            CheckAmountBiases(table, noon, Check);

            // Warm front: Nimbostratus (Warm) with support 2, swept along its downwind axis from the precursor to behind the core.
            var warm = new[] { new CloudLayerSystem(MakeSystem(table, CloudFormationProfile.Nimbostratus,
                RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 0.9f, 2.0f)) };
            var warmValues = new List<Vector4>();
            foreach (float s in new[] { 2.1f, 1.9f, 1.6f, 1.2f, 0.9f, 0.6f, 0.3f, 0.0f, -0.3f, -0.6f, -0.8f, -1.0f })
            {
                CloudLayerPoint p = Point(CenterAround + s * Major, CenterAcross, 0.4f, 0.0f, 0.0f, noon);
                warmValues.Add(Record(golden, "warm_s" + s.ToString("0.0", CultureInfo.InvariantCulture), p, warm, table, Check, expectWeatherness: true));
            }
            // order of arrival: high first, then mid, then low/weatherness (index 0 = far ahead)
            Check(warmValues[0].Z < warmValues[3].Z, "warm: high cloud does not thicken toward the front");
            Check(warmValues[4].Y > warmValues[1].Y, "warm: mid cloud does not thicken after the high cloud");
            Check(warmValues[2].W == 0.0f && warmValues[4].W == 0.0f, "warm: base lowered ahead of s = 0.5");
            Check(warmValues[7].W > 0.5f, "warm: no base lowering at the core");
            Check(warmValues[11].W < warmValues[7].W, "warm: base still fully lowered behind the core");

            // Cold front: Cumulonimbus (Cold), a narrow band, quick clearing.
            var cold = new[] { new CloudLayerSystem(MakeSystem(table, CloudFormationProfile.Cumulonimbus,
                RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 0.9f, 1.0f)) };
            var coldValues = new List<Vector4>();
            foreach (float s in new[] { 0.9f, 0.6f, 0.3f, 0.0f, -0.3f, -0.6f, -0.9f })
            {
                CloudLayerPoint p = Point(CenterAround + s * Major, CenterAcross, 0.4f, 0.0f, 0.0f, noon);
                coldValues.Add(Record(golden, "cold_s" + s.ToString("0.0", CultureInfo.InvariantCulture), p, cold, table, Check, expectWeatherness: true));
            }
            Check(coldValues[3].W > 0.5f && coldValues[0].W < coldValues[3].W && coldValues[6].W < coldValues[3].W, "cold: weatherness is not a core band");

            // Storm: a Cumulonimbus and a Cb-free neighbour; centre, half radius and outside.
            foreach (float fraction in new[] { 0.0f, 0.5f, 1.2f })
            {
                CloudLayerPoint p = Point(CenterAround, CenterAcross + fraction * Minor, 0.5f, 0.0f, 0.0f, noon);
                Vector4 map = Record(golden, "storm_u" + fraction.ToString("0.0", CultureInfo.InvariantCulture), p, cold, table, Check, expectWeatherness: true);
                if (fraction == 0.0f) Check(map.W > 0.5f && map.Z > 0.5f, "storm: core lacks weatherness or anvil cirrus");
            }

            // Diurnal sweep: the sun rotates about the ring axis; hours after local noon must increase 1 h per step, d peaks ~3 h after noon.
            float bestHours = -99.0f, bestFactor = -1.0f, previousHours = float.NaN;
            for (int hour = 0; hour < 24; hour++)
            {
                Vector3 sun = new Basis(Vector3.Up, hour * MathF.Tau / 24.0f) * noon;
                float hours = RingCloudLayers.LocalSolarHours(0.0f, Circumference, sun);
                float factor = RingCloudLayers.DiurnalCoverageFactor(0.0f, Circumference, sun, table);
                if (hour > 0)
                {
                    float step = hours - previousHours;
                    step -= 24.0f * MathF.Round(step / 24.0f);
                    Check(MathF.Abs(step - 1.0f) < 1e-3f, "diurnal: hour step " + step);
                }
                previousHours = hours;
                Check(factor >= table.DiurnalFloor - 1e-4f && factor <= 1.0001f, "diurnal: factor out of range " + factor);
                if (factor > bestFactor) { bestFactor = factor; bestHours = hours; }
                CloudLayerPoint p = Point(CenterAround, CenterAcross, 0.5f, 0.0f, 0.0f, sun) with { CanonicalAround = 0.0f };
                Record(golden, "diurnal_" + hour.ToString("00"), p, noSystems, table, Check, expectWeatherness: false);
            }
            Check(MathF.Abs(bestHours - table.DiurnalPeakHoursAfterNoon) <= 1.01f, "diurnal: peak at " + bestHours + " h");

            CheckRealDayNightCycle(table, Check);

            return Finish(golden, capture, failures);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
    }

    /// <summary>
    /// The diurnal phase against the real sun: CelestialEclipseMath.EvaluateFrame is the function RingDayNightCycle drives the StarLight with. The
    /// surface up is the explorer's (RingVoxelExplorerController.ResolveCanonicalSurfaceUp) and the ring axis is +Y in the grid frame, so the frame's ToSun is the
    /// bridge's sunLocal for an identity grid. At two ring positions, the hour angle must be 0 at the real elevation maximum, positive (afternoon)
    /// while the real elevation falls from it, negative while it rises, and advance +1 h per real hour.
    /// </summary>
    /// <summary>WO8: the menu amount biases are a no-op at 0 (bit-identical), monotonic, bounded, and DaytimeCumulus 0 removes the diurnal swing.</summary>
    static void CheckAmountBiases(CloudLayerTable table, Vector3 noon, Action<bool, string> check)
    {
        var noSystems = Array.Empty<CloudLayerSystem>();
        CloudLayerPoint p = Point(CenterAround, CenterAcross, 0.45f, 0.10f, 0.05f, noon);
        Vector4 baseline = RingCloudLayers.EvaluateMap(p, noSystems, table);
        try
        {
            table.LowAmountBias = 0.0f; table.MidAmountBias = 0.0f; table.HighAmountBias = 0.0f; table.DaytimeCumulus = 1.0f;
            check(RingCloudLayers.EvaluateMap(p, noSystems, table) == baseline, "bias 0 changed the map");
            foreach (float bias in new[] { -1.0f, -0.5f, 0.5f, 1.0f })
            {
                table.LowAmountBias = bias; table.MidAmountBias = bias; table.HighAmountBias = bias;
                Vector4 v = RingCloudLayers.EvaluateMap(p, noSystems, table);
                bool up = bias > 0.0f;
                check(up ? v.X > baseline.X && v.Y > baseline.Y && v.Z > baseline.Z : v.X < baseline.X && v.Y < baseline.Y && v.Z < baseline.Z, "bias " + bias + " wrong direction");
                check(v.X >= 0.0f && v.X <= 1.0f && v.Y >= 0.0f && v.Y <= 1.0f && v.Z >= 0.0f && v.Z <= 1.0f, "bias " + bias + " out of range");
                if (bias == -1.0f) check(v.X == 0.0f && v.Y == 0.0f && v.Z == 0.0f, "bias -1 leaves fair coverage");
            }
            table.LowAmountBias = 0.5f; table.MidAmountBias = 0.0f; table.HighAmountBias = 0.0f;
            Vector4 lowOnly = RingCloudLayers.EvaluateMap(p, noSystems, table);
            check(lowOnly.Y == baseline.Y && lowOnly.Z == baseline.Z && lowOnly.X > baseline.X, "low bias leaks into mid / high");
            table.LowAmountBias = 0.0f;
            // Night-side point with the diurnal swing on, then scaled out.
            CloudLayerPoint night = p with { SunLocal = new Vector3(1.0f, 0.0f, 0.0f) };
            float full = RingCloudLayers.EvaluateMap(night, noSystems, table).X;
            table.DaytimeCumulus = 0.0f;
            float flat = RingCloudLayers.EvaluateMap(night, noSystems, table).X;
            check(full < flat, "daytime cumulus 1 does not thin the night side");
            check(flat == RingCloudLayers.EvaluateMap(p, noSystems, table).X, "daytime cumulus 0 still varies with the sun");
            // The UBO carries the biases in its spare slots without growing.
            table.LowAmountBias = 0.25f; table.MidAmountBias = -0.5f; table.HighAmountBias = 0.75f; table.DaytimeCumulus = 0.5f;
            var block = new byte[RingCloudLayers.BlockBytes];
            RingCloudLayers.BuildBlock(table, Vector3.Zero, block);
            float At(int vec4, int c) => BitConverter.ToSingle(block, (vec4 * 4 + c) * 4);
            check(At(1, 3) == 0.25f && At(3, 2) == -0.5f && At(3, 3) == 0.75f && At(0, 3) == table.DiurnalStrength * 0.5f, "block slots wrong");
        }
        finally
        {
            table.LowAmountBias = 0.0f; table.MidAmountBias = 0.0f; table.HighAmountBias = 0.0f; table.DaytimeCumulus = 1.0f;
        }
    }

    static void CheckRealDayNightCycle(CloudLayerTable table, Action<bool, string> check)
    {
        var configuration = new CelestialConfiguration();
        double day = CelestialConfiguration.DefaultRingDaySeconds;
        var epochSun = new Vector3(0.35f, 0.0f, -0.93f).Normalized();
        foreach (float fraction in new[] { 0.0f, 0.23f, 0.61f })
        {
            float around = fraction * Circumference;
            float angle = around / Circumference * MathF.Tau;
            var up = new Vector3(-MathF.Cos(angle), 0.0f, -MathF.Sin(angle)).Normalized();
            const int Steps = 480;
            float peakElevation = -999.0f;
            double peakSeconds = 0.0;
            var hours = new float[Steps];
            var elevation = new float[Steps];
            for (int k = 0; k < Steps; k++)
            {
                double seconds = day * k / Steps;
                CelestialFrame frame = CelestialEclipseMath.EvaluateFrame(seconds, epochSun, up, Vector3.Up, configuration, Vector3.Zero, false);
                hours[k] = RingCloudLayers.LocalSolarHours(around, Circumference, frame.ToSun);
                elevation[k] = frame.ToSun.Dot(up);
                if (elevation[k] > peakElevation) { peakElevation = elevation[k]; peakSeconds = seconds; }
            }
            int peak = (int)Math.Round(peakSeconds / day * Steps) % Steps;
            check(MathF.Abs(hours[peak]) < 0.2f, $"real sun @{fraction}: hour angle {hours[peak]} at the elevation peak");
            int falling = 0, rising = 0, wrong = 0;
            for (int k = 0; k < Steps; k++)
            {
                float next = elevation[(k + 1) % Steps] - elevation[k];
                if (hours[k] > 0.5f && hours[k] < 5.5f) { falling++; if (next >= 0.0f) wrong++; }
                if (hours[k] < -0.5f && hours[k] > -5.5f) { rising++; if (next <= 0.0f) wrong++; }
                float step = hours[(k + 1) % Steps] - hours[k];
                step -= 24.0f * MathF.Round(step / 24.0f);
                if (MathF.Abs(step - 24.0f / Steps) > 5e-3f) { wrong++; break; }
            }
            check(falling > 0 && rising > 0 && wrong == 0, $"real sun @{fraction}: afternoon is not the falling-elevation side ({wrong} violations)");
        }
    }


    static void CheckGenusRows(CloudLayerTable table, Action<bool, string> check)
    {
        void Lowers(CloudFormationProfile profile, RingVolumetricCloudLayer.WeatherSystemKind kind, bool expected)
            => check(table.LowersBase(profile, kind) == expected, $"LOWERS_BASE {profile}/{kind} expected {expected}");
        var none = RingVolumetricCloudLayer.WeatherSystemKind.CumulusCluster;
        var bank = RingVolumetricCloudLayer.WeatherSystemKind.CloudBank;
        var front = RingVolumetricCloudLayer.WeatherSystemKind.MajorFront;
        Lowers(CloudFormationProfile.Cumulus, front, false);
        Lowers(CloudFormationProfile.Stratocumulus, none, false);
        Lowers(CloudFormationProfile.Stratocumulus, bank, true);
        Lowers(CloudFormationProfile.Stratocumulus, front, true);
        Lowers(CloudFormationProfile.Stratus, none, true);
        Lowers(CloudFormationProfile.Nimbostratus, none, true);
        Lowers(CloudFormationProfile.Altocumulus, front, false);
        Lowers(CloudFormationProfile.Altostratus, bank, false);
        Lowers(CloudFormationProfile.Altostratus, front, true);
        Lowers(CloudFormationProfile.Cirrus, front, false);
        Lowers(CloudFormationProfile.Cirrostratus, front, false);
        Lowers(CloudFormationProfile.Cirrocumulus, front, false);
        Lowers(CloudFormationProfile.StormTower, none, true);
        Lowers(CloudFormationProfile.Cumulonimbus, none, true);
    }

    static CloudLayerPoint Point(float around, float across, float coverage, float clusterMid, float clusterHigh, Vector3 sunLocal)
        => new(around, across, Vector2.Zero, Circumference, coverage, 0.4f, clusterMid, clusterHigh, 0.0f, sunLocal);

    static RingVolumetricCloudLayer.RenderWeatherSystem MakeSystem(
        CloudLayerTable table, CloudFormationProfile profile, RingVolumetricCloudLayer.WeatherSystemKind kind, float density, float support)
    {
        uint flags = (profile is CloudFormationProfile.Cumulonimbus or CloudFormationProfile.StormTower
            ? RingVolumetricCloudLayer.RenderWeatherFlagSculpt : 0u)
            | (table.LowersBase(profile, kind) ? RingVolumetricCloudLayer.RenderWeatherFlagLowersBase : 0u);
        return new RingVolumetricCloudLayer.RenderWeatherSystem(
            4242, kind, CenterAround, CenterAcross, Major, Minor, 1.0f, 0.0f, density, 0.5f, 500.0f, 18000.0f, profile, flags, 0.0f, support);
    }

    static Vector4 Record(Golden golden, string name, CloudLayerPoint point, IReadOnlyList<CloudLayerSystem> systems,
        CloudLayerTable table, Action<bool, string> check, bool expectWeatherness)
    {
        Vector4 map = RingCloudLayers.EvaluateMap(point, systems, table);
        CloudLayerColumn column = RingCloudLayers.Resolve(map, table);
        var values = new List<float> { map.X, map.Y, map.Z, map.W };
        float previousBase = -1.0f;
        for (int i = 0; i < 3; i++)
        {
            CloudLayerSlot slot = column.Layers[i];
            values.Add(slot.BaseMeters);
            values.Add(slot.TopMeters);
            check(slot.TopMeters <= RingCloudLayers.CeilingMeters, name + ": top above 18000 in layer " + i);
            check(slot.TopMeters >= slot.BaseMeters, name + ": top below base in layer " + i);
            check(slot.BaseMeters >= previousBase, name + ": layers not ordered at " + i);
            check(slot.Coverage >= 0.0f && slot.Coverage <= 1.0f, name + ": coverage out of range in layer " + i);
            previousBase = slot.BaseMeters;
        }
        check(map.W >= 0.0f && map.W <= 1.0f, name + ": weatherness out of range");
        check(column.Low.BaseMeters >= 1000.0f || map.W > 0.0f, name + ": base below 1 km with weatherness 0");
        if (!expectWeatherness) check(map.W == 0.0f, name + ": weatherness without a system");
        golden.Cases.Add(new Case { Name = name, Values = values.ToArray() });
        return map;
    }

    static int Finish(Golden golden, bool capture, List<string> failures)
    {
        string fixture = ProjectSettings.GlobalizePath(GoldenPath);
        if (capture)
        {
            if (File.Exists(fixture)) { Console.Error.WriteLine("CLOUD_LAYER_COLUMN=FAIL refusing to overwrite " + fixture); return 1; }
            if (failures.Count != 0) { Console.Error.WriteLine("CLOUD_LAYER_COLUMN=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
            File.WriteAllText(fixture, JsonSerializer.Serialize(golden, JsonOptions) + System.Environment.NewLine);
            Console.WriteLine("CLOUD_LAYER_COLUMN_CAPTURE cases=" + golden.Cases.Count);
            return 0;
        }
        Golden? expected = File.Exists(fixture) ? JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions) : null;
        if (expected == null) failures.Add("golden missing: " + fixture);
        else
        {
            if (expected.Format != Format) failures.Add("golden format " + expected.Format);
            if (expected.Cases.Count != golden.Cases.Count) failures.Add($"case count {golden.Cases.Count} vs golden {expected.Cases.Count}");
            else
                for (int i = 0; i < golden.Cases.Count; i++)
                {
                    Case a = golden.Cases[i], b = expected.Cases[i];
                    if (a.Name != b.Name || a.Values.Length != b.Values.Length) { failures.Add("case layout " + a.Name); continue; }
                    for (int k = 0; k < a.Values.Length; k++)
                        if (MathF.Abs(a.Values[k] - b.Values[k]) > Tolerance * MathF.Max(1.0f, MathF.Abs(b.Values[k])))
                        { failures.Add($"{a.Name}[{k}] {a.Values[k]} vs golden {b.Values[k]}"); break; }
                }
        }
        if (failures.Count != 0) { Console.Error.WriteLine("CLOUD_LAYER_COLUMN=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("CLOUD_LAYER_COLUMN=PASS cases=" + golden.Cases.Count);
        return 0;
    }

    sealed class Golden
    {
        public string Format { get; set; } = "";
        public List<Case> Cases { get; set; } = new();
    }

    sealed class Case
    {
        public string Name { get; set; } = "";
        public float[] Values { get; set; } = Array.Empty<float>();
    }
}
