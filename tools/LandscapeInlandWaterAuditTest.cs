using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>
/// Landscape audit T0-4: which inland-water (Lake, River) paths still work in a generation 1 (gen-17) world.
/// Pins that gen-17 has no inland water, finds gen-16 lake and river samples, builds real 64 m proxy sectors
/// over them (private BuildRawSector, by reflection) and records what the proxy emits for inland water.
/// Writes inland_water_audit.json and .md under .godot/audit_baseline/common/t0/water/&lt;stamp&gt;/.
/// </summary>
public partial class LandscapeInlandWaterAuditTest : RefCounted
{
    private const string Marker = "LANDSCAPE_INLAND_WATER";
    private const int Seed = 1337;
    private const int ProxyCells = 32;
    private const int ProxySpacing = 2;

    private readonly Dictionary<string, object> _report = new();
    private readonly List<string> _notes = new();
    private readonly StringBuilder _md = new();

    public bool Run()
    {
        try
        {
            var total = Stopwatch.StartNew();
            _md.AppendLine("# Inland water audit (T0-4)");
            _md.AppendLine();
            Gen17HasNoInlandWater();
            (long Around, int Across, TerrainSample Sample)? lake = null, river = null;
            Gen16InlandSamplesExist(ref lake, ref river);
            ProxySectorInlandWater(lake, river);
            WaterKindEncodingRoundtrip();
            OceanProxyShaderProfiles();
            _report["notes"] = _notes;
            _report["elapsed_s"] = Math.Round(total.Elapsed.TotalSeconds, 1);
            WriteOutputs();
            GD.Print($"{Marker}=PASS");
            return true;
        }
        catch (Exception error)
        {
            GD.PushError($"{Marker}=FAIL {error.Message}");
            GD.PrintErr(error.ToString());
            return false;
        }
    }

    // ---- test 1 ----------------------------------------------------------------------------------------------

    private void Gen17HasNoInlandWater()
    {
        // LandscapeWorld (T0-1) is not required for this audit; the water rule does not depend on climate or biome settings.
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17WeatherLung(Seed);
        long circumference = sampler.AroundRingVoxelLength;
        int width = sampler.RingAcrossWidthMeters;
        var watch = Stopwatch.StartNew();
        int latticeCount = 0, oceanCount = 0, minimaCount = 0;
        long lakeOrRiver = 0, badOcean = 0;

        void Check(long around, int across, bool minimum)
        {
            TerrainSample sample = sampler.GetTerrainSample(around, across);
            if (minimum) minimaCount++; else latticeCount++;
            if (sample.WaterKind is SurfaceWaterKind.Lake or SurfaceWaterKind.River) lakeOrRiver++;
            if (sample.HasSurfaceWater)
            {
                oceanCount++;
                if (sample.WaterKind != SurfaceWaterKind.Ocean || sample.WaterSurfaceHeight != 0) badOcean++;
            }
        }

        // 100,000 lattice points: 400 around x 250 across.
        for (int i = 0; i < 400; i++)
        for (int j = 0; j < 250; j++)
            Check((long)Math.Floor(i * (circumference / 400.0)) + 1, 9000 + j * 1200, false);

        // 20,000 lowland local minima: steepest descent on the shaped (presentation) height, 64 m steps, from
        // deterministic starts. Descent depth is capped, so a start may stop short of the true minimum.
        const int starts = 20_000, maxSteps = 24, step = 64;
        int reachedMinimum = 0;
        ulong state = 0x9E3779B97F4A7C15UL;
        for (int s = 0; s < starts; s++)
        {
            long around = (long)(NextUnit(ref state) * circumference);
            int across = 9000 + (int)(NextUnit(ref state) * (width - 18000));
            float height = sampler.GetPresentationSurfaceHeight(around, across);
            bool atMinimum = false;
            for (int n = 0; n < maxSteps; n++)
            {
                long bestAround = around; int bestAcross = across; float best = height;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    long a = ((around + dx * step) % circumference + circumference) % circumference;
                    int c = Math.Clamp(across + dz * step, 0, width);
                    float h = sampler.GetPresentationSurfaceHeight(a, c);
                    if (h < best) { best = h; bestAround = a; bestAcross = c; }
                }
                if (best >= height) { atMinimum = true; break; }
                around = bestAround; across = bestAcross; height = best;
            }
            if (atMinimum) reachedMinimum++;
            Check(around, across, true);
        }

        var result = new Dictionary<string, object>
        {
            ["generator"] = "EmitterSiteTestWorlds.CreateGen17WeatherLung(1337, climate: false)",
            ["lattice_points"] = latticeCount,
            ["minima_points"] = minimaCount,
            ["minima_reached_true_minimum"] = reachedMinimum,
            ["water_samples"] = oceanCount,
            ["lake_or_river_samples"] = lakeOrRiver,
            ["non_ocean_or_nonzero_surface_water_samples"] = badOcean,
            ["elapsed_s"] = Math.Round(watch.Elapsed.TotalSeconds, 1)
        };
        _report["gen17_has_no_inland_water"] = result;
        _md.AppendLine("## 1. gen17_has_no_inland_water");
        _md.AppendLine($"- lattice {latticeCount}, minima starts {minimaCount} ({reachedMinimum} reached a true 64 m minimum within {maxSteps} steps)");
        _md.AppendLine($"- water samples {oceanCount}, Lake or River samples {lakeOrRiver}, water samples that are not Ocean at surface 0: {badOcean}");
        _md.AppendLine();
        Require(latticeCount == 100_000 && minimaCount == starts, "gen17 sample counts");
        Require(lakeOrRiver == 0, $"gen17 has {lakeOrRiver} Lake/River samples");
        Require(badOcean == 0, $"gen17 has {badOcean} water samples that are not Ocean with WaterSurfaceHeight 0");
        Require(oceanCount > 0, "gen17 audit saw no ocean at all (sampling is not reaching water)");
        GD.Print($"{Marker} test=gen17_has_no_inland_water result=PASS lattice={latticeCount} minima={minimaCount} ocean={oceanCount} lake_river=0");
    }

    private static double NextUnit(ref ulong state)
    {
        state ^= state << 13; state ^= state >> 7; state ^= state << 17;
        return (state >> 11) * (1.0 / (1UL << 53));
    }

    // ---- test 2 ----------------------------------------------------------------------------------------------

    private void Gen16InlandSamplesExist(
        ref (long Around, int Across, TerrainSample Sample)? lake,
        ref (long Around, int Across, TerrainSample Sample)? river)
    {
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen16(Seed);
        long circumference = sampler.AroundRingVoxelLength;
        int width = sampler.RingAcrossWidthMeters;
        const int maxProbes = 2_000_000;
        const double phiA = 0.6180339887498949, phiB = 0.7548776662466927;
        int probes = 0;
        int oceanSeen = 0;
        var watch = Stopwatch.StartNew();
        for (int n = 1; n <= maxProbes && (lake == null || river == null); n++)
        {
            probes = n;
            long around = (long)((n * phiA % 1.0) * circumference);
            int across = 20000 + (int)((n * phiB % 1.0) * (width - 40000));
            TerrainSample sample = sampler.GetTerrainSample(around, across);
            if (sample.WaterKind == SurfaceWaterKind.Ocean) oceanSeen++;
            if (sample.WaterKind == SurfaceWaterKind.Lake && lake == null) lake = (around, across, sample);
            else if (sample.WaterKind == SurfaceWaterKind.River && river == null) river = (around, across, sample);
        }
        var result = new Dictionary<string, object>
        {
            ["probes"] = probes,
            ["scan"] = "Weyl sequence n*0.6180339887 (around), n*0.7548776662 (across, 20000..width-20000), seed 1337",
            ["ocean_samples_seen"] = oceanSeen,
            ["lake"] = Describe(lake),
            ["river"] = Describe(river),
            ["elapsed_s"] = Math.Round(watch.Elapsed.TotalSeconds, 1)
        };
        _report["gen16_inland_samples_exist"] = result;
        _md.AppendLine("## 2. gen16_inland_samples_exist");
        _md.AppendLine($"- probes used {probes} of {maxProbes}");
        _md.AppendLine($"- lake: {DescribeText(lake)}");
        _md.AppendLine($"- river: {DescribeText(river)}");
        _md.AppendLine();
        if (lake == null || river == null)
        {
            string note = $"gen16_inland_samples_exist: not found within {maxProbes} probes (lake={(lake != null)}, river={(river != null)})";
            _notes.Add(note);
            GD.Print($"{Marker} test=gen16_inland_samples_exist result=SKIP {note}");
        }
        else
        {
            GD.Print($"{Marker} test=gen16_inland_samples_exist result=PASS probes={probes} lake={DescribeText(lake)} river={DescribeText(river)}");
        }
    }

    private static Dictionary<string, object>? Describe((long Around, int Across, TerrainSample Sample)? hit) => hit == null ? null : new()
    {
        ["around"] = hit.Value.Around,
        ["across"] = hit.Value.Across,
        ["surface_height"] = hit.Value.Sample.SurfaceHeight,
        ["water_surface_height"] = hit.Value.Sample.WaterSurfaceHeight,
        ["water_depth"] = hit.Value.Sample.WaterDepth,
        ["biome"] = hit.Value.Sample.Biome.ToString()
    };

    private static string DescribeText((long Around, int Across, TerrainSample Sample)? hit) => hit == null
        ? "not found"
        : $"around={hit.Value.Around} across={hit.Value.Across} surface={hit.Value.Sample.SurfaceHeight} water={hit.Value.Sample.WaterSurfaceHeight} depth={hit.Value.Sample.WaterDepth} biome={hit.Value.Sample.Biome}";

    // ---- test 3 ----------------------------------------------------------------------------------------------

    private void ProxySectorInlandWater(
        (long Around, int Across, TerrainSample Sample)? lake,
        (long Around, int Across, TerrainSample Sample)? river)
    {
        if (lake == null && river == null)
        {
            _notes.Add("proxy_sector_inland_water: skipped, no gen-16 inland sample");
            GD.Print($"{Marker} test=proxy_sector_inland_water result=SKIP no gen-16 inland sample");
            return;
        }
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen16(Seed);
        var edits = new RingLodSurfaceEditSnapshot(0, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(),
            new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);
        var sectors = new Dictionary<string, object>();
        _md.AppendLine("## 3. proxy_sector_inland_water");
        _md.AppendLine($"64 m sector ({ProxyCells} cells at {ProxySpacing} m), built by the private `BuildRawSector` through reflection on `AllocationFixtureSession`.");
        _md.AppendLine();
        _md.AppendLine("| Sector | Water-half vertices by kind (None/Ocean/Lake/River) | OceanIndices triangles by kind (None/Ocean/Lake/River) | Indices triangles | Water triangles | FullyOceanSurface | ShorelineTile |");
        _md.AppendLine("|---|---|---|---|---|---|---|");
        if (lake != null) sectors["lake"] = BuildSector("lake", sampler, edits, lake.Value, SurfaceWaterKind.Lake);
        if (river != null) sectors["river"] = BuildSector("river", sampler, edits, river.Value, SurfaceWaterKind.River);
        _md.AppendLine();
        _report["proxy_sector_inland_water"] = sectors;
    }

    private Dictionary<string, object> BuildSector(string label, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        (long Around, int Across, TerrainSample Sample) hit, SurfaceWaterKind wanted)
    {
        const int size = ProxyCells * ProxySpacing;
        var coordinate = new RingTerrainSectorCoordinate(hit.Around / size, hit.Across / size);
        long canonicalAround = coordinate.AroundSector * (long)size;
        int canonicalAcross = coordinate.AcrossSector * size;
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0, sampler.AroundRingVoxelLength,
            CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
            new RingCanonicalPosition(canonicalAround, 0.0, canonicalAcross));

        using RingTerrainProxySector.AllocationFixtureSession session = RingTerrainProxySector
            .CreateAllocationFixtureSession(edits, sampler, geometry, sampler.RingAcrossWidthMeters);

        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        Type sessionType = typeof(RingTerrainProxySector.AllocationFixtureSession);
        object cache = Field(sessionType, "_cache", instance).GetValue(session)!;
        object sessionEdits = Field(sessionType, "_edits", instance).GetValue(session)!;
        object sessionGeometry = Field(sessionType, "_geometry", instance).GetValue(session)!;
        MethodInfo build = typeof(RingTerrainProxySector).GetMethod("BuildRawSector", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRawSector not found by reflection");

        // Same arguments AllocationFixtureSession.Build passes (RingTerrainProxySector.Allocation.Tests.cs:68-93), skirts off.
        object[] args =
        {
            1UL, coordinate, sessionEdits, sampler, cache, sessionGeometry, Transform3D.Identity,
            size, ProxySpacing, ProxySpacing * 2, null!, ProxyCells, 0.0f, 0.0f, false,
            sampler.RingAcrossWidthMeters, false, false, 32.0f, false, false, 1, Colors.Black, Colors.White, null!
        };
        object data = build.Invoke(null, args) ?? throw new InvalidOperationException("BuildRawSector returned null");
        Type dataType = data.GetType();
        Color[] colors = (Color[])Prop(dataType, "Colors").GetValue(data)!;
        Vector3[] vertices = (Vector3[])Prop(dataType, "Vertices").GetValue(data)!;
        int[] indices = (int[])Prop(dataType, "Indices").GetValue(data)!;
        int[] oceanIndices = (int[])Prop(dataType, "OceanIndices").GetValue(data)!;
        object? shoreline = Prop(dataType, "ShorelineTile").GetValue(data);

        // Vertex layout (RingTerrainProxySector.cs:3837): surface vertices first (row*row), then the water copy.
        int row = ProxyCells + 1;
        int surfaceCount = row * row;
        Require(colors.Length >= surfaceCount * 2, $"{label}: expected a water half in Colors");
        var vertexKinds = new int[4];
        for (int i = surfaceCount; i < surfaceCount * 2; i++) vertexKinds[(int)DecodeKind(colors[i].A)]++;

        // Indices holds the terrain surface only. Every displayed-water triangle, of any kind, is in OceanIndices (the name is
        // historical): RingTerrainProxySector.cs:4007-4019 adds full-wet triangles regardless of kind and the clipped partial
        // triangles are appended to the same list through AppendClippedWaterTriangle.
        int surfaceTriangles = 0, waterInSurfaceList = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
            if (indices[t] < surfaceCount) surfaceTriangles++; else waterInSurfaceList++;
        var triangleKinds = new int[4];
        int mixed = 0;
        for (int t = 0; t + 2 < oceanIndices.Length; t += 3)
        {
            SurfaceWaterKind ka = DecodeKind(colors[oceanIndices[t]].A), kb = DecodeKind(colors[oceanIndices[t + 1]].A), kc = DecodeKind(colors[oceanIndices[t + 2]].A);
            if (ka == kb && kb == kc) triangleKinds[(int)ka]++; else mixed++;
        }
        int waterTriangles = oceanIndices.Length / 3;
        bool oceanPresent = vertexKinds[(int)SurfaceWaterKind.Ocean] > 0;
        bool fullyOcean = (bool)Prop(dataType, "FullyOceanSurface").GetValue(data)!;

        string kinds(int[] v) => $"{v[0]}/{v[1]}/{v[2]}/{v[3]}";
        _md.AppendLine($"| {label} ({hit.Around},{hit.Across}) | {kinds(vertexKinds)} | {kinds(triangleKinds)} (mixed {mixed}) | {indices.Length / 3} ({surfaceTriangles} surface, {waterInSurfaceList} water) | {waterTriangles} | {fullyOcean} | {(shoreline != null ? "present" : "null")} |");

        Require(vertexKinds[(int)wanted] > 0, $"{label}: no {wanted} vertices in the water half of the proxy sector");
        Require(triangleKinds[(int)wanted] > 0, $"{label}: no {wanted} water triangles reach the water index list");
        Require(waterInSurfaceList == 0, $"{label}: water triangles found in the terrain index list");
        if (!oceanPresent) Require(!fullyOcean, $"{label}: ocean-free sector flagged FullyOceanSurface");
        GD.Print($"{Marker} test=proxy_sector_inland_water sector={label} result=PASS vertices[{kinds(vertexKinds)}] water_index_triangles[{kinds(triangleKinds)}] mixed={mixed} total_water_triangles={waterTriangles} fully_ocean={fullyOcean} shoreline={(shoreline != null)}");
        return new Dictionary<string, object>
        {
            ["coordinate"] = new[] { coordinate.AroundSector, coordinate.AcrossSector },
            ["vertex_count"] = vertices.Length,
            ["water_half_vertices_none_ocean_lake_river"] = vertexKinds,
            ["water_index_list_triangles_none_ocean_lake_river"] = triangleKinds,
            ["mixed_kind_water_triangles"] = mixed,
            ["surface_triangles_in_Indices"] = surfaceTriangles,
            ["water_triangles_in_Indices"] = waterInSurfaceList,
            ["water_triangles_in_OceanIndices"] = waterTriangles,
            ["ocean_vertices_in_sector"] = oceanPresent,
            ["fully_ocean_surface"] = fullyOcean,
            ["shoreline_tile_present"] = shoreline != null,
            ["finding"] = "OceanIndices is the displayed-water index list for every kind; inland water is emitted with its kind in vertex alpha. Only the FFT coverage mask, FullyOceanSurface and the shoreline tile are Ocean-only."
        };
    }

    private static FieldInfo Field(Type type, string name, BindingFlags flags)
        => type.GetField(name, flags) ?? throw new InvalidOperationException($"field {type.Name}.{name} not found by reflection");

    private static PropertyInfo Prop(Type type, string name)
        => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"property {type.Name}.{name} not found by reflection");

    /// <summary>Inverse of RingWaterModel.EncodeWaterKind: alpha = kind / 3.</summary>
    private static SurfaceWaterKind DecodeKind(float alpha)
        => (SurfaceWaterKind)Math.Clamp((int)Math.Round(alpha * 3.0f), 0, 3);

    // ---- test 4 ----------------------------------------------------------------------------------------------

    private void WaterKindEncodingRoundtrip()
    {
        var rows = new List<string>();
        foreach (SurfaceWaterKind kind in new[] { SurfaceWaterKind.None, SurfaceWaterKind.Ocean, SurfaceWaterKind.Lake, SurfaceWaterKind.River })
        {
            Color encoded = RingWaterModel.EncodeWaterKind(new Color(0.2f, 0.4f, 0.6f, 0.5f), kind);
            SurfaceWaterKind decoded = DecodeKind(encoded.A);
            Require(decoded == kind, $"EncodeWaterKind round trip: {kind} decoded as {decoded}");
            Require(RingWaterModel.DecodeWaterKind(encoded.A) == kind, $"RingWaterModel.DecodeWaterKind disagrees for {kind}");
            Require(encoded.R == 0.2f && encoded.G == 0.4f && encoded.B == 0.6f, $"EncodeWaterKind changed RGB for {kind}");
            rows.Add($"{kind}: alpha {encoded.A:0.###}");
        }
        _report["water_kind_encoding_roundtrip"] = rows;
        _md.AppendLine("## 4. water_kind_encoding_roundtrip");
        foreach (string row in rows) _md.AppendLine("- " + row);
        _md.AppendLine();
        GD.Print($"{Marker} test=water_kind_encoding_roundtrip result=PASS");
    }

    // ---- test 5 ----------------------------------------------------------------------------------------------

    private void OceanProxyShaderProfiles()
    {
        string shader = ReadText("res://materials/ring_ocean_proxy.gdshader");
        string common = ReadText("res://materials/ring_water_common.gdshaderinc");
        string fft = ReadText("res://materials/ring_water_fft.gdshaderinc");
        string all = shader + "\n" + common + "\n" + fft;
        string[] required =
        {
            "ring_water_kind_from_vertex_alpha", "water_kind_profile", "const float lake_scale", "const float river_scale",
            "uniform float water_river_advection", "ring_water_profile_height_scale", "ring_water_profile_shore_foam_scale",
            "ring_fft_ocean_ready"
        };
        var present = new List<string>();
        foreach (string token in required)
        {
            Require(all.Contains(token, StringComparison.Ordinal), $"shader text lacks '{token}'");
            present.Add(token);
        }
        // Kind thresholds: ocean < 1.5, lake < 2.5, river otherwise; the FFT surface only serves kind ~1 (Ocean).
        Require(common.Contains("water_kind < 2.5", StringComparison.Ordinal), "lake/river kind threshold missing");
        Require(fft.Contains("abs(water_kind - 1.0) <= 0.1", StringComparison.Ordinal), "FFT ocean-only kind test changed");
        var result = new Dictionary<string, object>
        {
            ["files"] = new[] { "materials/ring_ocean_proxy.gdshader", "materials/ring_water_common.gdshaderinc (included)", "materials/ring_water_fft.gdshaderinc (included)" },
            ["tokens_present"] = present,
            ["lake_profile"] = "ring_water_common.gdshaderinc: water_kind in [1.5,2.5): lake_scale 1.65 coordinate scale, height scale 0.24, shore foam 0.30, frequency 1.45, amplitude 0.85",
            ["river_profile"] = "water_kind >= 2.5: river_scale 2.15 coordinate scale, height scale 0.11, shore foam 0.46, frequency 1.85, amplitude 0.52, advected by water_river_advection (or ring_water_river_advection_main/far globals)",
            ["fft"] = "ring_fft_ocean_ready requires abs(water_kind - 1.0) <= 0.1, so the FFT surface is Ocean-only; ring_ocean_proxy.gdshader uses kind < 1.5 gates for FFT coverage and a 0.18 factor for non-ocean"
        };
        _report["ocean_proxy_shader_profiles"] = result;
        _md.AppendLine("## 5. ocean_proxy_shader_profiles");
        _md.AppendLine("- " + result["lake_profile"]);
        _md.AppendLine("- " + result["river_profile"]);
        _md.AppendLine("- " + result["fft"]);
        _md.AppendLine();
        GD.Print($"{Marker} test=ocean_proxy_shader_profiles result=PASS");
    }

    private static string ReadText(string path)
    {
        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException($"cannot open {path}");
        return file.GetAsText();
    }

    // ---- output ----------------------------------------------------------------------------------------------

    private void WriteOutputs()
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string dir = ProjectSettings.GlobalizePath("res://.godot/audit_baseline/common/t0/water/" + stamp);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "inland_water_audit.json"),
            JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
        if (_notes.Count > 0)
        {
            _md.AppendLine("## Notes");
            foreach (string note in _notes) _md.AppendLine("- " + note);
        }
        File.WriteAllText(Path.Combine(dir, "inland_water_audit.md"), _md.ToString());
        GD.Print($"{Marker} output={dir}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
