using System;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only regression coverage for water persistence and ring-period helpers.</summary>
[GlobalClass]
public partial class WaterSettingsSmoke : RefCounted
{
    private static bool Near(float a, float b) => Math.Abs(a - b) < .001f;

    /// <summary>Saved WaterSsr* choices must survive ApplyPreset in either initialisation order and across runtime preset changes.</summary>
    private static bool RunSsrPresetPersistence(string path, out string detail)
    {
        string globalPath = ProjectSettings.GlobalizePath(path);
        if (System.IO.File.Exists(globalPath)) System.IO.File.Delete(globalPath);
        using (var seedGraphics = new RingGraphicsQualityController())
        {
            var seed = new GameplayWaterDefaults(path);
            seed.Initialize(seedGraphics, null);
            seed.SetOverride(seedGraphics, null, "WaterSsrEnabled", false);
            seed.SetOverride(seedGraphics, null, "WaterSsrRoughnessCutoff", .81f);
            seed.SetOverride(seedGraphics, null, "WaterSsrCoarseSteps", 7);
            seed.Save();
        }

        // World startup order: drawer applies saved tuning, then GraphicsQuality _Ready applies the preset.
        using var drawerFirst = new RingGraphicsQualityController();
        new GameplayWaterDefaults(path).Initialize(drawerFirst, null);
        drawerFirst.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Medium, false);
        bool startup = !drawerFirst.WaterSsrEnabled && Near(drawerFirst.WaterSsrRoughnessCutoff, .81f) && drawerFirst.WaterSsrCoarseSteps == 7
            && Near(drawerFirst.WaterSsrCameraDistance, 250f) && drawerFirst.WaterSsrRefinementSteps == 3;

        // Runtime preset changes keep the overrides and still move untouched fields.
        drawerFirst.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Low, false);
        bool low = !drawerFirst.WaterSsrEnabled && Near(drawerFirst.WaterSsrRoughnessCutoff, .81f) && drawerFirst.WaterSsrCoarseSteps == 7;
        drawerFirst.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Cinematic5090, false);
        bool high = !drawerFirst.WaterSsrEnabled && Near(drawerFirst.WaterSsrRoughnessCutoff, .81f) && drawerFirst.WaterSsrCoarseSteps == 7
            && Near(drawerFirst.WaterSsrCameraDistance, 800f) && drawerFirst.WaterSsrRefinementSteps == 4;

        // Reverse order: preset first, then saved tuning.
        using var presetFirst = new RingGraphicsQualityController();
        presetFirst.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Medium, false);
        new GameplayWaterDefaults(path).Initialize(presetFirst, null);
        bool reverse = !presetFirst.WaterSsrEnabled && Near(presetFirst.WaterSsrRoughnessCutoff, .81f) && presetFirst.WaterSsrCoarseSteps == 7
            && Near(presetFirst.WaterSsrCameraDistance, 250f);

        // Nothing saved: preset defaults are unchanged.
        string emptyPath = path.Replace("_ssr.tres", "_ssr_empty.tres");
        if (System.IO.File.Exists(ProjectSettings.GlobalizePath(emptyPath))) System.IO.File.Delete(ProjectSettings.GlobalizePath(emptyPath));
        using var plain = new RingGraphicsQualityController();
        new GameplayWaterDefaults(emptyPath).Initialize(plain, null);
        plain.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Medium, false);
        bool medium = !plain.WaterSsrEnabled && Near(plain.WaterSsrRoughnessCutoff, .25f) && plain.WaterSsrCoarseSteps == 12;
        plain.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Low, false);
        bool lowDefault = !plain.WaterSsrEnabled;
        plain.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Cinematic5090, false);
        bool highDefault = plain.WaterSsrEnabled && Near(plain.WaterSsrRoughnessCutoff, .32f) && plain.WaterSsrCoarseSteps == 16;

        // WO-3 / R1-8 / R1-8b: High 12 steps / 4 refinements / 600 m, Very High 16 / 4 / 800 m, Ultra 24 / 5 / 1000 m.
        plain.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, false);
        bool highPreset = plain.WaterSsrEnabled && plain.WaterSsrCoarseSteps == 12 && Near(plain.WaterSsrCameraDistance, 600f) && plain.WaterSsrRefinementSteps == 4;
        plain.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.UltraVideo, false);
        bool ultraPreset = plain.WaterSsrEnabled && plain.WaterSsrCoarseSteps == 24 && plain.WaterSsrRefinementSteps == 5 && Near(plain.WaterSsrCameraDistance, 1000f);
        plain.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, false);
        bool veryHighPreset = plain.WaterSsrEnabled && plain.WaterSsrCoarseSteps == 16 && plain.WaterSsrRefinementSteps == 4 && Near(plain.WaterSsrCameraDistance, 800f);

        System.IO.File.Delete(globalPath);
        detail = $"startup={startup} low={low} high={high} reverse={reverse} medium={medium} lowDefault={lowDefault} highDefault={highDefault} highPreset={highPreset} ultraPreset={ultraPreset} veryHighPreset={veryHighPreset}";
        return startup && low && high && reverse && medium && lowDefault && highDefault && highPreset && ultraPreset && veryHighPreset;
    }

    /// <summary>T2R-13: river colour, lake colour and turbidity. Defaults equal the constants the seeded colour path used before, so generation-1 and legacy pixels are unchanged.</summary>
    private static bool RunBodyColors(string path, out string detail)
    {
        string globalPath = ProjectSettings.GlobalizePath(path);
        if (System.IO.File.Exists(globalPath)) System.IO.File.Delete(globalPath);
        // The literal values are today's constants (RingTerrainVisuals.ResolveSeededColor before T2R-13); changing them changes every river and lake vertex colour.
        bool constants = RingWaterBodyColors.DefaultRiver.IsEqualApprox(new Color(0.075f, 0.36f, 0.43f, 1f))
            && RingWaterBodyColors.DefaultLake.IsEqualApprox(new Color(0.045f, 0.29f, 0.36f, 1f))
            && RingWaterBodyColors.DefaultTurbidity == 0f
            && RingWaterBodyColors.Resolve(RingWaterBodyColors.DefaultRiver, 0f) == RingWaterBodyColors.DefaultRiver;
        bool defaults;
        bool set, invalid, clamp, roundTrip, reset, absent;
        using (var graphics = new RingGraphicsQualityController())
        {
            var settings = new GameplayWaterDefaults(path);
            settings.Initialize(graphics, null);
            graphics.ApplyWaterTuning();
            defaults = graphics.WaterRiverColor == RingWaterBodyColors.DefaultRiver && graphics.WaterLakeColor == RingWaterBodyColors.DefaultLake
                && graphics.WaterTurbidity == 0f && RingWaterBodyColors.Current.River == RingWaterBodyColors.DefaultRiver
                && RingWaterBodyColors.Current.Lake == RingWaterBodyColors.DefaultLake && RingWaterBodyColors.Current.Turbidity == 0f;
            set = settings.SetOverride(graphics, null, "WaterRiverColor", new Color(.2f, .3f, .1f, 1))
                && settings.SetOverride(graphics, null, "WaterLakeColor", new Color(.1f, .2f, .5f, 1))
                && settings.SetOverride(graphics, null, "WaterTurbidity", .4f)
                && RingWaterBodyColors.Current.River.IsEqualApprox(new Color(.2f, .3f, .1f, 1))
                && RingWaterBodyColors.Current.Lake.IsEqualApprox(new Color(.1f, .2f, .5f, 1))
                && Near(RingWaterBodyColors.Current.Turbidity, .4f);
            invalid = !settings.SetOverride(graphics, null, "WaterRiverColor", new Color(float.NaN, 0, 0, 1))
                && !settings.SetOverride(graphics, null, "WaterLakeColor", .5f)
                && !settings.SetOverride(graphics, null, "WaterTurbidity", float.NaN)
                && !settings.SetOverride(graphics, null, "WaterTurbidity", "murky");
            clamp = settings.SetOverride(graphics, null, "WaterTurbidity", 5f) && Near(graphics.WaterTurbidity, 1f)
                && settings.SetOverride(graphics, null, "WaterTurbidity", .4f);
            bool saved = settings.Save();
            using var reloaded = new RingGraphicsQualityController();
            new GameplayWaterDefaults(path).Initialize(reloaded, null);
            roundTrip = saved && reloaded.WaterRiverColor.IsEqualApprox(new Color(.2f, .3f, .1f, 1))
                && reloaded.WaterLakeColor.IsEqualApprox(new Color(.1f, .2f, .5f, 1)) && Near(reloaded.WaterTurbidity, .4f)
                && RingWaterBodyColors.Current.River.IsEqualApprox(new Color(.2f, .3f, .1f, 1));
            settings.Reset(graphics, null);
            reset = graphics.WaterRiverColor == RingWaterBodyColors.DefaultRiver && graphics.WaterLakeColor == RingWaterBodyColors.DefaultLake
                && graphics.WaterTurbidity == 0f && RingWaterBodyColors.Current.River == RingWaterBodyColors.DefaultRiver && RingWaterBodyColors.Current.Turbidity == 0f;
            // An older file without the three keys loads the defaults.
            string olderPath = path.Replace(".tres", "_older.tres");
            if (System.IO.File.Exists(ProjectSettings.GlobalizePath(olderPath))) System.IO.File.Delete(ProjectSettings.GlobalizePath(olderPath));
            var older = new GameplayWaterDefaults(olderPath);
            using var olderGraphics = new RingGraphicsQualityController();
            older.Initialize(olderGraphics, null);
            older.SetOverride(olderGraphics, null, "WaterWaveSpeed", 2f);
            older.Save();
            using var olderReloaded = new RingGraphicsQualityController();
            new GameplayWaterDefaults(olderPath).Initialize(olderReloaded, null);
            absent = olderReloaded.WaterRiverColor == RingWaterBodyColors.DefaultRiver && olderReloaded.WaterLakeColor == RingWaterBodyColors.DefaultLake
                && olderReloaded.WaterTurbidity == 0f && RingWaterBodyColors.Current.Turbidity == 0f;
            System.IO.File.Delete(ProjectSettings.GlobalizePath(olderPath));
        }
        RingWaterBodyColors.Set(RingWaterBodyColors.DefaultRiver, RingWaterBodyColors.DefaultLake, RingWaterBodyColors.DefaultTurbidity);
        System.IO.File.Delete(globalPath);
        detail = $"constants={constants} defaults={defaults} set={set} invalid={invalid} clamp={clamp} roundTrip={roundTrip} reset={reset} absent={absent}";
        return constants && defaults && set && invalid && clamp && roundTrip && reset && absent;
    }

    private static void WriteSeedFile(string path, Godot.Collections.Dictionary overrides)
    {
        var seed = new GameplayWaterSettings { Overrides = overrides };
        if (ResourceSaver.Save(seed, path) != Error.Ok) throw new InvalidOperationException("cannot write " + path);
    }

    private static Godot.Collections.Dictionary ReadOverrides(string path) =>
        ResourceLoader.Load<GameplayWaterSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore).Overrides;

    /// <summary>R1-8: a retired key (WaterSsrStrength) loads without an error and is dropped by the next save; the one-time migration resets the saved SSR steps, refinement and distances to the preset.</summary>
    private static bool RunRetiredKeysAndMigration(string path, out string detail)
    {
        string scratch = path.Replace(".tres", $"_reflection_{System.Environment.ProcessId}.tres");
        string globalScratch = ProjectSettings.GlobalizePath(scratch);
        try
        {
            // (i) A file that already carries the marker: only the retired key goes; the explicit SSR value is honoured.
            WriteSeedFile(scratch, new Godot.Collections.Dictionary { { "WaterReflectionMigration", 1 }, { "WaterSsrStrength", 1.0f }, { "WaterSsrMaxRayDistance", 310f }, { "WaterSsrDepthToleranceFar", 2.7f }, { "WaterSsrCoarseSteps", 9 }, { "WaterWaveSpeed", 2f } });
            using var retiredGraphics = new RingGraphicsQualityController();
            var retired = new GameplayWaterDefaults(scratch);
            retired.Initialize(retiredGraphics, null);
            bool retiredLoad = retired.LastError == "" && retired.Dirty && retiredGraphics.WaterSsrCoarseSteps == 9 && Near(retiredGraphics.WaterWaveSpeed, 2f);
            bool retiredSaved = retired.Save() && !retired.Dirty;
            var afterRetired = ReadOverrides(scratch);
            bool retiredGone = retiredSaved && !afterRetired.ContainsKey("WaterSsrStrength") && !afterRetired.ContainsKey("WaterSsrMaxRayDistance") && !afterRetired.ContainsKey("WaterSsrDepthToleranceFar") && afterRetired.ContainsKey("WaterReflectionMigration") && afterRetired.ContainsKey("WaterSsrCoarseSteps");
            bool retiredRejected = !retired.SetOverride(retiredGraphics, null, "WaterReflectionMigration", 1);

            // (ii) The live file shape before the migration: the four SSR values follow the preset, the retired key is gone, Dirty is set; a reload changes nothing.
            WriteSeedFile(scratch, new Godot.Collections.Dictionary
            {
                { "WaterSsrCoarseSteps", 32 }, { "WaterSsrRefinementSteps", 8 }, { "WaterSsrCameraDistance", 695f }, { "WaterSsrMaxRayDistance", 1000f },
                { "WaterSsrStrength", 1.0f }, { "WaterWaveSpeed", 2f },
            });
            using var migratedGraphics = new RingGraphicsQualityController();
            migratedGraphics.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, false);
            var migrated = new GameplayWaterDefaults(scratch);
            migrated.Initialize(migratedGraphics, null);
            bool migratedLoad = migrated.LastError == "" && migrated.Dirty && migratedGraphics.WaterSsrCoarseSteps == 12 && migratedGraphics.WaterSsrRefinementSteps == 4
                && Near(migratedGraphics.WaterSsrCameraDistance, 600f) && Near(migratedGraphics.WaterWaveSpeed, 2f);
            bool migratedSaved = migrated.Save();
            var afterMigration = ReadOverrides(scratch);
            bool migratedFile = migratedSaved && afterMigration.ContainsKey("WaterReflectionMigration") && afterMigration["WaterReflectionMigration"].AsInt32() == 1
                && afterMigration.ContainsKey("WaterWaveSpeed")
                && !afterMigration.ContainsKey("WaterSsrStrength") && !afterMigration.ContainsKey("WaterSsrCoarseSteps") && !afterMigration.ContainsKey("WaterSsrRefinementSteps")
                && !afterMigration.ContainsKey("WaterSsrCameraDistance") && !afterMigration.ContainsKey("WaterSsrMaxRayDistance");
            using var reloadedGraphics = new RingGraphicsQualityController();
            reloadedGraphics.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, false);
            var reloaded = new GameplayWaterDefaults(scratch);
            reloaded.Initialize(reloadedGraphics, null);
            bool reloadStable = reloaded.LastError == "" && !reloaded.Dirty && reloadedGraphics.WaterSsrCoarseSteps == 12 && Near(reloadedGraphics.WaterSsrCameraDistance, 600f);
            // A value chosen after the migration is kept and survives the next load.
            bool chosen = reloaded.SetOverride(reloadedGraphics, null, "WaterSsrCoarseSteps", 20) && reloaded.Save();
            var again = new GameplayWaterDefaults(scratch);
            using var againGraphics = new RingGraphicsQualityController();
            again.Initialize(againGraphics, null);
            bool chosenKept = chosen && againGraphics.WaterSsrCoarseSteps == 20 && !again.Dirty;
            detail = $"retiredLoad={retiredLoad} retiredGone={retiredGone} markerRejected={retiredRejected} migratedLoad={migratedLoad} migratedFile={migratedFile} reloadStable={reloadStable} chosenKept={chosenKept}";
            return retiredLoad && retiredGone && retiredRejected && migratedLoad && migratedFile && reloadStable && chosenKept;
        }
        finally { try { System.IO.File.Delete(globalScratch); } catch { } }
    }

    /// <summary>
    /// R1-8 check on a COPY of a real settings file (never the live one): load it at High, save it back to the same path, reload it, and report what the
    /// migration did. Used by hand by the lead on a copied user file; not part of Run().
    /// </summary>
    public Godot.Collections.Dictionary MigrateCopy(string copyPath)
    {
        var report = new Godot.Collections.Dictionary();
        using var graphics = new RingGraphicsQualityController();
        graphics.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, false);
        var loaded = new GameplayWaterDefaults(copyPath);
        loaded.Initialize(graphics, null);
        report["load_last_error"] = loaded.LastError;
        report["load_dirty"] = loaded.Dirty;
        report["effective"] = $"steps={graphics.WaterSsrCoarseSteps} refine={graphics.WaterSsrRefinementSteps} camera={graphics.WaterSsrCameraDistance} cutoff={graphics.WaterSsrRoughnessCutoff} edge={graphics.WaterSsrEdgeFade}";
        report["saved"] = loaded.Save();
        report["saved_keys"] = string.Join(",", System.Linq.Enumerable.Select(ReadOverrides(copyPath).Keys, k => k.AsString()));
        using var again = new RingGraphicsQualityController();
        again.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, false);
        var reloaded = new GameplayWaterDefaults(copyPath);
        reloaded.Initialize(again, null);
        report["reload_last_error"] = reloaded.LastError;
        report["reload_dirty"] = reloaded.Dirty;
        report["reload_effective"] = $"steps={again.WaterSsrCoarseSteps} refine={again.WaterSsrRefinementSteps} camera={again.WaterSsrCameraDistance}";
        return report;
    }

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
            && settings.SetOverride(graphics, fft, "WaterDetailFilteringStrength", 1.72f)
            && settings.SetOverride(graphics, fft, "WaterRiverVisibilityMeters", 3.5f)
            && settings.SetOverride(graphics, fft, "WaterLakeVisibilityMeters", 9f);
        bool setSsr = settings.SetOverride(graphics, fft, "WaterSsrEnabled", true)
            && settings.SetOverride(graphics, fft, "WaterSsrCoarseSteps", 19)
            && settings.SetOverride(graphics, fft, "WaterSsrRefinementSteps", 5)
            && settings.SetOverride(graphics, fft, "WaterSsrCameraDistance", 540f)
            && settings.SetOverride(graphics, fft, "WaterSsrDepthToleranceNear", .42f)
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
            && Math.Abs(nearMaterial.GetShaderParameter("river_visibility_meters").AsSingle() - 3.5f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("lake_visibility_meters").AsSingle() - 9f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("shallow_inscatter_strength").AsSingle() - .71f) < .001f
            && Math.Abs(nearMaterial.GetShaderParameter("crest_transmission_strength").AsSingle() - .63f) < .001f
            && nearMaterial.GetShaderParameter("water_ssr_enabled").AsBool()
            && nearMaterial.GetShaderParameter("water_ssr_coarse_steps").AsInt32() == 19
            && nearMaterial.GetShaderParameter("water_ssr_refinement_steps").AsInt32() == 5
            && Math.Abs(nearMaterial.GetShaderParameter("water_ssr_camera_distance").AsSingle() - 540f) < .001f
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
            && reloadedGraphics.WaterSsrEnabled
            && reloadedGraphics.WaterSsrCoarseSteps == 19 && reloadedGraphics.WaterSsrRefinementSteps == 5
            && Math.Abs(reloadedGraphics.WaterSsrCameraDistance - 540f) < .001f
            && Math.Abs(reloadedGraphics.WaterSsrDepthToleranceNear - .42f) < .001f
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
            && Math.Abs(reloadedGraphics.WaterSsrRoughnessCutoff - .32f) < .001f
            && Math.Abs(reloadedFft.LargeWaveLengthMeters - 79f) < .001f
            && reloadedFft.AdaptiveUpdatesEnabled
            && Math.Abs(reloadedFft.MinimumUpdatesPerSecond - 5f) < .001f
            && Math.Abs(reloadedFft.FullRateDistanceMeters - 200f) < .001f
            && Math.Abs(reloadedFft.FullRateAreaSquareKilometers - .25f) < .001f
            && Math.Abs(reloadedFft.IdleGraceSeconds - 1f) < .001f;
        bool bodyColors = RunBodyColors(path.Replace(".tres", "_body.tres"), out string bodyDetail);
        bool ssrPresetPersistence = RunSsrPresetPersistence(path.Replace(".tres", "_ssr.tres"), out string ssrDetail);
        bool reflectionMigration = RunRetiredKeysAndMigration(path, out string reflectionDetail);
        const double circumference = 1495000000.0;
        double tile = RingWaterFftController.PeriodicLength(12, circumference);
        double phaseA = RingWaterFftController.PositiveModulo(circumference - .125, tile) / tile;
        double phaseB = RingWaterFftController.PositiveModulo(-.125, tile) / tile;
        bool periodic = double.IsFinite(tile) && Math.Abs(phaseA - phaseB) < 1e-5
            && RingWaterFftController.PositiveModulo(-circumference * 3.0 - .5, tile) >= 0;
        result["pass"] = setFalse && setFloat && setColor && setOptics && setVariation && setSsr && materialBinding && unchangedMaterialCache && newMaterialClearsDisabledProvider && setSavedLargePatch && setGeometry && deepWaterOrdering && setAdaptiveDemand && adaptiveClamps && setExpandedLimits && invalid && saved && roundTrip && setTrue && failedWriteStaysDirty && reset && periodic && ssrPresetPersistence && reflectionMigration && bodyColors;
        result["detail"] = $"ssrPresets={ssrPresetPersistence}({ssrDetail}) reflectionMigration={reflectionMigration}({reflectionDetail})false={setFalse} float={setFloat} color={setColor} optics={setOptics} variation={setVariation} ssr={setSsr} productionShaders={productionShaders} material={materialBinding} cache={unchangedMaterialCache} newMaterialClear={newMaterialClearsDisabledProvider} savedLargePatch={setSavedLargePatch} geometry={setGeometry} deepOrdering={deepWaterOrdering} adaptive={setAdaptiveDemand} adaptiveClamps={adaptiveClamps} expanded={setExpandedLimits} invalid={invalid} save={saved} roundTrip={roundTrip} true={setTrue} failedWrite={failedWriteStaysDirty} reset={reset} periodic={periodic} bodyColors={bodyColors}({bodyDetail})";
        return result;
    }
}
