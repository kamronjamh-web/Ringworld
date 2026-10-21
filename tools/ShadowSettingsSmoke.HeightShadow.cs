using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class ShadowSettingsSmoke
{
    /// <summary>S4c: terrain height shadow keys (validation, clamping, persistence, old-file load, texel/range exclusivity, effective getters).</summary>
    private static (bool Pass, string Detail) RunHeightShadowKeys(string path, SceneTree tree)
    {
        static bool Ok(string key, Variant raw, out Variant clean) => RingGraphicsQualityController.TryValidateShadowOverride(key, raw, out clean);
        bool valid = Ok("HeightShadowEnabled", true, out Variant on) && on.AsBool()
            && Ok("HeightShadowL0TexelMeters", 2, out Variant t2) && t2.AsInt32() == 2 && Ok("HeightShadowL0TexelMeters", 8.0f, out Variant t8) && t8.AsInt32() == 8
            && Ok("HeightShadowRangeMeters", 16000, out Variant range) && range.AsInt32() == 16000 && Ok("HeightShadowRangeMeters", 4000, out _)
            && Ok("HeightShadowLevels", 1, out Variant levels) && levels.AsInt32() == 1 && Ok("HeightShadowLevels", 2, out _)
            && Ok("HeightShadowSunStepDegrees", .05f, out Variant step) && Mathf.IsEqualApprox(step.AsSingle(), .05f)
            && Ok("HeightShadowSunStepDegrees", .001f, out Variant stepLow) && Mathf.IsEqualApprox(stepLow.AsSingle(), .02f)
            && Ok("HeightShadowSunStepDegrees", 5f, out Variant stepHigh) && Mathf.IsEqualApprox(stepHigh.AsSingle(), .5f)
            && Ok("HeightShadowRowsPerFrame", 100.4f, out Variant rows) && rows.AsInt32() == 100 && rows.VariantType == Variant.Type.Int
            && Ok("HeightShadowRowsPerFrame", 5, out Variant rowsLow) && rowsLow.AsInt32() == 16 && Ok("HeightShadowRowsPerFrame", 5000, out Variant rowsHigh) && rowsHigh.AsInt32() == 1024
            && Ok("HeightShadowPenumbraDegrees", 1.5f, out Variant penumbra) && Mathf.IsEqualApprox(penumbra.AsSingle(), 1.5f) && Ok("HeightShadowPenumbraDegrees", 9f, out Variant penumbraHigh) && Mathf.IsEqualApprox(penumbraHigh.AsSingle(), 2f)
            && Ok("HeightShadowCrossfadeSeconds", 0f, out Variant fade) && fade.AsSingle() == 0f && Ok("HeightShadowCrossfadeSeconds", 7f, out Variant fadeHigh) && Mathf.IsEqualApprox(fadeHigh.AsSingle(), 3f)
            && Ok("HeightShadowFillWorkers", 3, out Variant workers) && workers.AsInt32() == 3 && Ok("HeightShadowFillWorkers", 9, out Variant workersHigh) && workersHigh.AsInt32() == 4
            && Ok("HeightShadowBudgetMs", .3f, out Variant budget) && Mathf.IsEqualApprox(budget.AsSingle(), .3f) && Ok("HeightShadowBudgetMs", 0f, out Variant budgetLow) && Mathf.IsEqualApprox(budgetLow.AsSingle(), .05f)
            && Ok("HeightShadowNearMapEnabled", false, out Variant nearOff) && !nearOff.AsBool() && Ok("HeightShadowNearMapEnabled", true, out Variant nearOn) && nearOn.AsBool()
            && Ok("HeightShadowNearMapDistanceMeters", 300, out Variant near300) && near300.AsInt32() == 300 && Ok("HeightShadowNearMapDistanceMeters", 500.0f, out Variant near500) && near500.AsInt32() == 500 && Ok("HeightShadowNearMapDistanceMeters", 400, out _)
            && Ok("HeightShadowPlanetNearMapEnabled", false, out Variant planetNearOff) && !planetNearOff.AsBool() && Ok("HeightShadowPlanetNearMapEnabled", true, out Variant planetNearOn) && planetNearOn.AsBool()
            && Ok("HeightShadowPlanetNearMapDistanceMeters", 100, out Variant planet100) && planet100.AsInt32() == 100 && planet100.VariantType == Variant.Type.Int
            && Ok("HeightShadowPlanetNearMapDistanceMeters", 175.4f, out Variant planet175) && planet175.AsInt32() == 175 && Ok("HeightShadowPlanetNearMapDistanceMeters", 250, out Variant planet250) && planet250.AsInt32() == 250
            && Ok("HeightShadowPlanetNearMapDistanceMeters", 30, out Variant planetLow) && planetLow.AsInt32() == 100 && Ok("HeightShadowPlanetNearMapDistanceMeters", 6000, out Variant planetHigh) && planetHigh.AsInt32() == 250
            && Ok("HeightShadowPoiMapDistanceMeters", 20000, out Variant poi20k) && Mathf.IsEqualApprox(poi20k.AsSingle(), 20000f) && Ok("HeightShadowPoiMapDistanceMeters", 12345.5f, out Variant poiMid) && Mathf.IsEqualApprox(poiMid.AsSingle(), 12345.5f)
            && Ok("HeightShadowPoiMapDistanceMeters", 1000, out Variant poiLow) && Mathf.IsEqualApprox(poiLow.AsSingle(), 5000f) && Ok("HeightShadowPoiMapDistanceMeters", 90000, out Variant poiHigh) && Mathf.IsEqualApprox(poiHigh.AsSingle(), 30000f);
        bool rejected = !Ok("HeightShadowEnabled", 1, out _) && !Ok("HeightShadowEnabled", "true", out _) && !Ok("HeightShadowEnabled", .5f, out _)
            && !Ok("HeightShadowL0TexelMeters", 3, out _) && !Ok("HeightShadowL0TexelMeters", 16, out _) && !Ok("HeightShadowL0TexelMeters", 4.5f, out _) && !Ok("HeightShadowL0TexelMeters", float.NaN, out _) && !Ok("HeightShadowL0TexelMeters", true, out _)
            && !Ok("HeightShadowRangeMeters", 5000, out _) && !Ok("HeightShadowRangeMeters", 8000.5f, out _) && !Ok("HeightShadowRangeMeters", "8000", out _)
            && !Ok("HeightShadowLevels", 3, out _) && !Ok("HeightShadowLevels", 0, out _) && !Ok("HeightShadowLevels", 1.5f, out _)
            && !Ok("HeightShadowSunStepDegrees", float.NaN, out _) && !Ok("HeightShadowSunStepDegrees", float.PositiveInfinity, out _) && !Ok("HeightShadowSunStepDegrees", true, out _)
            && !Ok("HeightShadowRowsPerFrame", float.NaN, out _) && !Ok("HeightShadowPenumbraDegrees", "1", out _) && !Ok("HeightShadowCrossfadeSeconds", float.NaN, out _)
            && !Ok("HeightShadowFillWorkers", double.PositiveInfinity, out _) && !Ok("HeightShadowBudgetMs", float.NaN, out _) && !Ok("HeightShadowUnknown", 1, out _)
            && !Ok("HeightShadowNearMapEnabled", 1, out _) && !Ok("HeightShadowNearMapEnabled", "true", out _) && !Ok("HeightShadowNearMapDistanceMeters", 350, out _) && !Ok("HeightShadowNearMapDistanceMeters", 1000, out _)
            && !Ok("HeightShadowNearMapDistanceMeters", float.NaN, out _) && !Ok("HeightShadowNearMapDistanceMeters", "400", out _) && !Ok("HeightShadowNearMapDistanceMeters", 400.5f, out _)
            && !Ok("HeightShadowPlanetNearMapEnabled", 1, out _) && !Ok("HeightShadowPlanetNearMapEnabled", "true", out _) && !Ok("HeightShadowPlanetNearMapDistanceMeters", float.NaN, out _)
            && !Ok("HeightShadowPlanetNearMapDistanceMeters", "250", out _) && !Ok("HeightShadowPlanetNearMapDistanceMeters", float.PositiveInfinity, out _) && !Ok("HeightShadowPlanetNearMapDistanceMeters", true, out _)
            && !Ok("HeightShadowPoiMapDistanceMeters", float.NaN, out _) && !Ok("HeightShadowPoiMapDistanceMeters", float.PositiveInfinity, out _) && !Ok("HeightShadowPoiMapDistanceMeters", "20000", out _) && !Ok("HeightShadowPoiMapDistanceMeters", true, out _);

        string heightPath = path + ".s4c.tres";
        RingGraphicsQualityController NewController()
        {
            var controller = new RingGraphicsQualityController();
            var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
            controller.AddChild(sun);
            controller.SunLightPath = new NodePath("Sun");
            tree.Root.AddChild(controller);
            controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
            return controller;
        }
        RingGraphicsQualityController controller = NewController();
        var settings = new GameplayShadowDefaults(heightPath);
        settings.Initialize(controller);
        int revision0 = controller.HeightShadowRevision;
        bool defaults = controller.ResolveHeightShadowSettings() == new RingHeightShadowSettings() && !controller.EffectiveHeightShadowEnabled && controller.EffectiveHeightShadowL0TexelMeters == 4
            && controller.EffectiveHeightShadowRangeMeters == 8000 && controller.EffectiveHeightShadowLevels == 2 && controller.EffectiveHeightShadowRowsPerFrame == 64 && controller.EffectiveHeightShadowFillWorkers == 2
            && Mathf.IsEqualApprox(controller.EffectiveHeightShadowSunStepDegrees, .1f) && Mathf.IsEqualApprox(controller.EffectiveHeightShadowPenumbraDegrees, .5f) && Mathf.IsEqualApprox(controller.EffectiveHeightShadowCrossfadeSeconds, 1f)
            && Mathf.IsEqualApprox(controller.EffectiveHeightShadowBudgetMs, .15f) && controller.EffectiveHeightShadowStatus.Contains("off")
            && controller.EffectiveHeightShadowNearMapEnabled && controller.EffectiveHeightShadowNearMapDistanceMeters == 400 && !controller.NearSunMapActive
            && controller.EffectiveHeightShadowPlanetNearMapEnabled && controller.EffectiveHeightShadowPlanetNearMapDistanceMeters == 250 && !controller.NearPlanetMapActive
            && Mathf.IsEqualApprox(controller.EffectiveHeightShadowPoiMapDistanceMeters, 20000f) && !controller.PoiSunMapExtensionActive;
        bool set = settings.SetOverride(controller, "HeightShadowEnabled", true) && settings.SetOverride(controller, "HeightShadowL0TexelMeters", 2) && settings.SetOverride(controller, "HeightShadowLevels", 1)
            && settings.SetOverride(controller, "HeightShadowSunStepDegrees", .25f) && settings.SetOverride(controller, "HeightShadowRowsPerFrame", 128) && settings.SetOverride(controller, "HeightShadowPenumbraDegrees", 1f)
            && settings.SetOverride(controller, "HeightShadowCrossfadeSeconds", 2f) && settings.SetOverride(controller, "HeightShadowFillWorkers", 3) && settings.SetOverride(controller, "HeightShadowBudgetMs", .4f)
            && settings.SetOverride(controller, "HeightShadowNearMapEnabled", false) && settings.SetOverride(controller, "HeightShadowNearMapDistanceMeters", 500)
            && settings.SetOverride(controller, "HeightShadowPlanetNearMapEnabled", false) && settings.SetOverride(controller, "HeightShadowPlanetNearMapDistanceMeters", 180)
            && settings.SetOverride(controller, "HeightShadowPoiMapDistanceMeters", 12500f) && !settings.SetOverride(controller, "HeightShadowPoiMapDistanceMeters", float.NaN)
            && !settings.SetOverride(controller, "HeightShadowNearMapDistanceMeters", 450) &&!settings.SetOverride(controller, "HeightShadowL0TexelMeters", 5) && !settings.SetOverride(controller, "HeightShadowEnabled", 1);
        RingHeightShadowSettings applied = controller.ResolveHeightShadowSettings();
        bool appliedOk = set && controller.HeightShadowRevision > revision0 && applied.Enabled && applied.L0TexelMeters == 2 && applied.RangeMeters == 4000 && applied.Level1TexelMeters == 8 && applied.Levels == 1
            && Mathf.IsEqualApprox(applied.SunStepDegrees, .25f) && applied.RowsPerFrame == 128 && Mathf.IsEqualApprox(applied.PenumbraDegrees, 1f) && Mathf.IsEqualApprox(applied.CrossfadeSeconds, 2f) && applied.FillWorkers == 3
            && Mathf.IsEqualApprox(applied.BudgetMs, .4f) && !applied.NearMapEnabled && applied.NearMapDistanceMeters == 500 && !applied.PlanetNearMapEnabled && applied.PlanetNearMapDistanceMeters == 180 && !controller.EffectiveHeightShadowPlanetNearMapEnabled && controller.EffectiveHeightShadowPlanetNearMapDistanceMeters == 180 && !controller.EffectiveHeightShadowNearMapEnabled && controller.EffectiveHeightShadowNearMapDistanceMeters == 500 && Mathf.IsEqualApprox(applied.PoiMapDistanceMeters, 12500f) && Mathf.IsEqualApprox(controller.EffectiveHeightShadowPoiMapDistanceMeters, 12500f) && controller.EffectiveHeightShadowEnabled && controller.EffectiveHeightShadowStatus.Contains("starting");
        bool exclusive = settings.SetOverride(controller, "HeightShadowRangeMeters", 16000)
            && !settings.Settings.Overrides.ContainsKey("HeightShadowL0TexelMeters") && controller.EffectiveHeightShadowL0TexelMeters == 8 && controller.EffectiveHeightShadowRangeMeters == 16000
            && settings.SetOverride(controller, "HeightShadowL0TexelMeters", 4) && !settings.Settings.Overrides.ContainsKey("HeightShadowRangeMeters") && controller.EffectiveHeightShadowRangeMeters == 8000
            && controller.SetShadowTuningOverride("HeightShadowRangeMeters", 4000) && controller.EffectiveHeightShadowL0TexelMeters == 2
            && controller.SetShadowTuningOverride("HeightShadowL0TexelMeters", 8) && controller.EffectiveHeightShadowRangeMeters == 16000;
        settings.SetOverride(controller, "HeightShadowL0TexelMeters", 2);
        bool saved = settings.Save();
        RingGraphicsQualityController reloaded = NewController();
        new GameplayShadowDefaults(heightPath).Initialize(reloaded);
        bool roundTrip = saved && reloaded.ResolveHeightShadowSettings() == controller.ResolveHeightShadowSettings() && reloaded.EffectiveHeightShadowEnabled && reloaded.EffectiveHeightShadowL0TexelMeters == 2;

        // An old file (no height keys) loads with the feature off and every other override intact; invalid height values in a file are dropped.
        var oldFile = new GameplayShadowDefaults(heightPath);
        oldFile.Settings.Overrides["Split1"] = .18f;
        oldFile.Settings.Overrides["HeightShadowL0TexelMeters"] = 3;
        oldFile.Settings.Overrides["HeightShadowEnabled"] = 1;
        oldFile.Settings.Overrides["HeightShadowNearMapDistanceMeters"] = 450;
        bool oldSaved = ResourceSaver.Save(oldFile.Settings, heightPath) == Error.Ok;
        RingGraphicsQualityController old = NewController();
        var oldLoaded = new GameplayShadowDefaults(heightPath);
        oldLoaded.Initialize(old);
        bool oldFileOk = oldSaved && oldLoaded.Settings.Overrides.ContainsKey("Split1") && !oldLoaded.Settings.Overrides.ContainsKey("HeightShadowL0TexelMeters") && !oldLoaded.Settings.Overrides.ContainsKey("HeightShadowEnabled") && !oldLoaded.Settings.Overrides.ContainsKey("HeightShadowNearMapDistanceMeters")
            && old.ResolveHeightShadowSettings() == new RingHeightShadowSettings();
        settings.Reset(controller);
        bool reset = controller.ResolveHeightShadowSettings() == new RingHeightShadowSettings() && !controller.EffectiveHeightShadowEnabled;
        foreach (RingGraphicsQualityController extra in new[] { controller, reloaded, old })
        {
            tree.Root.RemoveChild(extra);
            extra.Free();
        }
        try { System.IO.File.Delete(ProjectSettings.GlobalizePath(heightPath)); } catch (System.Exception) { }
        bool pass = valid && rejected && defaults && set && appliedOk && exclusive && roundTrip && oldFileOk && reset;
        return (pass, $"heightShadow={pass}[{valid}/{rejected}/{defaults}/{set}/{appliedOk}/{exclusive}/{roundTrip}/{oldFileOk}/{reset}]");
    }
}
