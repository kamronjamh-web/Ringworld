using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;

namespace RingworldTechDemo.Terrain.Generation.Gpu;

/// <summary>Regional-climate inputs of the GPU K1 kernel: everything RingTerrainSampler reads from its settings, biome profiles and ring geometry.</summary>
public sealed class ProxyGpuClimateSettings
{
    public int Seed;
    public double AroundRingLength;
    public double RingRadius;
    public int RingWidth;
    public float ContinentalFrequency, ErosionFrequency, RidgeFrequency, TemperatureFrequency, MoistureFrequency, MountainWarpFrequency, ValleyFrequency;
    public float ContinentalSecondaryStrength, ContinentalContrast, ContinentalOceanBias, MountainWarpDistanceMeters, PeakRarityExponent;
    public float MountainBeltBroadness, ValleyNarrowness, OceanMoistureInfluence, ClimateLatitudeInfluence, CoastalBeachStrength;
    /// <summary>Runtime altitude temperature drop per kilometre (the sampler's runtime override, default AltitudeTemperatureDropPerKilometre).</summary>
    public float AltitudeTemperatureDropPerKilometre;
    /// <summary>Non-water biome profiles in catalog order. Channel: 0 desert, 1 forest, 2 tundra, 3 alpine, -1 none (grassland).</summary>
    public List<Profile> Profiles = new();

    public readonly record struct Profile(int Channel, int Priority, float MinT, float MaxT, float MinM, float MaxM, float MinR, float MaxR, float BlendWidth, float SelectionMultiplier);
}

/// <summary>
/// WO-G1 of docs/gpu-offload-plan.md: host side of the K1 `sample` kernel (materials/terrain_gpu/ring_proxy_height_k1.glsl). Packs the recipe
/// constants, compiles the kernel on a RenderingDevice and runs it over a sector lattice or a point list. Support is limited to the v7 and v8
/// natural path (see <see cref="IsSupported"/>); anything else must stay on the CPU model.
/// </summary>
public sealed class ProxyGpuHeightKernel : IDisposable
{
    public const int OutputFloats = 16;
    private const string ShaderDirectory = "res://materials/terrain_gpu/";
    private const double Tau = Math.PI * 2d;

    private readonly RenderingDevice _rd;
    private readonly Dictionary<string, int> _layout;
    private Rid _shader, _pipeline, _recipeBuffer, _dummyPoints, _outputBuffer, _pointsBuffer, _uniformSet;
    private int _outputCapacity, _pointsCapacity;
    private readonly int _acrossMax;
    private bool _timestampsWork;

    public bool TimestampsWork => _timestampsWork;
    public int RecipeWords { get; }

    /// <summary>Whether the recipe is inside the ported scope. v1-v6 (and any recipe with Swiss spur detail) must keep using the CPU model.</summary>
    public static bool IsSupported(TerrainGenerationParameters p, out string reason)
    {
        if (p.GeneratorId != TerrainGenerationParameters.LandformGeneratorIdentity && p.GeneratorId != TerrainGenerationParameters.WeatherLungGeneratorIdentity)
        { reason = $"generator '{p.GeneratorId}' is not v7/v8 (CPU model only)"; return false; }
        if ((p.MountainDetailMode & 1) != 0) { reason = "mountain_detail_mode bit 0 (Swiss spurs) is not ported"; return false; }
        if (p.ErosionOctaves > 4) { reason = "erosion_octaves > 4 is not ported"; return false; }
        if (p.WarpAmplitudeM < 0d) { reason = "negative warp amplitude"; return false; }
        reason = "";
        return true;
    }

    public ProxyGpuHeightKernel(RenderingDevice rd, TerrainGenerationParameters recipe, ProxyGpuClimateSettings climate)
    {
        _rd = rd;
        if (!IsSupported(recipe, out string reason)) throw new NotSupportedException("GPU height model: " + reason);
        _layout = ParseLayout(ReadResource("ring_height_model.glslinc") + "\n" + ReadResource("ring_regional_climate.glslinc"));
        _acrossMax = (int)recipe.WidthM - 2;
        uint[] words = PackRecipe(recipe, climate, _layout);
        RecipeWords = words.Length;
        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        _recipeBuffer = rd.StorageBufferCreate((uint)bytes.Length, bytes);
        _dummyPoints = rd.StorageBufferCreate(64);

        string source = ExpandIncludes(ReadResource("ring_proxy_height_k1.glsl"));
        var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = source };
        RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(src);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException("K1 compile: " + spirv.CompileErrorCompute);
        _shader = rd.ShaderCreateFromSpirV(spirv, "ring_proxy_height_k1");
        if (!_shader.IsValid) throw new InvalidOperationException("K1 shader_create_from_spirv failed");
        _pipeline = rd.ComputePipelineCreate(_shader);
        if (!_pipeline.IsValid) throw new InvalidOperationException("K1 compute_pipeline_create failed");
    }

    public void Dispose()
    {
        FreeIfValid(ref _uniformSet);
        FreeIfValid(ref _outputBuffer);
        FreeIfValid(ref _pointsBuffer);
        FreeIfValid(ref _dummyPoints);
        FreeIfValid(ref _recipeBuffer);
        FreeIfValid(ref _pipeline);
        FreeIfValid(ref _shader);
    }

    private void FreeIfValid(ref Rid rid) { if (rid.IsValid) _rd.FreeRid(rid); rid = default; }

    // ---------------------------------------------------------------- shader source

    private static string ReadResource(string file) => Godot.FileAccess.GetFileAsString(ShaderDirectory + file).Replace("\r\n", "\n");

    /// <summary>Expands #include "..." textually (a RenderingDevice shader compiled from source does not resolve includes) and drops the #[compute] marker.</summary>
    public static string ExpandIncludes(string text)
    {
        text = text.Replace("#[compute]\n", "");
        int at;
        while ((at = text.IndexOf("#include \"", StringComparison.Ordinal)) >= 0)
        {
            int start = at + "#include \"".Length;
            int end = text.IndexOf('"', start);
            string inc = text.Substring(start, end - start);
            int lineEnd = text.IndexOf('\n', end);
            text = text.Substring(0, at) + ReadResource(inc) + text.Substring(lineEnd);
        }
        return text;
    }

    /// <summary>The HM_P_* / RC_P_* recipe-buffer indices declared in the include files: the layout contract between GLSL and <see cref="PackRecipe"/>.</summary>
    public static Dictionary<string, int> ParseLayout(string includeText)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(includeText, @"^#define\s+((?:HM|RC)_P_[A-Z0-9_]+)\s+(\d+)\s*$", RegexOptions.Multiline))
            map[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return map;
    }

    // ---------------------------------------------------------------- recipe packing

    private sealed class Packer
    {
        public readonly uint[] Words;
        private readonly Dictionary<string, int> _layout;
        public Packer(Dictionary<string, int> layout) { _layout = layout; Words = new uint[layout["RC_P_COUNT"]]; }
        private int At(string name, int offset = 0)
        {
            if (!_layout.TryGetValue(name, out int index)) throw new InvalidOperationException("recipe layout has no " + name);
            return index + offset;
        }
        public void F(string name, double v, int offset = 0) => Words[At(name, offset)] = BitConverter.SingleToUInt32Bits((float)v);
        public void I(string name, int v, int offset = 0) => Words[At(name, offset)] = unchecked((uint)v);
        public void DF(string name, double v, int offset = 0)
        {
            float hi = (float)v, lo = (float)(v - hi);
            Words[At(name, offset)] = BitConverter.SingleToUInt32Bits(hi);
            Words[At(name, offset + 1)] = BitConverter.SingleToUInt32Bits(lo);
        }
        public void Raw(string name, int offset, uint bits) => Words[At(name, offset)] = bits;
    }

    /// <summary>Packs the v7/v8 height recipe and the regional climate into the recipe buffer, mirroring the constants the C# model derives in its constructor.</summary>
    public static uint[] PackRecipe(TerrainGenerationParameters p, ProxyGpuClimateSettings c, Dictionary<string, int> layout)
    {
        var w = new Packer(layout);
        double C = p.CircumferenceM, radius = C / Math.Tau;
        w.DF("HM_P_K", Math.Tau / C);
        w.DF("HM_P_R", radius);
        w.DF("HM_P_C", C);
        w.DF("HM_P_WARP_AMP", p.WarpAmplitudeM);
        w.I("HM_P_HAS_WARP", p.WarpAmplitudeM == 0d ? 0 : 1);
        w.I("HM_P_ACROSS_MAX", (int)p.WidthM - 2);
        w.F("HM_P_LAND_BALANCE", p.LandBalance);
        double continentalEps = Math.Clamp(p.ContinentalScaleM * .02, 128, 16384);
        w.DF("HM_P_EPS_CONT", continentalEps);
        w.F("HM_P_TWO_EPS_CONT", 2 * continentalEps);
        w.F("HM_P_MIN_GRADIENT", 1d / (32d * p.ContinentalScaleM));
        bool gullyEnabled = (p.HasMountainDetail && (p.MountainDetailMode & 2) != 0 && p.ErosionStrengthM > 0d) || (p.HasLowlandLandforms && p.LowlandErosionStrengthM > 0d);
        double gullyEps = gullyEnabled ? Math.Clamp(p.ErosionScaleM * .125d, 8d, 64d) : 0d;
        w.DF("HM_P_EPS_GULLY", gullyEps);
        w.F("HM_P_TWO_EPS_GULLY", 2d * gullyEps);
        double hillEps = p.HasLowlandLandforms ? Math.Clamp(p.HillScaleM * .02d, 16d, 128d) : 0d;
        w.DF("HM_P_EPS_HILL", hillEps);
        w.F("HM_P_EPS_HILL_F", hillEps);
        w.DF("HM_P_JIT_BASE", .3d);
        w.DF("HM_P_JIT_MUL", .4d);
        w.DF("HM_P_JIT_INV", 1d / uint.MaxValue);
        w.F("HM_P_MOUNT_AMP", p.MountainAmplitudeM);
        w.F("HM_P_RIDGE_SHARP", p.RidgeSharpness);
        w.F("HM_P_VALLEY_W", p.ValleyWidth);
        w.F("HM_P_VALLEY_W2", Math.Min(1, p.ValleyWidth * 2));
        w.F("HM_P_FLATTENING", p.Flattening);
        w.F("HM_P_FLATTEN_DEN", p.FlattenDenominator);
        w.I("HM_P_FLATTEN_ON", Math.Abs(p.Flattening) <= 1e-5d ? 0 : 1);
        double[] continentalHeights = { -p.BasinDepthM, -.8d * p.BasinDepthM, -1000d, -100d, 0d, 1500d, 2000d };
        for (int i = 0; i < 7; i++) w.F("HM_P_CONT_H", continentalHeights[i], i);
        double shelf = Math.Max(1, p.ShelfWidthM), shore = Math.Min(shelf * .25, 1.5 * 8d / p.BeachSlope);
        w.F("HM_P_BASIN_DEPTH", p.BasinDepthM);
        w.F("HM_P_SHELF_W", shelf);
        w.F("HM_P_SHELF_DEPTH", p.ShelfDepthM);
        w.F("HM_P_SHORE", shore);
        w.F("HM_P_BEACH_SLOPE", p.BeachSlope);
        w.F("HM_P_M_SHORE", Math.Min(.002, 3 * 8 / shore));
        w.F("HM_P_SLOPE_W", p.ContinentalSlopeWidthM);
        w.F("HM_P_TRENCH_DEPTH", p.TrenchDepthM);
        w.F("HM_P_TRENCH_THR", p.TrenchThreshold);
        w.F("HM_P_DETAIL_START", p.DetailStartM);
        w.F("HM_P_DETAIL_FULL", Math.Max(p.DetailStartM, p.DetailFullM));
        w.F("HM_P_SEC_AMP", p.SecondaryAmplitudeM);
        w.F("HM_P_MICRO_AMP", p.MicroAmplitudeM);
        w.F("HM_P_MIN_H", p.MinHeightM);
        w.F("HM_P_MAX_H", p.MaxHeightM);
        w.F("HM_P_FOOT_AMP", p.FoothillAmplitudeM);
        w.F("HM_P_SPUR_AMP", p.SpurAmplitudeM);
        w.I("HM_P_MODE", p.MountainDetailMode);
        w.F("HM_P_EROSION_STR", p.ErosionStrengthM);
        w.I("HM_P_EROSION_OCT", p.ErosionOctaves);
        w.I("HM_P_LOWLAND_OCT", p.LowlandErosionOctaves);
        w.F("HM_P_ERO_PRESERVE", p.ErosionRidgePreservation);
        w.F("HM_P_GULLY_NORM", p.GullyNormalization);
        w.F("HM_P_SHORE_START", p.LandformShoreStartM);
        w.F("HM_P_SHORE_FULL", p.LandformShoreFullM);
        w.F("HM_P_PLAINS_THR", p.PlainsThreshold);
        w.F("HM_P_HILLS_THR", p.HillsThreshold);
        w.F("HM_P_PLATEAU_THR", p.PlateauThreshold);
        w.F("HM_P_PLATEAU_FULL", p.PlateauFullThreshold);
        w.F("HM_P_HILL_DETAIL_W", p.HillDetailWeight);
        w.F("HM_P_HILL_VALLEY_EXP", p.HillValleyExponent);
        w.F("HM_P_HILL_AMP", p.HillAmplitudeM);
        w.F("HM_P_PLAINS_HILL_FRAC", p.PlainsHillFraction);
        w.F("HM_P_HILL_VALLEY_FLOOR", p.HillValleyFloor);
        w.F("HM_P_BREAKS_STR", p.BreaksStrength);
        w.F("HM_P_BREAKS_STEP", p.BreaksStepM);
        w.F("HM_P_RISER", p.TerraceRiserWidth);
        w.F("HM_P_PLATEAU_LIFT", p.PlateauLiftM);
        w.F("HM_P_ESC_JITTER", p.EscarpmentJitterM);
        w.F("HM_P_TERRACE_STR", p.TerraceStrength);
        w.F("HM_P_PLATEAU_STEP", p.PlateauStepM);
        w.F("HM_P_LL_DETAIL_FLOOR", p.LowlandDetailFloor);
        w.F("HM_P_PLAINS_DETAIL_FRAC", p.PlainsDetailFraction);
        w.F("HM_P_LL_EROSION_STR", p.LowlandErosionStrengthM);
        w.I("HM_P_SEED_LO", unchecked((int)(uint)p.Seed));
        w.I("HM_P_SEED_HI", unchecked((int)(uint)(p.Seed >> 32)));
        w.F("HM_P_LACUNARITY", p.NoiseLacunarity);
        w.F("HM_P_GAIN", p.NoiseGain);
        if (gullyEnabled)
        {
            for (int i = 0; i < p.ErosionOctaves; i++)
            {
                double scale = p.ErosionScaleM / Math.Pow(p.NoiseLacunarity, i);
                int count = Math.Max(1, (int)Math.Round(C / scale));
                double cellWidth = C / count;
                w.F("HM_P_GULLY_SCALE", scale, i);
                w.DF("HM_P_GULLY_SCALE_DF", scale, i * 2);
                w.F("HM_P_GULLY_WEIGHT", Math.Pow(p.NoiseGain, i), i);
                w.I("HM_P_GULLY_COUNT", count, i);
                w.DF("HM_P_GULLY_CELLW_DF", cellWidth, i * 2);
                w.F("HM_P_GULLY_CELLW", cellWidth, i);
            }
        }
        // Noise table, in the HM_N_* order: seed, fractal (0 none, 1 fbm, 2 ridged), octaves, frequency (float of 1/scale).
        (int Offset, double Scale, int Octaves, int Fractal)[] noises =
        {
            (101, p.ContinentalScaleM, p.ContinentalOctaves, 1),
            (211, p.MountainScaleM, p.MountainOctaves, 1),
            (307, Math.Max(16, p.MountainScaleM * .45), p.ValleyOctaves, 1),
            (401, Math.Max(16, p.ContinentalScaleM * .35), p.TrenchOctaves, 1),
            (503, p.RidgeScaleM, p.RidgeOctaves, 2),
            (601, p.SecondaryScaleM, p.SecondaryOctaves, 1),
            (701, p.MicroScaleM, p.MicroOctaves, 1),
            (809, p.WarpScaleM, p.WarpOctaves, 1),
            (907, p.WarpScaleM, p.WarpOctaves, 1),
            (1009, p.FoothillScaleM, 3, 1),
            (1103, p.SpurScaleM, 4, 2),
            (1201, p.LandformScaleM, 3, 1),
            (1301, p.PlateauScaleM, 2, 1),
            (1409, p.HillScaleM, 2, 1),
            (1423, p.HillScaleM / 4d, 2, 1),
            (1511, p.EscarpmentScaleM, 3, 1),
        };
        for (int n = 0; n < noises.Length; n++)
        {
            w.I("HM_P_NOISE", unchecked((int)(p.Seed + (long)noises[n].Offset)), n * 4);
            w.I("HM_P_NOISE", noises[n].Fractal, n * 4 + 1);
            w.I("HM_P_NOISE", noises[n].Octaves, n * 4 + 2);
            w.F("HM_P_NOISE", 1d / noises[n].Scale, n * 4 + 3);
        }

        // Regional climate.
        w.DF("RC_P_K", Math.Tau / c.AroundRingLength);
        w.DF("RC_P_R", c.RingRadius);
        w.I("RC_P_WIDTH", c.RingWidth);
        w.F("RC_P_WIDTH_F", (float)c.RingWidth);
        using (var defaults = new FastNoiseLite())
        {
            w.F("RC_P_LAC", defaults.FractalLacunarity);
            w.F("RC_P_GAIN", defaults.FractalGain);
            int fractal = defaults.FractalType == FastNoiseLite.FractalTypeEnum.Fbm ? 1 : defaults.FractalType == FastNoiseLite.FractalTypeEnum.Ridged ? 2 : 0;
            (int Offset, float Frequency)[] regional =
            {
                (3109, c.ContinentalFrequency), (4721, c.ErosionFrequency), (6151, c.RidgeFrequency), (7919, c.TemperatureFrequency),
                (9341, c.MoistureFrequency), (11003, c.MountainWarpFrequency), (12721, c.MountainWarpFrequency), (14503, c.ValleyFrequency),
            };
            for (int n = 0; n < regional.Length; n++)
            {
                w.I("RC_P_NOISE", c.Seed + regional[n].Offset, n * 4);
                w.I("RC_P_NOISE", fractal, n * 4 + 1);
                w.I("RC_P_NOISE", defaults.FractalOctaves, n * 4 + 2);
                w.F("RC_P_NOISE", Math.Max(0.000001f, regional[n].Frequency), n * 4 + 3);
            }
        }
        w.F("RC_P_SEC_STR", c.ContinentalSecondaryStrength);
        w.F("RC_P_CONTRAST", c.ContinentalContrast);
        w.F("RC_P_OCEAN_BIAS", c.ContinentalOceanBias);
        w.F("RC_P_MOUNT_WARP_DIST", c.MountainWarpDistanceMeters);
        w.F("RC_P_PEAK_RARITY", c.PeakRarityExponent);
        w.F("RC_P_BELT_BROAD", c.MountainBeltBroadness);
        w.F("RC_P_VALLEY_NARROW", c.ValleyNarrowness);
        w.F("RC_P_OCEAN_MOIST", c.OceanMoistureInfluence);
        w.F("RC_P_LAT_INFLUENCE", c.ClimateLatitudeInfluence);
        w.F("RC_P_BEACH_STRENGTH", c.CoastalBeachStrength);
        w.F("RC_P_ALT_DROP", c.AltitudeTemperatureDropPerKilometre);
        if (c.Profiles.Count > 8) throw new NotSupportedException("more than 8 biome profiles");
        w.I("RC_P_PROFILE_COUNT", c.Profiles.Count);
        for (int i = 0; i < c.Profiles.Count; i++)
        {
            ProxyGpuClimateSettings.Profile q = c.Profiles[i];
            int b = i * 10;
            w.I("RC_P_PROFILES", q.Channel, b);
            w.I("RC_P_PROFILES", q.Priority, b + 1);
            w.F("RC_P_PROFILES", q.MinT, b + 2); w.F("RC_P_PROFILES", q.MaxT, b + 3);
            w.F("RC_P_PROFILES", q.MinM, b + 4); w.F("RC_P_PROFILES", q.MaxM, b + 5);
            w.F("RC_P_PROFILES", q.MinR, b + 6); w.F("RC_P_PROFILES", q.MaxR, b + 7);
            w.F("RC_P_PROFILES", q.BlendWidth, b + 8);
            w.F("RC_P_PROFILES", q.SelectionMultiplier, b + 9);
        }
        return w.Words;
    }

    // ---------------------------------------------------------------- dispatch

    public readonly record struct RunTiming(double GpuMs, double WallMs);

    /// <summary>Anchor cos/sin(theta0) split as df64 floats: (cos.hi, cos.lo, sin.hi, sin.lo).</summary>
    public static (float CosHi, float CosLo, float SinHi, float SinLo) AnchorTrig(long anchor, double circumference)
    {
        double theta0 = anchor / circumference * Tau;
        double cos = Math.Cos(theta0), sin = Math.Sin(theta0);
        float ch = (float)cos, sh = (float)sin;
        return (ch, (float)(cos - ch), sh, (float)(sin - sh));
    }

    private void EnsureBuffers(int count, int pointWords)
    {
        bool rebuild = false;
        if (count > _outputCapacity)
        {
            FreeIfValid(ref _outputBuffer);
            _outputCapacity = Math.Max(count, _outputCapacity * 2);
            _outputBuffer = _rd.StorageBufferCreate((uint)((long)_outputCapacity * OutputFloats * 4));
            rebuild = true;
        }
        if (pointWords > 0 && pointWords > _pointsCapacity)
        {
            FreeIfValid(ref _pointsBuffer);
            _pointsCapacity = Math.Max(pointWords, _pointsCapacity * 2);
            _pointsBuffer = _rd.StorageBufferCreate((uint)((long)_pointsCapacity * 4));
            rebuild = true;
        }
        if (rebuild || !_uniformSet.IsValid)
        {
            FreeIfValid(ref _uniformSet);
            var recipe = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; recipe.AddId(_recipeBuffer);
            var output = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; output.AddId(_outputBuffer);
            var points = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 }; points.AddId(_pointsBuffer.IsValid ? _pointsBuffer : _dummyPoints);
            _uniformSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { recipe, output, points }, _shader, 0);
        }
    }

    private RunTiming Dispatch(byte[] push, int count)
    {
        var sw = Stopwatch.StartNew();
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, _uniformSet, 0);
        _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
        _rd.CaptureTimestamp("k1_begin");
        _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
        _rd.ComputeListEnd();
        _rd.CaptureTimestamp("k1_end");
        _rd.Submit();
        _rd.Sync();
        double wall = sw.Elapsed.TotalMilliseconds, gpu = -1;
        uint n = _rd.GetCapturedTimestampsCount();
        ulong begin = 0, end = 0; bool hb = false, he = false;
        for (uint i = 0; i < n; i++)
        {
            string name = _rd.GetCapturedTimestampName(i);
            if (name == "k1_begin") { begin = _rd.GetCapturedTimestampGpuTime(i); hb = true; }
            else if (name == "k1_end") { end = _rd.GetCapturedTimestampGpuTime(i); he = true; }
        }
        if (hb && he && end > begin) { gpu = (end - begin) / 1e6; _timestampsWork = true; }
        return new RunTiming(gpu, wall);
    }

    private static byte[] Push(int mode, int count, int cols, int rows, int anchor, int originDA, int originAcross, int spacing,
        (float CosHi, float CosLo, float SinHi, float SinLo) trig, int flags, int sectorStride)
    {
        var b = new byte[64];
        Buffer.BlockCopy(new[] { mode, count, cols, rows, anchor, originDA, originAcross, spacing }, 0, b, 0, 32);
        Buffer.BlockCopy(new[] { trig.CosHi, trig.CosLo, trig.SinHi, trig.SinLo }, 0, b, 32, 16);
        Buffer.BlockCopy(new[] { flags, sectorStride, 0, 0 }, 0, b, 48, 16);
        return b;
    }

    private float[] ReadOutput(int count)
    {
        byte[] data = _rd.BufferGetData(_outputBuffer, 0, (uint)((long)count * OutputFloats * 4));
        var f = new float[count * OutputFloats];
        Buffer.BlockCopy(data, 0, f, 0, data.Length);
        return f;
    }

    /// <summary>
    /// Evaluates <paramref name="sectors"/> lattices of cols x rows points: dA = originDA + s * sectorStride + ix * spacing and
    /// across = clamp(originAcross + iy * spacing, 0, width - 2). Output is 16 floats per point (see ring_proxy_height_k1.glsl).
    /// </summary>
    public RunTiming RunLattice(long anchor, double circumference, int originDA, int originAcross, int spacing, int cols, int rows, int sectors, int sectorStride, bool climate, out float[] output)
    {
        int count = cols * rows * sectors;
        EnsureBuffers(count, 0);
        RunTiming t = Dispatch(Push(0, count, cols, rows, (int)anchor, originDA, originAcross, spacing, AnchorTrig(anchor, circumference), climate ? 1 : 0, sectorStride), count);
        output = ReadOutput(count);
        return t;
    }

    /// <summary>Timing-only lattice dispatch (no read-back, no allocation of the result array).</summary>
    public RunTiming TimeLattice(long anchor, double circumference, int originDA, int originAcross, int spacing, int cols, int rows, int sectors, int sectorStride, bool climate)
    {
        int count = cols * rows * sectors;
        EnsureBuffers(count, 0);
        return Dispatch(Push(0, count, cols, rows, (int)anchor, originDA, originAcross, spacing, AnchorTrig(anchor, circumference), climate ? 1 : 0, sectorStride), count);
    }

    /// <summary>Evaluates an explicit point list: 8 words per point (anchor, dA, across, flags, cos0.hi, cos0.lo, sin0.hi, sin0.lo).</summary>
    public RunTiming RunPoints(uint[] pointWords, int count, out float[] output)
    {
        EnsureBuffers(count, count * 8);
        var bytes = new byte[count * 32];
        Buffer.BlockCopy(pointWords, 0, bytes, 0, bytes.Length);
        _rd.BufferUpdate(_pointsBuffer, 0, (uint)bytes.Length, bytes);
        RunTiming t = Dispatch(Push(1, count, 0, 0, 0, 0, 0, 0, default, 0, 0), count);
        output = ReadOutput(count);
        return t;
    }
}
