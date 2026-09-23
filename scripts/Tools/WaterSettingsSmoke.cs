using System;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only regression coverage for water persistence and ring-period helpers.</summary>
[GlobalClass]
public partial class WaterSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string path)
    {
        var result = new Godot.Collections.Dictionary();
        using var graphics = new RingGraphicsQualityController();
        using var fft = new RingWaterFftController();
        Shader? nearShader = GD.Load<Shader>("res://materials/ring_ocean_proxy.gdshader");
        Shader? globalShader = GD.Load<Shader>("res://materials/ring_ocean_global.gdshader");
        using var nearMaterial = new ShaderMaterial();
        using var globalMaterial = new ShaderMaterial();
        using var scaledFarMaterial = new ShaderMaterial();
        if (nearShader != null) nearMaterial.Shader = nearShader;
        if (globalShader != null) { globalMaterial.Shader = globalShader; scaledFarMaterial.Shader = globalShader; }
        RingWaterModel.ConfigureMaterial(nearMaterial);
        RingWaterModel.ConfigureMaterial(globalMaterial);
        RingWaterModel.ConfigureMaterial(scaledFarMaterial, .001f);
        long pushesBeforeIdleRefresh = RingWaterModel.OceanMaterialParameterPushes;
        RingWaterModel.RefreshOceanMaterials();
        bool unchangedMaterialCache = RingWaterModel.LastOceanMaterialRefreshPushes == 0
            && RingWaterModel.OceanMaterialParameterPushes == pushesBeforeIdleRefresh;
        using var newlyBoundNearMaterial = new ShaderMaterial { Shader = nearShader };
        long pushesBeforeNewMaterial = RingWaterModel.OceanMaterialParameterPushes;
        RingWaterModel.ConfigureMaterial(newlyBoundNearMaterial);
        bool newMaterialClearsDisabledProvider = !newlyBoundNearMaterial.GetShaderParameter("fft_ready").AsBool()
            && newlyBoundNearMaterial.GetShaderParameter("fft_normal_foam").VariantType == Variant.Type.Nil
            && newlyBoundNearMaterial.GetShaderParameter("fft_displacement").VariantType == Variant.Type.Nil
            && RingWaterModel.OceanMaterialParameterPushes > pushesBeforeNewMaterial;
        var settings = new GameplayWaterDefaults(path);
        settings.Initialize(graphics, fft);
        bool initialEnabled = fft.Enabled;
        bool setFalse = settings.SetOverride(graphics, fft, "FftEnabled", false);
        bool setFloat = settings.SetOverride(graphics, fft, "WaterWaveHeightMeters", .77f);
        bool setColor = settings.SetOverride(graphics, fft, "WaterShallowTint", new Color(.2f, .4f, .6f, 1));
        bool setOptics = settings.SetOverride(graphics, fft, "WaterAbsorptionCoefficients", new Color(.36f, .14f, .04f, 1))
            && settings.SetOverride(graphics, fft, "WaterShallowInscatterStrength", .71f)
            && settings.SetOverride(graphics, fft, "WaterCrestTransmissionStrength", .63f);
        bool setVariation = settings.SetOverride(graphics, fft, "WaterSwellVariation", .31f)
            && settings.SetOverride(graphics, fft, "WaterFoamPatchiness", .47f)
            && settings.SetOverride(graphics, fft, "WaterDistantFoamVisibility", .58f)
            && settings.SetOverride(graphics, fft, "WaterDetailFilteringStrength", 1.72f);
        bool setSsr = settings.SetOverride(graphics, fft, "WaterSsrEnabled", true)
            && settings.SetOverride(graphics, fft, "WaterSsrStrength", .73f)
            && settings.SetOverride(graphics, fft, "WaterSsrCoarseSteps", 19)
            && settings.SetOverride(graphics, fft, "WaterSsrRefinementSteps", 5)
            && settings.SetOverride(graphics, fft, "WaterSsrMaxRayDistance", 310f)
            && settings.SetOverride(graphics, fft, "WaterSsrCameraDistance", 540f)
            && settings.SetOverride(graphics, fft, "WaterSsrDepthToleranceNear", .42f)
            && settings.SetOverride(graphics, fft, "WaterSsrDepthToleranceFar", 2.7f)
            && settings.SetOverride(graphics, fft, "WaterSsrRayBias", .18f)
            && settings.SetOverride(graphics, fft, "WaterSsrEdgeFade", .16f)
            && settings.SetOverride(graphics, fft, "WaterSsrRoughnessCutoff", .37f)
            && settings.SetOverride(graphics, fft, "WaterSsrDebugMode", 3);
        bool productionShaders = nearShader != null && globalShader != null;
        bool materialBinding = productionShaders
            && Math.Abs(nearMaterial.GetShaderParameter("swell_variation").AsSingle() - .31f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("foam_patchiness").AsSingle() - .47f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("distant_foam_visibility").AsSingle() - .58f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("detail_filtering_strength").AsSingle() - 1.72f) < .001f
            && Math.Abs(globalMaterial.GetShaderParameter("swell_variation").AsSingle() - .31f) < .001f
            && Math.Abs(globalMaterial.GetShaderParameter("foam_patchiness").AsSingle() - .47f) < .001f
            && Math.Abs(globalMaterial.GetShaderParameter("distant_foam_visibility").AsSingle() - .58f) < .001f
            && Math.Abs(globalMaterial.GetShaderParameter("detail_filtering_strength").AsSingle() - 1.72f) < .001f
            && Math.Abs(scaledFarMaterial.GetShaderParameter("swell_variation").AsSingle() - .31f) < .001f
            && Math.Abs(scaledFarMaterial.GetShaderParameter("foam_patchiness").AsSingle() - .47f) < .001f
            && Math.Abs(scaledFarMaterial.GetShaderParameter("distant_foam_visibility").AsSingle() - .58f) < .001f
            && Math.Abs(scaledFarMaterial.GetShaderParameter("detail_filtering_strength").AsSingle() - 1.72f) < .001f
            && scaledFarMaterial.GetShaderParameter("absorption_coefficients").AsVector3().IsEqualApprox(new Vector3(.36f, .14f, .04f))
            && Math.Abs(nearMaterial.GetShaderParameter("shallow_inscatter_strength").AsSingle() - .71f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("crest_transmission_strength").AsSingle() - .63f) < .001f
            && nearMaterial.GetShaderParameter("water_ssr_enabled").AsBool()
            && Math.Abs(nearMaterial.GetShaderParameter("water_ssr_strength").AsSingle() - .73f) < .001f
            && nearMaterial.GetShaderParameter("water_ssr_coarse_steps").AsInt32() == 19
            && nearMaterial.GetShaderParameter("water_ssr_refinement_steps").AsInt32() == 5
            && Math.Abs(nearMaterial.GetShaderParameter("water_ssr_max_ray_distance").AsSingle() - 310f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("water_ssr_ray_bias").AsSingle() - .18f) < .001f
            && nearMaterial.GetShaderParameter("water_ssr_debug_mode").AsInt32() == 3
            && Math.Abs(scaledFarMaterial.GetShaderParameter("water_coordinate_scale").AsSingle() - .001f) < .00001f;
        bool setSavedLargePatch = settings.SetOverride(graphics, fft, "FftLargeWaveLengthMeters", 48f)
            && Math.Abs(fft.LargeWaveLengthMeters - 48f) < .001f;
        bool setGeometry = settings.SetOverride(graphics, fft, "FftGeometryHeightScale", 1.27f)
            && settings.SetOverride(graphics, fft, "FftGeometryChoppiness", .63f)
            && settings.SetOverride(graphics, fft, "FftGeometryDistanceMeters", 275f)
            && settings.SetOverride(graphics, fft, "FftGeometryFilterMeters", 3f)
            && settings.SetOverride(graphics, fft, "FftCrestLightingStrength", .41f)
            && settings.SetOverride(graphics, fft, "FftShorelineFadeDistanceMeters", 12f)
            && settings.SetOverride(graphics, fft, "FftShorelineChopFadeDistanceMeters", 24f)
            && settings.SetOverride(graphics, fft, "FftShorelineShallowDepthMeters", 2.5f)
            && settings.SetOverride(graphics, fft, "FftDeepWaterWaveDepthMeters", 100f)
            && Math.Abs(fft.GeometryHeightScale - 1.27f) < .001f
            && Math.Abs(fft.GeometryChoppiness - .63f) < .001f
            && Math.Abs(fft.GeometryDistanceMeters - 275f) < .001f
            && Math.Abs(fft.GeometryFilterMeters - 3f) < .001f
            && Math.Abs(fft.CrestLightingStrength - .41f) < .001f
            && Math.Abs(fft.ShorelineFadeDistanceMeters - 12f) < .001f
            && Math.Abs(fft.ShorelineChopFadeDistanceMeters - 24f) < .001f
            && Math.Abs(fft.ShorelineShallowDepthMeters - 2.5f) < .001f;
        // Properties omit the Fft prefix while persisted keys retain it. This
        // exercises the ordered-pair correction through that boundary.
        bool deepWaterOrdering = settings.SetOverride(graphics, fft, "FftShorelineShallowDepthMeters", 20f)
            && settings.SetOverride(graphics, fft, "FftDeepWaterWaveDepthMeters", 5f)
            && Math.Abs(fft.DeepWaterWaveDepthMeters - 21f) < .001f
            && settings.SetOverride(graphics, fft, "FftShorelineShallowDepthMeters", 2.5f)
            && settings.SetOverride(graphics, fft, "FftDeepWaterWaveDepthMeters", 100f)
            && Math.Abs(fft.DeepWaterWaveDepthMeters - 100f) < .001f;
        bool setAdaptiveDemand = settings.SetOverride(graphics, fft, "FftAdaptiveUpdatesEnabled", false)
            && settings.SetOverride(graphics, fft, "FftMinimumUpdatesPerSecond", 7f)
            && settings.SetOverride(graphics, fft, "FftFullRateDistanceMeters", 350f)
            && settings.SetOverride(graphics, fft, "FftFullRateAreaSquareKilometers", .75f)
            && settings.SetOverride(graphics, fft, "FftIdleGraceSeconds", 2f)
            && !fft.AdaptiveUpdatesEnabled
            && Math.Abs(fft.MinimumUpdatesPerSecond - 7f) < .001f
            && Math.Abs(fft.FullRateDistanceMeters - 350f) < .001f
            && Math.Abs(fft.FullRateAreaSquareKilometers - .75f) < .001f
            && Math.Abs(fft.IdleGraceSeconds - 2f) < .001f;
        bool adaptiveClamps = settings.SetOverride(graphics, fft, "FftMinimumUpdatesPerSecond", -1f)
            && settings.SetOverride(graphics, fft, "FftFullRateDistanceMeters", 5000f)
            && settings.SetOverride(graphics, fft, "FftFullRateAreaSquareKilometers", 100f)
            && settings.SetOverride(graphics, fft, "FftIdleGraceSeconds", 20f)
            && Math.Abs(fft.MinimumUpdatesPerSecond - 1f) < .001f
            && Math.Abs(fft.FullRateDistanceMeters - 1000f) < .001f
            && Math.Abs(fft.FullRateAreaSquareKilometers - 16f) < .001f
            && Math.Abs(fft.IdleGraceSeconds - 5f) < .001f;
        bool setExpandedLimits = settings.SetOverride(graphics, fft, "FftResolution", 1024)
            && settings.SetOverride(graphics, fft, "FftCascadeCount", 4)
            && settings.SetOverride(graphics, fft, "FftUpdatesPerSecond", 120f)
            && settings.SetOverride(graphics, fft, "FftGeometryHeightScale", 10f)
            && settings.SetOverride(graphics, fft, "FftGeometryChoppiness", 3f)
            && settings.SetOverride(graphics, fft, "FftGeometryDistanceMeters", 2000f)
            && settings.SetOverride(graphics, fft, "FftWindSpeedMetersPerSecond", 60f)
            && settings.SetOverride(graphics, fft, "FftSmallWaveLengthMeters", 256f)
            && settings.SetOverride(graphics, fft, "FftLargeWaveLengthMeters", 2048f)
            && settings.SetOverride(graphics, fft, "FftDetailDistanceMeters", 4000f)
            && settings.SetOverride(graphics, fft, "WaterWaveHeightMeters", 10f);
        bool invalid = !settings.SetOverride(graphics, fft, "UnknownWaterKey", 1f)
            && !settings.SetOverride(graphics, fft, "FftEnabled", 1f)
            && !settings.SetOverride(graphics, fft, "FftAdaptiveUpdatesEnabled", 1f)
            && !settings.SetOverride(graphics, fft, "FftAdaptiveUpdatesEnabled", "invalid")
            && !settings.SetOverride(graphics, fft, "FftMinimumUpdatesPerSecond", float.NaN)
            && !settings.SetOverride(graphics, fft, "FftFullRateDistanceMeters", float.PositiveInfinity)
            && !settings.SetOverride(graphics, fft, "FftFullRateAreaSquareKilometers", float.NaN)
            && !settings.SetOverride(graphics, fft, "FftIdleGraceSeconds", float.NegativeInfinity)
            && !settings.SetOverride(graphics, fft, "WaterWaveHeightMeters", float.NaN)
            && !settings.SetOverride(graphics, fft, "WaterSwellVariation", float.NaN)
            && !settings.SetOverride(graphics, fft, "WaterDetailFilteringStrength", "invalid")
            && !settings.SetOverride(graphics, fft, "FftResolution", 129)
            && !settings.SetOverride(graphics, fft, "FftResolution", 768)
            && !settings.SetOverride(graphics, fft, "WaterShallowTint", new Color(float.NaN, 0, 0, 1));
        bool saved = settings.Save();
        var loaded = new GameplayWaterDefaults(path);
        using var reloadedGraphics = new RingGraphicsQualityController();
        using var reloadedFft = new RingWaterFftController();
        loaded.Initialize(reloadedGraphics, reloadedFft);
        bool roundTrip = !reloadedFft.Enabled
            && Math.Abs(reloadedGraphics.WaterSwellVariation - .31f) < .001f
            && Math.Abs(reloadedGraphics.WaterFoamPatchiness - .47f) < .001f
            && Math.Abs(reloadedGraphics.WaterDistantFoamVisibility - .58f) < .001f
            && Math.Abs(reloadedGraphics.WaterDetailFilteringStrength - 1.72f) < .001f
            && reloadedGraphics.WaterAbsorptionCoefficients.IsEqualApprox(new Color(.36f, .14f, .04f, 1))
            && Math.Abs(reloadedGraphics.WaterShallowInscatterStrength - .71f) < .001f
            && Math.Abs(reloadedGraphics.WaterCrestTransmissionStrength - .63f) < .001f
            && Math.Abs(reloadedFft.GeometryHeightScale - 10f) < .001f
            && Math.Abs(reloadedFft.GeometryChoppiness - 3f) < .001f
            && Math.Abs(reloadedFft.GeometryDistanceMeters - 2000f) < .001f
            && Math.Abs(reloadedFft.GeometryFilterMeters - 3f) < .001f
            && Math.Abs(reloadedFft.CrestLightingStrength - .41f) < .001f
            && Math.Abs(reloadedFft.ShorelineFadeDistanceMeters - 12f) < .001f
            && Math.Abs(reloadedFft.ShorelineChopFadeDistanceMeters - 24f) < .001f
            && Math.Abs(reloadedFft.ShorelineShallowDepthMeters - 2.5f) < .001f
            && Math.Abs(reloadedFft.DeepWaterWaveDepthMeters - 100f) < .001f
            && reloadedFft.Resolution == 1024
            && reloadedFft.CascadeCount == 4
            && Math.Abs(reloadedFft.UpdatesPerSecond - 120f) < .001f
            && Math.Abs(reloadedFft.WindSpeedMetersPerSecond - 60f) < .001f
            && Math.Abs(reloadedFft.SmallWaveLengthMeters - 256f) < .001f
            && Math.Abs(reloadedFft.LargeWaveLengthMeters - 2048f) < .001f
            && Math.Abs(reloadedFft.DetailDistanceMeters - 4000f) < .001f
            && !reloadedFft.AdaptiveUpdatesEnabled
            && Math.Abs(reloadedFft.MinimumUpdatesPerSecond - 1f) < .001f
            && Math.Abs(reloadedFft.FullRateDistanceMeters - 1000f) < .001f
            && Math.Abs(reloadedFft.FullRateAreaSquareKilometers - 16f) < .001f
            && Math.Abs(reloadedFft.IdleGraceSeconds - 5f) < .001f
            && Math.Abs(reloadedGraphics.WaterWaveHeightMeters - 10f) < .001f
            && reloadedGraphics.WaterSsrEnabled && Math.Abs(reloadedGraphics.WaterSsrStrength - .73f) < .001f
            && reloadedGraphics.WaterSsrCoarseSteps == 19 && reloadedGraphics.WaterSsrRefinementSteps == 5
            && Math.Abs(reloadedGraphics.WaterSsrMaxRayDistance - 310f) < .001f && Math.Abs(reloadedGraphics.WaterSsrCameraDistance - 540f) < .001f
            && Math.Abs(reloadedGraphics.WaterSsrDepthToleranceNear - .42f) < .001f && Math.Abs(reloadedGraphics.WaterSsrDepthToleranceFar - 2.7f) < .001f
            && Math.Abs(reloadedGraphics.WaterSsrRayBias - .18f) < .001f && Math.Abs(reloadedGraphics.WaterSsrEdgeFade - .16f) < .001f
            && Math.Abs(reloadedGraphics.WaterSsrRoughnessCutoff - .37f) < .001f && reloadedGraphics.WaterSsrDebugMode == 3
            && reloadedGraphics.WaterShallowTint.IsEqualApprox(new Color(.2f, .4f, .6f, 1));
        bool setTrue = loaded.SetOverride(reloadedGraphics, reloadedFft, "FftEnabled", true) && reloadedFft.Enabled;
        // The saved file cannot also be a directory, on every supported platform.
        var unwritable = new GameplayWaterDefaults(path + "/blocked.tres");
        unwritable.Initialize(graphics, fft);
        unwritable.SetOverride(graphics, fft, "WaterWaveSpeed", 2f);
        bool failedWriteStaysDirty = !unwritable.Save() && unwritable.Dirty;
        loaded.Reset(reloadedGraphics, reloadedFft);
        bool reset = reloadedFft.Enabled == initialEnabled
            && Math.Abs(reloadedGraphics.WaterWaveHeightMeters - .32f) < .001f
            && Math.Abs(reloadedGraphics.WaterSwellVariation - .65f) < .001f
            && Math.Abs(reloadedGraphics.WaterFoamPatchiness - .65f) < .001f
            && Math.Abs(reloadedGraphics.WaterDistantFoamVisibility - .20f) < .001f
            && Math.Abs(reloadedGraphics.WaterDetailFilteringStrength - 1f) < .001f
            && Math.Abs(reloadedGraphics.WaterSsrStrength - .65f) < .001f
            && Math.Abs(reloadedFft.LargeWaveLengthMeters - 79f) < .001f
            && reloadedFft.AdaptiveUpdatesEnabled
            && Math.Abs(reloadedFft.MinimumUpdatesPerSecond - 5f) < .001f
            && Math.Abs(reloadedFft.FullRateDistanceMeters - 200f) < .001f
            && Math.Abs(reloadedFft.FullRateAreaSquareKilometers - .25f) < .001f
            && Math.Abs(reloadedFft.IdleGraceSeconds - 1f) < .001f;
        const double circumference = 1495000000.0;
        double tile = RingWaterFftController.PeriodicLength(12, circumference);
        double phaseA = RingWaterFftController.PositiveModulo(circumference - .125, tile) / tile;
        double phaseB = RingWaterFftController.PositiveModulo(-.125, tile) / tile;
        bool periodic = double.IsFinite(tile) && Math.Abs(phaseA - phaseB) < 1e-5
            && RingWaterFftController.PositiveModulo(-circumference * 3.0 - .5, tile) >= 0;
        result["pass"] = setFalse && setFloat && setColor && setOptics && setVariation && setSsr && materialBinding && unchangedMaterialCache && newMaterialClearsDisabledProvider && setSavedLargePatch && setGeometry && deepWaterOrdering && setAdaptiveDemand && adaptiveClamps && setExpandedLimits && invalid && saved && roundTrip && setTrue && failedWriteStaysDirty && reset && periodic;
        result["detail"] = $"false={setFalse} float={setFloat} color={setColor} optics={setOptics} variation={setVariation} ssr={setSsr} productionShaders={productionShaders} material={materialBinding} cache={unchangedMaterialCache} newMaterialClear={newMaterialClearsDisabledProvider} savedLargePatch={setSavedLargePatch} geometry={setGeometry} deepOrdering={deepWaterOrdering} adaptive={setAdaptiveDemand} adaptiveClamps={adaptiveClamps} expanded={setExpandedLimits} invalid={invalid} save={saved} roundTrip={roundTrip} true={setTrue} failedWrite={failedWriteStaysDirty} reset={reset} periodic={periodic}";
        return result;
    }
}
