using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Generation 1 fingerprint test (docs/landscape-audit-t0-spec.md WO T0-6). Recomputes row 1 from the pinned world and compares every field with
/// tools/fixtures/world_generation_fingerprint_golden.json. Flags (after `--`): --capture (append row 1 if the golden has no row for the generation; never rewrites a row),
/// --print (print the row JSON), --golden=&lt;path&gt; (compare against another golden file, for the can-it-fail check). Run via tools/world_generation_fingerprint_test.gd.
/// </summary>
public partial class WorldGenerationFingerprintTest : RefCounted
{
    const string GoldenPath = "res://tools/fixtures/world_generation_fingerprint_golden.json";
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static readonly HashSet<string> Informational = new() { "captured" };

    static bool Flag(string flag) => OS.GetCmdlineUserArgs().Contains(flag);
    static string Arg(string prefix)
    {
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return "";
    }

    public bool Run()
    {
        try { return RunChecks(); }
        catch (Exception exception) { GD.PushError("WORLD_GENERATION_FINGERPRINT=FAIL " + exception); return false; }
    }

    static bool RunChecks()
    {
        var clock = Stopwatch.StartNew();
        string fixturePath = ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath);
        LandscapePoseFinder.Fixture fixture = LandscapePoseFinder.ReadFixture(fixturePath);
        LandscapeWorld.Worker[] workers = LandscapeWorld.CreateWorkers(1);
        Dictionary<string, object?> row;
        try { row = WorldGenerationFingerprint.Compute(workers[0], fixture, "tools/fixtures/landscape_poses_v8_s1337.json"); }
        finally { foreach (var w in workers) w.Dispose(); }
        row["captured"] = DateTime.UtcNow.ToString("yyyy-MM-dd");
        GD.Print($"WORLD_GENERATION_FINGERPRINT_COMPUTED seconds={clock.Elapsed.TotalSeconds:F1}");

        string golden = Arg("--golden=");
        string path = golden.Length > 0 ? golden : ProjectSettings.GlobalizePath(GoldenPath);
        if (Flag("--print")) GD.Print(JsonSerializer.Serialize(row, Pretty));

        if (Flag("--capture"))
        {
            var rows = new List<object?>();
            if (File.Exists(path))
            {
                using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(path));
                foreach (JsonElement e in existing.RootElement.GetProperty("rows").EnumerateArray())
                {
                    if (e.GetProperty("generation").GetInt32() == WorldGenerationFingerprint.Generation)
                    { GD.PushError("WORLD_GENERATION_FINGERPRINT=FAIL --capture refused: the golden already has a row for generation " + WorldGenerationFingerprint.Generation); return false; }
                    rows.Add(JsonSerializer.Deserialize<Dictionary<string, object?>>(e.GetRawText()));
                }
            }
            rows.Add(row);
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?> { ["schema"] = 1, ["rows"] = rows }, Pretty) + "\n");
            GD.Print("WORLD_GENERATION_FINGERPRINT_CAPTURED " + path);
            return true;
        }

        if (!File.Exists(path)) { GD.PushError("WORLD_GENERATION_FINGERPRINT=FAIL golden missing: " + path); return false; }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement? stored = null;
        foreach (JsonElement e in doc.RootElement.GetProperty("rows").EnumerateArray())
            if (e.GetProperty("generation").GetInt32() == WorldGenerationFingerprint.Generation) stored = e;
        if (stored == null) { GD.PushError("WORLD_GENERATION_FINGERPRINT=FAIL golden has no row for generation 1"); return false; }

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Flatten("", stored.Value, expected);
        using (JsonDocument now = JsonDocument.Parse(JsonSerializer.Serialize(row))) Flatten("", now.RootElement, actual);

        var differences = new List<string>();
        foreach (string key in expected.Keys.Union(actual.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (Informational.Contains(key)) continue;
            expected.TryGetValue(key, out string? e);
            actual.TryGetValue(key, out string? a);
            if (e != a) differences.Add($"{key}: golden {e ?? "<absent>"} now {a ?? "<absent>"}");
        }
        if (differences.Count > 0)
        {
            foreach (string d in differences) GD.PushError("WORLD_GENERATION_FINGERPRINT diff " + d);
            GD.PushError($"WORLD_GENERATION_FINGERPRINT=FAIL {differences.Count} field(s) differ from the generation 1 golden");
            return false;
        }
        GD.Print($"WORLD_GENERATION_FINGERPRINT_ROW heights={actual["heights.hash"]} sampler={actual["sampler.hash"]} catalog={actual["catalog.hash"]} fields={actual.Count} seconds={clock.Elapsed.TotalSeconds:F1}");
        return true;
    }

    static void Flatten(string prefix, JsonElement e, SortedDictionary<string, string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in e.EnumerateObject()) Flatten(prefix.Length == 0 ? p.Name : prefix + "." + p.Name, p.Value, into);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (JsonElement item in e.EnumerateArray()) Flatten($"{prefix}[{i++}]", item, into);
                break;
            default:
                into[prefix] = e.ToString();
                break;
        }
    }
}
