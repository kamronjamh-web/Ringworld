using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Isolated parity and optional timing fixture for the Terrain3D runtime cache fusion.</summary>
public partial class RuntimeSurfaceCacheBenchmark : RefCounted
{
    private const int Side = RingTerrain3DSurface.RegionSideSamples * RingTerrain3DSurface.RegionSideCount;
    private static readonly Type SurfaceType = typeof(RingTerrain3DSurface);
    private int _checks;

    public bool Run(bool benchmark)
    {
        try
        {
            TerrainGenerationParameters recipe = TerrainGenerationParameters.Default();
            using var settings = Settings(recipe.CanonicalJson);
            using var sampler = Sampler(settings);
            float foothillHeight = sampler.GetPresentationSurfaceHeight(2_356_196, 159_000);
            float mountainHeight = sampler.GetPresentationSurfaceHeight(1_570_796, 159_000);
            Check(float.IsFinite(foothillHeight) && float.IsFinite(mountainHeight), "representative heights are finite");
            ValidateFusedAndFallback(sampler);
            ValidateFusedEdits(sampler);
            ValidateCustomBaselineAndEdits(sampler);
            object? timings = benchmark ? Benchmark(sampler) : null;
            object report = new
            {
                passed = true,
                checks = _checks,
                benchmark_requested = benchmark,
                is_jit_optimizer_disabled = DetectJitOptimizerDisabled(),
                recipe_json = recipe.CanonicalJson,
                terrain_definition_hash = sampler.TerrainDefinitionHash.ToString("X16"),
                cpu = new { name = OS.GetProcessorName(), count = OS.GetProcessorCount() },
                representative_heights = new { foothill = foothillHeight, mountain = mountainHeight },
                benchmark = timings
            };
            WriteReport(report);
            GD.Print($"RUNTIME_SURFACE_CACHE=PASS checks={_checks}");
            return true;
        }
        catch (Exception error)
        {
            WriteReport(new { passed = false, checks = _checks, error = error.ToString() });
            GD.PushError($"RUNTIME_SURFACE_CACHE=FAIL checks={_checks}: {error}");
            return false;
        }
    }

    private void ValidateFusedAndFallback(RingTerrainSampler sampler)
    {
        foreach ((long around, int across, int direction) in new[]
        {
            (0L, 0, 1),
            (0L, 2, -1),
            (sampler.AroundRingVoxelLength - 2, 2, -1)
        })
        {
            var fusedField = Field(sampler, sampler.GetPresentationSurfaceHeight);
            var fallbackField = Field(sampler, (a, z) => sampler.GetPresentationSurfaceHeight(a, z));
            using var fused = new SurfaceCache(fusedField, sampler, Frame(sampler, around, across, direction));
            using var fallback = new SurfaceCache(fallbackField, sampler, Frame(sampler, around, across, direction));
            fused.Build(); fallback.Build();
            Check(fused.Fused, "method-group runtime baseline fused");
            Check(!fallback.Fused, "equivalent lambda baseline remains fallback");
            Compare(fused.Baselines, fallback.Baselines, "fused baseline parity");
            Compare(fused.Biomes, fallback.Biomes, "fused biome parity");
            ValidateOutsideAcross(fused, "fused boundary minimum/color zero");
            ValidateOutsideAcross(fallback, "fallback boundary minimum/color zero");
        }
    }

    private void ValidateCustomBaselineAndEdits(RingTerrainSampler sampler)
    {
        Func<long, int, float> custom = (around, across) => sampler.GetPresentationSurfaceHeight(around, across) + 123f;
        var field = Field(sampler, custom);
        using var cache = new SurfaceCache(field, sampler, Frame(sampler, 0, 0, 1));
        cache.Build();
        Check(!cache.Fused, "custom same-hash baseline remains fallback");
        int x = RingTerrain3DSurface.RegionSideSamples, z = RingTerrain3DSurface.RegionSideSamples;
        long around = 0; int across = 0;
        float expected = ClampBaseline(custom(around, across), field);
        Check(Bits(cache.Baselines[x, z]) == Bits(expected), "custom baseline is respected");
        float original = cache.Heights[x, z];
        Check(field.ApplyBrush(around, across, 1, 10f), "custom baseline edit accepted");
        cache.Build();
        Check(Bits(cache.Heights[x, z]) != Bits(original), "cached edit overlays baseline");
        field.Clear();
        cache.Build();
        Check(Bits(cache.Heights[x, z]) == Bits(original), "clear restores custom baseline");
    }

    private void ValidateFusedEdits(RingTerrainSampler sampler)
    {
        var field = Field(sampler, sampler.GetPresentationSurfaceHeight);
        using var cache = new SurfaceCache(field, sampler, Frame(sampler, 0, 0, 1));
        cache.Build();
        int x = RingTerrain3DSurface.RegionSideSamples, z = RingTerrain3DSurface.RegionSideSamples;
        float original = cache.Heights[x, z];
        Check(cache.Fused, "fused edit fixture remains fused");
        Check(field.ApplyBrush(0, 0, 1, 10f), "fused baseline edit accepted");
        cache.Build();
        Check(cache.Fused && Bits(cache.Heights[x, z]) != Bits(original), "fused cache overlays edit");
        field.Clear();
        cache.Build();
        Check(cache.Fused && Bits(cache.Heights[x, z]) == Bits(original), "fused clear restores baseline");
    }

    private object Benchmark(RingTerrainSampler sampler)
    {
        var cases = new List<object>();
        foreach (var point in new (string Label, long Around, int Across)[]
        {
            ("foothill", 2_356_196L, 159_000),
            ("mountain", 1_570_796L, 159_000)
        })
        {
            var fusedField = Field(sampler, sampler.GetPresentationSurfaceHeight);
            var fallbackField = Field(sampler, (a, z) => sampler.GetPresentationSurfaceHeight(a, z));
            using var fused = new SurfaceCache(fusedField, sampler, Frame(sampler, point.Around, point.Across, -1));
            using var fallback = new SurfaceCache(fallbackField, sampler, Frame(sampler, point.Around, point.Across, -1));
            for (int i = 0; i < 2; i++) { fused.Build(); fallback.Build(); }
            var fusedMs = new List<double>(); var fallbackMs = new List<double>();
            for (int i = 0; i < 8; i++)
            {
                if ((i & 1) == 0) { fusedMs.Add(Time(fused)); fallbackMs.Add(Time(fallback)); }
                else { fallbackMs.Add(Time(fallback)); fusedMs.Add(Time(fused)); }
                // Validate immediately after each measured pair, outside the timers.
                Compare(fused.Baselines, fallback.Baselines, "benchmark baseline parity " + point.Label);
                Compare(fused.Biomes, fallback.Biomes, "benchmark biome parity " + point.Label);
            }
            cases.Add(new { label = point.Label, origin_around = point.Around, origin_across = point.Across,
                samples = Side * Side, warmups = 2, rounds = 8, fused_ms = fusedMs, fallback_ms = fallbackMs });
        }
        return cases;
    }

    private static double Time(SurfaceCache cache)
    {
        var timer = Stopwatch.StartNew(); cache.Build(); timer.Stop();
        return timer.Elapsed.TotalMilliseconds;
    }

    private static RingSurfaceHeightField Field(RingTerrainSampler sampler, Func<long, int, float> baseline)
        => new(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters,
            -8000f, 8000f, baseline, useTriangleInterpolation: true);

    private static RingSurfaceFrame Frame(RingTerrainSampler sampler, long around, int across, int direction)
        => new(around, across, 0f, sampler.AroundRingVoxelLength, sampler.RingRadiusMeters, 2f, direction);

    private static VoxelTerrainSettings Settings(string json) => new()
    {
        SurfaceGenerationRecipeJson = json,
        SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
        BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
        EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
    };

    private static RingTerrainSampler Sampler(VoxelTerrainSettings settings) => new(settings, true,
        CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters,
        CertifiedRingScale.RingWidthMeters, 8400);

    private static float ClampBaseline(float value, RingSurfaceHeightField field)
        => float.IsFinite(value) ? Math.Clamp(value, field.MinimumHeight, field.MaximumHeight) : field.MinimumHeight;

    private void ValidateOutsideAcross(SurfaceCache cache, string label)
    {
        bool found = false;
        for (int z = 0; z < Side; z++) for (int x = 0; x < Side; x++)
        {
            int across = cache.AcrossAt(x, z);
            if (across >= 0 && across < cache.Field.WidthMeters) continue;
            found = true;
            Check(Bits(cache.Baselines[x, z]) == Bits(cache.Field.MinimumHeight), label + " height");
            Check(ColorBits(cache.Biomes[x, z]) == ColorBits(new Color(0, 0, 0, 0)), label + " color");
        }
        Check(found, label + " includes out-of-range samples");
    }

    private void Compare(float[,] left, float[,] right, string label)
    {
        for (int z = 0; z < Side; z++) for (int x = 0; x < Side; x++)
            Check(Bits(left[x, z]) == Bits(right[x, z]), label);
    }

    private void Compare(Color[,] left, Color[,] right, string label)
    {
        for (int z = 0; z < Side; z++) for (int x = 0; x < Side; x++)
            Check(ColorBits(left[x, z]) == ColorBits(right[x, z]), label);
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    private static (int, int, int, int) ColorBits(Color value) => (Bits(value.R), Bits(value.G), Bits(value.B), Bits(value.A));
    private static bool? DetectJitOptimizerDisabled()
    {
        return typeof(RingTerrainSampler).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled;
    }
    private void Check(bool value, string label) { _checks++; if (!value) throw new InvalidOperationException(label); }

    private static void WriteReport(object report)
    {
        string path = ProjectSettings.GlobalizePath("res://.godot/terrain-generation-tests/cpu-pass2-cache-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class SurfaceCache : IDisposable
    {
        private readonly RingTerrain3DSurface _surface = new();
        public RingSurfaceHeightField Field { get; }
        private readonly RingSurfaceFrame _frame;
        public SurfaceCache(RingSurfaceHeightField field, RingTerrainSampler sampler, RingSurfaceFrame frame)
        {
            Field = field; _frame = frame;
            Set("_field", field); Set("_frame", frame); Set("_biomeSampler", sampler);
        }
        public float[,] Baselines => (float[,])Get("_baselineWindowHeights")!;
        public float[,] Heights => (float[,])Get("_windowHeights")!;
        public Color[,] Biomes => (Color[,])Get("_biomeWeights")!;
        public bool Fused => (bool)Get("_biomeWeightsFusedWithHeightCache")!;
        public void Build() { Invoke("CacheWindowHeights"); Invoke("CacheBiomeWeights"); }
        public int AcrossAt(int x, int z)
        {
            int origin = -RingTerrain3DSurface.RegionSideSamples;
            return _frame.AnchorAcrossMeters + _frame.AcrossDirection * (z + origin) * (int)Field.SpacingMeters;
        }
        private object? Get(string name) => SurfaceType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_surface);
        private void Set(string name, object value) => SurfaceType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_surface, value);
        private void Invoke(string name) => SurfaceType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_surface, null);
        public void Dispose() => _surface.Free();
    }
}
