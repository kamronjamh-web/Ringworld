using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Verifies the frozen fingerprints and generation 1 v8/v9 content parity. Run via tools/world_generation_fingerprint_test.gd.
/// Use --print to show the v8 row, --golden=&lt;path&gt; for a scratch fixture, or --capture --generation=N to append a frozen row.
/// </summary>
public partial class WorldGenerationFingerprintTest : RefCounted
{
    internal const string GoldenPath = "res://tools/fixtures/world_generation_fingerprint_golden.json";
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
        string golden = Arg("--golden=");
        string path = Path.GetFullPath(golden.Length > 0 ? golden : ProjectSettings.GlobalizePath(GoldenPath));
        WorldGenerationLedger ledger = WorldGenerationLedger.Production;
        string[] args = OS.GetCmdlineUserArgs();
        var selection = LandscapeWorld.ParseGenerationArgs(args);
        if (Flag("--capture"))
        {
            if (selection.Review.Count > 0)
                throw new InvalidOperationException("--capture cannot include review features");
            int generation = selection.Generation;
            if (generation < 1 || generation > ledger.LatestFrozen || !ledger.IsLoadable(generation))
                throw new InvalidOperationException($"--capture requires a loadable frozen --generation=N (latest frozen is {ledger.LatestFrozen})");
            Capture(path, generation);
            return true;
        }

        if (selection.IsExplicit)
        {
            if (!Flag("--print"))
                throw new InvalidOperationException("Use --print with generation flags for a diagnostic fingerprint.");
            GD.Print(JsonSerializer.Serialize(ComputeRow(LandscapeWorld.For(selection.Generation, selection.Review)), Pretty));
            return true;
        }

        SortedDictionary<int, JsonElement> rows = ReadRows(path);
        if (!rows.ContainsKey(ledger.LatestFrozen))
            throw new InvalidOperationException($"golden has no row for latest frozen generation {ledger.LatestFrozen}");
        if (!rows.ContainsKey(1)) throw new InvalidOperationException("golden has no row for generation 1");

        JsonElement legacy = ComputeRow(LandscapeWorld.Default);
        if (Flag("--print")) GD.Print(JsonSerializer.Serialize(legacy, Pretty));
        if (!Compare(rows[1], legacy, "generation 1 v8", Informational, out int legacyFields)) return false;
        GD.Print($"WORLD_GENERATION_FINGERPRINT_ROW heights={legacy.GetProperty("heights").GetProperty("hash")} sampler={legacy.GetProperty("sampler").GetProperty("hash")} catalog={legacy.GetProperty("catalog").GetProperty("hash")} fields={legacyFields + 1} seconds={clock.Elapsed.TotalSeconds:F1}");

        var v9Metadata = new HashSet<string>(Informational, StringComparer.Ordinal) { "label", "generator_id", "definition_hash" };
        if (!Compare(rows[1], ComputeRow(LandscapeWorld.For(1)), "generation 1 v9 content", v9Metadata, out int v9Fields)) return false;
        GD.Print($"V9_GEN1_EQUALS_ROW1 fields={v9Fields}");
        foreach (var (generation, stored) in rows)
        {
            if (generation == 1) continue;
            if (generation > ledger.LatestFrozen)
                throw new InvalidOperationException($"golden contains an unknown generation {generation}");
            if (!Compare(stored, ComputeRow(LandscapeWorld.For(generation)), $"generation {generation}", Informational, out _)) return false;
        }
        return true;
    }

    static JsonElement ComputeRow(LandscapeWorld.Spec spec)
    {
        LandscapePoseFinder.Fixture fixture = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        using LandscapeWorld.Worker worker = spec.CreateWorkers(1)[0];
        Dictionary<string, object?> row = WorldGenerationFingerprint.Compute(spec, worker, fixture, "tools/fixtures/landscape_poses_v8_s1337.json");
        row["captured"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return JsonSerializer.SerializeToElement(row);
    }

    internal static SortedDictionary<int, JsonElement> ReadRows(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.GetProperty("schema").GetInt32() != 1)
            throw new InvalidOperationException("unsupported fingerprint golden schema");
        var rows = new SortedDictionary<int, JsonElement>();
        foreach (JsonElement row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            int generation = row.GetProperty("generation").GetInt32();
            if (generation < 1 || !rows.TryAdd(generation, row.Clone()))
                throw new InvalidOperationException($"invalid or duplicate golden generation {generation}");
        }
        return rows;
    }

    static void Capture(string path, int generation)
    {
        string lockPath = path + ".capture.lock";
        using var captureLock = new FileStream(lockPath, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        SortedDictionary<int, JsonElement> rows = File.Exists(path) ? ReadRows(path) : new();
        if (rows.ContainsKey(generation))
            throw new InvalidOperationException($"--capture refused: the golden already has a row for generation {generation}");

        JsonElement row = ComputeRow(generation == 1 ? LandscapeWorld.Default : LandscapeWorld.For(generation));
        if (Flag("--print")) GD.Print(JsonSerializer.Serialize(row, Pretty));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = new { schema = 1, rows = rows.Values.Append(row) };
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Pretty) + "\n");
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        GD.Print("WORLD_GENERATION_FINGERPRINT_CAPTURED " + path);
    }

    static bool Compare(JsonElement stored, JsonElement now, string label, HashSet<string> excluded, out int fields)
    {
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Flatten("", stored, expected);
        Flatten("", now, actual);
        fields = actual.Keys.Count(key => !excluded.Contains(key));
        var differences = new List<string>();
        foreach (string key in expected.Keys.Union(actual.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            if (excluded.Contains(key)) continue;
            expected.TryGetValue(key, out string? e);
            actual.TryGetValue(key, out string? a);
            if (e != a) differences.Add($"{key}: golden {e ?? "<absent>"} now {a ?? "<absent>"}");
        }
        if (differences.Count == 0) return true;
        foreach (string d in differences) GD.PushError("WORLD_GENERATION_FINGERPRINT diff " + d);
        GD.PushError($"WORLD_GENERATION_FINGERPRINT=FAIL {differences.Count} field(s) differ from the {label} golden");
        return false;
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
