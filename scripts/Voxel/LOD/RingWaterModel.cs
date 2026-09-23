using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Shared, inspector-driven appearance settings for every ring water LOD.</summary>
public static partial class RingWaterModel
{
    public const float EncodedWaterKindDivisor = 3.0f;

    private sealed class MaterialBinding
    {
        public MaterialBinding(ShaderMaterial material, float coordinateScale)
        {
            Material = new WeakReference<ShaderMaterial>(material);
            CoordinateScale = coordinateScale;
        }

        public WeakReference<ShaderMaterial> Material { get; }
        public float CoordinateScale { get; set; }
    }

    private static readonly List<MaterialBinding> Materials = new();

    public static float WaveHeightMeters { get; private set; } = 0.32f;
    public static float WaveLengthMeters { get; private set; } = 28.0f;
    public static float WaveSpeed { get; private set; } = 1.35f;
    public static float WaveDetailStrength { get; private set; } = 0.42f;
    public static float WaveDomainWarpStrength { get; private set; } = 0.46f;
    public static float WaveCrestSharpness { get; private set; } = 1.35f;
    public static float WaveCrestBlend { get; private set; } = 0.62f;
    public static float WaveNormalStrength { get; private set; } = 0.90f;
    public static float WaveBandlimitStartRadians { get; private set; } = 0.35f;
    public static float WaveBandlimitEndRadians { get; private set; } = 1.10f;
    public static float WaveGeometryFadeMeters { get; private set; } = 8000.0f;
    public static float WaveNormalFadeStartMeters { get; private set; } = 500.0f;
    public static float WaveNormalFadeEndMeters { get; private set; } = 3000.0f;
    public static float FarWaterRoughness { get; private set; } = 0.46f;
    public static float FresnelF0 { get; private set; } = 0.020f;
    public static float FresnelStrength { get; private set; } = 0.88f;
    public static float SpecularAaStrength { get; private set; } = 0.85f;
    public static float RefractionStrength { get; private set; } = 0.012f;
    public static float AbsorptionDensity { get; private set; } = 0.11f;
    public static Color AbsorptionCoefficients { get; private set; } = new(0.30f, 0.12f, 0.035f, 1.0f);
    public static float ShallowInscatterStrength { get; private set; } = 0.58f;
    public static float CrestTransmissionStrength { get; private set; } = 0.95f;
    public static float ShallowAlpha { get; private set; } = 0.38f;
    public static float DeepAlpha { get; private set; } = 0.995f;
    public static bool SsrEnabled { get; private set; } = true;
    public static float SsrStrength { get; private set; } = 0.65f;
    public static int SsrCoarseSteps { get; private set; } = 24;
    public static int SsrRefinementSteps { get; private set; } = 4;
    public static float SsrMaxRayDistance { get; private set; } = 250.0f;
    public static float SsrCameraDistance { get; private set; } = 450.0f;
    public static float SsrDepthToleranceNear { get; private set; } = 0.35f;
    public static float SsrRayBias { get; private set; } = 0.12f;
    public static float SsrDepthToleranceFar { get; private set; } = 2.0f;
    public static float SsrEdgeFade { get; private set; } = 0.12f;
    public static float SsrRoughnessCutoff { get; private set; } = 0.32f;
    public static int SsrDebugMode { get; private set; }
    public static float ShoreFoamDepthMeters { get; private set; } = 2.4f;
    public static float ShoreFoamStrength { get; private set; } = 0.82f;
    public static float BreakingFoamSlopeStart { get; private set; } = 0.28f;
    public static float BreakingFoamSlopeEnd { get; private set; } = 0.48f;
    public static float BreakingFoamStrength { get; private set; } = 0.55f;
    public static float SwellVariation { get; private set; } = 0.65f;
    public static float FoamPatchiness { get; private set; } = 0.65f;
    public static float DistantFoamVisibility { get; private set; } = 0.20f;
    public static float DetailFilteringStrength { get; private set; } = 1.0f;
    public static Color ShallowTint { get; private set; } = new(0.055f, 0.42f, 0.50f, 1.0f);
    public static Color DeepTint { get; private set; } = new(0.012f, 0.075f, 0.16f, 1.0f);
    public static Color FoamColor { get; private set; } = new(0.72f, 0.88f, 0.91f, 1.0f);
    private static bool _waveProfileValidationReported;

    /// <summary>
    /// Water kind travels in vertex alpha because the water shaders own final
    /// transparency and the existing mesh formats have no spare custom channel.
    /// RGB remains the seeded water tint.
    /// </summary>
    public static Color EncodeWaterKind(Color color, SurfaceWaterKind kind) => new(
        color.R,
        color.G,
        color.B,
        Mathf.Clamp((int)kind / EncodedWaterKindDivisor, 0.0f, 1.0f));

    public static SurfaceWaterKind DecodeWaterKind(float encodedAlpha) =>
        (SurfaceWaterKind)Mathf.Clamp(
            Mathf.RoundToInt(encodedAlpha * EncodedWaterKindDivisor),
            (int)SurfaceWaterKind.None,
            (int)SurfaceWaterKind.River);

    public static void ApplyTuning(
        float waveHeightMeters,
        float waveLengthMeters,
        float waveSpeed,
        float waveDetailStrength,
        float waveDomainWarpStrength,
        float waveCrestSharpness,
        float waveCrestBlend,
        float waveNormalStrength,
        float waveBandlimitStartRadians,
        float waveBandlimitEndRadians,
        float waveGeometryFadeMeters,
        float waveNormalFadeStartMeters,
        float waveNormalFadeEndMeters,
        float farWaterRoughness,
        float fresnelF0,
        float fresnelStrength,
        float specularAaStrength,
        float refractionStrength,
        float absorptionDensity,
        Color absorptionCoefficients,
        float shallowInscatterStrength,
        float crestTransmissionStrength,
        float shallowAlpha,
        float deepAlpha,
        bool ssrEnabled, float ssrStrength, int ssrCoarseSteps, int ssrRefinementSteps,
        float ssrMaxRayDistance, float ssrCameraDistance, float ssrDepthToleranceNear, float ssrRayBias,
        float ssrDepthToleranceFar, float ssrEdgeFade, float ssrRoughnessCutoff, int ssrDebugMode,
        float shoreFoamDepthMeters,
        float shoreFoamStrength,
        float breakingFoamSlopeStart,
        float breakingFoamSlopeEnd,
        float breakingFoamStrength,
        Color shallowTint,
        Color deepTint,
        Color foamColor,
        float swellVariation = 0.65f,
        float foamPatchiness = 0.65f,
        float distantFoamVisibility = 0.20f,
        float detailFilteringStrength = 1.0f)
    {
        WaveHeightMeters = Mathf.Clamp(waveHeightMeters, 0.0f, 10.0f);
        WaveLengthMeters = Mathf.Max(2.0f, waveLengthMeters);
        WaveSpeed = Mathf.Max(0.0f, waveSpeed);
        WaveDetailStrength = Mathf.Clamp(waveDetailStrength, 0.0f, 1.0f);
        WaveDomainWarpStrength = Mathf.Clamp(waveDomainWarpStrength, 0.0f, 1.5f);
        WaveCrestSharpness = Mathf.Clamp(waveCrestSharpness, 0.05f, 4.0f);
        WaveCrestBlend = Mathf.Clamp(waveCrestBlend, 0.0f, 1.0f);
        WaveNormalStrength = Mathf.Clamp(waveNormalStrength, 0.0f, 2.0f);
        WaveBandlimitStartRadians = Mathf.Clamp(waveBandlimitStartRadians, 0.05f, 2.0f);
        WaveBandlimitEndRadians = Mathf.Clamp(waveBandlimitEndRadians, WaveBandlimitStartRadians + 0.01f, 3.0f);
        WaveGeometryFadeMeters = Mathf.Max(100.0f, waveGeometryFadeMeters);
        WaveNormalFadeStartMeters = Mathf.Max(100.0f, waveNormalFadeStartMeters);
        WaveNormalFadeEndMeters = Mathf.Max(WaveNormalFadeStartMeters + 1.0f, waveNormalFadeEndMeters);
        FarWaterRoughness = Mathf.Clamp(farWaterRoughness, 0.05f, 1.0f);
        FresnelF0 = Mathf.Clamp(fresnelF0, 0.0f, 0.12f);
        FresnelStrength = Mathf.Clamp(fresnelStrength, 0.0f, 1.5f);
        SpecularAaStrength = Mathf.Clamp(specularAaStrength, 0.0f, 2.0f);
        RefractionStrength = Mathf.Clamp(refractionStrength, 0.0f, 0.08f);
        AbsorptionDensity = Mathf.Max(0.001f, absorptionDensity);
        AbsorptionCoefficients = new Color(Mathf.Clamp(absorptionCoefficients.R, 0.001f, 2.0f), Mathf.Clamp(absorptionCoefficients.G, 0.001f, 2.0f), Mathf.Clamp(absorptionCoefficients.B, 0.001f, 2.0f), 1.0f);
        ShallowInscatterStrength = Mathf.Clamp(shallowInscatterStrength, 0.0f, 2.0f);
        CrestTransmissionStrength = Mathf.Clamp(crestTransmissionStrength, 0.0f, 2.0f);
        ShallowAlpha = Mathf.Clamp(shallowAlpha, 0.0f, 1.0f);
        DeepAlpha = Mathf.Clamp(deepAlpha, ShallowAlpha, 1.0f);
        SsrEnabled = ssrEnabled;
        SsrStrength = Mathf.Clamp(ssrStrength, 0.0f, 1.0f);
        SsrCoarseSteps = Mathf.Clamp(ssrCoarseSteps, 1, 32);
        SsrRefinementSteps = Mathf.Clamp(ssrRefinementSteps, 0, 8);
        SsrMaxRayDistance = Mathf.Clamp(ssrMaxRayDistance, 10.0f, 1000.0f);
        SsrCameraDistance = Mathf.Clamp(ssrCameraDistance, 10.0f, 1000.0f);
        SsrDepthToleranceNear = Mathf.Clamp(ssrDepthToleranceNear, 0.01f, 2.0f);
        SsrRayBias = Mathf.Clamp(ssrRayBias, 0.01f, 1.0f);
        SsrDepthToleranceFar = Mathf.Clamp(ssrDepthToleranceFar, SsrDepthToleranceNear, 8.0f);
        SsrEdgeFade = Mathf.Clamp(ssrEdgeFade, 0.01f, 0.4f);
        SsrRoughnessCutoff = Mathf.Clamp(ssrRoughnessCutoff, 0.02f, 1.0f);
        SsrDebugMode = Mathf.Clamp(ssrDebugMode, 0, 3);
        ShoreFoamDepthMeters = Mathf.Max(0.1f, shoreFoamDepthMeters);
        ShoreFoamStrength = Mathf.Clamp(shoreFoamStrength, 0.0f, 2.0f);
        BreakingFoamSlopeStart = Mathf.Clamp(breakingFoamSlopeStart, 0.05f, 1.5f);
        BreakingFoamSlopeEnd = Mathf.Clamp(breakingFoamSlopeEnd, BreakingFoamSlopeStart + 0.01f, 2.0f);
        BreakingFoamStrength = Mathf.Clamp(breakingFoamStrength, 0.0f, 2.0f);
        ShallowTint = shallowTint;
        DeepTint = deepTint;
        FoamColor = foamColor;
        SwellVariation = Mathf.Clamp(swellVariation, 0.0f, 1.0f);
        FoamPatchiness = Mathf.Clamp(foamPatchiness, 0.0f, 1.0f);
        DistantFoamVisibility = Mathf.Clamp(distantFoamVisibility, 0.0f, 1.0f);
        DetailFilteringStrength = Mathf.Clamp(detailFilteringStrength, 0.0f, 2.0f);
        ValidateWaveProfileDerivative();
        ApplyAll();
    }

    public static void ConfigureMaterial(ShaderMaterial? material, float coordinateScale = 1.0f)
    {
        if (material == null) return;
        float scale = Math.Max(0.000001f, coordinateScale);
        MaterialBinding? binding = null;
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            if (!Materials[index].Material.TryGetTarget(out ShaderMaterial? existing)
                || !GodotObject.IsInstanceValid(existing))
            {
                Materials.RemoveAt(index);
                continue;
            }
            if (ReferenceEquals(existing, material)) binding = Materials[index];
        }

        if (binding == null)
        {
            binding = new MaterialBinding(material, scale);
            Materials.Add(binding);
        }
        else
        {
            binding.CoordinateScale = scale;
        }
        ApplyMaterial(material, scale);
    }

    private static void ApplyAll()
    {
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            MaterialBinding binding = Materials[index];
            if (!binding.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }
            ApplyMaterial(material, binding.CoordinateScale);
        }
    }

    private static void ApplyMaterial(ShaderMaterial material, float coordinateScale)
    {
        material.SetShaderParameter("water_coordinate_scale", coordinateScale);
        material.SetShaderParameter("wave_height_meters", WaveHeightMeters * coordinateScale);
        material.SetShaderParameter("wave_length_meters", WaveLengthMeters * coordinateScale);
        material.SetShaderParameter("wave_speed", WaveSpeed);
        material.SetShaderParameter("wave_detail_strength", WaveDetailStrength);
        material.SetShaderParameter("wave_domain_warp_strength", WaveDomainWarpStrength);
        material.SetShaderParameter("wave_crest_sharpness", WaveCrestSharpness);
        material.SetShaderParameter("wave_crest_blend", WaveCrestBlend);
        material.SetShaderParameter("wave_normal_strength", WaveNormalStrength);
        material.SetShaderParameter("wave_bandlimit_start_radians", WaveBandlimitStartRadians);
        material.SetShaderParameter("wave_bandlimit_end_radians", WaveBandlimitEndRadians);
        material.SetShaderParameter("wave_geometry_fade_meters", WaveGeometryFadeMeters * coordinateScale);
        material.SetShaderParameter("wave_normal_fade_start_meters", WaveNormalFadeStartMeters * coordinateScale);
        material.SetShaderParameter("wave_normal_fade_end_meters", WaveNormalFadeEndMeters * coordinateScale);
        material.SetShaderParameter("far_water_roughness", FarWaterRoughness);
        material.SetShaderParameter("fresnel_f0", FresnelF0);
        material.SetShaderParameter("fresnel_strength", FresnelStrength);
        material.SetShaderParameter("specular_aa_strength", SpecularAaStrength);
        material.SetShaderParameter("refraction_strength", RefractionStrength);
        material.SetShaderParameter("absorption_density", AbsorptionDensity / coordinateScale);
        material.SetShaderParameter("absorption_coefficients", new Vector3(AbsorptionCoefficients.R, AbsorptionCoefficients.G, AbsorptionCoefficients.B));
        material.SetShaderParameter("shallow_inscatter_strength", ShallowInscatterStrength);
        material.SetShaderParameter("crest_transmission_strength", CrestTransmissionStrength);
        material.SetShaderParameter("shallow_alpha", ShallowAlpha);
        material.SetShaderParameter("deep_alpha", DeepAlpha);
        material.SetShaderParameter("water_ssr_enabled", SsrEnabled);
        material.SetShaderParameter("water_ssr_strength", SsrStrength);
        material.SetShaderParameter("water_ssr_coarse_steps", SsrCoarseSteps);
        material.SetShaderParameter("water_ssr_refinement_steps", SsrRefinementSteps);
        material.SetShaderParameter("water_ssr_max_ray_distance", SsrMaxRayDistance * coordinateScale);
        material.SetShaderParameter("water_ssr_camera_distance", SsrCameraDistance * coordinateScale);
        material.SetShaderParameter("water_ssr_depth_tolerance_near", SsrDepthToleranceNear * coordinateScale);
        material.SetShaderParameter("water_ssr_ray_bias", SsrRayBias * coordinateScale);
        material.SetShaderParameter("water_ssr_depth_tolerance_far", SsrDepthToleranceFar * coordinateScale);
        material.SetShaderParameter("water_ssr_edge_fade", SsrEdgeFade);
        material.SetShaderParameter("water_ssr_roughness_cutoff", SsrRoughnessCutoff);
        material.SetShaderParameter("water_ssr_debug_mode", SsrDebugMode);
        material.SetShaderParameter("shore_foam_depth_meters", ShoreFoamDepthMeters * coordinateScale);
        material.SetShaderParameter("shore_foam_strength", ShoreFoamStrength);
        material.SetShaderParameter("breaking_foam_slope_start", BreakingFoamSlopeStart);
        material.SetShaderParameter("breaking_foam_slope_end", BreakingFoamSlopeEnd);
        material.SetShaderParameter("breaking_foam_strength", BreakingFoamStrength);
        material.SetShaderParameter("shallow_water_tint", new Vector3(ShallowTint.R, ShallowTint.G, ShallowTint.B));
        material.SetShaderParameter("deep_water_tint", new Vector3(DeepTint.R, DeepTint.G, DeepTint.B));
        material.SetShaderParameter("foam_color", new Vector3(FoamColor.R, FoamColor.G, FoamColor.B));
        material.SetShaderParameter("swell_variation", SwellVariation);
        material.SetShaderParameter("foam_patchiness", FoamPatchiness);
        material.SetShaderParameter("distant_foam_visibility", DistantFoamVisibility);
        material.SetShaderParameter("detail_filtering_strength", DetailFilteringStrength);
        ApplyOceanMaterial(material, coordinateScale);
    }

    private static void ValidateWaveProfileDerivative()
    {
        double sharpness = WaveCrestSharpness;
        double blend = WaveCrestBlend;
        double exponentialMean = ModifiedBesselI0Approx(sharpness);
        double exponentialScale = 1.0 / Math.Max(0.0001, Math.Exp(sharpness) - exponentialMean);
        const double step = 0.0001;
        double maximumError = 0.0;
        for (int index = 0; index < 32; index++)
        {
            double phase = Math.Tau * index / 32.0;
            double numeric = (WaveProfile(phase + step, sharpness, blend, exponentialMean, exponentialScale)
                - WaveProfile(phase - step, sharpness, blend, exponentialMean, exponentialScale)) / (step * 2.0);
            double analytic = WaveProfileDerivative(phase, sharpness, blend, exponentialScale);
            maximumError = Math.Max(maximumError, Math.Abs(numeric - analytic));
        }

        if (!double.IsFinite(maximumError) || maximumError > 0.001)
        {
            GD.PushError($"Water analytic-normal self-test failed: max derivative error={maximumError:0.000000}.");
            return;
        }
        if (_waveProfileValidationReported) return;
        _waveProfileValidationReported = true;
        GD.Print(
            $"Water surface synthesis ready: five warped sine/fBm bands, exponential crests "
            + $"{WaveCrestSharpness:0.00}/{WaveCrestBlend:0.00}, analytic-normal error={maximumError:0.000000}, "
            + $"bandlimit={WaveBandlimitStartRadians:0.00}-{WaveBandlimitEndRadians:0.00}rad/px, "
            + $"breakingFoamSlope>={BreakingFoamSlopeStart:0.00}, specularAA={SpecularAaStrength:0.00}, "
            + $"Schlick F0={FresnelF0:0.000}.");
    }

    private static double ModifiedBesselI0Approx(double value)
    {
        double q = value * value * 0.25;
        double q2 = q * q;
        double q3 = q2 * q;
        double q4 = q3 * q;
        double q5 = q4 * q;
        double q6 = q5 * q;
        return 1.0 + q + q2 * 0.25 + q3 / 36.0 + q4 / 576.0
            + q5 / 14400.0 + q6 / 518400.0;
    }

    private static double WaveProfile(
        double phase,
        double sharpness,
        double blend,
        double exponentialMean,
        double exponentialScale)
    {
        double sine = Math.Sin(phase);
        double exponential = (Math.Exp(sharpness * sine) - exponentialMean) * exponentialScale;
        return sine + (exponential - sine) * blend;
    }

    private static double WaveProfileDerivative(
        double phase,
        double sharpness,
        double blend,
        double exponentialScale)
    {
        double sine = Math.Sin(phase);
        double cosine = Math.Cos(phase);
        double exponential = sharpness * Math.Exp(sharpness * sine) * cosine * exponentialScale;
        return cosine + (exponential - cosine) * blend;
    }
}
